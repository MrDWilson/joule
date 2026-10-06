using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Joule;
using Xunit;

namespace Joule.Tests;

public sealed class LiveTelemetryAuditTests : IDisposable
{
    readonly string folder = Path.Combine(Path.GetTempPath(), "predbat-live-telemetry-" + Guid.NewGuid().ToString("N"));
    static readonly DateTimeOffset At = DateTimeOffset.Parse("2020-01-01T10:00:00Z");
    static TelemetrySample Meter(string metric, int minute, double value) => new(metric, "sensor." + metric, At.AddMinutes(minute), value, metric.EndsWith("tariff") ? "p/kWh" : "kWh", "HomeAssistant", value.ToString(CultureInfo.InvariantCulture), "kWh");

    [Theory]
    [InlineData("load", "MWh")]
    [InlineData("import_tariff", "GBP/kWh")]
    public void OverflowingUnitConversionRemainsAnUnavailableReading(string metric, string unit)
    {
        var options = new HomeAssistantOptions(new ConfigurationBuilder().Build());
        var sample = new HomeAssistantClient(new HttpClient(), options).Parse(metric, "sensor.fixture", new JsonObject
        {
            ["state"] = "1e308", ["attributes"] = new JsonObject { ["unit_of_measurement"] = unit }
        }, At);
        Assert.Null(sample.Value);
        Assert.NotEqual("observed", sample.Status);
        using var db = new DataStore(folder);
        db.SaveTelemetry([sample]);
        Assert.Null(db.ReadLatestTelemetry()[metric].Value);
    }

    [Fact]
    public void FiniteTariffAndEnergyCannotProduceUnserializableInfiniteCosts()
    {
        using var db = new DataStore(folder);
        db.SaveTelemetry([Meter("grid_import", 0, 0), Meter("grid_import", 5, 1e308), Meter("grid_export", 0, 0), Meter("grid_export", 5, 0), Meter("import_tariff", 0, 1e308), Meter("export_tariff", 0, 15)]);
        var summary = db.ReadEnergySummary(At, At.AddMinutes(5));
        Assert.Null(summary.ImportCostGbp);
        Assert.Null(summary.ObservedNetCostGbp);
        Assert.Equal(0, summary.CostCoverageFraction);
        JsonSerializer.Serialize(summary, JsonDefaults.Options);
    }

    [Fact]
    public void FiniteIntervalCostsCannotOverflowPeriodTotalsOrClaimCostCoverage()
    {
        using var db = new DataStore(folder);
        db.SaveTelemetry([Meter("grid_import", 0, 0), Meter("grid_import", 5, 100), Meter("grid_import", 10, 200), Meter("grid_export", 0, 0), Meter("grid_export", 5, 0), Meter("grid_export", 10, 0), Meter("import_tariff", 0, 1e308), Meter("import_tariff", 5, 1e308), Meter("import_tariff", 10, 1e308), Meter("export_tariff", 0, 15)]);
        var summary = db.ReadEnergySummary(At, At.AddMinutes(10));
        Assert.Equal(200, summary.Metrics["grid_import"].EnergyKwh);
        Assert.Equal(1, summary.Metrics["grid_import"].CoverageFraction);
        Assert.Null(summary.ImportCostGbp);
        Assert.Null(summary.ObservedNetCostGbp);
        Assert.Equal(0, summary.CostCoverageFraction);
        Assert.Equal(0, summary.CostObservedSeconds);
        JsonSerializer.Serialize(summary, JsonDefaults.Options);
    }

    [Fact]
    public void SignedImportExportCostsCannotOverflowNetDifference()
    {
        using var db = new DataStore(folder);
        db.SaveTelemetry([Meter("grid_import", 0, 0), Meter("grid_import", 5, 100), Meter("grid_export", 0, 0), Meter("grid_export", 5, 100), Meter("import_tariff", 0, 1e308), Meter("export_tariff", 0, -1e308)]);
        var summary = db.ReadEnergySummary(At, At.AddMinutes(5));
        Assert.Equal(1e308, summary.ImportCostGbp);
        Assert.Equal(-1e308, summary.ExportCreditGbp);
        Assert.Null(summary.ObservedNetCostGbp);
        Assert.Equal(0, summary.CostCoverageFraction);
        JsonSerializer.Serialize(summary, JsonDefaults.Options);
    }

