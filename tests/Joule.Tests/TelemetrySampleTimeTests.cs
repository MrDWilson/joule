using System.Globalization;
using Joule;
using Xunit;

namespace Joule.Tests;

/// <summary>
/// Readings are keyed at Home Assistant's update time (last_updated) rather than the poll time, so a skipped sensor update no longer
/// draws a zero followed by a double, and slot and day boundaries fall where the energy did. The (metric,time) key must stay unique
/// and ordered, and older poll-keyed history must derive the same way.
/// </summary>
public sealed class TelemetrySampleTimeTests : IDisposable
{
    readonly string path = Path.Combine(Path.GetTempPath(), "predbat-sample-time-" + Guid.NewGuid().ToString("N"));
    static readonly DateTimeOffset T = DateTimeOffset.Parse("2026-10-04T22:40:00Z");
    static TelemetrySample Poll(string metric, DateTimeOffset polled, double? value, DateTimeOffset updated, string? lastReset = "2026-10-04T00:00:00+01:00") => value is null
        ? new(metric, "sensor." + metric, polled, null, "", "HomeAssistant", "unknown", "", updated, lastReset is null ? "{}" : $"{{\"last_reset\":\"{lastReset}\"}}", "idle")
        : new(metric, "sensor." + metric, polled, value, "kWh", "HomeAssistant", value.Value.ToString(CultureInfo.InvariantCulture), "kWh", updated, lastReset is null ? "{}" : $"{{\"last_reset\":\"{lastReset}\"}}");
    static void Collect(DataStore db, params TelemetrySample[] polls) { foreach (var p in polls) db.SaveTelemetry([p], alignToSource: true); }

    [Fact]
    public void AChangedReadingIsKeyedAtItsUpdateTimeAndARepeatAtThePollTime()
    {
        using var db = new DataStore(path);
        // Live load sensor: updates at 22:41:09 and 22:51:09 (it skipped 22:46); polls at 22:42:36, 22:47:36 and 22:52:36.
        Collect(db, Poll("load", T.AddSeconds(156), 61.069, T.AddSeconds(69)), Poll("load", T.AddSeconds(456), 61.069, T.AddSeconds(69)), Poll("load", T.AddSeconds(756), 61.109, T.AddSeconds(669)));
        var times = db.ReadTelemetrySamples(T, T.AddHours(1), "load").Select(x => x.Time).ToList();
        // The very first reading of a metric has nothing to order against, so it keeps its poll time.
        Assert.Equal([T.AddSeconds(156), T.AddSeconds(456), T.AddSeconds(669)], times);
    }

    [Fact]
    public void RepeatedPollsOfOneUpdateCollapseIntoOneIntervalWithNoZeroFollowedByADouble()
    {
        using var db = new DataStore(path);
        Collect(db, Poll("load", T.AddSeconds(156), 61.069, T.AddSeconds(69)), Poll("load", T.AddSeconds(456), 61.069, T.AddSeconds(69)), Poll("load", T.AddSeconds(756), 61.109, T.AddSeconds(669)));
        var interval = Assert.Single(db.Query("SELECT start_time,end_time,energy_kwh FROM telemetry_intervals WHERE metric='load'"));
        Assert.Equal(.04, Convert.ToDouble(interval["energy_kwh"]), 9);
        // The skipped update cycle is one interval at 0.28 kW, not 0 kW followed by 0.69 kW.
        var trends = db.ReadObservedMeterTrends(T, T.AddMinutes(12)).Intervals;
        Assert.Equal(.04 / ((669 - 156) / 3600d), Assert.Single(trends).AverageKw!.Value, 6);
    }

