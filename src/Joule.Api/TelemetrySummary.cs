namespace Joule;

/// <summary>A stored telemetry interval as read back for accounting.</summary>
public sealed record StoredInterval(string Metric, DateTimeOffset Start, DateTimeOffset End, double? Energy, string Source, string Status, string Method, double? ImportCost, double? ExportCredit, string? CostMethod, double? MissingKwh)
{
    public double? Cost => Metric == "grid_import" ? ImportCost : Metric == "grid_export" ? ExportCredit : null;
}

/// <summary>Energy allocated to one fixed slot. Status: measured, idle (all expected-idle zero), estimated (part spread or tail) or missing.
/// Exact is true when whole intervals tile the slot (no proration of a moving counter at the edges). Measured is the value when no
/// part is estimated.</summary>
public sealed record SlotAllocation(double? Value, string Status, bool Exact, double? Measured);

public partial class DataStore
{
    const string IntervalColumns = "metric,start_time,end_time,energy_kwh,source,status,import_cost_gbp,export_credit_gbp,method,cost_method,missing_kwh";

    List<StoredInterval> LoadIntervals(DateTimeOffset from, DateTimeOffset to, IReadOnlyCollection<string>? metrics = null)
    {
        var filter = metrics is null ? "" : " AND metric IN (" + string.Join(",", metrics.Select(_ => "?")) + ")";
        var args = new List<object?> { to, from }; if (metrics is not null) args.AddRange(metrics);
        using var c = Command($"SELECT {IntervalColumns} FROM telemetry_intervals WHERE start_time<? AND end_time>?{filter} ORDER BY metric,start_time", args.ToArray());
        using var r = c.ExecuteReader(); var result = new List<StoredInterval>();
        while (r.Read()) result.Add(new(r.GetString(0), Stamp(r.GetValue(1)), Stamp(r.GetValue(2)), r.IsDBNull(3) ? null : r.GetDouble(3), r.GetString(4), r.GetString(5),
            r.IsDBNull(8) ? "" : r.GetString(8), r.IsDBNull(6) ? null : r.GetDouble(6), r.IsDBNull(7) ? null : r.GetDouble(7), r.IsDBNull(9) ? null : r.GetString(9), r.IsDBNull(10) ? null : r.GetDouble(10)));
        return result;
    }

    /// <summary>Allocates energy to [from,to] from intervals sorted by start, assuming uniform energy within each interval.</summary>
    public static SlotAllocation Allocate(IReadOnlyList<StoredInterval> intervals, DateTimeOffset from, DateTimeOffset to)
    {
        int lo = 0, hi = intervals.Count;
        while (lo < hi) { var mid = (lo + hi) / 2; if (intervals[mid].End <= from) lo = mid + 1; else hi = mid; }
        var cursor = from; double total = 0; bool spread = false, idle = true, exact = true, any = false;
        for (var i = lo; i < intervals.Count && intervals[i].Start < to; i++)
        {
            var x = intervals[i];
            if (x.Start > cursor || x.Energy is null) return new(null, "missing", false, null);
            var left = x.Start > from ? x.Start : from; var right = x.End < to ? x.End : to;
            if (right <= left) continue;
            total += x.Energy.Value * ((right - left).TotalSeconds / (x.End - x.Start).TotalSeconds);
            if (!double.IsFinite(total)) return new(null, "missing", false, null);
            if (x.Status == "spread") spread = true;
            if (x.Method != "idle") idle = false;
            // A flat counter is flat over any part of it, so prorating it is exact.
            if ((x.Start < from || x.End > to) && x.Energy > FlatTolerance) exact = false;
            cursor = right; any = true;
        }
        if (!any || cursor != to) return new(null, "missing", false, null);
        return new(total, spread ? "estimated" : idle ? "idle" : "measured", exact && !spread, spread ? null : total);
    }

    DateTimeOffset? FirstObservationAt()
    {
        if (firstObservationLoaded) return firstObservation;
        var metrics = string.Join(",", TelemetrySchema.EnergyMetrics.Select(_ => "?"));
        using var cmd = Command($"SELECT min(time) FROM telemetry_samples WHERE metric IN ({metrics}) AND status='observed' AND value IS NOT NULL", TelemetrySchema.EnergyMetrics.Cast<object?>().ToArray());
        firstObservation = cmd.ExecuteScalar() is { } value && value is not DBNull ? Stamp(value) : null; firstObservationLoaded = true;
        return firstObservation;
    }

    static double? FiniteTotal(IEnumerable<double> values)
    {
        double total = 0; bool any = false;
        foreach (var value in values) { total += value; if (!double.IsFinite(total)) return null; any = true; }
        return any ? total : null;
    }

    public EnergySummary ReadEnergySummary(DateTimeOffset from, DateTimeOffset to)
    {
        if (to <= from || to - from > TimeSpan.FromDays(3660)) throw new DomainException("Choose a positive evidence period of at most ten years.", 400);
        lock (gate) return Summarise(from, to, detail: true);
    }

