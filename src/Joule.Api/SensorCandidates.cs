using System.Globalization;
using System.Text.Json;

namespace Joule;

/// <summary>A Home Assistant entity that looks like it measures one of Joule's metrics.</summary>
public sealed record SensorCandidate(string Entity, string? Name, string? Unit, string? State);

/// <summary>
/// Suggests Home Assistant sensors for each meter from the entities Predbat already sees (Predbat's /api/state mirrors every
/// Home Assistant entity). Predbat's own apps.yaml comes first (see SetupConfigEndpoints.Detect); these name-and-unit matches
/// fill the gaps. A match is used without asking only when it is clear (<see cref="Clear"/>); otherwise Setup asks.
/// </summary>
public static class SensorCandidates
{
    /// <summary>Joule metric → environment variable name suffix (HomeAssistant__Entities__{Name}).</summary>
    public static readonly IReadOnlyDictionary<string, string> EnvNames = new Dictionary<string, string>
    {
        ["load"] = "Load", ["pv"] = "Pv", ["grid_import"] = "GridImport", ["grid_export"] = "GridExport", ["battery_charge"] = "BatteryCharge",
        ["battery_discharge"] = "BatteryDischarge", ["ev"] = "Ev", ["soc"] = "Soc", ["import_tariff"] = "ImportTariff", ["export_tariff"] = "ExportTariff",
        ["standing_charge"] = "StandingCharge",
    };
    /// <summary>The meters a usable setup needs: without home use there is nothing to compare Predbat's plan against.</summary>
    public static readonly string[] Required = ["load"];

    enum Unit { Energy, Percent, Price, DailyCharge }
    sealed record Rule(Unit Unit, string[] Any, string[] All, string[] Not);
    static readonly string[] NotEnergy = ["forecast", "predict", "cost", "rate", "price", "tariff", "power", "limit", "target"];
    static readonly Dictionary<string, Rule> Rules = new()
    {
        ["load"] = new(Unit.Energy, ["load", "consumption", "house", "home_energy"], [], [.. NotEnergy, "solar", "pv_", "car", "_ev", "ev_", "grid", "export", "import", "battery"]),
        ["pv"] = new(Unit.Energy, ["pv", "solar"], [], [.. NotEnergy, "solcast", "export", "import"]),
        ["grid_import"] = new(Unit.Energy, ["import", "grid_in"], [], [.. NotEnergy, "export"]),
        ["grid_export"] = new(Unit.Energy, ["export", "grid_out"], [], [.. NotEnergy, "import"]),
        ["battery_charge"] = new(Unit.Energy, ["battery", "batt"], ["charge"], [.. NotEnergy, "discharge", "car", "_ev", "ev_"]),
        ["battery_discharge"] = new(Unit.Energy, ["battery", "batt"], ["discharge"], [.. NotEnergy, "car", "_ev", "ev_"]),
        ["ev"] = new(Unit.Energy, ["car", "zappi", "ohme", "wallbox", "myenergi", "charger", "_ev_", "ev_charg", "_ev"], [], [.. NotEnergy, "battery_charge", "solar", "pv_"]),
        ["soc"] = new(Unit.Percent, ["soc", "battery_level", "state_of_charge", "battery_percent"], [], ["target", "reserve", "limit", "max", "min", "car", "_ev", "ev_", "predbat", "forecast", "best"]),
        ["import_tariff"] = new(Unit.Price, ["import", "current_rate", "electricity_current", "unit_rate"], [], ["export", "standing", "forecast", "previous", "next"]),
        ["export_tariff"] = new(Unit.Price, ["export"], [], ["import", "standing", "forecast", "previous", "next"]),
        // The daily standing charge in pounds (Octopus: sensor.octopus_energy_electricity_…_current_standing_charge, unit GBP).
        ["standing_charge"] = new(Unit.DailyCharge, ["standing_charge"], [], ["gas", "previous", "next", "forecast", "export"]),
    };

    /// <summary>True when <paramref name="unit"/> is one Joule can read for <paramref name="metric"/>: energy for the meters, % for the
    /// battery level, a price per kWh for the tariffs and money for the standing charge.</summary>
    public static bool UnitFits(string metric, string? unit) => Rules.TryGetValue(metric, out var rule) && UnitKind(unit) == rule.Unit;