    [Fact]
    public void ALongUnchangedRunKeepsItsLastPollAsTheStartOfTheNextChange()
    {
        using var db = new DataStore(path);
        // Import flat for an hour (the battery covers the house), then a 0.2 kWh rise at 23:31:09.
        var polls = new List<TelemetrySample> { Poll("grid_import", T.AddSeconds(156), 50, T.AddSeconds(69)) };
        for (var i = 1; i <= 12; i++) polls.Add(Poll("grid_import", T.AddSeconds(156 + 300 * i), 50, T.AddSeconds(69)));
        polls.Add(Poll("grid_import", T.AddSeconds(156 + 300 * 13), 50.2, T.AddSeconds(156 + 300 * 12 + 60)));
        Collect(db, [.. polls]);
        var last = db.Query("SELECT start_time,energy_kwh FROM telemetry_intervals WHERE metric='grid_import' ORDER BY start_time DESC LIMIT 1")[0];
        // The rise belongs after the last poll that still saw 50 kWh, not spread over the flat hour.
        var start = last["start_time"] switch { DateTimeOffset d => d, DateTime d => new DateTimeOffset(DateTime.SpecifyKind(d, DateTimeKind.Utc)), var v => DateTimeOffset.Parse(Convert.ToString(v, CultureInfo.InvariantCulture)!, CultureInfo.InvariantCulture) };
        Assert.Equal(T.AddSeconds(156 + 300 * 12), start);
        Assert.Equal(.2, Convert.ToDouble(last["energy_kwh"]), 9);
    }

    [Fact]
    public void AnUpdateTimeThatIsNotNewerThanThePreviousSampleFallsBackToThePollTime()
    {
        using var db = new DataStore(path);
        Collect(db, Poll("load", T.AddMinutes(5), 10, T.AddMinutes(4)));
        Assert.Equal(T.AddMinutes(5), db.ReadTelemetrySamples(T, T.AddHours(1), "load").Single().Time);
        // Home Assistant restored an older last_updated after a restart: the poll time keeps the key ordered.
        Collect(db, Poll("load", T.AddMinutes(10), 10.2, T.AddMinutes(3)));
        // And an update time in the future (clock skew) is never used either.
        Collect(db, Poll("load", T.AddMinutes(15), 10.4, T.AddMinutes(20)));
        Assert.Equal([T.AddMinutes(5), T.AddMinutes(10), T.AddMinutes(15)], db.ReadTelemetrySamples(T, T.AddHours(1), "load").Select(x => x.Time));
    }

    [Fact]
    public void AChangeJustBeforeMidnightPolledJustAfterLandsOnTheOldDay()
    {
        using var db = new DataStore(path);
        var midnight = DateTimeOffset.Parse("2026-10-04T23:00:00Z");
        Collect(db, Poll("load", midnight.AddMinutes(-8), 61.0, midnight.AddMinutes(-9)),
            // Updated 23:59:50 local, polled 00:02:30 local, still carrying the old day's reset.
            Poll("load", midnight.AddSeconds(150), 61.2, midnight.AddSeconds(-10)),
            Poll("load", midnight.AddSeconds(450), .05, midnight.AddSeconds(420), "2026-10-05T00:00:00+01:00"));
        var oldDay = db.ReadEnergySummary(midnight.AddMinutes(-9), midnight).Metrics["load"];
        Assert.Equal(.2, oldDay.EnergyKwh!.Value - (oldDay.EstimatedKwh ?? 0), 9);
        var newDay = db.ReadEnergySummary(midnight, midnight.AddSeconds(420)).Metrics["load"];
        Assert.Equal(.05, newDay.EnergyKwh!.Value, 9);
    }

