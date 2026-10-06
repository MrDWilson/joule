using Joule;
using Xunit;

namespace Joule.Tests;

/// <summary>Measured history on a fixed slot grid backs the "compare with yesterday / last week" overlays.</summary>
public sealed class MeasuredHistoryTests : IDisposable
{
    readonly string path=Path.Combine(Path.GetTempPath(),"predbat-measured-history-"+Guid.NewGuid().ToString("N"));
    static readonly DateTimeOffset At=DateTimeOffset.Parse("2020-01-01T10:00:00Z");
    // Constant-rate meter: 1 kWh per 3000 s, so every whole 30-minute slot holds exactly 0.6 kWh wherever the polls land.
    static TelemetrySample Meter(string metric,DateTimeOffset time,bool available=true)
    {
        var value=10+(time-At.AddMinutes(-5)).TotalSeconds/3000;
        return available?new(metric,"sensor."+metric,time,value,"kWh","HomeAssistant",value.ToString(System.Globalization.CultureInfo.InvariantCulture),"kWh")
            :new(metric,"sensor."+metric,time,null,"kWh","HomeAssistant","unavailable","",Status:"unavailable");
    }
    static IEnumerable<TelemetrySample> Jittered(string metric,DateTimeOffset from,int count,Func<int,bool>? available=null)=>
        Enumerable.Range(0,count).Select(i=>Meter(metric,from.AddMinutes(5*i).AddSeconds(i%3*4),available?.Invoke(i)??true));

    [Fact]
    public void JitteredPollsGiveWholeSlotEnergyOnTheRequestedGrid()
    {
        using var db=new DataStore(path);
        db.SaveTelemetry(Jittered("load",At.AddMinutes(-5),26).Concat(Jittered("pv",At.AddMinutes(-5),26)));
        var history=db.ReadMeasuredHistory(At,At.AddHours(2),30);
        Assert.Equal(4,history.Slots.Count);Assert.Equal(30,history.SlotMinutes);
        Assert.Equal([At,At.AddMinutes(30),At.AddMinutes(60),At.AddMinutes(90)],history.Slots.Select(s=>s.Time));
        Assert.All(history.Slots,s=>{Assert.Equal(.6,s.Load!.Value,9);Assert.Equal(.6,s.Pv!.Value,9);Assert.Equal(30,s.DurationMinutes);});
        var fine=db.ReadMeasuredHistory(At,At.AddHours(1),5);
        Assert.Equal(12,fine.Slots.Count);Assert.All(fine.Slots,s=>Assert.Equal(.1,s.Load!.Value,9));
        Assert.Contains("never zero",history.Method);
    }

    [Fact]
    public void MissingReadingsLeaveNullSlotsNotZeros()
    {
        using var db=new DataStore(path);
        // Polls 8–11 (10:35–10:50) are unavailable: the 10:30 slot cannot be accounted for; its neighbours can.
        db.SaveTelemetry(Jittered("load",At.AddMinutes(-5),26,i=>i is < 8 or > 11));
        var slots=db.ReadMeasuredHistory(At,At.AddHours(2),30).Slots;
        Assert.NotNull(slots[0].Load);Assert.Null(slots[1].Load);Assert.NotNull(slots[2].Load);Assert.NotNull(slots[3].Load);
        Assert.All(slots,s=>Assert.Null(s.Pv));
    }

    [Fact]
    public void CounterResetAndUnfinishedSlotsStayNull()
    {
        using var db=new DataStore(path);
        var now=DateTimeOffset.UtcNow;var start=new DateTimeOffset(now.Year,now.Month,now.Day,now.Hour,now.Minute/5*5,0,TimeSpan.Zero).AddHours(-1);
        db.SaveTelemetry(Enumerable.Range(0,13).Select(i=>new TelemetrySample("load","sensor.load",start.AddMinutes(5*i),i<6?100+i:i-6,"kWh","HomeAssistant","x","kWh")));
        var slots=db.ReadMeasuredHistory(start,start.AddHours(2),30).Slots;
        Assert.Equal(4,slots.Count);
        // Behaviour change: the counter resets to 0 at +30 min; the new value (0) is the energy since the reset, so the slot is measured.
        Assert.Equal(5,slots[0].Load!.Value,9);Assert.Equal("measured",slots[0].LoadStatus);
        Assert.Equal(6,slots[1].Load!.Value,9);// counting resumes from zero
        Assert.Null(slots[2].Load);Assert.Null(slots[3].Load);Assert.Equal("pending",slots[3].LoadStatus);// not finished yet
    }

    [Theory]
    [InlineData(7,30)]
    [InlineData(0,30)]
    [InlineData(30,-1)]
    [InlineData(30,0)]
    [InlineData(5,24*7+1)]
    public void InvalidRequestsAreRejected(int slotMinutes,int hours)
    {
        using var db=new DataStore(path);
        var error=Assert.Throws<DomainException>(()=>db.ReadMeasuredHistory(At,At.AddHours(hours),slotMinutes));
        Assert.Equal(400,error.Status);
    }