    /// <summary>The coverage denominator for a metric: from the first record (or the window start) to the window end, or to the metric's
    /// latest sample while the collector is running, so pre-history and the "now" edge never count as missing.</summary>
    (DateTimeOffset From, DateTimeOffset To) CoverageWindow(string metric, DateTimeOffset from, DateTimeOffset to, DateTimeOffset now)
    {
        var start = FirstObservationAt() is { } first && first > from ? first : from;
        if (start > to) start = to;
        var latest = LatestSample(metric)?.Time;
        if (latest is null) return (start, start);
        DateTimeOffset end;
        if (to <= latest) end = to;
        else
        {
            var gap = telemetrySettings.MaxGap ?? DefaultGap;
            var alive = now - latest.Value <= gap + gap;
            var cutoff = now - gap - gap;
            end = alive ? latest.Value : latest.Value > cutoff ? latest.Value : cutoff < to ? cutoff : to;
        }
        return (start, end < start ? start : end);
    }

    EnergySummary Summarise(DateTimeOffset from, DateTimeOffset to, bool detail)
    {
        var now = Clock.GetUtcNow();
        var all = LoadIntervals(from, to);
        var byMetric = TelemetrySchema.EnergyMetrics.ToDictionary(m => m, m => all.Where(x => x.Metric == m).ToList());
        bool metricOverflowed = false;
        var metrics = new Dictionary<string, EnergyMetricSummary>();
        var windows = new Dictionary<string, (DateTimeOffset From, DateTimeOffset To)>();
        foreach (var metric in TelemetrySchema.EnergyMetrics)
        {
            var window = windows[metric] = CoverageWindow(metric, from, to, now);
            var (summary, overflowed) = MetricSummary(metric, byMetric[metric], from, to, window, detail);
            metrics[metric] = summary; metricOverflowed |= overflowed;
        }

        // Gross cost per side: every priced interval, prorated at the window edges.
        double? SideTotal(string metric, out double pricedSeconds, out bool estimated, out double estimatedCost, out double unpricedKwh, List<CostGap> gaps)
        {
            var costs = new List<double>(); pricedSeconds = 0; estimated = false; estimatedCost = 0; unpricedKwh = 0;
            var (cs, ce) = windows[metric];
            foreach (var x in byMetric[metric])
            {
                var left = x.Start > from ? x.Start : from; var right = x.End < to ? x.End : to; if (right <= left) continue;
                var share = (right - left).TotalSeconds / (x.End - x.Start).TotalSeconds;
                if (x.Energy is null) continue;
                if (x.Cost is { } cost)
                {
                    costs.Add(cost * share);
                    var l = left > cs ? left : cs; var r = right < ce ? right : ce; if (r > l) pricedSeconds += (r - l).TotalSeconds;
                    if (x.Status == "spread" || x.CostMethod == "plan_rate") { estimated = true; estimatedCost += Math.Abs(cost * share); }
                }
                else { unpricedKwh += x.Energy.Value * share; gaps.Add(new(metric, left, right, "unpriced")); }
            }
            foreach (var gap in metrics[metric].Gaps) gaps.Add(new(metric, gap.From, gap.To, gap.Reason));
            return FiniteTotal(costs);
        }
        var costGaps = new List<CostGap>();
        var importTotal = SideTotal("grid_import", out var importSeconds, out var importEstimated, out var importEstimatedCost, out var importUnpriced, costGaps);
        var exportTotal = SideTotal("grid_export", out var exportSeconds, out var exportEstimated, out var exportEstimatedCost, out var exportUnpriced, costGaps);
        double Fraction(string metric, double seconds) { var (a, b) = windows[metric]; var d = (b - a).TotalSeconds; return d > 0 ? Math.Min(1, seconds / d) : 0; }
        var importHas = byMetric["grid_import"].Any(x => x.Cost is not null); var exportHas = byMetric["grid_export"].Any(x => x.Cost is not null);
        var grossOverflowed = importHas && importTotal is null || exportHas && exportTotal is null;
        double? net = null;
        if (!grossOverflowed && (importTotal is not null || exportTotal is not null))
        {
            var difference = (importTotal ?? 0) - (exportTotal ?? 0);
            net = double.IsFinite(difference) ? difference : null;
        }
        var gridUnknown = metrics["grid_import"].MissingKwh is null || metrics["grid_export"].MissingKwh is null;
        var unpricedGrid = importUnpriced + exportUnpriced + (metrics["grid_import"].MissingKwh ?? 0) + (metrics["grid_export"].MissingKwh ?? 0);

        // Matched figure: only the stretches both meters cover with measured (not spread) priced intervals. Each meter reads at its own
        // update times, so the stretches are the pieces between either meter's boundaries, with energy prorated within an interval.
        var imports = byMetric["grid_import"].Where(x => x.Status == "observed" && x.ImportCost is not null).ToList();
        var exports = byMetric["grid_export"].Where(x => x.Status == "observed" && x.ExportCredit is not null).ToList();
        var costOverflowed = grossOverflowed;
        double costSeconds = 0; double matched = 0; bool anyMatched = false;
        {
            var cuts = imports.SelectMany(x => new[] { x.Start, x.End }).Concat(exports.SelectMany(x => new[] { x.Start, x.End })).Append(from).Append(to)
                .Where(t => t >= from && t <= to).Distinct().Order().ToList();
            int i = 0, e = 0;
            for (var k = 1; k < cuts.Count && !costOverflowed; k++)
            {
                var left = cuts[k - 1]; var right = cuts[k];
                while (i < imports.Count && imports[i].End <= left) i++;
                while (e < exports.Count && exports[e].End <= left) e++;
                if (i >= imports.Count || e >= exports.Count || imports[i].Start > left || exports[e].Start > left || imports[i].End < right || exports[e].End < right) continue;
                double Part(StoredInterval x, double cost) => cost * ((right - left).TotalSeconds / (x.End - x.Start).TotalSeconds);
                var difference = Part(imports[i], imports[i].ImportCost!.Value) - Part(exports[e], exports[e].ExportCredit!.Value); var next = matched + difference;
                if (!double.IsFinite(difference) || !double.IsFinite(next)) { costOverflowed = true; break; }
                matched = next; costSeconds += (right - left).TotalSeconds; anyMatched = true;
            }
        }
        if (costOverflowed) { costSeconds = 0; anyMatched = false; }
        if (grossOverflowed) { net = null; importSeconds = 0; exportSeconds = 0; }
        var seconds = (to - from).TotalSeconds;

        EnergyMetricSummary? home = null; bool? includesEv = null;
        if (detail)
        {
            includesEv = LoadIncludesEv();
            var load = metrics["load"]; var ev = metrics["ev"];
            if (includesEv == true && load.EnergyKwh is { } l && ev.EnergyKwh is { } e && ev.CoverageFraction >= .99)
                home = load with { EnergyKwh = Math.Max(0, l - e), Profile = "derived", CounterDayTotalKwh = null, Reconciled = null };
        }
        var limitations = new List<string>
        {
            "Totals include measured intervals and 'spread' intervals (energy proved by the meter's counter across a short outage, timing estimated); intervals straddling the period edges are prorated. Missing coverage is not zero.",
            "netCostGbp is import cost minus export credit, each with its own coverage. observedNetCostGbp counts only periods both meters cover with whole measured intervals. Neither includes the standing charge: standingChargeGbp is that, prorated over the window, and netCostWithStandingChargeGbp adds it.",
            "Tariffs are applied as recorded by Home Assistant, split at rate changes; Predbat's plan rates fill periods when the tariff sensor was unavailable.",
            "Aggregate sensors cannot identify physical battery-to-EV flow or causal savings.",
            "Home Assistant 'unavailable' means a device is offline. 'unknown' is stored as idle and counts as zero only where the sensor's profile expects it (solar overnight, a charger between sessions, a daily counter before its first reading)."
        };
        if (costOverflowed || metricOverflowed) limitations.Add("Invalid totals are unavailable and cannot claim usable period coverage; source intervals remain retained.");
        var standing = detail ? StandingChargeFor(from, to) : null;
        var standingPrefs = detail ? ReadStandingChargePreferences() : null;
        return new EnergySummary(from, to, metrics, grossOverflowed && importHas ? null : importTotal, grossOverflowed && exportHas ? null : exportTotal, anyMatched ? matched : null, Math.Min(1, costSeconds / seconds), costSeconds,
            all.Select(x => x.Source).Distinct().ToArray(), limitations.ToArray())
        {
            NetCostGbp = net,
            ImportCostCoverage = Fraction("grid_import", importSeconds), ExportCostCoverage = Fraction("grid_export", exportSeconds),
            ImportCostEstimated = importEstimated, ExportCostEstimated = exportEstimated,
            EstimatedCostGbp = importEstimatedCost + exportEstimatedCost,
            UnpricedGridKwh = unpricedGrid, GridEnergyUnknown = gridUnknown,
            CostGaps = MergeCostGaps(costGaps),
            Home = home, LoadIncludesEv = includesEv,
            StandingChargeGbp = standing?.Gbp, StandingChargePencePerDay = standing?.PencePerDay, StandingChargeSource = standing?.Source, StandingChargeAssumed = standing?.Assumed ?? false,
            StandingChargeIncluded = standing is not null && standingPrefs?.IncludeInNet != false,
            NetCostWithStandingChargeGbp = net is { } n && standing is { } sc ? n + sc.Gbp : null
        };
    }

