namespace Joule;

/// <summary>One derived row of telemetry_intervals.</summary>
public sealed class DerivedInterval
{
    public required string Metric { get; init; }
    public required DateTimeOffset Start { get; init; }
    public required DateTimeOffset End { get; init; }
    public double? Energy { get; init; }
    public required string Source { get; init; }
    /// <summary>observed, spread, gap, invalid, reset or source_changed.</summary>
    public required string Status { get; init; }
    /// <summary>How the energy was worked out (counter, flat, idle, reset, reset_split, reset_after_idle, session_start, spread, tail) or, for an unknown interval, why (offline, idle, not_found, invalid, no_samples, reset, source_changed).</summary>
    public required string Method { get; init; }
    public double? MissingKwh { get; init; }
    public double? Cost { get; set; }
    public string? CostMethod { get; set; }
    public bool Known => Energy is not null;
}

/// <summary>What the derivation needs to know beyond the samples themselves.</summary>
public sealed record DerivationContext(string Profile, TimeSpan Gap, TimeZoneInfo Zone)
{
    /// <summary>The first instant in [from,to] when Predbat expected solar (a plan slot forecasting more than 0.01 kWh, or no forecast
    /// at all); null when the sun was down throughout.</summary>
    public Func<DateTimeOffset, DateTimeOffset, DateTimeOffset?>? SunUp { get; init; }
    public bool SunDown(DateTimeOffset from, DateTimeOffset to) => SunUp?.Invoke(from, to) is null && SunUp is not null;
    /// <summary>Average kW of the stored measured interval that ends at the given instant, used to estimate a short tail.</summary>
    public Func<DateTimeOffset, double?>? RateEndingAt { get; init; }
    /// <summary>An outage longer than this, with no reset inside it, stays a gap: the energy is known but where it fell is not.</summary>
    public static readonly TimeSpan MaxSpread = TimeSpan.FromHours(2);
    /// <summary>A first reading after an idle run is credited to the final poll only if that implies at most this power (a domestic supply).</summary>
    public const double MaxPlausibleKw = 25;
}

/// <summary>
/// Turns one meter's samples into energy intervals, following Home Assistant's conventions for cumulative counters.
/// <list type="bullet">
/// <item>Consecutive readings give the counter difference (status observed).</item>
/// <item>A drop is a reset: the new value is the energy since the reset (Home Assistant total_increasing). When the reading carries a
/// newer <c>last_reset</c>, the interval is split there: the stretch before it is an estimated tail (spread, the previous rate) and the
/// stretch after it is measured.</item>
/// <item>An outage between two readings of the same entity with no reset becomes one interval of energy b − a: observed when the counter
/// did not move (≤ <see cref="DataStore.FlatTolerance"/>), spread when it did and the outage lasted at most two hours, otherwise a gap.</item>
/// <item>"unknown" (status idle) means zero where the sensor profile expects it: a daily counter that has not started today, a charger
/// between sessions, solar when the sun is down. The first reading after such a run is credited to the final poll interval.</item>
/// <item>Repeated polls of an unchanged reading (same last_updated) within one sensor update cycle collapse, so a skipped sensor update
/// does not draw a zero followed by a double.</item>
/// </list>
/// </summary>
public static class IntervalDerivation
{
    static bool Valid(TelemetrySample s) => s.Status == "observed" && s.Value is not null;

