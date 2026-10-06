using System.Diagnostics;
using System.Globalization;

namespace Joule;

/// <summary>Telemetry accounting settings from Home Assistant configuration.</summary>
/// <param name="TimeZone">Local zone for daily resets and day totals.</param>
/// <param name="Profiles">Per-metric sensor profile overrides (metric → profile).</param>
/// <param name="LoadIncludesEv">auto (detect from history), true or false: whether the load meter includes EV charging.</param>
/// <summary>How the collector is set up: time zone, profile overrides, whether load includes the car, and the gap tolerance (null: <see cref="DataStore.DefaultGap"/>).</summary>
public sealed record TelemetrySettings(string TimeZone = "Europe/London", IReadOnlyDictionary<string, string>? Profiles = null, string LoadIncludesEv = "auto", TimeSpan? MaxGap = null);

public partial class DataStore
{
    /// <summary>The interval rules version. Bump it when derivation changes so stored history is re-derived once at startup.</summary>
    public const string IntervalRulesVersion = "intervals-v5";
    /// <summary>Gap tolerance when a caller does not pass one: max(15 min, 2.5 × the default 5-minute poll).</summary>
    public static readonly TimeSpan DefaultGap = TimeSpan.FromMinutes(15);
    public const double FlatTolerance = 0.05, JitterTolerance = 0.001;
    /// <summary>How far back an incremental rebuild looks for the last usable reading before new data, so a returning sensor re-derives its whole outage.</summary>
    static readonly TimeSpan BridgeLookback = TimeSpan.FromHours(36);
    static readonly string[] NoAlignMetrics = ["intelligent_slots", "alternative_forecast"];

    TelemetrySettings telemetrySettings = new();
    TimeZoneInfo telemetryZone = ResolveTelemetryZone("Europe/London");
    readonly Dictionary<string, (string Profile, DateTimeOffset CheckedAt, double SpanDays, int Count)> profileCache = [];
    Dictionary<string, TelemetrySample>? latestSamples;
    Dictionary<string, TelemetrySample>? latestObserved;
    DateTimeOffset? firstObservation; bool firstObservationLoaded;
    (bool? Value, DateTimeOffset At)? loadIncludesEv;

