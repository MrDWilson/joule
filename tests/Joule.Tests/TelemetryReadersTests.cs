using System.Globalization;
using Joule;
using Xunit;

namespace Joule.Tests;

/// <summary>What the readers built on the intervals report: home use excluding the car, battery level at slot start, hourly rows,
/// plan-versus-actual rows, coverage windows, power series and the demo meters.</summary>
public sealed class TelemetryReadersTests : IDisposable
{
    readonly string path = Path.Combine(Path.GetTempPath(), "predbat-readers-" + Guid.NewGuid().ToString("N"));
    static readonly DateTimeOffset At = DateTimeOffset.Parse("2026-10-04T10:00:00Z");
    static TelemetrySample M(string metric, DateTimeOffset at, double value, string unit = "kWh") =>
        new(metric, "sensor." + metric, at, value, unit, "HomeAssistant", value.ToString(CultureInfo.InvariantCulture), unit, at);
    static PlanSnapshot Plan(DateTimeOffset start, int slots, double socStart = 50, double socStep = 0) => new()
    {
        Source = "Predbat", At = start.AddHours(-1), CollectedAt = start.AddHours(-1),
        Slots = Enumerable.Range(0, slots).Select(i => new PlanSlot(start.AddMinutes(30 * i), .4, null, 0, null, socStart + socStep * i, null, 6.67, 15, "Chrg", .03)).ToList()
    };

    [Fact]
    public void HomeIsLoadMinusEvAndNeverBelowZeroWhenTheCarReadsAheadOfTheHouseMeter()
    {
        using var db = new DataStore(path);
        db.ConfigureTelemetry(new TelemetrySettings(LoadIncludesEv: "true"));
        db.SavePlan(Plan(At, 2));
        var samples = new List<TelemetrySample>();
        // A 7 kW charge: the EV meter reports each five minutes a poll ahead of the whole-house meter.
        for (var i = 0; i <= 12; i++)
        {
            samples.Add(M("load", At.AddMinutes(5 * i), 10 + .05 * i + (i >= 2 ? .58 * (i - 1) : 0)));
            samples.Add(M("ev", At.AddMinutes(5 * i), i >= 1 ? .58 * i : 0));
        }
        db.SaveTelemetry(samples);
        var history = db.ReadMeasuredHistory(At, At.AddMinutes(60), 5).Slots;
        Assert.All(history, s => Assert.True(s.Home >= 0, $"{s.Time:HH:mm} {s.Home}"));
        Assert.Equal(0, history[0].Home!.Value, 9);
        var half = db.ReadMeasuredHistory(At, At.AddMinutes(60), 30).Slots;
        Assert.Equal(.3, half[1].Home!.Value, 6);
        var summary = db.ReadEnergySummary(At, At.AddMinutes(60));
        Assert.True(summary.LoadIncludesEv);
        Assert.Equal(summary.Metrics["load"].EnergyKwh!.Value - summary.Metrics["ev"].EnergyKwh!.Value, summary.Home!.EnergyKwh!.Value, 9);
        var slot = db.GetPlan()!.Slots[1];
        Assert.Equal(.3, slot.HomeActual!.Value, 6); Assert.Equal(.58 * 6, slot.EvActual!.Value, 6);
        var row = db.ReadPlanVsActual(At, At.AddMinutes(60))[1];
        Assert.Equal(.3, row.HomeKwh!.Value, 6); Assert.Empty(row.EstimatedMetrics);
    }

    [Fact]
    public void LoadIncludesEvIsDetectedFromHistory()
    {
        DataStore Store(string suffix, bool included)
        {
            var db = new DataStore(path + suffix);
            var samples = new List<TelemetrySample>();
            for (var i = 0; i <= 72; i++)
            {
                var ev = i is >= 12 and < 48 ? .6 * (i - 11) : i >= 48 ? .6 * 36 : 0;
                samples.Add(M("ev", At.AddMinutes(5 * i), ev)); samples.Add(M("load", At.AddMinutes(5 * i), 10 + .05 * i + (included ? ev : 0)));
            }
            db.SaveTelemetry(samples); return db;
        }
        using var with = Store("-with", true); using var without = Store("-without", false);
        Assert.True(with.LoadIncludesEv()); Assert.False(without.LoadIncludesEv());
        Assert.Null(without.ReadEnergySummary(At, At.AddHours(6)).Home);
    }

    [Fact]
    public void BatteryLevelIsReadAtTheSlotStartToCompareWithPredbatsForecast()
    {
        using var db = new DataStore(path);
        // A charge slot Predbat planned to start at 14% (and reach 29% by its end); the battery read 14.5% at the start and 28.4% at the end.
        db.SavePlan(Plan(At, 2, 14, 15));
        db.SaveTelemetry([M("soc", At.AddSeconds(40), 14.5, "%"), M("soc", At.AddMinutes(15), 21, "%"), M("soc", At.AddMinutes(29).AddSeconds(50), 28.4, "%"), M("soc", At.AddMinutes(45), 35, "%"), M("soc", At.AddMinutes(59), 41, "%")]);
        var slot = db.GetPlan()!.Slots[0];
        Assert.Equal(.5, slot.SocActualStart!.Value - slot.SocForecast, 9);
        Assert.Equal(28.4, slot.SocActual);
        var row = db.ReadPlanVsActual(At, At.AddMinutes(30))[0];
        Assert.Equal(14.5, row.SocActualStartPercent); Assert.Equal(28.4, row.SocActualEndPercent);
    }

