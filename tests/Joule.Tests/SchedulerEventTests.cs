using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Joule;
using Xunit;
using Xunit.Abstractions;

namespace Joule.Tests;

/// <summary>
/// Fake-clock tests of event-led automatic checks: window ends, Predbat warnings, the battery off plan, sensor outages, the 07:30 and
/// 21:30 digests across the October clock change, the 3-hour heartbeat, the interval as minimum spacing, the free quiet check, and a
/// replay of the 4 Oct schedule counting AI calls and prompt size.
/// </summary>
public sealed class SchedulerEventTests(ITestOutputHelper output) : IDisposable
{
    readonly string directory = Path.Combine(Path.GetTempPath(), "predbat-scheduler-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(directory, true); } catch { } }
    static readonly TimeZoneInfo London = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");
    static AppState Scheduled(int interval = 60) => new() { Ai = new AiPreferences { Scheduled = true, Provider = "ChatGpt", Model = "fixture", IntervalMinutes = interval, MaxRunsPerDay = 48 } };
    static InvestigationScheduleStatus Status(AppState s, DateTimeOffset now, IReadOnlyList<ScheduleSignal>? signals = null) =>
        InvestigationScheduler.BuildStatus(s, now, false, false, false, true, null, London, signals);
    static PlanVsActualSlot Slot(DateTimeOffset t, string key, double? planned = 50, double? actual = 50) =>
        new(t, 30, key, planned, actual, actual, 6.67, 12.28, null, null, null, null, null, null, null, null, null, t.AddMinutes(-5), 1, 1) { ActionKey = key };
    static List<PlanVsActualSlot> Frozen(DateTimeOffset from, params string[] keys) => keys.Select((k, i) => Slot(from.AddMinutes(30 * i), k)).ToList();
    static void Ran(AppState s, DateTimeOffset at) { s.LastAnalysisAttemptAt = at; s.LastAnalysis = at.AddMinutes(4); s.Usage.Add(new(at.AddMinutes(4), "ChatGpt", "fixture", 20000, 500, null, "Completed")); }

    [Fact]
    public void ACheapWindowEndStartsACheckFiveMinutesLater()
    {
        var day = new DateTimeOffset(2026, 10, 4, 0, 0, 0, TimeSpan.Zero);
        var recent = Frozen(day, "charge", "charge", "charge", "charge", "demand", "demand"); // charge 00:00–02:00 UTC
        var s = Scheduled(); Ran(s, day.AddMinutes(-30));
        var at = day.AddHours(2).AddMinutes(4);
        var signals = InvestigationScheduler.ComputeSignals(recent, null, null, at, London);
        var end = Assert.Single(signals, x => x.Kind == "window_end");
        Assert.Equal(day.AddHours(2).AddMinutes(5), end.DueAt); Assert.Contains("03:00 end of a charge window", end.Reason);
        var waiting = Status(s, at, signals);
        Assert.Equal("Waiting", waiting.State); Assert.Equal(day.AddHours(2).AddMinutes(5), waiting.NextRunAt);
        var due = Status(s, at.AddMinutes(1), InvestigationScheduler.ComputeSignals(recent, null, null, at.AddMinutes(1), London));
        Assert.Equal("Due", due.State); Assert.Equal("window_end", due.TriggerKind);
        // Once a check has started after the window end, the same event doesn't start another.
        Ran(s, at.AddMinutes(1));
        Assert.Equal("Waiting", Status(s, at.AddMinutes(30), InvestigationScheduler.ComputeSignals(recent, null, null, at.AddMinutes(30), London)).State);
        // An export window that ends inside the current plan is known in advance.
        var plan = new PlanSnapshot { Slots = [new(at.AddMinutes(60), 0, null, 0, null, 50, null, 6, 15, "export", 0) { ActionKey = "export" }, new(at.AddMinutes(90), 0, null, 0, null, 50, null, 6, 15, "demand", 0) { ActionKey = "demand" }] };
        Assert.Contains(InvestigationScheduler.ComputeSignals(recent, plan, null, at, London), x => x.Kind == "window_end" && x.DueAt == at.AddMinutes(95) && x.Reason.Contains("end of an export window"));
    }

    [Fact]
    public void TheIntervalIsTheMinimumSpacingBetweenAiChecks()
    {
        var day = new DateTimeOffset(2026, 10, 4, 0, 0, 0, TimeSpan.Zero);
        var recent = Frozen(day, "charge", "charge", "demand");
        var s = Scheduled(60); Ran(s, day.AddMinutes(40));
        var at = day.AddMinutes(70);
        var status = Status(s, at, InvestigationScheduler.ComputeSignals(recent, null, null, at, London));
        Assert.Equal("Waiting", status.State); Assert.Equal(day.AddMinutes(104), status.NextRunAt); Assert.Contains("minimum spacing of 60 minutes", status.Reason);
    }

    [Fact]
    public void TheBatteryOffPlanByMoreThanTenPointsAndASensorDownForHalfAnHourAreEvents()
    {
        var at = new DateTimeOffset(2026, 10, 4, 3, 10, 0, TimeSpan.Zero);
        var recent = new List<PlanVsActualSlot> { Slot(at.AddMinutes(-70), "demand", 60, 55), Slot(at.AddMinutes(-40), "demand", 55, 40) };
        var telemetry = new TelemetryStatus(false, true, "Europe/London", at, null, [], [], 15, []) { Issues = [new("pv", "Solar meter offline", at.AddMinutes(-31)), new("grid_export", "Export meter offline", at.AddMinutes(-10))] };
        var signals = InvestigationScheduler.ComputeSignals(recent, null, telemetry, at, London);
        var soc = Assert.Single(signals, x => x.Kind == "soc_miss"); Assert.Contains("15 points below plan at 03:30", soc.Reason);
        var sensor = Assert.Single(signals, x => x.Kind == "sensor_outage"); Assert.Contains("solar meter down since 03:39", sensor.Reason);
        var s = Scheduled(); Ran(s, at.AddHours(-2));
        var status = Status(s, at, signals);
        Assert.Equal("Due", status.State); Assert.Equal("soc_miss", status.TriggerKind);
    }

    [Fact]
    public void ABatteryThatStaysOffPlanIsOneEventNotOneEveryHalfHour()
    {
        var at = new DateTimeOffset(2026, 10, 4, 6, 10, 0, TimeSpan.Zero);
        // 02:40–05:10 UTC 15–20 points below plan, back on plan at 05:40, then 12 points above at 06:10 (07:10 BST).
        var recent = Enumerable.Range(0, 7).Select(i => Slot(at.AddMinutes(-210 + 30 * i), "demand", 60, 45 - i)).ToList();
        recent[^1] = Slot(at.AddMinutes(-30), "demand", 60, 58);
        recent.Add(Slot(at, "demand", 50, 62));
        var signals = InvestigationScheduler.ComputeSignals(recent, null, null, at, London).Where(x => x.Kind == "soc_miss").ToList();
        Assert.Equal(2, signals.Count);
        Assert.Contains("15 points below plan at 03:40", signals[0].Reason);
        Assert.Contains("12 points above plan at 07:10", signals[1].Reason);
        // The same continuing miss, seen a half-hour later, keeps its key, so a handled event isn't raised again.
        var later = InvestigationScheduler.ComputeSignals(recent.Take(6).ToList(), null, null, at.AddMinutes(-30), London).Single(x => x.Kind == "soc_miss");
        Assert.Equal(signals[0].Key, later.Key);
        // A miss already under way at the edge of the six-hour horizon began earlier and was flagged then.
        var edge = Enumerable.Range(0, 13).Select(i => Slot(at.AddHours(-6).AddMinutes(30 * i), "demand", 60, 40)).ToList();
        Assert.DoesNotContain(InvestigationScheduler.ComputeSignals(edge, null, null, at, London), x => x.Kind == "soc_miss");
    }

    [Fact]
    public void TheHeartbeatGuaranteesACheckEveryThreeHoursWhenNothingElseHappens()
    {
        var at = new DateTimeOffset(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);
        var s = Scheduled(); Ran(s, at.AddHours(-2));
        var waiting = Status(s, at);
        Assert.Equal("Waiting", waiting.State); Assert.Equal(at.AddHours(1).AddMinutes(4), waiting.NextRunAt); Assert.Contains("3-hour heartbeat", waiting.Reason);
        var due = Status(s, at.AddHours(1).AddMinutes(5));
        Assert.Equal("Due", due.State); Assert.Equal("heartbeat", due.TriggerKind);
        // A fresh install with no check yet is due straight away.
        Assert.Equal("Due", Status(Scheduled(), at).State);
    }

    [Fact]
    public void DigestsRunAtHalfPastSevenAndHalfPastNineHomeTimeAcrossTheClockChange()
    {
        // Saturday 24 Oct is BST (UTC+1); the clocks go back at 02:00 BST on Sunday 25 Oct.
        var saturday = InvestigationScheduler.DigestInstants(new DateTimeOffset(2026, 10, 24, 12, 0, 0, TimeSpan.Zero), London).ToList();
        Assert.Contains(new DateTimeOffset(2026, 10, 24, 6, 30, 0, TimeSpan.Zero), saturday);
        Assert.Contains(new DateTimeOffset(2026, 10, 24, 20, 30, 0, TimeSpan.Zero), saturday);
        var sunday = InvestigationScheduler.DigestInstants(new DateTimeOffset(2026, 10, 25, 12, 0, 0, TimeSpan.Zero), London).ToList();
        Assert.Contains(new DateTimeOffset(2026, 10, 25, 7, 30, 0, TimeSpan.Zero), sunday);
        Assert.Contains(new DateTimeOffset(2026, 10, 25, 21, 30, 0, TimeSpan.Zero), sunday);
        // On Sunday morning 06:30 UTC is 06:30 GMT, not the digest; 07:30 UTC is.
        var s = Scheduled(); Ran(s, new DateTimeOffset(2026, 10, 25, 6, 0, 0, TimeSpan.Zero));
        Assert.Equal("Waiting", Status(s, new DateTimeOffset(2026, 10, 25, 7, 10, 0, TimeSpan.Zero)).State);
        var digest = Status(s, new DateTimeOffset(2026, 10, 25, 7, 31, 0, TimeSpan.Zero));
        Assert.Equal("Due", digest.State); Assert.Equal("digest", digest.TriggerKind); Assert.Equal("the morning digest", digest.Trigger);
    }

    [Fact]
    public void TheDailyAllowanceIgnoresChecksThatFailedBeforeTheAiAnswered()
    {
        var at = new DateTimeOffset(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);
        var s = Scheduled(); s.Ai.MaxRunsPerDay = 2;
        s.Usage = [new(at.AddHours(-3), "ChatGpt", "m", 0, 0, null, "Failed"), new(at.AddHours(-2), "ChatGpt", "m", 0, 0, null, "Failed"), new(at.AddHours(-1), "ChatGpt", "m", 18000, 0, null, "Completed")];
        Assert.Equal(1, InvestigationScheduler.RunsToday(s.Usage, at, London));
        Assert.NotEqual("DailyLimit", Status(s, at).State);
        s.Usage.Add(new(at.AddMinutes(-5), "ChatGpt", "m", 18000, 200, null, "Failed"));
        Assert.Equal("DailyLimit", Status(s, at).State);
    }

    // ---- Quiet checks: the scheduler with fake Predbat MCP and no AI calls ----

    sealed class NoWriter : IPredbatClient
    {
        public bool Configured => true; public bool WritesEnabled => false;
        public Task<LiveSnapshot> CollectAsync(CancellationToken ct = default) => Task.FromResult(new LiveSnapshot([], null, "{}", "{}"));
        public Task ApplyAsync(List<Change> c, List<Setting> s, CancellationToken ct = default) => throw new InvalidOperationException();
    }
    sealed class Factory(HttpClient http) : IHttpClientFactory { public HttpClient CreateClient(string name) => http; }
    sealed class CountingProvider : HttpMessageHandler
    {
        public List<string> Prompts = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri!.Host == "raw.githubusercontent.com") return new(HttpStatusCode.ServiceUnavailable);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            Prompts.Add(body.RootElement.GetProperty("messages")[0].GetProperty("content").GetString()!);
            var finish = JsonSerializer.Serialize(new { action = "finish", verdict = "no_change", title = "No material change since the last review", summary = "Nothing new.", evidence = new[] { "All half-hours matched the plan." }, evidenceReferences = new[] { "configuration" } });
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { choices = new[] { new { finish_reason = "stop", message = new { content = finish } } }, usage = new { prompt_tokens = 1000, completion_tokens = 50 } }), Encoding.UTF8, "application/json") };
        }
    }
    sealed class LogMcp(Func<string> log) : IPredbatMcpClient
    {
        public int Reads;
        public bool Configured => true;
        public McpDiscovery Status => new(true, true, DateTimeOffset.UtcNow, [new("get_log", "Read the log", JsonDocument.Parse("{}").RootElement)], null);
        public Task<McpDiscovery> DiscoverAsync(CancellationToken ct = default) => Task.FromResult(Status);
        public Task<McpReadResult> CallReadOnlyAsync(string name, JsonElement arguments, CancellationToken ct = default)
        { Reads++; return Task.FromResult(new McpReadResult(true, JsonSerializer.Serialize(new { lines = log() }), false, null)); }
    }

    async Task<(StateService State, InvestigationScheduler Scheduler, CountingProvider Provider, AnalysisService Analysis)> Live(DataStore db, IPredbatMcpClient? mcp)
    {
        var provider = new CountingProvider(); var http = new HttpClient(provider);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Ai:ApiKey"] = "fixture" }).Build();
        var state = new StateService(db, new NoWriter(), false); // live: no demo plans, so only the events each test sets up
        var model = new AiModelClient(http, new ChatGptAuth(http, Path.Combine(directory, "auth")), config) { RetryDelays = [] };
        var analysis = new AnalysisService(state, db, model, new Factory(http), config, mcp: mcp);
        await state.MutateAsync(s =>
        {
            s.Investigations.Clear(); s.Usage.Clear(); s.Ai = new AiPreferences { Scheduled = true, Provider = "Api", Model = "fixture", IntervalMinutes = 60, MaxRunsPerDay = 24 };
            // These tests run on the real clock: the 07:30 and 21:30 digests are already handled, so a test run soon after one
            // isn't started by the digest instead of the event under test.
            foreach (var digest in InvestigationScheduler.DigestInstants(DateTimeOffset.UtcNow, analysis.Zone)) s.AiSchedule.Handled[$"digest:{digest:O}"] = DateTimeOffset.UtcNow;
        });
        return (state, new InvestigationScheduler(state, analysis, model, new ChatGptAuth(http, Path.Combine(directory, "auth2")), db, mcp), provider, analysis);
    }

    [Fact]
    public async Task StatePollsShareOneReadingOfTheEventsForAMinute()
    {
        // Status runs on every /api/state poll; the plan-versus-actual queries and the telemetry scan must not run each time.
        using var db = new DataStore(directory);
        var s = await Live(db, null);
        var now = DateTimeOffset.UtcNow;
        for (var i = 0; i < 6; i++) s.Scheduler.Status(now.AddSeconds(i * 10));
        _ = s.Scheduler.TryStart(now.AddSeconds(55), CancellationToken.None); // the worker shares the same reading
        for (var i = 0; i < 200 && s.Analysis.Running; i++) await Task.Delay(10);
        Assert.Equal(1, s.Scheduler.SignalReads);
        s.Scheduler.Status(now.AddSeconds(61));
        Assert.Equal(2, s.Scheduler.SignalReads);
        // A fake clock moving backwards (a test replay) reads again rather than reuse a reading from its future.
        s.Scheduler.Status(now.AddMinutes(-5));
        Assert.Equal(3, s.Scheduler.SignalReads);
    }

    [Fact]
    public async Task AQuietCheckRecordsVisibleActivityWithoutCallingTheAi()
    {
        using var db = new DataStore(directory);
        var mcp = new LogMcp(() => "2026-10-04 11:58:01 INFO: Plan calculated\n2026-10-04 11:59:00 INFO: Inverter 0 SoC 55%");
        var s = await Live(db, mcp);
        var now = DateTimeOffset.UtcNow;
        await s.State.MutateAsync(x => { x.LastAnalysisAttemptAt = now.AddMinutes(-70); x.LastAnalysis = now.AddMinutes(-66); x.Usage.Add(new(now.AddMinutes(-66), "Api", "fixture", 1000, 10, null, "Completed")); });
        Assert.Equal("checked", await s.Scheduler.TickAsync(now, CancellationToken.None));
        Assert.Empty(s.Provider.Prompts); Assert.Equal(1, mcp.Reads);
        var activity = s.State.Read(false).Activities.Last();
        Assert.Equal("check", activity.Kind); Assert.Contains("nothing new", activity.Message); Assert.Contains("No new Predbat warnings", activity.Message); Assert.Contains("The AI wasn't needed", activity.Message);
        var status = s.Scheduler.Status(now);
        Assert.Equal(now, status.LastQuietCheckAt); Assert.Equal(now.AddMinutes(60), status.NextCheckAt); Assert.Contains("nothing new", status.LastQuietCheck);
        // The next tick inside the interval does nothing at all.
        Assert.Equal("waiting", await s.Scheduler.TickAsync(now.AddMinutes(10), CancellationToken.None)); Assert.Equal(1, mcp.Reads);
    }

    [Fact]
    public async Task ANewPredbatWarningStartsACheckEvenWhenEverythingElseIsQuiet()
    {
        using var db = new DataStore(directory);
        var log = "2026-10-04 11:58:01 WARN: Inverter 0 failed to set charge rate, retrying (attempt 2)";
        var mcp = new LogMcp(() => log);
        var s = await Live(db, mcp);
        var now = DateTimeOffset.UtcNow;
        await s.State.MutateAsync(x => { x.LastAnalysisAttemptAt = now.AddMinutes(-70); x.LastAnalysis = now.AddMinutes(-66); x.Usage.Add(new(now.AddMinutes(-66), "Api", "fixture", 1000, 10, null, "Completed")); });
        Assert.Equal("started", await s.Scheduler.TickAsync(now, CancellationToken.None));
        for (var i = 0; i < 200 && s.Analysis.Running; i++) await Task.Delay(10);
        Assert.Single(s.Provider.Prompts);
        var check = s.State.Read(false).Investigations.Single();
        Assert.True(check.Request.Trigger?.StartsWith("new Predbat warning: 2026-10-04 11:58:01 WARN: Inverter 0 failed to set charge rate") == true, check.Request.Trigger + " | " + check.Request.Label);
        Assert.Contains("Automatic check of", check.Request.Label);
        Assert.Contains(s.State.Read(false).Activities, a => a.Kind == "check" && a.Message.Contains("new warning"));
        // The same warning with new numbers isn't new again.
        log = "2026-10-04 13:01:00 WARN: Inverter 0 failed to set charge rate, retrying (attempt 3)";
        await s.Scheduler.QuietCheckAsync(now.AddMinutes(61), CancellationToken.None);
        Assert.Contains("nothing new", s.State.Read(false).AiSchedule.LastQuietCheck);
    }

    // ---- Replay: 4 Oct ran 24 hourly checks (5.97M input tokens). The same day under event-led scheduling. ----

    [Fact]
    public async Task ReplayOfTheFourthOfOctoberUsesFarFewerAiCallsAndSmallerPrompts()
    {
        var day = new DateTimeOffset(2026, 10, 3, 23, 0, 0, TimeSpan.Zero); // 00:00 BST, 4 Oct
        // That night's shape: charge 00:00–03:30, export 03:30–04:00, charge 04:00–05:30 (BST), evening export 17:00–18:00.
        var keys = Enumerable.Range(0, 48).Select(i => i switch { < 7 => "charge", 7 => "export", < 11 => "charge", 34 or 35 => "export", _ => "demand" }).ToArray();
        var frozen = Frozen(day, keys);
        var s = Scheduled(60); s.Ai.MaxRunsPerDay = 24;
        Ran(s, day.AddMinutes(-20));
        var runs = new List<(DateTimeOffset At, string? Kind)>(); var quiet = 0;
        for (var at = day; at < day.AddDays(1); at = at.AddMinutes(1))
        {
            var visible = frozen.Where(x => x.Time < at).ToList();
            var status = Status(s, at, InvestigationScheduler.ComputeSignals(visible, null, null, at, London));
            if (status.State == "Due") { runs.Add((at, status.TriggerKind)); Ran(s, at); }
            else if (status.NextCheckAt is { } check && check <= at) { quiet++; s.AiSchedule.LastQuietCheckAt = at; }
        }
        output.WriteLine($"AI checks: {runs.Count} (was 24); quiet checks: {quiet}");
        foreach (var run in runs) output.WriteLine($"  {TimeZoneInfo.ConvertTime(run.At, London):HH:mm} {run.Kind}");
        Assert.InRange(runs.Count, 8, 12); Assert.Contains(runs, r => r.Kind == "digest" && TimeZoneInfo.ConvertTime(r.At, London).ToString("HH:mm") == "21:30");
        Assert.Equal(4, runs.Count(r => r.Kind == "window_end"));
        Assert.Equal(2, runs.Count(r => r.Kind == "digest"));
        Assert.All(runs.Zip(runs.Skip(1)), pair => Assert.True(pair.Second.At - pair.First.At >= TimeSpan.FromMinutes(60)));
        Assert.All(runs.Zip(runs.Skip(1)), pair => Assert.True(pair.Second.At - pair.First.At <= TimeSpan.FromHours(4).Add(TimeSpan.FromMinutes(5))));

        // Prompt size: an automatic check one hour after the last sends what happened since plus a baseline, not 24–48 hours of rows.
        using var db = new DataStore(directory);
        var env = await Live(db, null);
        var now = DateTimeOffset.UtcNow;
        db.SavePlan(new PlanSnapshot { Source = "Predbat", At = now.AddHours(-30), CollectedAt = now.AddHours(-30), Slots = Enumerable.Range(0, 120).Select(i => new PlanSlot(now.AddHours(-29).AddMinutes(30 * i), .4, null, .1, null, 50, null, 6.67, 12.28, i % 7 == 0 ? "Chrg" : "Demand", .02)).ToList() });
        var samples = new List<TelemetrySample>();
        foreach (var metric in new[] { "load", "pv", "grid_import", "grid_export", "battery_charge", "battery_discharge" })
            for (var m = -48 * 60; m <= 0; m += 5) samples.Add(new(metric, "sensor." + metric, now.AddMinutes(m), 100 + (m + 48 * 60) * 0.004, "kWh", "HomeAssistant", "1", "kWh", now.AddMinutes(m)));
        db.SaveTelemetry(samples);
        await env.State.MutateAsync(x => x.LastAnalysis = null);
        await env.Analysis.RunAsync(new(AnalysisService.ScheduledReviewQuestion(env.State.Read(false)), Scheduled: true));
        var full = env.Provider.Prompts[^1].Length;
        await env.State.MutateAsync(x => x.LastAnalysis = now.AddHours(-1));
        await env.Analysis.RunAsync(new(AnalysisService.ScheduledReviewQuestion(env.State.Read(false)), Scheduled: true));
        var scoped = env.Provider.Prompts[^1].Length;
        output.WriteLine($"First-turn prompt: {full:N0} characters for a full brief, {scoped:N0} for an hourly-scoped automatic brief ({100.0 * scoped / full:0}%).");
        output.WriteLine($"Day total first-turn characters: old {24 * full:N0}; new {runs.Count * scoped:N0} ({100.0 * runs.Count * scoped / (24.0 * full):0}% of before).");
        Assert.True(scoped < full * 0.8, $"scoped {scoped} vs full {full}");
        Assert.True(runs.Count * scoped < 24 * full * 0.4);
    }
}
