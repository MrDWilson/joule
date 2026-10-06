using System.Text;
using System.Text.Json;

namespace Joule;

/// <summary>
/// How much history stays in the in-memory, JSON-persisted AppState. Everything older moves to DuckDB archive tables, so the
/// state blob that is cloned on every change and read on every poll stays a bounded size, while the paged
/// /api/investigations, /api/activities and /api/usage endpoints still return the full history.
/// </summary>
public static class RetentionPolicy
{
    /// <summary>Newest investigations always kept in AppState, whatever their age (about 600 KB of state at live sizes).</summary>
    public const int Investigations = 100;
    /// <summary>Investigations this recent are always kept: the weekly report (the previous seven local days) reads them.</summary>
    public static readonly TimeSpan InvestigationFloor = TimeSpan.FromDays(8);
    /// <summary>Proposals this recent keep their investigation in AppState (the AI explains earlier denials from it).</summary>
    public static readonly TimeSpan ProposalLink = TimeSpan.FromDays(90);
    public const int Activities = 500;
    /// <summary>Usage records this recent stay in AppState: the scheduler and daily budget read today's, the AI page reads the last month.</summary>
    public static readonly TimeSpan Usage = TimeSpan.FromDays(35);
    public const int MaxUsage = 1000;
}

public record ArchivedPage<T>(List<T> Items, string? NextCursor);

public partial class DataStore
{
    long archivedInvestigationCount;
    /// <summary>Investigations moved out of AppState into the archive table.</summary>
    public long ArchivedInvestigationCount { get { lock (gate) return archivedInvestigationCount; } }

    void InitializeStateArchive()
    {
        Execute("""
            CREATE TABLE IF NOT EXISTS archived_investigations (id VARCHAR PRIMARY KEY, started_at TIMESTAMPTZ NOT NULL, status VARCHAR, verdict VARCHAR, payload VARCHAR NOT NULL, archived_at TIMESTAMPTZ);
            CREATE TABLE IF NOT EXISTS archived_activities (occurred_at TIMESTAMPTZ NOT NULL, kind VARCHAR NOT NULL, message VARCHAR NOT NULL, PRIMARY KEY(occurred_at, kind, message));
            CREATE TABLE IF NOT EXISTS archived_usage (occurred_at TIMESTAMPTZ NOT NULL, provider VARCHAR NOT NULL, model VARCHAR NOT NULL, input_tokens BIGINT, output_tokens BIGINT, estimated_usd DOUBLE, status VARCHAR NOT NULL, PRIMARY KEY(occurred_at, provider, model, status));
            """);
        using var c = Command("SELECT count(*) FROM archived_investigations");
        archivedInvestigationCount = Convert.ToInt64(c.ExecuteScalar());
    }

    /// <summary>
    /// Moves history past the retention limits out of <paramref name="state"/> into the archive tables, in place. Inserts are
    /// idempotent, so a failed state publication after archiving only leaves duplicates that readers already de-duplicate.
    /// </summary>
    internal void ApplyRetention(AppState state, DateTimeOffset now)
    {
        var keepIds = RetainedInvestigationIds(state, now);
        var retiredInvestigations = state.Investigations.Where(i => !keepIds.Contains(i.Id)).ToList();
        var retiredActivities = state.Activities.Count > RetentionPolicy.Activities ? state.Activities.Take(state.Activities.Count - RetentionPolicy.Activities).ToList() : [];
        var usageCutoff = now - RetentionPolicy.Usage;
        var keptUsage = state.Usage.Where(u => u.At >= usageCutoff).OrderByDescending(u => u.At).Take(RetentionPolicy.MaxUsage).ToHashSet();
        var retiredUsage = state.Usage.Where(u => !keptUsage.Contains(u)).ToList();
        if (retiredInvestigations.Count == 0 && retiredActivities.Count == 0 && retiredUsage.Count == 0) return;

        // Small transactions: DuckDB keeps uncommitted rows in memory, and a first run on a large legacy state can retire
        // thousands of records at once under the 256 MB memory limit.
        void Batch<T>(IEnumerable<T> rows, Action<T> insert)
        {
            foreach (var chunk in rows.Chunk(200))
            {
                Execute("BEGIN TRANSACTION");
                try { foreach (var row in chunk) insert(row); Execute("COMMIT"); }
                catch { Execute("ROLLBACK"); throw; }
            }
        }
        Batch(retiredInvestigations, item => Execute("INSERT OR REPLACE INTO archived_investigations VALUES (?, ?, ?, ?, ?, ?)", item.Id, item.At, item.Status, item.Verdict, JsonSerializer.Serialize(item, summaryOptions), now));
        Batch(retiredActivities, a => Execute("INSERT OR IGNORE INTO archived_activities VALUES (?, ?, ?)", a.At, a.Kind, a.Message));
        Batch(retiredUsage, u => Execute("INSERT OR IGNORE INTO archived_usage VALUES (?, ?, ?, ?, ?, ?, ?)", u.At, u.Provider, u.Model, u.InputTokens, u.OutputTokens, u.EstimatedUsd, u.Status));
        using (var c = Command("SELECT count(*) FROM archived_investigations")) archivedInvestigationCount = Convert.ToInt64(c.ExecuteScalar());
        state.Investigations = state.Investigations.Where(i => keepIds.Contains(i.Id)).ToList();
        if (retiredActivities.Count > 0) state.Activities = state.Activities.Skip(retiredActivities.Count).ToList();
        if (retiredUsage.Count > 0) state.Usage = state.Usage.Where(keptUsage.Contains).ToList();
    }

