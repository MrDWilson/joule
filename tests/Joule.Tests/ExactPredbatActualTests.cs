using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Joule;
using Xunit;

namespace Joule.Tests;

public sealed class ExactPredbatActualTests : IDisposable
{
    readonly string directory = Path.Combine(Path.GetTempPath(), "predbat-exact-actual-" + Guid.NewGuid().ToString("N"));
    static readonly DateTimeOffset Start = DateTimeOffset.Parse("2020-01-01T10:10:00Z");
    static PlanSnapshot Plan(int minutes) => new() { Source = "Predbat", At = Start.AddMinutes(-10), CollectedAt = Start.AddMinutes(-5), Slots = [new(Start, 3, null, 1, null, 50, null, 25, 15, "Demand", .2, minutes)] };
    static string State(params (DateTimeOffset Time, double Energy)[] points) => JsonSerializer.Serialize(new Dictionary<string, object> {
        ["sensor.predbat_load_energy_actual"] = new { state = "2", attributes = new { results = points.ToDictionary(p => p.Time.ToString("O"), p => p.Energy) } }
    });

    [Theory]
    [InlineData(15)]
    [InlineData(20)]
    [InlineData(30)]
    [InlineData(60)]
    public void LaterMappedMeterBoundariesEnrichFrozenArbitraryDurationPlan(int minutes)
    {
        using var db = new DataStore(directory);
        var plan = Plan(minutes);
        db.SavePlan(plan);
        db.SavePlan(Plan(minutes), State((Start, 10), (Start.AddMinutes(minutes), 12)), "{}");
        db.SaveTelemetry([new("load","sensor.house",Start,10,"kWh","HomeAssistant","10","kWh"),new("load","sensor.house",Start.AddMinutes(minutes),12,"kWh","HomeAssistant","12","kWh")],TimeSpan.FromMinutes(minutes));
        var actual = Assert.Single(db.GetPlan(plan.Id)!.Slots);
        Assert.Equal(2, actual.LoadActual);
        Assert.Equal(3, actual.LoadForecast);
        Assert.Equal(1, db.ListPlans(null, null).Total);
        var matched = Assert.Single(db.ReadMatchedForecastEstimates("load", Start, Start.AddMinutes(minutes)));
        Assert.Equal(2, matched.Actual);
        Assert.Equal(3, matched.Forecast);
        Assert.Equal(minutes * 60, matched.DurationSeconds);
    }

    [Fact]
    public void InteriorCumulativeResetCannotBecomeAnExactActualDespiteIncreasingEndpoints()
    {
        using var db = new DataStore(directory);
        var plan = Plan(60);
        db.SavePlan(plan);
        db.SaveSource(State((Start, 10), (Start.AddMinutes(20), 0), (Start.AddMinutes(60), 12)), "{}");
        Assert.Null(Assert.Single(db.GetPlan(plan.Id)!.Slots).LoadActual);
        Assert.Empty(db.ReadMatchedForecastEstimates("load", Start, Start.AddHours(1)));
    }

    [Fact]
    public void MissingExactEndBoundaryCannotBecomeAnActual()
    {
        using var db = new DataStore(directory);
        var plan = Plan(20);
        db.SavePlan(plan);
        db.SaveSource(State((Start, 10), (Start.AddMinutes(25), 12)), "{}");
        Assert.Null(Assert.Single(db.GetPlan(plan.Id)!.Slots).LoadActual);
    }

    [Fact]
    public void FutureCumulativeBoundariesCannotBeStoredAsCompletedActuals()
    {
        using var db = new DataStore(directory);
        var future = DateTimeOffset.UtcNow.AddHours(1);
        var plan = new PlanSnapshot { Source = "Predbat", At = DateTimeOffset.UtcNow, CollectedAt = DateTimeOffset.UtcNow, Slots = [new(future, 3, null, 1, null, 50, null, 25, 15, "Demand", .2, 20)] };
        db.SavePlan(plan);
        db.SaveSource(State((future, 10), (future.AddMinutes(20), 12)), "{}");
        Assert.Null(Assert.Single(db.GetPlan(plan.Id)!.Slots).LoadActual);
    }

    [Fact]
    public void OverflowingAllocatedForecastCannotBePublishedAsMatchedEvidence()
    {
        using var db = new DataStore(directory);
        var plan = Plan(5);
        plan.Slots = [new(Start, 1e308, null, 1, null, 50, null, 25, 15, "Demand", .2, 5), new(Start.AddMinutes(5), 1e308, null, 1, null, 50, null, 25, 15, "Demand", .2, 5)];
        db.SavePlan(plan);
        db.SaveTelemetry([new("load", "sensor.load", Start, 10, "kWh", "HomeAssistant", "10", "kWh"), new("load", "sensor.load", Start.AddMinutes(10), 11, "kWh", "HomeAssistant", "11", "kWh")]);
        Assert.Empty(db.ReadMatchedForecastEstimates("load", Start, Start.AddMinutes(10)));
    }

