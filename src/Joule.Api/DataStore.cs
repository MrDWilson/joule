using DuckDB.NET.Data;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Joule;

public partial class DataStore : IDisposable
{
    readonly DuckDBConnection db;
    readonly object gate = new();
    public string DirectoryPath { get; }
    /// <summary>The store's "now" (retention, freshness, completed intervals). The app uses the system clock; tests pin it.</summary>
    public TimeProvider Clock { get; }
    /// <summary>The database file: joule.duckdb, or the pre-rename predbat.duckdb when it couldn't be renamed (see <see cref="DataFiles"/>).</summary>
    public string DatabasePath { get; }
    public DataStore(string directory, TimeProvider? clock = null, ILogger? logger = null)
    {
        Clock = clock ?? TimeProvider.System;
        Logger = logger;
        DirectoryPath = Path.GetFullPath(directory);
        Directory.CreateDirectory(DirectoryPath);
        var choice = DataFiles.ChooseDatabase(DirectoryPath);
        DatabasePath = choice.Path;
        if (choice.Note is { } note)
        {
            if (choice.Warning) Logger?.LogWarning("{Note}", note); else Logger?.LogInformation("{Note}", note);
        }
        db = new DuckDBConnection($"Data Source={DatabasePath}"); db.Open();
        Execute("SET enable_external_access=false; SET memory_limit='256MB'; SET threads=2");
        Execute("""
            CREATE TABLE IF NOT EXISTS application_state (id INTEGER PRIMARY KEY, payload VARCHAR NOT NULL);
            CREATE TABLE IF NOT EXISTS revisions (id INTEGER PRIMARY KEY, recorded_at TIMESTAMPTZ, source VARCHAR, payload VARCHAR NOT NULL);
            CREATE TABLE IF NOT EXISTS plans (id VARCHAR PRIMARY KEY, recorded_at TIMESTAMPTZ, source VARCHAR, payload VARCHAR NOT NULL);
            CREATE TABLE IF NOT EXISTS plan_slots (snapshot_id VARCHAR, captured_at TIMESTAMPTZ, time TIMESTAMPTZ, load_forecast DOUBLE, load_actual DOUBLE, pv_forecast DOUBLE, pv_actual DOUBLE, soc_forecast DOUBLE, soc_actual DOUBLE, import_rate DOUBLE, export_rate DOUBLE, action VARCHAR, cost DOUBLE);
            CREATE TABLE IF NOT EXISTS observations (recorded_at TIMESTAMPTZ, entity_id VARCHAR, value VARCHAR, unit VARCHAR);
            CREATE TABLE IF NOT EXISTS actual_energy (recorded_at TIMESTAMPTZ, time TIMESTAMPTZ, load_actual DOUBLE);
            CREATE TABLE IF NOT EXISTS predbat_actual_intervals (start_time TIMESTAMPTZ, end_time TIMESTAMPTZ, recorded_at TIMESTAMPTZ, load_actual DOUBLE, entity_id VARCHAR, source VARCHAR, PRIMARY KEY(start_time,end_time));
            CREATE TABLE IF NOT EXISTS source_snapshots (id VARCHAR PRIMARY KEY, recorded_at TIMESTAMPTZ, state_json VARCHAR, plan_json VARCHAR);
            CREATE TABLE IF NOT EXISTS write_journal (id VARCHAR PRIMARY KEY, recorded_at TIMESTAMPTZ, status VARCHAR, payload VARCHAR);
            ALTER TABLE plan_slots ADD COLUMN IF NOT EXISTS duration_minutes INTEGER DEFAULT 30;
            ALTER TABLE plans ADD COLUMN IF NOT EXISTS collected_at TIMESTAMPTZ;
            UPDATE plans SET collected_at=(SELECT min(recorded_at) FROM source_snapshots WHERE source_snapshots.id=plans.id) WHERE collected_at IS NULL AND source<>'Demo';
            UPDATE plans SET collected_at=recorded_at WHERE collected_at IS NULL AND source='Demo';
            CREATE TABLE IF NOT EXISTS store_migrations(name VARCHAR PRIMARY KEY, applied_at TIMESTAMPTZ);
            """);
        // One-time: slots captured before captured_at existed take their plan's receipt time. This used to run on every start.
        Migrate("plan-slots-captured-at-v1", () => Execute("UPDATE plan_slots SET captured_at=(SELECT collected_at FROM plans WHERE plans.id=plan_slots.snapshot_id)"));
        InitializePlanDetails();
        InitializeSnapshotStorage();
        InitializeTelemetry();
        InitializeInvestigationEvidence();
        InitializeMemory();
    }
    DuckDBCommand Command(string sql, params object?[] values)
    {
        var cmd = db.CreateCommand(); cmd.CommandText = sql;
        foreach (var v in values) cmd.Parameters.Add(new DuckDBParameter { Value = v ?? DBNull.Value });
        return cmd;
    }
    void Execute(string sql, params object?[] values) { using var cmd = Command(sql, values); cmd.ExecuteNonQuery(); }
    /// <summary>Runs a schema/data migration exactly once, recorded in store_migrations, inside one transaction.</summary>
    void Migrate(string name, Action apply)
    {
        using (var c = Command("SELECT count(*) FROM store_migrations WHERE name=?", name)) if (Convert.ToInt64(c.ExecuteScalar()) > 0) return;
        Execute("BEGIN TRANSACTION");
        try { apply(); Execute("INSERT INTO store_migrations VALUES (?,?)", name, Clock.GetUtcNow()); Execute("COMMIT"); }
        catch { Execute("ROLLBACK"); throw; }
    }
    internal bool MigrationApplied(string name)
    {
        lock (gate) { using var c = Command("SELECT count(*) FROM store_migrations WHERE name=?", name); return Convert.ToInt64(c.ExecuteScalar()) > 0; }
    }
    public AppState? Load(bool includeEvidence = true)
    {
        lock (gate)
        {
            AppState? state;
            using (var cmd = Command("SELECT payload FROM application_state WHERE id=1")) state = cmd.ExecuteScalar() is string text ? JsonSerializer.Deserialize<AppState>(text, JsonDefaults.Options) : null;
            if (state != null && includeEvidence) HydrateInvestigationEvidence(state);
            // Follow-ups recorded before they had identities get stable ones derived from their position.
            if (state != null) foreach (var investigation in state.Investigations) for (var index = 0; index < investigation.NextSteps.Count; index++)
                if (string.IsNullOrEmpty(investigation.NextSteps[index].Id)) investigation.NextSteps[index].Id = $"{investigation.Id}-{index}";
            return state;
        }
    }
    public virtual void Save(AppState state)
    {
        lock (gate)
        {
            ArchiveInvestigationEvidence(state);
            Execute("BEGIN TRANSACTION");
            try
            {
                Execute("INSERT OR REPLACE INTO application_state VALUES (1, ?)", JsonSerializer.Serialize(state, summaryOptions));
                using var maxRevision = Command("SELECT coalesce(max(id), 0) FROM revisions");
                var storedRevision = Convert.ToInt32(maxRevision.ExecuteScalar());
                foreach (var r in state.Revisions.Where(r => r.Id > storedRevision)) Execute("INSERT OR IGNORE INTO revisions VALUES (?, ?, ?, ?)", r.Id, r.At, r.Source, JsonSerializer.Serialize(r, JsonDefaults.Options));
                Execute("COMMIT");
            }
            catch { Execute("ROLLBACK"); throw; }
        }
    }
    public void SavePlan(PlanSnapshot p, string? rawState = null, string? rawPlan = null)
    {
        lock (gate)
        {
            using (var check = Command("SELECT count(*) FROM plans WHERE id=? OR (recorded_at=? AND source=?)", p.Id, p.At, p.Source))
                if (Convert.ToInt64(check.ExecuteScalar()) > 0)
                {
                    // Predbat replans every 10 minutes and is polled every 5: an unchanged plan adds only changed observations,
                    // not another full source snapshot of the same plan.
                    if (rawState != null) { Execute("BEGIN TRANSACTION"); try { SaveObservations(Clock.GetUtcNow(), rawState); Execute("COMMIT"); } catch { Execute("ROLLBACK"); throw; } }
                    return;
                }
            Execute("BEGIN TRANSACTION");
            try
            {
                p.CollectedAt ??= rawState is not null ? Clock.GetUtcNow() : p.Source == "Demo" ? p.At : null;
                p = NormalisePlan(p);
                Execute("INSERT INTO plans (id,recorded_at,source,payload,collected_at,detail_version) VALUES (?, ?, ?, ?, ?, ?)", p.Id, p.At, p.Source, JsonSerializer.Serialize(p, PlanPayloadOptions), p.CollectedAt, rawPlan is not null || p.Source == "Demo" ? PlanDetailVersion : 0);
                InsertPlanSlots(p);
                if (rawState != null) SaveSourceInternal(p.Id, rawState, rawPlan ?? "{}");
                Execute("COMMIT");
            }
            catch { Execute("ROLLBACK"); throw; }
        }
    }
    public void SaveSource(string rawState, string rawPlan)
    {
        lock (gate) { Execute("BEGIN TRANSACTION"); try { SaveSourceInternal(Guid.NewGuid().ToString("N"), rawState, rawPlan); Execute("COMMIT"); } catch { Execute("ROLLBACK"); throw; } }
    }
    /// <summary>Test and benchmark hook: stores a source snapshot as if received at <paramref name="at"/>.</summary>
    internal void SaveSourceAt(DateTimeOffset at, string rawState, string rawPlan, string? id = null)
    {
        lock (gate) { Execute("BEGIN TRANSACTION"); try { SaveSourceInternal(id ?? Guid.NewGuid().ToString("N"), rawState, rawPlan, at); Execute("COMMIT"); } catch { Execute("ROLLBACK"); throw; } }
    }
    void SaveSourceInternal(string id, string rawState, string rawPlan, DateTimeOffset? receivedAt = null)
    {
        var at = receivedAt ?? Clock.GetUtcNow();
        SaveSnapshot(id, at, rawState, rawPlan);
        // Native curves remain source diagnostics: load_energy_actual can contain
        // forecasts and adjusted load. Existing derived rows remain stored, but
        // this series cannot establish new authoritative meter actuals.
        SaveObservations(at, rawState);
    }
    public PlanSnapshot? GetPlan(string? id = null)
    {
        lock (gate)
        {
            using var cmd = id == null ? Command("SELECT payload,collected_at FROM plans ORDER BY recorded_at DESC LIMIT 1") : Command("SELECT payload,collected_at FROM plans WHERE id=?", id);
            using var reader = cmd.ExecuteReader();
            if (!reader.Read()) return null;
            var plan = JsonSerializer.Deserialize<PlanSnapshot>(reader.GetString(0), JsonDefaults.Options)!;
            plan.CollectedAt = reader.IsDBNull(1) ? null : Stamp(reader.GetValue(1));
            reader.Close();
            return EnrichPlan(NormalisePlan(plan));
        }
    }
    public object ListPlans()
    {
        lock (gate)
        {
            using var cmd = Command("SELECT id,recorded_at,source FROM plans ORDER BY recorded_at DESC LIMIT 100"); using var r = cmd.ExecuteReader(); var items = new List<object>();
            while (r.Read()) items.Add(new { Id = r.GetString(0), At = Stamp(r.GetValue(1)), Source = r.GetString(2) });
            return items;
        }
    }
    public virtual string BeginWrite(List<Change> changes)
    {
        lock (gate) { var id = Guid.NewGuid().ToString("N"); Execute("INSERT INTO write_journal VALUES (?, ?, 'pending', ?)", id, Clock.GetUtcNow(), JsonSerializer.Serialize(changes, JsonDefaults.Options)); return id; }
    }
    public virtual void EndWrite(string id, string status) { lock (gate) Execute("UPDATE write_journal SET status=? WHERE id=?", status, id); }
    public bool HasUncertainWrite()
    {
        lock (gate) { using var c = Command("SELECT count(*) FROM write_journal WHERE status IN ('pending','uncertain')"); return Convert.ToInt64(c.ExecuteScalar()) > 0; }
    }
    public virtual void ReconcileWrites() { lock (gate) Execute("UPDATE write_journal SET status='reconciled' WHERE status IN ('pending','uncertain')"); }
    public List<Dictionary<string, object?>> Query(string sql, CancellationToken cancellationToken = default, TimeSpan? timeout = null)
    {
        // A single SELECT is nested as an expression: no DDL/DML, pragmas, COPY or multiple statements.
        // External file/network/extension access is independently disabled on this DuckDB connection.
        if (string.IsNullOrWhiteSpace(sql) || sql.Length > 4000 || !Regex.IsMatch(sql, @"^\s*(SELECT|WITH)\b", RegexOptions.IgnoreCase) || sql.Contains(';') || sql.Contains("--") || sql.Contains("/*"))
            throw new DomainException("Evidence queries must be a single SELECT or WITH statement.", 400);
        // DuckDB also accepts single-quoted relation names after FROM/JOIN. Keep
        // private-name and external-function checks on the original SQL; only the
        // LOAD keyword check distinguishes quoted metric values from SQL tokens.
        var tokens = EvidenceSqlTokens(sql);
        if (Regex.IsMatch(sql, @"\b(application_state|investigation_evidence|archived_\w+|write_journal|source_snapshots|read_\w+|query|query_table|glob|http\w*|sqlite\w*|postgres\w*|attach|install|pragma|call|copy)\b", RegexOptions.IgnoreCase) || Regex.IsMatch(tokens, @"\bload\b", RegexOptions.IgnoreCase))
            throw new DomainException("Query only plan_slots, actual_energy, predbat_actual_intervals, observations, plans, revisions, telemetry_samples or telemetry_intervals. External access is unavailable.", 400);
        lock (gate)
        {
            using var cmd = Command($"SELECT * FROM ({sql}) AS evidence LIMIT 200");
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(timeout ?? TimeSpan.FromSeconds(3));
            using var interrupt = deadline.Token.Register(cmd.Cancel);
            deadline.Token.ThrowIfCancellationRequested();
            using var r = cmd.ExecuteReader(); var rows = new List<Dictionary<string, object?>>();
            while (r.Read())
            {
                var row = new Dictionary<string, object?>();
                for (var i = 0; i < r.FieldCount; i++) { var v = r.GetValue(i); row[r.GetName(i)] = v is DBNull ? null : v is string text && text.Length > 4000 ? text[..4000] : v; }
                rows.Add(row);
            }
            return rows;
        }
    }
    static string EvidenceSqlTokens(string sql)
    {
        // Token view for the LOAD keyword only: mask standard single-quoted text,
        // preserve quoted identifiers, and reject ambiguous escape syntaxes.
        // This view cannot authorize relations; DuckDB permits quoted relation
        // strings, so private/external names are checked against raw SQL above.
        const string invalid = "Use standard single-quoted SQL values with doubled apostrophes. Escape-prefixed, backslash and dollar-quoted strings are unsupported; all quotes must be closed.";
        if (sql.Contains('\\')) throw new DomainException(invalid, 400);
        var tokens = sql.ToCharArray();
        for (var i = 0; i < sql.Length;)
        {
            if (sql[i] == '"')
            {
                i++; var closed = false;
                while (i < sql.Length)
                {
                    if (sql[i++] != '"') continue;
                    if (i < sql.Length && sql[i] == '"') { i++; continue; }
                    closed = true; break;
                }
                if (!closed) throw new DomainException(invalid, 400);
                continue;
            }
            if (sql[i] == '$') throw new DomainException(invalid, 400);
            if (sql[i] != '\'') { i++; continue; }
            if (i > 0 && (char.IsLetterOrDigit(sql[i - 1]) || sql[i - 1] is '_' or '$' or '&')) throw new DomainException(invalid, 400);
            tokens[i++] = ' '; var ended = false;
            while (i < sql.Length)
            {
                var current = sql[i]; tokens[i++] = ' ';
                if (current != '\'') continue;
                if (i < sql.Length && sql[i] == '\'') { tokens[i++] = ' '; continue; }
                ended = true; break;
            }
            if (!ended) throw new DomainException(invalid, 400);
        }
        return new string(tokens);
    }
    public void Dispose() => db.Dispose();
}
