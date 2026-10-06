using System.Globalization;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Joule;
using Xunit;

namespace Joule.Tests;

/// <summary>
/// Reconciliation against a real night: a sanitised snapshot of the live collector's samples from 4 Oct 2026 21:00 UTC to 5 Oct
/// 10:23 UTC (Octopus entity IDs replaced; only last_reset and state_class kept from the attributes). On this morning the live
/// Overview showed "Net cost £0.06" beside "Import £1.80, export £0.70", solar "25% measured" and a blank midnight half-hour.
/// The meters' own daily counters are the ground truth: today's totals must match them, and net cost must be import minus export.
/// </summary>
/// <summary>The snapshot collected once, poll by poll, and shared read-only by the reconciliation tests.</summary>
public sealed class LiveSnapshotStore : IDisposable
{
    readonly string path = Path.Combine(Path.GetTempPath(), "predbat-live-snapshot-" + Guid.NewGuid().ToString("N"));
    public List<TelemetrySample> Samples { get; } = TelemetryLiveReconciliationTests.Fixture();
    public DataStore Db { get; }
    public LiveSnapshotStore() { Db = TelemetryLiveReconciliationTests.Collect(path, Samples); }
    public void Dispose() { Db.Dispose(); if (Directory.Exists(path)) Directory.Delete(path, true); }
}

public sealed class TelemetryLiveReconciliationTests(LiveSnapshotStore snapshot) : IDisposable, IClassFixture<LiveSnapshotStore>
{
    readonly string path = Path.Combine(Path.GetTempPath(), "predbat-live-reconcile-" + Guid.NewGuid().ToString("N"));
    static readonly DateTimeOffset Midnight = DateTimeOffset.Parse("2026-10-04T23:00:00Z"); // 5 Oct 00:00 BST
    static readonly string[] DailyCounters = ["load", "pv", "grid_import", "grid_export", "battery_charge", "battery_discharge"];

    static string FixturePath([CallerFilePath] string source = "") => Path.Combine(Path.GetDirectoryName(source)!, "Fixtures", "live-telemetry-2026-10-05.csv.gz");
    internal static List<TelemetrySample> Fixture()
    {
        using var file = File.OpenRead(FixturePath());
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var reader = new StreamReader(gzip);
        var header = reader.ReadLine()!.Split(',');
        var samples = new List<TelemetrySample>();
        while (reader.ReadLine() is { } line)
        {
            var f = line.Split(',');
            var attributes = new Dictionary<string, string>();
            if (f[8] != "") attributes["last_reset"] = f[8];
            if (f[9] != "") attributes["state_class"] = f[9];
            samples.Add(new(f[0], f[1], DateTimeOffset.Parse(f[2], CultureInfo.InvariantCulture), f[3] == "" ? null : double.Parse(f[3], CultureInfo.InvariantCulture), f[4], "HomeAssistant", f[5], f[6],
                f[7] == "" ? null : DateTimeOffset.Parse(f[7], CultureInfo.InvariantCulture), JsonSerializer.Serialize(attributes), f[10]));
        }
        Assert.Equal("metric", header[0]);
        return samples;
    }
    /// <summary>Saves the snapshot one poll per call, re-keyed at Home Assistant's update time: exactly what the live collector now does.</summary>
    internal static DataStore Collect(string path, IEnumerable<TelemetrySample> samples)
    {
        var db = new DataStore(path);
        foreach (var poll in samples.GroupBy(x => x.Time).OrderBy(x => x.Key)) db.SaveTelemetry(poll, TimeSpan.FromMinutes(15), alignToSource: true);
        return db;
    }
    static DateTimeOffset LastSample(DataStore db) => db.ReadLatestTelemetry().Values.Max(x => x.Time);
    static double LastCounter(List<TelemetrySample> samples, string metric) => samples.Where(x => x.Metric == metric && x.Status == "observed" && x.Value is not null).MaxBy(x => x.Time)!.Value!.Value;