    [Fact]
    public void OverflowingEnergyAcrossCounterResetsCannotClaimUsablePeriodCoverage()
    {
        using var db = new DataStore(folder);
        db.SaveTelemetry([Meter("load", 0, 0), Meter("load", 5, 1e308), Meter("load", 10, 0), Meter("load", 15, 1e308)]);
        var summary = db.ReadEnergySummary(At, At.AddMinutes(15));
        Assert.Null(summary.Metrics["load"].EnergyKwh);
        Assert.Equal(0, summary.Metrics["load"].CoverageFraction);
        JsonSerializer.Serialize(summary, JsonDefaults.Options);
    }

    [Fact]
    public void FrozenPreSlotActualsCannotBecomeLiveHistoricalMeasurements()
    {
        using var db = new DataStore(folder);
        var plan = new PlanSnapshot { Source = "Predbat", At = At.AddMinutes(-10), CollectedAt = At.AddMinutes(-10), Slots = [new(At, 1, .6, 1, .3, 50, 55, 25, 15, "Demand", 0)] };
        db.SavePlan(plan);
        var slot = Assert.Single(db.GetPlan(plan.Id)!.Slots);
        Assert.Null(slot.LoadActual);
        Assert.Null(slot.PvActual);
        Assert.Null(slot.SocActual);
        Assert.Empty(db.ReadMatchedForecasts("load", At, At.AddHours(1)));
        Assert.Empty(db.ReadMatchedForecasts("pv", At, At.AddHours(1)));
        Assert.Empty(db.ReadMatchedForecastEstimates("load", At, At.AddHours(1)));
    }

    [Fact]
    public void FutureSlotsCannotDisplayEmbeddedActuals()
    {
        using var db = new DataStore(folder);
        var start = DateTimeOffset.UtcNow.AddHours(1);
        var plan = new PlanSnapshot { Source = "Predbat", At = start.AddMinutes(-10), CollectedAt = start.AddMinutes(-10), Slots = [new(start, 1, .6, 1, .3, 50, 55, 25, 15, "Demand", 0)] };
        db.SavePlan(plan);
        var slot = Assert.Single(db.GetPlan(plan.Id)!.Slots);
        Assert.Null(slot.LoadActual);
        Assert.Null(slot.PvActual);
        Assert.Null(slot.SocActual);
    }

    [Fact]
    public void LaterMeterObservationsProvideActualWithoutUsingUnverifiedPayloadOrReplacingForecast()
    {
        using var db = new DataStore(folder);
        var forecast = new PlanSnapshot { Source = "Predbat", At = At.AddMinutes(-10), CollectedAt = At.AddMinutes(-10), Slots = [new(At, 1, null, 1, null, 50, null, 25, 15, "Demand", 0)] };
        db.SavePlan(forecast);
        db.SavePlan(new PlanSnapshot { Source = "Predbat", At = At.AddMinutes(35), CollectedAt = At.AddMinutes(35), Slots = [new(At, 9, .6, 9, .3, 99, 55, 25, 15, "Demand", 0)] });
        Assert.Empty(db.ReadMatchedForecasts("load",At,At.AddHours(1)));
        db.SaveTelemetry([Meter("load",0,10),Meter("load",30,10.6)],TimeSpan.FromMinutes(30));
        var row = Assert.Single(db.ReadMatchedForecasts("load", At, At.AddHours(1)));
        Assert.Equal(1, row.Forecast);
        Assert.Equal(.6, row.Actual,8);
    }

