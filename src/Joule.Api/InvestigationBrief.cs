using System.Globalization;
using System.Text;

namespace Joule;

/// <summary>
/// Predbat's battery economics from its own settings: battery_loss (charge), battery_loss_discharge, inverter_loss and
/// metric_battery_cycle (wear cost, p/kWh). Exporting stored energy is worth it when what a kWh earns after losses and wear beats
/// what it cost to put in: export × (1 − discharge loss)(1 − inverter loss) − wear &gt; import ÷ ((1 − charge loss)(1 − inverter loss)).
/// </summary>
public sealed record ArbitrageParameters(double BatteryLoss, double BatteryLossDischarge, double InverterLoss, double CycleCostPence, double MinExportGainPence)
{
    public static readonly ArbitrageParameters Default = new(0.04, 0.04, 0.04, 0, 0);

    public static ArbitrageParameters From(IEnumerable<Setting> settings)
    {
        var list = settings as IReadOnlyCollection<Setting> ?? settings.ToList();
        double Value(string key, double fallback, double min, double max) =>
            list.FirstOrDefault(s => s.Key == key) is { } s && double.TryParse(s.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && double.IsFinite(v) && v >= min && v <= max ? v : fallback;
        var charge = Value("battery_loss", Default.BatteryLoss, 0, 0.5);
        return new(charge, Value("battery_loss_discharge", charge, 0, 0.5), Value("inverter_loss", Default.InverterLoss, 0, 0.5),
            Value("metric_battery_cycle", 0, 0, 100), Value("metric_min_improvement_export", 0, -100, 100));
    }

    /// <summary>Share of an imported kWh that ends up stored.</summary>
    public double ChargeEfficiency => (1 - BatteryLoss) * (1 - InverterLoss);
    /// <summary>Share of a stored kWh that reaches the grid.</summary>
    public double DischargeEfficiency => (1 - BatteryLossDischarge) * (1 - InverterLoss);

    /// <summary>Pence gained per kWh exported from the battery, against re-buying it at the import rate. Positive means the export pays.</summary>
    public double MarginPence(double importPence, double exportPence) =>
        exportPence * DischargeEfficiency - CycleCostPence - importPence / ChargeEfficiency;

    public bool Profitable(double importPence, double exportPence) => MarginPence(importPence, exportPence) > Math.Max(0, MinExportGainPence);
}

/// <summary>How the household wants Predbat judged. Goes into every brief so a preference doesn't depend on a free-text memory note.</summary>
public static class HouseholdObjective
{
    public const string MaxSavings = "max_savings", LimitCycling = "limit_cycling", SelfSufficiency = "self_sufficiency";
    public static readonly string[] All = [MaxSavings, LimitCycling, SelfSufficiency];
    public static string Describe(string? objective) => objective switch
    {
        LimitCycling => "Limit battery wear: prefer fewer charge/discharge cycles even if it costs a little more; flag heavy cycling that earns under about 5p.",
        SelfSufficiency => "Self-sufficiency: use as little grid energy as possible; exporting stored energy to the grid is not a goal in itself.",
        _ => "Lowest net cost (default): every penny counts, so buying cheap energy at night and exporting it at a higher price is intended, not a fault.",
    };
    public static string Label(string? objective) => objective switch { LimitCycling => "Limit battery wear", SelfSufficiency => "Use as little grid as possible", _ => "Save the most money" };
}

/// <summary>Plain-English, glossary-labelled pieces of the AI brief. Pure functions so their wording and maths are unit-tested.</summary>
public static class InvestigationBrief
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static string Local(DateTimeOffset t, TimeZoneInfo zone) => TimeZoneInfo.ConvertTime(t, zone).ToString("ddd dd MMM HH:mm", Inv);
    public static string Clock(DateTimeOffset t, TimeZoneInfo zone) => TimeZoneInfo.ConvertTime(t, zone).ToString("HH:mm", Inv);
    static string Kwh(double? v) => v is null ? "-" : v.Value.ToString("0.00", Inv);
    static string Pct(double? v) => v is null ? "-" : Math.Round(v.Value).ToString(Inv) + "%";
    static string Pence(double? v) => v is null ? "-" : v.Value.ToString("0.00", Inv) + "p";
    static string Signed(double v, string format = "0.0") => (v >= 0 ? "+" : "") + v.ToString(format, Inv);
    public static string Money(double gbp) => (gbp < 0 ? "−£" : "£") + Math.Abs(gbp).ToString("0.00", Inv);