    [Fact]
    public void TodaysTotalsMatchEachMetersOwnDailyCounter()
    {
        var samples = snapshot.Samples; var db = snapshot.Db;
        var today = db.ReadEnergySummary(Midnight, LastSample(db));
        foreach (var metric in DailyCounters)
        {
            var m = today.Metrics[metric];
            Assert.Equal(LastCounter(samples, metric), m.EnergyKwh!.Value, .05);
            Assert.Equal(LastCounter(samples, metric), m.CounterDayTotalKwh!.Value, 9);
            Assert.True(m.Reconciled, metric);
            Assert.True(m.CoverageFraction >= .98, $"{metric} coverage {m.CoverageFraction}");
        }
        // Live this morning: grid export read 5.71 kWh while its own meter said 6.33.
        Assert.Equal(6.329, today.Metrics["grid_export"].EnergyKwh!.Value, .01);
    }

    [Fact]
    public void NetCostIsImportMinusExportAndNotTheMatchedPeriodFigure()
    {
        var db = snapshot.Db;
        var today = db.ReadEnergySummary(Midnight, LastSample(db));
        Assert.InRange(today.ImportCostGbp!.Value, 1.75, 1.95);
        Assert.InRange(today.ExportCreditGbp!.Value, .65, .85);
        Assert.Equal(today.ImportCostGbp!.Value - today.ExportCreditGbp!.Value, today.NetCostGbp!.Value, 9);
        // The headline used to be £0.06 because the overnight import was dropped wherever export read "unknown".
        Assert.InRange(today.NetCostGbp!.Value, 1.0, 1.2);
        Assert.True(today.ImportCostCoverage >= .98 && today.ExportCostCoverage >= .98, $"{today.ImportCostCoverage} {today.ExportCostCoverage}");
        // The matched figure now covers the night too (export was a known zero), so it agrees with the headline.
        Assert.True(today.CostCoverageFraction >= .95, $"matched coverage {today.CostCoverageFraction}");
        Assert.Equal(today.NetCostGbp!.Value, today.ObservedNetCostGbp!.Value, .02);
    }

    [Fact]
    public void OvernightUnknownsAreKnownZerosNotGaps()
    {
        var db = snapshot.Db;
        var today = db.ReadEnergySummary(Midnight, LastSample(db));
        // Solar read unknown from midnight to 06:43 UTC; it was "25% measured" live.
        Assert.True(today.Metrics["pv"].CoverageFraction >= .98);
        Assert.Contains(today.Metrics["pv"].State, new[] { "complete", "idle_zero" });
        // The EV charger reads unknown between sessions; it was "27% measured" live.
        Assert.True(today.Metrics["ev"].CoverageFraction >= .95, $"ev {today.Metrics["ev"].CoverageFraction}");
        // Export read unknown until 01:47 UTC and came back at 0.6176 kWh: the night is a known zero and the energy lands in the final poll.
        var export = db.Query("SELECT method,energy_kwh,start_time,end_time FROM telemetry_intervals WHERE metric='grid_export' AND start_time>=TIMESTAMPTZ '2026-10-04T23:00:00Z' AND start_time<TIMESTAMPTZ '2026-10-05T02:00:00Z' ORDER BY start_time");
        Assert.Equal("idle", export[0]["method"]); Assert.Equal(0d, Convert.ToDouble(export[0]["energy_kwh"]));
        Assert.Equal("reset_after_idle", export[1]["method"]); Assert.Equal(.6176, Convert.ToDouble(export[1]["energy_kwh"]), 3);
        // The first reading of the 23:02 EV session (0.293 kWh) is counted, not dropped.
        var history = db.ReadMeasuredHistory(Midnight.AddHours(-1), Midnight.AddHours(9), 30).Slots;
        Assert.True(history.Single(s => s.Time == Midnight).Ev > 3.0);
    }

    [Fact]
    public void TheMidnightHalfHourIsMeasured()
    {
        var db = snapshot.Db;
        var slots = db.ReadMeasuredHistory(Midnight.AddHours(-1), Midnight.AddHours(1), 30).Slots;
        var midnight = slots.Single(s => s.Time == Midnight);
        Assert.Equal("measured", midnight.LoadStatus); Assert.NotNull(midnight.Load);
        // The car charged at ~7.4 kW from midnight: the slot holds well over 3 kWh, which used to be blank.
        Assert.True(midnight.Load > 3, $"{midnight.Load}");
        // The half-hour before midnight holds the daily counter's estimated pre-reset tail: shown as ≈, never blank.
        var before = slots.Single(s => s.Time == Midnight.AddMinutes(-30));
        Assert.NotNull(before.LoadEstimate);
    }

