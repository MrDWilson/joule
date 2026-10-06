using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Joule;
using Xunit;

namespace Joule.Tests;

/// <summary>Sensor profiles (pinned detection rules and overrides), the latest-reading status and when a collection is a fault.</summary>
public sealed class TelemetryProfileTests : IDisposable
{
    readonly string path = Path.Combine(Path.GetTempPath(), "predbat-profiles-" + Guid.NewGuid().ToString("N"));
    static readonly TimeZoneInfo London = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");
    static readonly DateTimeOffset Midnight = DateTimeOffset.Parse("2026-10-04T23:00:00Z");
    static TelemetrySample S(string metric, DateTimeOffset at, double? value, string attributes = "{}", string? status = null) =>
        new(metric, "sensor." + metric, at, value, value is null ? "" : "kWh", "HomeAssistant", value?.ToString(CultureInfo.InvariantCulture) ?? "unknown", "kWh", at, attributes, status ?? (value is null ? "idle" : "observed"));

    [Fact]
    public void ALastResetAtLocalMidnightMakesADailyCounterAndSolarDaily()
    {
        var attrs = "{\"state_class\":\"total\",\"last_reset\":\"2026-10-05T00:00:00+01:00\"}";
        Assert.Equal(SensorProfiles.DailyCounter, SensorProfiles.Detect("grid_export", [S("grid_export", Midnight.AddHours(1), null, attrs)], London));
        Assert.Equal(SensorProfiles.SolarDaily, SensorProfiles.Detect("pv", [S("pv", Midnight.AddHours(9), 1, attrs)], London));
        // A last_reset that is not at midnight says nothing about a daily reset.
        Assert.Equal(SensorProfiles.LifetimeCounter, SensorProfiles.Detect("load", [S("load", Midnight.AddHours(9), 1, "{\"last_reset\":\"2026-10-05T09:13:00+01:00\"}")], London));
    }

    [Fact]
    public void DropsAtMidnightOnTwoDaysMakeADailyCounterWithoutAnyAttribute()
    {
        List<TelemetrySample> Days(int count) => Enumerable.Range(0, count).SelectMany(d => new[] { S("load", Midnight.AddDays(d).AddMinutes(-4), 30 + d), S("load", Midnight.AddDays(d).AddMinutes(1), .05), S("load", Midnight.AddDays(d).AddHours(6), 6) }).ToList();
        Assert.Equal(SensorProfiles.LifetimeCounter, SensorProfiles.Detect("load", Days(1), London));
        Assert.Equal(SensorProfiles.DailyCounter, SensorProfiles.Detect("load", Days(2), London));
    }

    [Fact]
    public void TwoRestartsAwayFromMidnightMakeASessionCounterButGlitchesDoNot()
    {
        var t = Midnight.AddHours(14);
        var sessions = new List<TelemetrySample> { S("ev", t, 4), S("ev", t.AddMinutes(30), null), S("ev", t.AddHours(2), .4), S("ev", t.AddHours(3), 6), S("ev", t.AddHours(3.5), null), S("ev", t.AddHours(5), .3) };
        Assert.Equal(SensorProfiles.SessionCounter, SensorProfiles.Detect("load", sessions, London));
        // Small dips on a busy meter are glitches, not sessions.
        var glitches = new List<TelemetrySample> { S("load", t, 100), S("load", t.AddMinutes(5), 99.9), S("load", t.AddMinutes(10), 100.2), S("load", t.AddMinutes(15), 100.1), S("load", t.AddMinutes(20), 100.4) };
        Assert.Equal(SensorProfiles.LifetimeCounter, SensorProfiles.Detect("load", glitches, London));
        // Solar never runs in sessions.
        Assert.Equal(SensorProfiles.SolarDaily, SensorProfiles.Detect("pv", sessions.Select(x => x with { Metric = "pv" }).ToList(), London));
    }

