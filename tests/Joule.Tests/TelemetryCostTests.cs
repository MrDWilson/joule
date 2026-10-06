using System.Globalization;
using Joule;
using Xunit;

namespace Joule.Tests;

/// <summary>Net cost, tariff boundaries and the cost evidence experiments rely on.</summary>
public sealed class TelemetryCostTests : IDisposable
{
    readonly string path = Path.Combine(Path.GetTempPath(), "predbat-cost-" + Guid.NewGuid().ToString("N"));
    static readonly DateTimeOffset Midnight = DateTimeOffset.Parse("2026-10-04T23:00:00Z"); // 5 Oct 00:00 BST
    static string Reset(DateTimeOffset at) => $"{{\"state_class\":\"total\",\"last_reset\":\"{(at >= Midnight ? "2026-10-05" : "2026-10-04")}T00:00:00+01:00\"}}";
    static TelemetrySample Meter(string metric, DateTimeOffset at, double? value) => value is null
        ? new(metric, "sensor." + metric, at, null, "", "HomeAssistant", "unknown", "", at, Reset(at), "idle")
        : new(metric, "sensor." + metric, at, value, "kWh", "HomeAssistant", value.Value.ToString(CultureInfo.InvariantCulture), "kWh", at, Reset(at));
    static TelemetrySample Rate(string metric, DateTimeOffset at, double? pence, DateTimeOffset? changed = null) => pence is null
        ? new(metric, "sensor." + metric, at, null, "", "HomeAssistant", "unavailable", "", null, "{}", "unavailable")
        : new(metric, "sensor." + metric, at, pence, "p/kWh", "HomeAssistant", (pence / 100).Value.ToString(CultureInfo.InvariantCulture), "GBP/kWh", changed ?? at);

    [Fact]
    public void OvernightImportStaysInNetCostWhileExportReadsUnknown()
    {
        using var db = new DataStore(path);
        // Intelligent Octopus Go night: 3 kWh per 5 minutes at 6.67p while the export counter reads unknown until 02:47 BST.
        var samples = new List<TelemetrySample> { Meter("grid_import", Midnight.AddMinutes(-3), 40), Meter("grid_export", Midnight.AddMinutes(-3), 2.2) };
        double imported = 0;
        for (var m = 2; m <= 167; m += 5)
        {
            var at = Midnight.AddMinutes(m); imported += m == 2 ? .1 : .5;
            samples.Add(Meter("grid_import", at, imported)); samples.Add(Meter("grid_export", at, null)); samples.Add(Rate("import_tariff", at, 6.67)); samples.Add(Rate("export_tariff", at, 15));
        }
        var end = Midnight.AddMinutes(172);
        samples.AddRange([Meter("grid_import", end, imported), Meter("grid_export", end, .6), Rate("import_tariff", end, 6.67), Rate("export_tariff", end, 15)]);
        foreach (var poll in samples.GroupBy(x => x.Time).OrderBy(x => x.Key)) db.SaveTelemetry(poll);
        var s = db.ReadEnergySummary(Midnight, end);
        Assert.Equal(imported * .0667, s.ImportCostGbp!.Value, 6);
        Assert.Equal(.6 * .15, s.ExportCreditGbp!.Value, 6);
        Assert.Equal(imported * .0667 - .6 * .15, s.NetCostGbp!.Value, 6);
        Assert.Equal(1, s.ImportCostCoverage, 6); Assert.Equal(1, s.ExportCostCoverage, 6);
        // With export a known zero overnight, the matched figure covers the whole night too.
        Assert.Equal(s.NetCostGbp!.Value, s.ObservedNetCostGbp!.Value, 6);
    }

