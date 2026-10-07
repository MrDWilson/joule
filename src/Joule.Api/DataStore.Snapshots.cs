using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Joule;

/// <summary>What a snapshot retention pass found and (only in enforce mode) did.</summary>
public sealed record SnapshotRetentionResult(DateTimeOffset At, string Mode, bool Ran, int Candidates, long CandidateBytes, int Deleted, int Uncompacted, long UncompactedBytes, int Compacted, int Protected, string Note);

/// <summary>Size and retention status of the local database.</summary>
public sealed record StorageReport(long DbSizeBytes, long WalSizeBytes, long Snapshots, long SnapshotBytes, long DistinctPlanSnapshots, long Observations, long Plans, long PlanSlots,
    bool PlanDetailBackfillComplete, string RetentionMode, int RetentionKeepDays, SnapshotRetentionResult? LastRetention);

/// <summary>
/// Source snapshots: compaction, consecutive-duplicate skipping, changed-only observations and a guarded retention pass.
/// Retention order is deliberate: plan details are backfilled first; pruning never runs before that completes, never touches
/// the snapshot that belongs to a stored plan, never touches snapshots cited by investigation evidence or inside an open
/// experiment's comparison window, and defaults to a dry run that only reports what it would do.
/// </summary>
public partial class DataStore
{
    public const string RetentionOff = "off", RetentionDryRun = "dry-run", RetentionEnforce = "enforce";
    /// <summary>Entities whose attributes are large display blobs (Predbat's HTML plan and yesterday/savings tables).</summary>
    static readonly Regex HeavyEntity = new(@"^predbat\.(?:plan_html|.*_yesterday(?:_.*)?|savings_.*)$", RegexOptions.Compiled);
    static readonly HashSet<string> KeptAttributes = ["friendly_name", "unit_of_measurement", "device_class", "state_class", "icon", "last_reset"];
    static readonly Regex PresentationField = new(@"(?:_color|^state_html|^rowspan_\w+|^skip_\w+_cell)$", RegexOptions.Compiled);
    readonly Dictionary<string, (string Value, string Unit, DateTimeOffset At)> lastObservation = new(StringComparer.Ordinal);
    bool observationCacheLoaded;
    /// <summary>Observations are written when an entity's value or unit changes, and at least this often otherwise.</summary>
    public static readonly TimeSpan ObservationHeartbeat = TimeSpan.FromHours(1);
    public SnapshotRetentionResult? LastRetention { get; private set; }
    public ILogger? Logger { get; set; }

    void InitializeSnapshotStorage()
    {
        Execute("""
            ALTER TABLE source_snapshots ADD COLUMN IF NOT EXISTS state_sha256 VARCHAR;
            ALTER TABLE source_snapshots ADD COLUMN IF NOT EXISTS plan_sha256 VARCHAR;
            ALTER TABLE source_snapshots ADD COLUMN IF NOT EXISTS compacted BOOLEAN DEFAULT false;
            ALTER TABLE source_snapshots ADD COLUMN IF NOT EXISTS state_gz BLOB;
            ALTER TABLE source_snapshots ADD COLUMN IF NOT EXISTS plan_gz BLOB;
            """);
    }

