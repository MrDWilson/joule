using Joule;
using Xunit;

namespace Joule.Tests;

/// <summary>
/// Chart actuals were always empty live because poll times drift off the :00/:30 boundaries, so exact-coverage never reached 1.
/// Elapsed slots now fall back to the boundary-allocated meter energy that the evidence endpoint already computed, labelled by method.
/// </summary>
public sealed class BoundaryActualTests : IDisposable
{
    readonly string path=Path.Combine(Path.GetTempPath(),"predbat-boundary-actual-"+Guid.NewGuid().ToString("N"));
    static readonly DateTimeOffset At=DateTimeOffset.Parse("2020-01-01T10:00:00Z");
    // Constant-rate meter: 1 kWh per 3000 s from 09:55:00, so any 30-minute window holds exactly 0.6 kWh regardless of where the samples land.
    static TelemetrySample Meter(string metric,string clock)
    {
        var time=DateTimeOffset.Parse("2020-01-01T"+clock+"Z");var value=10+(time-At.AddMinutes(-5)).TotalSeconds/3000;
        return new(metric,"sensor."+metric,time,value,"kWh","HomeAssistant",value.ToString(System.Globalization.CultureInfo.InvariantCulture),"kWh");
    }
    static PlanSnapshot Plan()=>new(){Source="Predbat",At=At.AddHours(-1),CollectedAt=At.AddHours(-1),Slots=[new(At,3,null,1,null,50,null,20,10,"Demand",.2)]};

    [Fact]
    public void JitteredPollTimesSpanningBothSlotEdgesGiveBoundaryAllocatedActual()
    {
        using var db=new DataStore(path);var plan=Plan();db.SavePlan(plan);
        db.SaveTelemetry(new[]{"09:55:00","10:00:07","10:05:09","10:10:08","10:15:10","10:20:06","10:25:09","10:30:11"}.Select(clock=>Meter("load",clock)));
        var slot=Assert.Single(db.GetPlan(plan.Id)!.Slots);
        Assert.NotNull(slot.LoadActual);Assert.Equal(.6,slot.LoadActual!.Value,9);Assert.Equal("boundary",slot.LoadActualMethod);
        Assert.Null(slot.PvActual);Assert.Null(slot.PvActualMethod);
        var timeline=Assert.Single(db.ReadRecentTimeline(At.AddHours(-2),At.AddHours(1)));
        Assert.Equal(.6,timeline.LoadActual!.Value,9);Assert.Equal("boundary",timeline.LoadActualMethod);
        // Behaviour change: the period summary now prorates the intervals that straddle its edges, so it agrees with the boundary actual.
        var summary=db.ReadEnergySummary(At,At.AddMinutes(30)).Metrics["load"];
        Assert.Equal(1,summary.CoverageFraction,6);Assert.Equal(.6,summary.EnergyKwh!.Value,9);
    }
    [Fact]
    public void SamplesExactlyOnSlotEdgesStayMeasured()
    {
        using var db=new DataStore(path);var plan=Plan();db.SavePlan(plan);
        db.SaveTelemetry(new[]{"10:00:00","10:05:00","10:10:00","10:15:00","10:20:00","10:25:00","10:30:00"}.Select(clock=>Meter("load",clock)));
        var slot=Assert.Single(db.GetPlan(plan.Id)!.Slots);
        Assert.Equal(.6,slot.LoadActual!.Value,9);Assert.Equal("measured",slot.LoadActualMethod);
    }
    // Behaviour change: a counter that drops to near zero has reset (Home Assistant's total_increasing rule), and its new value is the
    // energy since the reset. The slot used to be left null; it is now measured.
    [Fact]
    public void CounterResetInsideSlotCountsTheNewValueAsEnergySinceTheReset()
    {
        using var db=new DataStore(path);var plan=Plan();db.SavePlan(plan);
        double[] values=[10,10.1,10.2,0,.1,.2,.3];
        db.SaveTelemetry(values.Select((v,i)=>new TelemetrySample("load","sensor.load",At.AddMinutes(i*5),v,"kWh","HomeAssistant",v.ToString(System.Globalization.CultureInfo.InvariantCulture),"kWh")));
        Assert.Equal(1,Convert.ToInt32(db.Query("SELECT count(*) n FROM telemetry_intervals WHERE metric='load' AND method='reset' AND status='observed'")[0]["n"]));
        var slot=Assert.Single(db.GetPlan(plan.Id)!.Slots);
        Assert.Equal(.5,slot.LoadActual!.Value,9);Assert.Equal("measured",slot.LoadActualMethod);
        Assert.Equal(.5,Assert.Single(db.ReadPlanEvidence(plan.Id)!.Slots).EstimatedLoadKwh!.Value,9);
    }
    [Fact]
    public void AnUnexplainedSmallDropStaysAnUnknownReset()
    {
        using var db=new DataStore(path);var plan=Plan();db.SavePlan(plan);
        // 10.2 → 9.0 is neither jitter nor a reset to near zero: a meter fault, so the interval has no energy.
        double[] values=[10,10.1,10.2,9.0,9.1,9.2,9.3];
        db.SaveTelemetry(values.Select((v,i)=>new TelemetrySample("load","sensor.load",At.AddMinutes(i*5),v,"kWh","HomeAssistant",v.ToString(System.Globalization.CultureInfo.InvariantCulture),"kWh")));
        Assert.Equal(1,Convert.ToInt32(db.Query("SELECT count(*) n FROM telemetry_intervals WHERE metric='load' AND status='reset' AND energy_kwh IS NULL")[0]["n"]));
        var slot=Assert.Single(db.GetPlan(plan.Id)!.Slots);
        Assert.Null(slot.LoadActual);Assert.Null(slot.LoadActualMethod);
    }
    [Fact]
    public void UpcomingSlotHasNoActualOrMethod()
    {
        using var db=new DataStore(path);var start=DateTimeOffset.UtcNow.AddHours(1);
        var plan=new PlanSnapshot{Source="Predbat",At=start.AddMinutes(-10),CollectedAt=start.AddMinutes(-10),Slots=[new(start,1,.6,1,.3,50,55,25,15,"Demand",0,30,"measured","measured")]};
        db.SavePlan(plan);
        var slot=Assert.Single(db.GetPlan(plan.Id)!.Slots);
        Assert.Null(slot.LoadActual);Assert.Null(slot.LoadActualMethod);Assert.Null(slot.PvActual);Assert.Null(slot.PvActualMethod);
    }

    public void Dispose(){if(Directory.Exists(path))Directory.Delete(path,true);}
}
