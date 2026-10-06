using System.Globalization;
using Joule;
using Xunit;

namespace Joule.Tests;

/// <summary>
/// Counter resets, Home Assistant "unknown" (idle) runs and short outages, using the shapes seen on the live installation
/// (Europe/London, BST: local midnight is 23:00 UTC). Samples are saved one poll per call, the way the collector writes them.
/// </summary>
public sealed class TelemetryResetAndIdleTests : IDisposable
{
    readonly string path = Path.Combine(Path.GetTempPath(), "predbat-reset-idle-" + Guid.NewGuid().ToString("N"));
    static readonly DateTimeOffset Midnight = DateTimeOffset.Parse("2026-10-04T23:00:00Z"); // 5 Oct 00:00 BST
    static readonly DateTimeOffset PreviousMidnight = Midnight.AddDays(-1);

    static string Attributes(DateTimeOffset? lastReset, string stateClass = "total") =>
        lastReset is { } r ? $"{{\"state_class\":\"{stateClass}\",\"last_reset\":\"{TimeZoneInfo.ConvertTime(r, TimeZoneInfo.FindSystemTimeZoneById("Europe/London")):yyyy-MM-dd'T'HH:mm:sszzz}\"}}" : $"{{\"state_class\":\"{stateClass}\"}}";
    /// <summary>A daily counter reading, reset at the local midnight before <paramref name="at"/>.</summary>
    static TelemetrySample Daily(string metric, DateTimeOffset at, double? value, string entity = "sensor.meter") =>
        Reading(metric, at, value, entity, at >= Midnight ? Midnight : PreviousMidnight);
    static TelemetrySample Reading(string metric, DateTimeOffset at, double? value, string entity, DateTimeOffset? lastReset, string? status = null, DateTimeOffset? updated = null, string stateClass = "total") => value is null
        ? new(metric, entity, at, null, "", "HomeAssistant", status == "unavailable" ? "unavailable" : "unknown", "", updated ?? at, Attributes(lastReset, stateClass), status ?? "idle")
        : new(metric, entity, at, value, "kWh", "HomeAssistant", value.Value.ToString(CultureInfo.InvariantCulture), "kWh", updated ?? at, Attributes(lastReset, stateClass));
    static void OnePollAtATime(DataStore db, IEnumerable<TelemetrySample> samples) { foreach (var s in samples.OrderBy(x => x.Time)) db.SaveTelemetry([s]); }

    [Fact]
    public void OvernightSolarUnknownIsBridgedWhenReadingsArriveOnePollAtATime()
    {
        using var db = new DataStore(path);
        // Live 4–5 Oct: 9.398 kWh at 22:57, "unknown" from 23:02 to 06:38, then 0.001 kWh at 06:43 (UTC).
        var samples = new List<TelemetrySample> { Daily("pv", Midnight.AddMinutes(-3), 9.398, "sensor.solar_today") };
        for (var i = 0; i < 90; i++) samples.Add(Daily("pv", Midnight.AddMinutes(2 + 5 * i), null, "sensor.solar_today"));
        samples.Add(Daily("pv", Midnight.AddMinutes(2 + 5 * 90), .001, "sensor.solar_today"));
        OnePollAtATime(db, samples);
        var night = db.ReadEnergySummary(Midnight, samples[^1].Time).Metrics["pv"];
        Assert.Equal(.001, night.EnergyKwh!.Value, 9);
        Assert.Equal(1, night.CoverageFraction, 6); Assert.Empty(night.Gaps); Assert.Equal(0, night.MissingIntervals);
        Assert.Contains(night.State, new[] { "complete", "idle_zero" });
        Assert.All(db.ReadMeasuredHistory(Midnight, Midnight.AddHours(7), 30).Slots, s => Assert.Equal(0, s.Pv!.Value, 3));
        // Every interval from midnight to the returning reading is measured.
        var statuses = db.Query("SELECT DISTINCT status FROM telemetry_intervals WHERE metric='pv' AND start_time>=TIMESTAMPTZ '2026-10-04T23:00:00Z'").Select(r => (string)r["status"]!).ToList();
        Assert.Equal(["observed"], statuses);
    }

