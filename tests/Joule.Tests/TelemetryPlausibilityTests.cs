using System.Globalization;
using Joule;
using Xunit;

namespace Joule.Tests;

/// <summary>
/// A counter that falls is only credited with its whole new reading when that much energy could have flowed since the previous one
/// (at most <see cref="DerivationContext.MaxPlausibleKw"/>). A charger meter that reports a lifetime total must never have its total
/// counted as one charging session.
/// </summary>
public sealed class TelemetryPlausibilityTests : IDisposable
{
    readonly string path = Path.Combine(Path.GetTempPath(), "predbat-plausible-" + Guid.NewGuid().ToString("N"));
    static readonly TimeZoneInfo London = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");
    static readonly DateTimeOffset T = DateTimeOffset.Parse("2026-10-04T08:00:00Z");
    static readonly DerivationContext Session = new(SensorProfiles.SessionCounter, TimeSpan.FromMinutes(15), London);
    static readonly DerivationContext Lifetime = new(SensorProfiles.LifetimeCounter, TimeSpan.FromMinutes(15), London);

    static TelemetrySample Ev(DateTimeOffset at, double? value, string entity = "sensor.charger_total") => value is null
        ? new("ev", entity, at, null, "", "HomeAssistant", "unknown", "", at, "{\"state_class\":\"total_increasing\"}", "idle")
        : new("ev", entity, at, value, "kWh", "HomeAssistant", value.Value.ToString(CultureInfo.InvariantCulture), "kWh", at, "{\"state_class\":\"total_increasing\"}");

    /// <summary>The reviewer's reproduction: 2000 → 2006.5 kWh, a 0.002 kWh dip, 45 minutes of "unknown", then 2006.5 again.</summary>
    static List<TelemetrySample> LifetimeChargerWithDipAndUnknown()
    {
        var samples = Enumerable.Range(0, 14).Select(i => Ev(T.AddMinutes(5 * i), 2000 + .5 * i)).ToList();
        var dip = T.AddMinutes(70);
        samples.Add(Ev(dip, 2006.498));
        for (var i = 1; i <= 9; i++) samples.Add(Ev(dip.AddMinutes(5 * i), null));
        samples.Add(Ev(dip.AddMinutes(50), 2006.5));
        return samples;
    }
    static double Total(IEnumerable<DerivedInterval> intervals) => intervals.Sum(x => x.Energy ?? 0);

    [Fact]
    public void AChargerLifetimeTotalIsDetectedAsALifetimeCounterAndCountedOnce()
    {
        using var db = new DataStore(path);
        foreach (var s in LifetimeChargerWithDipAndUnknown()) db.SaveTelemetry([s]);
        Assert.Equal(SensorProfiles.LifetimeCounter, db.ProfileFor("ev"));
        var ev = db.ReadEnergySummary(T, T.AddMinutes(120)).Metrics["ev"];
        // Was 4018.5 kWh: one 2006 kWh "session_start" and one 2006.5 kWh "spread".
        Assert.InRange(ev.EnergyKwh!.Value, 6.45, 6.55);
        Assert.Equal(0, Convert.ToInt32(db.Query("SELECT count(*) n FROM telemetry_intervals WHERE metric='ev' AND energy_kwh>1")[0]["n"]));
    }

    [Fact]
    public void ALifetimeTotalTreatedAsASessionCounterIsStillNeverCreditedInFull()
    {
        // Even with the profile forced to session_counter (a configuration mistake), neither the direct dip nor the dip followed by
        // an idle run longer than two gap tolerances may credit the whole reading.
        var direct = IntervalDerivation.Derive("ev", [Ev(T, 2006.5), Ev(T.AddMinutes(5), 2006.498)], Session);
        Assert.Equal(0, Total(direct), 9); Assert.Equal("flat", Assert.Single(direct).Method);

        var outage = IntervalDerivation.Derive("ev", LifetimeChargerWithDipAndUnknown(), Session);
        Assert.InRange(Total(outage), 6.45, 6.55);
        Assert.All(outage, x => Assert.True((x.Energy ?? 0) < 1, $"{x.Start:HH:mm}-{x.End:HH:mm} {x.Energy} {x.Status}/{x.Method}"));
    }

    [Fact]
    public void AnImplausibleDropOnALifetimeMeterIsAnUnknownResetNotItsReading()
    {
        // 2000 → 900 kWh in five minutes (a replaced meter): crediting 900 kWh would mean 10.8 MW.
        var intervals = IntervalDerivation.Derive("grid_import", [Ev(T, 2000), Ev(T.AddMinutes(5), 900)], Lifetime);
        var reset = Assert.Single(intervals);
        Assert.Null(reset.Energy); Assert.Equal("reset", reset.Status);
        // A drop across a short outage is held to the same limit.
        var outage = IntervalDerivation.Derive("grid_import", [Ev(T, 2000), Ev(T.AddMinutes(5), null) with { Status = "unavailable", RawState = "unavailable" }, Ev(T.AddMinutes(30), 900)], Lifetime);
        Assert.All(outage, x => Assert.Null(x.Energy));
    }

