namespace Joule;

/// <summary>
/// One plan slot as it was frozen before the slot began, joined with what the meters measured. SocPlannedPercent is Predbat's forecast
/// for the slot START, so compare it with SocActualStartPercent. Energy values include timing-estimated (spread) energy where the meters
/// prove it; EstimatedMetrics lists the metrics whose value is such an estimate. HomeKwh is load minus EV when the load meter includes EV
/// charging, which is what Predbat's load forecast predicts. ActionKey, TargetPercent and ReasonText come from Predbat's plan when the
/// plan store records them (null otherwise).
/// </summary>
public record PlanVsActualSlot(DateTimeOffset Time, int DurationMinutes, string? PlannedAction, double? SocPlannedPercent, double? SocActualStartPercent, double? SocActualEndPercent,
    double? ImportRatePence, double? ExportRatePence, double? LoadForecastKwh, double? LoadKwh, double? PvForecastKwh, double? PvKwh, double? GridImportKwh, double? GridExportKwh,
    double? BatteryChargeKwh, double? BatteryDischargeKwh, double? EvKwh, DateTimeOffset? PlanCapturedAt, int PlanSnapshots, int DistinctPlannedActions)
{
    public double? HomeKwh { get; init; }
    public string[] EstimatedMetrics { get; init; } = [];
    public string? ActionKey { get; init; }
    public double? TargetPercent { get; init; }
    public string? ReasonText { get; init; }
}

/// <summary>
/// Measured energy per local clock hour from mapped cumulative meters, prorated at the hour edges, with the battery level and tariff
/// at the end of the hour. EnergyKwh[metric] is null unless the whole hour is accounted for; Coverage[metric] is the known share.
/// CoverageFraction is the load meter's coverage (kept for older readers). Label is the local hour, with its UTC offset when the clock
/// hour repeats at a DST change.
/// </summary>
public record HourlyMeasured(DateTimeOffset Hour, Dictionary<string, double?> EnergyKwh, double CoverageFraction, double? SocEndPercent, double? ImportRatePence, double? ExportRatePence)
{
    public Dictionary<string, double> Coverage { get; init; } = [];
    public string[] EstimatedMetrics { get; init; } = [];
    public string? Label { get; init; }
}

public partial class DataStore
{
    static DateTimeOffset FloorHalfHour(DateTimeOffset t) => new(t.Year, t.Month, t.Day, t.Hour, t.Minute < 30 ? 0 : 30, 0, t.Offset);

    bool PlanSlotColumn(string column)
    {
        using var c = Command("SELECT count(*) FROM information_schema.columns WHERE table_name='plan_slots' AND column_name=?", column);
        return Convert.ToInt64(c.ExecuteScalar()) > 0;
    }