    /// <summary>The legend that heads plan-versus-actual rows: what each plan label means for the battery, from the glossary.</summary>
    public static string PlanRowsLegend(ArbitrageParameters economics) =>
        "One row per half-hour in UK time. 'plan' is what Predbat had planned just before the half-hour started, as a plain label with its battery target and Predbat's own reason. " +
        "'battery planned a→b' is Predbat's forecast battery level at the START of the half-hour (a) and at its end (the next half-hour's start, b); 'actual a→b' is what the battery really did. Compare start with start and end with end. " +
        "Then the import/export prices in p/kWh and the measured kWh: home use (forecast/measured), solar (forecast/measured), from grid, to grid, battery charged, battery discharged, car ('-' = not measured). " +
        $"'export pays' is the arbitrage margin for that half-hour in pence per kWh exported from the battery: export price × {economics.DischargeEfficiency.ToString("0.000", Inv)} − wear {economics.CycleCostPence.ToString("0.##", Inv)}p − import price ÷ {economics.ChargeEfficiency.ToString("0.000", Inv)} (from battery_loss {economics.BatteryLoss.ToString("0.###", Inv)}, battery_loss_discharge {economics.BatteryLossDischarge.ToString("0.###", Inv)}, inverter_loss {economics.InverterLoss.ToString("0.###", Inv)}, metric_battery_cycle {economics.CycleCostPence.ToString("0.##", Inv)}). A positive margin means exporting then recharging made money, which Predbat does on purpose; 'earned' is that margin times the kWh actually exported. " +
        "Finally how many plan snapshots covered the half-hour and how many different plans they made (several means Predbat changed its mind). " +
        "What each plan label means: " + string.Join(" ", PredbatGlossary.Actions.Values.Select(a => $"“{a.Label}”: {a.Description} Battery: {a.Battery}"));

    /// <summary>Plan-versus-actual rows with glossary labels instead of Predbat codes, target and reason, planned and actual battery start→end.</summary>
    public static List<string> PlanRows(IReadOnlyList<PlanVsActualSlot> slots, TimeZoneInfo zone, ArbitrageParameters economics)
    {
        var rows = new List<string>(slots.Count);
        for (var i = 0; i < slots.Count; i++)
        {
            var x = slots[i];
            var next = i + 1 < slots.Count && slots[i + 1].Time == x.Time.AddMinutes(x.DurationMinutes) ? slots[i + 1] : null;
            var code = x.ActionKey ?? x.PlannedAction;
            var label = code is null ? "no plan captured" : PredbatGlossary.Label(code);
            var text = new StringBuilder(Local(x.Time, zone)).Append(" | plan: ").Append(label);
            if (x.TargetPercent is { } target) text.Append(" (target ").Append(Pct(target)).Append(')');
            if (!string.IsNullOrWhiteSpace(x.ReasonText)) text.Append(" — ").Append(x.ReasonText!.Trim());
            text.Append(" | battery planned ").Append(Pct(x.SocPlannedPercent)).Append("→").Append(Pct(next?.SocPlannedPercent))
                .Append(", actual ").Append(Pct(x.SocActualStartPercent)).Append("→").Append(Pct(x.SocActualEndPercent));
            text.Append(" | import ").Append(Pence(x.ImportRatePence)).Append(" export ").Append(Pence(x.ExportRatePence));
            text.Append(" | home ").Append(Kwh(x.LoadForecastKwh)).Append('/').Append(Kwh(x.HomeKwh ?? x.LoadKwh))
                .Append(" solar ").Append(Kwh(x.PvForecastKwh)).Append('/').Append(Kwh(x.PvKwh))
                .Append(" from grid ").Append(Kwh(x.GridImportKwh)).Append(" to grid ").Append(Kwh(x.GridExportKwh))
                .Append(" charged ").Append(Kwh(x.BatteryChargeKwh)).Append(" discharged ").Append(Kwh(x.BatteryDischargeKwh))
                .Append(" car ").Append(Kwh(x.EvKwh));
            if (x.ImportRatePence is { } imp && x.ExportRatePence is { } exp)
            {
                var margin = economics.MarginPence(imp, exp);
                text.Append(" | export pays ").Append(Signed(margin, "0.0")).Append("p/kWh");
                if (x.GridExportKwh is > 0.05 && x.BatteryDischargeKwh is > 0.05)
                    text.Append(", earned ").Append(Signed(margin * Math.Min(x.GridExportKwh.Value, x.BatteryDischargeKwh.Value), "0.0")).Append('p');
            }
            if (x.EstimatedMetrics.Length > 0) text.Append(" | ≈ timing estimated for ").Append(string.Join(", ", x.EstimatedMetrics));
            text.Append(" | plans ").Append(x.PlanSnapshots).Append(", different plans ").Append(x.DistinctPlannedActions);
            rows.Add(text.ToString());
        }
        return rows;
    }

