namespace Joule;

/// <summary>A crucial fact every investigation should know. Kept deliberately small: one sentence, no measurements. Source is user, user-reply (accepted from a reply to a recommendation) or model.</summary>
public record MemoryFact(string Id, string Text, string Source, DateTimeOffset CreatedAt, string? InvestigationId);

public partial class DataStore
{
    public const int MemoryTextLimit = 300, MemoryFactLimit = 40;
    void InitializeMemory() => Execute("CREATE TABLE IF NOT EXISTS agent_memory (id VARCHAR PRIMARY KEY, created_at TIMESTAMPTZ, source VARCHAR, text VARCHAR NOT NULL, investigation_id VARCHAR, active BOOLEAN DEFAULT true)");

    public List<MemoryFact> ListMemory()
    {
        lock (gate)
        {
            using var c = Command("SELECT id,text,source,created_at,investigation_id FROM agent_memory WHERE active ORDER BY created_at");
            using var r = c.ExecuteReader(); var result = new List<MemoryFact>();
            while (r.Read()) result.Add(new(r.GetString(0), r.GetString(1), r.GetString(2), Stamp(r.GetValue(3)), r.IsDBNull(4) ? null : r.GetString(4)));
            return result;
        }
    }

    /// <summary>Adds a fact unless an equivalent one is already remembered; returns the stored or existing fact.</summary>
    public MemoryFact AddMemory(string text, string source, string? investigationId = null)
    {
        text = System.Text.RegularExpressions.Regex.Replace((text ?? "").Trim(), @"\s+", " ");
        if (text.Length is < 1 or > MemoryTextLimit) throw new DomainException($"A remembered fact needs 1–{MemoryTextLimit} characters.", 400);
        if (source is not ("user" or "model" or "user-reply")) throw new DomainException("Memory source must be user, user-reply or model.", 400);
        lock (gate)
        {
            var existing = ListMemory();
            if (existing.FirstOrDefault(f => string.Equals(f.Text, text, StringComparison.OrdinalIgnoreCase)) is { } same) return same;
            if (existing.Count >= MemoryFactLimit) throw new DomainException($"Shared memory holds at most {MemoryFactLimit} facts. Remove one before adding another.", 400);
            var fact = new MemoryFact(Guid.NewGuid().ToString("N"), text, source, DateTimeOffset.UtcNow, investigationId);
            Execute("INSERT INTO agent_memory (id,created_at,source,text,investigation_id,active) VALUES (?,?,?,?,?,true)", fact.Id, fact.CreatedAt, fact.Source, fact.Text, fact.InvestigationId);
            return fact;
        }
    }

    public bool DeleteMemory(string id)
    {
        lock (gate)
        {
            using var c = Command("UPDATE agent_memory SET active=false WHERE id=? AND active", id);
            return c.ExecuteNonQuery() > 0;
        }
    }
}
