using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;

namespace Joule;

public partial class DataStore
{
    readonly HashSet<string> archivedInvestigations = [];
    /// <summary>Evidence counts last archived for checks that can still grow (running, failed or interrupted, which can resume).</summary>
    readonly Dictionary<string, int> archivedCounts = new(StringComparer.Ordinal);

    /// <summary>One turn of a check's conversation: the model's action (bounded), the evidence it produced and any server note.</summary>
    public sealed record TranscriptTurn(string Reply, string? ToolId, string? Note);

    void EnsureTranscriptTable() => Execute("CREATE TABLE IF NOT EXISTS investigation_transcripts (investigation_id VARCHAR PRIMARY KEY, payload VARCHAR NOT NULL)");
    /// <summary>Saves a check's conversation so a failed or interrupted check can resume where it stopped instead of paying for it again.</summary>
    public void SaveInvestigationTranscript(string id, IReadOnlyList<TranscriptTurn> turns)
    {
        lock (gate) { EnsureTranscriptTable(); Execute("INSERT OR REPLACE INTO investigation_transcripts VALUES (?, ?)", id, JsonSerializer.Serialize(turns, JsonDefaults.Options)); }
    }
    public List<TranscriptTurn> ReadInvestigationTranscript(string id)
    {
        lock (gate)
        {
            EnsureTranscriptTable();
            using var cmd = Command("SELECT payload FROM investigation_transcripts WHERE investigation_id=?", id);
            return cmd.ExecuteScalar() is string text ? JsonSerializer.Deserialize<List<TranscriptTurn>>(text, JsonDefaults.Options) ?? [] : [];
        }
    }

    /// <summary>Predbat's own expected cost (GBP) for each half-hour in [from,to), from the latest plan captured before the half-hour began.</summary>
    public Dictionary<DateTimeOffset, double> ReadFrozenPlanCosts(DateTimeOffset from, DateTimeOffset to)
    {
        var result = new Dictionary<DateTimeOffset, double>();
        lock (gate)
        {
            using var c = Command("SELECT time,cost FROM (SELECT time,cost,row_number() OVER(PARTITION BY time ORDER BY captured_at DESC,snapshot_id DESC) rn FROM plan_slots WHERE duration_minutes=30 AND captured_at<=time AND time>=? AND time<?) WHERE rn=1", from.ToUniversalTime(), to.ToUniversalTime());
            using var r = c.ExecuteReader();
            while (r.Read()) if (!r.IsDBNull(1)) result[Stamp(r.GetValue(0))] = Convert.ToDouble(r.GetValue(1), System.Globalization.CultureInfo.InvariantCulture);
        }
        return result;
    }
    static readonly JsonSerializerOptions summaryOptions = CreateSummaryOptions();
    static JsonSerializerOptions CreateSummaryOptions()
    {
        var resolver = new DefaultJsonTypeInfoResolver();
        resolver.Modifiers.Add(type =>
        {
            if (type.Type == typeof(Investigation))
                foreach (var property in type.Properties.Where(p => p.Name == "toolEvidence")) property.ShouldSerialize = (_, _) => false;
        });
        return new(JsonDefaults.Options) { TypeInfoResolver = resolver };
    }
    public static AppState StateSummary(AppState state) => JsonSerializer.Deserialize<AppState>(JsonSerializer.Serialize(state, summaryOptions), JsonDefaults.Options)!;
    void InitializeInvestigationEvidence()
    {
        Execute("CREATE TABLE IF NOT EXISTS investigation_evidence (investigation_id VARCHAR PRIMARY KEY, payload VARCHAR NOT NULL)");
        using var cmd = Command("SELECT investigation_id FROM investigation_evidence"); using var rows = cmd.ExecuteReader();
        while (rows.Read()) archivedInvestigations.Add(rows.GetString(0));
        rows.Close();
        InitializeStateArchive();
    }
    void ArchiveInvestigationEvidence(AppState state)
    {
        // Immutable result bodies are archived before publishing metadata. A failed state
        // publication can leave harmless orphan evidence, never a dangling published reference.
        // A check in progress is saved after every step, so its archive grows: it is rewritten whenever it carries more
        // evidence than was last archived (results are only ever appended, never changed).
        foreach (var batch in state.Investigations.Where(i => i.ToolEvidence.Count > 0 && (!archivedInvestigations.Contains(i.Id) || (archivedCounts.TryGetValue(i.Id, out var n) ? n < i.ToolEvidence.Count : i.Status == "Running"))).Chunk(32))
        {
            Execute("BEGIN TRANSACTION");
            try
            {
                foreach (var item in batch)
                    Execute(archivedInvestigations.Contains(item.Id) ? "INSERT OR REPLACE INTO investigation_evidence VALUES (?, ?)" : "INSERT OR IGNORE INTO investigation_evidence VALUES (?, ?)", item.Id, JsonSerializer.Serialize(item.ToolEvidence, JsonDefaults.Options));
                Execute("COMMIT");
                foreach (var item in batch) { archivedInvestigations.Add(item.Id); if (item.Status is "Running" or "Interrupted" or "Failed") archivedCounts[item.Id] = item.ToolEvidence.Count; else archivedCounts.Remove(item.Id); }
            }
            catch { Execute("ROLLBACK"); throw; }
        }
        // Bound the published state: history past the retention limits moves to the archive tables (StateRetention.cs).
        ApplyRetention(state, Clock.GetUtcNow());
    }
    public List<ToolEvidence> ReadInvestigationEvidence(string id)
    {
        lock (gate)
        {
            using var cmd = Command("SELECT payload FROM investigation_evidence WHERE investigation_id=?", id);
            return cmd.ExecuteScalar() is string text ? JsonSerializer.Deserialize<List<ToolEvidence>>(text, JsonDefaults.Options)! : [];
        }
    }
    public void HydrateInvestigationEvidence(AppState state)
    {
        foreach (var item in state.Investigations.Where(i => i.ToolEvidence.Count == 0)) item.ToolEvidence = ReadInvestigationEvidence(item.Id);
    }
}