    /// <summary>Drops heartbeat polls (same reading, same last_updated) inside a sensor update cycle no longer than the gap tolerance.</summary>
    public static List<TelemetrySample> EffectivePoints(IReadOnlyList<TelemetrySample> raw, TimeSpan gap)
    {
        bool Heartbeat(int i) => i > 0 && raw[i].SourceUpdatedAt is { } u && raw[i - 1].SourceUpdatedAt == u && raw[i].Value == raw[i - 1].Value && raw[i].Status == raw[i - 1].Status && raw[i].EntityId == raw[i - 1].EntityId;
        var result = new List<TelemetrySample>(raw.Count);
        for (var i = 0; i < raw.Count;)
        {
            result.Add(raw[i]);
            var j = i + 1; while (j < raw.Count && Heartbeat(j)) j++;
            // raw[i] starts a run; raw[i+1..j-1] repeat it; raw[j] is the next change (or the end).
            var collapse = j < raw.Count && j > i + 1 && Valid(raw[i]) && Valid(raw[j]) && raw[j].Time - raw[i].Time <= gap;
            if (!collapse) for (var k = i + 1; k < j; k++) result.Add(raw[k]);
            i = j;
        }
        return result;
    }

    public static List<DerivedInterval> Derive(string metric, IReadOnlyList<TelemetrySample> raw, DerivationContext ctx)
    {
        var points = EffectivePoints(raw, ctx.Gap);
        var output = new List<DerivedInterval>();
        var builder = new Builder(metric, ctx, output);
        var valid = new List<int>();
        for (var i = 0; i < points.Count; i++) if (Valid(points[i])) valid.Add(i);
        if (valid.Count == 0)
        {
            if (points.Count >= 2) builder.Leading(points, null);
            return output;
        }
        if (valid[0] > 0) builder.Leading(points.Take(valid[0]).ToList(), points[valid[0]]);
        for (var k = 1; k < valid.Count; k++)
        {
            var a = points[valid[k - 1]]; var b = points[valid[k]];
            var run = points.Skip(valid[k - 1] + 1).Take(valid[k] - valid[k - 1] - 1).ToList();
            if (run.Count == 0 && b.Time - a.Time <= ctx.Gap) builder.Direct(a, b);
            else builder.Outage(a, run, b);
        }
        if (valid[^1] < points.Count - 1) builder.Open(points[valid[^1]], points.Skip(valid[^1] + 1).ToList());
        return output;
    }

    sealed class Builder(string metric, DerivationContext ctx, List<DerivedInterval> output)
    {
        void Add(DateTimeOffset start, DateTimeOffset end, double? energy, string source, string status, string method, double? missing = null)
        {
            if (end <= start) return;
            output.Add(new DerivedInterval { Metric = metric, Start = start, End = end, Energy = energy, Source = source, Status = status, Method = method, MissingKwh = missing });
        }
        bool Session => ctx.Profile == SensorProfiles.SessionCounter;
        bool Daily => SensorProfiles.IsDaily(ctx.Profile);

