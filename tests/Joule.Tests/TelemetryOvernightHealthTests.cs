using System.Globalization;
using Joule;
using Xunit;

namespace Joule.Tests;

/// <summary>
/// The collector's fault rule (<see cref="TelemetryCollectionService.Problems"/>) replayed poll by poll over real overnight shapes:
/// a normal night must never raise a collection error, while a solar meter that stays dead after sunrise must.
/// Clock-free: every judgement here is made from the stored samples and plans, not the wall clock.
/// </summary>
public sealed class TelemetryOvernightHealthTests : IDisposable
{
    readonly string path = Path.Combine(Path.GetTempPath(), "predbat-overnight-health-" + Guid.NewGuid().ToString("N"));
    static readonly TimeZoneInfo London = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");
    static readonly DateTimeOffset Midnight = DateTimeOffset.Parse("2026-10-04T23:00:00Z"); // 5 Oct 00:00 BST

    static string Reset(DateTimeOffset m) => $"{{\"state_class\":\"total\",\"last_reset\":\"{TimeZoneInfo.ConvertTime(m, London):yyyy-MM-dd'T'HH:mm:sszzz}\"}}";
    static TelemetrySample Counter(string metric, DateTimeOffset poll, double? value, DateTimeOffset updated, DateTimeOffset lastReset) => value is null
        ? new(metric, "sensor." + metric, poll, null, "", "HomeAssistant", "unknown", "kWh", updated, Reset(lastReset), "idle")
        : new(metric, "sensor." + metric, poll, value, "kWh", "HomeAssistant", value.Value.ToString(CultureInfo.InvariantCulture), "kWh", updated, Reset(lastReset));

    /// <summary>Predbat's PV forecast from the live 23:30 BST plan of 5 Oct: nothing until 07:00 BST, then 0.04, 0.04, 0.18, 0.18, 0.38 … kWh per half hour.</summary>
    static PlanSnapshot LiveForecast()
    {
        double[] morning = [.04, .04, .18, .18, .38, .38, .64, .64, .79, .79];
        var slots = Enumerable.Range(0, 32).Select(i => new PlanSlot(Midnight.AddMinutes(30 * i), .3, null, i < 14 ? 0 : morning[Math.Min(i - 14, morning.Length - 1)], null, 50, null, 25, 15, "Demand", 0)).ToList();
        return new PlanSnapshot { Source = "Predbat", At = Midnight.AddMinutes(-30), CollectedAt = Midnight.AddMinutes(-27), Slots = slots };
    }

    /// <summary>Saves each poll the way the collector does and returns every issue the fault rule raised, with the poll time.</summary>
    static List<(DateTimeOffset Poll, TelemetryIssue Issue)> Replay(DataStore db, IEnumerable<TelemetrySample> samples)
    {
        var raised = new List<(DateTimeOffset, TelemetryIssue)>();
        // The Home Assistant client reports "unknown" as idle; the live snapshot predates that and says unavailable.
        static TelemetrySample AsCollected(TelemetrySample x) => x.Status is "unavailable" or "invalid" && x.Value is null && x.RawState == "unknown" ? x with { Status = "idle" } : x;
        foreach (var poll in samples.Select(AsCollected).GroupBy(x => x.Time).OrderBy(x => x.Key))
        {
            db.SaveTelemetry(poll, TimeSpan.FromMinutes(15), alignToSource: true);
            raised.AddRange(TelemetryCollectionService.Problems(db, poll).Select(x => (poll.Key, x)));
        }
        return raised;
    }

    [Fact]
    public void AHeartbeatPolledBetweenMidnightAndHomeAssistantsResetIsStillYesterdaysReading()
    {
        using var db = new DataStore(path);
        db.SavePlan(LiveForecast());
        // Live 2→3 Oct shape: the 00:00:49 BST poll still carried yesterday's value, last_reset and last_updated (16:46 for solar);
        // Home Assistant reset the counters at 00:01:50, after which they read unknown until something flowed.
        var samples = new List<TelemetrySample>();
        var heartbeat = Midnight.AddSeconds(49); var haReset = Midnight.AddSeconds(110);
        foreach (var poll in new[] { heartbeat.AddMinutes(-10), heartbeat.AddMinutes(-5), heartbeat })
        {
            samples.Add(Counter("pv", poll, 9.4, Midnight.AddHours(-6).AddMinutes(-14), Midnight.AddDays(-1)));
            samples.Add(Counter("grid_export", poll, 2.212, Midnight.AddMinutes(-29), Midnight.AddDays(-1)));
        }
        for (var poll = heartbeat.AddMinutes(5); poll <= Midnight.AddHours(5); poll = poll.AddMinutes(5))
        {
            samples.Add(Counter("pv", poll, null, haReset, Midnight));
            samples.Add(Counter("grid_export", poll, null, haReset, Midnight));
        }
        var raised = Replay(db, samples);
        Assert.True(raised.Count == 0, string.Join("; ", raised.Select(x => $"{x.Poll:HH:mm} {x.Issue.Metric}: {x.Issue.Message}")));
        var latest = db.ReadLatestTelemetry();
        Assert.True(latest["pv"].Expected); Assert.Equal("Solar meter asleep (normal overnight); counted as 0.", latest["pv"].Reason);
        Assert.True(latest["grid_export"].Expected); Assert.Equal("No export yet today; counted as 0.", latest["grid_export"].Reason);
    }