    /// <summary>Per-half-hour arbitrage for slots that exported from the battery: the margin and what it actually earned.</summary>
    public sealed record ArbitrageSlot(DateTimeOffset Time, double ImportPence, double ExportPence, double ExportedKwh, double MarginPencePerKwh, double EarnedPence, bool Profitable);

    public static List<ArbitrageSlot> Arbitrage(IEnumerable<PlanVsActualSlot> slots, ArbitrageParameters economics) =>
        slots.Where(x => x.ImportRatePence is not null && x.ExportRatePence is not null && x.GridExportKwh is > 0.05 && x.BatteryDischargeKwh is > 0.05)
            .Select(x =>
            {
                var exported = Math.Min(x.GridExportKwh!.Value, x.BatteryDischargeKwh!.Value);
                var margin = economics.MarginPence(x.ImportRatePence!.Value, x.ExportRatePence!.Value);
                return new ArbitrageSlot(x.Time, x.ImportRatePence.Value, x.ExportRatePence.Value, exported, margin, margin * exported, economics.Profitable(x.ImportRatePence.Value, x.ExportRatePence.Value));
            }).ToList();

    /// <summary>A short paragraph for the brief: did exporting stored energy pay over this window?</summary>
    public static string ArbitrageSummary(IReadOnlyList<PlanVsActualSlot> slots, ArbitrageParameters economics, TimeZoneInfo zone)
    {
        var exports = Arbitrage(slots, economics);
        if (exports.Count == 0) return "No battery export to the grid in this window.";
        var earned = exports.Sum(x => x.EarnedPence);
        var lossy = exports.Where(x => !x.Profitable).ToList();
        var text = $"Battery exported {exports.Sum(x => x.ExportedKwh).ToString("0.00", Inv)} kWh in {exports.Count} half-hour(s); after losses and wear that earned about {Signed(earned, "0")}p against buying it back at the import price.";
        return lossy.Count == 0
            ? text + " Every export paid (positive margin), so it is intended arbitrage, not a fault, unless it left the battery short before a more expensive period."
            : text + $" {lossy.Count} half-hour(s) exported at a loss: {string.Join(", ", lossy.Select(x => $"{Clock(x.Time, zone)} ({Signed(x.MarginPencePerKwh, "0.0")}p/kWh)"))}. Those are worth reporting.";
    }

    /// <summary>
    /// Pence effect of a set of half-hours: measured grid cost (import × import price − export × export price) minus the cost the frozen
    /// plan expected (Predbat's per-slot cost). Positive = cost more than planned. Null unless at least one slot has both.
    /// </summary>
    public static double? ImpactPence(IEnumerable<PlanVsActualSlot> slots, IReadOnlyDictionary<DateTimeOffset, double> plannedCostGbp)
    {
        double total = 0; var any = false;
        foreach (var x in slots)
        {
            if (x.GridImportKwh is not { } imp || x.GridExportKwh is not { } exp || x.ImportRatePence is not { } ip || x.ExportRatePence is not { } ep) continue;
            if (!plannedCostGbp.TryGetValue(x.Time, out var planned) || !double.IsFinite(planned)) continue;
            total += imp * ip - exp * ep - planned * 100; any = true;
        }
        return any ? Math.Round(total, 1) : null;
    }