// Historical archives remain immutable. Clean only caller-owned summary/detail
// copies so pre-redaction rejected replies cannot expose credentials in the UI.
internal sealed class InvestigationReadSanitizer(IConfiguration? configuration)
{
    readonly string[] secrets = PredbatMcpSafety.Secrets(configuration);
    readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> shortTextCache = new(StringComparer.Ordinal);
    public string? Clean(string? text)
    {
        if (text == null) return null;
        if (text.Length > 1000 || shortTextCache.Count >= 20000) return PredbatMcpSafety.CleanText(text, secrets);
        return shortTextCache.GetOrAdd(text, value => PredbatMcpSafety.CleanText(value, secrets));
    }
    public void SanitizeCopy(Investigation item)
    {
        item.Title = Clean(item.Title)!; item.Summary = Clean(item.Summary)!; item.Category = Clean(item.Category)!;
        item.Request = item.Request with { Question = Clean(item.Request.Question), Label = Clean(item.Request.Label), Trigger = Clean(item.Request.Trigger) };
        item.Steps = item.Steps.Select(x => Clean(x)!).ToList(); item.Evidence = item.Evidence.Select(x => Clean(x)!).ToList();
        item.Headline = Clean(item.Headline); item.Plain = Clean(item.Plain);
        item.Watching = item.Watching.Select(x => Clean(x)!).ToList();
        item.StepDetails = item.StepDetails.Select(x => x with { Label = Clean(x.Label)!, Detail = Clean(x.Detail)! }).ToList();
        item.NextSteps = item.NextSteps.Select(step => step with
        {
            Title = Clean(step.Title)!, Rationale = Clean(step.Rationale)!, SuggestedAction = Clean(step.SuggestedAction)!,
            Verification = Clean(step.Verification)!, Uncertainty = Clean(step.Uncertainty)!,
            DecisionNote = Clean(step.DecisionNote), Thread = CleanThread(step.Thread)
        }).ToList();
        foreach (var change in item.FileChanges)
        {
            change.Summary = Clean(change.Summary)!; change.Location = Clean(change.Location)!; change.Reason = Clean(change.Reason)!;
            change.Snippet = Clean(change.Snippet)!; change.Before = Clean(change.Before);
            change.DecisionNote = Clean(change.DecisionNote); change.Thread = CleanThread(change.Thread);
        }
        item.DecisionNote = Clean(item.DecisionNote); item.Thread = CleanThread(item.Thread);
        // Read(false) never hydrates evidence; normal polling touches metadata only.
        item.ToolEvidence = item.ToolEvidence.Select(SanitizeTool).ToList();
    }
    public void SanitizeCopy(Proposal item)
    {
        item.Title = Clean(item.Title)!; item.Summary = Clean(item.Summary)!;
        item.ExpectedEffect = Clean(item.ExpectedEffect)!; item.Tradeoff = Clean(item.Tradeoff)!;
        item.Evidence = item.Evidence.Select(x => Clean(x)!).ToList();
        item.DecisionNote = Clean(item.DecisionNote); item.Thread = CleanThread(item.Thread);
    }
    List<ReplyMessage> CleanThread(List<ReplyMessage> thread) => thread.Select(m => m with { Text = Clean(m.Text)!, Memory = Clean(m.Memory), SuggestedMemory = Clean(m.SuggestedMemory), Action = Clean(m.Action) }).ToList();
    public void SanitizeCopy(ConfigRevision item) => item.Reason = Clean(item.Reason)!;
    public void SanitizeCopy(Experiment item)
    {
        item.Title = Clean(item.Title)!; item.Hypothesis = Clean(item.Hypothesis)!;
        item.Result = Clean(item.Result)!; item.RevertReason = Clean(item.RevertReason)!;
        item.Decisions = item.Decisions.Select(decision => decision with { Notes = Clean(decision.Notes)! }).ToList();
    }
    public ToolEvidence SanitizeTool(ToolEvidence tool) => tool with
    {
        Request = Clean(tool.Request)!, Error = Clean(tool.Error), Label = Clean(tool.Label),
        // Primary-documentation excerpts/hashes retain their exact provenance.
        ResultJson = tool.Kind == "model" ? CleanModelResult(tool.ResultJson)
            : tool.Kind == "documentation" ? tool.ResultJson : CleanToolResult(tool.ResultJson)
    };
    string CleanToolResult(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return CleanValues(document.RootElement)?.ToJsonString() ?? "null";
        }
        catch (JsonException) { return Clean(json)!; }
    }
    JsonNode? CleanValues(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var result = new JsonObject();
            foreach (var property in element.EnumerateObject())
                // A SQL/configuration "key" field is a setting identifier. Keep
                // structural evidence and numeric values intact while masking
                // credential-bearing strings and explicit credential fields.
                result[Clean(property.Name)!] = !property.Name.Equals("key", StringComparison.OrdinalIgnoreCase) && PredbatMcpSafety.SensitiveKey(property.Name)
                    ? JsonValue.Create("[redacted]") : CleanValues(property.Value);
            return result;
        }
        if (element.ValueKind == JsonValueKind.Array)
        {
            var result = new JsonArray(); foreach (var item in element.EnumerateArray()) result.Add(CleanValues(item)); return result;
        }
        return element.ValueKind == JsonValueKind.String ? JsonValue.Create(Clean(element.GetString())) : JsonNode.Parse(element.GetRawText());
    }
    string CleanModelResult(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return PredbatMcpSafety.Clean(document.RootElement, secrets)?.ToJsonString() ?? "null";
        }
        catch (JsonException) { return JsonSerializer.Serialize(new { excerpt = Clean(json) }); }
    }
}