    [Fact]
    public void SolarIsNotAFaultAtDawnBeforeThePanelsFirstReport()
    {
        using var db = new DataStore(path);
        db.SavePlan(LiveForecast());
        // Live 5 Oct: unknown from 00:01 BST until the first reading of 0.001 kWh at 07:41 BST (06:41 UTC), with Predbat forecasting
        // 0.04 kWh for the 07:00 and 07:30 BST half hours.
        var samples = new List<TelemetrySample> { Counter("pv", Midnight.AddMinutes(-3), 9.398, Midnight.AddHours(-4), Midnight.AddDays(-1)) };
        for (var poll = Midnight.AddMinutes(2); poll < Midnight.AddHours(7).AddMinutes(43); poll = poll.AddMinutes(5)) samples.Add(Counter("pv", poll, null, Midnight.AddMinutes(1), Midnight));
        samples.Add(Counter("pv", Midnight.AddHours(7).AddMinutes(43), .001, Midnight.AddHours(7).AddMinutes(41), Midnight));
        var raised = Replay(db, samples);
        Assert.True(raised.Count == 0, string.Join("; ", raised.Select(x => $"{x.Poll:HH:mm} {x.Issue.Metric}: {x.Issue.Message}")));
        // And the derivation counts those minutes as a known zero.
        var pv = db.ReadEnergySummary(Midnight, Midnight.AddHours(7).AddMinutes(43)).Metrics["pv"];
        Assert.Equal(.001, pv.EnergyKwh!.Value, 9); Assert.Equal(1, pv.CoverageFraction, 6);
    }

    [Fact]
    public void SolarStillDeadWellAfterSunriseIsRaisedOnceTheForecastAddsUp()
    {
        using var db = new DataStore(path);
        db.SavePlan(LiveForecast());
        var samples = new List<TelemetrySample> { Counter("pv", Midnight.AddMinutes(-3), 9.398, Midnight.AddHours(-4), Midnight.AddDays(-1)) };
        for (var poll = Midnight.AddMinutes(2); poll <= Midnight.AddHours(9); poll = poll.AddMinutes(5)) samples.Add(Counter("pv", poll, null, Midnight.AddMinutes(1), Midnight));
        var raised = Replay(db, samples);
        // Forecast from 06:00 UTC: 0.04 + 0.04 by 07:00, then 0.18 per half hour, so 0.2 kWh is reached at about 07:20 UTC.
        var first = Assert.Single(raised.Take(1));
        Assert.InRange(first.Poll, Midnight.AddHours(8).AddMinutes(15), Midnight.AddHours(8).AddMinutes(25));
        Assert.Equal("pv", first.Issue.Metric); Assert.StartsWith("unknown for", first.Issue.Message);
        // The outage is counted from when generation was first expected, not from last evening.
        Assert.Equal(Midnight.AddHours(7), first.Issue.Since);
        Assert.All(raised, x => Assert.Equal("pv", x.Issue.Metric));
        Assert.Equal(raised.Count, samples.Count(x => x.Time >= first.Poll));
        var latest = db.ReadLatestTelemetry()["pv"];
        Assert.False(latest.Expected); Assert.StartsWith("Solar meter still reports unknown, though Predbat forecast", latest.Reason);
    }

    [Fact]
    public void TheLive5OctoberNightRaisesNoCollectionError()
    {
        using var db = new DataStore(path);
        db.SavePlan(LiveForecast());
        var raised = Replay(db, TelemetryLiveReconciliationTests.Fixture());
        Assert.True(raised.Count == 0, string.Join("; ", raised.Select(x => $"{x.Poll:HH:mm} {x.Issue.Metric}: {x.Issue.Message}").Distinct().Take(20)));
    }

    public void Dispose() { if (Directory.Exists(path)) Directory.Delete(path, true); }
}
