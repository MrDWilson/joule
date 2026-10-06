namespace Joule;

/// <summary>
/// Measured energy for one fixed clock slot. Load/Pv/Ev/Home are slot-precise measurements only (null where any part is missing or
/// timing-estimated). The *Estimate fields add spread intervals (energy proved by the counter across a short outage), so a chart can
/// draw them as ≈. Status per series: measured, idle (the sensor said unknown and that is a known zero, e.g. solar overnight),
/// estimated, missing, or pending (the slot has not finished).
/// </summary>
public record MeasuredHistorySlot(DateTimeOffset Time, int DurationMinutes, double? Load, double? Pv)
{
    public string LoadStatus { get; init; } = "missing";
    public string PvStatus { get; init; } = "missing";
    public double? LoadEstimate { get; init; }
    public double? PvEstimate { get; init; }
    public double? Ev { get; init; }
    public double? EvEstimate { get; init; }
    public string EvStatus { get; init; } = "missing";
    /// <summary>Home use excluding the car (load − EV, never below zero) when the load meter includes EV charging.</summary>
    public double? Home { get; init; }
    public double? HomeEstimate { get; init; }
    public string HomeStatus { get; init; } = "missing";
}
public record MeasuredHistory(DateTimeOffset From, DateTimeOffset To, int SlotMinutes, List<MeasuredHistorySlot> Slots, string Method)
{
    /// <summary>Whether the load meter includes EV charging (so Home is meaningful); null when unknown.</summary>
    public bool? LoadIncludesEv { get; init; }
}

public partial class DataStore
{
    public static readonly int[] MeasuredHistorySlotMinutes = [5, 10, 15, 30, 60];
    /// <summary>
    /// Measured load, PV, EV and home energy on a fixed grid of whole slots starting at <paramref name="from"/>. Used for period comparisons
    /// (the same clock times a day or a week earlier). Each slot uses the boundary allocation of the plan actuals: contiguous meter
    /// intervals spanning the slot, prorated at the edges.
    /// </summary>
    public MeasuredHistory ReadMeasuredHistory(DateTimeOffset from, DateTimeOffset to, int slotMinutes = 30)
    {
        if (!MeasuredHistorySlotMinutes.Contains(slotMinutes)) throw new DomainException("Choose a slot length of 5, 10, 15, 30 or 60 minutes.", 400);
        if (to <= from || to - from > TimeSpan.FromDays(7)) throw new DomainException("Choose a positive history window of at most seven days.", 400);
        var slot = TimeSpan.FromMinutes(slotMinutes);
        var now = Clock.GetUtcNow();
        lock (gate)
        {
            var intervals = LoadIntervals(from, to, ["load", "pv", "ev"]);
            var load = intervals.Where(x => x.Metric == "load").ToList(); var pv = intervals.Where(x => x.Metric == "pv").ToList(); var ev = intervals.Where(x => x.Metric == "ev").ToList();
            var includesEv = ev.Count > 0 ? LoadIncludesEv() : false;
            var slots = new List<MeasuredHistorySlot>();
            for (var start = from; start + slot <= to; start += slot)
            {
                var end = start + slot;
                if (end > now) { slots.Add(new(start, slotMinutes, null, null) { LoadStatus = "pending", PvStatus = "pending", EvStatus = "pending", HomeStatus = "pending" }); continue; }
                var l = Allocate(load, start, end); var p = Allocate(pv, start, end); var e = Allocate(ev, start, end);
                double? homeEstimate = includesEv == true && l.Value is { } lv && e.Value is { } evv ? Math.Max(0, lv - evv) : null;
                double? homeMeasured = includesEv == true && l.Measured is { } lm && e.Measured is { } em ? Math.Max(0, lm - em) : null;
                var homeStatus = homeEstimate is null ? "missing" : homeMeasured is null ? "estimated" : l.Status == "idle" && e.Status == "idle" ? "idle" : "measured";
                slots.Add(new(start, slotMinutes, l.Measured, p.Measured)
                {
                    LoadStatus = l.Status, PvStatus = p.Status, LoadEstimate = l.Value, PvEstimate = p.Value,
                    Ev = e.Measured, EvEstimate = e.Value, EvStatus = e.Status,
                    Home = homeMeasured, HomeEstimate = homeEstimate, HomeStatus = homeStatus
                });
            }
            return new(from, to, slotMinutes, slots, "Measured meter energy per whole slot: contiguous cumulative-counter intervals covering the slot, prorated at the slot edges assuming uniform use within each meter interval. Load/Pv/Ev/Home are slot-precise; *Estimate also includes spread intervals (energy proved by the counter, timing estimated) and is shown as ≈. Gaps and unfinished slots are null, never zero; idle means the sensor reported unknown where that is a known zero.")
            { LoadIncludesEv = includesEv };
        }
    }
}