    static List<CostGap> MergeCostGaps(List<CostGap> gaps)
    {
        var result = new List<CostGap>();
        foreach (var g in gaps.OrderBy(x => x.Metric, StringComparer.Ordinal).ThenBy(x => x.From))
        {
            if (result.Count > 0 && result[^1] is var last && last.Metric == g.Metric && last.Reason == g.Reason && g.From <= last.To) { result[^1] = last with { To = g.To > last.To ? g.To : last.To }; continue; }
            result.Add(g);
        }
        return result;
    }

    (EnergyMetricSummary Summary, bool Overflowed) MetricSummary(string metric, List<StoredInterval> intervals, DateTimeOffset from, DateTimeOffset to, (DateTimeOffset From, DateTimeOffset To) window, bool detail)
    {
        var (cs, ce) = window; var denominator = (ce - cs).TotalSeconds;
        var energies = new List<double>(); double covered = 0, observedSeconds = 0, measured = 0, idle = 0, estimatedSeconds = 0; var estimatedKwh = new List<double>();
        var gaps = new List<EnergyGap>(); var cursor = cs;
        foreach (var x in intervals)
        {
            var left = x.Start > from ? x.Start : from; var right = x.End < to ? x.End : to; if (right <= left) continue;
            var share = (right - left).TotalSeconds / (x.End - x.Start).TotalSeconds;
            var cl = x.Start > cs ? x.Start : cs; var cr = x.End < ce ? x.End : ce; var inCoverage = cr > cl ? (cr - cl).TotalSeconds : 0;
            if (inCoverage > 0 && cl > cursor) gaps.Add(new(cursor, cl, "no_samples"));
            if (inCoverage > 0 && cr > cursor) cursor = cr;
            if (x.Energy is { } energy)
            {
                energies.Add(energy * share); observedSeconds += (right - left).TotalSeconds; covered += inCoverage;
                if (x.Status == "spread") { estimatedSeconds += inCoverage; estimatedKwh.Add(energy * share); }
                else { measured += inCoverage; if (x.Method == "idle") idle += inCoverage; }
            }
            else if (inCoverage > 0) gaps.Add(new(cl, cr, x.Method == "" ? x.Status : x.Method, x.MissingKwh is { } missing ? missing * (cr - cl).TotalSeconds / (x.End - x.Start).TotalSeconds : null));
        }
        if (denominator > 0 && cursor < ce) gaps.Add(new(cursor, ce, "no_samples"));
        // Merge touching gaps that share a reason.
        var merged = new List<EnergyGap>();
        foreach (var g in gaps)
        {
            if (merged.Count > 0 && merged[^1] is var last && last.Reason == g.Reason && last.To >= g.From) { merged[^1] = last with { To = g.To > last.To ? g.To : last.To, KnownKwh = last.KnownKwh is { } a && g.KnownKwh is { } b ? a + b : null }; continue; }
            merged.Add(g);
        }
        var total = FiniteTotal(energies); var overflowed = false;
        if (energies.Count > 0 && total is null) { covered = 0; observedSeconds = 0; measured = 0; overflowed = true; }
        var coverage = denominator > 0 ? Math.Min(1, covered / denominator) : 0;
        double? missingKwh = merged.Count == 0 ? 0 : merged.All(g => g.KnownKwh is not null) ? merged.Sum(g => g.KnownKwh!.Value) : null;
        var profile = ProfileFor(metric);
        double? counter = null; bool? reconciled = null;
        if (detail && SensorProfiles.IsDaily(profile) && CounterDayTotal(metric, from, to) is { } c)
        {
            // Compare like with like: the meter's last reading against the energy counted up to that reading. The estimated tail
            // between the last reading and midnight is in the day total but not in the meter's own figure.
            counter = c.Value;
            var upToReading = FiniteTotal(intervals.Where(x => x.Energy is not null).Select(x =>
            {
                var l = x.Start > from ? x.Start : from; var r = x.End < c.At ? x.End : c.At;
                return r > l ? x.Energy!.Value * (r - l).TotalSeconds / (x.End - x.Start).TotalSeconds : 0;
            }));
            reconciled = upToReading is { } u && Math.Abs(u - c.Value) < .05;
        }
        var estimated = FiniteTotal(estimatedKwh);
        string state;
        if (denominator <= 0) state = LatestSample(metric) is null ? "no_records" : "missing";
        else if (denominator - covered <= Math.Max(60, denominator * .005))
            state = estimated is { } e && e >= Math.Max(.05, Math.Abs(total ?? 0) * .01) ? "estimated" : idle >= 900 ? "idle_zero" : "complete";
        else state = covered <= 0 ? "missing" : "partial";
        return (new EnergyMetricSummary(total, observedSeconds, coverage, merged.Count)
        {
            MeasuredSeconds = measured, IdleSeconds = idle, EstimatedSeconds = estimatedSeconds, EstimatedKwh = estimated,
            MissingKwh = missingKwh, Gaps = merged, CounterDayTotalKwh = counter, Reconciled = reconciled, Profile = profile, State = state,
            CoverageFrom = cs, CoverageTo = ce
        }, overflowed);
    }

