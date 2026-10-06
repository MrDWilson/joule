using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Joule;
using Xunit;

namespace Joule.Tests;

/// <summary>
/// What a check says and how it ends: house style with one corrective retry then server repair, verdicts that are never invented as
/// "problem", failures recorded as a status, the running stub, resume after an interruption, retry notices, repeats and claims.
/// The provider is scripted; these pin the server's rules, not model judgement.
/// </summary>
public sealed class AnalysisQualityTests : IDisposable
{
    readonly string directory = Path.Combine(Path.GetTempPath(), "joule-quality-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(directory, true); } catch { } }

    sealed class NoWriter : IPredbatClient
    {
        public bool Configured => true;
        public bool WritesEnabled => false;
        public Task<LiveSnapshot> CollectAsync(CancellationToken ct = default) => Task.FromResult(new LiveSnapshot([], null, "{}", "{}"));
        public Task ApplyAsync(List<Change> c, List<Setting> s, CancellationToken ct = default) => throw new InvalidOperationException("No writes.");
    }
    sealed class Factory(HttpClient http) : IHttpClientFactory { public HttpClient CreateClient(string name) => http; }
    /// <summary>Answers each call with the script's text; null means an overloaded server (HTTP 503 server_is_overloaded).</summary>
    sealed class Provider(Func<int, string, string?> reply) : HttpMessageHandler
    {
        public List<string> Prompts = [];
        public Func<int, CancellationToken, Task>? BeforeReply;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri!.Host == "raw.githubusercontent.com") return new(HttpStatusCode.ServiceUnavailable);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            var messages = body.RootElement.GetProperty("messages"); var prompt = messages[messages.GetArrayLength() - 1].GetProperty("content").GetString()!;
            Prompts.Add(prompt);
            if (BeforeReply != null) await BeforeReply(Prompts.Count - 1, ct);
            var text = reply(Prompts.Count - 1, prompt);
            if (text is null) return new(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("{\"error\":{\"code\":\"server_is_overloaded\",\"message\":\"Our servers are busy.\"}}", Encoding.UTF8, "application/json") };
            var finishReason = text == CutOff ? "length" : "stop";
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { choices = new[] { new { finish_reason = finishReason, message = new { content = text } } }, usage = new { prompt_tokens = 1000, completion_tokens = 50 } }), Encoding.UTF8, "application/json") };
        }
    }
    /// <summary>Script text that makes the provider report an answer cut off at the output limit.</summary>
    const string CutOff = "{\"action\":\"fin";
    static IConfiguration Config() => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Ai:ApiKey"] = "fixture-secret" }).Build();

    async Task<(StateService State, AnalysisService Analysis, AiModelClient Model)> Services(DataStore db, HttpMessageHandler handler, Action<AppState>? setup = null)
    {
        var http = new HttpClient(handler); var config = Config();
        var state = new StateService(db, new NoWriter(), true);
        var model = new AiModelClient(http, new ChatGptAuth(http, Path.Combine(directory, "auth")), config) { RetryDelays = [] };
        var analysis = new AnalysisService(state, db, model, new Factory(http), config);
        await state.MutateAsync(s => { s.Proposals.Clear(); s.Investigations.Clear(); s.Usage.Clear(); s.Ai = new AiPreferences { Provider = "Api", Model = "fixture", MaxRunsPerDay = 50 }; setup?.Invoke(s); });
        return (state, analysis, model);
    }

    static string Query => JsonSerializer.Serialize(new { action = "query", sql = "SELECT count(*) AS n FROM plan_slots", notes = "counting plans" });
    static string Finish(string title, string? verdict = "problem", string summary = "The battery stopped charging early. Check the inverter.", object? extra = null)
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(new { action = "finish", title, summary, evidence = new[] { "The battery reached 61% at 02:30 against 94% planned." }, evidenceReferences = new[] { "configuration" } }))!.AsObject();
        if (verdict != null) node["verdict"] = verdict;
        if (extra != null) foreach (var (k, v) in System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(extra))!.AsObject().ToList()) node[k] = v?.DeepClone();
        return node.ToJsonString();
    }

    [Fact]
    public async Task AFinishWithAPredbatCodeIsRetriedOnceAndThenRepairedByTheServer()
    {
        using var db = new DataStore(directory);
        var provider = new Provider((_, _) => Finish("FrzExp slot lost 2 points at 09:00", summary: "At 2026-10-05T08:00:00Z `sensor.my_home_solar_generated` stalled during a FrzExp slot."));
        var s = await Services(db, provider);
        await s.Analysis.RunAsync(new("What happened at 09:00?"));
        Assert.Equal(2, provider.Prompts.Count);
        Assert.Contains("Your finish reply was rejected", provider.Prompts[1]); Assert.Contains("FrzExp", provider.Prompts[1]);
        var i = s.State.Read().Investigations.Single();
        Assert.Equal("Completed", i.Status);
        Assert.DoesNotContain("FrzExp", i.Title); Assert.StartsWith("Export solar, don't charge battery slot lost 2 points", i.Title);
        Assert.DoesNotContain("FrzExp", i.Headline!);
        // The summary the UI shows gets the same repair: no codes, entity IDs, backticks or UTC stamps.
        Assert.StartsWith("At 09:00 the My Home Solar Generated sensor stalled", i.Summary);
        foreach (var banned in new[] { "FrzExp", "`", "sensor.", "Z " }) Assert.DoesNotContain(banned, i.Summary);
        Assert.Contains(i.StepDetails, d => d.Label.Contains("fix its wording"));
    }

    [Fact]
    public void TheStyleCheckBansOnlyCodesEntityIdsBackticksAndTimestamps()
    {
        using var ok = JsonDocument.Parse(JsonSerializer.Serialize(new { title = "Solar forecast scaling is 10% high for the local shop slot (PV observed)", headline = "Local time is fine", nextSteps = new[] { new { title = "Ask the installer about the PV slot" } } }));
        Assert.Empty(InvestigationQuality.StyleIssues(ok.RootElement));
        using var bad = JsonDocument.Parse(JsonSerializer.Serialize(new { title = "HoldChrg at 2026-10-05T02:30:00Z", plain = "Check `sensor.my_home_solar_generated`", nextSteps = new[] { new { title = "Restore input_number.predbat_pv_today" } } }));
        var issues = InvestigationQuality.StyleIssues(bad.RootElement);
        Assert.Contains(issues, x => x.Contains("HoldChrg")); Assert.Contains(issues, x => x.Contains("backticks")); Assert.Contains(issues, x => x.Contains("2026-10-05T02:30:00Z"));
        Assert.Contains(issues, x => x.Contains("sensor.my_home_solar_generated")); Assert.Contains(issues, x => x.Contains("input_number.predbat_pv_today"));
        var london = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");
        Assert.Equal("Hold at charge target at 03:30", InvestigationQuality.Sanitise("HoldChrg at 2026-10-05T02:30:00Z", london));
        Assert.Equal("Check your solar meter", InvestigationQuality.Sanitise("Check `sensor.pv`", london, new Dictionary<string, string> { ["sensor.pv"] = "your solar meter" }));
    }

    [Fact]
    public async Task AFinishWithNoVerdictGetsOneRetryAndStillCompletes()
    {
        using var db = new DataStore(directory);
        var provider = new Provider((_, _) => Finish("No material change since the last review", verdict: null, summary: "Everything went to plan. Solar was a little above forecast and the overnight charge reached its target on time."));
        var s = await Services(db, provider);
        await s.Analysis.RunAsync(new());
        Assert.Equal(2, provider.Prompts.Count); Assert.Contains("verdict is missing", provider.Prompts[1]);
        var i = s.State.Read().Investigations.Single();
        Assert.Equal("Completed", i.Status); Assert.Equal("no_change", i.Verdict); Assert.Null(i.Severity);
        // A quiet check gets the server's fixed one-liner, never the model's narration of what worked.
        Assert.True(i.Summary.Length <= InvestigationQuality.NoChangeLimit); Assert.Contains("nothing new needs your attention", i.Summary);
        Assert.DoesNotContain("Solar was a little above forecast", i.Summary);
        Assert.Equal("Nothing new", i.Headline); Assert.Equal("Nothing new since the last check", i.Title);
    }

    [Fact]
    public async Task AMissingVerdictWithAToDoIsANeutralFindingNotAProblem()
    {
        using var db = new DataStore(directory);
        var step = new { title = "Replace the CT clamp on the meter tails", rationale = "Readings jump", suggestedAction = "Ask an electrician to reseat it", verification = "Readings are smooth", uncertainty = "Could be the integration", evidenceReferences = new[] { "configuration" } };
        var provider = new Provider((_, _) => Finish("Import readings jump by 3 kWh", verdict: null, extra: new { nextSteps = new[] { step } }));
        var s = await Services(db, provider);
        await s.Analysis.RunAsync(new());
        var i = s.State.Read().Investigations.Single();
        Assert.Equal("Completed", i.Status); Assert.Equal("finding", i.Verdict); Assert.Single(i.NextSteps);
    }

    [Fact]
    public async Task AProviderFailureIsAStatusWithAPlainMessageNeverAProblem()
    {
        using var db = new DataStore(directory);
        var provider = new Provider((n, _) => n == 0 ? Query : null);
        var s = await Services(db, provider, x => x.Ai.Scheduled = true);
        await s.Analysis.RunAsync(new());
        var i = s.State.Read().Investigations.Single();
        Assert.Equal("Failed", i.Status); Assert.Null(i.Verdict); Assert.Equal("provider_busy", i.FailureKind); Assert.Equal("server_is_overloaded", i.ProviderCode);
        Assert.StartsWith("The AI service didn't answer (server busy). Nothing was changed.", i.Summary);
        Assert.Contains("Next try", i.Summary); Assert.NotNull(i.NextTryAt);
        Assert.Equal("Check didn't finish", i.Title); Assert.Equal("Didn't finish", i.Category);
        Assert.Contains(i.ToolEvidence, t => t.Kind == "query" && t.Success);
        Assert.Contains("1 result gathered so far is kept", Assert.Single(i.Evidence));
        // The failed attempt got one AI answer, so it counts; a failure before any answer would not.
        Assert.Equal(1000, s.State.Read().Usage.Single().InputTokens);
    }

    [Fact]
    public async Task ARunningCheckIsSavedAfterEachStepAndARestartMarksItInterruptedForResumption()
    {
        using var db = new DataStore(directory);
        var reachedSecondCall = new TaskCompletionSource(); var release = new TaskCompletionSource();
        var provider = new Provider((n, _) => n == 0 ? Query : Finish("Battery charged at 1.7 kW against a 5 kW plan"));
        provider.BeforeReply = async (n, _) => { if (n == 1) { reachedSecondCall.TrySetResult(); await release.Task; } };
        var s = await Services(db, provider);
        var run = s.Analysis.RunAsync(new("Why was charging slow?"));
        await reachedSecondCall.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var stub = s.State.Read().Investigations.Single();
        Assert.Equal("Running", stub.Status); Assert.Null(stub.Verdict);
        Assert.Contains(stub.StepDetails, d => d.Label == "Looked up stored readings");
        Assert.Contains(s.State.ReadInvestigation(stub.Id)!.ToolEvidence, t => t.Kind == "query");
        Assert.Single(db.ReadInvestigationTranscript(stub.Id));
        // What startup recovery does with a stub a crash left behind.
        var crashed = JsonDefaults.Clone(s.State.Read(false));
        Assert.Equal(1, AnalysisService.RecoverInterrupted(crashed, DateTimeOffset.UtcNow, TimeZoneInfo.Utc));
        var interrupted = crashed.Investigations.Single();
        Assert.Equal("Interrupted", interrupted.Status); Assert.Equal("restart", interrupted.FailureKind); Assert.Null(interrupted.Verdict);
        crashed.Ai.Scheduled = true; crashed.LastAnalysisAttemptAt = interrupted.At;
        var schedule = InvestigationScheduler.BuildStatus(crashed, DateTimeOffset.UtcNow, false, true, true, true);
        Assert.Equal("Due", schedule.State); Assert.Equal("resume", schedule.TriggerKind); Assert.Equal(interrupted.Id, schedule.ResumeId);
        release.SetResult(); await run;
        Assert.Equal("Completed", s.State.Read().Investigations.Single().Status);
    }

    [Fact]
    public async Task AFailedCheckResumesFromItsSavedTranscriptInsteadOfStartingAgain()
    {
        using var db = new DataStore(directory);
        var fail = true;
        var provider = new Provider((n, prompt) => !prompt.Contains("Conversation so far") ? Query : fail ? null : Finish("Battery charged at 1.7 kW against a 5 kW plan"));
        var s = await Services(db, provider);
        await s.Analysis.RunAsync(new("Why was charging slow?"));
        var failed = s.State.Read().Investigations.Single();
        Assert.Equal("Failed", failed.Status);
        var queryId = s.State.ReadInvestigation(failed.Id)!.ToolEvidence.Single(t => t.Kind == "query").Id;
        fail = false; var before = provider.Prompts.Count;
        await s.Analysis.RunAsync(new(ResumeOf: failed.Id));
        // One call: the earlier read is replayed from the transcript, not repeated.
        Assert.Equal(before + 1, provider.Prompts.Count);
        Assert.Contains(queryId, provider.Prompts[^1]); Assert.Contains("SELECT count(*) AS n FROM plan_slots", provider.Prompts[^1]);
        var done = s.State.Read().Investigations.Single();
        Assert.Equal(failed.Id, done.Id); Assert.Equal("Completed", done.Status); Assert.Equal("Why was charging slow?", done.Request.Question);
        Assert.Contains(done.StepDetails, d => d.Label.StartsWith("Resumed where it stopped"));
        Assert.Contains(s.State.ReadInvestigation(done.Id)!.ToolEvidence, t => t.Id == queryId);
        await Assert.ThrowsAsync<DomainException>(() => s.Analysis.RunAsync(new(ResumeOf: done.Id)));
    }

    [Fact]
    public async Task ResumingTheSameFailedCheckBacksOffFiveFifteenThenSixtyMinutesWithoutGrowingItsSteps()
    {
        // A long outage: the scheduler resumes the same record each time. Its backoff must keep growing and its steps stay bounded.
        using var db = new DataStore(directory);
        var provider = new Provider((n, prompt) => !prompt.Contains("Conversation so far") ? Query : null);
        var s = await Services(db, provider, x => x.Ai.Scheduled = true);
        s.Model.RetryDelays = [TimeSpan.Zero, TimeSpan.Zero]; s.Model.RetryJitter = 0;
        await s.Analysis.RunAsync(new(AnalysisService.ScheduledReviewQuestion(s.State.Read(false)), Scheduled: true));
        var first = s.State.Read().Investigations.Single();
        Assert.Equal("Failed", first.Status); Assert.Equal(1, first.Attempts);
        Assert.Equal(TimeSpan.FromMinutes(5), first.NextTryAt - first.FinishedAt);
        var waits = new List<TimeSpan>(); var stepCounts = new List<int>();
        for (var n = 0; n < 4; n++)
        {
            await s.Analysis.RunAsync(new(Scheduled: true, ResumeOf: first.Id));
            var again = s.State.Read().Investigations.Single();
            Assert.Equal(first.Id, again.Id); Assert.Equal("Failed", again.Status); Assert.Equal(n + 2, again.Attempts);
            waits.Add(again.NextTryAt!.Value - again.FinishedAt!.Value);
            stepCounts.Add(again.StepDetails.Count);
            // Only this try's retry notices and stop marker; earlier tries' are folded away.
            Assert.Equal(2, again.StepDetails.Count(d => d.Kind == "retry"));
            Assert.Single(again.StepDetails, d => d.Detail == "server: provider_busy");
            Assert.Single(again.StepDetails, d => d.Detail.StartsWith("server: resumed"));
        }
        Assert.Equal([TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(60), TimeSpan.FromMinutes(60), TimeSpan.FromMinutes(60)], waits);
        Assert.All(stepCounts, c => Assert.Equal(stepCounts[0], c));
        Assert.Contains(s.State.Read().Investigations.Single().StepDetails, d => d.Label.EndsWith("(try 5)"));
        // Earlier gathered results are still there.
        Assert.Contains(s.State.Read().Investigations.Single().StepDetails, d => d.Label == "Looked up stored readings");
    }

    [Fact]
    public async Task AnAutomaticRetryOfYourOwnQuestionSaysSo()
    {
        using var db = new DataStore(directory);
        var fail = true;
        var provider = new Provider((n, prompt) => !prompt.Contains("Conversation so far") ? Query : fail ? null : Finish("Battery charged at 1.7 kW against a 5 kW plan"));
        var s = await Services(db, provider, x => x.Ai.Scheduled = true);
        await s.Analysis.RunAsync(new("Why was charging slow?"));
        var failed = s.State.Read(false);
        var status = InvestigationScheduler.BuildStatus(failed, failed.Investigations.Single().NextTryAt!.Value.AddMinutes(1), false, false, true, true);
        Assert.Equal("Due", status.State); Assert.Equal("Retrying your question that didn't finish.", status.Reason); Assert.Equal("retry of your question", status.Trigger);
        fail = false;
        await s.Analysis.RunAsync(new(Scheduled: true, Trigger: status.Trigger, ResumeOf: status.ResumeId));
        Assert.Contains(s.State.Read().Activities, a => a.Message == "Trying your question again automatically: “Why was charging slow?”.");
        Assert.Equal("Why was charging slow?", s.State.Read().Investigations.Single().Request.Question);
    }

    [Fact]
    public async Task RetryNoticesFromTheModelClientAppearAsStepsAndActivity()
    {
        using var db = new DataStore(directory);
        var calls = 0;
        var provider = new Provider((_, _) => calls++ == 0 ? null : Finish("Battery charged at 1.7 kW against a 5 kW plan"));
        var s = await Services(db, provider);
        s.Model.RetryDelays = [TimeSpan.Zero]; s.Model.RetryJitter = 0;
        await s.Analysis.RunAsync(new());
        var i = s.State.Read().Investigations.Single();
        Assert.Equal("Completed", i.Status);
        var retry = Assert.Single(i.StepDetails, d => d.Kind == "retry");
        Assert.Contains("is busy, trying again", retry.Label); Assert.Contains("(2 of 2)", retry.Label);
        for (var n = 0; n < 50 && !s.State.Read().Activities.Any(a => a.Message.Contains("trying again")); n++) await Task.Delay(20);
        Assert.Contains(s.State.Read().Activities, a => a.Message.Contains("trying again"));
    }

    [Fact]
    public async Task UserStopAndShutdownAreWordedDifferently()
    {
        using var db = new DataStore(directory);
        var started = new TaskCompletionSource();
        var provider = new Provider((_, _) => Query); provider.BeforeReply = async (_, ct) => { started.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); };
        var s = await Services(db, provider);
        var run = s.Analysis.RunAsync(new());
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(s.Analysis.Cancel()); await run.WaitAsync(TimeSpan.FromSeconds(10));
        var stopped = s.State.Read().Investigations.Single();
        Assert.Equal("Interrupted", stopped.Status); Assert.Equal("stopped", stopped.FailureKind); Assert.Equal("You stopped this check", stopped.Headline);
        Assert.Null(stopped.NextTryAt);
        using var shutdown = new CancellationTokenSource(); started = new TaskCompletionSource();
        var second = s.Analysis.RunAsync(new(), shutdown.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10)); shutdown.Cancel(); await second.WaitAsync(TimeSpan.FromSeconds(10));
        var restarted = s.State.Read().Investigations.OrderBy(i => i.At).Last();
        Assert.Equal("restart", restarted.FailureKind); Assert.Contains("Joule restarted", restarted.Summary); Assert.NotNull(restarted.NextTryAt);
    }

    [Fact]
    public async Task TheSameFindingAgainCountsOnTheOriginalInsteadOfANewCard()
    {
        using var db = new DataStore(directory);
        var provider = new Provider((_, _) => Finish("Export meter reads unknown after midnight (sensor.grid_export_today)", summary: "sensor.grid_export_today reads unknown until the first export."));
        var s = await Services(db, provider);
        await s.Analysis.RunAsync(new()); await s.Analysis.RunAsync(new());
        var all = s.State.Read().Investigations.OrderBy(i => i.At).ToList();
        Assert.Equal(2, all.Count);
        Assert.Equal(2, all[0].Occurrences); Assert.EndsWith("still happening (2×)", all[0].Headline);
        Assert.Equal(all[0].Id, all[1].RepeatOf); Assert.Equal("info", all[1].Severity);
        Assert.Equal(all[0].Fingerprint, all[1].Fingerprint);
        // The repeat is left out of the next brief's previous findings; the original stays with its count.
        await s.Analysis.RunAsync(new());
        Assert.Contains("found 2 times", provider.Prompts[^1]);
    }

    [Fact]
    public async Task VerifyToDosBecomeThingsJouleWatchesAndClaimsAreRecordedThenRefuted()
    {
        using var db = new DataStore(directory);
        string? claimId = null;
        var verify = new { title = "Verify the 02:00–03:30 battery charge", rationale = "r", suggestedAction = "At about 02:05, confirm grid charging is enabled", verification = "v", uncertainty = "u", evidenceReferences = new[] { "configuration" } };
        var physical = new { title = "Check the CT clamp is on the right tail", rationale = "r", suggestedAction = "Ask an electrician to reseat the clamp", verification = "v", uncertainty = "u", evidenceReferences = new[] { "configuration" } };
        var provider = new Provider((n, prompt) =>
        {
            if (n == 0) return Finish("Battery charged at 3.3 kW against a 5 kW plan", extra: new { nextSteps = new object[] { verify, physical }, claims = new[] { new { text = "The battery charges at no more than 3.3 kW", test = "max charge kW in the next cheap window" } } });
            claimId = System.Text.RegularExpressions.Regex.Match(prompt, @"- id ([0-9a-f]{12}) ").Groups[1].Value;
            return Finish("Battery charged at 4.7 kW, refuting the 3.3 kW cap", verdict: "no_change", extra: new { refuteClaims = new[] { new { id = claimId, reason = "4.31 kWh in one hour on 5 Oct" } } });
        });
        var s = await Services(db, provider);
        await s.Analysis.RunAsync(new());
        var first = s.State.Read().Investigations.Single();
        Assert.Equal(["Check the CT clamp is on the right tail"], first.NextSteps.Select(x => x.Title));
        Assert.Equal(["Verify the 02:00–03:30 battery charge"], first.Watching);
        var claim = Assert.Single(s.State.Read().Claims); Assert.Equal("open", claim.Status);
        await s.Analysis.RunAsync(new());
        Assert.Equal(claim.Id, claimId);
        var settled = Assert.Single(s.State.Read().Claims); Assert.Equal("refuted", settled.Status); Assert.Equal("4.31 kWh in one hour on 5 Oct", settled.Reason);
        Assert.Contains("Open claims", provider.Prompts[1]); Assert.Contains("The battery charges at no more than 3.3 kW", provider.Prompts[1]);
    }

    [Fact]
    public async Task AnAnswerCutOffAtTheOutputLimitGetsOneRequestForAShorterOne()
    {
        using var db = new DataStore(directory);
        // The model client already retries once with less reasoning; when that is cut off too, the check asks for a shorter answer.
        var provider = new Provider((n, _) => n < 2 ? CutOff : Finish("Battery charged at 1.7 kW against a 5 kW plan"));
        var s = await Services(db, provider);
        await s.Analysis.RunAsync(new());
        Assert.Equal(3, provider.Prompts.Count);
        Assert.Contains("cut off at the output limit", provider.Prompts[2]);
        var i = s.State.Read().Investigations.Single();
        Assert.Equal("Completed", i.Status); Assert.Contains(i.StepDetails, d => d.Label.Contains("asked for a shorter one"));
    }

    [Fact]
    public async Task TheBriefCarriesTheHouseholdObjectiveAndTheMoneySection()
    {
        using var db = new DataStore(directory);
        var provider = new Provider((_, _) => Finish("Battery charged at 1.7 kW against a 5 kW plan"));
        var s = await Services(db, provider, x => x.HouseholdObjective = HouseholdObjective.LimitCycling);
        await s.Analysis.RunAsync(new());
        Assert.Contains("## Household objective (untrusted data)\nLimit battery wear", provider.Prompts[0]);
        Assert.Contains("## Money (net cost is the figure to quote)", provider.Prompts[0]);
        Assert.Contains("Voice: write headline, plain, title, summary and to-do titles in plain UK English", provider.Prompts[0]);
        Assert.Contains("\"verdict\":\"problem|opportunity|no_change\"", provider.Prompts[0]);
    }

    [Fact]
    public void ConfidenceComesFromWhatTheFindingCites()
    {
        List<ToolEvidence> tools =
        [
            new("configuration", "configuration", "settings", DateTimeOffset.UtcNow, true, "{}", []),
            new("pva", "plan_vs_actual", "a/b", DateTimeOffset.UtcNow, true, "{}", []),
            new("sum", "summary", "a/b", DateTimeOffset.UtcNow, true, "{}", []),
            new("log", "mcp", "get_log: {\"name\":\"get_log\"}", DateTimeOffset.UtcNow, true, "{}", []),
            new("broken", "query", "SELECT", DateTimeOffset.UtcNow, false, "{}", [])
        ];
        Assert.Equal("High", InvestigationQuality.Confidence(["pva", "sum"], tools));
        Assert.Equal("High", InvestigationQuality.Confidence(["pva", "log"], tools));
        Assert.Equal("Medium", InvestigationQuality.Confidence(["log", "configuration"], tools));
        Assert.Equal("Low", InvestigationQuality.Confidence(["configuration", "broken"], tools));
    }

    [Fact]
    public void StepLabelsDescribeReadsInWords()
    {
        var london = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");
        ToolEvidence Tool(string kind, string request) => new("t", kind, request, DateTimeOffset.UtcNow, true, "{}", []);
        Assert.Equal("Compared the plan with your meters, 04:00–08:18", InvestigationQuality.StepLabel(Tool("plan_vs_actual", "2026-10-05T04:00:00.0000000+01:00/2026-10-05T08:18:00.0000000+01:00"), london));
        Assert.Equal("Read Predbat's log for “Warn”, 02:05–03:06", InvestigationQuality.StepLabel(Tool("mcp", "get_log: {\"name\":\"get_log\",\"arguments\":{\"filter\":\"all\",\"search\":\"Warn\",\"start\":\"2026-10-05 02:05\",\"end\":\"2026-10-05 03:06\"}}"), london));
        Assert.Equal("Re-read earlier results for “Error”", InvestigationQuality.StepLabel(Tool("evidence", "Archive tool-1, offset 0, limit 6000, search: Error"), london));
        Assert.Equal("Read Predbat's guide on “combine_charge_slots”", InvestigationQuality.StepLabel(Tool("documentation", "combine_charge_slots"), london));
        Assert.Contains("Automatic check of 05:16–08:20", InvestigationQuality.ScheduledLabel(new DateTimeOffset(2026, 10, 5, 4, 16, 0, TimeSpan.Zero), new DateTimeOffset(2026, 10, 5, 7, 20, 0, TimeSpan.Zero), london, null));
    }

    [Fact]
    public async Task McpDiscoveryAtTheStartOfACheckIsRecordedForTheStatusCard()
    {
        using var db = new DataStore(directory);
        var provider = new Provider((_, _) => Finish("Battery charged at 1.7 kW against a 5 kW plan"));
        var http = new HttpClient(provider); var config = Config();
        var state = new StateService(db, new NoWriter(), true);
        var analysis = new AnalysisService(state, db, new AiModelClient(http, new ChatGptAuth(http, Path.Combine(directory, "auth")), config) { RetryDelays = [] }, new Factory(http), config, mcp: new FakeMcp());
        await state.MutateAsync(s => { s.Investigations.Clear(); s.Ai = new AiPreferences { Provider = "Api", Model = "fixture" }; });
        await analysis.RunAsync(new());
        var record = state.Read(false).McpDiscovery;
        Assert.NotNull(record); Assert.True(record!.Connected); Assert.Equal(1, record.ToolCount); Assert.Equal(["get_log"], record.Tools);
    }
    sealed class FakeMcp : IPredbatMcpClient
    {
        public bool Configured => true;
        public McpDiscovery Status => new(true, true, DateTimeOffset.UtcNow, [new("get_log", "Read the log", JsonDocument.Parse("{}").RootElement)], null);
        public Task<McpDiscovery> DiscoverAsync(CancellationToken ct = default) => Task.FromResult(Status);
        public Task<McpReadResult> CallReadOnlyAsync(string name, JsonElement arguments, CancellationToken ct = default) => Task.FromResult(new McpReadResult(true, "{\"lines\":[]}", false, null));
    }
}