    static TimeZoneInfo ResolveTelemetryZone(string id)
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
        catch (Exception e) when (e is TimeZoneNotFoundException or InvalidTimeZoneException) { return TimeZoneInfo.Utc; }
    }

    /// <summary>Applies Home Assistant settings (time zone, profile overrides, EV scope). Called once by the collector at startup.</summary>
    public void ConfigureTelemetry(TelemetrySettings settings)
    {
        lock (gate)
        {
            telemetrySettings = settings; telemetryZone = ResolveTelemetryZone(settings.TimeZone);
            profileCache.Clear(); loadIncludesEv = null;
        }
    }
    public TimeZoneInfo TelemetryZone => telemetryZone;

    void InitializeTelemetry()
    {
        Execute("""
            CREATE TABLE IF NOT EXISTS telemetry_samples(metric VARCHAR, entity_id VARCHAR, time TIMESTAMPTZ, value DOUBLE, unit VARCHAR, source VARCHAR, raw_state VARCHAR, raw_unit VARCHAR, source_updated_at TIMESTAMPTZ, attributes_json VARCHAR, status VARCHAR, PRIMARY KEY(metric,time));
            CREATE TABLE IF NOT EXISTS store_migrations(name VARCHAR PRIMARY KEY, applied_at TIMESTAMPTZ);
            CREATE TABLE IF NOT EXISTS telemetry_profiles(metric VARCHAR PRIMARY KEY, profile VARCHAR, detected_at TIMESTAMPTZ, origin VARCHAR);
            DELETE FROM actual_energy WHERE rowid NOT IN (SELECT max(rowid) FROM actual_energy GROUP BY time);
            """);
        // Intervals are derived data. An older table without the method columns is replaced and re-derived by EnsureIntervalRules.
        bool current;
        using (var c = Command("SELECT count(*) FROM information_schema.columns WHERE table_name='telemetry_intervals' AND column_name='missing_kwh'")) current = Convert.ToInt64(c.ExecuteScalar()) > 0;
        if (!current)
        {
            Execute("DROP TABLE IF EXISTS telemetry_intervals");
            Execute("DELETE FROM store_migrations WHERE name LIKE 'intervals-%'");
        }
        Execute("CREATE TABLE IF NOT EXISTS telemetry_intervals(metric VARCHAR, start_time TIMESTAMPTZ, end_time TIMESTAMPTZ, energy_kwh DOUBLE, source VARCHAR, status VARCHAR, import_cost_gbp DOUBLE, export_credit_gbp DOUBLE, method VARCHAR, cost_method VARCHAR, missing_kwh DOUBLE, PRIMARY KEY(metric,start_time,end_time))");
    }

    /// <summary>
    /// Stores samples and re-derives the affected energy intervals. With <paramref name="alignToSource"/> (the live collector), a reading
    /// that changed is timestamped with Home Assistant's last_updated instead of the poll time, provided that is newer than the metric's
    /// previous sample; an unchanged reading keeps the poll time as a heartbeat. Home Assistant "unknown" is stored as status idle.
    /// </summary>
    public void SaveTelemetry(IEnumerable<TelemetrySample> samples, TimeSpan? maxGap = null, bool alignToSource = false)
    {
        var batch = samples.Select(NormaliseStatus).OrderBy(s => s.Time).ThenBy(s => s.Metric, StringComparer.Ordinal).ToList(); if (batch.Count == 0) return;
        // Bound transaction undo/index memory for history imports and demo seeding.
        // Validate the entire request before any chunk writes; chunks remain idempotent.
        if (batch.Any(s => s.Value is double n && !double.IsFinite(n))) throw new DomainException("Telemetry values must be finite.", 400);
        if (batch.Count > 500) { foreach (var chunk in batch.Chunk(500)) SaveTelemetry(chunk, maxGap, alignToSource); return; }
        var gap = maxGap ?? DefaultGap;
        if (gap <= TimeSpan.Zero || gap > TimeSpan.FromHours(24)) throw new DomainException("Invalid telemetry gap tolerance.", 400);
        lock (gate)
        {
            Execute("BEGIN TRANSACTION");
            try
            {
                if (alignToSource) batch = AlignToSourceTimes(batch);
                InsertRows("telemetry_samples", batch.Select(s => new object?[] { s.Metric, s.EntityId, s.Time, s.Value, s.Unit, s.Source, s.RawState, s.RawUnit, s.SourceUpdatedAt, s.AttributesJson, s.Status }));
                RememberSamples(batch);
                foreach (var group in batch.GroupBy(x => x.Metric))
                {
                    var from = group.Min(x => x.Time); var to = group.Max(x => x.Time);
                    if (TelemetrySchema.EnergyMetrics.Contains(group.Key))
                    {
                        if (RefreshProfile(group.Key)) RebuildAll(group.Key, gap, inTransaction: true);
                        else RebuildIntervals(group.Key, from, to, gap);
                    }
                    if (group.Key is "import_tariff" or "export_tariff") RebuildIntervals(group.Key == "import_tariff" ? "grid_import" : "grid_export", from - gap, to + gap, gap);
                }
                Execute("COMMIT");
            }
            catch { Execute("ROLLBACK"); latestSamples = null; latestObserved = null; firstObservationLoaded = false; throw; }
        }
    }

    /// <summary>Home Assistant's "unknown" is a normal no-value state, not a fault: store it as idle whatever the writer called it.</summary>
    static TelemetrySample NormaliseStatus(TelemetrySample s) =>
        s.Status is "unavailable" or "invalid" && s.Value is null && s.RawState.Equals("unknown", StringComparison.OrdinalIgnoreCase) ? s with { Status = "idle" } : s;

    /// <summary>Re-keys changed readings at the sensor's own update time. The previous sample comes from the batch or the store.</summary>
    List<TelemetrySample> AlignToSourceTimes(List<TelemetrySample> batch)
    {
        var previous = new Dictionary<string, TelemetrySample>();
        var result = new List<TelemetrySample>(batch.Count);
        foreach (var s in batch)
        {
            var prior = previous.TryGetValue(s.Metric, out var p) ? p : LatestSample(s.Metric);
            var sample = s;
            if (!NoAlignMetrics.Contains(s.Metric) && s.SourceUpdatedAt is { } updated && prior is not null && updated > prior.Time && updated != prior.SourceUpdatedAt && updated <= s.Time.AddSeconds(1))
                sample = s with { Time = updated < s.Time ? updated : s.Time };
            previous[s.Metric] = sample; result.Add(sample);
        }
        return result.OrderBy(s => s.Time).ThenBy(s => s.Metric, StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// Upserts rows (strings, doubles, timestamps or nulls, in column order) through a scratch table filled by DuckDB's appender, then
    /// one INSERT OR REPLACE. Planning a parameterised VALUES list for every hundred rows made bulk writes (the demo's five weeks of
    /// history, interval rebuilds) more than ten times slower: about ten seconds of every first start, and minutes on a small machine.
    /// </summary>
    void InsertRows(string table, IEnumerable<object?[]> rows, string? columns = null)
    {
        var batch = rows as IReadOnlyCollection<object?[]> ?? rows.ToList();
        if (batch.Count == 0) return;
        var scratch = "insert_rows_" + table;
        Execute($"CREATE OR REPLACE TEMP TABLE {scratch} AS SELECT {columns ?? "*"} FROM {table} LIMIT 0");
        using (var appender = db.CreateAppender("temp", "main", scratch))
        {
            foreach (var values in batch)
            {
                var row = appender.CreateRow();
                foreach (var value in values)
                {
                    switch (value)
                    {
                        case null or DBNull: row.AppendNullValue(); break;
                        case string s: row.AppendValue(s); break;
                        case double d: row.AppendValue(d); break;
                        case DateTimeOffset t: row.AppendValue(t); break;
                        default: throw new ArgumentException($"Unsupported column value {value.GetType().Name}.", nameof(rows));
                    }
                }
                row.EndRow();
            }
        }
        Execute($"INSERT OR REPLACE INTO {table}{(columns is null ? "" : "(" + columns + ")")} SELECT * FROM {scratch}");
        Execute($"DELETE FROM {scratch}");
    }
    static DateTimeOffset Stamp(object value) => value switch { DateTimeOffset d => d, DateTime d => new DateTimeOffset(DateTime.SpecifyKind(d, DateTimeKind.Utc)), _ => DateTimeOffset.Parse(value.ToString()!, CultureInfo.InvariantCulture) };

    // ---- caches (all under the gate) ----

    void LoadLatestCaches()
    {
        if (latestSamples is not null && latestObserved is not null) return;
        latestSamples = []; latestObserved = [];
        foreach (var (sql, target) in new[] {
            ("SELECT * FROM (SELECT *,row_number() OVER(PARTITION BY metric ORDER BY time DESC) rn FROM telemetry_samples) WHERE rn=1", latestSamples),
            ("SELECT * FROM (SELECT *,row_number() OVER(PARTITION BY metric ORDER BY time DESC) rn FROM telemetry_samples WHERE status='observed' AND value IS NOT NULL) WHERE rn=1", latestObserved) })
        {
            using var c = Command(sql); using var r = c.ExecuteReader();
            while (r.Read()) { var s = ReadSample(r); target[s.Metric] = s; }
        }
    }
    TelemetrySample? LatestSample(string metric) { LoadLatestCaches(); return latestSamples!.GetValueOrDefault(metric); }
    TelemetrySample? LatestObserved(string metric) { LoadLatestCaches(); return latestObserved!.GetValueOrDefault(metric); }
    void RememberSamples(IEnumerable<TelemetrySample> batch)
    {
        LoadLatestCaches();
        foreach (var s in batch)
        {
            if (!latestSamples!.TryGetValue(s.Metric, out var l) || s.Time >= l.Time) latestSamples[s.Metric] = s;
            if (s.Status == "observed" && s.Value is not null && (!latestObserved!.TryGetValue(s.Metric, out var o) || s.Time >= o.Time)) latestObserved[s.Metric] = s;
            if (firstObservationLoaded && TelemetrySchema.EnergyMetrics.Contains(s.Metric) && s.Status == "observed" && s.Value is not null && (firstObservation is null || s.Time < firstObservation)) firstObservation = s.Time;
        }
    }
    /// <summary>Latest sample time of any metric: when the collector last polled.</summary>
    DateTimeOffset? LastPollTime() { LoadLatestCaches(); return latestSamples!.Count == 0 ? null : latestSamples.Values.Max(x => x.Time); }

    static TelemetrySample ReadSample(System.Data.Common.DbDataReader r) =>
        new(r.GetString(0), r.GetString(1), Stamp(r.GetValue(2)), r.IsDBNull(3) ? null : r.GetDouble(3), r.GetString(4), r.GetString(5), r.GetString(6), r.GetString(7), r.IsDBNull(8) ? null : Stamp(r.GetValue(8)), r.GetString(9), r.GetString(10));

    // ---- sensor profiles ----

    /// <summary>The profile in force for a metric: a configured override, else the profile detected from recent history.</summary>
    public string ProfileFor(string metric)
    {
        lock (gate)
        {
            if (SensorProfiles.Fixed(metric) is { } fixedProfile) return fixedProfile;
            if (telemetrySettings.Profiles is { } overrides && SensorProfiles.Override(overrides, metric) is { } configured) return configured;
            if (profileCache.TryGetValue(metric, out var cached)) return cached.Profile;
            RefreshProfile(metric, force: true);
            return profileCache[metric].Profile;
        }
    }
    public Dictionary<string, string> ReadSensorProfiles()
    {
        lock (gate) return TelemetrySchema.EnergyMetrics.ToDictionary(m => m, ProfileFor);
    }
    /// <summary>Re-detects a profile when it was judged on under two days of history, or six hours ago. Returns true when it changed.</summary>
    bool RefreshProfile(string metric, bool force = false)
    {
        if (telemetrySettings.Profiles is { } overrides && SensorProfiles.Override(overrides, metric) is { } configured)
        {
            var had = profileCache.TryGetValue(metric, out var old) && old.Profile != configured;
            profileCache[metric] = (configured, Clock.GetUtcNow(), double.MaxValue, int.MaxValue);
            StoreProfile(metric, configured, "configured");
            return had;
        }
        // Settled after two days of history: re-check every six hours. Before that, re-check every ten minutes, or at once while there are
        // under fifty readings (the first day decides most profiles from last_reset).
        if (!force && profileCache.TryGetValue(metric, out var cached) && Clock.GetUtcNow() - cached.CheckedAt < (cached.SpanDays >= 2 ? TimeSpan.FromHours(6) : cached.Count < 50 ? TimeSpan.Zero : TimeSpan.FromMinutes(10))) return false;
        var history = new List<TelemetrySample>();
        using (var c = Command("SELECT * FROM telemetry_samples WHERE metric=? AND time>=(SELECT max(time) FROM telemetry_samples WHERE metric=?)-INTERVAL 14 DAY ORDER BY time", metric, metric))
        using (var r = c.ExecuteReader()) while (r.Read()) history.Add(ReadSample(r));
        var detected = SensorProfiles.Detect(metric, history, telemetryZone);
        var span = history.Count < 2 ? 0 : (history[^1].Time - history[0].Time).TotalDays;
        var changed = profileCache.TryGetValue(metric, out var previous) ? previous.Profile != detected : StoredProfile(metric) is { } stored && stored != detected;
        profileCache[metric] = (detected, Clock.GetUtcNow(), span, history.Count);
        StoreProfile(metric, detected, "detected");
        return changed;
    }
    string? StoredProfile(string metric)
    {
        using var c = Command("SELECT profile FROM telemetry_profiles WHERE metric=?", metric);
        return c.ExecuteScalar() as string;
    }
    void StoreProfile(string metric, string profile, string origin)
    {
        if (StoredProfile(metric) == profile) return;
        Execute("INSERT OR REPLACE INTO telemetry_profiles VALUES (?,?,?,?)", metric, profile, Clock.GetUtcNow(), origin);
    }

    // ---- interval derivation ----

    /// <summary>
    /// Re-derives every stored energy interval once per rule version and gap tolerance, so rule changes also apply to history collected
    /// before the upgrade. The same pass migrates legacy sample statuses: HA "unknown" stored as unavailable/invalid becomes idle, and
    /// legacy "stale" rows (a value nulled for age) get their reading back from the raw state.
    /// </summary>
    public void EnsureIntervalRules(TimeSpan gap)
    {
        var marker = $"{IntervalRulesVersion}-gap{(int)gap.TotalSeconds}";
        lock (gate)
        {
            using (var c = Command("SELECT count(*) FROM store_migrations WHERE name=?", marker)) if (Convert.ToInt64(c.ExecuteScalar()) > 0) return;
            var clock = Stopwatch.StartNew();
            // Bounded transactions keep DuckDB's memory use flat however long the history is. Every step is idempotent, and the marker is
            // written last, so an interrupted rebuild simply runs again at the next start.
            try
            {
                Transaction(MigrateSampleStatuses);
                RekeySamplesToSourceTime();
                profileCache.Clear();
                foreach (var metric in TelemetrySchema.EnergyMetrics) { Transaction(() => RefreshProfile(metric, force: true)); RebuildAll(metric, gap); }
                Transaction(() => Execute("INSERT INTO store_migrations VALUES (?,?)", marker, Clock.GetUtcNow()));
                LastIntervalRebuild = clock.Elapsed;
            }
            finally { latestSamples = null; latestObserved = null; firstObservationLoaded = false; }
        }
    }

    /// <summary>
    /// Bulk history load (demo seeding, imports): stores the samples, then derives each affected meter once over the whole range instead
    /// of once per 500-sample chunk.
    /// </summary>
    public void ImportTelemetry(IEnumerable<TelemetrySample> samples, TimeSpan? maxGap = null)
    {
        var batch = samples.Select(NormaliseStatus).OrderBy(s => s.Time).ThenBy(s => s.Metric, StringComparer.Ordinal).ToList(); if (batch.Count == 0) return;
        if (batch.Any(s => s.Value is double n && !double.IsFinite(n))) throw new DomainException("Telemetry values must be finite.", 400);
        var gap = maxGap ?? DefaultGap;
        lock (gate)
        {
            try
            {
                foreach (var chunk in batch.Chunk(2000)) Transaction(() => { InsertRows("telemetry_samples", chunk.Select(s => new object?[] { s.Metric, s.EntityId, s.Time, s.Value, s.Unit, s.Source, s.RawState, s.RawUnit, s.SourceUpdatedAt, s.AttributesJson, s.Status })); RememberSamples(chunk); });
                var touched = batch.GroupBy(x => x.Metric).ToDictionary(g => g.Key, g => (From: g.Min(x => x.Time), To: g.Max(x => x.Time)));
                foreach (var (tariff, grid) in new[] { ("import_tariff", "grid_import"), ("export_tariff", "grid_export") })
                    if (touched.TryGetValue(tariff, out var t)) touched[grid] = touched.TryGetValue(grid, out var g) ? (g.From < t.From ? g.From : t.From, g.To > t.To ? g.To : t.To) : t;
                foreach (var (metric, range) in touched.Where(x => TelemetrySchema.EnergyMetrics.Contains(x.Key)))
                {
                    Transaction(() => RefreshProfile(metric, force: true));
                    RebuildRange(metric, range.From, range.To, gap);
                }
            }
            catch { latestSamples = null; latestObserved = null; firstObservationLoaded = false; throw; }
        }
    }

    void Transaction(Action work)
    {
        Execute("BEGIN TRANSACTION");
        try { work(); Execute("COMMIT"); }
        catch { Execute("ROLLBACK"); throw; }
    }

    /// <summary>Re-derives [from,to] in two-day steps, each in its own transaction; each step anchors on the intervals the last one stored.</summary>
    void RebuildRange(string metric, DateTimeOffset from, DateTimeOffset to, TimeSpan gap)
    {
        var step = TimeSpan.FromDays(2);
        for (var start = from; ; start += step)
        {
            var end = start + step < to ? start + step : to;
            Transaction(() => RebuildIntervals(metric, start, end, gap));
            if (end >= to) break;
        }
    }

    /// <summary>How long the last full interval rebuild took (it runs under the store lock at startup).</summary>
    public TimeSpan? LastIntervalRebuild { get; private set; }

    void MigrateSampleStatuses()
    {
        Execute("UPDATE telemetry_samples SET status='idle' WHERE status IN ('unavailable','invalid') AND value IS NULL AND lower(raw_state)='unknown'");
        var stale = new List<TelemetrySample>();
        using (var c = Command("SELECT * FROM telemetry_samples WHERE status='stale'")) using (var r = c.ExecuteReader()) while (r.Read()) stale.Add(ReadSample(r));
        foreach (var s in stale)
        {
            var (value, unit, status) = HomeAssistantClient.Normalize(s.Metric, s.RawState, s.RawUnit);
            Execute("UPDATE telemetry_samples SET value=?, unit=?, status=? WHERE metric=? AND time=?", value, unit == "" ? s.Unit : unit, status, s.Metric, s.Time);
        }
    }

    /// <summary>
    /// Samples collected before readings were keyed at Home Assistant's update time carry the poll time. Re-key them once with the
    /// collector's rule (a changed reading moves to its last_updated when that is newer than the previous sample; an unchanged one stays
    /// a heartbeat at its poll time), so old and new history derive the same way. Values are never changed; only the time key moves
    /// back by at most one poll interval, and keys stay strictly increasing per metric, so they cannot collide.
    /// </summary>
    void RekeySamplesToSourceTime()
    {
        const string marker = "samples-source-time-v1";
        using (var c = Command("SELECT count(*) FROM store_migrations WHERE name=?", marker)) if (Convert.ToInt64(c.ExecuteScalar()) > 0) return;
        var metrics = new List<string>();
        using (var c = Command("SELECT DISTINCT metric FROM telemetry_samples")) using (var r = c.ExecuteReader()) while (r.Read()) metrics.Add(r.GetString(0));
        foreach (var metric in metrics.Where(m => !NoAlignMetrics.Contains(m)))
        {
            var samples = SamplesInternal(DateTimeOffset.MinValue.AddYears(1), DateTimeOffset.MaxValue.AddYears(-1), metric, 0, int.MaxValue);
            var moves = new List<(TelemetrySample Old, DateTimeOffset Time)>(); TelemetrySample? previous = null; DateTimeOffset? previousTime = null;
            foreach (var s in samples)
            {
                var time = s.Time;
                if (s.SourceUpdatedAt is { } updated && previous is not null && updated > previousTime && updated != previous.SourceUpdatedAt && updated < s.Time) time = updated;
                if (time != s.Time) moves.Add((s, time));
                previous = s; previousTime = time;
            }
            // Set-based moves through a scratch table: delete the old keys, then insert the rows at their new keys.
            foreach (var chunk in moves.Chunk(5000)) Transaction(() =>
            {
                Execute("CREATE TEMP TABLE IF NOT EXISTS telemetry_rekey(metric VARCHAR, old_time TIMESTAMPTZ, PRIMARY KEY(metric,old_time))");
                Execute("DELETE FROM telemetry_rekey");
                InsertRows("telemetry_rekey", chunk.Select(m => new object?[] { m.Old.Metric, m.Old.Time }));
                Execute("DELETE FROM telemetry_samples USING telemetry_rekey r WHERE telemetry_samples.metric=r.metric AND telemetry_samples.time=r.old_time");
                InsertRows("telemetry_samples", chunk.Select(m => new object?[] { m.Old.Metric, m.Old.EntityId, m.Time, m.Old.Value, m.Old.Unit, m.Old.Source, m.Old.RawState, m.Old.RawUnit, m.Old.SourceUpdatedAt, m.Old.AttributesJson, m.Old.Status }));
            });
        }
        Transaction(() => Execute("INSERT INTO store_migrations VALUES (?,?)", marker, Clock.GetUtcNow()));
    }

    /// <summary>Re-derives every interval of a metric. Inside a caller's transaction (a profile change during a save) it runs as one pass;
    /// otherwise in bounded steps.</summary>
    void RebuildAll(string metric, TimeSpan gap, bool inTransaction = false)
    {
        DateTimeOffset? first = null, last = null;
        using (var c = Command("SELECT min(time),max(time) FROM telemetry_samples WHERE metric=?", metric)) using (var r = c.ExecuteReader())
            if (r.Read() && !r.IsDBNull(0)) { first = Stamp(r.GetValue(0)); last = Stamp(r.GetValue(1)); }
        if (inTransaction) { Execute("DELETE FROM telemetry_intervals WHERE metric=?", metric); if (first is not null) DeriveAndStore(metric, first.Value, last!.Value, gap); return; }
        Transaction(() => Execute("DELETE FROM telemetry_intervals WHERE metric=?", metric));
        if (first is not null) RebuildRange(metric, first.Value, last!.Value, gap);
    }

    /// <summary>
    /// Re-derives the intervals touched by samples in [from,to]. The window starts at a usable reading: the last one at or before
    /// the earliest reading within one gap tolerance (so a collapsed sensor update cycle is rebuilt in one piece), looking back up to 36 hours so an
    /// outage is derived as a whole when the sensor returns. It then grows to cover every stored interval it overlaps, re-anchoring
    /// on a usable reading each time, so it never starts in the middle of an outage or a reset split.
    /// </summary>
    void RebuildIntervals(string metric, DateTimeOffset from, DateTimeOffset to, TimeSpan gap)
    {
        DateTimeOffset? Scalar(string sql, params object?[] args) { using var c = Command(sql, args); return c.ExecuteScalar() is { } v && v is not DBNull ? Stamp(v) : null; }
        // The earliest reading within one gap tolerance before the new data: a sensor update cycle collapsed in derivation starts no earlier.
        var anchor = Scalar("SELECT min(time) FROM telemetry_samples WHERE metric=? AND time>=? AND time<?", metric, from - gap, from) ?? from;
        // A heartbeat repeats an earlier Home Assistant state write; start from that write so the run is judged as a whole.
        if (Scalar("SELECT min(s.time) FROM telemetry_samples s JOIN telemetry_samples h ON h.metric=s.metric AND h.time=? AND s.source_updated_at=h.source_updated_at AND s.time<=h.time AND s.time>=? WHERE s.metric=?", anchor, anchor - BridgeLookback, metric) is { } runStart && runStart < anchor) anchor = runStart;
        var start = from;
        var end = Scalar("SELECT min(time) FROM telemetry_samples WHERE metric=? AND time>?", metric, to) ?? to;
        for (var pass = 0; pass < 8; pass++)
        {
            var anchored = Scalar("SELECT max(time) FROM telemetry_samples WHERE metric=? AND time<=? AND time<? AND time>=? AND status='observed' AND value IS NOT NULL", metric, anchor, from, anchor - BridgeLookback)
                ?? Scalar("SELECT max(time) FROM telemetry_samples WHERE metric=? AND time<=? AND time<?", metric, anchor, from) ?? anchor;
            if (anchored < start) start = anchored;
            DateTimeOffset? earlier = null;
            using (var c = Command("SELECT min(start_time),max(end_time) FROM telemetry_intervals WHERE metric=? AND start_time<? AND end_time>?", metric, end, start)) using (var r = c.ExecuteReader())
                if (r.Read() && !r.IsDBNull(0)) { var s = Stamp(r.GetValue(0)); var e = Stamp(r.GetValue(1)); if (s < start) earlier = s; if (e > end) end = e; }
            if (earlier is null) break;
            start = earlier.Value; anchor = earlier.Value;
        }
        DeriveAndStore(metric, start, end, gap);
    }

    void DeriveAndStore(string metric, DateTimeOffset from, DateTimeOffset to, TimeSpan gap)
    {
        var points = SamplesInternal(from, to.AddMicroseconds(1), metric, 0, int.MaxValue);
        var profile = ProfileFor(metric);
        var context = new DerivationContext(profile, gap, telemetryZone)
        {
            SunUp = profile == SensorProfiles.SolarDaily ? SunUpFunction(from, to) : null,
            RateEndingAt = at =>
            {
                using var c = Command("SELECT energy_kwh,start_time,end_time FROM telemetry_intervals WHERE metric=? AND end_time=? AND status='observed' AND energy_kwh IS NOT NULL AND method NOT IN ('idle','reset_after_idle') LIMIT 1", metric, at);
                using var r = c.ExecuteReader();
                return r.Read() ? r.GetDouble(0) / (Stamp(r.GetValue(2)) - Stamp(r.GetValue(1))).TotalHours : null;
            }
        };
        var intervals = IntervalDerivation.Derive(metric, points, context);
        if (metric is "grid_import" or "grid_export" && intervals.Count > 0) PriceIntervals(metric == "grid_import" ? "import_tariff" : "export_tariff", intervals, gap);
        // Intervals starting exactly at the window end belong to the next stretch and are kept.
        Execute("DELETE FROM telemetry_intervals WHERE metric=? AND start_time<? AND end_time>?", metric, to, from);
        Execute("DELETE FROM telemetry_intervals WHERE metric=? AND start_time>=? AND end_time<=?", metric, from, to);
        InsertRows("telemetry_intervals", intervals.Select(x => new object?[] { x.Metric, x.Start, x.End, x.Energy, x.Source, x.Status, metric == "grid_import" ? x.Cost : null, metric == "grid_export" ? x.Cost : null, x.Method, x.CostMethod, x.MissingKwh }),
            "metric,start_time,end_time,energy_kwh,source,status,import_cost_gbp,export_credit_gbp,method,cost_method,missing_kwh");
    }

    /// <summary>
    /// From Predbat's PV forecast (the latest captured row per slot): the first instant in a range when solar was expected, meaning a
    /// slot forecasting more than 0.01 kWh or a stretch with no forecast at all. Null when every slot in the range forecast no solar.
    /// </summary>
    Func<DateTimeOffset, DateTimeOffset, DateTimeOffset?> SunUpFunction(DateTimeOffset from, DateTimeOffset to)
    {
        var slots = new List<(DateTimeOffset Start, DateTimeOffset End, double Pv)>();
        using (var c = Command("SELECT time,duration_minutes,pv_forecast FROM (SELECT *,row_number() OVER(PARTITION BY time ORDER BY captured_at DESC,snapshot_id DESC) rn FROM plan_slots WHERE duration_minutes>0 AND time>=? AND time<?) WHERE rn=1 ORDER BY time", from.AddHours(-1), to.AddHours(1)))
        using (var r = c.ExecuteReader()) while (r.Read()) { var s = Stamp(r.GetValue(0)); slots.Add((s, s.AddMinutes(r.GetInt32(1)), r.GetDouble(2))); }
        return (a, b) =>
        {
            var cursor = a;
            foreach (var s in slots)
            {
                if (s.End <= cursor) continue;
                if (cursor >= b) break;
                if (s.Start > cursor) return cursor;
                if (s.Pv > .01) return s.Start > a ? s.Start : a;
                cursor = s.End;
            }
            return cursor >= b ? null : cursor;
        };
    }

    /// <summary>Prices grid intervals at the tariff in force, split at rate changes; Predbat's plan rates fill tariff outages.</summary>
    void PriceIntervals(string tariffMetric, List<DerivedInterval> intervals, TimeSpan gap)
    {
        var from = intervals.Min(x => x.Start); var to = intervals.Max(x => x.End);
        var samples = SamplesInternal(from - gap - gap, to + gap, tariffMetric, 0, int.MaxValue);
        var schedule = new TariffSchedule(samples, gap);
        var priced = intervals.Where(x => x.Energy is not null).Select(x => schedule.Price(x.Start, x.End, x.Energy!.Value)).ToList();
        if (priced.Any(x => x.Cost is null))
        {
            schedule = new TariffSchedule(samples, gap, PlanRates(tariffMetric == "import_tariff" ? "import_rate" : "export_rate", from, to));
            priced = intervals.Where(x => x.Energy is not null).Select(x => schedule.Price(x.Start, x.End, x.Energy!.Value)).ToList();
        }
        var k = 0;
        foreach (var interval in intervals.Where(x => x.Energy is not null)) { interval.Cost = priced[k].Cost; interval.CostMethod = priced[k].Method; k++; }
    }
    List<(DateTimeOffset From, DateTimeOffset To, double Rate)> PlanRates(string column, DateTimeOffset from, DateTimeOffset to)
    {
        var rates = new List<(DateTimeOffset, DateTimeOffset, double)>();
        using var c = Command($"SELECT time,duration_minutes,{column} FROM (SELECT *,row_number() OVER(PARTITION BY time ORDER BY captured_at DESC,snapshot_id DESC) rn FROM plan_slots WHERE duration_minutes>0 AND time>=? AND time<? AND {column} IS NOT NULL) WHERE rn=1 ORDER BY time", from.AddHours(-1), to);
        using var r = c.ExecuteReader();
        while (r.Read()) { var s = Stamp(r.GetValue(0)); var rate = r.GetDouble(2); if (double.IsFinite(rate)) rates.Add((s, s.AddMinutes(r.GetInt32(1)), rate)); }
        return rates;
    }

    // ---- reading samples ----

    public List<TelemetrySample> ReadTelemetrySamples(DateTimeOffset from, DateTimeOffset to, string? metric = null, int offset = 0, int limit = 200)
    {
        if (to < from || offset < 0 || limit is < 1 or > 1000) throw new DomainException("Invalid telemetry page.", 400);
        lock (gate) return SamplesInternal(from, to, metric, offset, limit);
    }
    List<TelemetrySample> SamplesInternal(DateTimeOffset from, DateTimeOffset to, string? metric, int offset, int limit)
    {
        using var c = Command("SELECT * FROM telemetry_samples WHERE time>=? AND time<? AND (? IS NULL OR metric=?) ORDER BY time LIMIT ? OFFSET ?", from, to, metric, metric, limit, offset); using var r = c.ExecuteReader(); var result = new List<TelemetrySample>();
        while (r.Read()) result.Add(ReadSample(r));
        return result;
    }

    /// <summary>
    /// The latest reading per metric, from memory. Values are kept however old they are; AgeSeconds and Stale report age.
    /// For a non-observed latest reading, LastObservedValue/At give the last usable one, and Expected/Reason say whether the state is normal.
    /// </summary>
    public Dictionary<string, LatestTelemetry> ReadLatestTelemetry(TimeSpan? staleAfter = null)
    {
        lock (gate)
        {
            LoadLatestCaches();
            var now = Clock.GetUtcNow(); var result = new Dictionary<string, LatestTelemetry>();
            foreach (var (metric, s) in latestSamples!)
            {
                var observed = latestObserved!.GetValueOrDefault(metric);
                var profile = TelemetrySchema.EnergyMetrics.Contains(metric) ? ProfileFor(metric) : SensorProfiles.Default(metric);
                var (expected, reason) = Explain(metric, s, observed, profile, now);
                var age = Math.Max(0, (now - s.Time).TotalSeconds);
                result[metric] = new LatestTelemetry(s.Value, s.Unit, s.Time, s.Status, s.EntityId, s.Source, s.RawState, s.RawUnit, s.SourceUpdatedAt)
                {
                    LastObservedValue = observed?.Value, LastObservedAt = observed?.Time, Expected = expected, Reason = reason,
                    AgeSeconds = Math.Round(age), Stale = age > (staleAfter ?? DefaultGap).TotalSeconds, Profile = profile
                };
            }
            return result;
        }
    }

    /// <summary>Whether a sensor's latest state is normal for its profile, and a plain-English reason when it is not a fresh reading.</summary>
    (bool Expected, string? Reason) Explain(string metric, TelemetrySample latest, TelemetrySample? observed, string profile, DateTimeOffset now)
    {
        string Since(DateTimeOffset? at) => at is { } t ? $" since {TimeZoneInfo.ConvertTime(t, telemetryZone):HH:mm}" : "";
        switch (latest.Status)
        {
            case "observed": return (true, null);
            case "not_found": return (false, "Sensor not found in Home Assistant: check the entity mapping.");
            case "unsupported_unit": return (false, $"Unsupported unit '{latest.RawUnit}': map an energy (kWh/Wh), percentage or price sensor.");
            case "invalid": return (false, "Home Assistant reported a value that is not a number.");
            case "idle":
                if (profile == SensorProfiles.SessionCounter) return (true, "Not charging (the charger reports unknown between sessions).");
                var startedToday = observed is { } o && StartedSinceReset(o, latest);
                if (profile == SensorProfiles.SolarDaily && !startedToday)
                {
                    // Asleep is normal until Predbat's forecast since sunrise adds up to a real amount of generation.
                    if (SolarExpectation(latest) is { ForecastKwh: >= SolarAwakeForecastKwh } e)
                        return (false, $"Solar meter still reports unknown, though Predbat forecast {e.ForecastKwh.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)} kWh since {TimeZoneInfo.ConvertTime(e.Since, telemetryZone):HH:mm}.");
                    return (true, "Solar meter asleep (normal overnight); counted as 0.");
                }
                if (profile == SensorProfiles.DailyCounter && !startedToday) return (true, metric == "grid_export" ? "No export yet today; counted as 0." : "No reading yet today; counted as 0.");
                return (false, $"Sensor reports unknown{Since(observed?.Time)}.");
            default:
                return (false, $"Device offline{Since(observed?.Time)}.");
        }
    }
    /// <summary>
    /// True when the last usable reading belongs to the counter's current day (after the latest reset). This is decided from the
    /// reading itself, never its poll time: a heartbeat polled at 00:00:49 still carries yesterday's value, last_reset and last_updated
    /// when Home Assistant resets the counter a minute or two after midnight.
    /// </summary>
    bool StartedSinceReset(TelemetrySample observed, TelemetrySample latest)
    {
        var reset = Attributes.LastReset(latest.AttributesJson) ?? CurrentDayStart(latest.Time);
        if (Attributes.LastReset(observed.AttributesJson) is { } own) return own >= reset;
        var updated = observed.SourceUpdatedAt is { } u && u <= observed.Time ? u : observed.Time;
        return updated >= reset;
    }
    DateTimeOffset CurrentDayStart(DateTimeOffset at) => CivilTime.FirstValidInstant(TimeZoneInfo.ConvertTime(at, telemetryZone).Date, telemetryZone);

    /// <summary>Solar still unknown is a fault only once Predbat's forecast since it was first expected reaches this (live dawns forecast 0.05 kWh per half hour).</summary>
    public const double SolarAwakeForecastKwh = .2;
    /// <summary>
    /// For a solar meter that has not reported today: when Predbat first expected generation since the day's reset (a slot forecasting
    /// more than 0.01 kWh, latest captured forecast per slot) and how much it forecast from then until the latest sample. Null when no
    /// forecast covers the stretch or every slot in it forecast nothing.
    /// </summary>
    (DateTimeOffset Since, double ForecastKwh)? SolarExpectation(TelemetrySample latest)
    {
        var dayStart = CurrentDayStart(latest.Time);
        var reset = Attributes.LastReset(latest.AttributesJson) is { } r && r <= latest.Time && r >= dayStart.AddHours(-2) ? r : dayStart;
        using var c = Command("SELECT time,duration_minutes,pv_forecast FROM (SELECT *,row_number() OVER(PARTITION BY time ORDER BY captured_at DESC,snapshot_id DESC) rn FROM plan_slots WHERE duration_minutes>0 AND time<? AND time>=?) WHERE rn=1 ORDER BY time", latest.Time, reset.AddHours(-1));
        using var reader = c.ExecuteReader();
        DateTimeOffset? since = null; double total = 0;
        while (reader.Read())
        {
            var start = Stamp(reader.GetValue(0)); var end = start.AddMinutes(reader.GetInt32(1)); var pv = reader.GetDouble(2);
            var from = start > reset ? start : reset; var to = end < latest.Time ? end : latest.Time;
            if (to <= from || !(pv > .01) || !double.IsFinite(pv)) continue;
            since ??= from;
            total += pv * (to - from).TotalSeconds / (end - start).TotalSeconds;
        }
        return since is { } s ? (s, total) : null;
    }

    /// <summary>How long a metric has been without a usable reading, measured to its latest sample.</summary>
    public TimeSpan? OutageDuration(string metric)
    {
        lock (gate)
        {
            var latest = LatestSample(metric); if (latest is null || latest.Status == "observed") return null;
            var observed = LatestObserved(metric);
            // Solar that has not woken up today: the outage counts from when Predbat first expected generation, not from last evening.
            if (latest.Status == "idle" && TelemetrySchema.EnergyMetrics.Contains(metric) && ProfileFor(metric) == SensorProfiles.SolarDaily && !(observed is { } o && StartedSinceReset(o, latest)))
                return SolarExpectation(latest) is { } e ? latest.Time - e.Since : TimeSpan.Zero;
            if (observed is not null) return latest.Time - observed.Time;
            using var c = Command("SELECT min(time) FROM telemetry_samples WHERE metric=?", metric);
            return c.ExecuteScalar() is { } v && v is not DBNull ? latest.Time - Stamp(v) : TimeSpan.Zero;
        }
    }
    /// <summary>Whether the latest non-observed state of a metric is normal for its profile (see <see cref="ReadLatestTelemetry"/>).</summary>
    public bool OutageExpected(string metric)
    {
        lock (gate)
        {
            var latest = LatestSample(metric); if (latest is null) return true;
            var profile = TelemetrySchema.EnergyMetrics.Contains(metric) ? ProfileFor(metric) : SensorProfiles.Default(metric);
            // Solar not yet awake today is judged against Predbat's forecast inside Explain.
            return Explain(metric, latest, LatestObserved(metric), profile, Clock.GetUtcNow()).Expected;
        }
    }

    public PlanPage ListPlans(DateTimeOffset? from, DateTimeOffset? to, int offset = 0, int limit = 100)
    {
        if (offset < 0 || limit is < 1 or > 200 || from > to) throw new DomainException("Invalid plan page.", 400);
        lock (gate)
        {
            const string where = "WHERE (? IS NULL OR recorded_at>=?) AND (? IS NULL OR recorded_at<?)";
            using var count = Command("SELECT count(*) FROM plans " + where, from, from, to, to); var total = Convert.ToInt64(count.ExecuteScalar());
            using var cmd = Command("SELECT id,recorded_at,source,collected_at FROM plans " + where + " ORDER BY recorded_at DESC,id DESC LIMIT ? OFFSET ?", from, from, to, to, limit, offset); using var r = cmd.ExecuteReader(); var items = new List<PlanReference>();
            while (r.Read()) items.Add(new(r.GetString(0), Stamp(r.GetValue(1)), r.GetString(2), r.IsDBNull(3) ? null : Stamp(r.GetValue(3))));
            return new(items, total, offset, limit);
        }
    }
}
