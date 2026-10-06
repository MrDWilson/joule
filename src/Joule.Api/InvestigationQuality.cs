using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Joule;

/// <summary>
/// Server-side quality rules for what a check says: the house style for user-facing headlines, the verdict when the AI leaves it out,
/// plain headline and summary fields, repeat fingerprints, evidence-based confidence, step labels and failure wording.
/// </summary>
public static class InvestigationQuality
{
    public const int HeadlineLimit = 80, PlainLimit = 280, NoChangeLimit = 200;
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(200);

    // Only what is unambiguous jargon. Ordinary words ("local", "slot", "PV" inside a setting name) are deliberately allowed.
    static readonly Regex StateCode = new(@"(?<![A-Za-z0-9_/])(?:FrzChrg|FrzChg|HoldChrg|HoldChg|NoChrg|NoChg|FrzExp|HoldExp|FrzDis|HoldDis|Chrg|Exp)(?:/(?:Chrg|Exp))?(?![A-Za-z0-9_/])", RegexOptions.Compiled, RegexTimeout);
    static readonly Regex EntityId = new(@"\b(?:sensor|input_number|input_boolean|input_select|select|switch|binary_sensor|number|predbat|automation|script)\.[a-z0-9_]+\b", RegexOptions.Compiled, RegexTimeout);
    static readonly Regex IsoStamp = new(@"\b\d{4}-\d{2}-\d{2}(?:T\d{2}:\d{2}(?::\d{2}(?:\.\d+)?)?(?:Z|[+-]\d{2}:?\d{2})?|[ ]\d{2}:\d{2}(?::\d{2})?(?:Z| ?UTC))\b|\b\d{2}:\d{2}(?::\d{2})?Z\b", RegexOptions.Compiled, RegexTimeout);

    /// <summary>The user-facing fields the style check covers.</summary>
    public static readonly string[] StyledFields = ["headline", "plain", "title"];

    /// <summary>Banned tokens in the finish's user-facing fields: Predbat state codes, entity IDs, backticks and ISO/UTC timestamps.</summary>
    public static List<string> StyleIssues(JsonElement finish)
    {
        var issues = new List<string>();
        void Check(string where, string? text)
        {
            if (string.IsNullOrEmpty(text)) return;
            foreach (Match m in StateCode.Matches(text)) issues.Add($"{where} uses the Predbat code “{m.Value}” (write “{PredbatGlossary.Label(m.Value)}”)");
            foreach (Match m in EntityId.Matches(text)) issues.Add($"{where} names the entity “{m.Value}” (say which meter or setting in words)");
            if (text.Contains('`')) issues.Add($"{where} uses backticks");
            foreach (Match m in IsoStamp.Matches(text)) issues.Add($"{where} has the timestamp “{m.Value}” (write a UK time such as 09:30)");
        }
        foreach (var field in StyledFields)
            if (finish.TryGetProperty(field, out var v) && v.ValueKind == JsonValueKind.String) Check(field, v.GetString());
        if (finish.TryGetProperty("nextSteps", out var steps) && steps.ValueKind == JsonValueKind.Array)
            foreach (var step in steps.EnumerateArray())
                if (step.ValueKind == JsonValueKind.Object && step.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String) Check("a to-do title", t.GetString());
        return issues.Distinct().Take(8).ToList();
    }

