using System.Globalization;

namespace Joule;

/// <summary>
/// The honest money and measurement context for an AI suggestion: either a £/month range or an explicit reason it isn't estimated, and
/// for calibration settings the measured quantity over recent comparable windows (which would have exposed a 3.3 kW charge-cap claim
/// that later nights contradicted).
/// </summary>
public static class ProposalEstimates
{
    static readonly HashSet<string> ForecastOnly = ["load_scaling", "load_scaling10", "pv_scaling", "pv_scaling10", "pv_metric10_weight", "battery_rate_max_scaling", "battery_rate_max_scaling_discharge"];

    /// <summary>Joule cannot replan Predbat, so no AI suggestion gets a made-up £ figure: the reason is stated instead.</summary>
    public static string SavingEstimate(Proposal proposal) =>
        proposal.Changes.All(c => ForecastOnly.Contains(c.Key))
            ? "Not estimated: this changes what Predbat assumes when it plans, not what your home uses, so the money effect depends on Predbat's next plans. Joule compares the measured cost before and after if you try it."
            : "Not estimated: Joule can't rerun Predbat's plan with this change. If you try it, Joule compares the measured cost before and after.";

    /// <summary>Measured series for calibration settings over the last <paramref name="days"/> days; null for other settings or no data.</summary>
    public static CalibrationSeries? Calibration(DataStore db, Proposal proposal, AppState s, DateTimeOffset now, TimeZoneInfo zone, int days = 7)
    {
        var change = proposal.Changes.FirstOrDefault(c => c.Key is "battery_rate_max_scaling" or "battery_rate_max_scaling_discharge" or "load_scaling" or "pv_scaling");
        if (change is null) return null;
        var slots = new List<PlanVsActualSlot>();
        try
        {
            for (var from = now.AddDays(-days); from < now; from = from.AddDays(3))
            {
                var to = from.AddDays(3) < now ? from.AddDays(3) : now;
                if (to - from >= TimeSpan.FromMinutes(30)) slots.AddRange(db.ReadPlanVsActual(from, to));
            }
        }
        catch (Exception e) when (e is DomainException or InvalidOperationException) { return null; }
        double? Current(string key) => double.TryParse(s.Settings.FirstOrDefault(x => x.Key == key)?.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
        if (change.Key is "battery_rate_max_scaling" or "battery_rate_max_scaling_discharge")
        {
            var charging = change.Key == "battery_rate_max_scaling";
            var sides = charging ? new[] { "charge" } : new[] { "export" };
            var points = Windows(slots, x => sides.Contains(x.ActionKey ?? x.PlannedAction ?? ""))
                .Select(w => (At: w[0].Time, Kw: w.Select(x => (charging ? x.BatteryChargeKwh : x.BatteryDischargeKwh) is { } kwh ? kwh * 60 / x.DurationMinutes : (double?)null).Max()))
                .Where(p => p.Kw is > 0).Select(p => new CalibrationPoint(p.At, Math.Round(p.Kw!.Value, 2))).ToList();
            if (points.Count == 0) return null;
            return new(charging ? "Highest measured charge rate in each planned charge window" : "Highest measured discharge rate in each planned export window", "kW", Current(change.Key), points,
                $"Each point is the busiest half-hour of one window, from the battery meter. Predbat currently scales its {(charging ? "charge" : "discharge")} rate by {change.Before}; the suggestion is {change.After}.");
        }
        var load = change.Key == "load_scaling";
        var byDay = slots.GroupBy(x => TimeZoneInfo.ConvertTime(x.Time, zone).Date).Select(g =>
        {
            var pairs = g.Select(x => load ? (F: x.LoadForecastKwh, A: x.HomeKwh ?? x.LoadKwh) : (F: x.PvForecastKwh, A: x.PvKwh)).Where(p => p.F is not null && p.A is not null).ToList();
            var forecast = pairs.Sum(p => p.F!.Value); var actual = pairs.Sum(p => p.A!.Value);
            return (Day: g.Min(x => x.Time), Slots: pairs.Count, Ratio: forecast > 0.2 ? actual / forecast : (double?)null);
        }).Where(d => d.Slots >= 24 && d.Ratio is not null).Select(d => new CalibrationPoint(d.Day, Math.Round(d.Ratio!.Value, 2))).ToList();
        if (byDay.Count == 0) return null;
        return new(load ? "Measured home use ÷ Predbat's forecast, per day" : "Measured solar ÷ Predbat's forecast, per day", "× forecast", Current(change.Key), byDay,
            $"1.00 means the forecast was right. Days with under 12 hours of matched readings are left out. Current setting {change.Before}; suggestion {change.After}.");
    }

    /// <summary>Runs of consecutive half-hours that satisfy <paramref name="inWindow"/>.</summary>
    public static List<List<PlanVsActualSlot>> Windows(IEnumerable<PlanVsActualSlot> slots, Func<PlanVsActualSlot, bool> inWindow)
    {
        var result = new List<List<PlanVsActualSlot>>(); List<PlanVsActualSlot>? current = null;
        foreach (var x in slots.OrderBy(x => x.Time))
        {
            if (inWindow(x) && current is { Count: > 0 } && current[^1].Time.AddMinutes(current[^1].DurationMinutes) == x.Time) current.Add(x);
            else if (inWindow(x)) { current = [x]; result.Add(current); }
            else current = null;
        }
        return result;
    }
}