    /// <summary>For a window starting at local midnight: the daily counter's last reading inside it that carries that day's reset, and when it was read.</summary>
    (double Value, DateTimeOffset At)? CounterDayTotal(string metric, DateTimeOffset from, DateTimeOffset to)
    {
        var local = TimeZoneInfo.ConvertTime(from, telemetryZone);
        if (CivilTime.FirstValidInstant(local.Date, telemetryZone) != from) return null;
        using var c = Command("SELECT * FROM telemetry_samples WHERE metric=? AND time>=? AND time<=? AND status='observed' AND value IS NOT NULL ORDER BY time DESC LIMIT 12", metric, from, to);
        using var r = c.ExecuteReader();
        while (r.Read())
        {
            var s = ReadSample(r);
            if (Attributes.LastReset(s.AttributesJson) is { } reset && Math.Abs((reset - from).TotalMinutes) <= SensorProfiles.MidnightTolerance.TotalMinutes) return (s.Value!.Value, s.Time);
        }
        return null;
    }

    /// <summary>Whether the load meter includes EV charging: configured, or detected when at least 90% of half-hours with ≥ 0.5 kWh of
    /// EV charging (at least three in the last seven days) show load ≥ 80% of the EV energy. Null when there is not enough evidence.</summary>
    public bool? LoadIncludesEv()
    {
        lock (gate)
        {
            switch (telemetrySettings.LoadIncludesEv.ToLowerInvariant()) { case "true" or "on" or "yes": return true; case "false" or "off" or "no": return false; }
            if (loadIncludesEv is { } cached && Clock.GetUtcNow() - cached.At < TimeSpan.FromHours(1)) return cached.Value;
            bool? result = null;
            if (LatestSample("ev") is { } latestEv && LatestSample("load") is not null)
            {
                var end = latestEv.Time; var start = end.AddDays(-7);
                var intervals = LoadIntervals(start, end, ["load", "ev"]);
                var load = intervals.Where(x => x.Metric == "load").ToList(); var ev = intervals.Where(x => x.Metric == "ev").ToList();
                int sessions = 0, inside = 0;
                var slot = new DateTimeOffset(start.Year, start.Month, start.Day, start.Hour, 0, 0, TimeSpan.Zero);
                for (; slot < end; slot = slot.AddMinutes(30))
                {
                    if (Allocate(ev, slot, slot.AddMinutes(30)).Value is not { } e || e < .5) continue;
                    if (Allocate(load, slot, slot.AddMinutes(30)).Value is not { } l) continue;
                    sessions++; if (l >= .8 * e) inside++;
                }
                if (sessions >= 3) result = inside >= .9 * sessions;
            }
            loadIncludesEv = (result, Clock.GetUtcNow());
            return result;
        }
    }

