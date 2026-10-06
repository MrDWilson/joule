using System.Text.Json;
using Joule;
using Xunit;
namespace Joule.Tests;
public sealed class AlternativeForecastTests : IDisposable
{
    readonly string directory=Path.Combine(Path.GetTempPath(),"predbat-alternative-"+Guid.NewGuid().ToString("N"));
    readonly DateTimeOffset start=DateTimeOffset.UtcNow.AddDays(-1);
    TelemetrySample Sample(DateTimeOffset captured,string unit="kWh", int duration=30) => new("alternative_forecast","sensor.shadow",captured,null,"","HomeAssistant","ready","",AttributesJson:JsonSerializer.Serialize(new {forecast_unit=unit,forecast_kind="interval_energy",forecast=new[]{new {time=start,duration_minutes=duration,load_kwh=.8}}}));
    PlanSnapshot Plan()=>new(){At=start.AddMinutes(-10),Slots=[new(start,1.2,.6,0,null,50,null,20,10,"Self-use",.1)]};
    [Fact] public void ComparisonUsesFrozenPrePlanEvidenceAndCommonMeasuredSlots()
    {
        using var db=new DataStore(directory);var plan=Plan();db.SavePlan(plan);db.SaveTelemetry([Sample(start.AddMinutes(-15)),Sample(start.AddMinutes(10))]);
        db.SaveTelemetry([new("load","sensor.load",start,10,"kWh","HomeAssistant","10","kWh"),new("load","sensor.load",start.AddMinutes(30),10.6,"kWh","HomeAssistant","10.6","kWh")],TimeSpan.FromMinutes(31));
        var result=AlternativeForecastService.Compare(db,plan.Id);
        Assert.True(result.Available);Assert.Equal(start.AddMinutes(-15),result.CapturedAt);
        Assert.Equal(.6,result.PredbatMaeKwhPerHalfHour!.Value,6);Assert.Equal(.2,result.AlternativeMaeKwhPerHalfHour!.Value,6);
        Assert.Equal(1,result.MatchedSlots);
    }
    [Theory][InlineData("W",30)][InlineData("kWh",15)]
    public void UnsupportedUnitsOrSlotBoundariesCannotProduceScores(string unit,int duration)
    {
        using var db=new DataStore(directory);var plan=Plan();db.SavePlan(plan);db.SaveTelemetry([Sample(start.AddMinutes(-15),unit,duration)]);
        var result=AlternativeForecastService.Compare(db,plan.Id);Assert.False(result.Available);Assert.Null(result.AlternativeMaeKwhPerHalfHour);
    }
    [Fact] public void MissingActualsRemainUnavailable()
    {
        using var db=new DataStore(directory);var plan=Plan();plan.Slots[0]=plan.Slots[0] with{LoadActual=null};db.SavePlan(plan);db.SaveTelemetry([Sample(start.AddMinutes(-15))]);
        var result=AlternativeForecastService.Compare(db,plan.Id);Assert.True(result.Available);Assert.Equal(0,result.MatchedSlots);Assert.Null(result.AlternativeMaeKwhPerHalfHour);
    }
    [Fact] public void ForecastCollectedAfterPlanCannotGiveAnAdvantage()
    {
        using var db=new DataStore(directory);var plan=Plan();db.SavePlan(plan);db.SaveTelemetry([Sample(start.AddMinutes(-5))]);
        Assert.False(AlternativeForecastService.Compare(db,plan.Id).Available);
    }
    [Fact] public void EmbeddedFutureActualCannotBeScoredAsACompletedMeasurement()
    {
        using var db=new DataStore(directory);var plan=Plan();db.SavePlan(plan);db.SaveTelemetry([Sample(start.AddMinutes(-15))]);
        var result=AlternativeForecastService.Compare(db,plan.Id);
        Assert.True(result.Available);Assert.Equal(0,result.MatchedSlots);Assert.Null(Assert.Single(result.Slots).ActualKwh);Assert.Null(result.AlternativeMaeKwhPerHalfHour);
    }
    [Fact] public void PartialMeterCoverageDoesNotAcquireAWholeIntervalScore()
    {
        using var db=new DataStore(directory);var plan=Plan();db.SavePlan(plan);db.SaveTelemetry([Sample(start.AddMinutes(-15))]);
        db.SaveTelemetry([new("load","sensor.load",start,10,"kWh","HomeAssistant","10","kWh"),new("load","sensor.load",start.AddMinutes(15),10.3,"kWh","HomeAssistant","10.3","kWh")],TimeSpan.FromMinutes(16));
        var result=AlternativeForecastService.Compare(db,plan.Id);Assert.True(result.Available);Assert.Equal(0,result.MatchedSlots);Assert.Null(result.PredbatMaeKwhPerHalfHour);
    }
    [Fact] public void LaterNativeCurveCannotProvideAnAuthoritativeAlternativeScore()
    {
        using var db=new DataStore(directory);
        var stamp=DateTimeOffset.Parse("2020-01-01T12:00:00Z");
        var plan=new PlanSnapshot { At=stamp.AddMinutes(-10),Slots=[new(stamp,1.2,null,0,null,50,null,20,10,"Self-use",.1)] };db.SavePlan(plan);
        db.SaveTelemetry([Sample(stamp.AddMinutes(-15)) with { AttributesJson=JsonSerializer.Serialize(new {forecast_unit="kWh",forecast_kind="interval_energy",forecast=new[]{new{time=stamp,duration_minutes=30,load_kwh=.8}}}) }]);
        db.SaveSource("""{"predbat.load_energy_actual":{"state":"3","attributes":{"results":{"2020-01-01T12:00:00Z":1,"2020-01-01T12:30:00Z":1.6}}}}""","{}");
        var result=AlternativeForecastService.Compare(db,plan.Id);Assert.True(result.Available);Assert.Equal(0,result.MatchedSlots);Assert.Null(result.AlternativeMaeKwhPerHalfHour);Assert.Null(Assert.Single(result.Slots).ActualKwh);
    }
    [Theory]
    [InlineData("{bad")]
    [InlineData("{\"forecast_unit\":\"kWh\",\"forecast_kind\":\"interval_energy\",\"forecast\":null}")]
    [InlineData("{\"forecast_unit\":\"kWh\",\"forecast_kind\":\"interval_energy\",\"forecast\":[{\"time\":\"2026-01-01T00:00:00Z\",\"duration_minutes\":30,\"load_kwh\":-1}]}")]
    public void MalformedForecastsRemainUnavailable(string attributes)
    {
        using var db=new DataStore(directory);var plan=Plan();db.SavePlan(plan);db.SaveTelemetry([Sample(start.AddMinutes(-15)) with { AttributesJson=attributes }]);
        var result=AlternativeForecastService.Compare(db,plan.Id);Assert.False(result.Available);Assert.Null(result.AlternativeMaeKwhPerHalfHour);
    }
    [Fact] public void RepeatedAlternativeIntervalsAreRejected()
    {
        using var db=new DataStore(directory);var plan=Plan();db.SavePlan(plan);
        var interval=new { time=start,duration_minutes=30,load_kwh=.8 };
        db.SaveTelemetry([Sample(start.AddMinutes(-15)) with { AttributesJson=JsonSerializer.Serialize(new { forecast_unit="kWh",forecast_kind="interval_energy",forecast=new[]{interval,interval} }) }]);
        Assert.False(AlternativeForecastService.Compare(db,plan.Id).Available);
    }
    [Fact] public void FutureIntervalsDoNotAcquireActualScores()
    {
        using var db=new DataStore(directory);var time=DateTimeOffset.UtcNow.AddHours(1);
        var plan=new PlanSnapshot { At=time.AddMinutes(-10),Slots=[new(time,1.2,.6,0,null,50,null,20,10,"Self-use",.1)] };db.SavePlan(plan);
        db.SaveTelemetry([Sample(time.AddMinutes(-15)) with { AttributesJson=JsonSerializer.Serialize(new {forecast_unit="kWh",forecast_kind="interval_energy",forecast=new[]{new{time,duration_minutes=30,load_kwh=.8}}}) }]);
        var result=AlternativeForecastService.Compare(db,plan.Id);Assert.True(result.Available);Assert.Equal(0,result.MatchedSlots);Assert.Null(Assert.Single(result.Slots).ActualKwh);
    }
    string NativeState(DateTimeOffset time,string status="active",bool reset=false)=>JsonSerializer.Serialize(new Dictionary<string,object>{
        ["sensor.predbat_load_ml_forecast"]=new { state=status,attributes=new {results=new Dictionary<string,double>{[time.ToString("O")]=10,[time.AddMinutes(15).ToString("O")]=10.3,[time.AddMinutes(30).ToString("O")]=reset?0:10.8}}}
    });
    [Fact] public void NativePredbatLoadMlProvidesCurveWithoutOptionalSensorMapping()
    {
        using var db=new DataStore(directory);var plan=Plan();plan.Source="Predbat";plan.CollectedAt=start.AddMinutes(-10);db.SavePlan(plan,NativeState(start),"{}");
        db.SaveTelemetry([new("load","sensor.house",start,10,"kWh","HomeAssistant","10","kWh"),new("load","sensor.house",start.AddMinutes(30),10.6,"kWh","HomeAssistant","10.6","kWh")],TimeSpan.FromMinutes(31));
        var result=AlternativeForecastService.Compare(db,plan.Id);
        Assert.True(result.Available);Assert.Equal("sensor.predbat_load_ml_forecast",result.EntityId);Assert.Equal(plan.CollectedAt,result.CapturedAt);Assert.Equal(.8,Assert.Single(result.Slots).AlternativeKwh,6);
        // Published native LoadML can exclude EV energy; a whole-house meter alone cannot establish equal target scope.
        Assert.Null(result.AlternativeMaeKwhPerHalfHour);Assert.Null(Assert.Single(result.Slots).ActualKwh);Assert.Contains("scope",result.Reason,StringComparison.OrdinalIgnoreCase);
    }
    [Theory][InlineData("not_initialized")][InlineData("insufficient_data")][InlineData("training")]
    public void NativeModelWarmupExplainsPredbatReadiness(string status)
    {
        using var db=new DataStore(directory);var plan=Plan();plan.Source="Predbat";plan.CollectedAt=start.AddMinutes(-10);db.SavePlan(plan,NativeState(start,status),"{}");
        var result=AlternativeForecastService.Compare(db,plan.Id);Assert.False(result.Available);Assert.Contains(status,result.Reason);Assert.DoesNotContain("Configure the optional sensor",result.Reason);
    }
    [Fact] public void NativeCumulativeResetCannotBecomeEnergy()
    {
        using var db=new DataStore(directory);var plan=Plan();plan.Source="Predbat";plan.CollectedAt=start.AddMinutes(-10);db.SavePlan(plan,NativeState(start,reset:true),"{}");
        var result=AlternativeForecastService.Compare(db,plan.Id);Assert.False(result.Available);Assert.Null(result.AlternativeMaeKwhPerHalfHour);Assert.Contains("boundaries",result.Reason);
    }
    [Fact] public void NativeForecastFetchedAfterPlanDoesNotRewriteFrozenAlternative()
    {
        using var db=new DataStore(directory);var plan=Plan();plan.Source="Predbat";plan.CollectedAt=start.AddMinutes(-10);db.SavePlan(plan,"{}","{}");db.SaveSource(NativeState(start),"{}");
        Assert.False(AlternativeForecastService.Compare(db,plan.Id).Available);
    }
    [Fact] public void NativePredbatOffsetWithoutColonUsesExactBoundaries()
    {
        using var db=new DataStore(directory);var time=DateTimeOffset.Parse("2026-10-01T10:30:00Z");
        var plan=new PlanSnapshot{Source="Predbat",At=time.AddMinutes(-10),CollectedAt=time.AddMinutes(-9),Slots=[new(time,1.2,null,0,null,50,null,20,10,"Self-use",.1)]};
        db.SavePlan(plan,"""{"sensor.predbat_load_ml_forecast":{"state":"active","attributes":{"results":{"2026-10-01T10:30:00+0000":10,"2026-10-01T11:00:00+0000":10.8}}}}""","{}");
        Assert.Equal(.8,Assert.Single(AlternativeForecastService.Compare(db,plan.Id).Slots).AlternativeKwh,6);
    }
    [Fact] public void NativeOldPublishTimestampIsStaleDespiteFreshCollection()
    {
        using var db=new DataStore(directory);var plan=Plan();plan.Source="Predbat";plan.CollectedAt=start.AddMinutes(-10);
        var raw=System.Text.Json.Nodes.JsonNode.Parse(NativeState(start))!.AsObject();raw["sensor.predbat_load_ml_forecast"]!["last_updated"]=start.AddHours(-3).ToString("O");
        db.SavePlan(plan,raw.ToJsonString(),"{}");
        var result=AlternativeForecastService.Compare(db,plan.Id);Assert.False(result.Available);Assert.Contains("stale",result.Reason,StringComparison.OrdinalIgnoreCase);
    }
    [Fact] public void MultipleNativeInstancesAreAmbiguousRatherThanNotPublished()
    {
        using var db=new DataStore(directory);var plan=Plan();plan.Source="Predbat";plan.CollectedAt=start.AddMinutes(-10);
        var raw=System.Text.Json.Nodes.JsonNode.Parse(NativeState(start))!.AsObject();raw["sensor.other_load_ml_forecast"]=raw["sensor.predbat_load_ml_forecast"]!.DeepClone();db.SavePlan(plan,raw.ToJsonString(),"{}");
        var result=AlternativeForecastService.Compare(db,plan.Id);Assert.False(result.Available);Assert.Equal("ambiguous_source",result.Status);Assert.DoesNotContain("not published",result.Reason);
    }
    [Fact] public void CorruptRetainedNativeSourceReturnsDiagnosticRatherThanThrowing()
    {
        var plan=Plan();plan.Source="Predbat";plan.CollectedAt=start.AddMinutes(-10);
        using(var db=new DataStore(directory))db.SavePlan(plan,NativeState(start),"{}");
        using(var connection=new DuckDB.NET.Data.DuckDBConnection($"Data Source={Path.Combine(directory,"predbat.duckdb")}"))
        {
            connection.Open();using var command=connection.CreateCommand();command.CommandText="UPDATE source_snapshots SET state_json='{broken'";command.ExecuteNonQuery();
        }
        using var reopened=new DataStore(directory);var result=AlternativeForecastService.Compare(reopened,plan.Id);
        Assert.False(result.Available);Assert.Equal("invalid_source",result.Status);
    }
    public void Dispose(){if(Directory.Exists(directory))Directory.Delete(directory,true);}
}
