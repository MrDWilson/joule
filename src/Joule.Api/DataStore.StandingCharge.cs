namespace Joule;

/// <summary>
/// Standing charges, stored per local day because the rate changes (usually each quarter). A day's row comes from the standing charge
/// sensor (its latest reading that day wins) or, without a sensor, from the manual figure in Setup. A sensor row is never replaced by a
/// manual one. Days with no row take the nearest earlier day's rate, or, before the first row, the first rate recorded.
/// </summary>
public partial class DataStore
{
    List<StandingChargeDay>? standingCharges;
    StandingChargePreferences? standingChargePreferences;

    void InitializeStandingCharges() => Execute("""
        CREATE TABLE IF NOT EXISTS standing_charges(day DATE PRIMARY KEY, pence_per_day DOUBLE NOT NULL, source VARCHAR NOT NULL, entity_id VARCHAR, recorded_at TIMESTAMPTZ);
        CREATE TABLE IF NOT EXISTS standing_charge_preferences(id INTEGER PRIMARY KEY, manual_pence_per_day DOUBLE, include_in_net BOOLEAN NOT NULL, updated_at TIMESTAMPTZ);
        """);

    public StandingChargePreferences ReadStandingChargePreferences()
    {
        lock (gate)
        {
            if (standingChargePreferences is { } cached) return cached;
            using var c = Command("SELECT manual_pence_per_day, include_in_net FROM standing_charge_preferences WHERE id=1");
            using var r = c.ExecuteReader();
            return standingChargePreferences = r.Read() ? new(r.IsDBNull(0) ? null : r.GetDouble(0), r.GetBoolean(1)) : new(null, true);
        }
    }

    /// <summary>Saves the owner's choices. A manual figure is recorded for today at once (unless today already has a sensor reading).</summary>
    public StandingChargePreferences SaveStandingChargePreferences(StandingChargeRequest request)
    {
        if (request.ManualPencePerDay is { } p && (!double.IsFinite(p) || p < 0 || p > 1000))
            throw new DomainException("Enter the standing charge in pence per day, between 0 and 1000 (for example 53.5).", 400);
        lock (gate)
        {
            var current = ReadStandingChargePreferences();
            var next = new StandingChargePreferences(request.ClearManual ? null : request.ManualPencePerDay is { } m ? Math.Round(m, 4) : current.ManualPencePerDay, request.IncludeInNet ?? current.IncludeInNet);
            Execute("INSERT OR REPLACE INTO standing_charge_preferences VALUES (1,?,?,?)", next.ManualPencePerDay, next.IncludeInNet, Clock.GetUtcNow());
            standingChargePreferences = next;
            if (request.ManualPencePerDay is not null && next.ManualPencePerDay is { } manual) RecordStandingCharge(LocalDay(Clock.GetUtcNow()), manual, "manual", null);
            return next;
        }
    }

    DateOnly LocalDay(DateTimeOffset at) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(at, telemetryZone).Date);

    /// <summary>Records a day's rate. A sensor reading replaces whatever the day had; a manual figure never replaces a sensor reading.</summary>
    public void RecordStandingCharge(DateOnly day, double pencePerDay, string source, string? entityId)
    {
        if (!double.IsFinite(pencePerDay) || pencePerDay < 0) return;
        lock (gate)
        {
            var existing = StandingChargeRows().FirstOrDefault(x => x.Day == day);
            if (existing is not null && (existing.Source == "sensor" && source != "sensor" || existing.PencePerDay == pencePerDay && existing.Source == source && existing.EntityId == entityId)) return;
            Execute("INSERT OR REPLACE INTO standing_charges VALUES (?,?,?,?,?)", day.ToDateTime(TimeOnly.MinValue), pencePerDay, source, entityId, Clock.GetUtcNow());
            standingCharges = null;
        }
    }

    /// <summary>Called with every stored batch of readings: each observed standing charge reading sets its local day's rate.</summary>
    void RecordStandingChargeSamples(IEnumerable<TelemetrySample> samples)
    {
        foreach (var s in samples.Where(x => x.Metric == StandingCharge.Metric && x.Status == "observed" && x.Value is not null).OrderBy(x => x.Time))
            RecordStandingCharge(LocalDay(s.Time), s.Value!.Value, "sensor", s.EntityId);
    }

    /// <summary>Once per collection: without a sensor reading today, today takes the manual figure (when there is one).</summary>
    public void RecordManualStandingCharge()
    {
        lock (gate)
        {
            if (ReadStandingChargePreferences().ManualPencePerDay is { } manual) RecordStandingCharge(LocalDay(Clock.GetUtcNow()), manual, "manual", null);
        }
    }

    List<StandingChargeDay> StandingChargeRows()
    {
        if (standingCharges is { } cached) return cached;
        using var c = Command("SELECT day, pence_per_day, source, entity_id, recorded_at FROM standing_charges ORDER BY day");
        using var r = c.ExecuteReader(); var rows = new List<StandingChargeDay>();
        while (r.Read()) rows.Add(new(r.GetValue(0) switch { DateOnly d => d, DateTime d => DateOnly.FromDateTime(d), var v => DateOnly.Parse(v.ToString()!, System.Globalization.CultureInfo.InvariantCulture) }, r.GetDouble(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3), r.IsDBNull(4) ? Clock.GetUtcNow() : Stamp(r.GetValue(4))));
        return standingCharges = rows;
    }

    /// <summary>The rate for a local day: its own row, else the nearest earlier day's, else (before any row) the first recorded, marked assumed.</summary>
    (double Pence, string Source, bool Assumed)? StandingChargeRate(DateOnly day)
    {
        var rows = StandingChargeRows();
        if (rows.Count == 0) return null;
        StandingChargeDay? earlier = null;
        foreach (var row in rows) { if (row.Day > day) break; earlier = row; }
        return earlier is { } e ? (e.PencePerDay, e.Source, false) : (rows[0].PencePerDay, rows[0].Source, true);
    }

    /// <summary>
    /// The standing charge for a window, clipped to the time Joule has measured (from its first reading to now), so a day before Joule
    /// started or the rest of today never carries a charge the energy figures don't. Null when no rate is known.
    /// </summary>
    StandingChargeAmount? StandingChargeFor(DateTimeOffset from, DateTimeOffset to)
    {
        var first = FirstObservationAt(); if (first is null) return null;
        var start = first > from ? first.Value : from; var now = Clock.GetUtcNow(); var end = to < now ? to : now;
        return StandingCharge.Prorate(start, end, telemetryZone, StandingChargeRate);
    }

    public StandingChargeView ReadStandingCharge(string? entity, string? origin)
    {
        lock (gate)
        {
            var prefs = ReadStandingChargePreferences();
            var sample = LatestSample(StandingCharge.Metric);
            var observed = LatestObserved(StandingCharge.Metric);
            var today = StandingChargeRate(LocalDay(Clock.GetUtcNow()));
            var rows = StandingChargeRows();
            return new(prefs.IncludeInNet, prefs.ManualPencePerDay, entity, origin, observed?.Value, observed?.Time, sample?.Status, today?.Pence, today?.Source, rows.TakeLast(14).Reverse().ToList());
        }
    }
}
