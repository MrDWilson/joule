using System.Globalization;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Joule;

/// <summary>What one canonical plan action means for the battery. Paraphrased from Predbat's documentation.</summary>
public sealed record GlossaryAction(string Key, string Label, string PredbatName, string Description, string Battery, string SocDirection, bool MayCharge, bool MayDischarge, string Tone, string Rate);

/// <summary>A recognised Predbat code or status string. <see cref="Key"/> is the canonical plan action, or null for statuses that
/// are not plan actions (read-only, calibration, error). Label and description fall back to the canonical action's.</summary>
public sealed record GlossaryEntry(string Id, string? Key, string Label, string Description, string Battery, string SocDirection, bool MayCharge, bool MayDischarge);

/// <summary>Plan-state code parsed from Predbat text such as "Chrg&amp;nearr; 70%" or "Exp↘ 4%".</summary>
public sealed record ParsedPlanState(string Code, GlossaryEntry? Entry, double? TargetPercent, bool Forced, bool HoldForCar);

/// <summary>
/// The single server-side glossary of Predbat plan states (Knowledge/predbat-states.json). Every place that stores or shows
/// a plan action goes through here, so "Chrg", "Charge", "FrzExp" and "Hold for car" always mean one documented thing.
/// The web UI imports a byte-identical copy of the same JSON (web/src/lib/predbat-states.json).
/// </summary>
public static class PredbatGlossary
{
    public const string UnknownKey = "unknown";
    public const string UnknownLabel = "Other Predbat state";
    public static readonly string[] ChargeSide = ["charge", "freeze-charge", "hold-charge", "no-charge"];
    public static readonly string[] ExportSide = ["export", "freeze-export", "hold-export"];
    public static IReadOnlyDictionary<string, GlossaryAction> Actions { get; }
    public static IReadOnlyList<GlossaryEntry> Entries { get; }
    public static IReadOnlyDictionary<string, string> ReasonTemplates { get; }
    public static IReadOnlyDictionary<string, (string Label, bool Estimated)> RateTypes { get; }
    public static string DocsRef { get; }
    /// <summary>The raw JSON text, exactly as embedded, for mirroring checks.</summary>
    public static string SourceJson { get; }
    static readonly Dictionary<string, GlossaryEntry> byCode = new(StringComparer.Ordinal);

    static PredbatGlossary()
    {
        SourceJson = ReadResource("predbat-states.json");
        using var json = JsonDocument.Parse(SourceJson);
        var root = json.RootElement;
        DocsRef = root.GetProperty("docsRef").GetString() ?? "";
        var actions = new Dictionary<string, GlossaryAction>(StringComparer.Ordinal);
        foreach (var item in root.GetProperty("actions").EnumerateObject())
        {
            var a = item.Value;
            actions[item.Name] = new(item.Name, a.GetProperty("label").GetString()!, a.GetProperty("predbatName").GetString()!, a.GetProperty("description").GetString()!, a.GetProperty("battery").GetString()!,
                a.GetProperty("socDirection").GetString()!, a.GetProperty("mayCharge").GetBoolean(), a.GetProperty("mayDischarge").GetBoolean(), a.GetProperty("tone").GetString()!, a.GetProperty("rate").GetString()!);
        }
        Actions = actions;
        var entries = new List<GlossaryEntry>();
        foreach (var e in root.GetProperty("states").EnumerateArray())
        {
            var key = e.GetProperty("key").ValueKind == JsonValueKind.Null ? null : e.GetProperty("key").GetString();
            var action = key is null ? null : actions[key];
            string Text(string name, string? fallback) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : fallback ?? "";
            bool Flag(string name, bool fallback) => e.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : fallback;
            var entry = new GlossaryEntry(e.GetProperty("id").GetString()!, key, Text("label", action?.Label), Text("description", action?.Description), Text("battery", action?.Battery),
                Text("socDirection", action?.SocDirection ?? "unknown"), Flag("mayCharge", action?.MayCharge ?? true), Flag("mayDischarge", action?.MayDischarge ?? true));
            entries.Add(entry);
            foreach (var code in e.GetProperty("codes").EnumerateArray()) byCode[Fold(code.GetString()!)] = entry;
            byCode.TryAdd(Fold(entry.Id), entry);
        }
        // Canonical keys are themselves valid codes (stored plan_slots.action holds them).
        foreach (var key in actions.Keys) byCode.TryAdd(Fold(key), entries.First(x => x.Id == key));
        Entries = entries;
        ReasonTemplates = root.GetProperty("reasons").EnumerateObject().ToDictionary(x => x.Name, x => x.Value.GetString()!, StringComparer.Ordinal);
        RateTypes = root.GetProperty("rateTypes").EnumerateObject().ToDictionary(x => x.Name, x => (x.Value.GetProperty("label").GetString()!, x.Value.GetProperty("estimated").GetBoolean()), StringComparer.Ordinal);
    }