    // Demo history pinned to Monday 5 October 2026 19:30 (BST), so the outage day and car evenings are fixed.
    static readonly DateTimeOffset DemoNow=CivilTime.FirstValidInstant(new DateTime(2026,10,5,19,30,0),DemoHouse.Zone);
    static DateTimeOffset DemoDay(int day)=>DemoHouse.Midnight(new DateOnly(2026,10,day));
    static string[] LocalTimes(IEnumerable<MeasuredHistorySlot> slots,Func<MeasuredHistorySlot,bool> which)=>slots.Where(which).Select(s=>TimeZoneInfo.ConvertTime(s.Time,DemoHouse.Zone).ToString("HH:mm")).ToArray();

    [Fact]
    public void DemoMetersVaryByDayAndCarryShortAndLongOutages()
    {
        using var db=new DataStore(path);
        DemoTelemetry.Seed(db,DemoNow);
        var y=db.ReadMeasuredHistory(DemoDay(4),DemoDay(5),30).Slots;var d=db.ReadMeasuredHistory(DemoDay(3),DemoDay(4),30).Slots;var o=db.ReadMeasuredHistory(DemoDay(2),DemoDay(3),30).Slots;
        Assert.Equal(48,y.Count);
        // Every night a 20-minute 03:00 dropout is bridged by the counters as an estimated (≈) half-hour. Behaviour change: the long
        // outage is a single 40-minute one (15:10–15:50) three days before seeding, not 10:00–12:30 every day.
        Assert.Empty(LocalTimes(y,s=>s.LoadStatus=="missing"));Assert.Empty(LocalTimes(y,s=>s.PvStatus=="missing"));
        Assert.Contains("03:00",LocalTimes(y,s=>s.LoadStatus=="estimated"));Assert.NotNull(y.Single(s=>LocalTimes([s],_=>true)[0]=="03:00").LoadEstimate);
        Assert.Equal(["15:00","15:30"],LocalTimes(o,s=>s.LoadStatus is "missing" or "estimated").Where(t=>string.CompareOrdinal(t,"04:00")>0 && string.CompareOrdinal(t,"23:00")<0));
        Assert.True(DemoTelemetry.InOutage(DemoDay(2).AddHours(15).AddMinutes(30),DemoNow));Assert.False(DemoTelemetry.InOutage(DemoDay(4).AddHours(15).AddMinutes(30),DemoNow));
        Assert.True(DemoTelemetry.InOutage(DemoDay(4).AddHours(3).AddMinutes(10),DemoNow));
        // Overnight solar reads unknown and its counter proves zero; daytime solar peaks after midday.
        Assert.Equal(0,y[2].Pv);Assert.Equal(0,y[6].Pv);
        Assert.True(y[26].Pv>.8,$"13:00 solar {y[26].Pv}");
        // The demo whole-house meter includes the car, so the household part is load minus EV.
        Assert.All(y.Where(s=>s.Home is not null),s=>Assert.InRange(s.Home!.Value,.1,1.4));
        Assert.Contains(y,s=>s.Ev>2);Assert.All(y.Where(s=>s.Load is not null && s.Ev is not null),s=>Assert.True(s.Load>=s.Ev));
        // Consecutive days differ, so a "yesterday" overlay has something to show.
        var evening=y.Where(s=>s.Time>=DemoDay(4).AddHours(17)&&s.Time<DemoDay(4).AddHours(19)).Sum(s=>s.Home??0);var earlier=d.Where(s=>s.Time>=DemoDay(3).AddHours(17)&&s.Time<DemoDay(3).AddHours(19)).Sum(s=>s.Home??0);
        Assert.True(Math.Abs(evening-earlier)>.2,$"{evening} vs {earlier}");
    }

    [Fact]
    public void DemoPlanActualsDoNotHideMeasuredGaps()
    {
        using var db=new DataStore(path);
        DemoTelemetry.Seed(db);
        var outage=DemoHouse.Midnight(DemoHouse.LocalDate(DateTimeOffset.UtcNow).AddDays(-3)).AddHours(15).AddMinutes(30);
        var night=DemoHouse.Midnight(DemoHouse.LocalDate(DateTimeOffset.UtcNow).AddDays(-1)).AddHours(2);
        PlanSlot Slot(DateTimeOffset time)=>new(time,.4,.45,.2,.25,50,51,20,10,"Demand",.1);
        var plan=new PlanSnapshot{Source="Demo",At=night,CollectedAt=night,Slots=[Slot(night),Slot(outage)]};
        db.SavePlan(plan);
        var slots=db.GetPlan(plan.Id)!.Slots;
        Assert.Equal("measured",slots[0].LoadActualMethod);Assert.NotEqual(.45,slots[0].LoadActual);Assert.NotNull(slots[0].SocActual);
        // The demo battery sensor stays up through the long outage, so the slot keeps its battery level.
        Assert.NotEqual("measured",slots[1].LoadActualMethod);Assert.NotEqual(.45,slots[1].LoadActual);Assert.NotNull(slots[1].SocActual);
        // Before the demo meters are seeded, the scripted actuals still fill the chart.
        using var empty=new DataStore(path+"-empty");
        var seedless=new PlanSnapshot{Source="Demo",At=night,CollectedAt=night,Slots=[Slot(outage)]};empty.SavePlan(seedless);
        Assert.Equal(.45,empty.GetPlan(seedless.Id)!.Slots[0].LoadActual);
    }

    public void Dispose()
    {
        foreach(var directory in new[]{path,path+"-empty"})if(Directory.Exists(directory))Directory.Delete(directory,true);
    }
}