    [Fact]
    public void MidnightResetSplitsAtLastResetWithAnEstimatedTailBefore()
    {
        using var db = new DataStore(path);
        // Live: load 61.069 at 22:47, 61.109 at 22:52, then 0.030 at 23:02 (energy since midnight), climbing again.
        OnePollAtATime(db, [Daily("load", Midnight.AddMinutes(-18), 61.029), Daily("load", Midnight.AddMinutes(-13), 61.069), Daily("load", Midnight.AddMinutes(-8), 61.109),
            Daily("load", Midnight.AddMinutes(2), .030), Daily("load", Midnight.AddMinutes(7), .070), Daily("load", Midnight.AddMinutes(12), .110)]);
        var before = db.ReadEnergySummary(Midnight.AddMinutes(-18), Midnight).Metrics["load"];
        // 0.08 kWh measured, then an 8-minute tail at the previous rate (0.04 kWh per 5 minutes) flagged as estimated.
        Assert.Equal(.08 + .04 * 8 / 5, before.EnergyKwh!.Value, 6); Assert.Equal(.04 * 8 / 5, before.EstimatedKwh!.Value, 6);
        var after = db.ReadEnergySummary(Midnight, Midnight.AddMinutes(12)).Metrics["load"];
        Assert.Equal(.110, after.EnergyKwh!.Value, 9); Assert.Equal(1, after.CoverageFraction, 6); Assert.Null(after.EstimatedKwh);
        Assert.Equal(1, Convert.ToInt32(db.Query("SELECT count(*) n FROM telemetry_intervals WHERE metric='load' AND method='reset_split' AND start_time=TIMESTAMPTZ '2026-10-04T23:00:00Z'")[0]["n"]));
    }

    [Fact]
    public void PreviousDayReconcilesWithItsLastReadingPlusTheEstimatedTail()
    {
        using var db = new DataStore(path);
        var samples = new List<TelemetrySample> { Reading("grid_import", PreviousMidnight.AddMinutes(-3), 20, "sensor.meter", PreviousMidnight.AddDays(-1)) };
        for (var m = 0; m <= 23 * 60 + 50; m += 5) samples.Add(Daily("grid_import", PreviousMidnight.AddMinutes(m + 2), .01 * (m + 2)));
        samples.Add(Daily("grid_import", Midnight.AddMinutes(4), .02));
        db.SaveTelemetry(samples);
        var day = db.GetDailySummaries(PreviousMidnight, Midnight)[0].Metrics["grid_import"];
        var lastReading = samples[^2].Value!.Value;
        Assert.Equal(lastReading, day.CounterDayTotalKwh!.Value, 9);
        Assert.True(day.Reconciled);
        // The day total is the counter plus the estimated tail between its last reading (23:52 BST) and midnight.
        Assert.Equal(lastReading + day.EstimatedKwh!.Value, day.EnergyKwh!.Value, 9);
        Assert.InRange(day.EstimatedKwh!.Value, .07, .09);
    }

    [Fact]
    public void ExportUnknownAfterMidnightIsZeroUntilItsFirstReadingWhichLandsInTheFinalPoll()
    {
        using var db = new DataStore(path);
        // Live: export 2.212 at 22:57, "unknown" 23:02–01:42, first reading 0.6176 kWh at 01:47 with last_reset at midnight.
        var samples = new List<TelemetrySample> { Daily("grid_export", Midnight.AddMinutes(-3), 2.212) };
        for (var at = Midnight.AddMinutes(2); at <= Midnight.AddMinutes(162); at = at.AddMinutes(5)) samples.Add(Daily("grid_export", at, null));
        samples.Add(Daily("grid_export", Midnight.AddMinutes(167), .6176)); samples.Add(Daily("grid_export", Midnight.AddMinutes(172), 1.4634));
        OnePollAtATime(db, samples);
        var night = db.ReadEnergySummary(Midnight, Midnight.AddMinutes(172)).Metrics["grid_export"];
        Assert.Equal(1.4634, night.EnergyKwh!.Value, 9); Assert.Equal(1, night.CoverageFraction, 6); Assert.Equal("idle_zero", night.State);
        Assert.True(night.IdleSeconds >= 2.5 * 3600);
        var slots = db.ReadMeasuredHistory(Midnight, Midnight.AddMinutes(150), 30).Slots;
        Assert.Equal(5, slots.Count);
        var final = db.Query("SELECT energy_kwh,method FROM telemetry_intervals WHERE metric='grid_export' AND end_time=TIMESTAMPTZ '2026-10-05T01:47:00Z'")[0];
        Assert.Equal("reset_after_idle", final["method"]); Assert.Equal(.6176, Convert.ToDouble(final["energy_kwh"]), 9);
    }

