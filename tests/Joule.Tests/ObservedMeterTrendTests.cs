using System.Text.Json;
using Joule;
using Xunit;

namespace Joule.Tests;

public sealed class ObservedMeterTrendTests : IDisposable
{
    readonly string directory = Path.Combine(Path.GetTempPath(), "predbat-meter-trends-" + Guid.NewGuid().ToString("N"));
    static readonly DateTimeOffset Start = DateTimeOffset.Parse("2020-01-01T10:00:03Z");
    static TelemetrySample Sample(string metric,DateTimeOffset at,double? value) => new(metric,"sensor."+metric,at,value,"kWh","HomeAssistant",value?.ToString()??"unavailable","kWh",Status:value is null?"unavailable":"observed");

    [Fact]
    public void JitteredWholeMeterIntervalsProvideMeasuredAveragePowerWithoutPlanBoundaries()
    {
        using var db = new DataStore(directory);
        var end = Start.AddSeconds(301);
        db.SaveTelemetry([Sample("load",Start,10),Sample("load",end,10.5),Sample("pv",Start,2),Sample("pv",end,2.1)]);
        var result = db.ReadObservedMeterTrends(Start,end);
        Assert.False(result.Truncated);
        var load = Assert.Single(result.Intervals,p=>p.Metric=="load");
        var pv = Assert.Single(result.Intervals,p=>p.Metric=="pv");
        Assert.Equal(.5*3600/301,load.AverageKw!.Value,8);
        Assert.Equal(.1*3600/301,pv.AverageKw!.Value,8);
        Assert.Equal(Start,load.Start);Assert.Equal(end,load.End);
        Assert.Equal("sensor.load",load.EntityId);Assert.Equal("HomeAssistant",load.Source);
        Assert.Contains("whole observed meter interval",result.Method);
    }

    // Behaviour change: a reset now counts its new value (0 here) as energy since the reset, and a short outage or a missed poll
    // becomes one "spread" interval with the counter difference. Spread intervals never report power (they cannot set a peak).
    [Fact]
    public void ZeroConsumptionRemainsValidResetsCountAndOutagesAreSpreadWithoutPower()
    {
        using var db = new DataStore(directory);
        db.SaveTelemetry([Sample("load",Start,0),Sample("load",Start.AddMinutes(5),1),Sample("load",Start.AddMinutes(10),0),Sample("load",Start.AddMinutes(15),null),Sample("load",Start.AddMinutes(20),1),Sample("load",Start.AddMinutes(40),2),Sample("load",Start.AddMinutes(45),2)]);
        var points = db.ReadObservedMeterTrends(Start,Start.AddMinutes(45)).Intervals;
        Assert.Equal(new[]{"observed","observed","spread","spread","observed"},points.Select(p=>p.Status));
        Assert.Equal(0,points[1].AverageKw);
        Assert.All(points.Skip(2).Take(2),p=>Assert.Null(p.AverageKw));
        Assert.Equal(0,points[^1].AverageKw);
    }

    [Fact]
    public void NonfiniteDerivedPowerIsUnavailableAndSerializable()
    {
        using var db = new DataStore(directory);
        db.SaveTelemetry([Sample("load",Start,0),Sample("load",Start.AddMinutes(5),1e308)]);
        var result = db.ReadObservedMeterTrends(Start,Start.AddMinutes(5));
        var point = Assert.Single(result.Intervals);
        Assert.Null(point.AverageKw);Assert.Equal("invalid_numeric_range",point.Status);
        JsonSerializer.Serialize(result,JsonDefaults.Options);
    }

    [Fact]
    public void FutureAndPartiallyOverlappingIntervalsAreExcluded()
    {
        using var db = new DataStore(directory);
        var now = DateTimeOffset.UtcNow;
        db.SaveTelemetry([Sample("load",now.AddMinutes(-5),0),Sample("load",now.AddMinutes(5),1)]);
        Assert.Empty(db.ReadObservedMeterTrends(now.AddMinutes(-10),now.AddMinutes(10)).Intervals);
        db.SaveTelemetry([Sample("pv",Start,0),Sample("pv",Start.AddMinutes(5),1)]);
        Assert.Empty(db.ReadObservedMeterTrends(Start.AddMinutes(1),Start.AddMinutes(5)).Intervals);
    }

    [Fact]
    public void ResponseKeepsNewestBoundedIntervalsAndDeclaresTruncation()
    {
        using var db = new DataStore(directory);
        db.SaveTelemetry(Enumerable.Range(0,1003).Select(i=>Sample("load",Start.AddSeconds(i),i/3600d)));
        var result = db.ReadObservedMeterTrends(Start,Start.AddSeconds(1002));
        Assert.True(result.Truncated);Assert.Equal(1000,result.Limit);Assert.Equal(result.Limit,result.Intervals.Count);
        Assert.Equal(Start.AddSeconds(2),result.Intervals[0].Start);
        Assert.Equal(Start.AddSeconds(1002),result.Intervals[^1].End);
    }

    [Fact]
    public void PeriodMustBePositiveAndAtMostSevenDays()
    {
        using var db = new DataStore(directory);
        Assert.Throws<DomainException>(()=>db.ReadObservedMeterTrends(Start,Start));
        Assert.Throws<DomainException>(()=>db.ReadObservedMeterTrends(Start,Start.AddDays(8)));
    }

    public void Dispose(){if(Directory.Exists(directory))Directory.Delete(directory,true);}
}
