using System.Text.Json;
using System.Text.RegularExpressions;

namespace Joule;

/// <summary>How Joule treats a Predbat setting.</summary>
public static class SettingKind
{
    /// <summary>A plan setting: Joule may suggest it, trial it, undo it and restore it.</summary>
    public const string Tunable = "tunable";
    /// <summary>Manual one-off controls (manual_*, holiday countdown): recorded as timeline events, never restored or trialled.</summary>
    public const string Override = "override";
    /// <summary>Predbat's own software controls (update, auto_update, saverestore, expert mode): events only; a restore could downgrade Predbat.</summary>
    public const string Software = "software";
    /// <summary>Predbat's operating controls and status (mode, read-only, calculating flag): events only.</summary>
    public const string Control = "control";
    /// <summary>Diagnostics, notifications, chat options and Predbat-maintained values: not tracked.</summary>
    public const string Debug = "debug";
    public static readonly string[] All = [Tunable, Override, Software, Control, Debug];
    /// <summary>Not a setting: an edit to one of Predbat's configuration files (apps.yaml), shown as a Changes timeline event.</summary>
    public const string File = "file";
}

public sealed record SettingCatalogueEntry(string Key, string FriendlyName, string Description, string Section, string Kind, string Risk, bool CommonlyTuned, string Unit, string? Default, string Doc, bool ExpertOnly);

/// <summary>
/// The curated catalogue of Predbat settings (Knowledge/predbat-settings.json): plain names, descriptions paraphrased from
/// the cited section of Predbat's docs at the pinned ref, sections, kind, real risk and defaults. Unknown keys fall back to
/// key patterns, so a new manual_* or debug_* control is never mistaken for a tunable setting.
/// </summary>
public static class PredbatSettingsCatalogue
{
    public static IReadOnlyDictionary<string, SettingCatalogueEntry> Entries { get; }
    public static IReadOnlyList<string> Sections { get; }
    public static string DocsRef { get; }
    public static string DocsBase { get; }
    static readonly string[] numberedVariants;
    public const string OtherSection = "Other";

    static PredbatSettingsCatalogue()
    {
        using var json = JsonDocument.Parse(PredbatGlossary.ReadResource("predbat-settings.json"));
        var root = json.RootElement;
        DocsRef = root.GetProperty("docsRef").GetString()!;
        DocsBase = root.GetProperty("docsBase").GetString()!;
        Sections = root.GetProperty("sections").EnumerateArray().Select(x => x.GetString()!).Append(OtherSection).ToList();
        numberedVariants = root.GetProperty("numberedVariants").EnumerateArray().Select(x => x.GetString()!).OrderByDescending(x => x.Length).ToArray();
        Entries = root.GetProperty("settings").EnumerateArray().Select(s => new SettingCatalogueEntry(
            s.GetProperty("key").GetString()!, s.GetProperty("friendlyName").GetString()!, s.GetProperty("description").GetString()!, s.GetProperty("section").GetString()!,
            s.GetProperty("kind").GetString()!, s.GetProperty("risk").GetString()!, s.GetProperty("commonlyTuned").GetBoolean(), s.GetProperty("unit").GetString() ?? "",
            s.GetProperty("default").ValueKind == JsonValueKind.Null ? null : s.GetProperty("default").GetString(), s.GetProperty("doc").GetString() ?? "", s.GetProperty("expertOnly").GetBoolean()))
            .ToDictionary(x => x.Key, StringComparer.Ordinal);
    }

    /// <summary>The catalogue entry for a key, including numbered variants for extra cars and inverters (car_charging_rate_1 → car 2).</summary>
    public static (SettingCatalogueEntry Entry, int? Variant)? Find(string key)
    {
        if (Entries.TryGetValue(key, out var exact)) return (exact, null);
        foreach (var prefix in numberedVariants)
        {
            var match = Regex.Match(key, "^" + Regex.Escape(prefix) + @"_(\d)$");
            if (match.Success && Entries.TryGetValue(prefix, out var baseEntry)) return (baseEntry, int.Parse(match.Groups[1].Value));
        }
        return null;
    }

    static readonly Regex VersionOption = new(@"^(?:v?\d+\.\d+(?:\.\d+)?\b|main$)", RegexOptions.IgnoreCase);
    /// <summary>Classifies any key: the catalogue first, then Predbat's naming patterns, then the select's options.</summary>
    public static string Kind(string key, string type = "number", IReadOnlyCollection<string>? options = null)
    {
        if (Find(key) is { } found) return found.Entry.Kind;
        if (key is "update" or "auto_update" or "saverestore" or "expert_mode") return SettingKind.Software;
        if (key is "mode" or "set_read_only" or "active") return SettingKind.Control;
        if (key.StartsWith("manual_", StringComparison.Ordinal) || key.StartsWith("holiday_days", StringComparison.Ordinal)) return SettingKind.Override;
        if (key.StartsWith("debug_", StringComparison.Ordinal) || key.EndsWith("_notify", StringComparison.Ordinal) || key is "plan_debug" || key.StartsWith("chat_", StringComparison.Ordinal) || key.EndsWith("_today", StringComparison.Ordinal)) return SettingKind.Debug;
        // A select whose options are release versions installs software when chosen.
        if (type == "select" && options is { Count: > 0 } && options.Count(o => VersionOption.IsMatch(o)) * 2 >= options.Count) return SettingKind.Software;
        return SettingKind.Tunable;
    }

