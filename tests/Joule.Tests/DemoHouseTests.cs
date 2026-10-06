using Joule;
using Xunit;

namespace Joule.Tests;

/// <summary>The demo plan and the demo meters describe one household: battery, charging, export, import and prices agree.</summary>
public sealed class DemoHouseTests : IDisposable
{
    readonly string path = Path.Combine(Path.GetTempPath(), "predbat-demo-house-" + Guid.NewGuid().ToString("N"));
    static readonly TimeZoneInfo London = DemoHouse.Zone;
    /// <summary>Local clock times across a year (both clock changes included) at which "now" is checked.</summary>
    public static IEnumerable<object[]> Moments() =>
        from date in new[] { "2026-10-05", "2026-10-06", "2026-03-29", "2026-10-25", "2026-06-21", "2026-12-21" }
        from time in new[] { "00:10", "03:40", "07:05", "12:20", "16:45", "19:30", "23:50" }
        select new object[] { date, time };
    static DateTimeOffset Local(string date, string time) => CivilTime.FirstValidInstant(DateTime.Parse($"{date}T{time}:00"), London);

    [Theory, MemberData(nameof(Moments))]
    public void MeasuredBatteryStaysWithinTenPointsOfThePlanInEveryElapsedSlot(string date, string time)
    {
        var now = Local(date, time);
        foreach (var daysAgo in new[] { 0, 1, 2 })
        {
            var plan = DemoData.Plan(daysAgo, now);
            foreach (var slot in plan.Slots.Where(s => s.SocActual is not null))
                Assert.True(Math.Abs(slot.SocActual!.Value - slot.SocForecastEnd!.Value) <= 10, $"{daysAgo} days ago {TimeZoneInfo.ConvertTime(slot.Time, London):ddd HH:mm}: measured {slot.SocActual} vs planned {slot.SocForecastEnd}");
        }
    }

    [Theory, MemberData(nameof(Moments))]
    public void TodaysPlanRestartsFromTheMeasuredLevelAndCoversTheNext36Hours(string date, string time)
    {
        var now = Local(date, time);
        var plan = DemoData.Plan(0, now);
        Assert.True(plan.Slots[^1].Time.AddMinutes(30) - now >= TimeSpan.FromHours(36), $"plan ends {plan.Slots[^1].Time:O}");
        Assert.True(plan.Slots.Count >= 96);
        var current = plan.Slots.Single(s => s.Time <= now && s.Time.AddMinutes(30) > now);
        var measured = DemoTelemetry.Readings(current.Time)["soc"]!.Value;
        Assert.Equal(measured, current.SocForecast, 1);
        // An export never aims above the battery's level when it starts.
        foreach (var slot in plan.Slots.Where(s => s.Time >= current.Time && s.ActionKey == "export"))
            Assert.True(slot.TargetPercent <= slot.SocForecast, $"{slot.Time:HH:mm} exports to {slot.TargetPercent}% from {slot.SocForecast}%");
        // Only prices past the published cut-off are estimates.
        var local = TimeZoneInfo.ConvertTime(now, London);
        var cutoff = DemoHouse.Midnight(DateOnly.FromDateTime(local.Date).AddDays(local.Hour >= 16 ? 1 : 0)).AddHours(23.5);
        Assert.All(plan.Slots, s => Assert.Equal(s.Time >= cutoff, s.RateEstimated == true));
    }

    [Theory]
    [InlineData("2026-10-05")]
    [InlineData("2026-10-06")]
    [InlineData("2026-01-14")]
    [InlineData("2026-07-02")]
    public void OvernightChargeIsCheapAndMatchesTheBatteryRise(string date)
    {
        using var db = new DataStore(path + date);
        var night = Local(date, "05:30");
        DemoTelemetry.Seed(db, night.AddHours(1));
        var from = night.AddHours(-6);
        var summary = db.ReadEnergySummary(from, night);
        var import = summary.Metrics["grid_import"].EnergyKwh!.Value;
        Assert.True(import > 5, $"imported {import} kWh");
        Assert.Equal(7, summary.ImportCostGbp!.Value * 100 / import, .5);
        var start = DemoTelemetry.Readings(from)["soc"]!.Value; var end = DemoTelemetry.Readings(night)["soc"]!.Value;
        var charge = summary.Metrics["battery_charge"].EnergyKwh!.Value; var discharge = summary.Metrics["battery_discharge"].EnergyKwh!.Value;
        Assert.Equal((end - start) / 100 * DemoHouse.CapacityKwh, charge - discharge, .05);
        Assert.Equal((DemoHouse.ChargeEnd - start) / 100 * DemoHouse.CapacityKwh, charge, .3);
        Assert.True(end > start + 20, $"{start}% -> {end}%");
    }

    [Fact]
    public void EveryHalfHourBalancesAndSolarSuitsTheSeason()
    {
        for (var date = new DateOnly(2026, 1, 1); date < new DateOnly(2027, 1, 1); date = date.AddDays(1))
        {
            var day = DemoHouse.Measured(date);
            for (var i = 0; i < 48; i++)
            {
                var supply = day.Import[i] + day.Pv[i] + day.Discharge[i]; var use = day.Load[i] + day.Car[i] + day.Charge[i] + day.Export[i];
                Assert.True(Math.Abs(supply - use) < .002, $"{date} {i}: {supply} vs {use}");
                Assert.True(day.Import[i] == 0 || day.Export[i] == 0);
                Assert.InRange(day.Levels[i + 1], DemoHouse.ReservePercent, 100);
                Assert.True(day.Car[i] <= 3.5);
            }
            // The evening export always ends at 60%, so each day starts where the previous one ended.
            Assert.Equal(DemoHouse.ExportTarget, day.Levels[37], 2);
            Assert.Equal(DemoHouse.Measured(date.AddDays(-1)).Levels[48], day.Levels[0], 2);
            if (date.Month == 10 && date.Day <= 15) Assert.InRange(day.Pv.Sum(), 8, 14.5);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void YesterdayAndLastWeekDifferFromToday(int unused)
    {
        _ = unused;
        var today = new DateOnly(2026, 10, 5);
        double Evening(DateOnly d) => Enumerable.Range(34, 6).Select(i => DemoHouse.LoadKwh(d, i)).ToArray() is var x ? x.Select((v, i) => v * i).Sum() / x.Sum() : 0;
        Assert.NotEqual(DemoHouse.OvenSlot(today), DemoHouse.OvenSlot(today.AddDays(-1)));
        Assert.NotEqual(DemoHouse.OvenSlot(today), DemoHouse.OvenSlot(today.AddDays(-7)));
        Assert.True(Math.Abs(Evening(today) - Evening(today.AddDays(-1))) > .3);
    }

    public void Dispose()
    {
        foreach (var dir in Directory.GetDirectories(Path.GetTempPath(), Path.GetFileName(path) + "*")) Directory.Delete(dir, true);
    }
}