    static HashSet<string> RetainedInvestigationIds(AppState state, DateTimeOffset now)
    {
        var keep = state.Investigations.OrderByDescending(i => i.At).Take(RetentionPolicy.Investigations).Select(i => i.Id).ToHashSet(StringComparer.Ordinal);
        var floor = now - RetentionPolicy.InvestigationFloor;
        var linked = state.Proposals.Where(p => p.Status == "Pending" || p.CreatedAt >= now - RetentionPolicy.ProposalLink).Select(p => p.InvestigationId).ToHashSet(StringComparer.Ordinal);
        foreach (var i in state.Investigations)
            if (i.At >= floor || i.Status == "Running" || linked.Contains(i.Id) ||
                i.NextSteps.Any(s => (s.Status ?? "open") == "open") || i.FileChanges.Any(InvestigationFileChanges.IsOpen))
                keep.Add(i.Id);
        return keep;
    }

    static DateTimeOffset ArchiveStamp(object value) => value switch
    {
        DateTimeOffset d => d.ToUniversalTime(),
        DateTime d => new DateTimeOffset(DateTime.SpecifyKind(d, DateTimeKind.Utc)),
        _ => DateTimeOffset.Parse(value.ToString()!, System.Globalization.CultureInfo.InvariantCulture)
    };

    /// <summary>An archived investigation (without tool evidence), or null.</summary>
    public Investigation? ReadArchivedInvestigation(string id)
    {
        lock (gate)
        {
            using var c = Command("SELECT payload FROM archived_investigations WHERE id=?", id);
            return c.ExecuteScalar() is string text ? JsonSerializer.Deserialize<Investigation>(text, JsonDefaults.Options) : null;
        }
    }

    /// <summary>Archived investigations strictly older than the (at, id) cursor, newest first.</summary>
    public List<Investigation> ReadArchivedInvestigations(DateTimeOffset? beforeAt, string? beforeId, int limit)
    {
        lock (gate)
        {
            using var c = beforeAt is null
                ? Command("SELECT payload FROM archived_investigations ORDER BY started_at DESC, id DESC LIMIT ?", limit)
                : Command("SELECT payload FROM archived_investigations WHERE started_at < ? OR (started_at = ? AND id < ?) ORDER BY started_at DESC, id DESC LIMIT ?", beforeAt.Value, beforeAt.Value, beforeId ?? "", limit);
            using var r = c.ExecuteReader(); var result = new List<Investigation>();
            while (r.Read()) result.Add(JsonSerializer.Deserialize<Investigation>(r.GetString(0), JsonDefaults.Options)!);
            return result;
        }
    }

    /// <summary>Archived activities in [after, before), oldest first; when <paramref name="newest"/> is set the newest <paramref name="limit"/> rows are returned (still oldest first).</summary>
    public List<Activity> ReadArchivedActivities(DateTimeOffset? after, DateTimeOffset? before, int limit, bool newest, string? kind = null)
    {
        lock (gate)
        {
            var sql = new StringBuilder("SELECT occurred_at, kind, message FROM archived_activities WHERE 1=1");
            var values = new List<object?>();
            if (after is not null) { sql.Append(" AND occurred_at > ?"); values.Add(after.Value); }
            if (before is not null) { sql.Append(" AND occurred_at < ?"); values.Add(before.Value); }
            if (kind is not null) { sql.Append(" AND kind = ?"); values.Add(kind); }
            sql.Append(newest ? " ORDER BY occurred_at DESC LIMIT ?" : " ORDER BY occurred_at ASC LIMIT ?"); values.Add(limit);
            using var c = Command(sql.ToString(), values.ToArray());
            using var r = c.ExecuteReader(); var result = new List<Activity>();
            while (r.Read()) result.Add(new(ArchiveStamp(r.GetValue(0)), r.GetString(1), r.GetString(2)));
            if (newest) result.Reverse();
            return result;
        }
    }

    public List<UsageRecord> ReadArchivedUsage(DateTimeOffset from, DateTimeOffset to)
    {
        lock (gate)
        {
            using var c = Command("SELECT occurred_at, provider, model, input_tokens, output_tokens, estimated_usd, status FROM archived_usage WHERE occurred_at >= ? AND occurred_at < ? ORDER BY occurred_at", from, to);
            using var r = c.ExecuteReader(); var result = new List<UsageRecord>();
            while (r.Read()) result.Add(new(ArchiveStamp(r.GetValue(0)), r.GetString(1), r.GetString(2), r.IsDBNull(3) ? 0 : r.GetInt64(3), r.IsDBNull(4) ? 0 : r.GetInt64(4), r.IsDBNull(5) ? null : r.GetDouble(5), r.GetString(6)));
            return result;
        }
    }
}