    public List<EnergySummary> GetDailySummaries(DateTimeOffset from, DateTimeOffset to, string timeZone = "Europe/London")
    {
        if (to <= from || to - from > TimeSpan.FromDays(366)) throw new DomainException("Daily evidence supports positive periods up to 366 days.", 400);
        TimeZoneInfo zone; try { zone = TimeZoneInfo.FindSystemTimeZoneById(timeZone); } catch (TimeZoneNotFoundException) { throw new DomainException("Unknown telemetry timezone.", 400); }
        var day = TimeZoneInfo.ConvertTime(from, zone).Date; var result = new List<EnergySummary>();
        while (true)
        {
            var start = CivilTime.FirstValidInstant(day, zone);
            var end = CivilTime.FirstValidInstant(day.AddDays(1), zone);
            if (start >= to) break;
            if (end > start) result.Add(ReadEnergySummary(start < from ? from : start, end > to ? to : end)); day = day.AddDays(1);
        }
        return result;
    }

    public List<MatchedForecast> ReadMatchedForecastEstimates(string metric, DateTimeOffset from, DateTimeOffset to, DateTimeOffset? forecastNotBefore = null)
    {
        if (metric is not ("load" or "pv") || to <= from) throw new DomainException("Choose load or PV and a positive matching period.", 400);
        lock (gate)
        {
            var forecasts = new List<(DateTimeOffset Start, DateTimeOffset End, double Energy)>();
            using (var c = Command($"SELECT time,duration_minutes,{metric}_forecast FROM (SELECT s.*,row_number() OVER(PARTITION BY s.time ORDER BY s.captured_at DESC,s.snapshot_id DESC) rn FROM plan_slots s JOIN plans p ON p.id=s.snapshot_id WHERE duration_minutes>0 AND captured_at<=time AND time>=? AND time<? AND (? IS NULL OR (captured_at>=? AND p.recorded_at>=?))) WHERE rn=1 ORDER BY time", from.AddDays(-1), to, forecastNotBefore, forecastNotBefore, forecastNotBefore))
            using (var r = c.ExecuteReader()) while (r.Read()) { var start = Stamp(r.GetValue(0)); forecasts.Add((start, start.AddMinutes(r.GetInt32(1)), r.GetDouble(2))); }
            var intervals = new List<(DateTimeOffset Start, DateTimeOffset End, double Actual)>();
            using (var c = Command("SELECT start_time,end_time,energy_kwh FROM telemetry_intervals WHERE metric=? AND start_time>=? AND end_time<=? AND energy_kwh IS NOT NULL AND status='observed' ORDER BY start_time", metric, from, to))
            using (var r = c.ExecuteReader()) while (r.Read()) intervals.Add((Stamp(r.GetValue(0)), Stamp(r.GetValue(1)), r.GetDouble(2)));
            var result = new List<MatchedForecast>();
            foreach (var interval in intervals)
            {
                if (interval.End > Clock.GetUtcNow()) continue;
                var covering = forecasts.Where(f => f.End > interval.Start && f.Start < interval.End).ToList(); var cursor = interval.Start; double energy = 0; bool invalid = false;
                for (var i = 0; i < covering.Count; i++)
                {
                    var f = covering[i]; if (f.Start > cursor || i > 0 && f.Start < cursor) { invalid = true; break; }
                    var end = f.End < interval.End ? f.End : interval.End;
                    energy += f.Energy * ((end - cursor).TotalSeconds / (f.End - f.Start).TotalSeconds); cursor = end;
                    if (cursor == interval.End) { if (i + 1 < covering.Count) invalid = true; break; }
                }
                if (!invalid && cursor == interval.End && double.IsFinite(energy)) result.Add(new(interval.Start, energy, interval.Actual, (interval.End - interval.Start).TotalSeconds, "Whole observed meter interval; forecast assumes uniform energy within each latest eligible pre-slot forecast and allocates by temporal overlap. Actual energy is unchanged."));
            }
            foreach (var legacy in ReadMatchedForecasts(metric, from, to, forecastNotBefore))
                if (!intervals.Any(i => i.End > legacy.Time && i.Start < legacy.Time.AddSeconds(legacy.DurationSeconds)) && !result.Any(r => r.Time.AddSeconds(r.DurationSeconds) > legacy.Time && r.Time < legacy.Time.AddSeconds(legacy.DurationSeconds))) result.Add(legacy);
            return result.OrderBy(x => x.Time).ToList();
        }
    }

