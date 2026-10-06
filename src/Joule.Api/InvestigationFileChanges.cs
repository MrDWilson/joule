using System.Text.Json;
using System.Text.RegularExpressions;

namespace Joule;

/// <summary>
/// A reviewable edit to a Predbat configuration file (for example apps.yaml) that the user applies by hand.
/// The app never writes these files; it shows the snippet, records whether the user applied it, and asks
/// the next review to verify it.
/// </summary>
public sealed class ConfigFileChange
{
    public string Id { get; set; } = "";
    /// <summary>pending, applied (by the user, awaiting verification), verified, dismissed (by the user) or retired (no longer carried forward).</summary>
    public string Status { get; set; } = "pending";
    public string File { get; set; } = "";
    public string Summary { get; set; } = "";
    public string Location { get; set; } = "";
    public string Snippet { get; set; } = "";
    public string? Before { get; set; }
    public string Reason { get; set; } = "";
    public DateTimeOffset? AppliedAt { get; set; }
    public DateTimeOffset? ClosedAt { get; set; }
    public string? ClosedReason { get; set; }
    /// <summary>The user's note when they dismissed it; with DecidedAt it suppresses re-raising the same change.</summary>
    public string? DecisionNote { get; set; }
    public DateTimeOffset? DecidedAt { get; set; }
    List<ReplyMessage> thread = [];
    public List<ReplyMessage> Thread { get => thread; set => thread = value ?? []; }
}

public partial class Investigation
{
    List<ConfigFileChange> fileChanges = [];
    // Existing persisted records predate this field; tolerate an explicit null too.
    public List<ConfigFileChange> FileChanges { get => fileChanges; set => fileChanges = value ?? []; }
}

public static class InvestigationFileChanges
{
    public const int MaxChanges = 2, SummaryLimit = 300, LocationLimit = 200, SnippetLimit = 1500, ReasonLimit = 400, FileLimit = 100;
    static readonly HashSet<string> fields = ["file", "summary", "location", "snippet", "before", "reason"];
    static readonly Regex FileName = new(@"^[A-Za-z0-9_][A-Za-z0-9_.\-]*(/[A-Za-z0-9_][A-Za-z0-9_.\-]*)*\.(yaml|yml|json|conf|cfg|txt)$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    public static bool IsOpen(ConfigFileChange change) => change.Status is "pending" or "applied";

    public static List<ConfigFileChange> Parse(JsonElement root)
    {
        if (!root.TryGetProperty("fileChanges", out var value) || value.ValueKind == JsonValueKind.Null) return [];
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() > MaxChanges)
            throw new DomainException($"fileChanges must be an optional array of at most {MaxChanges} configuration file edits.", 502);
        string Text(JsonElement change, string name, int max)
        {
            if (!change.TryGetProperty(name, out var text) || text.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(text.GetString()) || text.GetString()!.Length > max)
                throw new DomainException($"fileChanges {name} must contain 1–{max} characters.", 502);
            return text.GetString()!;
        }
        var result = new List<ConfigFileChange>();
        foreach (var change in value.EnumerateArray())
        {
            if (change.ValueKind != JsonValueKind.Object || change.EnumerateObject().Any(p => !fields.Contains(p.Name)))
                throw new DomainException("fileChanges entries may contain only file, summary, location, snippet, before and reason.", 502);
            var file = Text(change, "file", FileLimit).Trim();
            if (file.Contains("..", StringComparison.Ordinal) || !FileName.IsMatch(file))
                throw new DomainException("fileChanges file must be a relative configuration file name such as apps.yaml.", 502);
            string? before = null;
            if (change.TryGetProperty("before", out var b) && b.ValueKind != JsonValueKind.Null) before = Text(change, "before", SnippetLimit);
            var parsed = new ConfigFileChange
            {
                Id = Guid.NewGuid().ToString("N"), File = file,
                Summary = Text(change, "summary", SummaryLimit), Location = Text(change, "location", LocationLimit),
                Snippet = Text(change, "snippet", SnippetLimit), Before = before, Reason = Text(change, "reason", ReasonLimit)
            };
            if (before != null && Normalise(before) == Normalise(parsed.Snippet))
                throw new DomainException("fileChanges snippet must differ from the text it replaces.", 502);
            if (result.Any(x => SameChange(x, parsed))) continue;
            result.Add(parsed);
        }
        return result;
    }