    static string Sha256(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    /// <summary>Drops large display-only attributes (HTML plan, yesterday and savings tables) from a raw Predbat state.</summary>
    internal static string CompactState(string rawState)
    {
        try
        {
            if (JsonNode.Parse(rawState) is not JsonObject state) return rawState;
            foreach (var (id, node) in state.ToList())
            {
                if (!HeavyEntity.IsMatch(id) || node is not JsonObject entity || entity["attributes"] is not JsonObject attributes) continue;
                var kept = new JsonObject();
                foreach (var (name, value) in attributes) if (KeptAttributes.Contains(name)) kept[name] = value?.DeepClone();
                if (attributes.Count != kept.Count) { kept["joule_compacted"] = true; entity["attributes"] = kept; }
            }
            return state.ToJsonString();
        }
        catch (JsonException) { return rawState; }
    }

    /// <summary>Drops presentation-only columns (colours, HTML duplicates, table spans) from Predbat plan rows.</summary>
    internal static string CompactPlan(string rawPlan)
    {
        try
        {
            if (JsonNode.Parse(rawPlan) is not JsonObject data) return rawPlan;
            foreach (var block in new[] { "plan", "yesterday", "baseline" })
                if (data[block] is JsonObject plan && plan["rows"] is JsonArray rows)
                    foreach (var row in rows.OfType<JsonObject>())
                        foreach (var name in row.Select(x => x.Key).Where(x => PresentationField.IsMatch(x)).ToList()) row.Remove(name);
            return data.ToJsonString();
        }
        catch (JsonException) { return rawPlan; }
    }

    /// <summary>Stores a compacted snapshot, skipping it when it is identical to the latest one unless it belongs to a plan.</summary>
    void SaveSnapshot(string id, DateTimeOffset at, string rawState, string rawPlan, bool required = false)
    {
        var state = CompactState(rawState); var plan = CompactPlan(rawPlan);
        var stateHash = Sha256(state); var planHash = Sha256(plan);
        var belongsToPlan = required;
        if (!belongsToPlan) using (var c = Command("SELECT count(*) FROM plans WHERE id=?", id)) belongsToPlan = Convert.ToInt64(c.ExecuteScalar()) > 0;
        if (!belongsToPlan)
        {
            using var latest = Command("SELECT state_sha256, plan_sha256 FROM source_snapshots ORDER BY recorded_at DESC LIMIT 1");
            using var r = latest.ExecuteReader();
            if (r.Read() && !r.IsDBNull(0) && !r.IsDBNull(1) && r.GetString(0) == stateHash && r.GetString(1) == planHash) return;
        }
        var (stateText, stateGz) = Pack(state); var (planText, planGz) = Pack(plan);
        Execute("INSERT OR IGNORE INTO source_snapshots (id, recorded_at, state_json, plan_json, state_sha256, plan_sha256, compacted, state_gz, plan_gz) VALUES (?, ?, ?, ?, ?, ?, true, ?, ?)", id, at, stateText, planText, stateHash, planHash, stateGz, planGz);
    }

    /// <summary>Large JSON is stored gzip-compressed: DuckDB keeps big strings uncompressed in their own blocks, which made each
    /// 200 KB plan cost nearly twice its size on disk. Small payloads stay readable text.</summary>
    internal const int CompressAbove = 16 * 1024;
    const string StoredBytes = "(strlen(coalesce(state_json,''))+strlen(coalesce(plan_json,''))+coalesce(octet_length(state_gz),0)+coalesce(octet_length(plan_gz),0))";
    static (string? Text, byte[]? Gzip) Pack(string json)
    {
        if (json.Length <= CompressAbove) return (json, null);
        using var buffer = new MemoryStream();
        using (var gzip = new GZipStream(buffer, CompressionLevel.Optimal, true)) gzip.Write(Encoding.UTF8.GetBytes(json));
        return (null, buffer.ToArray());
    }
    /// <summary>Reads a snapshot JSON column pair: the text column when present (also after a manual edit), else the gzip column.</summary>
    internal static string SnapshotText(object? text, object? gzip)
    {
        if (text is string value) return value;
        byte[]? bytes = gzip switch { byte[] b => b, Stream s => ReadAll(s), _ => null };
        if (bytes is null) return "{}";
        using var input = new GZipStream(new MemoryStream(bytes), CompressionMode.Decompress);
        using var reader = new StreamReader(input, Encoding.UTF8);
        return reader.ReadToEnd();
        static byte[] ReadAll(Stream stream) { using var copy = new MemoryStream(); stream.CopyTo(copy); return copy.ToArray(); }
    }
    internal static string SnapshotText(System.Data.Common.DbDataReader reader, int text, int gzip) => SnapshotText(reader.IsDBNull(text) ? null : reader.GetValue(text), reader.IsDBNull(gzip) ? null : reader.GetValue(gzip));

    /// <summary>Writes one observation per Predbat entity whose value or unit changed (or hasn't been written for an hour).</summary>
    void SaveObservations(DateTimeOffset at, string rawState)
    {
        using var json = JsonDocument.Parse(rawState);
        if (json.RootElement.ValueKind != JsonValueKind.Object) return;
        if (!observationCacheLoaded)
        {
            using var c = Command("SELECT entity_id, arg_max(value, recorded_at), arg_max(unit, recorded_at), max(recorded_at) FROM observations WHERE recorded_at >= ? GROUP BY entity_id", at - ObservationHeartbeat);
            using var r = c.ExecuteReader();
            while (r.Read()) lastObservation[r.GetString(0)] = (r.IsDBNull(1) ? "" : r.GetString(1), r.IsDBNull(2) ? "" : r.GetString(2), Stamp(r.GetValue(3)));
            observationCacheLoaded = true;
        }
        foreach (var item in json.RootElement.EnumerateObject())
        {
            if (item.Value.ValueKind != JsonValueKind.Object || !item.Value.TryGetProperty("state", out var value)) continue;
            var unit = item.Value.TryGetProperty("attributes", out var attrs) && attrs.ValueKind == JsonValueKind.Object && attrs.TryGetProperty("unit_of_measurement", out var u) ? u.ToString() : "";
            var text = value.ToString();
            if (lastObservation.TryGetValue(item.Name, out var last) && last.Value == text && last.Unit == unit && at - last.At < ObservationHeartbeat && at >= last.At) continue;
            Execute("INSERT INTO observations VALUES (?, ?, ?, ?)", at, item.Name, text, unit);
            lastObservation[item.Name] = (text, unit, at);
        }
    }

    /// <summary>Snapshot IDs cited anywhere in retained investigation evidence (32-hex identifiers).</summary>
    HashSet<string> SnapshotIdsCitedByInvestigations()
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        using var c = Command("SELECT payload FROM investigation_evidence");
        using var r = c.ExecuteReader();
        while (r.Read()) foreach (Match m in Regex.Matches(r.GetString(0), "(?<![0-9a-f])[0-9a-f]{32}(?![0-9a-f])")) ids.Add(m.Value);
        return ids;
    }