    [Fact]
    public void LegacyPollKeyedHistoryIsRekeyedOnceAtItsUpdateTimesWithoutChangingValues()
    {
        using var db = new DataStore(path);
        // Stored the old way: every sample at its poll time.
        db.SaveTelemetry([Poll("load", T.AddSeconds(156), 61.069, T.AddSeconds(69)), Poll("load", T.AddSeconds(456), 61.069, T.AddSeconds(69)), Poll("load", T.AddSeconds(756), 61.109, T.AddSeconds(669)),
            Poll("intelligent_slots", T.AddSeconds(156), null, T.AddSeconds(10))]);
        var before = db.ReadTelemetrySamples(T, T.AddHours(1), limit: 1000);
        db.EnsureIntervalRules(TimeSpan.FromMinutes(15)); db.EnsureIntervalRules(TimeSpan.FromMinutes(15));
        var load = db.ReadTelemetrySamples(T, T.AddHours(1), "load");
        Assert.Equal([T.AddSeconds(156), T.AddSeconds(456), T.AddSeconds(669)], load.Select(x => x.Time));
        Assert.Equal(before.Where(x => x.Metric == "load").Select(x => x.Value), load.Select(x => x.Value));
        // State sensors keep their poll time.
        Assert.Equal(T.AddSeconds(156), Assert.Single(db.ReadTelemetrySamples(T, T.AddHours(1), "intelligent_slots")).Time);
        Assert.Equal(.04, db.ReadEnergySummary(T.AddSeconds(156), T.AddSeconds(669)).Metrics["load"].EnergyKwh!.Value, 9);
    }

    [Fact]
    public void LegacyUnknownAndStaleRowsAreMigrated()
    {
        using var db = new DataStore(path);
        db.SaveTelemetry([
            new("ev", "sensor.ev", T, null, "", "HomeAssistant", "unknown", "Wh", T, "{}", "invalid"),
            new("grid_export", "sensor.export", T, null, "", "HomeAssistant", "2.473880859375", "kWh", T, "{}", "stale"),
            new("export_tariff", "sensor.rate", T, null, "", "HomeAssistant", "0.1262", "GBP/kWh", T, "{}", "stale")]);
        db.Query("SELECT 1");
        // Writes normalise "unknown" at once; the migration repairs rows written by older versions.
        Assert.Equal("idle", db.ReadTelemetrySamples(T, T.AddMinutes(1), "ev").Single().Status);
        db.EnsureIntervalRules(TimeSpan.FromMinutes(15));
        var export = db.ReadTelemetrySamples(T, T.AddMinutes(1), "grid_export").Single();
        Assert.Equal("observed", export.Status); Assert.Equal(2.473880859375, export.Value);
        var rate = db.ReadTelemetrySamples(T, T.AddMinutes(1), "export_tariff").Single();
        Assert.Equal("observed", rate.Status); Assert.Equal(12.62, rate.Value!.Value, 9); Assert.Equal("p/kWh", rate.Unit);
    }

    [Fact]
    public void UnknownWrittenAsUnavailableByAnyWriterIsStoredAsIdle()
    {
        using var db = new DataStore(path);
        db.SaveTelemetry([new("pv", "sensor.pv", T, null, "", "HomeAssistant", "Unknown", "", T, "{}", "unavailable"), new("load", "sensor.load", T, null, "", "HomeAssistant", "unavailable", "", T, "{}", "unavailable")]);
        Assert.Equal("idle", db.ReadTelemetrySamples(T, T.AddMinutes(1), "pv").Single().Status);
        Assert.Equal("unavailable", db.ReadTelemetrySamples(T, T.AddMinutes(1), "load").Single().Status);
    }

    [Fact]
    public void RebuildTimeOnAYearOfFiveMinuteSamplesIsRecorded()
    {
        using var db = new DataStore(path);
        // A month of one meter at the live cadence, saved the way a history import would.
        var start = DateTimeOffset.Parse("2026-09-01T00:00:00Z");
        db.SaveTelemetry(Enumerable.Range(0, 30 * 288).Select(i => Poll("load", start.AddMinutes(5 * i).AddSeconds(90), 10 + i * .02, start.AddMinutes(5 * i), null)));
        db.EnsureIntervalRules(TimeSpan.FromMinutes(15));
        Assert.NotNull(db.LastIntervalRebuild);
        Assert.True(db.LastIntervalRebuild < TimeSpan.FromSeconds(30), $"{db.LastIntervalRebuild}");
    }

    public void Dispose() { if (Directory.Exists(path)) Directory.Delete(path, true); }
}