    [Fact]
    public void RealSessionStartsAndResetsAreStillCredited()
    {
        // A new session 0.3 kWh in, five minutes after the last one ended at 10.9 kWh.
        var start = Assert.Single(IntervalDerivation.Derive("ev", [Ev(T, 10.9), Ev(T.AddMinutes(5), .3)], Session));
        Assert.Equal(.3, start.Energy!.Value, 9); Assert.Equal("session_start", start.Method);
        // The next session after an hour of unknown.
        var samples = new List<TelemetrySample> { Ev(T, 10.9) };
        for (var i = 1; i <= 12; i++) samples.Add(Ev(T.AddMinutes(5 * i), null));
        samples.Add(Ev(T.AddMinutes(65), .45));
        Assert.Equal(.45, Total(IntervalDerivation.Derive("ev", samples, Session)), 9);
        // A lifetime meter that restarted from zero.
        var restart = Assert.Single(IntervalDerivation.Derive("grid_import", [Ev(T, 2000), Ev(T.AddMinutes(5), .2)], Lifetime));
        Assert.Equal(.2, restart.Energy!.Value, 9); Assert.Equal("reset", restart.Method);
    }

    [Fact]
    public void ChargerReadingsThatNeverFallNearZeroAreALifetimeTotal()
    {
        Assert.Equal(SensorProfiles.LifetimeCounter, SensorProfiles.Detect("ev", LifetimeChargerWithDipAndUnknown(), London));
        // A session counter that happens to be mid-session with a few kWh is still a session counter.
        Assert.Equal(SensorProfiles.SessionCounter, SensorProfiles.Detect("ev", [Ev(T, 4.2), Ev(T.AddMinutes(5), 4.8), Ev(T.AddMinutes(10), null)], London));
    }

    [Fact]
    public void AZeroRateTailBeforeAResetIsMeasuredFlatNotEstimated()
    {
        using var db = new DataStore(path);
        var midnight = DateTimeOffset.Parse("2026-10-04T23:00:00Z");
        string Reset(DateTimeOffset m) => $"{{\"state_class\":\"total\",\"last_reset\":\"{TimeZoneInfo.ConvertTime(m, London):yyyy-MM-dd'T'HH:mm:sszzz}\"}}";
        TelemetrySample Pv(DateTimeOffset at, double? value) => value is null
            ? new("pv", "sensor.solar_today", at, null, "", "HomeAssistant", "unknown", "", at, Reset(midnight), "idle")
            : new("pv", "sensor.solar_today", at, value, "kWh", "HomeAssistant", value.Value.ToString(CultureInfo.InvariantCulture), "kWh", at, Reset(midnight.AddDays(-1)));
        db.SavePlan(new PlanSnapshot { Source = "Predbat", At = midnight.AddHours(-2), CollectedAt = midnight.AddHours(-2), Slots = Enumerable.Range(0, 12).Select(i => new PlanSlot(midnight.AddMinutes(-120 + 30 * i), .3, null, 0, null, 50, null, 25, 15, "Demand", 0)).ToList() });
        // Solar sat at 9.398 kWh all evening, then reset at midnight: the minutes before midnight are a measured zero.
        foreach (var at in Enumerable.Range(0, 7).Select(i => midnight.AddMinutes(-33 + 5 * i))) db.SaveTelemetry([Pv(at, 9.398)]);
        for (var i = 0; i < 6; i++) db.SaveTelemetry([Pv(midnight.AddMinutes(2 + 5 * i), null)]);
        var evening = db.ReadEnergySummary(midnight.AddMinutes(-30), midnight).Metrics["pv"];
        Assert.Equal(0, evening.EnergyKwh!.Value, 9); Assert.Null(evening.EstimatedKwh);
        Assert.Equal("observed", (string)db.Query("SELECT status FROM telemetry_intervals WHERE metric='pv' AND end_time=TIMESTAMPTZ '2026-10-04T23:00:00Z'")[0]["status"]!);
        Assert.All(db.ReadMeasuredHistory(midnight.AddMinutes(-60), midnight.AddMinutes(30), 30).Slots, s => Assert.NotEqual("estimated", s.PvStatus));
    }

    public void Dispose() { if (Directory.Exists(path)) Directory.Delete(path, true); }
}
