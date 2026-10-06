using System.Globalization;

namespace Joule;

public record ImpactSlot(DateTimeOffset Time, int DurationMinutes, double BaselineLoadKwh, double ProposedLoadKwh,
    double BaselinePvKwh, double ProposedPvKwh, double ImportRate, double ExportRate);

/// <summary>
/// How a scaling suggestion changes the forecast Predbat plans with. CostDeltaLowerGbp/UpperGbp are always null now: multiplying the
/// forecast change by tariffs assumed the home would really use less, which a forecast setting can't do. Description says the change in
/// words ("Predbat will plan for 0.4 kWh less home use, mostly in the evening"); LoadDeltaKwh/PvDeltaKwh are the totals over the plan.
/// </summary>
public record ImpactPreview(bool Available, string Reason, string Methodology, string? PlanId, DateTimeOffset? PlanAt,
    int ConfigurationRevision, double? BaselineForecastCostGbp, double? CostDeltaLowerGbp,
    double? CostDeltaUpperGbp, List<ImpactSlot> Slots, List<string> Assumptions)
{
    public string? Description { get; init; }
    public double? LoadDeltaKwh { get; init; }
    public double? PvDeltaKwh { get; init; }
    /// <summary>The home's time zone, for the chart's time labels.</summary>
    public string? TimeZone { get; init; }
}

public static class ImpactPreviewService
{
    const string Method = "How the forecast changes, not a Predbat replan. The captured home-use and solar forecasts are multiplied by the proposed/current scaling ratio. This changes what Predbat expects when it plans the battery, not what your home uses, so no money figure is given: the effect on cost depends on the plans Predbat makes next.";

    public static ImpactPreview Build(AppState state, Proposal proposal, PlanSnapshot? plan, DateTimeOffset now, TimeZoneInfo? zone = null)
    {
        zone ??= London();
        ImpactPreview Unavailable(string why) => new(false, why, Method, plan?.Id, plan?.At, state.Revision,
            null, null, null, [], []) { TimeZone = zone.Id };

        if (proposal.BaseRevision != state.Revision || proposal.Changes.Any(c =>
            state.Settings.All(s => s.Key != c.Key || !ChangeEngine.Equal(s.Value, c.Before))))
            return Unavailable("The configuration changed since this recommendation was prepared. Request a fresh investigation.");
        if (proposal.Changes.Count == 0 || proposal.Changes.Any(c => c.Key is not ("load_scaling" or "pv_scaling")))
            return Unavailable("This change affects settings that the forecast preview cannot show. Review the documented hypothesis; no effect has been calculated.");
        if (proposal.Changes.Select(c => c.Key).Distinct().Count() != proposal.Changes.Count)
            return Unavailable("A setting occurs more than once; this recommendation cannot be applied or previewed.");
        foreach (var change in proposal.Changes)
        {
            try { ChangeEngine.Validate(ChangeEngine.Find(state, change.Key), change.After); }
            catch (DomainException) { return Unavailable("A proposed value is not currently editable or valid. Refresh the settings and request a fresh recommendation."); }
        }
        if (plan == null) return Unavailable("A captured Predbat plan is required to calculate a preview.");
        if (state.Revisions.LastOrDefault() is { } revision && plan.At < revision.At)
            return Unavailable("This plan was generated before the current configuration. Wait for a new Predbat plan before estimating this change.");

        var loadRatio = 1d;
        var pvRatio = 1d;
        foreach (var change in proposal.Changes)
        {
            if (!double.TryParse(change.Before, NumberStyles.Float, CultureInfo.InvariantCulture, out var before) ||
                !double.TryParse(change.After, NumberStyles.Float, CultureInfo.InvariantCulture, out var after) ||
                !double.IsFinite(before) || !double.IsFinite(after) || before <= 0 || after < 0)
                return Unavailable("The scaling values cannot produce a valid preview.");
            if (change.Key == "load_scaling") loadRatio = after / before;
            else pvRatio = after / before;
        }

        var future = plan.Slots.Where(s => s.Time >= now).OrderBy(s => s.Time).ToList();
        if (future.Count == 0) return Unavailable("This plan contains no future intervals. Collect a current plan first.");
        for (var i = 0; i < future.Count; i++)
        {
            var s = future[i];
            if (s.DurationMinutes <= 0 || !double.IsFinite(s.LoadForecast) || !double.IsFinite(s.PvForecast) ||
                !double.IsFinite(s.ImportRate) || !double.IsFinite(s.ExportRate) || !double.IsFinite(s.Cost) ||
                s.LoadForecast < 0 || s.PvForecast < 0 ||
                (i > 0 && future[i - 1].Time.AddMinutes(future[i - 1].DurationMinutes) > s.Time))
                return Unavailable("The plan has invalid or overlapping intervals; a reliable preview cannot be calculated.");
        }

        var slots = future.Select(s => new ImpactSlot(s.Time, s.DurationMinutes, s.LoadForecast, s.LoadForecast * loadRatio,
            s.PvForecast, s.PvForecast * pvRatio, s.ImportRate, s.ExportRate)).ToList();
        if (slots.Any(s => !double.IsFinite(s.ProposedLoadKwh) || !double.IsFinite(s.ProposedPvKwh)))
            return Unavailable("The proposed scaling exceeds the supported numeric range.");
        var loadDelta = slots.Sum(s => s.ProposedLoadKwh - s.BaselineLoadKwh);
        var pvDelta = slots.Sum(s => s.ProposedPvKwh - s.BaselinePvKwh);
        var baselineCost = future.Sum(s => s.Cost);
        if (!double.IsFinite(loadDelta) || !double.IsFinite(pvDelta) || !double.IsFinite(baselineCost))
            return Unavailable("The forecast change exceeds the supported numeric range.");
        return new(true, "The forecast change is shown for the rest of the captured plan.", Method, plan.Id, plan.At,
            state.Revision, baselineCost, null, null, slots,
            ["Only future half-hours are included; this is not a monthly projection.",
             "Weather, tariffs and other forecast adjustments are held fixed.",
             "Battery scheduling, efficiency, capacity limits and car charging are not recalculated, so battery levels and charge or export windows are not predicted here.",
             "Predbat's actual next plan may differ. Joule compares the measured cost before and after if you try it."])
        { Description = Describe(slots, loadDelta, pvDelta, zone), LoadDeltaKwh = Math.Round(loadDelta, 2), PvDeltaKwh = Math.Round(pvDelta, 2), TimeZone = zone.Id };
    }