        /// <summary>Average kW of the measured interval ending at <paramref name="at"/>.</summary>
        double? RateEndingAt(DateTimeOffset at)
        {
            var last = output.Count > 0 ? output[^1] : null;
            if (last is { Status: "observed", Energy: { } e } && last.End == at && last.Method is not ("idle" or "reset_after_idle")) return e / (last.End - last.Start).TotalHours;
            return last is null || last.End != at ? ctx.RateEndingAt?.Invoke(at) : null;
        }
        /// <summary>The estimated energy between the last reading and a reset or session end: the previous rate over that stretch.</summary>
        void Tail(TelemetrySample a, DateTimeOffset end, string reason)
        {
            if (end <= a.Time) return;
            if (end - a.Time > ctx.Gap) { Add(a.Time, end, null, a.Source, "gap", reason); return; }
            var rate = RateEndingAt(a.Time);
            var energy = rate is { } kw && double.IsFinite(kw) && kw >= 0 ? kw * (end - a.Time).TotalHours : (double?)null;
            // A counter that was not moving before the reset proves the tail is zero: a measured flat stretch, not an estimate.
            if (energy is { } e && e <= DataStore.JitterTolerance) { Add(a.Time, end, 0, a.Source, "observed", "flat"); return; }
            Add(a.Time, end, energy, a.Source, energy is null ? "gap" : "spread", energy is null ? reason : "tail");
        }
        bool IdleIsZero(DateTimeOffset from, DateTimeOffset to, TelemetrySample? b) => ctx.Profile switch
        {
            SensorProfiles.SessionCounter or SensorProfiles.DailyCounter => true,
            SensorProfiles.SolarDaily => b is { Value: <= DataStore.FlatTolerance } || ctx.SunDown(from, to),
            _ => false
        };
        /// <summary>
        /// An idle run, allowing brief offline blips inside it (a Home Assistant restart while the charger or solar sat idle): every
        /// stretch of non-idle readings must last no longer than one gap tolerance and be surrounded by idle readings.
        /// </summary>
        bool MostlyIdle(IReadOnlyList<TelemetrySample> run)
        {
            if (run.Count == 0 || run[0].Status != "idle" || run[^1].Status != "idle") return false;
            DateTimeOffset? blipStart = null;
            foreach (var x in run)
            {
                if (x.Status == "idle") { blipStart = null; continue; }
                blipStart ??= x.Time;
                if (x.Time - blipStart.Value > ctx.Gap) return false;
            }
            return true;
        }
        /// <summary>When the sensor switched to unknown: Home Assistant's last_updated on the first idle reading, if it lies after the last value.</summary>
        static DateTimeOffset SwitchTime(TelemetrySample a, TelemetrySample firstIdle) =>
            firstIdle.SourceUpdatedAt is { } u && u >= a.Time && u < firstIdle.Time ? u : firstIdle.Time;
        /// <summary>
        /// Whether crediting a whole reading to [from,to] implies at most <see cref="DerivationContext.MaxPlausibleKw"/>. Every reset or
        /// new-session credit passes through this: a lifetime counter that dips by a few watt-hours must never count its total reading.
        /// </summary>
        static bool Plausible(double energy, DateTimeOffset from, DateTimeOffset to)
        {
            var hours = (to - from).TotalHours;
            return hours > 0 ? energy / hours <= DerivationContext.MaxPlausibleKw : energy <= .01;
        }
        static string Reason(IReadOnlyList<TelemetrySample> run)
        {
            if (run.Count == 0) return "no_samples";
            if (run.All(x => x.Status == "idle")) return "idle";
            if (run.Any(x => x.Status == "not_found")) return "not_found";
            if (run.Any(x => x.Status == "unavailable")) return "offline";
            return "invalid";
        }
        /// <summary>The reset instant when the later reading (or an idle reading in the run) carries a newer last_reset inside the stretch.</summary>
        static DateTimeOffset? ResetInstant(TelemetrySample a, IReadOnlyList<TelemetrySample> run, TelemetrySample? b)
        {
            var before = Attributes.LastReset(a.AttributesJson);
            foreach (var candidate in (b is null ? run : run.Append(b)))
                if (Attributes.LastReset(candidate.AttributesJson) is { } r && r > a.Time && r <= candidate.Time && (before is null || r > before)) return r;
            return null;
        }

        public void Direct(TelemetrySample a, TelemetrySample b)
        {
            if (a.EntityId != b.EntityId) { Add(a.Time, b.Time, null, a.Source, "source_changed", "source_changed"); return; }
            var av = a.Value!.Value; var bv = b.Value!.Value;
            if (ResetInstant(a, [], b) is { } reset) { AfterReset(a, reset, [], b); return; }
            if (bv >= av - DataStore.JitterTolerance) { Add(a.Time, b.Time, Math.Max(0, bv - av), a.Source, "observed", "counter"); return; }
            // The counter fell. Its new reading is the energy since the restart only if that much could have flowed meanwhile.
            var plausible = Plausible(bv, a.Time, b.Time);
            if (Session && plausible) { Add(a.Time, b.Time, bv, a.Source, "observed", "session_start"); return; }
            if (Daily && bv < av / 2 && SensorProfiles.LocalMidnightWithin(a.Time, b.Time, ctx.Zone) is { } midnight) { AfterReset(a, midnight, [], b); return; }
            if (bv < av / 2 && plausible) { Add(a.Time, b.Time, bv, a.Source, "observed", "reset"); return; }
            if (av - bv <= DataStore.FlatTolerance) { Add(a.Time, b.Time, 0, a.Source, "observed", "flat"); return; }
            Add(a.Time, b.Time, null, a.Source, "reset", "reset");
        }