    [Fact]
    public void ExactPredbatActualsCannotDoubleCountPartiallyOverlappingTelemetry()
    {
        using var db = new DataStore(directory);
        var plan = Plan(20);
        db.SavePlan(plan);
        db.SaveSource(State((Start, 10), (Start.AddMinutes(20), 12)), "{}");
        db.SaveTelemetry([new("load", "sensor.load", Start, 10, "kWh", "HomeAssistant", "10", "kWh"), new("load", "sensor.load", Start.AddMinutes(5), 11, "kWh", "HomeAssistant", "11", "kWh")]);
        var matched = Assert.Single(db.ReadMatchedForecastEstimates("load", Start, Start.AddMinutes(20)));
        Assert.Equal(300, matched.DurationSeconds);
        Assert.Equal(1, matched.Actual);
    }

    [Fact]
    public void CorrectedNativeCurveRemainsDiagnosticWithOrWithoutInteriorReset()
    {
        using var db = new DataStore(directory);
        var plan = Plan(60);
        db.SavePlan(plan);
        db.SaveSource(State((Start, 10), (Start.AddMinutes(60), 12)), "{}");
        Assert.Null(Assert.Single(db.GetPlan(plan.Id)!.Slots).LoadActual);
        db.SaveSource(State((Start, 10), (Start.AddMinutes(20), 0), (Start.AddMinutes(60), 12)), "{}");
        Assert.Null(Assert.Single(db.GetPlan(plan.Id)!.Slots).LoadActual);
    }

    [Fact]
    public void InvalidRetainedIntervalSuppressesAnOlderEmbeddedObservedActual()
    {
        using var db = new DataStore(directory);
        var plan = Plan(60);
        plan.CollectedAt = Start.AddMinutes(65);
        plan.Slots[0] = plan.Slots[0] with { LoadActual = 2 };
        db.SavePlan(plan, State((Start, 10), (Start.AddMinutes(20), 0), (Start.AddMinutes(60), 12)), "{}");
        Assert.Null(Assert.Single(db.GetPlan(plan.Id)!.Slots).LoadActual);
    }

    [Fact]
    public async Task InitialPredbatCaptureRejectsAnInteriorCumulativeReset()
    {
        var rawState = State((Start, 10), (Start.AddMinutes(20), 0), (Start.AddMinutes(60), 12));
        var rawPlan = JsonSerializer.Serialize(new { plan = new { timestamp = Start.AddMinutes(-5), rows = new[] { Start, Start.AddMinutes(60) }.Select(time => new { time, load_forecast = 3, pv_forecast = 1, soc_percent = 50, import_rate = 25, export_rate = 15, state = "Demand", cost_change = .2 }) } });
        var client = new PredbatClient(new HttpClient(new FixtureHandler(rawState,rawPlan)), new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> { ["Predbat:BaseUrl"] = "http://fixture.test" }).Build());
        var snapshot = await client.CollectAsync(CancellationToken.None);
        Assert.Null(snapshot.Plan!.Slots[0].LoadActual);
    }

    [Fact]
    public void DashboardPlanReferencesPreserveExistingMetadataContract()
    {
        using var db = new DataStore(directory);
        var plan = Plan(20);
        db.SavePlan(plan);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(db.ListPlans(), JsonDefaults.Options));
        var item = Assert.Single(json.RootElement.EnumerateArray());
        Assert.Equal(plan.Id, item.GetProperty("id").GetString());
        Assert.Equal(plan.At, item.GetProperty("at").GetDateTimeOffset());
        Assert.Equal(plan.Source, item.GetProperty("source").GetString());
    }

    [Fact]
    public void ExplicitAlternativeCanScoreRetainedTwentyMinuteMeterActualAfterRestart()
    {
        var plan = Plan(20);
        using (var db = new DataStore(directory))
        {
            db.SavePlan(plan);
            db.SaveTelemetry([new("alternative_forecast", "sensor.shadow", Start.AddMinutes(-10), null, "", "HomeAssistant", "ready", "", AttributesJson: JsonSerializer.Serialize(new { forecast_unit = "kWh", forecast_kind = "interval_energy", forecast = new[] { new { time = Start, duration_minutes = 20, load_kwh = 2 } } }))]);
            db.SaveSource(State((Start, 10), (Start.AddMinutes(20), 12)), "{}");
            db.SaveTelemetry([new("load","sensor.house",Start,10,"kWh","Predbat mirror","10","kWh"),new("load","sensor.house",Start.AddMinutes(20),12,"kWh","Predbat mirror","12","kWh")],TimeSpan.FromMinutes(20));
        }
        using var reopened = new DataStore(directory);
        var comparison = AlternativeForecastService.Compare(reopened, plan.Id);
        Assert.Equal("scored", comparison.Status);
        Assert.Equal(2, Assert.Single(comparison.Slots).ActualKwh);
        Assert.Equal(0, comparison.AlternativeMaeKwhPerHalfHour);
        Assert.Equal(1.5, comparison.PredbatMaeKwhPerHalfHour);
    }

    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    sealed class FixtureHandler(string state,string plan) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct) => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(request.RequestUri!.AbsolutePath.EndsWith("plan_data") ? plan : state) });
    }
}