    [Fact]
    public void AnOldBatteryReadingIsNotTakenAsTheLevelAtASlotEdge()
    {
        using var db = new DataStore(path);
        db.SavePlan(Plan(At, 1));
        // Live: 65.278% polled at 10:03 and 10:13, but Home Assistant last updated it at 09:40; then 66.1% updated at 10:28:30.
        db.SaveTelemetry([new("soc", "sensor.soc", At.AddMinutes(-20), 65.278, "%", "HomeAssistant", "65.278", "%", At.AddMinutes(-20)),
            new("soc", "sensor.soc", At.AddMinutes(3), 65.278, "%", "HomeAssistant", "65.278", "%", At.AddMinutes(-20)),
            new("soc", "sensor.soc", At.AddMinutes(28).AddSeconds(30), 66.1, "%", "HomeAssistant", "66.1", "%", At.AddMinutes(28).AddSeconds(30))]);
        var slot = db.GetPlan()!.Slots[0];
        // At the slot start the 09:40 reading was still in force and a poll confirmed it, so it stands; at the end the fresh one wins.
        Assert.Equal(65.278, slot.SocActualStart); Assert.Equal(66.1, slot.SocActual);
    }

    [Fact]
    public void HourlyRowsProrateAcrossHourEdgesAndReportCoveragePerMeter()
    {
        using var db = new DataStore(path);
        var samples = new List<TelemetrySample>();
        // Polls 20 minutes apart that straddle the hour: the old reader put whole intervals in the hour they started.
        for (var m = -10; m <= 130; m += 20) samples.Add(M("load", At.AddMinutes(m), 10 + m * .01));
        for (var m = 0; m <= 30; m += 5) samples.Add(M("pv", At.AddMinutes(m), 2 + m * .01));
        db.SaveTelemetry(samples, TimeSpan.FromMinutes(25));
        var hours = db.ReadHourlyMeasured(At, At.AddHours(2));
        Assert.Equal(.6, hours[0].EnergyKwh["load"]!.Value, 9); Assert.Equal(.6, hours[1].EnergyKwh["load"]!.Value, 9);
        Assert.Equal(1, hours[0].Coverage["load"]); Assert.Equal(.5, hours[0].Coverage["pv"], 6); Assert.Null(hours[0].EnergyKwh["pv"]);
        Assert.Equal("Sun 04 Oct 11:00", hours[0].Label);
    }

    [Fact]
    public void HourlyLabelsCarryTheOffsetWhenTheClockHourRepeats()
    {
        using var db = new DataStore(path);
        // 25 Oct 2026: 01:00–02:00 BST is followed by 01:00–02:00 GMT.
        var start = DateTimeOffset.Parse("2026-10-24T23:00:00Z");
        db.SaveTelemetry(Enumerable.Range(0, 49).Select(i => M("load", start.AddMinutes(5 * i), 1 + i * .01)));
        var labels = db.ReadHourlyMeasured(start, start.AddHours(4)).Select(h => h.Label).ToList();
        Assert.Contains("Sun 25 Oct 01:00 (UTC+01:00)", labels); Assert.Contains("Sun 25 Oct 01:00 (UTC+00:00)", labels);
    }

    [Fact]
    public void CoverageStartsWhenRecordsBeganSoPrehistoryIsNotMissing()
    {
        using var db = new DataStore(path);
        db.SaveTelemetry(Enumerable.Range(0, 13).Select(i => M("load", At.AddMinutes(5 * i), 10 + i * .1)));
        var day = db.ReadEnergySummary(At.AddHours(-10), At.AddHours(1)).Metrics["load"];
        Assert.Equal(1, day.CoverageFraction, 6); Assert.Equal(At, day.CoverageFrom); Assert.Equal(At.AddHours(1), day.CoverageTo); Assert.Equal("complete", day.State);
        Assert.Empty(day.Gaps);
        // Nothing before records began: no records, not "missing".
        Assert.Equal("no_records", db.ReadEnergySummary(At.AddDays(-2), At.AddDays(-1)).Metrics["pv"].State);
    }

