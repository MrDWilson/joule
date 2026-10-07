using System.Net;
using System.Text.Json;
using DuckDB.NET.Data;
using Microsoft.Extensions.Configuration;
using Joule;
using Xunit;

namespace Joule.Tests;

public sealed class NativeActualProvenanceTests : IDisposable
{
    readonly string directory=Path.Combine(Path.GetTempPath(),"predbat-native-provenance-"+Guid.NewGuid().ToString("N"));
    static readonly DateTimeOffset Start=DateTimeOffset.Parse("2020-01-01T10:00:00Z");
    const string NativeState="""
        {"predbat.load_energy_actual":{"state":"30","last_changed":"2020-01-01T09:00:00Z","attributes":{"unit_of_measurement":"kWh","results":{"2020-01-01T10:00:00Z":1,"2020-01-01T10:30:00Z":9}}}}
        """;
    static PlanSnapshot Plan()=>new(){Source="Predbat",At=Start.AddMinutes(-10),CollectedAt=Start.AddMinutes(-5),Slots=[new(Start,2,null,1,null,50,null,25,15,"Demand",.2)]};
    static TelemetrySample Meter(string metric,int minute,double value)=>new(metric,"sensor."+metric,Start.AddMinutes(minute),value,metric=="soc"?"%":"kWh","HomeAssistant",value.ToString(System.Globalization.CultureInfo.InvariantCulture),metric=="soc"?"%":"kWh");
    void LegacyRows()
    {
        using var connection=new DuckDBConnection($"Data Source={Path.Combine(directory,DataFiles.Database)}");connection.Open();
        using var command=connection.CreateCommand();
        command.CommandText="""
            INSERT INTO actual_energy VALUES ('2020-01-01T11:00:00Z','2020-01-01T10:00:00Z',8);
            INSERT INTO predbat_actual_intervals VALUES ('2020-01-01T10:00:00Z','2020-01-01T10:30:00Z','2020-01-01T11:00:00Z',8,'predbat.load_energy_actual','Predbat cumulative load results');
            """;command.ExecuteNonQuery();
    }
    [Fact]
    public async Task FreshlyFetchedNativeCurveCannotBecomeAnEmbeddedMeasuredActual()
    {
        var rawPlan=JsonSerializer.Serialize(new{plan=new{timestamp=Start.AddMinutes(-10),rows=new[]{Start,Start.AddMinutes(30)}.Select(time=>new{time,load_forecast=2,pv_forecast=1,soc_percent=50,import_rate=25,export_rate=15,state="Demand",cost_change=.2})}});
        var config=new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{["Predbat:BaseUrl"]="http://fixture.test"}).Build();
        var client=new PredbatClient(new HttpClient(new Handler(rawPlan)),config);
        var snapshot=await client.CollectAsync(default);
        Assert.Null(snapshot.Plan!.Slots[0].LoadActual);
        Assert.Contains("load_energy_actual",snapshot.RawState);Assert.Contains("2020-01-01T10:30:00Z",snapshot.RawState);
    }
    [Fact]
    public void NativeCollectionPreservesSourceDiagnosticsWithoutCreatingMeasuredEnergyRows()
    {
        using var db=new DataStore(directory);var plan=Plan();db.SavePlan(plan,NativeState,"{}");db.SaveSource(NativeState,"{}");
        Assert.Null(Assert.Single(db.GetPlan(plan.Id)!.Slots).LoadActual);
        Assert.Empty(db.Query("SELECT * FROM actual_energy"));Assert.Empty(db.Query("SELECT * FROM predbat_actual_intervals"));
        // The identical second collection adds no duplicate observation or snapshot; the plan's own snapshot keeps the curve.
        Assert.Single(db.Query("SELECT * FROM observations WHERE entity_id='predbat.load_energy_actual'"));
        using var connection=new DuckDBConnection($"Data Source={Path.Combine(directory,DataFiles.Database)}");connection.Open();using var command=connection.CreateCommand();
        command.CommandText="SELECT count(*) FROM source_snapshots WHERE state_json LIKE '%load_energy_actual%'";
        Assert.Equal(1,Convert.ToInt32(command.ExecuteScalar()));
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public void LegacyNativeAndEmbeddedActualsRemainDiagnosticAfterRestart(bool addMeters)
    {
        var plan=Plan();
        using(var db=new DataStore(directory))
        {
            db.SavePlan(plan);
            db.SavePlan(new PlanSnapshot{Source="Predbat",At=Start.AddHours(1),CollectedAt=Start.AddHours(1),Slots=[plan.Slots[0] with{LoadActual=8,PvActual=6,SocActual=77}]});
            db.SaveTelemetry([new("alternative_forecast","sensor.shadow",Start.AddMinutes(-10),null,"","HomeAssistant","ready","",AttributesJson:JsonSerializer.Serialize(new{forecast_unit="kWh",forecast_kind="interval_energy",forecast=new[]{new{time=Start,duration_minutes=30,load_kwh=1.2}}}))]);
            LegacyRows();
            if(addMeters)db.SaveTelemetry([Meter("load",0,10),Meter("load",30,11.2),Meter("pv",0,20),Meter("pv",30,20.5),Meter("soc",30,55)],TimeSpan.FromMinutes(30));
        }
        using var reopened=new DataStore(directory);
        foreach(var id in new[]{plan.Id,(string?)null})
        {
            var slot=Assert.Single(reopened.GetPlan(id)!.Slots);
            CheckMeasurement(slot.LoadActual,1.2,addMeters);CheckMeasurement(slot.PvActual,.5,addMeters);CheckMeasurement(slot.SocActual,55,addMeters);
        }
        foreach(var metric in new[]{"load","pv"})
        {
            Assert.Equal(addMeters?1:0,reopened.ReadMatchedForecasts(metric,Start,Start.AddMinutes(30)).Count);
            Assert.Equal(addMeters?1:0,reopened.ReadMatchedForecastEstimates(metric,Start,Start.AddMinutes(30)).Count);
        }
        var comparison=AlternativeForecastService.Compare(reopened,plan.Id);
        Assert.Equal(addMeters?"scored":"awaiting_actuals",comparison.Status);
        Assert.Equal(addMeters?1:0,comparison.MatchedSlots);
        CheckMeasurement(Assert.Single(comparison.Slots).ActualKwh,1.2,addMeters);
        // Historical rows are retained for diagnostics, never rewritten into zeros or deleted.
        Assert.Equal(8,Convert.ToDouble(Assert.Single(reopened.Query("SELECT load_actual FROM actual_energy"))["load_actual"]));
        Assert.Equal(8,Convert.ToDouble(Assert.Single(reopened.Query("SELECT load_actual FROM predbat_actual_intervals"))["load_actual"]));
        Assert.Equal(8,Convert.ToDouble(Assert.Single(reopened.Query("SELECT load_actual FROM plan_slots WHERE load_actual IS NOT NULL"))["load_actual"]));
    }
    [Fact]
    public void PartialMetersCannotBeCompletedByAnUnverifiedNativeCurve()
    {
        using var db=new DataStore(directory);var plan=Plan();db.SavePlan(plan);LegacyRows();
        db.SaveTelemetry([Meter("load",0,10),Meter("load",15,10.3)],TimeSpan.FromMinutes(15));
        Assert.Null(Assert.Single(db.GetPlan(plan.Id)!.Slots).LoadActual);
        Assert.Empty(db.ReadMatchedForecasts("load",Start,Start.AddMinutes(30)));
        var partial=Assert.Single(db.ReadMatchedForecastEstimates("load",Start,Start.AddMinutes(30)));
        Assert.Equal(900,partial.DurationSeconds);Assert.Equal(.3,partial.Actual,8);Assert.Equal(1,partial.Forecast,8);
    }
    [Theory] [InlineData("Demo",1)] [InlineData("Predbat",0)]
    public void DemoPayloadActualsOnlyScoreDemoForecasts(string source,int expectedMatches)
    {
        using var db=new DataStore(directory);var forecast=Plan();forecast.Source=source;db.SavePlan(forecast);
        db.SavePlan(new PlanSnapshot{Source="Demo",At=Start.AddHours(1),CollectedAt=Start.AddHours(1),Slots=[forecast.Slots[0] with{LoadActual=8}]});
        Assert.Equal(expectedMatches,db.ReadMatchedForecasts("load",Start,Start.AddMinutes(30)).Count);
        Assert.Equal(expectedMatches,db.ReadMatchedForecastEstimates("load",Start,Start.AddMinutes(30)).Count);
    }
    static void CheckMeasurement(double? actual,double expected,bool hasMeters)
    {
        if(hasMeters){Assert.NotNull(actual);Assert.Equal(expected,actual.Value,8);}else Assert.Null(actual);
    }
    sealed class Handler(string plan):HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)=>Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(request.RequestUri!.AbsolutePath.EndsWith("plan_data")?plan:NativeState)});
    }
    public void Dispose(){if(Directory.Exists(directory))Directory.Delete(directory,true);}
}