        /// <summary>A reset at <paramref name="reset"/> between reading a and reading b: estimated tail before it, b's value after it.</summary>
        void AfterReset(TelemetrySample a, DateTimeOffset reset, IReadOnlyList<TelemetrySample> run, TelemetrySample b)
        {
            var reason = Reason(run.Where(x => x.Time < reset).ToList());
            if (reset >= b.Time)
            {
                Tail(a, b.Time, reason);
                return;
            }
            Tail(a, reset, reason);
            var after = run.Where(x => x.Time >= reset).ToList();
            var bv = b.Value!.Value;
            if (!Plausible(bv, reset, b.Time)) { Add(reset, b.Time, null, b.Source, "reset", "reset"); return; }
            if (after.Count == 0 || b.Time - reset <= ctx.Gap) { Add(reset, b.Time, bv, b.Source, "observed", "reset_split"); return; }
            // The counter itself proves that (almost) nothing flowed since the reset, whatever the sensor said in between.
            if (bv <= DataStore.FlatTolerance)
            {
                var lastRun = after[^1].Time;
                Add(reset, lastRun, 0, b.Source, "observed", MostlyIdle(after) ? "idle" : "flat");
                Add(lastRun, b.Time, bv, b.Source, "observed", "reset_after_idle");
                return;
            }
            var last = after[^1].Time;
            if (MostlyIdle(after) && IdleIsZero(reset, last, b) && Plausible(bv, last, b.Time))
            {
                Add(reset, last, 0, b.Source, "observed", "idle");
                Add(last, b.Time, bv, b.Source, "observed", "reset_after_idle");
                return;
            }
            // Solar that read unknown overnight and past sunrise: zero while the sun was down, the first reading spread over the rest.
            if (ctx.Profile == SensorProfiles.SolarDaily && MostlyIdle(after) && ctx.SunUp?.Invoke(reset, last) is { } sunrise && sunrise > reset)
            {
                Add(reset, sunrise, 0, b.Source, "observed", "idle");
                Add(sunrise, b.Time, bv, b.Source, "spread", "spread");
                return;
            }
            Add(reset, b.Time, bv, b.Source, "spread", "spread");
        }