    [Fact]
    public void TheDayTotalFromTheMetersOwnCounterIsOnlyGivenForAWindowStartingAtLocalMidnight()
    {
        using var db = new DataStore(path);
        var midnight = DateTimeOffset.Parse("2026-10-03T23:00:00Z");
        var attrs = "{\"state_class\":\"total\",\"last_reset\":\"2026-10-04T00:00:00+01:00\"}";
        db.SaveTelemetry(Enumerable.Range(0, 13).Select(i => new TelemetrySample("load", "sensor.load", midnight.AddMinutes(1 + 5 * i), .02 + i * .1, "kWh", "HomeAssistant", "x", "kWh", midnight.AddMinutes(1 + 5 * i), attrs)));
        // The last reading inside the window (00:56) is the day so far.
        Assert.Equal(1.12, db.ReadEnergySummary(midnight, midnight.AddHours(1)).Metrics["load"].CounterDayTotalKwh!.Value, 9);
        Assert.Null(db.ReadEnergySummary(midnight.AddMinutes(30), midnight.AddHours(1)).Metrics["load"].CounterDayTotalKwh);
        Assert.Equal(SensorProfiles.DailyCounter, db.ReadEnergySummary(midnight, midnight.AddHours(1)).Metrics["load"].Profile);
    }

    [Fact]
    public void EstimatedHalfHoursAreShownButNeverSetThePeak()
    {
        using var db = new DataStore(path);
        // A one-hour outage across which 8 kWh flowed: spread at 8 kW would be the "peak"; the measured peak is 2 kW.
        var samples = new List<TelemetrySample>();
        for (var m = 0; m <= 30; m += 5) samples.Add(M("load", At.AddMinutes(m), 10 + m * (1 / 30d)));
        samples.Add(M("load", At.AddMinutes(90), 19)); for (var m = 95; m <= 150; m += 5) samples.Add(M("load", At.AddMinutes(m), 19 + (m - 90) / 30d));
        db.SaveTelemetry(samples);
        var load = db.ReadObservedMeterTrends(At, At.AddMinutes(150), compact: true).Series.Single(s => s.Metric == "load");
        Assert.Equal(2, load.PeakKw!.Value, 6);
        Assert.Contains("estimated", load.Status);
        Assert.Contains(load.Kw, kw => kw > 7);
    }

    // Seeded at a pinned time after today's solar has started. Until then (00:00 to about 07:30 London) yesterday's last
    // five minutes of solar, which end on today's first "unknown", stay an open idle stretch rather than a known zero.
    [Fact]
    public void DemoMetersBehaveLikeTheRealInstallation()
    {
        var now = DateTimeOffset.Parse("2026-10-06T12:00:00Z", CultureInfo.InvariantCulture);
        using var db = new DataStore(path, new ManualClock(now));
        DemoTelemetry.Seed(db);
        var profiles = db.ReadSensorProfiles();
        Assert.Equal(SensorProfiles.DailyCounter, profiles["load"]); Assert.Equal(SensorProfiles.SolarDaily, profiles["pv"]);
        Assert.Equal(SensorProfiles.DailyCounter, profiles["grid_export"]); Assert.Equal(SensorProfiles.SessionCounter, profiles["ev"]);
        var yesterday = CivilTime.FirstValidInstant(TimeZoneInfo.ConvertTime(now, db.TelemetryZone).Date.AddDays(-1), db.TelemetryZone);
        var day = db.ReadEnergySummary(yesterday, yesterday.AddDays(1));
        // Overnight solar and pre-export unknowns are known zeros; the counters reconcile.
        foreach (var metric in new[] { "grid_import", "grid_export", "battery_charge", "battery_discharge" }) { Assert.Equal(1, day.Metrics[metric].CoverageFraction, 3); Assert.True(day.Metrics[metric].Reconciled, metric); }
        Assert.True(day.Metrics["grid_export"].IdleSeconds > 4 * 3600);
        Assert.True(day.Metrics["ev"].CoverageFraction > .99); Assert.True(day.Metrics["ev"].EnergyKwh >= 7.5 - .01);
        // Behaviour change: yesterday has no long outage; the only one is 15:10–15:50 three days before the history was seeded.
        Assert.Empty(day.Metrics["pv"].Gaps);
        var outageDay = yesterday.AddDays(-2);
        var outage = db.ReadEnergySummary(outageDay, outageDay.AddDays(1)).Metrics["pv"];
        // At 40 minutes it is short enough for the counters to bridge: the energy is kept, its timing marked estimated (≈).
        Assert.True(outage.EstimatedSeconds >= 40 * 60, $"{outage.EstimatedSeconds}"); Assert.True(outage.EstimatedKwh > 0);
        Assert.Equal(day.ImportCostGbp!.Value - day.ExportCreditGbp!.Value, day.NetCostGbp!.Value, 9);
        Assert.True(day.LoadIncludesEv);
        var samples = db.ReadTelemetrySamples(yesterday, yesterday.AddHours(3), "pv", 0, 1000);
        Assert.Contains(samples, s => s.Status == "idle" && s.RawState == "unknown");
        Assert.Contains(db.ReadTelemetrySamples(yesterday, yesterday.AddHours(6), "ev", 0, 1000), s => s.Value is > .4 and < .5);
    }

    public void Dispose()
    {
        foreach (var dir in new[] { path, path + "-with", path + "-without" }) if (Directory.Exists(dir)) Directory.Delete(dir, true);
    }
}