    internal static string ReadResource(string name)
    {
        using var stream = typeof(PredbatGlossary).Assembly.GetManifestResourceStream("Joule.Knowledge." + name) ?? throw new InvalidOperationException($"Embedded knowledge file {name} is missing.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    static string Fold(string code) => Regex.Replace(code.Trim(), @"\s+", " ").ToLowerInvariant();

    static readonly Regex Entity = new(@"&(#x[0-9a-fA-F]+|#\d+|[a-zA-Z]+);", RegexOptions.Compiled);
    static readonly Dictionary<string, string> Arrows = new(StringComparer.OrdinalIgnoreCase) { ["rarr"] = "→", ["larr"] = "←", ["uarr"] = "↑", ["darr"] = "↓", ["nearr"] = "↗", ["searr"] = "↘", ["nwarr"] = "↖", ["swarr"] = "↙" };
    /// <summary>Decodes the HTML entities Predbat writes into plan text, including HTML5 arrows that WebUtility doesn't know.</summary>
    public static string DecodeEntities(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var decoded = Entity.Replace(text, m =>
        {
            var body = m.Groups[1].Value;
            if (body.StartsWith("#x", StringComparison.OrdinalIgnoreCase) && int.TryParse(body[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hex) && hex is > 0 and <= 0x10FFFF) return char.ConvertFromUtf32(hex);
            if (body.StartsWith('#') && int.TryParse(body[1..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var dec) && dec is > 0 and <= 0x10FFFF) return char.ConvertFromUtf32(dec);
            return Arrows.TryGetValue(body, out var arrow) ? arrow : WebUtility.HtmlDecode(m.Value);
        });
        // Split plan cells are joined with raw table markup; keep only the text.
        return Regex.Replace(decoded, "<[^>]*>", " ");
    }

    const string Car = "\U0001F697";
    // Arrows, Predbat's forced marker (U+214E), the slow-rate snail, the mixed-slot asterisk and the alert/manual SoC symbols.
    static readonly Regex Decorations = new(@"[←-⇿⬀-⯿ⅎ⚠✎️*]|" + "\U0001F40C", RegexOptions.Compiled);

    /// <summary>Parses one Predbat state text into its code, glossary entry and any trailing target ("Chrg↗ 70%" → charge, 70).</summary>
    public static ParsedPlanState Parse(string? raw)
    {
        var text = DecodeEntities(raw);
        var holdForCar = text.Contains(Car, StringComparison.Ordinal);
        var forced = text.Contains('ⅎ');
        var cleaned = Decorations.Replace(text.Replace(Car, " "), " ");
        cleaned = Regex.Replace(cleaned, @"\s+", " ").Trim();
        var targetMatch = Regex.Match(cleaned, @"^(?<code>.*?[A-Za-z\])])\s*(?<target>\d+(?:\.\d+)?)\s*%?$");
        if (targetMatch.Success && Find(targetMatch.Groups["code"].Value) is { } targeted)
            return new(targetMatch.Groups["code"].Value.Trim(), targeted, double.Parse(targetMatch.Groups["target"].Value, CultureInfo.InvariantCulture), forced, holdForCar);
        if (cleaned.Length == 0 && holdForCar) return new(Car, byCode[Fold(Car)], null, forced, true);
        return new(cleaned, Find(cleaned), null, forced, holdForCar);
    }

    /// <summary>Finds the glossary entry for a code or status, trying progressively looser forms: exact, without status suffixes
    /// such as "[Alert]" or ", Hold for car", and finally as a two-part split such as "Chrg/Exp".</summary>
    public static GlossaryEntry? Find(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;
        var text = Regex.Replace(DecodeEntities(code), @"\s+", " ").Trim();
        if (byCode.TryGetValue(Fold(text), out var exact)) return exact;
        var stripped = Regex.Replace(Decorations.Replace(text.Replace(Car, " "), " "), @"\s+", " ").Trim();
        if (byCode.TryGetValue(Fold(stripped), out var plain)) return plain;
        // Status suffixes Predbat appends: " [Alert]", " [Manual SoC]", " [Manual SoC Max]", ", Hold for car".
        var withoutBrackets = Regex.Replace(stripped, @"\s*\[(?:Alert|Manual SoC(?: Max)?)\]", "", RegexOptions.IgnoreCase).Trim();
        if (byCode.TryGetValue(Fold(withoutBrackets), out var bracketed)) return bracketed;
        var hold = Regex.Match(withoutBrackets, @"^(?<head>.+?),\s*Hold for (?<what>car|iBoost)$", RegexOptions.IgnoreCase);
        if (hold.Success && byCode.TryGetValue(Fold(hold.Groups["head"].Value), out var held))
            return held.Key == "demand" ? byCode[Fold("Hold for " + hold.Groups["what"].Value)] : held;
        var withoutTarget = Regex.Replace(withoutBrackets, @"\s*\d+(?:\.\d+)?\s*%?$", "").Trim();
        if (withoutTarget != withoutBrackets && byCode.TryGetValue(Fold(withoutTarget), out var targeted)) return targeted;
        var parts = withoutTarget.Split('/', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 2 && byCode.TryGetValue(Fold(parts[0]), out var first) && byCode.TryGetValue(Fold(parts[1]), out var second) && first.Key is not null && second.Key is not null)
        {
            var key = Combine(first.Key, second.Key);
            return Entries.First(x => x.Id == key);
        }
        return null;
    }

    /// <summary>Canonical action key for any code, or "unknown".</summary>
    public static string Key(string? code) => Find(code)?.Key ?? UnknownKey;

    /// <summary>Plain label for any code. Never echoes the raw code: unrecognised codes read "Other Predbat state".</summary>
    public static string Label(string? code) => Find(code)?.Label ?? UnknownLabel;

    public static string Description(string? code) => Find(code) is { } entry ? entry.Description : string.IsNullOrWhiteSpace(code) ? "Predbat didn't say what this slot does." : $"Predbat reported “{code.Trim()}”, which Joule doesn't recognise yet.";

    /// <summary>Combines the two halves of a split slot. A charge followed by an export is "charge-export"; a demand part yields
    /// to the other part; a real export dominates any other charge-side state; otherwise the charge side wins because a freeze
    /// or held export doesn't discharge the battery.</summary>
    public static string Combine(string? first, string second)
    {
        if (first is null || first == second || first == "demand") return second;
        if (second == "demand") return first;
        if (second == "export") return first == "charge" ? "charge-export" : ChargeSide.Contains(first) ? "export" : second;
        if (ExportSide.Contains(second) && ChargeSide.Contains(first)) return first;
        return second;
    }

    const string ShortCode = "FrzChrg|FrzChg|HoldChrg|HoldChg|NoChrg|NoChg|FrzExp|HoldExp|Chrg|Exp";
    static readonly Regex CodeWords = new($@"(?<![A-Za-z0-9_/])(?:{ShortCode})(?:/(?:{ShortCode}))?(?![A-Za-z0-9_/])", RegexOptions.Compiled);
    /// <summary>Safety net for prose: replaces Predbat's short state codes ("FrzExp") with their plain labels, lower-cased
    /// mid-sentence ("the next export solar, don't charge battery slot").</summary>
    public static string ReplaceCodes(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";
        return CodeWords.Replace(text, m =>
        {
            var label = Label(m.Value);
            var before = text[..m.Index].TrimEnd();
            var sentenceStart = before.Length == 0 || before[^1] is '.' or '!' or '?' or ':';
            return sentenceStart ? label : char.ToLowerInvariant(label[0]) + label[1..];
        });
    }

    /// <summary>Renders Predbat's reasons with the plan's own templates (reason_templates), falling back to Joule's wording.</summary>
    public static string? RenderReasons(IReadOnlyList<PlanReason>? reasons, IReadOnlyDictionary<string, string>? templates = null)
    {
        if (reasons is null || reasons.Count == 0) return null;
        var parts = new List<string>();
        foreach (var reason in reasons)
        {
            string? template = null;
            if (templates is not null && templates.TryGetValue(reason.Code, out var predbat)) template = predbat;
            else if (ReasonTemplates.TryGetValue(reason.Code, out var own)) template = own;
            if (template is null) continue;
            var text = Regex.Replace(template, @"\{(\w+)\}", m => reason.Params.TryGetValue(m.Groups[1].Value, out var value) ? value : m.Value);
            if (text.Contains('{')) continue;
            text = text.Trim();
            if (text.Length > 0 && !parts.Contains(text)) parts.Add(text);
        }
        return parts.Count == 0 ? null : string.Join(" ", parts);
    }

    public static string? RateTypeLabel(string? type) => type is not null && RateTypes.TryGetValue(type, out var value) ? value.Label : null;
    public static bool RateEstimated(string? type) => type is not null && RateTypes.TryGetValue(type, out var value) && value.Estimated;

    /// <summary>Plain-language legend for model prompts: canonical key, label, meaning and expected battery behaviour.</summary>
    public static string Legend() => string.Join(" ", Actions.Values.Select(a => $"{a.Key} = “{a.Label}” (Predbat: {a.PredbatName}): {a.Description} Battery: {a.Battery}"));
}
