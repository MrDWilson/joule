using System.Text.Json;
using Joule;
using Xunit;

namespace Joule.Tests;

public sealed class TelemetryBoundaryNumericsTests : IDisposable
{
    readonly string directory=Path.Combine(Path.GetTempPath(),"predbat-boundary-numerics-"+Guid.NewGuid().ToString("N"));
    [Theory] [InlineData(0,10,1)] [InlineData(2,6,.6)]
    public void FiniteBoundaryEnergyDoesNotOverflowItsIntermediateTimeAllocation(int offset,int minutes,double fraction)
    {
        using var db=new DataStore(directory);var start=DateTimeOffset.Parse("2020-01-01T10:00:00Z");
        var plan=new PlanSnapshot{Source="Predbat",At=start.AddMinutes(-10),CollectedAt=start.AddMinutes(-5),Slots=[new(start.AddMinutes(offset),1,null,1,null,50,null,25,15,"Demand",.2,minutes)]};
        db.SavePlan(plan);
        foreach(var metric in new[]{"load","pv"})db.SaveTelemetry([
            new(metric,"sensor."+metric,start,0,"kWh","HomeAssistant","0","kWh"),
            new(metric,"sensor."+metric,start.AddMinutes(10),1e308,"kWh","HomeAssistant","1e308","kWh")
        ]);
        var evidence=db.ReadPlanEvidence(plan.Id)!;var slot=Assert.Single(evidence.Slots);
        Assert.NotNull(slot.EstimatedLoadKwh);Assert.True(double.IsFinite(slot.EstimatedLoadKwh.Value));
        Assert.Equal(fraction,slot.EstimatedLoadKwh.Value/1e308,8);
        Assert.NotNull(slot.EstimatedPvKwh);Assert.True(double.IsFinite(slot.EstimatedPvKwh.Value));
        Assert.Equal(fraction,slot.EstimatedPvKwh.Value/1e308,8);
        JsonSerializer.Serialize(evidence,JsonDefaults.Options);
    }
    [Fact]
    public void FiniteAlternativeMeanDoesNotOverflowItsUnnormalizedErrorTotal()
    {
        using var db=new DataStore(directory);var start=DateTimeOffset.Parse("2020-01-01T10:00:00Z");
        var plan=new PlanSnapshot{Source="Predbat",At=start.AddMinutes(-10),CollectedAt=start.AddMinutes(-5),Slots=[new(start,1e308,null,0,null,50,null,25,15,"Demand",0),new(start.AddMinutes(30),1e308,null,0,null,50,null,25,15,"Demand",0)]};
        db.SavePlan(plan);
        db.SaveTelemetry([new("alternative_forecast","sensor.shadow",start.AddMinutes(-10),null,"","HomeAssistant","ready","",AttributesJson:JsonSerializer.Serialize(new{forecast_unit="kWh",forecast_kind="interval_energy",forecast=new[]{new{time=start,duration_minutes=30,load_kwh=0},new{time=start.AddMinutes(30),duration_minutes=30,load_kwh=0}}}))]);
        db.SaveTelemetry(Enumerable.Range(0,3).Select(i=>new TelemetrySample("load","sensor.load",start.AddMinutes(i*30),0,"kWh","HomeAssistant","0","kWh")),TimeSpan.FromMinutes(30));
        var comparison=AlternativeForecastService.Compare(db,plan.Id);
        Assert.True(comparison.Available,comparison.Reason);Assert.Equal(2,comparison.MatchedSlots);
        Assert.Equal(1e308,comparison.PredbatMaeKwhPerHalfHour);Assert.Equal(0,comparison.AlternativeMaeKwhPerHalfHour);
        JsonSerializer.Serialize(comparison,JsonDefaults.Options);
    }
    public void Dispose(){if(Directory.Exists(directory))Directory.Delete(directory,true);}
}