    [Fact]
    public void PowerPeaksComeFromHalfHourAveragesAndTheTrendPayloadIsSmall()
    {
        var db = snapshot.Db;
        var end = LastSample(db);
        var trends = db.ReadObservedMeterTrends(end.AddHours(-12), end, compact: true);
        var load = trends.Series.Single(s => s.Metric == "load");
        // Live the card said "peak 15 kW": a 5-minute interval that held two sensor updates. The real peak was ~7.6 kW (EV + house).
        Assert.InRange(load.PeakKw!.Value, 6.5, 9);
        Assert.Equal(30, load.StepMinutes);
        Assert.True(JsonSerializer.Serialize(trends, JsonDefaults.Options).Length < 10_000);
        Assert.Empty(trends.Intervals);
        // The peak is the midnight half-hour, when the car charged at 7.4 kW on top of the house.
        Assert.Equal(Midnight, load.PeakAt);
        // No zero/double comb: no measured half-hour reads zero, which a lived-in house never does.
        Assert.All(load.Kw.Where((kw, i) => load.Status[i] == "measured"), kw => Assert.True(kw > .05, $"{kw}"));
    }

    [Fact]
    public void BatchIncrementalAndRederivedHistoryGiveTheSameDailyTotals()
    {
        var samples = Fixture();
        using var live = Collect(path, samples);
        using var batch = new DataStore(path + "-batch");
        batch.SaveTelemetry(samples);
        batch.EnsureIntervalRules(TimeSpan.FromMinutes(15));
        var end = LastSample(live);
        var a = live.ReadEnergySummary(Midnight, end); var b = batch.ReadEnergySummary(Midnight, end);
        foreach (var metric in TelemetrySchema.EnergyMetrics) Assert.Equal(a.Metrics[metric].EnergyKwh!.Value, b.Metrics[metric].EnergyKwh!.Value, .01);
        Assert.Equal(a.NetCostGbp!.Value, b.NetCostGbp!.Value, .01);
        // The legacy rows were re-keyed at Home Assistant's update time by the rebuild, so they derive like live-keyed ones.
        Assert.Equal(live.ReadTelemetrySamples(Midnight, end, "load", 0, 1000).Select(x => x.Time), batch.ReadTelemetrySamples(Midnight, end, "load", 0, 1000).Select(x => x.Time));
        live.EnsureIntervalRules(TimeSpan.FromMinutes(15));
        var c = live.ReadEnergySummary(Midnight, end);
        foreach (var metric in TelemetrySchema.EnergyMetrics) Assert.Equal(a.Metrics[metric].EnergyKwh!.Value, c.Metrics[metric].EnergyKwh!.Value, .01);
    }

    [Fact]
    public void YesterdayIsTheCounterPlusAnEstimatedTailBeforeMidnight()
    {
        var samples = snapshot.Samples; var db = snapshot.Db;
        // The snapshot starts at 21:00 UTC on 4 Oct; the last two hours of that day carry the tail before the reset.
        var evening = db.ReadEnergySummary(Midnight.AddHours(-2), Midnight);
        var load = evening.Metrics["load"];
        var lastOldDay = samples.Where(x => x.Metric == "load" && x.Status == "observed" && x.Time < Midnight).MaxBy(x => x.Time)!;
        var firstOfEvening = samples.Where(x => x.Metric == "load" && x.Status == "observed" && x.Time >= Midnight.AddHours(-2)).MinBy(x => x.SourceUpdatedAt)!;
        var tail = load.EstimatedKwh ?? 0;
        Assert.InRange(tail, 0, .1);
        // Counted energy = the counter's rise over the evening plus the short estimated tail between its last reading and midnight.
        Assert.Equal(lastOldDay.Value!.Value - firstOfEvening.Value!.Value + tail, load.EnergyKwh!.Value, .05);
    }

    public void Dispose()
    {
        foreach (var dir in new[] { path, path + "-batch" }) if (Directory.Exists(dir)) Directory.Delete(dir, true);
    }
}