    /// <summary>Whitespace-insensitive identity of a file edit, used to recognise the same change raised again.</summary>
    public static bool SameChange(ConfigFileChange a, ConfigFileChange b) =>
        string.Equals(a.File, b.File, StringComparison.OrdinalIgnoreCase) && Normalise(a.Snippet) == Normalise(b.Snippet);
    static string Normalise(string text) => Regex.Replace(text, @"\s+", " ").Trim().ToLowerInvariant();

    /// <summary>
    /// Publishes a finished investigation's file changes. A change already open from an earlier investigation is
    /// carried forward instead of duplicated, and one the user dismissed recently is not raised again. When the
    /// model returned keepFollowUps, earlier open changes it omitted are closed: pending ones retire, and ones the
    /// user marked applied count as verified.
    /// </summary>
    public static void Reconcile(AppState s, Investigation current, HashSet<string>? keep, int suppressionDays = RecommendationDecisions.SuppressionDays, TimeZoneInfo? zone = null)
    {
        var earlier = s.Investigations.Where(i => i.Id != current.Id).SelectMany(i => i.FileChanges).ToList();
        var cutoff = DateTimeOffset.UtcNow.AddDays(-suppressionDays);
        foreach (var change in current.FileChanges.ToList())
        {
            if (earlier.FirstOrDefault(x => IsOpen(x) && SameChange(x, change)) is { } open)
            {
                current.FileChanges.Remove(change); keep?.Add(open.Id);
                current.Steps.Add($"server: file change “{Cut(change.Summary)}” is already open from an earlier investigation; carried forward");
                current.StepDetails.Add(new(DateTimeOffset.UtcNow, "server", $"The {change.File} edit is already waiting for you from an earlier check", current.Steps[^1]));
            }
            else if (earlier.Any(x => x.Status == "dismissed" && x.DecidedAt >= cutoff && SameChange(x, change)))
            {
                current.FileChanges.Remove(change);
                current.Steps.Add($"server: file change “{Cut(change.Summary)}” not raised; you dismissed the same change in the last {suppressionDays} days");
                current.StepDetails.Add(new(DateTimeOffset.UtcNow, "server", $"Skipped a {change.File} edit you declined recently", current.Steps[^1]));
            }
        }
        if (keep is null) return;
        foreach (var prior in s.Investigations.Where(i => i.Id != current.Id))
            foreach (var change in prior.FileChanges.Where(x => IsOpen(x) && !keep.Contains(x.Id)))
            {
                var applied = change.Status == "applied";
                change.Status = applied ? "verified" : "retired"; change.ClosedAt = DateTimeOffset.UtcNow;
                var when = InvestigationBrief.Clock(current.At, zone ?? LondonZone());
                change.ClosedReason = applied ? $"Verified by the {when} check" : $"No longer needed: the {when} check didn't carry it forward";
            }
    }
    static string Cut(string text) => text.Length <= 100 ? text : text[..100] + "…";
    static TimeZoneInfo LondonZone()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById("Europe/London"); } catch (Exception e) when (e is TimeZoneNotFoundException or InvalidTimeZoneException) { return TimeZoneInfo.Utc; }
    }

    public static ConfigFileChange Find(AppState s, string investigationId, string changeId)
    {
        var investigation = s.Investigations.FirstOrDefault(i => i.Id == investigationId) ?? throw new DomainException("Investigation not found.", 404);
        return investigation.FileChanges.FirstOrDefault(x => x.Id == changeId) ?? throw new DomainException("Configuration file change not found.", 404);
    }

    public static void MarkApplied(AppState s, string investigationId, string changeId)
    {
        var change = Find(s, investigationId, changeId);
        if (change.Status != "pending") throw new DomainException(change.Status == "applied" ? "This change is already marked as applied." : "This change is closed.");
        change.Status = "applied"; change.AppliedAt = DateTimeOffset.UtcNow;
        ChangeEngine.Log(s, "decision", $"You marked the {change.File} change “{change.Summary}” as applied. The next review will verify it.");
    }
}
