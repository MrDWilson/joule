using Joule;
using Xunit;

namespace Joule.Tests;

/// <summary>
/// Live, the "solar generated today" sensor reads unknown from about 23:00 to 06:15 every night, which left 7 hours of every
/// day unmeasured. A counter that reads the same value (or restarts from zero) either side of an outage produced no energy in
/// between, so the outage is a measured zero. Outages across which the counter moved stay unmeasured.
/// </summary>
public sealed class FlatOutageIntervalTests : IDisposable
{
    readonly string path=Path.Combine(Path.GetTempPath(),"predbat-flat-outage-"+Guid.NewGuid().ToString("N"));
    static readonly DateTimeOffset Night=DateTimeOffset.Parse("2026-10-03T22:00:00Z");
    static TelemetrySample Reading(string metric,DateTimeOffset at,double? value,string entity="sensor.solar_today")=>value is null
        ? new(metric,entity,at,null,"kWh","HomeAssistant","unknown","",at,"{}","unavailable")
        : new(metric,entity,at,value,"kWh","HomeAssistant",value.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),"kWh",at);
    static IEnumerable<TelemetrySample> Overnight(string metric,double before,double after,string entity="sensor.solar_today")
    {
        for(var at=Night;at<=Night.AddHours(1);at=at.AddMinutes(5))yield return Reading(metric,at,before,entity);
        for(var at=Night.AddHours(1).AddMinutes(5);at<Night.AddHours(8);at=at.AddMinutes(5))yield return Reading(metric,at,null,entity);
        for(var at=Night.AddHours(8);at<=Night.AddHours(9);at=at.AddMinutes(5))yield return Reading(metric,at,after,entity);
    }

    [Fact]
    public void DailySolarCounterUnknownOvernightIsMeasuredZero()
    {
        using var db=new DataStore(path);
        // 4.162 kWh before midnight; after the daily reset the first reading is still 0.
        db.SaveTelemetry(Overnight("pv",4.162,0));
        var summary=db.ReadEnergySummary(Night,Night.AddHours(9)).Metrics["pv"];
        Assert.Equal(1,summary.CoverageFraction,6);Assert.Equal(0,summary.EnergyKwh);Assert.Equal(0,summary.MissingIntervals);
        Assert.All(db.ReadMeasuredHistory(Night,Night.AddHours(9),30).Slots,s=>Assert.Equal(0,s.Pv));
    }
    [Fact]
    public void UnchangedCounterAcrossOutageIsMeasuredZero()
    {
        using var db=new DataStore(path);
        db.SaveTelemetry(Overnight("pv",12.5,12.5));
        Assert.Equal(1,db.ReadEnergySummary(Night,Night.AddHours(9)).Metrics["pv"].CoverageFraction,6);
    }
    [Fact]
    public void CounterThatMovedAcrossOutageStaysUnmeasured()
    {
        using var db=new DataStore(path);
        db.SaveTelemetry(Overnight("load",30,33.4,"sensor.load_today"));
        var summary=db.ReadEnergySummary(Night,Night.AddHours(9)).Metrics["load"];
        Assert.True(summary.CoverageFraction<.3,$"coverage {summary.CoverageFraction}");Assert.True(summary.MissingIntervals>0);
        Assert.Contains(db.ReadMeasuredHistory(Night,Night.AddHours(9),30).Slots,s=>s.Load is null);
    }
    // Behaviour change: the 0.4 kWh after the reset used to be dropped. It is energy since the reset (Home Assistant's rule), so it now
    // counts in the totals as a spread interval: timing unknown, flagged estimated, and never a slot-precise measurement.
    [Fact]
    public void ResetToAMeaningfulValueAcrossOutageIsCountedButEstimated()
    {
        using var db=new DataStore(path);
        // After the reset the counter already shows 0.4 kWh: when that energy flowed is unknown, but that it flowed is not.
        db.SaveTelemetry(Overnight("pv",4.162,.4));
        var pv=db.ReadEnergySummary(Night.AddHours(1),Night.AddHours(9)).Metrics["pv"];
        Assert.Equal(.4,pv.EnergyKwh!.Value,6);Assert.Equal(1,pv.CoverageFraction,6);Assert.Equal("estimated",pv.State);Assert.Equal(.4,pv.EstimatedKwh!.Value,6);
        var history=db.ReadMeasuredHistory(Night.AddHours(1),Night.AddHours(8),30).Slots;
        Assert.All(history,s=>{Assert.Null(s.Pv);Assert.Equal("estimated",s.PvStatus);Assert.NotNull(s.PvEstimate);});
    }
    [Fact]
    public void RebuildingStoredHistoryAppliesTheRuleAndIsIdempotent()
    {
        using var db=new DataStore(path);
        db.SaveTelemetry(Overnight("pv",4.162,0));
        db.SaveTelemetry(Overnight("load",30,33.4,"sensor.load_today"));
        db.EnsureIntervalRules(TimeSpan.FromMinutes(10));db.EnsureIntervalRules(TimeSpan.FromMinutes(10));
        var metrics=db.ReadEnergySummary(Night,Night.AddHours(9)).Metrics;
        Assert.Equal(1,metrics["pv"].CoverageFraction,6);Assert.True(metrics["load"].CoverageFraction<.3);
        // The 3.4 kWh that flowed during the outage is never attributed to measured time.
        Assert.Equal(0,metrics["load"].EnergyKwh!.Value,6);
    }
    [Fact]
    public void OutageStartingBeforeMidnightStillCountsForTheNextDay()
    {
        using var db=new DataStore(path);
        // Live: the first reading after the daily reset was 0.001 kWh.
        db.SaveTelemetry(Overnight("pv",4.162,.001));
        // The day starts two hours into the outage; the flat interval began the evening before.
        var day=Night.AddHours(2);
        var pv=db.ReadEnergySummary(day,Night.AddHours(9)).Metrics["pv"];
        Assert.Equal(1,pv.CoverageFraction,6);Assert.InRange(pv.EnergyKwh!.Value,0,.001);
    }
    [Fact]
    public void RoundingDipOnAnIdleCounterIsZeroNotAReset()
    {
        using var db=new DataStore(path);
        db.SaveTelemetry([Reading("pv",Night,4.162),Reading("pv",Night.AddMinutes(5),4.1619999999),Reading("pv",Night.AddMinutes(10),4.162)]);
        var pv=db.ReadEnergySummary(Night,Night.AddMinutes(10)).Metrics["pv"];
        Assert.Equal(1,pv.CoverageFraction,6);Assert.Equal(0,pv.EnergyKwh!.Value,9);
    }
    public void Dispose(){try{Directory.Delete(path,true);}catch(IOException){}}
}