    /// <summary>Forecast versus exact slot measurement: whole measured intervals tiling the slot (a flat counter may straddle its edges).
    /// Spread and tail estimates never score forecast accuracy.</summary>
    public List<MatchedForecast> ReadMatchedForecasts(string metric, DateTimeOffset from, DateTimeOffset to, DateTimeOffset? forecastNotBefore = null)
    {
        if (metric is not ("load" or "pv") || to <= from) throw new DomainException("Choose load or PV and a positive matching period.", 400);
        lock (gate)
        {
            var forecasts = new List<(DateTimeOffset Time, double Forecast, bool Demo)>();
            using (var c = Command($"SELECT time,{metric}_forecast,forecast_source FROM (SELECT s.*,p.source forecast_source,row_number() OVER(PARTITION BY s.time ORDER BY s.captured_at DESC,s.snapshot_id DESC) rn FROM plan_slots s JOIN plans p ON p.id=s.snapshot_id WHERE duration_minutes=30 AND captured_at<=time AND time>=? AND time<? AND (? IS NULL OR (captured_at>=? AND p.recorded_at>=?))) WHERE rn=1 ORDER BY time", from, to, forecastNotBefore, forecastNotBefore, forecastNotBefore))
            using (var r = c.ExecuteReader()) while (r.Read()) forecasts.Add((Stamp(r.GetValue(0)), r.GetDouble(1), r.GetString(2) == "Demo"));
            var result = new List<MatchedForecast>();
            if (forecasts.Count == 0) return result;
            var intervals = LoadIntervals(forecasts[0].Time, forecasts[^1].Time.AddMinutes(30), [metric]);
            foreach (var f in forecasts)
            {
                var end = f.Time.AddMinutes(30); if (end > to || end > Clock.GetUtcNow()) continue;
                var allocation = Allocate(intervals, f.Time, end);
                double? actual = allocation.Exact && allocation.Measured is { } m ? m : null;
                if (actual is null && f.Demo)
                {
                    // Embedded actuals are supported only in explicitly labelled demo data.
                    // Legacy live/native-derived values do not establish meter observations.
                    using var c = Command($"SELECT {metric}_actual FROM plan_slots s JOIN plans p ON p.id=s.snapshot_id WHERE p.source='Demo' AND time=? AND duration_minutes=30 AND captured_at>=? AND {metric}_actual IS NOT NULL ORDER BY captured_at DESC LIMIT 1", f.Time, end);
                    if (c.ExecuteScalar() is { } n && n is not DBNull) actual = Convert.ToDouble(n);
                }
                if (actual is double a) result.Add(new(f.Time, f.Forecast, a, 1800));
            }
            return result;
        }
    }

