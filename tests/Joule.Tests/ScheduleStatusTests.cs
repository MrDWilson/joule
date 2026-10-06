using Microsoft.Extensions.Configuration;
using Joule;
using Xunit;

namespace Joule.Tests;

public sealed class ScheduleStatusTests : IDisposable
{
    readonly string directory = Path.Combine(Path.GetTempPath(), "predbat-schedules-" + Guid.NewGuid().ToString("N"));
    static readonly DateTimeOffset Now = new(2026, 10, 2, 10, 0, 0, TimeSpan.Zero);
    static AppState Scheduled() => new() { Ai = new AiPreferences { Scheduled = true, Provider = "ChatGpt", Model = "fixture", IntervalMinutes = 60, MaxRunsPerDay = 2 } };
    static InvestigationScheduleStatus Status(AppState s, DateTimeOffset? now = null, bool running = false, bool connected = true, DateTimeOffset? lastAttempt = null) => InvestigationScheduler.BuildStatus(s, now ?? Now, running, false, false, connected, lastAttempt);

    [Fact] public void DisabledScheduleHasNoDueTimeEvenInRecommendMode()
    {
        var s = Scheduled(); s.Ai.Scheduled = false; s.Mode = "Recommend";
        var result = Status(s);
        Assert.False(result.Enabled); Assert.Equal("Disabled", result.State); Assert.Null(result.NextRunAt); Assert.Contains("AI settings", result.Reason);
    }
    [Fact] public void ProviderAndModelMustBeReadyBeforeScheduledRun()
    {
        var s = Scheduled();
        Assert.Equal("ProviderUnavailable", Status(s, connected: false).State);
        Assert.Null(Status(s, connected: false).NextRunAt);
        s.Ai.Model = " "; Assert.Equal("ProviderUnavailable", Status(s).State);
        s.Ai.Provider = "Demo"; Assert.Equal("ProviderUnavailable", Status(s).State);
    }
    [Fact] public void DailyLimitCountsTheHomesLocalDayAndOnlyChecksThatGotAnAnswer()
    {
        // 10:00 UTC on 2 Oct is 11:00 BST. A failure with no AI reply (0 tokens) doesn't use the allowance.
        var s = Scheduled(); s.Usage = [new(Now.AddHours(-2), "ChatGpt", "fixture", 100, 10, null, "Completed"), new(Now.AddMinutes(-5), "ChatGpt", "fixture", 0, 0, null, "Failed")];
        var result = Status(s);
        Assert.NotEqual("DailyLimit", result.State); Assert.Equal(1, result.RunsToday); Assert.Equal(1, result.FailedToday);
        s.Usage.Add(new(Now.AddMinutes(-3), "ChatGpt", "fixture", 0, 0, null, "Completed"));
        result = Status(s);
        // Resets at local midnight: 23:00 UTC while the clocks are on BST.
        Assert.Equal("DailyLimit", result.State); Assert.Equal(2, result.RunsToday); Assert.Equal(new DateTimeOffset(2026, 10, 2, 23, 0, 0, TimeSpan.Zero), result.NextRunAt);
        // 00:30 BST on 3 Oct (23:30 UTC on 2 Oct) is already a new local day.
        var afterReset = Status(s, new DateTimeOffset(2026, 10, 2, 23, 30, 0, TimeSpan.Zero));
        Assert.NotEqual("DailyLimit", afterReset.State); Assert.Equal(0, afterReset.RunsToday);
        // A failed check that did get an answer counts.
        Assert.True(InvestigationScheduler.Counts(new(Now, "ChatGpt", "fixture", 1, 0, null, "Failed")));
        Assert.False(InvestigationScheduler.Counts(new(Now, "ChatGpt", "fixture", 0, 0, null, "Failed")));
    }
    [Fact] public void AFailedCheckBacksOffFiveFifteenThenSixtyMinutesAndTheIntervalSpacesNormalChecks()
    {
        var s = Scheduled(); s.LastAnalysis = Now.AddHours(-1); s.Ai.MaxRunsPerDay = 12;
        Investigation Failed(DateTimeOffset at) => new() { At = at, Status = "Failed", FailureKind = "provider_busy", Headline = "ChatGPT didn't answer", Steps = ["a", "b"] };
        var first = Failed(Now.AddMinutes(-2)); first.NextTryAt = InvestigationScheduler.NextRetryAt(s, "provider_busy", true, null, Now.AddMinutes(-2));
        Assert.Equal(Now.AddMinutes(3), first.NextTryAt);
        s.Investigations.Add(first);
        Assert.Equal(Now.AddMinutes(-2).AddMinutes(15), InvestigationScheduler.NextRetryAt(s, "provider_busy", true, null, Now.AddMinutes(-2)));
        s.Investigations.Add(Failed(Now.AddMinutes(-1)));
        Assert.Equal(Now.AddMinutes(60), InvestigationScheduler.NextRetryAt(s, "provider_busy", true, null, Now));
        s.Investigations.Remove(s.Investigations[^1]);
        var waiting = Status(s);
        Assert.Equal("Waiting", waiting.State); Assert.Equal(Now.AddMinutes(3), waiting.NextRunAt); Assert.Contains("doesn't use today's allowance", waiting.Reason);
        var due = Status(s, Now.AddMinutes(4));
        Assert.Equal("Due", due.State); Assert.Equal("retry", due.TriggerKind); Assert.Equal(first.Id, due.ResumeId);
        Assert.Equal("Running", Status(s, running: true).State); Assert.Null(Status(s, running: true).NextRunAt);
        // A sign-in problem or a setup error isn't retried automatically.
        Assert.Null(InvestigationScheduler.NextRetryAt(s, "sign_in", false, null, Now));
        Assert.Null(InvestigationScheduler.NextRetryAt(s, "invalid_answer", false, null, Now));
    }
    sealed class NoWriter : IPredbatClient
    {
        public bool Configured => false; public bool WritesEnabled => false;
        public Task<LiveSnapshot> CollectAsync(CancellationToken ct = default) => throw new InvalidOperationException();
        public Task ApplyAsync(List<Change> c, List<Setting> s, CancellationToken ct = default) => throw new InvalidOperationException();
    }
    sealed class NoNetwork : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => throw new InvalidOperationException("No network expected from an inactive schedule.");
    }
    sealed class RateLimitedProvider : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.TooManyRequests));
    }
    sealed class Factory(HttpClient http) : IHttpClientFactory { public HttpClient CreateClient(string name) => http; }
    [Fact] public async Task UnavailableProviderIsSkippedWithoutCreatingInvestigationOrError()
    {
        using var db = new DataStore(directory); using var http = new HttpClient(new NoNetwork());
        db.Save(new AppState { DataSource = "Live", Ai = Scheduled().Ai });
        var state = new StateService(db, new NoWriter(), false); var config = new ConfigurationBuilder().Build();
        var auth = new ChatGptAuth(http, Path.Combine(directory, "auth")); var model = new AiModelClient(http, auth, config);
        var analysis = new AnalysisService(state, db, model, new Factory(http), config);
        var scheduler = new InvestigationScheduler(state, analysis, model, auth);
        Assert.False(scheduler.TryStart(Now, CancellationToken.None));
        Assert.False(scheduler.TryStart(Now.AddHours(1), CancellationToken.None));
        Assert.Empty(state.Read(false).Investigations); Assert.Empty(state.Read(false).Usage); Assert.Null(state.Read(false).AnalysisError);
        Assert.Null(scheduler.Status(Now).LastAttemptAt); Assert.False(analysis.Running);
    }
    [Fact] public void DailyCappedScheduleIsSkippedWithoutChangingTheSavedPreferences()
    {
        using var db = new DataStore(directory); using var http = new HttpClient(new NoNetwork());
        var saved = Scheduled(); saved.DataSource = "Live"; saved.Ai.Provider = "Api";
        saved.Usage = [new(Now.AddHours(-3), "Api", "fixture", 0, 0, null, "Completed"), new(Now.AddHours(-2), "Api", "fixture", 10, 0, null, "Failed")]; db.Save(saved);
        var state = new StateService(db, new NoWriter(), false);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Ai:ApiKey"] = "fixture" }).Build();
        var auth = new ChatGptAuth(http, Path.Combine(directory, "auth")); var model = new AiModelClient(http, auth, config);
        var analysis = new AnalysisService(state, db, model, new Factory(http), config);
        var scheduler = new InvestigationScheduler(state, analysis, model, auth);
        Assert.False(scheduler.TryStart(Now, CancellationToken.None));
        Assert.False(scheduler.TryStart(Now.AddHours(1), CancellationToken.None));
        Assert.True(db.Load()!.Ai.Scheduled); Assert.Equal(2, state.Read(false).Usage.Count);
        Assert.Empty(state.Read(false).Investigations); Assert.Null(state.Read(false).AnalysisError);
    }
    [Fact] public async Task AdmittedScheduledDemoRunCompletesAndRecordsActualAttemptTime()
    {
        using var db = new DataStore(directory); using var http = new HttpClient(new NoNetwork());
        var state = new StateService(db, new NoWriter(), true);
        // A window that has just ended makes the check due at any time of day. With only the first-run heartbeat due, a digest
        // within the spacing would (rightly) make it wait, so this test failed in the hour before each digest.
        await state.MutateAsync(s => { s.Ai = new AiPreferences { Provider = "Demo", Scheduled = true }; s.Investigations.Clear(); s.Usage.Clear(); s.LastAnalysis = null; s.AiSchedule.Pending.Add(new("test:window-end", "window_end", DateTimeOffset.UtcNow.AddMinutes(-1), "a cheap window that just ended")); });
        var config = new ConfigurationBuilder().Build(); var auth = new ChatGptAuth(http, Path.Combine(directory, "auth")); var model = new AiModelClient(http, auth, config);
        var analysis = new AnalysisService(state, db, model, new Factory(http), config); var scheduler = new InvestigationScheduler(state, analysis, model, auth);
        var started = DateTimeOffset.UtcNow;
        Assert.True(scheduler.TryStart(started, CancellationToken.None));
        for (var i = 0; i < 200 && analysis.Running; i++) await Task.Delay(10);
        Assert.False(analysis.Running); Assert.Equal("Completed", Assert.Single(state.Read(false).Investigations).Status);
        Assert.NotNull(db.Load()!.LastAnalysisAttemptAt); Assert.InRange(db.Load()!.LastAnalysisAttemptAt!.Value, started, DateTimeOffset.UtcNow);
        Assert.Equal("Waiting", scheduler.Status(DateTimeOffset.UtcNow).State);
        Assert.False(scheduler.TryStart(DateTimeOffset.UtcNow, CancellationToken.None));
        Assert.Single(state.Read(false).Usage);
    }
    [Fact] public async Task ProviderQuotaFailureWaitsFullIntervalAndSurvivesRestart()
    {
        using var db = new DataStore(directory); using var http = new HttpClient(new RateLimitedProvider());
        var saved = Scheduled(); saved.DataSource = "Live"; saved.Ai.Provider = "Api"; db.Save(saved);
        var state = new StateService(db, new NoWriter(), false);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Ai:ApiKey"] = "fixture" }).Build();
        var auth = new ChatGptAuth(http, Path.Combine(directory, "auth")); var model = new AiModelClient(http, auth, config);
        var analysis = new AnalysisService(state, db, model, new Factory(http), config);
        await analysis.RunAsync(new());
        var usage = Assert.Single(state.Read(false).Usage); Assert.Equal("Failed", usage.Status);
        Assert.Contains("HTTP 429", state.Read(false).AnalysisError!);
        var scheduler = new InvestigationScheduler(state, analysis, model, auth);
        Assert.False(scheduler.TryStart(DateTimeOffset.UtcNow, CancellationToken.None));
        var restarted = InvestigationScheduler.BuildStatus(db.Load()!, DateTimeOffset.UtcNow, false, false, true, false);
        var failed = Assert.Single(state.Read(false).Investigations);
        Assert.Equal("Waiting", restarted.State); Assert.Equal(failed.NextTryAt, restarted.NextRunAt); Assert.NotNull(failed.NextTryAt);
        Assert.Contains("didn't finish", restarted.Reason); Assert.Null(failed.Verdict); Assert.Equal("rate_limited", failed.FailureKind);
    }
    [Fact] public void ReportStatusShowsOptInAndUsesLocalDeliveryTime()
    {
        var s = new AppState(); s.ReportPreferences.DailyEnabled = false; var disabled = ReportService.BuildScheduleStatus(s, Now);
        Assert.Equal("Disabled", disabled.Daily.State); Assert.Null(disabled.Daily.NextRunAt);
        s.ReportPreferences.DailyEnabled = true;
        var before = new DateTimeOffset(2026, 10, 2, 6, 30, 0, TimeSpan.Zero);
        Assert.Equal(new DateTimeOffset(2026, 10, 2, 7, 0, 0, TimeSpan.Zero), ReportService.BuildScheduleStatus(s, before).Daily.NextRunAt);
        Assert.Equal("Due", ReportService.BuildScheduleStatus(s, Now).Daily.State);
        s.Reports.Add(new EnergyReport { Kind = "Daily", From = new(2026, 9, 30, 23, 0, 0, TimeSpan.Zero), To = new(2026, 10, 1, 23, 0, 0, TimeSpan.Zero), CreatedAt = Now.AddMinutes(-1) });
        var ready = ReportService.BuildScheduleStatus(s, Now).Daily;
        Assert.Equal("Waiting", ready.State); Assert.Equal(new DateTimeOffset(2026, 10, 3, 7, 0, 0, TimeSpan.Zero), ready.NextRunAt); Assert.Equal(Now.AddMinutes(-1), ready.LastGeneratedAt);
    }
    [Fact] public void WeeklyReportStatusMatchesCatchupAndDoesNotDuplicatePeriod()
    {
        var s = new AppState(); s.ReportPreferences.WeeklyEnabled = true;
        var due = ReportService.BuildScheduleStatus(s, Now).Weekly; Assert.Equal("Due", due.State);
        s.Reports.Add(new EnergyReport { Kind = "Weekly", From = new(2026, 9, 20, 23, 0, 0, TimeSpan.Zero), To = new(2026, 9, 27, 23, 0, 0, TimeSpan.Zero), CreatedAt = Now });
        Assert.Equal(new DateTimeOffset(2026, 10, 5, 7, 0, 0, TimeSpan.Zero), ReportService.BuildScheduleStatus(s, Now).Weekly.NextRunAt);
    }
    [Fact] public void ReportDeliveryHourRemainsLocalAcrossDstAndInvalidPreferencesAreActionable()
    {
        var s = new AppState(); s.ReportPreferences.DailyEnabled = true;
        var now = new DateTimeOffset(2026, 10, 24, 10, 0, 0, TimeSpan.Zero);
        s.Reports.Add(new EnergyReport { Kind = "Daily", From = new(2026, 10, 22, 23, 0, 0, TimeSpan.Zero), To = new(2026, 10, 23, 23, 0, 0, TimeSpan.Zero) });
        Assert.Equal(new DateTimeOffset(2026, 10, 25, 8, 0, 0, TimeSpan.Zero), ReportService.BuildScheduleStatus(s, now).Daily.NextRunAt);
        s.ReportPreferences.TimeZone = "invalid/timezone";
        Assert.Equal("InvalidPreferences", ReportService.BuildScheduleStatus(s, now).Daily.State);
        Assert.Null(ReportService.BuildScheduleStatus(s, now).Daily.NextRunAt);
    }
    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
}