    /// <summary>"Predbat will plan for 0.4 kWh less home use over the next 20 hours, mostly in the evening. This changes what Predbat expects, not what your home uses."</summary>
    public static string Describe(IReadOnlyList<ImpactSlot> slots, double loadDelta, double pvDelta, TimeZoneInfo zone)
    {
        static string PartOfDay(int hour) => hour switch { < 6 => "overnight", < 12 => "in the morning", < 17 => "in the afternoon", _ => "in the evening" };
        string Mostly(Func<ImpactSlot, double> delta)
        {
            var groups = slots.GroupBy(s => PartOfDay(TimeZoneInfo.ConvertTime(s.Time, zone).Hour)).Select(g => (Part: g.Key, Delta: g.Sum(delta))).OrderByDescending(x => Math.Abs(x.Delta)).ToList();
            var total = groups.Sum(p => Math.Abs(p.Delta));
            return groups.Count > 1 && total > 0 && Math.Abs(groups[0].Delta) / total >= 0.5 ? $", mostly {groups[0].Part}" : "";
        }
        var hours = slots.Count == 0 ? 0 : (slots[^1].Time.AddMinutes(slots[^1].DurationMinutes) - slots[0].Time).TotalHours;
        var span = hours >= 1 ? $" over the next {Math.Round(hours):0} hours" : "";
        var parts = new List<string>();
        if (Math.Abs(loadDelta) >= 0.005) parts.Add($"{Math.Abs(loadDelta).ToString("0.0#", CultureInfo.InvariantCulture)} kWh {(loadDelta < 0 ? "less" : "more")} home use{span}{Mostly(s => s.ProposedLoadKwh - s.BaselineLoadKwh)}");
        if (Math.Abs(pvDelta) >= 0.005) parts.Add($"{Math.Abs(pvDelta).ToString("0.0#", CultureInfo.InvariantCulture)} kWh {(pvDelta < 0 ? "less" : "more")} solar{(parts.Count == 0 ? span : "")}{Mostly(s => s.ProposedPvKwh - s.BaselinePvKwh)}");
        if (parts.Count == 0) return "This makes no noticeable difference to what Predbat expects over the rest of the plan.";
        return $"Predbat will plan for {string.Join(" and ", parts)}. This changes what Predbat expects, not what your home uses.";
    }

    static TimeZoneInfo London() { try { return TimeZoneInfo.FindSystemTimeZoneById("Europe/London"); } catch (Exception e) when (e is TimeZoneNotFoundException or InvalidTimeZoneException) { return TimeZoneInfo.Utc; } }
}