    /// <summary>
    /// Server-side repair after the one corrective retry: state codes become glossary labels, entity IDs become the meter or setting name,
    /// backticks go, and timestamps become UK clock times.
    /// </summary>
    static readonly Regex LocalSuffix = new(@"\b(\d{1,2}:\d{2}(?:\s*(?:[–—-]|to)\s*\d{1,2}:\d{2})?)\s*\(?\blocal(?: time)?\b\)?", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100));
    public static string Sanitise(string? text, TimeZoneInfo zone, IReadOnlyDictionary<string, string>? entityNames = null)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";
        var result = PredbatGlossary.ReplaceCodes(text).Replace("`", "");
        result = EntityId.Replace(result, m => entityNames != null && entityNames.TryGetValue(m.Value, out var name) ? name : FriendlyEntity(m.Value));
        result = IsoStamp.Replace(result, m => ParseStamp(m.Value) is { } at ? TimeZoneInfo.ConvertTime(at, zone).ToString("HH:mm", Inv) : m.Value);
        // Every time shown is already home time, so "20:30–23:30 local" reads as "20:30–23:30".
        result = LocalSuffix.Replace(result, "$1");
        return Regex.Replace(result, @"[ \t]{2,}", " ").Trim();
    }

    static DateTimeOffset? ParseStamp(string value)
    {
        var text = value.Replace(" UTC", "Z").Replace("UTC", "Z");
        if (Regex.IsMatch(text, @"^\d{2}:\d{2}(:\d{2})?Z$")) text = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd", Inv) + "T" + text;
        return DateTimeOffset.TryParse(text, Inv, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var at) ? at : null;
    }

    /// <summary>"sensor.my_home_solar_generated" → "the My Home Solar Generated sensor".</summary>
    public static string FriendlyEntity(string entity)
    {
        var dot = entity.IndexOf('.');
        var words = (dot >= 0 ? entity[(dot + 1)..] : entity).Replace('_', ' ').Trim();
        if (words.StartsWith("predbat ", StringComparison.Ordinal)) words = words[8..];
        var title = CultureInfo.GetCultureInfo("en-GB").TextInfo.ToTitleCase(words);
        return dot > 0 && entity[..dot] is "sensor" or "binary_sensor" ? $"the {title} sensor" : $"the {title} setting";
    }

    /// <summary>Names for the entities in this home: mapped meters and Predbat settings, used when sanitising AI text.</summary>
    public static Dictionary<string, string> EntityNames(AppState s, TelemetryStatus? telemetry)
    {
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        if (telemetry != null)
            foreach (var (metric, entity) in telemetry.EntityMappings) names.TryAdd(entity, "your " + InvestigationBrief.MeterName(metric).ToLowerInvariant());
        foreach (var setting in s.Settings.Where(x => !string.IsNullOrEmpty(x.EntityId) && !string.IsNullOrEmpty(x.Name))) names.TryAdd(setting.EntityId, $"“{setting.Name}”");
        return names;
    }

    /// <summary>The value of the finish's verdict, or null when it is missing; "invalid" for anything else.</summary>
    public static string? VerdictValue(JsonElement root) =>
        root.TryGetProperty("verdict", out var v) && v.ValueKind != JsonValueKind.Null ? v.ValueKind == JsonValueKind.String ? v.GetString() : "invalid" : null;

    static readonly Regex QuietTitle = new(@"^\s*(no (material |new )?change|nothing new|on plan|went to plan|all (normal|on track)|no new (problem|finding|issue)s?)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled, RegexTimeout);
    /// <summary>
    /// A finish that still has no verdict after the corrective retry is never failed for it. It is no_change when it raises nothing
    /// (no suggestions, to-dos or file edits) and either says so in its title or its measured impact is under 5p; otherwise a neutral
    /// "finding".
    /// </summary>
    public static string InferVerdict(string title, int proposals, int nextSteps, int fileChanges, double? impactPence)
    {
        if (proposals + nextSteps + fileChanges == 0 && (QuietTitle.IsMatch(title) || impactPence is { } p && Math.Abs(p) < 5)) return "no_change";
        return "finding";
    }

    /// <summary>How many earlier steps a resumed check carries forward at most.</summary>
    public const int ResumeStepLimit = 150;
    /// <summary>
    /// The earlier steps a resumed check keeps: everything it gathered, without the retry notices and the stop, interrupt and resume
    /// markers of earlier tries, so a long provider outage resumed again and again doesn't grow one record by hundreds of steps.
    /// </summary>
    public static List<InvestigationStep> ResumableSteps(IEnumerable<InvestigationStep> steps) => steps.Where(d => d.Kind != "retry" && !AttemptNoise(d.Detail)).TakeLast(ResumeStepLimit).ToList();
    public static List<string> ResumableSteps(IEnumerable<string> steps) => steps.Where(d => !AttemptNoise(d)).TakeLast(ResumeStepLimit).ToList();
    static bool AttemptNoise(string detail) => detail.StartsWith("provider: retry ", StringComparison.Ordinal) || Regex.IsMatch(detail, @"^server: ([a-z_]+|resumed .*|interrupted .*)$");

    /// <summary>Cuts at a word boundary with an ellipsis.</summary>
    public static string Shorten(string text, int max)
    {
        text = Regex.Replace(text.Trim(), @"\s+", " ");
        if (text.Length <= max) return text;
        var cut = text.LastIndexOf(' ', max - 1);
        return text[..(cut > max / 2 ? cut : max - 1)].TrimEnd(' ', ',', ';', ':', '.', '—', '-') + "…";
    }

    /// <summary>First sentence (or the whole text when it has one), bounded.</summary>
    public static string FirstSentences(string text, int max)
    {
        var clean = Regex.Replace(text.Trim(), @"\s+", " ");
        var sentences = Regex.Split(clean, @"(?<=[.!?])\s+(?=[A-Z0-9£])");
        var result = new StringBuilder();
        foreach (var sentence in sentences)
        {
            if (result.Length > 0 && result.Length + 1 + sentence.Length > max) break;
            if (result.Length > 0) result.Append(' ');
            result.Append(sentence);
        }
        return Shorten(result.Length == 0 ? clean : result.ToString(), max);
    }

    /// <summary>The fixed one-line summary of a check that found nothing new (the model's narration of what worked is not kept).</summary>
    public static string NoChangeLine(DateTimeOffset? from, DateTimeOffset to, TimeZoneInfo zone, string? note = null)
    {
        var window = from is { } f && to - f < TimeSpan.FromDays(2) ? $"Checked {InvestigationBrief.Clock(f, zone)}–{InvestigationBrief.Clock(to, zone)}" : $"Checked up to {InvestigationBrief.Clock(to, zone)}";
        var line = $"{window}: battery and costs went to plan, nothing new needs your attention.";
        if (!string.IsNullOrWhiteSpace(note)) line += " " + note.Trim();
        return Shorten(line, NoChangeLimit);
    }

    static readonly Regex KeyLike = new(@"\b[a-z][a-z0-9]*(?:_[a-z0-9]+)+\b", RegexOptions.Compiled, RegexTimeout);
    /// <summary>Category plus the setting keys and entity IDs a finding is about (sorted): the same problem found again has the same print.</summary>
    public static string Fingerprint(Investigation i, IEnumerable<Proposal> proposals, IReadOnlyCollection<string> knownKeys)
    {
        var text = string.Join(" ", new[] { i.Title, i.Summary }.Concat(i.Evidence).Concat(i.NextSteps.Select(n => n.Title + " " + n.SuggestedAction)).Concat(i.FileChanges.Select(f => f.Snippet)));
        var keys = new SortedSet<string>(StringComparer.Ordinal);
        foreach (Match m in EntityId.Matches(text)) keys.Add(m.Value);
        foreach (Match m in KeyLike.Matches(text)) if (knownKeys.Contains(m.Value)) keys.Add(m.Value);
        foreach (var c in proposals.SelectMany(p => p.Changes)) keys.Add(c.Key + (c.After is { Length: > 0 } a ? "=" + a.ToLowerInvariant() : ""));
        var category = Regex.Replace((i.Category ?? "").ToLowerInvariant(), @"[^a-z]+", " ").Trim();
        // Without any key the title words carry the identity (numbers removed, so "2.9 kWh" and "3.1 kWh" match).
        var basis = keys.Count > 0 ? category + "|" + string.Join(",", keys) : category + "|" + Regex.Replace(i.Title.ToLowerInvariant(), @"[^a-z]+", " ").Trim();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(basis)))[..16].ToLowerInvariant();
    }

    /// <summary>
    /// Confidence from what the finding rests on: High when it cites at least two successful measured sources (plan versus actual,
    /// meter summaries, queries, Predbat history) or one measured source plus Predbat's log; Medium for one measured or log source;
    /// Low when it rests only on configuration, documentation or sources that failed.
    /// </summary>
    public static string Confidence(IEnumerable<string> cited, IReadOnlyList<ToolEvidence> tools)
    {
        var used = cited.Select(id => tools.FirstOrDefault(t => t.Success && (t.Id == id || t.SourceReferences.Any(r => r.Id == id)))).Where(t => t != null).Select(t => t!).DistinctBy(t => t.Id).ToList();
        var measured = used.Count(t => t.Kind is "plan_vs_actual" or "summary" or "query" or "snapshots" || t.Kind == "mcp" && t.Request.Contains("get_entity_history", StringComparison.Ordinal));
        var logs = used.Count(t => t.Kind == "mcp" && !t.Request.Contains("get_entity_history", StringComparison.Ordinal));
        if (measured >= 2 || measured >= 1 && logs >= 1) return "High";
        if (measured + logs >= 1) return "Medium";
        return "Low";
    }

    /// <summary>A plain description of one read, for the step list and the progress panel; the raw request stays in the technical view.</summary>
    public static string StepLabel(ToolEvidence tool, TimeZoneInfo zone)
    {
        string Window(string request)
        {
            var parts = request.Split('/');
            return parts.Length == 2 && DateTimeOffset.TryParse(parts[0], Inv, DateTimeStyles.None, out var f) && DateTimeOffset.TryParse(parts[1], Inv, DateTimeStyles.None, out var t) ? Span(f, t, zone) : "";
        }
        var failed = tool.Success ? "" : " (not available)";
        switch (tool.Kind)
        {
            case "plan_vs_actual": return $"Compared the plan with your meters, {Window(tool.Request)}{failed}".TrimEnd(',', ' ');
            case "summary": return $"Added up your meters, {Window(tool.Request)}{failed}".TrimEnd(',', ' ');
            case "query": return "Looked up stored readings" + failed;
            case "configuration": return tool.Id == "configuration" ? "Read Predbat's settings" : tool.Request.StartsWith("Runtime setting search: ", StringComparison.Ordinal) ? $"Searched Predbat's settings for “{tool.Request[24..]}”{failed}" : "Read Predbat's settings" + failed;
            case "documentation": return $"Read Predbat's guide on “{Shorten(tool.Request, 60)}”{failed}";
            case "snapshots": return "Looked at Predbat's earlier plans" + failed;
            case "schema": return "Checked how to call a Predbat tool";
            case "evidence": return Regex.Match(tool.Request, @"search: (.+)$") is { Success: true } s ? $"Re-read earlier results for “{Shorten(s.Groups[1].Value, 40)}”" : "Re-read earlier results";
            case "model": return "The AI's answer was rejected";
            case "mcp":
                var name = Regex.Match(tool.Request, "\"name\"\\s*:\\s*\"([a-z_]+)\"") is { Success: true } n ? n.Groups[1].Value : Regex.Match(tool.Request, @"^([a-z_]+)") is { Success: true } m ? m.Groups[1].Value : "";
                var search = Regex.Match(tool.Request, "\"(?:search|pattern)\"\\s*:\\s*\"([^\"]{1,60})\"") is { Success: true } q ? $" for “{q.Groups[1].Value}”" : "";
                var times = Regex.Matches(tool.Request, "\"(?:start|end)\"\\s*:\\s*\"[^\"]*?(\\d{2}:\\d{2})").Select(x => x.Groups[1].Value).ToList();
                var when = times.Count == 2 ? $", {times[0]}–{times[1]}" : "";
                var entity = Regex.Match(tool.Request, "\"entity_id\"\\s*:\\s*\"([a-z_]+\\.[a-z0-9_]+)\"") is { Success: true } e ? FriendlyEntity(e.Groups[1].Value) : "a sensor";
                return name switch
                {
                    "get_log" => $"Read Predbat's log{search}{when}{failed}",
                    "get_entity_history" => $"Read the history of {entity}{when}{failed}",
                    "get_apps_config" or "get_apps" => "Read Predbat's configuration file" + failed,
                    "get_plan" => "Read Predbat's current plan" + failed,
                    "get_status" => "Read Predbat's status" + failed,
                    "" => "Tried a Predbat tool that isn't available",
                    _ => $"Read Predbat's {name.Replace("get_", "").Replace('_', ' ')}{failed}"
                };
            default: return "Read evidence" + failed;
        }
    }

    public static string Span(DateTimeOffset from, DateTimeOffset to, TimeZoneInfo zone)
    {
        var f = TimeZoneInfo.ConvertTime(from, zone); var t = TimeZoneInfo.ConvertTime(to, zone);
        return f.Date == t.Date ? $"{f:HH:mm}–{t:HH:mm}" : $"{f.ToString("ddd HH:mm", Inv)}–{t.ToString("ddd HH:mm", Inv)}";
    }

    /// <summary>The plain request label shown instead of the scheduled prompt.</summary>
    public static string ScheduledLabel(DateTimeOffset? since, DateTimeOffset now, TimeZoneInfo zone, string? trigger) =>
        $"Automatic check of {(since is { } s && now - s < TimeSpan.FromDays(2) ? Span(s, now, zone) : "the latest data")}{(string.IsNullOrWhiteSpace(trigger) ? "" : $" ({Shorten(trigger, 60)})")}: battery against the plan · Predbat warnings · forecast accuracy · missed savings · sensor health · file edits you applied.";

    /// <summary>The outcome of a check that didn't finish, in plain words, with the kind, code and next try for the UI.</summary>
    public sealed record FailureDescription(string Status, string Kind, string Message, string? ProviderCode, string? Reference, bool Retryable, DateTimeOffset? ResetsAt);

    /// <summary>
    /// The one sentence the app shell shows for a check that didn't finish (state AnalysisError): what stopped it and that nothing
    /// changed, e.g. "Stopped when Joule restarted at 19:08. Nothing was changed." It never carries a "Next try" time (that goes stale
    /// while the banner is up) or "kept below" (there is nothing below a banner); the full text stays on the check itself.
    /// </summary>
    public static string ShellMessage(FailureDescription failure, DateTimeOffset now, TimeZoneInfo zone)
    {
        const string nothing = "Nothing was changed.";
        if (failure.Kind == "restart") return $"Stopped when Joule restarted at {InvestigationBrief.Clock(now, zone)}. {nothing}";
        if (failure.Kind == "stopped") return $"You stopped this check. {nothing}";
        var first = Regex.Match(failure.Message, @"^.*?[.!?](?=\s|$)").Value;
        if (first.Length == 0 || first.Contains(nothing, StringComparison.Ordinal)) first = failure.Message.Replace(nothing, "").Trim();
        first = Regex.Replace(first, @"\s*Next try [^.]*\.?", "").Trim();
        return Shorten($"{first} {nothing}".Trim(), 160);
    }

    public static FailureDescription Describe(Exception ex, string provider, bool stoppedByUser, bool shuttingDown, TimeZoneInfo zone, DateTimeOffset now)
    {
        var who = provider.Equals("ChatGpt", StringComparison.OrdinalIgnoreCase) ? "ChatGPT" : provider == "Demo" ? "The demo" : "The AI service";
        const string nothing = "Nothing was changed.";
        if (ex is OperationCanceledException)
            return stoppedByUser
                ? new("Interrupted", "stopped", $"You stopped this check. {nothing} What it found so far is kept below and you can resume it.", null, null, false, null)
                : new("Interrupted", "restart", $"Joule restarted during this check ({InvestigationBrief.Clock(now, zone)}). {nothing} What it found so far is kept below; it will resume shortly.", null, null, true, null);
        if (ex is ModelProviderException p)
        {
            var code = p.Code;
            var kind = p.Kind switch
            {
                ModelFailureKind.Timeout => "timeout", ModelFailureKind.RateLimited => "rate_limited", ModelFailureKind.UsageLimit => "usage_limit",
                ModelFailureKind.SignIn => "sign_in", ModelFailureKind.Rejected => "rejected", ModelFailureKind.Incomplete => "incomplete",
                ModelFailureKind.ContentFilter => "content_filter", _ => "provider_busy"
            };
            // The model client's messages are already plain English ("… Joule tried 6 times over 13 min."); an overloaded server reads "server busy".
            var message = p.Message.Trim();
            if (p.Kind == ModelFailureKind.Transient && code is "server_is_overloaded" or "overloaded" or "server_overloaded" or "overloaded_error")
                message = $"{who} didn't answer (server busy)." + (Regex.Match(p.Message, @" Joule tried \d+ times[^.]*\.") is { Success: true } tried ? tried.Value : "");
            if (p.Kind == ModelFailureKind.SignIn) message += " Reconnect it in AI settings.";
            return new("Failed", kind, $"{message} {nothing}", code, p.Info.Reference, p.Retryable || p.Kind == ModelFailureKind.Timeout, p.Info.ResetsAt);
        }
        if (ex is DomainException d && d.Status is 400)
            return new("Failed", "setup", d.Message.TrimEnd('.') + ". " + nothing, null, null, false, null);
        var reason = ex is DomainException domain ? domain.Message : $"The AI's answer couldn't be checked ({ex.GetType().Name}).";
        return new("Failed", "invalid_answer", reason.Contains(nothing, StringComparison.Ordinal) || reason.Contains("No configuration changes", StringComparison.Ordinal) || reason.Contains("no changes", StringComparison.OrdinalIgnoreCase) ? reason : reason.TrimEnd() + " " + nothing, null, null, false, null);
    }

}