    /// <summary>
    /// One retention pass. Raw snapshots older than <paramref name="keepDays"/> are removed unless they belong to a stored plan,
    /// are cited by investigation evidence, or fall at or after <paramref name="protectFrom"/> (the earliest baseline window of an
    /// open experiment). Kept older snapshots are compacted (display blobs stripped). In dry-run mode nothing is changed: the
    /// result and the log say what would happen. Nothing runs until the plan-detail backfill is complete.
    /// </summary>
    public SnapshotRetentionResult RunSnapshotRetention(string mode, DateTimeOffset now, int keepDays = 14, DateTimeOffset? protectFrom = null, IEnumerable<string>? protectedIds = null)
    {
        mode = mode is RetentionOff or RetentionEnforce ? mode : RetentionDryRun;
        keepDays = Math.Clamp(keepDays, 1, 3650);
        if (mode == RetentionOff) return LastRetention = new(now, mode, false, 0, 0, 0, 0, 0, 0, 0, "Snapshot retention is off.");
        if (!PlanDetailBackfillComplete()) return LastRetention = Log(new(now, mode, false, 0, 0, 0, 0, 0, 0, 0, "Waiting for the plan detail backfill to finish before any snapshot is pruned."));
        lock (gate)
        {
            var cutoff = now.AddDays(-keepDays);
            var cited = SnapshotIdsCitedByInvestigations();
            if (protectedIds is not null) cited.UnionWith(protectedIds);
            var candidates = new List<(string Id, long Bytes)>(); var protectedCount = 0;
            using (var c = Command($"SELECT s.id, s.recorded_at, {StoredBytes} FROM source_snapshots s WHERE s.recorded_at < ? AND NOT EXISTS (SELECT 1 FROM plans p WHERE p.id=s.id)", cutoff))
            using (var r = c.ExecuteReader())
                while (r.Read())
                {
                    var id = r.GetString(0); var at = Stamp(r.GetValue(1));
                    if (cited.Contains(id) || protectFrom is { } from && at >= from) { protectedCount++; continue; }
                    candidates.Add((id, Convert.ToInt64(r.GetValue(2))));
                }
            var uncompacted = new List<(string Id, long Bytes)>(); var removing = candidates.Select(x => x.Id).ToHashSet(StringComparer.Ordinal);
            using (var c = Command($"SELECT id, {StoredBytes} FROM source_snapshots WHERE recorded_at < ? AND coalesce(compacted,false)=false", now.AddDays(-1)))
            using (var r = c.ExecuteReader()) while (r.Read()) if (!removing.Contains(r.GetString(0))) uncompacted.Add((r.GetString(0), Convert.ToInt64(r.GetValue(1))));
            int deleted = 0, compacted = 0;
            if (mode == RetentionEnforce)
            {
                Execute("BEGIN TRANSACTION");
                try
                {
                    foreach (var chunk in candidates.Chunk(200))
                    {
                        Execute($"DELETE FROM source_snapshots WHERE id IN ({string.Join(",", chunk.Select(_ => "?"))})", chunk.Select(x => (object?)x.Id).ToArray());
                        deleted += chunk.Length;
                    }
                    foreach (var (id, _) in uncompacted)
                    {
                        string state, plan;
                        using (var c = Command("SELECT state_json, state_gz, plan_json, plan_gz FROM source_snapshots WHERE id=?", id))
                        using (var r = c.ExecuteReader()) { if (!r.Read()) continue; state = SnapshotText(r, 0, 1); plan = SnapshotText(r, 2, 3); }
                        var compactState = CompactState(state); var compactPlan = CompactPlan(plan);
                        var (stateText, stateGz) = Pack(compactState); var (planText, planGz) = Pack(compactPlan);
                        Execute("UPDATE source_snapshots SET state_json=?, state_gz=?, plan_json=?, plan_gz=?, state_sha256=?, plan_sha256=?, compacted=true WHERE id=?", stateText, stateGz, planText, planGz, Sha256(compactState), Sha256(compactPlan), id);
                        compacted++;
                    }
                    Execute("COMMIT");
                }
                catch { Execute("ROLLBACK"); throw; }
                Execute("CHECKPOINT");
            }
            var note = mode == RetentionEnforce
                ? $"Removed {deleted} raw snapshots older than {keepDays} days and compacted {compacted}; kept every plan's own snapshot and {protectedCount} cited or in an open experiment's window."
                : $"Dry run: would remove {candidates.Count} raw snapshots older than {keepDays} days ({candidates.Sum(x => x.Bytes) / 1048576.0:0.0} MB) and compact {uncompacted.Count} ({uncompacted.Sum(x => x.Bytes) / 1048576.0:0.0} MB before compaction); {protectedCount} protected. Set Storage__SnapshotRetention=enforce to apply.";
            return LastRetention = Log(new(now, mode, true, candidates.Count, candidates.Sum(x => x.Bytes), deleted, uncompacted.Count, uncompacted.Sum(x => x.Bytes), compacted, protectedCount, note));
        }
    }
    SnapshotRetentionResult Log(SnapshotRetentionResult result) { Logger?.LogInformation("Snapshot retention ({Mode}): {Note}", result.Mode, result.Note); return result; }