    [Fact]
    public void NetCostIsTheDifferenceOfGrossFiguresEvenWhenTheMetersDoNotShareReadings()
    {
        using var db = new DataStore(path);
        var t = DateTimeOffset.Parse("2026-10-05T10:00:00Z");
        // Import readings every 5 minutes, export only every 10, offset by a minute: never a common boundary.
        db.SaveTelemetry([Meter("grid_import", t, 1), Meter("grid_import", t.AddMinutes(5), 2), Meter("grid_import", t.AddMinutes(10), 3),
            Meter("grid_export", t.AddMinutes(1), 1), Meter("grid_export", t.AddMinutes(11), 3), Rate("import_tariff", t, 25), Rate("export_tariff", t, 15)]);
        var s = db.ReadEnergySummary(t, t.AddMinutes(10));
        Assert.Equal(.5, s.ImportCostGbp!.Value, 9);
        Assert.Equal(2 * .9 * .15, s.ExportCreditGbp!.Value, 9);
        Assert.Equal(.5 - .27, s.NetCostGbp!.Value, 9);
    }

    [Fact]
    public void NetEarningsAreNegative()
    {
        using var db = new DataStore(path);
        var t = DateTimeOffset.Parse("2026-10-05T12:00:00Z");
        db.SaveTelemetry([Meter("grid_import", t, 1), Meter("grid_import", t.AddMinutes(30), 1.1), Meter("grid_export", t, 1), Meter("grid_export", t.AddMinutes(30), 3), Rate("import_tariff", t, 25), Rate("export_tariff", t, 15), Rate("import_tariff", t.AddMinutes(10), 25), Rate("export_tariff", t.AddMinutes(10), 15), Rate("import_tariff", t.AddMinutes(20), 25), Rate("export_tariff", t.AddMinutes(20), 15)]);
        var s = db.ReadEnergySummary(t, t.AddMinutes(30));
        Assert.Equal(.025 - .3, s.NetCostGbp!.Value, 9);
    }

    [Fact]
    public void RateChangeIsAppliedFromTheHalfHourNotFromThePoll()
    {
        using var db = new DataStore(path);
        var t = DateTimeOffset.Parse("2026-10-05T04:27:00Z");
        // 05:27→05:32 BST import of 1 kWh; the rate became 31.73p at 05:30, recorded by Home Assistant at 05:30:01 and polled at 05:32.
        db.SaveTelemetry([Meter("grid_import", t, 10), Meter("grid_import", t.AddMinutes(5), 11),
            Rate("import_tariff", t.AddMinutes(-25), 6.67, t.AddMinutes(-57)), Rate("import_tariff", t, 6.67, t.AddMinutes(-57)), Rate("import_tariff", t.AddMinutes(5), 31.73, t.AddMinutes(3).AddSeconds(1))]);
        var s = db.ReadEnergySummary(t, t.AddMinutes(5));
        Assert.Equal(.6 * .0667 + .4 * .3173, s.ImportCostGbp!.Value, 9);
        Assert.False(s.ImportCostEstimated);
    }

    [Fact]
    public void PlanRatesPriceEnergyWhileTheTariffSensorIsUnavailable()
    {
        using var db = new DataStore(path);
        var t = DateTimeOffset.Parse("2026-10-05T12:00:00Z");
        db.SavePlan(new PlanSnapshot { Source = "Predbat", At = t.AddHours(-1), CollectedAt = t.AddHours(-1), Slots = [new(t, .5, null, 0, null, 50, null, 24, 15, "Demand", 0), new(t.AddMinutes(30), .5, null, 0, null, 50, null, 26, 15, "Demand", 0)] });
        var samples = new List<TelemetrySample>();
        for (var m = 0; m <= 60; m += 5) { samples.Add(Meter("grid_import", t.AddMinutes(m), 10 + m * .01)); samples.Add(Rate("import_tariff", t.AddMinutes(m), m == 0 ? 25 : null)); }
        db.SaveTelemetry(samples);
        var s = db.ReadEnergySummary(t, t.AddMinutes(60));
        Assert.NotNull(s.ImportCostGbp);
        Assert.True(s.ImportCostEstimated);
        Assert.Equal(1, s.ImportCostCoverage, 6);
        Assert.Contains(db.Query("SELECT DISTINCT cost_method m FROM telemetry_intervals WHERE metric='grid_import'"), r => (string?)r["m"] == "plan_rate");
    }