    /// <summary>
    /// Today's money in words, led by the headline net cost (import cost − export credit, each with its own coverage, plus the standing
    /// charge when the owner's dashboard includes it, so the AI quotes the same figure the owner sees). The matched-period
    /// figure is labelled as the "both meters reporting" value, so the AI never quotes it as the day's cost.
    /// </summary>
    public static string MoneyBrief(EnergySummary summary, TimeZoneInfo zone, string period)
    {
        string Side(string name, double? gbp, double coverage, bool estimated)
        {
            if (gbp is null) return $"{name} not known";
            var approx = coverage < 0.95 || estimated ? "≈ " : "";
            return $"{name} {approx}{Money(gbp.Value)}" + (coverage < 0.98 ? $" ({Math.Round(coverage * 100)}% of the time measured)" : "");
        }
        var lines = new List<string>();
        var net = summary.NetCostGbp ?? (summary.ImportCostGbp is { } i && summary.ExportCreditGbp is { } e ? i - e : null);
        var approxNet = summary.ImportCostCoverage < 0.95 || summary.ExportCostCoverage < 0.95 || summary.ImportCostEstimated || summary.ExportCostEstimated ? "≈ " : "";
        var sides = $"{Side("Paid for import", summary.ImportCostGbp, summary.ImportCostCoverage, summary.ImportCostEstimated)}; {Side("earned from export", summary.ExportCreditGbp, summary.ExportCostCoverage, summary.ExportCostEstimated)}";
        if (net is not { } n) lines.Add($"{period}: net cost not known. {sides}.");
        else if (summary.StandingChargeIncluded && summary.StandingChargeGbp is { } standing)
        {
            // The dashboard's headline includes the standing charge, so that is the figure the AI quotes; the energy-only figure is secondary.
            var withStanding = summary.NetCostWithStandingChargeGbp ?? n + standing;
            lines.Add($"{period}: net cost {approxNet}{Money(withStanding)} including the standing charge (this is the figure to quote; it matches the owner's dashboard). "
                + $"Energy only, without the standing charge: {approxNet}{Money(n)} (what trials and like-for-like comparisons use; never quote it as the cost). {sides}. {StandingChargeBrief(summary)}");
        }
        else lines.Add($"{period}: net cost {approxNet}{Money(n)} (this is the figure to quote). {sides}. {StandingChargeBrief(summary, n)}");
        foreach (var gap in summary.CostGaps.Take(4))
            lines.Add($"- {(gap.Metric == "grid_export" ? "Export meter" : gap.Metric == "grid_import" ? "Import meter" : gap.Metric)} not priced {Clock(gap.From, zone)}–{Clock(gap.To, zone)} ({gap.Reason.Replace('_', ' ')}).");
        if (summary.ObservedNetCostGbp is { } matched)
            lines.Add($"Only while both meters were reporting with whole readings: {Money(matched)}. Use this only to compare like-for-like periods; never quote it as the day's cost.");
        return string.Join("\n", lines);
    }

    /// <summary>The standing charge for the money section: the amount, the daily rate, where it came from and whether the owner's headline
    /// includes it. When <paramref name="netWithout"/> is given the quoted net cost leaves it out, so the bill with it is stated too.
    /// Experiments and like-for-like comparisons always use the energy-only figure.</summary>
    public static string StandingChargeBrief(EnergySummary summary, double? netWithout = null)
    {
        if (summary.StandingChargeGbp is not { } standing || summary.StandingChargePencePerDay is not { } rate)
            return "Standing charge not known to Joule, so not included.";
        var source = summary.StandingChargeSource switch { "manual" => "the owner's figure", "predbat" => "the figure in Predbat's apps.yaml", _ => "the standing charge sensor" };
        var detail = $"Standing charge for this period {Money(standing)} ({rate.ToString("0.##", CultureInfo.InvariantCulture)}p a day, from {source}{(summary.StandingChargeAssumed ? "; early days assumed the same rate" : "")})";
        var placement = netWithout is { } n
            ? $", not in the net cost above; with it the bill is {Money(n + standing)}. The owner has chosen to leave the standing charge out of the dashboard's headline. "
            : ", included in the net cost above, as on the owner's dashboard. ";
        return detail + placement + "The standing charge is fixed per day: never treat it as something a setting change can save.";
    }