    [Fact]
    public void DefaultsAndFixedProfiles()
    {
        Assert.Equal(SensorProfiles.SessionCounter, SensorProfiles.Detect("ev", [], London));
        Assert.Equal(SensorProfiles.SolarDaily, SensorProfiles.Detect("pv", [], London));
        Assert.Equal(SensorProfiles.LifetimeCounter, SensorProfiles.Detect("grid_import", [], London));
        Assert.Equal(SensorProfiles.Price, SensorProfiles.Detect("import_tariff", [], London));
        Assert.Equal(SensorProfiles.State, SensorProfiles.Detect("soc", [], London));
    }

    [Fact]
    public void ConfiguredProfilesOverrideDetectionAndUnknownProfilesAreRejected()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["HomeAssistant:Entities:Ev"] = "sensor.charger_total", ["HomeAssistant:Profiles:Ev"] = "Lifetime_Counter", ["HomeAssistant:LoadIncludesEv"] = "false" }).Build();
        var options = new HomeAssistantOptions(config);
        Assert.Equal(SensorProfiles.LifetimeCounter, options.Profiles["ev"]); Assert.Equal("false", options.LoadIncludesEv);
        using var db = new DataStore(path);
        db.ConfigureTelemetry(options.TelemetrySettings);
        Assert.Equal(SensorProfiles.LifetimeCounter, db.ProfileFor("ev")); Assert.False(db.LoadIncludesEv());
        Assert.Throws<DomainException>(() => new HomeAssistantOptions(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["HomeAssistant:Profiles:Load"] = "hourly" }).Build()));
        Assert.Throws<DomainException>(() => new HomeAssistantOptions(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["HomeAssistant:LoadIncludesEv"] = "maybe" }).Build()));
        // The default gap tolerance is max(15 minutes, 2.5 × the poll interval).
        Assert.Equal(15, new HomeAssistantOptions(new ConfigurationBuilder().Build()).MaxGap.TotalMinutes);
        Assert.Equal(25, new HomeAssistantOptions(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["HomeAssistant:PollMinutes"] = "10" }).Build()).MaxGap.TotalMinutes);
    }

    [Fact]
    public void CoverageUpToNowUsesTheConfiguredGapTolerance()
    {
        var now = DateTimeOffset.UtcNow;
        // A 10-minute poll: the latest reading 40 minutes ago is still within two gap tolerances (2 × 25 min), so the collector is alive.
        TelemetrySample[] readings = [S("grid_import", now.AddMinutes(-50), 100), S("grid_import", now.AddMinutes(-40), 100.2)];
        using var slow = new DataStore(path);
        slow.ConfigureTelemetry(new HomeAssistantOptions(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["HomeAssistant:PollMinutes"] = "10" }).Build()).TelemetrySettings);
        slow.SaveTelemetry(readings, TimeSpan.FromMinutes(25));
        Assert.Equal(1, slow.ReadEnergySummary(now.AddMinutes(-50), now).Metrics["grid_import"].CoverageFraction, 6);
        // With the default 15 minutes, the collector looks stalled after 30 minutes and the last 10 count as missing.
        using var fast = new DataStore(path + "-fast");
        fast.SaveTelemetry(readings);
        Assert.InRange(fast.ReadEnergySummary(now.AddMinutes(-50), now).Metrics["grid_import"].CoverageFraction, .4, .6);
    }

    [Fact]
    public void LatestReadingsKeepOldValuesAndExplainNonObservedStates()
    {
        using var db = new DataStore(path);
        var now = DateTimeOffset.UtcNow;
        db.SaveTelemetry([S("load", now.AddMinutes(-50), 12.3), S("soc", now.AddMinutes(-40), 74) with { Unit = "%" },
            S("ev", now.AddHours(-3), 4.6), S("ev", now.AddMinutes(-1), null, "{\"state_class\":\"total_increasing\"}"),
            S("grid_import", now.AddMinutes(-20), 5), S("grid_import", now.AddMinutes(-1), null, "{}", "unavailable") with { RawState = "unavailable" }]);
        var latest = db.ReadLatestTelemetry(TimeSpan.FromMinutes(15));
        // Live: a reading from 11 minutes ago turned into "—". It is now kept, with its age.
        Assert.Equal(12.3, latest["load"].Value); Assert.True(latest["load"].Stale); Assert.InRange(latest["load"].AgeSeconds, 2900, 3100);
        Assert.Equal(74, latest["soc"].Value);
        Assert.Equal("idle", latest["ev"].Status); Assert.True(latest["ev"].Expected); Assert.Equal("Not charging (the charger reports unknown between sessions).", latest["ev"].Reason);
        Assert.Equal(4.6, latest["ev"].LastObservedValue); Assert.Equal(SensorProfiles.SessionCounter, latest["ev"].Profile);
        Assert.False(latest["grid_import"].Expected); Assert.StartsWith("Device offline since", latest["grid_import"].Reason); Assert.Equal(5, latest["grid_import"].LastObservedValue);
        Assert.Null(latest["load"].Reason);
    }

    [Fact]
    public void SolarAsleepOvernightIsExpected()
    {
        using var db = new DataStore(path);
        var now = DateTimeOffset.UtcNow;
        var midnight = CivilTime.FirstValidInstant(TimeZoneInfo.ConvertTime(now, London).Date, London);
        string Reset(DateTimeOffset m) => $"{{\"last_reset\":\"{TimeZoneInfo.ConvertTime(m, London):yyyy-MM-dd'T'HH:mm:sszzz}\"}}";
        db.SaveTelemetry([S("pv", midnight.AddMinutes(-10), 9.4, Reset(midnight.AddDays(-1))), S("pv", now.AddMinutes(-1), null, Reset(midnight))]);
        var pv = db.ReadLatestTelemetry()["pv"];
        Assert.True(pv.Expected); Assert.Equal("Solar meter asleep (normal overnight); counted as 0.", pv.Reason);
    }

    [Fact]
    public async Task AnEntityMissingFromHomeAssistantIsNotFoundNotOffline()
    {
        var options = new HomeAssistantOptions(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["HomeAssistant:BaseUrl"] = "http://ha.test", ["HomeAssistant:AccessToken"] = "x", ["HomeAssistant:Entities:Pv"] = "sensor.renamed_solar" }).Build());
        var client = new HomeAssistantClient(new HttpClient(new Handler(_ => new(HttpStatusCode.OK) { Content = new StringContent("[]") })), options);
        var sample = Assert.Single(await client.CollectAsync(default));
        Assert.Equal("not_found", sample.Status); Assert.Equal("entity not found in Home Assistant", sample.RawState); Assert.False(client.LastReadFailed);
    }

    static (TelemetryCollectionService Service, HomeAssistantClient Client) Collector(DataStore db, Func<JsonArray?> states, params (string Name, string Entity)[] entities)
    {
        var values = new Dictionary<string, string?> { ["App:Demo"] = "false", ["HomeAssistant:BaseUrl"] = "http://ha.test", ["HomeAssistant:AccessToken"] = "x" };
        foreach (var (name, entity) in entities) values["HomeAssistant:Entities:" + name] = entity;
        var config = new ConfigurationBuilder().AddInMemoryCollection(values).Build(); var options = new HomeAssistantOptions(config);
        var client = new HomeAssistantClient(new HttpClient(new Handler(_ => states() is { } s ? new(HttpStatusCode.OK) { Content = new StringContent(s.ToJsonString()) } : new(HttpStatusCode.BadGateway))), options, clock: db.Clock);
        return (new TelemetryCollectionService(db, client, options, config), client);
    }
    static JsonObject State(string entity, string state, DateTimeOffset updated, string? lastReset = null)
    {
        var attributes = new JsonObject { ["unit_of_measurement"] = "kWh", ["state_class"] = "total" };
        if (lastReset is not null) attributes["last_reset"] = lastReset;
        return new() { ["entity_id"] = entity, ["state"] = state, ["last_updated"] = updated.ToString("O"), ["attributes"] = attributes };
    }

    // A pinned clock in whole seconds (Home Assistant's timestamps are microseconds; the wall clock on Linux has 100 ns ticks that
    // the store cannot round-trip). Late morning and late evening, both after the counter's reset two hours earlier.
    [Theory]
    [InlineData("2026-10-06T11:10:00Z")]
    [InlineData("2026-10-06T20:52:54Z")]
    public async Task DaytimeSolarUnknownBeyondTheGracePeriodIsAWarningButOvernightIsNot(string at)
    {
        var now = DateTimeOffset.Parse(at, CultureInfo.InvariantCulture);
        var clock = new ManualClock(now);
        using var db = new DataStore(path, clock);
        var midnight = CivilTime.FirstValidInstant(TimeZoneInfo.ConvertTime(now, London).Date, London);
        // The counter's current day began two hours ago.
        var reset = now.AddHours(-2).ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture);
        // Solar generated today and Predbat expects more now, but the sensor has said unknown for 40 minutes.
        db.SavePlan(new PlanSnapshot { Source = "Predbat", At = now.AddHours(-2), CollectedAt = now.AddHours(-2), Slots = Enumerable.Range(-4, 8).Select(i => new PlanSlot(new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, now.Minute < 30 ? 0 : 30, 0, TimeSpan.Zero).AddMinutes(30 * i), .3, null, 1.2, null, 50, null, 25, 15, "Demand", 0)).ToList() });
        db.SaveTelemetry([new("pv", "sensor.pv", now.AddMinutes(-45), 3, "kWh", "HomeAssistant", "3", "kWh", now.AddMinutes(-45), $"{{\"last_reset\":\"{reset}\"}}"), new("pv", "sensor.pv", now.AddMinutes(-40), null, "", "HomeAssistant", "unknown", "", now.AddMinutes(-40), $"{{\"last_reset\":\"{reset}\"}}", "idle")]);
        var (service, _) = Collector(db, () => new JsonArray(State("sensor.pv", "unknown", now.AddMinutes(-40), reset)), ("Pv", "sensor.pv"));
        var error = await Assert.ThrowsAsync<DomainException>(() => service.CollectAsync(default));
        Assert.Contains("pv (unknown for", error.Message);
        Assert.Equal("pv", Assert.Single(service.Status().Issues).Metric);

        // Overnight (no solar yet today and none forecast), the same unknown is normal and never an error.
        var todayReset = TimeZoneInfo.ConvertTime(midnight, London).ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture);
        using var night = new DataStore(path + "-night", clock);
        night.SaveTelemetry([new("pv", "sensor.pv", midnight.AddMinutes(-10), 9.4, "kWh", "HomeAssistant", "9.4", "kWh", midnight.AddMinutes(-10), "{\"last_reset\":\"2020-01-01T00:00:00+00:00\"}"),
            new("pv", "sensor.pv", now.AddHours(-2), null, "", "HomeAssistant", "unknown", "", midnight, $"{{\"last_reset\":\"{todayReset}\"}}", "idle")]);
        var (quiet, _) = Collector(night, () => new JsonArray(State("sensor.pv", "unknown", midnight, todayReset)), ("Pv", "sensor.pv"));
        await quiet.CollectAsync(default);
        Assert.Null(quiet.Status().Error); Assert.Empty(quiet.Status().Issues);
    }

    [Fact]
    public async Task AMissingMappingIsAnErrorAtOnceAndAFailedReadIsAlwaysAnError()
    {
        using var db = new DataStore(path);
        var (service, _) = Collector(db, () => new JsonArray(), ("Load", "sensor.renamed"));
        var error = await Assert.ThrowsAsync<DomainException>(() => service.CollectAsync(default));
        Assert.Contains("load (entity not found in Home Assistant: check the mapping)", error.Message);
        using var other = new DataStore(path + "-down");
        var (down, client) = Collector(other, () => null, ("Load", "sensor.load"));
        await Assert.ThrowsAsync<DomainException>(() => down.CollectAsync(default));
        Assert.True(client.LastReadFailed); Assert.Equal("all", Assert.Single(down.Status().Issues).Metric);
    }

    public void Dispose()
    {
        foreach (var dir in new[] { path, path + "-night", path + "-down", path + "-fast" }) if (Directory.Exists(dir)) Directory.Delete(dir, true);
    }
    sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> reply) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => Task.FromResult(reply(request)); }
}