        public void Outage(TelemetrySample a, IReadOnlyList<TelemetrySample> run, TelemetrySample b)
        {
            var reason = Reason(run);
            if (a.EntityId != b.EntityId) { Add(a.Time, b.Time, null, a.Source, "source_changed", "source_changed"); return; }
            var av = a.Value!.Value; var bv = b.Value!.Value; var span = b.Time - a.Time;
            if (ResetInstant(a, run, b) is { } reset) { AfterReset(a, reset, run, b); return; }
            var allIdle = MostlyIdle(run);
            var dropped = bv < av - DataStore.JitterTolerance;
            // A session counter that reads less after an idle run has started a new session. If it reads more after a brief unknown
            // (a Home Assistant restart lasts minutes), it is the same session resumed: counting the whole reading as new would
            // double-count it. After a longer idle run it is a new session even if it already reads more (a 0.02 kWh top-up hours earlier).
            var newSession = Session && allIdle && (dropped || run[^1].Time - run[0].Time > ctx.Gap + ctx.Gap);
            if (Daily && dropped && bv < av / 2 && SensorProfiles.LocalMidnightWithin(a.Time, b.Time, ctx.Zone) is { } midnight) { AfterReset(a, midnight, run, b); return; }
            // A restart is credited only when its reading could have flowed since the previous one; otherwise (a lifetime counter's
            // glitch, or a total that came back after an unknown spell) the counter difference below decides.
            if ((newSession || dropped && bv < av / 2) && Plausible(bv, a.Time, b.Time))
            {
                if (allIdle && IdleIsZero(run[0].Time, run[^1].Time, b) && Plausible(bv, run[^1].Time, b.Time))
                {
                    var switchAt = SwitchTime(a, run[0]); Tail(a, switchAt, reason);
                    Add(switchAt, run[^1].Time, 0, b.Source, "observed", "idle");
                    Add(run[^1].Time, b.Time, bv, b.Source, "observed", "reset_after_idle");
                    return;
                }
                if (span <= DerivationContext.MaxSpread) Add(a.Time, b.Time, bv, a.Source, "spread", "reset");
                else Add(a.Time, b.Time, null, a.Source, "gap", reason);
                return;
            }
            if (dropped)
            {
                if (av - bv <= DataStore.FlatTolerance) Add(a.Time, b.Time, 0, a.Source, "observed", "flat");
                else Add(a.Time, b.Time, null, a.Source, "reset", "reset");
                return;
            }
            var delta = Math.Max(0, bv - av);
            // A counter that did not move across the outage proves that nothing flowed, however long it lasted.
            if (delta <= DataStore.FlatTolerance) { Add(a.Time, b.Time, delta, a.Source, "observed", allIdle && IdleIsZero(a.Time, b.Time, null) ? "idle" : "flat"); return; }
            if (span <= DerivationContext.MaxSpread) { Add(a.Time, b.Time, delta, a.Source, "spread", "spread"); return; }
            Add(a.Time, b.Time, null, a.Source, "gap", reason, delta);
        }

        /// <summary>An outage still in progress after the latest usable reading.</summary>
        public void Open(TelemetrySample a, IReadOnlyList<TelemetrySample> run)
        {
            var reason = Reason(run); var last = run[^1].Time;
            if (run.Any(x => x.EntityId != a.EntityId)) { Add(a.Time, last, null, a.Source, "source_changed", "source_changed"); return; }
            if (ResetInstant(a, run, null) is { } reset)
            {
                var after = run.Where(x => x.Time >= reset).ToList();
                if (MostlyIdle(after) && IdleIsZero(reset, last, null))
                {
                    Tail(a, reset, Reason(run.Where(x => x.Time < reset).ToList()));
                    Add(reset, last, 0, a.Source, "observed", "idle");
                    return;
                }
            }
            if (Session && MostlyIdle(run))
            {
                var switchAt = SwitchTime(a, run[0]); Tail(a, switchAt, reason);
                Add(switchAt, last, 0, a.Source, "observed", "idle");
                return;
            }
            Add(a.Time, last, null, a.Source, "invalid", reason);
        }

        /// <summary>Samples before the first usable reading (the start of history, or a long outage).</summary>
        public void Leading(IReadOnlyList<TelemetrySample> run, TelemetrySample? b)
        {
            var reason = Reason(run); var last = run[^1].Time;
            if (Session && MostlyIdle(run))
            {
                Add(run[0].Time, last, 0, run[0].Source, "observed", "idle");
                if (b is { Value: { } bv }) Add(last, b.Time, Plausible(bv, last, b.Time) ? bv : null, b.Source, Plausible(bv, last, b.Time) ? "observed" : "invalid", Plausible(bv, last, b.Time) ? "reset_after_idle" : reason);
                return;
            }
            Add(run[0].Time, b?.Time ?? last, null, run[0].Source, "invalid", reason);
        }
    }
}