    public void Checkpoint() { lock (gate) Execute("CHECKPOINT"); }

    public StorageReport ReadStorageReport(string retentionMode, int keepDays)
    {
        long Size(string path) { try { return File.Exists(path) ? new FileInfo(path).Length : 0; } catch (IOException) { return 0; } }
        var file = DatabasePath;
        lock (gate)
        {
            long Scalar(string sql) { using var c = Command(sql); return Convert.ToInt64(c.ExecuteScalar() is { } v and not DBNull ? v : 0L); }
            return new(Size(file), Size(file + ".wal"), Scalar("SELECT count(*) FROM source_snapshots"), Scalar($"SELECT CAST(coalesce(sum({StoredBytes}),0) AS BIGINT) FROM source_snapshots"),
                Scalar("SELECT count(*) FROM source_snapshots s WHERE EXISTS (SELECT 1 FROM plans p WHERE p.id=s.id)"), Scalar("SELECT count(*) FROM observations"), Scalar("SELECT count(*) FROM plans"), Scalar("SELECT count(*) FROM plan_slots"),
                PlanDetailBackfillCompleteUnlocked(), retentionMode, keepDays, LastRetention);
        }
    }
    bool PlanDetailBackfillCompleteUnlocked()
    {
        using var c = Command("SELECT count(*) FROM plans p JOIN source_snapshots s ON s.id=p.id WHERE coalesce(p.detail_version,0)<? AND p.source<>'Demo'", PlanDetailVersion);
        return Convert.ToInt64(c.ExecuteScalar()) == 0;
    }
}