    public PlanEvidence? ReadPlanEvidence(string planId)
    {
        lock (gate)
        {
            var plan = GetPlan(planId); if (plan is null) return null;
            var slots = plan.Slots.Where(s => s.DurationMinutes > 0).Select(s =>
            {
                var end = s.Time.AddMinutes(s.DurationMinutes);
                return new PlanSlotEvidence(s.Time, s.DurationMinutes, Summarise(s.Time, end, detail: false), EstimateBoundaryEnergy("load", s.Time, end), EstimateBoundaryEnergy("pv", s.Time, end), "Estimate assumes uniform energy use within each measured meter interval and allocates only slot-boundary overlaps. Requires complete valid consecutive coverage spanning both boundaries; spread (timing-estimated) intervals are excluded. It is neither authoritative slot actual nor a native Predbat replan.");
            }).ToList(); return new(planId, slots);
        }
    }

    /// <summary>Measured energy for [from,to] from contiguous observed intervals, prorated at the edges; null if any part is missing or estimated.</summary>
    double? EstimateBoundaryEnergy(string metric, DateTimeOffset from, DateTimeOffset to)
    {
        if (to > Clock.GetUtcNow()) return null;
        return Allocate(LoadIntervals(from, to, [metric]), from, to).Measured;
    }

    /// <summary>Elapsed half-hour slots in [from,to): the latest forecast captured before each slot started, joined with measured actuals.</summary>
    public List<PlanSlot> ReadRecentTimeline(DateTimeOffset from, DateTimeOffset to)
    {
        if (to <= from || to - from > TimeSpan.FromDays(7)) throw new DomainException("Choose a positive timeline window of at most seven days.", 400);
        lock (gate)
        {
            var timeline = new PlanSnapshot { Source = "Timeline" };
            using (var c = Command("SELECT time,load_forecast,pv_forecast,soc_forecast,import_rate,export_rate,action,cost,duration_minutes FROM (SELECT *,row_number() OVER(PARTITION BY time ORDER BY captured_at DESC,snapshot_id DESC) rn FROM plan_slots WHERE duration_minutes=30 AND captured_at<=time AND time>=? AND time<?) WHERE rn=1 ORDER BY time", from, to))
            using (var r = c.ExecuteReader())
                while (r.Read()) timeline.Slots.Add(new(Stamp(r.GetValue(0)), r.GetDouble(1), null, r.GetDouble(2), null, r.GetDouble(3), null, r.GetDouble(4), r.GetDouble(5), r.IsDBNull(6) ? "" : r.GetString(6), r.GetDouble(7), r.GetInt32(8)));
            return EnrichPlan(timeline).Slots;
        }
    }

    /// <summary>
    /// The battery level at <paramref name="at"/>. Each reading counts at Home Assistant's update time (not the poll time), so a reading
    /// that is half an hour old is never mistaken for a current one. The reading updated nearest to the instant (within five minutes)
    /// wins; otherwise the reading still in force then, provided the collector polled within five minutes of it (the level was steady).
    /// </summary>
    static double? SocAt(IReadOnlyList<TelemetrySample> soc, DateTimeOffset at)
    {
        var tolerance = TimeSpan.FromMinutes(5).TotalSeconds;
        static DateTimeOffset Effective(TelemetrySample s) => s.SourceUpdatedAt is { } u && u < s.Time ? u : s.Time;
        var nearest = soc.Where(x => x.Status == "observed" && x.Value is not null && Math.Abs((Effective(x) - at).TotalSeconds) <= tolerance)
            .OrderBy(x => Math.Abs((Effective(x) - at).TotalSeconds)).FirstOrDefault();
        if (nearest is not null) return nearest.Value;
        TelemetrySample? inForce = null; bool polledNear = false;
        foreach (var s in soc)
        {
            if (Math.Abs((s.Time - at).TotalSeconds) <= tolerance) polledNear = true;
            if (Effective(s) <= at) inForce = s;
        }
        return inForce is { Status: "observed" } && polledNear ? inForce.Value : null;
    }