/// <summary>
/// The tariff in force over time, from the tariff sensor's readings, with Predbat's plan rates filling stretches where the sensor was
/// unavailable. A rate change takes effect when Home Assistant recorded it (last_updated), moved back to the :00/:30 boundary when the
/// change was picked up within five minutes of it.
/// </summary>
public sealed class TariffSchedule
{
    readonly List<(DateTimeOffset From, DateTimeOffset To, double? Rate)> segments = [];
    readonly List<(DateTimeOffset From, DateTimeOffset To, double Rate)> plan;
    public TariffSchedule(IReadOnlyList<TelemetrySample> samples, TimeSpan gap, IReadOnlyList<(DateTimeOffset From, DateTimeOffset To, double Rate)>? planRates = null)
    {
        plan = planRates?.OrderBy(x => x.From).ToList() ?? [];
        DateTimeOffset? previousTime = null; (DateTimeOffset Start, DateTimeOffset LastSeen, double? Rate)? current = null;
        foreach (var s in samples.OrderBy(x => x.Time))
        {
            double? rate = s.Status == "observed" && s.Value is { } v && double.IsFinite(v) ? v : null;
            var changed = current is null || current.Value.Rate != rate || previousTime is { } p && s.Time - p > gap;
            if (changed)
            {
                var start = s.SourceUpdatedAt is { } u && u <= s.Time && (previousTime is null || u > previousTime) ? u : s.Time;
                // Octopus rates change on the half hour; a pickup up to five minutes late still belongs to the boundary.
                var boundary = new DateTimeOffset(start.Year, start.Month, start.Day, start.Hour, start.Minute < 30 ? 0 : 30, 0, start.Offset);
                if (start - boundary < TimeSpan.FromMinutes(5) && (previousTime is null || boundary > previousTime)) start = boundary;
                if (current is { } c) Close(c, start, gap);
                current = (start, s.Time, rate);
            }
            else current = current!.Value with { LastSeen = s.Time };
            previousTime = s.Time;
        }
        if (current is { } last) Close(last, last.LastSeen + gap, gap);
    }
    void Close((DateTimeOffset Start, DateTimeOffset LastSeen, double? Rate) run, DateTimeOffset next, TimeSpan gap)
    {
        var confirmed = run.LastSeen + gap;
        var end = next < confirmed ? next : confirmed;
        if (end > run.Start) segments.Add((run.Start, end, run.Rate));
    }

    /// <summary>Cost in £ of <paramref name="energy"/> kWh spread evenly over [start,end], and whether a plan rate was needed.</summary>
    public (double? Cost, string? Method) Price(DateTimeOffset start, DateTimeOffset end, double energy)
    {
        if (energy == 0) return (0, "tariff");
        if (end <= start || !double.IsFinite(energy)) return (null, null);
        double cost = 0; var cursor = start; bool usedPlan = false; var seconds = (end - start).TotalSeconds;
        bool AddPortion(DateTimeOffset from, DateTimeOffset to, double rate)
        {
            cost += energy * (rate / 100) * ((to - from).TotalSeconds / seconds);
            return double.IsFinite(cost);
        }
        bool Fallback(DateTimeOffset from, DateTimeOffset to)
        {
            var c = from;
            foreach (var p in plan)
            {
                if (p.To <= c) continue;
                if (p.From > c || c >= to) break;
                var right = p.To < to ? p.To : to;
                if (!AddPortion(c, right, p.Rate)) return false;
                usedPlan = true; c = right;
            }
            return c >= to;
        }
        foreach (var segment in segments)
        {
            if (segment.To <= cursor) continue;
            if (segment.From >= end) break;
            if (segment.From > cursor && !Fallback(cursor, segment.From)) return (null, null);
            var left = segment.From > cursor ? segment.From : cursor; var right = segment.To < end ? segment.To : end;
            if (segment.Rate is { } rate) { if (!AddPortion(left, right, rate)) return (null, null); }
            else if (!Fallback(left, right)) return (null, null);
            cursor = right;
            if (cursor >= end) break;
        }
        if (cursor < end && !Fallback(cursor, end)) return (null, null);
        return double.IsFinite(cost) ? (cost, usedPlan ? "plan_rate" : "tariff") : (null, null);
    }
}