    /// <summary>
    /// What each mapped meter is doing, explained from the stored readings and sensor profile (not from the clock): whether a quiet sensor
    /// is expected, how long a gap lasted and whether Joule could count the energy anyway.
    /// </summary>
    public static string TelemetryExplanations(TelemetryStatus status, TimeZoneInfo zone, IReadOnlyDictionary<string, EnergyMetricSummary>? today = null)
    {
        var lines = new List<string>
        {
            $"Readings come from {(status.HomeAssistantDirect ? "Home Assistant directly" : "Predbat's copy of Home Assistant")}; last collected {(status.LastCollection is { } c ? Local(c, zone) : "never")}."
        };
        if (status.Error != null) lines.Add("Collector message: " + status.Error);
        foreach (var (metric, r) in status.LatestReadings.OrderBy(x => x.Key, StringComparer.Ordinal))
        {
            var name = MeterName(metric);
            var profile = r.Profile ?? status.Profiles.GetValueOrDefault(metric);
            var value = r.Value is { } v ? $"{v.ToString("0.###", Inv)} {r.Unit}" : "no number";
            var line = new StringBuilder($"{name} ({r.EntityId}): {value} at {Clock(r.Time, zone)}, status {r.Status}");
            if (profile != null) line.Append(", behaves like a ").Append(profile.Replace('_', ' '));
            if (r.Status is "observed") { }
            else if (r.Expected) line.Append(". Expected, not a fault: ").Append(r.Reason ?? "this sensor is normally quiet now.");
            else line.Append(". Not expected: ").Append(r.Reason ?? "it should be reporting.").Append(r.LastObservedAt is { } lo ? $" Last good reading {Local(lo, zone)}." : "");
            if (today?.GetValueOrDefault(metric) is { } m && m.Gaps.Count > 0)
            {
                var gaps = m.Gaps.Take(3).Select(g => $"{Clock(g.From, zone)}–{Clock(g.To, zone)} ({GapReason(g.Reason)}{(g.KnownKwh is { } k ? $", the counter proves {k.ToString("0.00", Inv)} kWh flowed" : "")})");
                line.Append(". Today's unaccounted time: ").Append(string.Join("; ", gaps));
            }
            if (today?.GetValueOrDefault(metric) is { State: "idle_zero" }) line.Append(". Today is complete: the quiet time counts as zero, so there is no hole in the totals");
            lines.Add(line.Append('.').ToString().Replace("..", "."));
        }
        foreach (var issue in status.Issues) lines.Add($"Problem: {MeterName(issue.Metric)}: {issue.Message}{(issue.Since is { } s ? $" (since {Local(s, zone)})" : "")}");
        if (status.MissingMappings.Length > 0) lines.Add("Not mapped: " + string.Join(", ", status.MissingMappings.Select(MeterName)));
        return string.Join("\n", lines);
    }

    /// <summary>Whether a daily counter that Predbat reads (solar or export today) is currently unknown or unavailable: the playbook applies.</summary>
    public static bool DailyCounterUnknown(TelemetryStatus status) => status.LatestReadings.Any(x =>
        x.Key is "pv" or "grid_export" or "grid_import" or "load" && x.Value.Status is "idle" or "unavailable"
        && (x.Value.Profile ?? status.Profiles.GetValueOrDefault(x.Key)) is null or "daily_counter" or "solar_daily");

    /// <summary>The standard fix when a daily counter Predbat uses reads unknown or unavailable: a template sensor that never goes unknown.</summary>
    public const string DailyCounterPlaybook = """
        Playbook (daily counter reads unknown or unavailable, e.g. solar overnight or export before the first export of the day): the homeowner usually cannot change the source device, so don't argue about whether 0 is valid. Offer the fix as a fileChange: a Home Assistant template sensor that reports the last number, or 0 when the source is unknown or unavailable, then point Predbat at it in apps.yaml. Template (configuration.yaml or a package):
        template:
          - sensor:
              - name: "Solar today (Predbat)"
                unique_id: solar_today_predbat
                unit_of_measurement: kWh
                device_class: energy
                state_class: total_increasing
                state: "{{ states('sensor.SOURCE') | float(0) }}"
        apps.yaml under pred_bat: pv_today: - sensor.solar_today_predbat (export_today similarly). If the gap is harmless because Joule already counts it as zero, say so once and let the item retire; don't raise it again.
        """;

    public static string MeterName(string metric) => metric switch
    {
        "load" => "Home use meter", "pv" => "Solar meter", "grid_import" => "Grid import meter", "grid_export" => "Grid export meter",
        "battery_charge" => "Battery charge meter", "battery_discharge" => "Battery discharge meter", "ev" => "Car charger meter",
        "soc" => "Battery level", "import_tariff" => "Import price", "export_tariff" => "Export price", _ => metric.Replace('_', ' ')
    };
    static string GapReason(string reason) => reason switch
    {
        "offline" => "offline", "idle" => "said unknown", "not_found" => "sensor missing", "invalid" => "unreadable", "no_samples" => "no readings",
        "reset" => "unexplained counter drop", "source_changed" => "different sensor", _ => reason.Replace('_', ' ')
    };

    /// <summary>Open measurable claims for the brief, so later checks confirm or refute them instead of repeating them.</summary>
    public static string ClaimsBrief(IEnumerable<AiClaim> claims, TimeZoneInfo zone)
    {
        var open = claims.Where(c => c.Status == "open").OrderByDescending(c => c.CreatedAt).Take(10).ToList();
        if (open.Count == 0) return "None open.";
        return "Check each against new evidence. Return confirmClaims or refuteClaims with its id and a one-line reason when the evidence settles it; say so in the summary when an earlier finding is refuted.\n" +
            string.Join("\n", open.Select(c => $"- id {c.Id} ({Local(c.CreatedAt, zone)}): {c.Text}{(string.IsNullOrWhiteSpace(c.Test) ? "" : $" — test: {c.Test}")}"));
    }
}