    /// <summary>
    /// Elapsed slots take exact meter coverage first ("measured"). Live polls drift off the :00/:30 boundaries, so the fallback prorates
    /// contiguous measured intervals that straddle both slot edges ("boundary"); when part of the slot is a spread (timing-estimated)
    /// interval the value is still given, labelled "estimated". Gaps leave the actual null. Battery level is read at the slot start
    /// (SocActualStart, comparable with Predbat's slot-start forecast) and at its end (SocActual).
    /// </summary>
    PlanSnapshot EnrichPlan(PlanSnapshot plan)
    {
        var now = Clock.GetUtcNow();
        var elapsed = plan.Slots.Where(s => s.DurationMinutes > 0 && s.Time.AddMinutes(s.DurationMinutes) <= now).ToList();
        List<StoredInterval> load = [], pv = [], ev = [], gridIn = [], gridOut = []; List<TelemetrySample> soc = []; bool? includesEv = null;
        if (elapsed.Count > 0)
        {
            var first = elapsed.Min(s => s.Time); var last = elapsed.Max(s => s.Time.AddMinutes(s.DurationMinutes));
            var intervals = LoadIntervals(first, last, ["load", "pv", "ev", "grid_import", "grid_export"]);
            load = intervals.Where(x => x.Metric == "load").ToList(); pv = intervals.Where(x => x.Metric == "pv").ToList(); ev = intervals.Where(x => x.Metric == "ev").ToList();
            gridIn = intervals.Where(x => x.Metric == "grid_import").ToList(); gridOut = intervals.Where(x => x.Metric == "grid_export").ToList();
            soc = SamplesInternal(first.AddMinutes(-10), last.AddMinutes(10), "soc", 0, int.MaxValue);
            includesEv = ev.Count > 0 ? LoadIncludesEv() : null;
        }
        for (var i = 0; i < plan.Slots.Count; i++)
        {
            var s = plan.Slots[i]; var end = s.Time.AddMinutes(s.DurationMinutes);
            if (s.DurationMinutes <= 0 || end > now)
            {
                plan.Slots[i] = s with { LoadActual = null, PvActual = null, SocActual = null, LoadActualMethod = null, PvActualMethod = null, SocActualStart = null, HomeActual = null, EvActual = null, GridImportActual = null, GridExportActual = null };
                continue;
            }
            static (double? Value, string? Method) Actual(SlotAllocation a) =>
                a.Status == "missing" ? (null, null) : a.Status == "estimated" ? (a.Value, "estimated") : a.Exact ? (a.Value, "measured") : (a.Value, "boundary");
            var (loadValue, loadMethod) = Actual(Allocate(load, s.Time, end)); var (pvValue, pvMethod) = Actual(Allocate(pv, s.Time, end));
            var evValue = Allocate(ev, s.Time, end).Value;
            double? homeValue = includesEv == true && loadValue is { } l && evValue is { } e ? Math.Max(0, l - e) : null;
            var socEnd = SocAt(soc, end); var socStart = SocAt(soc, s.Time);
            // Demo plans carry scripted actuals for use before the demo meters are seeded. Once the meters cover a slot, their
            // gaps stay gaps: the embedded values never paper over a missing measurement.
            bool Embedded(string metric, DateTimeOffset from, DateTimeOffset to) => plan.Source == "Demo" && SamplesInternal(from, to, metric, 0, 1).Count == 0;
            var socWindow = TimeSpan.FromMinutes(5);
            plan.Slots[i] = s with
            {
                LoadActual = loadValue ?? (Embedded("load", s.Time, end) ? s.LoadActual : null), LoadActualMethod = loadMethod,
                PvActual = pvValue ?? (Embedded("pv", s.Time, end) ? s.PvActual : null), PvActualMethod = pvMethod,
                SocActual = socEnd ?? (Embedded("soc", end - socWindow, end + socWindow) ? s.SocActual : null),
                SocActualStart = socStart, HomeActual = homeValue, EvActual = evValue,
                GridImportActual = Allocate(gridIn, s.Time, end).Value, GridExportActual = Allocate(gridOut, s.Time, end).Value
            };
        }
        return plan;
    }
}

/// <summary>Whether a window's measured cost is solid enough to compare experiments on.</summary>
public static class TelemetryCostEvidence
{
    /// <summary>
    /// Usable when the matched-period figure is at most 30 minutes (or 1% of a longer window) short, and what is left out is small in
    /// energy and money: at most 0.1 kWh of grid energy unpriced or missing (and none of unknown size), and at most £0.05 or 5% of the
    /// gross cost resting on estimates. Those limits govern, not the matched fraction itself: a five-minute blip bridged by an estimate
    /// leaves the matched figure short (live 4 Oct: 98.7%) while only £0.075 of £5 rests on it. Interval counts do not matter either:
    /// a nightly reset or an idle export meter is not missing data.
    /// </summary>
    public static bool Usable(EnergySummary s) =>
        s.CostObservedSeconds > 0 && (s.To - s.From).TotalSeconds - s.CostObservedSeconds <= Math.Max(1800, .01 * (s.To - s.From).TotalSeconds) &&
        !s.GridEnergyUnknown && s.UnpricedGridKwh <= .1 &&
        s.EstimatedCostGbp <= Math.Max(.05, .05 * (Math.Abs(s.ImportCostGbp ?? 0) + Math.Abs(s.ExportCreditGbp ?? 0)));
}