    [Fact]
    public void LateCollectionCannotRetroactivelyBecomeAPreSlotForecast()
    {
        using var db = new DataStore(folder);
        var plan = new PlanSnapshot { Source = "Predbat", At = At.AddMinutes(-10), Slots = [new(At, 1, null, 1, null, 50, null, 25, 15, "Demand", 0)] };
        // SaveSource records the app receipt time. Generation alone is insufficient evidence of capture.
        db.SavePlan(plan, "{}", "{}");
        db.SaveTelemetry([Meter("load", 0, 10), Meter("load", 5, 11)]);
        Assert.Empty(db.ReadRecentTimeline(At, At.AddHours(1)));
        Assert.Empty(db.ReadMatchedForecastEstimates("load", At, At.AddHours(1)));
    }

    [Fact]
    public void UnknownLegacyCollectionCannotQualifyAStoredLiveForecast()
    {
        var plan = new PlanSnapshot { Source = "Predbat", At = At.AddMinutes(-10), Slots = [new(At, 1, null, 1, null, 50, null, 25, 15, "Demand", 0)] };
        using (var db = new DataStore(folder)) db.SavePlan(plan);
        using var reopened = new DataStore(folder);
        Assert.Empty(reopened.ReadRecentTimeline(At, At.AddHours(1)));
    }

    [Fact]
    public void RepeatedGenerationKeepsFirstCollectionAcrossDatabaseRestart()
    {
        var plan = new PlanSnapshot { Source = "Predbat", At = At.AddMinutes(-10), CollectedAt = At.AddMinutes(-5), Slots = [new(At, 1, null, 1, null, 50, null, 25, 15, "Demand", 0)] };
        using (var db = new DataStore(folder))
        {
            db.SavePlan(plan);
            db.SavePlan(new PlanSnapshot { Source = "Predbat", At = plan.At, CollectedAt = At.AddMinutes(10), Slots = [new(At, 9, null, 9, null, 50, null, 25, 15, "Demand", 0)] });
        }
        using var reopened = new DataStore(folder);
        Assert.Equal(At.AddMinutes(-5), reopened.GetPlan(plan.Id)!.CollectedAt);
        Assert.Equal(1, Assert.Single(reopened.ReadRecentTimeline(At, At.AddHours(1))).LoadForecast);
        Assert.Equal(At.AddMinutes(-5), Assert.Single(reopened.ListPlans(null, null).Items).CollectedAt);
    }

    [Fact]
    public void SpringDstAndPartialDayBoundariesHaveExactUtcDurations()
    {
        using var db = new DataStore(folder);
        var days = db.GetDailySummaries(DateTimeOffset.Parse("2026-03-29T00:00:00Z"), DateTimeOffset.Parse("2026-03-29T23:00:00Z"));
        Assert.Equal(23, Assert.Single(days).To.Subtract(days[0].From).TotalHours);
        var partial = db.GetDailySummaries(DateTimeOffset.Parse("2026-03-29T12:00:00Z"), DateTimeOffset.Parse("2026-03-30T00:00:00Z"));
        Assert.Equal(2, partial.Count);
        Assert.Equal(11, (partial[0].To - partial[0].From).TotalHours);
        Assert.Equal(1, (partial[1].To - partial[1].From).TotalHours);
    }

    [Fact]
    public void NegativeTariffsRemainSignedObservedCosts()
    {
        using var db = new DataStore(folder);
        db.SaveTelemetry([Meter("grid_import", 0, 1), Meter("grid_import", 5, 3), Meter("grid_export", 0, 1), Meter("grid_export", 5, 2), Meter("import_tariff", 0, -5), Meter("export_tariff", 0, -2)]);
        var summary = db.ReadEnergySummary(At, At.AddMinutes(5));
        Assert.Equal(-.1, summary.ImportCostGbp);
        Assert.Equal(-.02, summary.ExportCreditGbp);
        Assert.Equal(-.08, summary.ObservedNetCostGbp!.Value, 8);
        Assert.Equal(1, summary.CostCoverageFraction);
    }

    public void Dispose() { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
}