    public static bool IsTunable(string key, string type = "number", IReadOnlyCollection<string>? options = null) => Kind(key, type, options) == SettingKind.Tunable;
    public static bool IsTunable(Setting setting) => (string.IsNullOrEmpty(setting.Kind) ? Kind(setting.Key, setting.Type, setting.Options) : setting.Kind) == SettingKind.Tunable;

    public static string DocumentationUrl(string? doc)
    {
        if (string.IsNullOrWhiteSpace(doc)) return DocsBase + "customisation/";
        var parts = doc.Split('#', 2);
        var page = parts[0].EndsWith(".md", StringComparison.Ordinal) ? parts[0][..^3] : parts[0];
        return DocsBase + page + "/" + (parts.Length == 2 ? "#" + parts[1] : "");
    }

    static string VariantSuffix(string key, int? variant) => variant is { } n ? (key.StartsWith("set_reserve_min", StringComparison.Ordinal) ? $" (inverter {n + 1})" : $" (car {n + 1})") : "";

    /// <summary>Fills a discovered setting from the catalogue: plain name, description, section, kind, risk, default, unit and doc
    /// link. Only tunable settings stay editable in Joule; automation is offered only for low-risk numeric tunables.</summary>
    public static Setting Apply(Setting setting)
    {
        var found = Find(setting.Key);
        var jouleName = found is var (match, number) ? match.FriendlyName + VariantSuffix(setting.Key, number) : null;
        // Keep Home Assistant's own name once; on later passes Name is already Joule's (possibly "(car 2)"-suffixed) name.
        if (string.IsNullOrEmpty(setting.PredbatName) && !string.IsNullOrEmpty(setting.Name) && setting.Name != setting.Key && setting.Name != jouleName) setting.PredbatName = setting.Name;
        setting.Kind = Kind(setting.Key, setting.Type, setting.Options);
        if (found is { } f)
        {
            var (entry, variant) = f;
            setting.Name = jouleName!;
            setting.Description = entry.Description;
            setting.Section = entry.Section;
            setting.Risk = entry.Risk;
            setting.CommonlyTuned = entry.CommonlyTuned && variant is null;
            setting.Unit = entry.Unit;
            setting.Default = entry.Default;
            setting.Documentation = DocumentationUrl(entry.Doc);
            setting.DocumentationAnchor = string.IsNullOrEmpty(entry.Doc) ? null : entry.Doc;
            setting.ExpertOnly = entry.ExpertOnly;
        }
        else
        {
            setting.Section = setting.Kind switch { SettingKind.Override => "Manual overrides", SettingKind.Software => "Predbat software", SettingKind.Control => "Predbat control", SettingKind.Debug => "Notifications & debug", _ => OtherSection };
            if (setting.Description.StartsWith("Discovered from Predbat", StringComparison.Ordinal)) setting.Description = "";
            setting.Risk = "High";
            setting.Documentation = DocumentationUrl(null);
        }
        if (PredbatClient.IsDiagnosticSetting(setting.Key)) setting.Description = PredbatClient.ActiveDescription;
        setting.Category = setting.Section;
        if (setting.Kind != SettingKind.Tunable) { setting.Editable = false; setting.AutoAllowed = false; }
        setting.AutoEligible = setting.Kind == SettingKind.Tunable && setting.Editable && setting.Risk == "Low" && setting.Type == "number";
        if (!setting.AutoEligible) setting.AutoAllowed = false;
        return setting;
    }

    /// <summary>The automatic step a setting starts with before the user changes it: 0.1, but never less than one of the
    /// setting's own steps (so a whole-number setting such as calculate_plan_every can move at all) and never more than
    /// <see cref="MaxAutoStep"/>.</summary>
    public static double DefaultAutoStep(Setting setting) => Math.Min(MaxAutoStep(setting), Math.Max(.1, setting.Step > 0 ? setting.Step : 0));

    /// <summary>The largest automatic step allowed for a setting: 10% of its range (at least one step), or 0.1 without a range.</summary>
    public static double MaxAutoStep(Setting setting)
    {
        if (setting.Min is { } min && setting.Max is { } max && max > min && double.IsFinite(max - min))
            return Math.Max(setting.Step > 0 ? setting.Step : 0, Math.Round((max - min) * .1, 10));
        return .1;
    }
}