    [Fact]
    public void TinyEnergyAcrossARateChangeIsStillPriced()
    {
        using var db = new DataStore(path);
        var t = DateTimeOffset.Parse("2026-10-05T04:00:00Z");
        // A bridged overnight interval with 0.02 kWh crossing the 05:30 BST rate change used to get no cost at all.
        db.SaveTelemetry([Meter("grid_import", t, 5), Meter("grid_import", t.AddMinutes(30), null), Meter("grid_import", t.AddMinutes(60), 5.02),
            Rate("import_tariff", t, 6.67), Rate("import_tariff", t.AddMinutes(10), 6.67), Rate("import_tariff", t.AddMinutes(20), 6.67), Rate("import_tariff", t.AddMinutes(30), 31.73), Rate("import_tariff", t.AddMinutes(40), 31.73), Rate("import_tariff", t.AddMinutes(50), 31.73), Rate("import_tariff", t.AddMinutes(60), 31.73)]);
        var s = db.ReadEnergySummary(t, t.AddMinutes(60));
        Assert.Equal(.01 * .0667 + .01 * .3173, s.ImportCostGbp!.Value, 9);
    }

    static EnergySummary Usable(double unpriced = 0, bool unknown = false, double estimated = 0) =>
        new(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow, [], 5, 1, 4, 1, 86400, [], []) { UnpricedGridKwh = unpriced, GridEnergyUnknown = unknown, EstimatedCostGbp = estimated };

    [Fact]
    public void ExperimentCostEvidenceDependsOnMissingEnergyAndEstimatedCostNotIntervalCounts()
    {
        Assert.True(TelemetryCostEvidence.Usable(Usable()));
        // A nightly reset or an idle export meter used to add "missing intervals" and block every comparison; they no longer count.
        var withResets = Usable() with { Metrics = new() { ["grid_import"] = new(10, 86400, 1, 5), ["grid_export"] = new(1, 86400, 1, 9) } };
        Assert.True(ExperimentEvaluator.CostUsable(withResets));
        Assert.False(TelemetryCostEvidence.Usable(Usable(unpriced: .5)));
        Assert.False(TelemetryCostEvidence.Usable(Usable(unknown: true)));
        Assert.False(TelemetryCostEvidence.Usable(Usable(estimated: 1)));
        Assert.True(TelemetryCostEvidence.Usable(Usable(estimated: .2)));
        // Live 4 Oct: a blip bridged by an estimate left the matched figure at 98.7% of the day while only £0.075 rested on it.
        Assert.True(TelemetryCostEvidence.Usable(Usable(estimated: .075) with { CostCoverageFraction = .987, CostObservedSeconds = .987 * 86400 }));
        // Three quarters of an hour without matched evidence is still too much, however it is priced.
        Assert.False(TelemetryCostEvidence.Usable(Usable() with { CostCoverageFraction = .969, CostObservedSeconds = 86400 - 2700 }));
    }

    [Fact]
    public void ALongGridGapRecordsTheMissingEnergyAndBlocksExperimentCost()
    {
        using var db = new DataStore(path);
        var t = DateTimeOffset.Parse("2026-10-05T08:00:00Z");
        var samples = new List<TelemetrySample>();
        for (var m = 0; m <= 360; m += 5)
        {
            var at = t.AddMinutes(m);
            samples.Add(Meter("grid_import", at, m is > 60 and < 250 ? null : 1 + m * .01) with { Status = m is > 60 and < 250 ? "unavailable" : "observed", RawState = m is > 60 and < 250 ? "unavailable" : "x" });
            samples.Add(Meter("grid_export", at, 1)); samples.Add(Rate("import_tariff", at, 25)); samples.Add(Rate("export_tariff", at, 15));
        }
        db.SaveTelemetry(samples);
        var s = db.ReadEnergySummary(t, t.AddMinutes(360));
        Assert.Equal(1.9, s.UnpricedGridKwh, 6);
        Assert.False(s.GridEnergyUnknown);
        Assert.Contains(s.CostGaps, g => g.Metric == "grid_import" && g.Reason == "offline");
        Assert.False(TelemetryCostEvidence.Usable(s));
    }

    public void Dispose() { if (Directory.Exists(path)) Directory.Delete(path, true); }
}