    /// <summary>
    /// A name match clear enough to use without asking: the only sensor that fits, or one that scores well ahead of the next
    /// (a daily counter over its running total scores 3 more). Anything closer is a choice for the person.
    /// </summary>
    public static bool Clear(IReadOnlyList<(int Score, SensorCandidate Candidate)> ranked) =>
        ranked.Count == 1 || ranked.Count > 1 && ranked[0].Score - ranked[1].Score >= 3;

    /// <summary>Up to <paramref name="limit"/> candidates per metric, best first. <paramref name="state"/> is Predbat's /api/state object.</summary>
    public static Dictionary<string, List<SensorCandidate>> Find(JsonElement state, int limit = 3) =>
        Ranked(state).ToDictionary(x => x.Key, x => x.Value.Take(limit).Select(c => c.Candidate).ToList());

    /// <summary>Every candidate per metric with its score, best first.</summary>
    public static Dictionary<string, List<(int Score, SensorCandidate Candidate)>> Ranked(JsonElement state)
    {
        var result = Rules.Keys.ToDictionary(k => k, _ => new List<(int Score, SensorCandidate Candidate)>());
        if (state.ValueKind != JsonValueKind.Object) return result;
        var scored = Rules.Keys.ToDictionary(k => k, _ => new List<(int Score, SensorCandidate Candidate)>());
        foreach (var entity in state.EnumerateObject())
        {
            var id = entity.Name;
            if (!id.StartsWith("sensor.", StringComparison.Ordinal) || id.Contains("predbat", StringComparison.OrdinalIgnoreCase) || entity.Value.ValueKind != JsonValueKind.Object) continue;
            var attributes = entity.Value.TryGetProperty("attributes", out var a) && a.ValueKind == JsonValueKind.Object ? a : default;
            string? Attr(string name) => attributes.ValueKind == JsonValueKind.Object && attributes.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            var unit = Attr("unit_of_measurement");
            var kind = UnitKind(unit);
            if (kind is null) continue;
            var lower = id.ToLowerInvariant();
            var name = Attr("friendly_name");
            var text = $"{lower} {name?.ToLowerInvariant().Replace(' ', '_')}";
            var raw = entity.Value.TryGetProperty("state", out var s) ? s.ValueKind == JsonValueKind.String ? s.GetString() : s.ValueKind == JsonValueKind.Number ? s.GetRawText() : null : null;
            var shown = double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number) ? Math.Round(number, 3).ToString(CultureInfo.InvariantCulture) : null;
            foreach (var (metric, rule) in Rules)
            {
                if (rule.Unit != kind) continue;
                var hits = rule.Any.Count(w => text.Contains(w, StringComparison.Ordinal));
                if (hits == 0 || !rule.All.All(w => text.Contains(w, StringComparison.Ordinal)) || rule.Not.Any(w => lower.Contains(w, StringComparison.Ordinal))) continue;
                var score = hits * 2;
                if (kind == Unit.Energy)
                {
                    if (lower.Contains("today") || lower.Contains("daily")) score += 3;
                    if (Attr("device_class") == "energy") score += 1;
                    if (Attr("state_class") is "total" or "total_increasing") score += 1;
                    if (lower.Contains("week") || lower.Contains("month") || lower.Contains("year") || lower.Contains("yesterday")) score -= 4;
                }
                if (kind == Unit.Percent && Attr("device_class") == "battery") score += 2;
                if (shown is null) score -= 1;
                scored[metric].Add((score, new SensorCandidate(id, name, unit, shown)));
            }
        }
        foreach (var (metric, list) in scored)
            result[metric] = list.OrderByDescending(x => x.Score).ThenBy(x => x.Candidate.Entity, StringComparer.Ordinal).ToList();
        return result;
    }

    static Unit? UnitKind(string? unit)
    {
        if (string.IsNullOrWhiteSpace(unit)) return null;
        var u = unit.Replace(" ", "").ToLowerInvariant();
        if (u is "kwh" or "wh" or "mwh") return Unit.Energy;
        if (u == "%") return Unit.Percent;
        if (u.EndsWith("/kwh", StringComparison.Ordinal)) return Unit.Price;
        if (u is "gbp" or "£" or "p" or "pence" or "gbp/day" or "£/day" or "p/day" or "pence/day") return Unit.DailyCharge;
        return null;
    }
}