    /// <summary>
    /// Frozen plan versus measured outcome for every half-hour slot in [from,to): the latest 30-minute plan row captured
    /// before each slot started, measured meter energy allocated to the slot (boundary-prorated; null when a gap leaves it unknown),
    /// and the battery level in force at each slot edge.
    /// </summary>
    public List<PlanVsActualSlot> ReadPlanVsActual(DateTimeOffset from, DateTimeOffset to)
    {
        from = FloorHalfHour(from.ToUniversalTime()); to = FloorHalfHour(to.ToUniversalTime());
        if (to <= from || to - from > TimeSpan.FromDays(3)) throw new DomainException("plan_vs_actual needs an ordered window of at most three days.", 400);
        lock (gate)
        {
            var extras = new[] { "action_key", "target_percent", "reason_text" }.Where(PlanSlotColumn).ToList();
            var extraSelect = string.Concat(new[] { "action_key", "target_percent", "reason_text" }.Select(c => extras.Contains(c) ? "," + c : ",NULL"));
            var frozen = new Dictionary<DateTimeOffset, (string Action, double Soc, double Import, double Export, double Load, double Pv, DateTimeOffset Captured, string? Key, double? Target, string? Reason)>();
            var churn = new Dictionary<DateTimeOffset, (int Snapshots, int Actions)>();
            using (var c = Command($"SELECT time,action,soc_forecast,import_rate,export_rate,load_forecast,pv_forecast,captured_at{extraSelect} FROM (SELECT *,row_number() OVER(PARTITION BY time ORDER BY captured_at DESC,snapshot_id DESC) rn FROM plan_slots WHERE duration_minutes=30 AND captured_at<=time AND time>=? AND time<?) WHERE rn=1", from, to))
            using (var r = c.ExecuteReader())
                while (r.Read()) frozen[Stamp(r.GetValue(0))] = (r.IsDBNull(1) ? "" : r.GetString(1), r.GetDouble(2), r.GetDouble(3), r.GetDouble(4), r.GetDouble(5), r.GetDouble(6), Stamp(r.GetValue(7)),
                    r.IsDBNull(8) ? null : Convert.ToString(r.GetValue(8)), r.IsDBNull(9) ? null : Convert.ToDouble(r.GetValue(9)), r.IsDBNull(10) ? null : Convert.ToString(r.GetValue(10)));
            using (var c = Command("SELECT time,count(*),count(DISTINCT action) FROM plan_slots WHERE duration_minutes=30 AND captured_at<=time AND time>=? AND time<? GROUP BY time", from, to))
            using (var r = c.ExecuteReader())
                while (r.Read()) churn[Stamp(r.GetValue(0))] = ((int)r.GetInt64(1), (int)r.GetInt64(2));
            var soc = SamplesInternal(from.AddMinutes(-10), to.AddMinutes(10), "soc", 0, int.MaxValue);
            var intervals = LoadIntervals(from, to);
            var byMetric = TelemetrySchema.EnergyMetrics.ToDictionary(m => m, m => intervals.Where(x => x.Metric == m).ToList());
            var includesEv = byMetric["ev"].Count > 0 ? LoadIncludesEv() : false;
            var now = Clock.GetUtcNow();
            var result = new List<PlanVsActualSlot>();
            for (var t = from; t < to; t = t.AddMinutes(30))
            {
                var end = t.AddMinutes(30); var elapsed = end <= now;
                var estimated = new List<string>();
                double? Energy(string metric)
                {
                    if (!elapsed) return null;
                    var a = Allocate(byMetric[metric], t, end);
                    if (a.Status == "estimated") estimated.Add(metric);
                    return a.Value;
                }
                var plan = frozen.TryGetValue(t, out var p) ? p : default;
                var has = frozen.ContainsKey(t); var c = churn.TryGetValue(t, out var ch) ? ch : (0, 0);
                var load = Energy("load"); var ev = Energy("ev");
                result.Add(new(t, 30, has ? plan.Action : null, has ? plan.Soc : null, SocAt(soc, t), elapsed ? SocAt(soc, end) : null, has ? plan.Import : null, has ? plan.Export : null,
                    has ? plan.Load : null, load, has ? plan.Pv : null, Energy("pv"), Energy("grid_import"), Energy("grid_export"), Energy("battery_charge"), Energy("battery_discharge"), ev,
                    has ? plan.Captured : null, c.Item1, c.Item2)
                {
                    HomeKwh = includesEv == true && load is { } l && ev is { } e ? Math.Max(0, l - e) : null,
                    EstimatedMetrics = estimated.ToArray(),
                    ActionKey = has ? plan.Key : null, TargetPercent = has ? plan.Target : null, ReasonText = has ? plan.Reason : null
                });
            }
            return result;
        }
    }