    [Fact]
    public void SessionCounterCountsItsFirstReadingAfterUnknown()
    {
        using var db = new DataStore(path);
        // Live Hypervolt (Wh, no last_reset): unknown between sessions, then 293 Wh at the first poll of a session.
        TelemetrySample Ev(DateTimeOffset at, double? wh) => wh is null
            ? new("ev", "sensor.hypervolt_session_energy_total_increasing", at, null, "", "HomeAssistant", "unknown", "Wh", at, "{\"state_class\":\"total_increasing\"}", "idle")
            : new("ev", "sensor.hypervolt_session_energy_total_increasing", at, wh / 1000, "kWh", "HomeAssistant", wh.Value.ToString(CultureInfo.InvariantCulture), "Wh", at, "{\"state_class\":\"total_increasing\"}");
        // The previous session ended at 21:00 UTC (4.615 kWh); the charger then read unknown until the next session.
        var samples = new List<TelemetrySample> { Ev(Midnight.AddHours(-2).AddMinutes(-10), 4000), Ev(Midnight.AddHours(-2).AddMinutes(-5), 4615) };
        for (var at = Midnight.AddHours(-2); at < Midnight.AddMinutes(2); at = at.AddMinutes(5)) samples.Add(Ev(at, null));
        samples.AddRange([Ev(Midnight.AddMinutes(2), 293), Ev(Midnight.AddMinutes(7), 909), Ev(Midnight.AddMinutes(12), 1527)]);
        OnePollAtATime(db, samples);
        Assert.Equal(SensorProfiles.SessionCounter, db.ProfileFor("ev"));
        var ev = db.ReadEnergySummary(Midnight.AddHours(-2), Midnight.AddMinutes(12)).Metrics["ev"];
        Assert.Equal(1.527, ev.EnergyKwh!.Value, 9); Assert.Equal(1, ev.CoverageFraction, 6);
        Assert.True(ev.IdleSeconds > 3600, "the charger's unknown before the session is a known zero");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AnyDropOnASessionCounterStartsANewSession(bool idleBetween)
    {
        using var db = new DataStore(path);
        var t = Midnight.AddHours(-6);
        // A previous 0.4 kWh session followed by a first reading of 0.3 kWh: that is a new session (0.3 kWh), not a 0.1 kWh refund.
        var samples = new List<TelemetrySample> { Reading("ev", t, .2, "sensor.charger", null, stateClass: "total_increasing"), Reading("ev", t.AddMinutes(5), .4, "sensor.charger", null, stateClass: "total_increasing") };
        var next = t.AddMinutes(10);
        if (idleBetween) { for (; next < t.AddMinutes(70); next = next.AddMinutes(5)) samples.Add(Reading("ev", next, null, "sensor.charger", null, stateClass: "total_increasing")); }
        samples.Add(Reading("ev", next, .3, "sensor.charger", null, stateClass: "total_increasing"));
        OnePollAtATime(db, samples);
        var ev = db.ReadEnergySummary(t, next).Metrics["ev"];
        Assert.Equal(.2 + .3 + (ev.EstimatedKwh ?? 0), ev.EnergyKwh!.Value, 9);
        Assert.Equal(1, ev.CoverageFraction, 6);
    }

    [Fact]
    public void AHomeAssistantRestartInTheMiddleOfASessionCountsOnlyTheIncrease()
    {
        using var db = new DataStore(path);
        var t = Midnight.AddHours(-6);
        // The charger reads unknown for 25 minutes while Home Assistant restarts, then carries on from 3 kWh to 6 kWh.
        var samples = new List<TelemetrySample> { Reading("ev", t, 2.4, "sensor.charger", null), Reading("ev", t.AddMinutes(5), 3, "sensor.charger", null) };
        for (var m = 10; m <= 30; m += 5) samples.Add(Reading("ev", t.AddMinutes(m), null, "sensor.charger", null));
        samples.Add(Reading("ev", t.AddMinutes(35), 6, "sensor.charger", null));
        OnePollAtATime(db, samples);
        var ev = db.ReadEnergySummary(t, t.AddMinutes(35)).Metrics["ev"];
        Assert.Equal(3.6, ev.EnergyKwh!.Value, 9); Assert.Equal(3, ev.EstimatedKwh!.Value, 9);
    }

    [Fact]
    public void ANewSessionHoursAfterATinyOneIsCountedInFullEvenThoughItReadsMore()
    {
        using var db = new DataStore(path);
        var t = Midnight.AddHours(-20);
        // Live 3 Oct: a 0.017 kWh top-up at 02:45, unknown all day, then the next session's first reading of 0.45 kWh at 23:04.
        var samples = new List<TelemetrySample> { Reading("ev", t, .017, "sensor.charger", null) };
        for (var at = t.AddMinutes(5); at < t.AddHours(20); at = at.AddMinutes(5)) samples.Add(Reading("ev", at, null, "sensor.charger", null));
        samples.Add(Reading("ev", t.AddHours(20).AddMinutes(4), .45, "sensor.charger", null));
        db.SaveTelemetry(samples);
        var ev = db.ReadEnergySummary(t, t.AddHours(20).AddMinutes(4)).Metrics["ev"];
        Assert.Equal(.45, ev.EnergyKwh!.Value - (ev.EstimatedKwh ?? 0), 9); Assert.True(ev.CoverageFraction > .99);
    }

    [Fact]
    public void SessionEndTailIsEstimatedUpToTheSwitchToUnknown()
    {
        using var db = new DataStore(path);
        var t = DateTimeOffset.Parse("2026-10-04T00:19:18Z");
        // Live: 10.324 → 10.963 kWh over five minutes (7.7 kW), last reading at 00:29:18, unknown from 00:30:03 (Home Assistant time).
        OnePollAtATime(db, [Reading("ev", t, 9.685, "sensor.charger", null), Reading("ev", t.AddMinutes(5), 10.324, "sensor.charger", null), Reading("ev", t.AddMinutes(10), 10.963, "sensor.charger", null),
            Reading("ev", t.AddMinutes(15).AddSeconds(10), null, "sensor.charger", null, updated: t.AddMinutes(10).AddSeconds(45)), Reading("ev", t.AddMinutes(20).AddSeconds(10), null, "sensor.charger", null, updated: t.AddMinutes(10).AddSeconds(45))]);
        var ev = db.ReadEnergySummary(t, t.AddMinutes(20).AddSeconds(10)).Metrics["ev"];
        // 45 seconds at 7.67 kW ≈ 0.096 kWh, estimated; the rest is a known zero.
        Assert.Equal(.639 * 45 / 300, ev.EstimatedKwh!.Value, 3);
        Assert.Equal(1, ev.CoverageFraction, 6);
    }

    [Fact]
    public void ShortUnavailableBlipIsSpreadNotDroppedAndItsSlotsAreEstimates()
    {
        using var db = new DataStore(path);
        // Live 4 Oct: load 47.5387 at 14:27:52, "unknown" at 14:32:52, 47.6286 at 14:37:53 UTC.
        var start = DateTimeOffset.Parse("2026-10-04T13:57:52Z");
        var samples = new List<TelemetrySample>();
        for (var i = 0; i <= 13; i++)
        {
            var at = start.AddMinutes(5 * i);
            var value = 47.2 + .0563 * i;
            samples.Add(i == 7 ? Reading("load", at, null, "sensor.meter", PreviousMidnight, "unavailable") : Daily("load", at, Math.Round(value, 4)));
        }
        OnePollAtATime(db, samples);
        var summary = db.ReadEnergySummary(start, start.AddMinutes(60)).Metrics["load"];
        Assert.Equal(.0563 * 12, summary.EnergyKwh!.Value, 3); Assert.Equal(1, summary.CoverageFraction, 6); Assert.Equal(.0563 * 2, summary.EstimatedKwh!.Value, 3);
        var slots = db.ReadMeasuredHistory(start.AddMinutes(2).AddSeconds(8), start.AddMinutes(62).AddSeconds(8), 30).Slots;
        Assert.Contains(slots, s => s.LoadStatus == "estimated");
        Assert.All(slots, s => Assert.NotNull(s.LoadEstimate));
    }

    [Fact]
    public void DaytimeSolarOutageWithAForecastIsNeverCountedAsAnIdleZero()
    {
        using var db = new DataStore(path);
        var day = DateTimeOffset.Parse("2026-10-04T10:00:00Z");
        db.SavePlan(new PlanSnapshot { Source = "Predbat", At = day.AddHours(-3), CollectedAt = day.AddHours(-3), Slots = Enumerable.Range(0, 12).Select(i => new PlanSlot(day.AddMinutes(30 * i), .3, null, 1.2, null, 50, null, 25, 15, "Demand", 0)).ToList() });
        // Solar reads unknown from 11:00 to 13:10 UTC while Predbat expected 1.2 kWh per half hour; the counter rose 1.5 kWh meanwhile.
        var samples = new List<TelemetrySample>();
        for (var at = day; at <= day.AddHours(1); at = at.AddMinutes(5)) samples.Add(Daily("pv", at, 2 + (at - day).TotalHours));
        for (var at = day.AddHours(1).AddMinutes(5); at <= day.AddHours(3).AddMinutes(10); at = at.AddMinutes(5)) samples.Add(Daily("pv", at, null));
        OnePollAtATime(db, samples);
        // While the outage is still open, it is not a normal state for solar: the collector will warn after its grace period.
        Assert.False(db.OutageExpected("pv"));
        OnePollAtATime(db, [Daily("pv", day.AddHours(3).AddMinutes(15), 4.5)]);
        var pv = db.ReadEnergySummary(day, day.AddHours(3).AddMinutes(15)).Metrics["pv"];
        Assert.Equal(0, pv.IdleSeconds);
        Assert.True(pv.CoverageFraction < .5, $"{pv.CoverageFraction}");
        Assert.Equal("idle", Assert.Single(pv.Gaps).Reason); Assert.Equal(1.5, pv.MissingKwh!.Value, 9);
        Assert.All(db.ReadMeasuredHistory(day.AddHours(1), day.AddHours(3), 30).Slots, s => Assert.Equal("missing", s.PvStatus));
    }

    [Fact]
    public void SolarUnknownCountsAsZeroWhenThePlanSaysTheSunWasDown()
    {
        using var db = new DataStore(path);
        db.SavePlan(new PlanSnapshot { Source = "Predbat", At = Midnight.AddHours(-1), CollectedAt = Midnight.AddHours(-1), Slots = Enumerable.Range(0, 18).Select(i => new PlanSlot(Midnight.AddMinutes(30 * i), .3, null, i < 16 ? 0 : .2, null, 50, null, 25, 15, "Demand", 0)).ToList() });
        // The first reading after the night is 0.06 kWh (more than the counter alone can prove is zero), at 07:05 UTC.
        var samples = new List<TelemetrySample> { Daily("pv", Midnight.AddMinutes(-5), 9.4) };
        for (var at = Midnight.AddMinutes(5); at <= Midnight.AddMinutes(480); at = at.AddMinutes(5)) samples.Add(Daily("pv", at, null));
        samples.Add(Daily("pv", Midnight.AddMinutes(485), .06));
        OnePollAtATime(db, samples);
        var pv = db.ReadEnergySummary(Midnight, Midnight.AddMinutes(485)).Metrics["pv"];
        Assert.Equal(.06, pv.EnergyKwh!.Value, 9); Assert.Equal(1, pv.CoverageFraction, 6); Assert.Equal("idle_zero", pv.State);
    }

    [Fact]
    public void SolarUnknownPastSunriseIsZeroUntilSunriseAndEstimatedAfter()
    {
        using var db = new DataStore(path);
        // Predbat expected solar from 05:00 UTC, but the sensor read unknown until 07:05, when it showed 0.6 kWh since midnight.
        db.SavePlan(new PlanSnapshot { Source = "Predbat", At = Midnight.AddHours(-1), CollectedAt = Midnight.AddHours(-1), Slots = Enumerable.Range(0, 18).Select(i => new PlanSlot(Midnight.AddMinutes(30 * i), .3, null, i < 12 ? 0 : .2, null, 50, null, 25, 15, "Demand", 0)).ToList() });
        var samples = new List<TelemetrySample> { Daily("pv", Midnight.AddMinutes(-5), 9.4) };
        for (var at = Midnight.AddMinutes(5); at <= Midnight.AddMinutes(480); at = at.AddMinutes(5)) samples.Add(Daily("pv", at, null));
        samples.Add(Daily("pv", Midnight.AddMinutes(485), .6));
        OnePollAtATime(db, samples);
        var pv = db.ReadEnergySummary(Midnight, Midnight.AddMinutes(485)).Metrics["pv"];
        Assert.Equal(.6, pv.EnergyKwh!.Value, 9); Assert.Equal(1, pv.CoverageFraction, 6);
        // Six hours of night are a known zero; the 0.6 kWh is spread over the two hours after Predbat's forecast sunrise.
        Assert.Equal(6 * 3600, pv.IdleSeconds, 0); Assert.Equal(125 * 60, pv.EstimatedSeconds, 0); Assert.Equal(.6, pv.EstimatedKwh!.Value, 9);
        var slots = db.ReadMeasuredHistory(Midnight, Midnight.AddHours(8), 30).Slots;
        Assert.All(slots.Take(12), s => Assert.Equal("idle", s.PvStatus));
        Assert.All(slots.Skip(12).Take(4), s => Assert.Equal("estimated", s.PvStatus));
    }

    [Fact]
    public void AnIdleRunStillInProgressIsAKnownZeroForAChargerButAGapForALifetimeMeter()
    {
        using var db = new DataStore(path);
        var t = Midnight.AddHours(-3);
        OnePollAtATime(db, [Reading("ev", t, 4.6, "sensor.charger", null), .. Enumerable.Range(1, 24).Select(i => Reading("ev", t.AddMinutes(5 * i), null, "sensor.charger", null, updated: t.AddMinutes(1))),
            Reading("grid_import", t, 100, "sensor.lifetime", null), .. Enumerable.Range(1, 24).Select(i => Reading("grid_import", t.AddMinutes(5 * i), null, "sensor.lifetime", null, "unavailable"))]);
        var summary = db.ReadEnergySummary(t, t.AddHours(2));
        // The minute between the last reading and the switch to unknown has no rate to estimate from, so it alone is unknown.
        Assert.True(summary.Metrics["ev"].CoverageFraction > .99); Assert.Equal("idle_zero", summary.Metrics["ev"].State);
        Assert.Equal(0, summary.Metrics["grid_import"].CoverageFraction, 6); Assert.Equal("offline", Assert.Single(summary.Metrics["grid_import"].Gaps).Reason);
    }

    [Fact]
    public void ReplacingOneReadingInTheMiddleKeepsEveryIntervalAfterIt()
    {
        using var db = new DataStore(path);
        var t = Midnight.AddHours(10);
        var samples = Enumerable.Range(0, 7).Select(i => Daily("load", t.AddMinutes(5 * i), 10 + .2 * i)).ToList();
        db.SaveTelemetry(samples);
        // A late correction of the 10:15 reading re-derives its neighbourhood; the interval that starts at the next reading must survive.
        db.SaveTelemetry([samples[3] with { Value = null, Status = "invalid", RawState = "garbled" }]);
        var load = db.ReadEnergySummary(t, t.AddMinutes(30)).Metrics["load"];
        Assert.Equal(1.2, load.EnergyKwh!.Value, 9); Assert.Equal(1, load.CoverageFraction, 6); Assert.Equal(.4, load.EstimatedKwh!.Value, 9);
        Assert.Equal(5, Convert.ToInt32(db.Query("SELECT count(*) n FROM telemetry_intervals WHERE metric='load'")[0]["n"]));
    }

    public void Dispose() { if (Directory.Exists(path)) Directory.Delete(path, true); }
}
