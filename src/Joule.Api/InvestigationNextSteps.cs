using System.Text.Json;

namespace Joule;

// Narrative follow-ups are part of an investigation. They have no change payload,
// permission, approval or execution state, and are never passed to ChangeEngine.
public sealed record InvestigationNextStep
{
    public string Id { get; set; } = "";
    /// <summary>open until a later investigation stops carrying it forward (keepFollowUps) or the user dismisses it.</summary>
    public string Status { get; set; } = "open";
    public DateTimeOffset? ClosedAt { get; set; }
    public string? ClosedReason { get; set; }
    public string Title { get; set; } = "";
    public string Rationale { get; set; } = "";
    public string SuggestedAction { get; set; } = "";
    public string Verification { get; set; } = "";
    public string Uncertainty { get; set; } = "";
    public List<string> EvidenceReferences { get; set; } = [];
    /// <summary>The user's note when they dismissed it; with DecidedAt it suppresses re-raising the same follow-up.</summary>
    public string? DecisionNote { get; set; }
    public DateTimeOffset? DecidedAt { get; set; }
    List<ReplyMessage> thread = [];
    public List<ReplyMessage> Thread { get => thread; set => thread = value ?? []; }
}

public partial class Investigation
{
    List<InvestigationNextStep> nextSteps = [];
    // Existing persisted records predate this field; tolerate an explicit null too.
    public List<InvestigationNextStep> NextSteps { get => nextSteps; set => nextSteps = value ?? []; }
}

public static class InvestigationNextSteps
{
    static readonly HashSet<string> fields = ["title", "rationale", "suggestedAction", "verification", "uncertainty", "evidenceReferences"];
    public const int MaxSteps = 3, TextLimit = 400;

    /// <summary>IDs of earlier open follow-ups the model still considers valid; null when the reply did not say.</summary>
    public static HashSet<string>? ParseKeepFollowUps(JsonElement root)
    {
        if (!root.TryGetProperty("keepFollowUps", out var keep) || keep.ValueKind == JsonValueKind.Null) return null;
        if (keep.ValueKind != JsonValueKind.Array || keep.GetArrayLength() > 50 || keep.EnumerateArray().Any(k => k.ValueKind != JsonValueKind.String || k.GetString()!.Length > 100))
            throw new DomainException("keepFollowUps must be an optional array of up to 50 follow-up IDs.", 502);
        return keep.EnumerateArray().Select(k => k.GetString()!).ToHashSet(StringComparer.Ordinal);
    }

    public static List<InvestigationNextStep> Parse(JsonElement root, IReadOnlyList<ToolEvidence> tools)
    {
        if (!root.TryGetProperty("nextSteps", out var value) || value.ValueKind == JsonValueKind.Null) return [];
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() > MaxSteps)
            throw new DomainException($"nextSteps must be an optional array of at most {MaxSteps} manual follow-ups.", 502);
        string Text(JsonElement step, string name, int max = TextLimit)
        {
            if (!step.TryGetProperty(name, out var text) || text.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(text.GetString()) || text.GetString()!.Length > max)
                throw new DomainException($"nextSteps {name} must contain 1–{max} characters.", 502);
            return text.GetString()!;
        }
        var successfulIds = tools.Where(t => t.Success && t.Kind != "model")
            .SelectMany(t => t.SourceReferences.Select(r => r.Id).Prepend(t.Id)).ToHashSet(StringComparer.Ordinal);
        var result = new List<InvestigationNextStep>();
        foreach (var step in value.EnumerateArray())
        {
            if (step.ValueKind != JsonValueKind.Object || step.EnumerateObject().Any(p => !fields.Contains(p.Name)))
                throw new DomainException("nextSteps entries may contain only title, rationale, suggestedAction, verification, uncertainty and evidenceReferences; no executable change payloads.", 502);
            if (!step.TryGetProperty("evidenceReferences", out var references) || references.ValueKind != JsonValueKind.Array ||
                references.GetArrayLength() is < 1 or > 16 || references.EnumerateArray().Any(r =>
                    r.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(r.GetString()) || r.GetString()!.Length > 100 || !successfulIds.Contains(r.GetString()!)))
                throw new DomainException("nextSteps evidenceReferences must contain 1–16 IDs of successfully retrieved tools or primary documentation from this investigation.", 502);
            result.Add(new InvestigationNextStep
            {
                Id = Guid.NewGuid().ToString("N"),
                Title = Text(step, "title", 140), Rationale = Text(step, "rationale"),
                SuggestedAction = Text(step, "suggestedAction"), Verification = Text(step, "verification"),
                Uncertainty = Text(step, "uncertainty"),
                EvidenceReferences = references.EnumerateArray().Select(r => r.GetString()!).Distinct(StringComparer.Ordinal).ToList()
            });
        }
        return result;
    }
}