    /// <summary>Energy per local hour per mapped meter (prorated at the hour edges), with per-metric coverage, battery level and tariffs at the hour end.</summary>
    public List<HourlyMeasured> ReadHourlyMeasured(DateTimeOffset from, DateTimeOffset to)
    {
        from = from.ToUniversalTime(); to = to.ToUniversalTime();
        var zone = telemetryZone;
        var localStart = TimeZoneInfo.ConvertTime(from, zone);
        from = CivilTime.FirstValidInstant(new DateTime(localStart.Year, localStart.Month, localStart.Day, localStart.Hour, 0, 0), zone);
        if (from > to) from = from.AddHours(-1);
        if (to <= from || to - from > TimeSpan.FromDays(14)) throw new DomainException("Hourly measurements need an ordered window of at most fourteen days.", 400);
        lock (gate)
        {
            var intervals = LoadIntervals(from, to);
            var byMetric = TelemetrySchema.EnergyMetrics.ToDictionary(m => m, m => intervals.Where(x => x.Metric == m).ToList());
            var includesEv = byMetric["ev"].Count > 0 ? LoadIncludesEv() : false;
            var soc = SamplesInternal(from.AddMinutes(-10), to.AddMinutes(10), "soc", 0, int.MaxValue);
            var rates = new Dictionary<string, List<TelemetrySample>>();
            foreach (var m in new[] { "import_tariff", "export_tariff" }) rates[m] = SamplesInternal(from.AddHours(-1), to, m, 0, int.MaxValue).Where(x => x.Status == "observed" && x.Value is not null).ToList();
            double? RateAt(string metric, DateTimeOffset at) => rates[metric].LastOrDefault(x => x.Time < at && x.Time >= at.AddHours(-1))?.Value;
            var result = new List<HourlyMeasured>();
            // Step in UTC hours; a local hour always starts on a UTC hour boundary for whole-hour offsets, and on the same minute otherwise.
            for (var hour = from; hour < to; hour = hour.AddHours(1))
            {
                var end = hour.AddHours(1);
                var energy = new Dictionary<string, double?>(); var coverage = new Dictionary<string, double>(); var estimated = new List<string>();
                foreach (var metric in TelemetrySchema.EnergyMetrics)
                {
                    var a = Allocate(byMetric[metric], hour, end);
                    energy[metric] = a.Value; coverage[metric] = a.Value is null ? KnownShare(byMetric[metric], hour, end) : 1;
                    if (a.Status == "estimated") estimated.Add(metric);
                }
                energy["home"] = includesEv == true && energy["load"] is { } l && energy["ev"] is { } e ? Math.Max(0, l - e) : null;
                if (coverage.Values.All(x => x <= 0) && SocAt(soc, end) is null) continue;
                var local = TimeZoneInfo.ConvertTime(hour, zone);
                var label = local.ToString("ddd dd MMM HH:mm", System.Globalization.CultureInfo.InvariantCulture);
                if (zone.IsAmbiguousTime(local.DateTime)) label += $" (UTC{(local.Offset < TimeSpan.Zero ? "-" : "+")}{local.Offset:hh\\:mm})";
                result.Add(new(hour, energy, coverage["load"], SocAt(soc, end), RateAt("import_tariff", end), RateAt("export_tariff", end)) { Coverage = coverage, EstimatedMetrics = estimated.ToArray(), Label = label });
            }
            return result;
        }
    }

    /// <summary>Share of [from,to] covered by intervals whose energy is known.</summary>
    static double KnownShare(List<StoredInterval> intervals, DateTimeOffset from, DateTimeOffset to)
    {
        double seconds = 0;
        foreach (var x in intervals)
        {
            if (x.End <= from || x.Start >= to || x.Energy is null) continue;
            var l = x.Start > from ? x.Start : from; var r = x.End < to ? x.End : to; seconds += (r - l).TotalSeconds;
        }
        return Math.Min(1, seconds / (to - from).TotalSeconds);
    }

    /// <summary>What changed since the previous investigation: new plan snapshots, configuration revisions and measurements.</summary>
    public object ReadChangesSince(DateTimeOffset? since)
    {
        var start = since ?? DateTimeOffset.MinValue.AddYears(1);
        lock (gate)
        {
            object Scalar(string sql) { using var c = Command(sql, start); return c.ExecuteScalar() ?? 0; }
            return new
            {
                since,
                newPlanSnapshots = Scalar("SELECT count(*) FROM plans WHERE recorded_at>?"),
                latestPlanGeneratedAt = Scalar("SELECT max(recorded_at) FROM plans WHERE recorded_at>?"),
                newConfigurationRevisions = Scalar("SELECT count(*) FROM revisions WHERE recorded_at>?"),
                newTelemetrySamples = Scalar("SELECT count(*) FROM telemetry_samples WHERE time>?"),
                newObservedIntervals = Scalar("SELECT count(*) FROM telemetry_intervals WHERE status='observed' AND end_time>?")
            };
        }
    }
}
