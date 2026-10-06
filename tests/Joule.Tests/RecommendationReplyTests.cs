using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Joule;
using Xunit;

namespace Joule.Tests;

// Scripted provider boundaries: these verify the reply workflow, storage and suppression, not model judgement.
public sealed class RecommendationReplyTests : IDisposable
{
    readonly string directory = Path.Combine(Path.GetTempPath(), "predbat-replies-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(directory, true); } catch { } }

    sealed class NoWriter : IPredbatClient
    {
        public bool Configured => true;
        public bool WritesEnabled => false;
        public Task<LiveSnapshot> CollectAsync(CancellationToken ct = default) => Task.FromResult(new LiveSnapshot([], null, "{}", "{}"));
        public Task ApplyAsync(List<Change> c, List<Setting> s, CancellationToken ct = default) => throw new InvalidOperationException("No writes.");
    }
    sealed class Factory(HttpClient http) : IHttpClientFactory { public HttpClient CreateClient(string name) => http; }
    sealed class Provider(Func<int, string, string?> reply, TimeSpan? delay = null) : HttpMessageHandler
    {
        public List<string> Prompts = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            var prompt = body.RootElement.GetProperty("messages")[0].GetProperty("content").GetString()!;
            Prompts.Add(prompt);
            if (delay is { } wait) await Task.Delay(wait, ct);
            var text = reply(Prompts.Count - 1, prompt);
            if (text is null) return new(HttpStatusCode.InternalServerError) { Content = new StringContent("upstream failure") };
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { choices = new[] { new { finish_reason = "stop", message = new { content = text } } }, usage = new { prompt_tokens = 900, completion_tokens = 60 } }), Encoding.UTF8, "application/json") };
        }
    }
    static IConfiguration Config() => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Ai:ApiKey"] = "fixture-provider-secret" }).Build();

    async Task<(StateService State, DataStore Db, RecommendationReplyService Replies, HttpClient Http)> Services(DataStore db, HttpMessageHandler handler, string provider = "Api")
    {
        var http = new HttpClient(handler); var config = Config();
        var state = new StateService(db, new NoWriter(), true);
        await state.MutateAsync(s => s.Ai = new AiPreferences { Provider = provider, Model = provider == "Demo" ? "" : "fixture" });
        return (state, db, new RecommendationReplyService(state, db, new AiModelClient(http, new ChatGptAuth(http, Path.Combine(directory, "auth")), config) { RetryDelays = [TimeSpan.Zero, TimeSpan.Zero] }, config) { RetryDelays = [TimeSpan.Zero, TimeSpan.Zero] }, http);
    }
    static string Verdict(string verdict, string reply, string? memory = null, bool? retire = null) =>
        JsonSerializer.Serialize(new { verdict, reply, memory, retire });

    // ---- Reply evaluation schema ----

    [Fact]
    public void EvaluationParsingAcceptsTheSchemaAndKeepsMemoryOnlyForAccept()
    {
        var accept = RecommendationReplyService.ParseEvaluation("```json\n" + Verdict("accept", "Agreed.", "The household cooks late.", true) + "\n```");
        Assert.Equal(("accept", "Agreed.", "The household cooks late.", true), (accept.Verdict, accept.Reply, accept.Memory, accept.Retire));
        var stayOpen = RecommendationReplyService.ParseEvaluation(Verdict("accept", "Fine, later.", null, false));
        Assert.False(stayOpen.Retire);
        var defaulted = RecommendationReplyService.ParseEvaluation("""{"verdict":"accept","reply":"Agreed."}""");
        Assert.True(defaulted.Retire); Assert.Null(defaulted.Memory);
        var disagree = RecommendationReplyService.ParseEvaluation(Verdict("disagree", "The log shows 1.7 kW at 02:30.", "Should not be stored.", true));
        Assert.Equal("disagree", disagree.Verdict); Assert.Null(disagree.Memory); Assert.False(disagree.Retire);
        Assert.Equal("clarify", RecommendationReplyService.ParseEvaluation(Verdict("clarify", "Which nights?")).Verdict);
    }

    [Fact]
    public void EvaluationParsingBoundsTextAndRejectsMalformedReplies()
    {
        var bounded = RecommendationReplyService.ParseEvaluation(Verdict("accept", new string('r', 900), new string('m', 301)));
        Assert.Equal(RecommendationReplyService.ReplyLimit, bounded.Reply.Length);
        Assert.Null(bounded.Memory); // half a rule is worse than none
        Assert.Throws<DomainException>(() => RecommendationReplyService.ParseEvaluation(Verdict("maybe", "Hmm.")));
        Assert.Throws<DomainException>(() => RecommendationReplyService.ParseEvaluation(Verdict("accept", " ")));
        Assert.Throws<DomainException>(() => RecommendationReplyService.ParseEvaluation("""{"verdict":"accept","reply":"Yes.","retire":"yes"}"""));
        Assert.Throws<DomainException>(() => RecommendationReplyService.ParseEvaluation("""{"verdict":"accept","reply":"Yes.","memory":["a"]}"""));
        Assert.ThrowsAny<JsonException>(() => RecommendationReplyService.ParseEvaluation("not json"));
    }

    // ---- Reply workflow ----

    [Fact]
    public async Task AcceptedReplyStoresMemoryDeniesTheProposalAndKeepsTheThread()
    {
        using var db = new DataStore(directory);
        db.AddMemory("There is a 10 kW heat pump.", "user");
        var provider = new Provider((_, _) => Verdict("accept", "Agreed: a late-cooking evening needs the buffer.", "The household cooks late; keep the evening load forecast as it is.", true));
        var services = await Services(db, provider);
        var proposal = services.State.Read().Proposals.Single();

        var outcome = await services.Replies.ReplyAsync(new("proposal", proposal.Id), "We cook late most evenings, so the forecast is right.", CancellationToken.None);

        Assert.Equal("accept", outcome.Verdict); Assert.True(outcome.Retired);
        Assert.Equal("The household cooks late; keep the evening load forecast as it is.", outcome.Memory);
        var fact = Assert.Single(db.ListMemory(), f => f.Source == "user-reply");
        Assert.Equal(proposal.InvestigationId, fact.InvestigationId);
        var stored = services.State.Read().Proposals.Single();
        Assert.Equal("Denied", stored.Status); Assert.NotNull(stored.DecidedAt);
        Assert.Equal("We cook late most evenings, so the forecast is right.", stored.DecisionNote);
        Assert.Equal(["user", "ai"], stored.Thread.Select(m => m.Role));
        Assert.Equal("accept", stored.Thread[1].Verdict); Assert.Equal(fact.Text, stored.Thread[1].Memory);
        Assert.Equal(900, stored.Thread[1].InputTokens);
        // A single short call with the item, the note, shared memory and the setting's current value.
        var prompt = Assert.Single(provider.Prompts);
        Assert.Contains("We cook late most evenings", prompt);
        Assert.Contains("There is a 10 kW heat pump.", prompt);
        Assert.Contains("Bring the evening load forecast closer to reality", prompt);
        Assert.Contains("\"key\":\"load_scaling\"", prompt); Assert.Contains("\"value\":\"1.08\"", prompt);
        Assert.Contains("verdict", prompt);
        Assert.Contains(services.State.Read().Activities, a => a.Message.Contains("The AI agreed and it was dismissed"));
        services.Http.Dispose();
    }

    [Fact]
    public async Task DisagreementKeepsTheItemOpenAndTheConversationContinues()
    {
        using var db = new DataStore(directory);
        var provider = new Provider((turn, _) => turn == 0
            ? Verdict("disagree", "Eight of ten evenings were 0.58 kWh below forecast; that is not a one-off.", "Ignored on disagree.")
            : Verdict("accept", "Understood; guests explain it.", null, true));
        var services = await Services(db, provider);
        var proposal = services.State.Read().Proposals.Single();

        var first = await services.Replies.ReplyAsync(new("proposal", proposal.Id), "This is wrong.", CancellationToken.None);
        Assert.Equal("disagree", first.Verdict); Assert.False(first.Retired); Assert.Null(first.Memory);
        Assert.Empty(db.ListMemory());
        var open = services.State.Read().Proposals.Single();
        Assert.Equal("Pending", open.Status); Assert.Equal(2, open.Thread.Count);
        Assert.Contains("0.58 kWh", open.Thread[1].Text);

        var second = await services.Replies.ReplyAsync(new("proposal", proposal.Id), "We had guests all week.", CancellationToken.None);
        Assert.Equal("accept", second.Verdict);
        Assert.Contains("This is wrong.", provider.Prompts[1]); // the earlier exchange is part of the next evaluation
        Assert.Contains("0.58 kWh", provider.Prompts[1]);
        Assert.Equal(4, services.State.Read().Proposals.Single().Thread.Count);
        await Assert.ThrowsAsync<DomainException>(() => services.Replies.ReplyAsync(new("proposal", proposal.Id), "Again", CancellationToken.None));
        services.Http.Dispose();
    }

    [Fact]
    public async Task ProviderFailureKeepsTheItemOpenAndOffersNoMemory()
    {
        using var db = new DataStore(directory);
        var services = await Services(db, new Provider((_, _) => null));
        var proposal = services.State.Read().Proposals.Single();

        var outcome = await services.Replies.ReplyAsync(new("proposal", proposal.Id), "Where exactly in apps.yaml should this go?", CancellationToken.None);

        Assert.Equal("unavailable", outcome.Verdict); Assert.False(outcome.Retired);
        Assert.Null(outcome.SuggestedMemory); Assert.Null(outcome.Memory);
        Assert.Empty(db.ListMemory());
        var stored = services.State.Read().Proposals.Single();
        Assert.Equal("Pending", stored.Status); Assert.Null(stored.DecisionNote); Assert.Null(stored.DecidedAt);
        Assert.Equal(["user", "system"], stored.Thread.Select(m => m.Role));
        Assert.StartsWith(RecommendationReplyService.UnavailableNotice, stored.Thread[1].Text);
        Assert.DoesNotContain("upstream failure", stored.Thread[1].Text);
        Assert.Null(stored.Thread[1].SuggestedMemory);
        Assert.Contains(services.State.Read().Activities, a => a.Message.Contains("nothing was dismissed"));
        services.Http.Dispose();
    }

    [Fact]
    public async Task ProviderFailureOnAFollowUpFileChangeAndFindingLeavesEachOpen()
    {
        using var db = new DataStore(directory);
        var services = await Services(db, new Provider((_, _) => null));
        var step = new InvestigationNextStep { Id = "s1", Title = "Fix the hook", Rationale = "r", SuggestedAction = "a", Verification = "v", Uncertainty = "u" };
        var change = new ConfigFileChange { Id = "f1", File = "apps.yaml", Summary = "Add export_today", Location = "pred_bat", Snippet = "  export_today: sensor.x", Reason = "r" };
        await services.State.MutateAsync(s => s.Investigations.Add(new Investigation { Id = "inv", Title = "t", Summary = "s", Evidence = ["e"], NextSteps = [step], FileChanges = [change] }));
        await services.Replies.ReplyAsync(new("followup", "inv", "s1"), "I can't do that though.", CancellationToken.None);
        await services.Replies.ReplyAsync(new("filechange", "inv", "f1"), "Why?", CancellationToken.None);
        await services.Replies.ReplyAsync(new("finding", "inv"), "This is wrong.", CancellationToken.None);
        var stored = services.State.Read().Investigations.Single(i => i.Id == "inv");
        Assert.Equal("open", stored.NextSteps[0].Status); Assert.Null(stored.NextSteps[0].DecidedAt);
        Assert.Equal("pending", stored.FileChanges[0].Status); Assert.Null(stored.DismissedAt);
        services.Http.Dispose();
    }

    [Fact]
    public async Task AQuestionIsAnsweredAndNeverDismissesEvenWhenTheModelSaysAccept()
    {
        using var db = new DataStore(directory);
        var provider = new Provider((_, _) => JsonSerializer.Serialize(new { verdict = "accept", reply = "It doesn't break charging; it is harmless.", memory = "Should not be stored.", retire = true }));
        var services = await Services(db, provider);
        var proposal = services.State.Read().Proposals.Single();
        var outcome = await services.Replies.ReplyAsync(new("proposal", proposal.Id), "Is this a non issue? Does it actually break anything or?", CancellationToken.None);
        Assert.Equal("answer", outcome.Verdict); Assert.False(outcome.Retired); Assert.Null(outcome.Memory);
        Assert.Equal("Pending", services.State.Read().Proposals.Single().Status);
        Assert.Empty(db.ListMemory());
        Assert.Contains("verdict answer", provider.Prompts.Single());
        services.Http.Dispose();
    }

    [Fact]
    public async Task AnAcceptThatStillAsksTheUserToActKeepsTheItemOpen()
    {
        using var db = new DataStore(directory);
        var services = await Services(db, new Provider((_, _) => JsonSerializer.Serialize(new { verdict = "accept", reply = "That confirms it recovers in the morning.", action = "Reload Predbat after the solar sensor comes back.", memory = (string?)null, retire = true })));
        var proposal = services.State.Read().Proposals.Single();
        var outcome = await services.Replies.ReplyAsync(new("proposal", proposal.Id), "Yes, it comes back later.", CancellationToken.None);
        Assert.Equal("accept", outcome.Verdict); Assert.False(outcome.Retired);
        Assert.Equal("Pending", services.State.Read().Proposals.Single().Status);
        Assert.Equal("Reload Predbat after the solar sensor comes back.", outcome.Thread[^1].Action);
        services.Http.Dispose();
    }

    [Fact]
    public async Task AReplyCanDraftAFileChangeAndCarriesJoulesSensorKnowledge()
    {
        using var db = new DataStore(directory);
        var draft = new { file = "apps.yaml", summary = "Point pv_today at a template sensor that never reads unknown", location = "pred_bat", snippet = "  pv_today:\n    - sensor.solar_today_predbat", reason = "The source reads unknown overnight." };
        var provider = new Provider((_, _) => JsonSerializer.Serialize(new { verdict = "accept", reply = "Use a template sensor; here is the edit.", action = "Add the template sensor and the apps.yaml line.", memory = (string?)null, retire = false, fileChange = draft }));
        var services = await Services(db, provider);
        var step = new InvestigationNextStep { Id = "s1", Title = "Restore the solar sensor", Rationale = "pv_today unknown", SuggestedAction = "a", Verification = "v", Uncertainty = "u" };
        await services.State.MutateAsync(s => s.Investigations.Add(new Investigation { Id = "inv", Title = "Solar sensor unknown", Summary = "s", Evidence = ["sensor.my_home_solar_generated unknown"], NextSteps = [step] }));
        var outcome = await services.Replies.ReplyAsync(new("followup", "inv", "s1"), "I can't fix the source. How am I meant to fix this then?", CancellationToken.None);
        Assert.NotNull(outcome.FileChange);
        var stored = services.State.Read().Investigations.Single(i => i.Id == "inv");
        var change = Assert.Single(stored.FileChanges); Assert.Equal("pending", change.Status); Assert.Equal(change.Id, outcome.Thread[^1].FileChangeId);
        Assert.Equal("open", stored.NextSteps[0].Status);
        Assert.Contains("template sensor", provider.Prompts.Single(), StringComparison.OrdinalIgnoreCase);
        services.Http.Dispose();
    }

    [Theory]
    [InlineData("We cook late because the kids have clubs, so evening use is higher.", "The household cooks late because the kids have clubs, so evening use is higher.")]
    [InlineData("We always charge the car overnight", "The household always charges the car overnight.")]
    [InlineData("I have a heat pump.", "The homeowner has a heat pump.")]
    [InlineData("We cook late most evenings, so we prefer the higher forecast.", "The household cooks late most evenings, so the household prefers the higher forecast.")]
    public void RememberedFactsAreThirdPersonSentencesNotQuotes(string note, string fact) =>
        Assert.Equal(fact, RecommendationReplyService.ThirdPerson(note));

    [Fact]
    public async Task SlowProviderTimesOutIntoTheFallback()
    {
        using var db = new DataStore(directory);
        var services = await Services(db, new Provider((_, _) => Verdict("accept", "Too late."), TimeSpan.FromSeconds(10)));
        services.Replies.Timeout = TimeSpan.FromMilliseconds(200);
        var proposal = services.State.Read().Proposals.Single();
        var outcome = await services.Replies.ReplyAsync(new("proposal", proposal.Id), "Not needed.", CancellationToken.None);
        Assert.Equal("unavailable", outcome.Verdict);
        Assert.Contains("no answer within", outcome.Reply);
        services.Http.Dispose();
    }

    [Fact]
    public async Task ReplyNotesAreRedactedBeforeStorageAndPrompting()
    {
        using var db = new DataStore(directory);
        var provider = new Provider((_, _) => Verdict("disagree", "No."));
        var services = await Services(db, provider);
        var proposal = services.State.Read().Proposals.Single();
        await services.Replies.ReplyAsync(new("proposal", proposal.Id), "It is fine. api_key=fixture-provider-secret", CancellationToken.None);
        Assert.DoesNotContain("fixture-provider-secret", provider.Prompts.Single());
        Assert.DoesNotContain("fixture-provider-secret", JsonSerializer.Serialize(services.State.Read(), JsonDefaults.Options));
        await Assert.ThrowsAsync<DomainException>(() => services.Replies.ReplyAsync(new("proposal", proposal.Id), new string('x', 1001), CancellationToken.None));
        await Assert.ThrowsAsync<DomainException>(() => services.Replies.ReplyAsync(new("proposal", proposal.Id), "  ", CancellationToken.None));
        services.Http.Dispose();
    }

    [Fact]
    public async Task ScriptedDemoExercisesEveryVerdictWithoutAProvider()
    {
        using var db = new DataStore(directory);
        var provider = new Provider((_, _) => throw new InvalidOperationException("The demo must not call a provider."));
        var services = await Services(db, provider, "Demo");
        var proposal = services.State.Read().Proposals.Single();
        var demo = services.State.Read().Investigations.Single(i => i.FileChanges.Count == 1);

        var why = await services.Replies.ReplyAsync(new("proposal", proposal.Id), "Is this weekdays only?", CancellationToken.None);
        Assert.Equal("answer", why.Verdict);
        Assert.DoesNotContain("this comes from:", why.Reply);
        // Questions get a reply that fits them: where a file edit goes, or why it was raised.
        var where = await services.Replies.ReplyAsync(new("filechange", demo.Id, demo.FileChanges[0].Id), "Where does this go in apps.yaml?", CancellationToken.None);
        Assert.Equal("answer", where.Verdict);
        Assert.StartsWith("It goes in apps.yaml, at pred_bat, after import_today.", where.Reply);
        var reason = await services.Replies.ReplyAsync(new("filechange", demo.Id, demo.FileChanges[0].Id), "Why do I need this?", CancellationToken.None);
        Assert.StartsWith("Because of what the sample meters show: without export_today", reason.Reply);
        Assert.Equal("disagree", (await services.Replies.ReplyAsync(new("proposal", proposal.Id), "This is not right.", CancellationToken.None)).Verdict);
        var accept = await services.Replies.ReplyAsync(new("filechange", demo.Id, demo.FileChanges[0].Id), "We already export through a different meter.", CancellationToken.None);
        Assert.Equal("accept", accept.Verdict); Assert.NotNull(accept.Memory);
        Assert.Equal("Pending", services.State.Read().Proposals.Single().Status);
        Assert.Equal("dismissed", services.State.Read().Investigations.Single(i => i.Id == demo.Id).FileChanges[0].Status);
        Assert.Empty(provider.Prompts);
        services.Http.Dispose();
    }

    [Fact]
    public async Task FollowUpsAndFindingsAcceptRepliesToo()
    {
        using var db = new DataStore(directory);
        var services = await Services(db, new Provider((_, prompt) => prompt.Contains("manual follow-up")
            ? Verdict("accept", "Agreed.", "The inverter has no backup mode.", true)
            : Verdict("accept", "Agreed; the finding was wrong.", null, true)));
        var step = new InvestigationNextStep { Id = "step-1", Title = "Check backup mode", Rationale = "r", SuggestedAction = "a", Verification = "v", Uncertainty = "u" };
        var investigation = new Investigation { Id = "inv-1", Title = "Backup mode interrupted charging", Summary = "s", Evidence = ["e"], NextSteps = [step, step with { Id = "step-2", Title = "Other" }] };
        await services.State.MutateAsync(s => s.Investigations.Add(investigation));

        await services.Replies.ReplyAsync(new("followup", "inv-1", "step-1"), "There is no backup mode on this inverter.", CancellationToken.None);
        var stored = services.State.Read().Investigations.Single(i => i.Id == "inv-1");
        Assert.Equal("closed", stored.NextSteps[0].Status); Assert.NotNull(stored.NextSteps[0].DecidedAt);
        Assert.Equal("open", stored.NextSteps[1].Status);

        await services.Replies.ReplyAsync(new("finding", "inv-1"), "Charging was interrupted by a power cut.", CancellationToken.None);
        stored = services.State.Read().Investigations.Single(i => i.Id == "inv-1");
        Assert.NotNull(stored.DismissedAt); Assert.Equal("Charging was interrupted by a power cut.", stored.DecisionNote);
        Assert.Equal("closed", stored.NextSteps[1].Status); Assert.Equal("Findings dismissed by user", stored.NextSteps[1].ClosedReason);
        Assert.Equal(2, stored.Thread.Count);
        services.Http.Dispose();
    }

    [Fact]
    public void DismissalsWithNotesRecordTheDecision()
    {
        var s = DemoData.Create();
        var demo = s.Investigations.Single(i => i.FileChanges.Count == 1);
        var change = demo.FileChanges[0];
        InvestigationFileChanges.MarkApplied(s, demo.Id, change.Id);
        Assert.Equal("applied", change.Status); Assert.NotNull(change.AppliedAt);
        Assert.Throws<DomainException>(() => InvestigationFileChanges.MarkApplied(s, demo.Id, change.Id));
        RecommendationDecisions.DismissFileChange(s, demo.Id, change.Id, "Done differently.");
        Assert.Equal(("dismissed", "Done differently."), (change.Status, change.DecisionNote));
        Assert.Throws<DomainException>(() => RecommendationDecisions.DismissFileChange(s, demo.Id, change.Id, null));
        ChangeEngine.Deny(s, s.Proposals[0].Id, "Not now.");
        Assert.Equal("Not now.", s.Proposals[0].DecisionNote);
        Assert.Contains(s.Activities, a => a.Message.EndsWith("Note: Not now."));
        Assert.Equal(1000, RecommendationDecisions.Note(new string('n', 1000), [])!.Length);
        Assert.Null(RecommendationDecisions.Note("   ", []));
    }

    // ---- Suppression of re-raised recommendations ----

    static List<ToolEvidence> Tools(int observed) =>
    [
        new("measurement", "query", "SELECT load_actual FROM actual_energy ORDER BY time", DateTimeOffset.UtcNow, true, $"{{\"rows\":[{{\"load_actual\":{observed}}}]}}", []),
        new("docs", "documentation", "scaling", DateTimeOffset.UtcNow, true, "{}", [
            new("doc-scaling", "v9.3.3", "docs/customisation.md", "https://raw.githubusercontent.com/springfall2008/batpred/v9.3.3/docs/customisation.md", "fixture-sha", 1, 2, "input_number.predbat_load_scaling and input_number.predbat_pv_scaling set forecast scaling.", DateTimeOffset.UtcNow)
        ])
    ];
    static JsonDocument Finish(string after, JsonArray? nextSteps = null) => JsonDocument.Parse(new JsonObject
    {
        ["action"] = "finish", ["title"] = "Forecast finding", ["summary"] = "Review the forecast", ["evidence"] = new JsonArray("Observed load is unchanged."),
        ["nextSteps"] = nextSteps,
        ["proposals"] = new JsonArray(new JsonObject
        {
            ["title"] = "Lower load scaling", ["summary"] = "Test forecast scaling", ["expectedEffect"] = "A hypothesis", ["tradeoff"] = "Accuracy may worsen",
            ["evidence"] = new JsonArray("Observed load is unchanged."), ["evidenceReferences"] = new JsonArray("measurement"),
            ["documentationReferences"] = new JsonArray(new JsonObject { ["settingKey"] = "load_scaling", ["referenceId"] = "doc-scaling" }),
            ["changes"] = new JsonArray(new JsonObject { ["key"] = "load_scaling", ["after"] = after })
        })
    }.ToJsonString());
    static AppState Declined(string? note, DateTimeOffset decidedAt)
    {
        var state = DemoData.Create(); state.Proposals.Clear(); state.Investigations.Clear();
        var prior = new Investigation { ToolEvidence = Tools(1) }; state.Investigations.Add(prior);
        state.Proposals.Add(new Proposal { Title = "Lower load scaling", Status = "Denied", InvestigationId = prior.Id, Changes = [new("load_scaling", "1.08", "1.00")], DecisionNote = note, DecidedAt = decidedAt });
        return state;
    }

    [Theory]
    [InlineData("1.00")] // the identical change
    [InlineData("0.95")] // a further move in the same direction
    public void AProposalTheUserDeclinedWithANoteIsSkippedQuietly(string after)
    {
        var state = Declined("We cook late; the forecast is right.", DateTimeOffset.UtcNow.AddDays(-3));
        using var reply = Finish(after); var steps = new List<string>();
        var result = AnalysisService.ValidateResult(reply.RootElement, state, "Api", steps, Tools(1));
        Assert.Empty(result.Proposals);
        Assert.Contains(steps, s => s.Contains("not raised; you declined the same change"));
    }

    [Fact]
    public void SuppressionAllowsTheOppositeDirectionNewEvidenceAndExpiredDeclines()
    {
        using (var opposite = Finish("1.20")) Assert.Single(AnalysisService.ValidateResult(opposite.RootElement, Declined("Too low.", DateTimeOffset.UtcNow.AddDays(-3)), "Api", [], Tools(1)).Proposals);
        using (var fresh = Finish("0.95")) Assert.Single(AnalysisService.ValidateResult(fresh.RootElement, Declined("Too low.", DateTimeOffset.UtcNow.AddDays(-3)), "Api", [], Tools(2)).Proposals);
        using (var expired = Finish("0.95")) Assert.Single(AnalysisService.ValidateResult(expired.RootElement, Declined("Too low.", DateTimeOffset.UtcNow.AddDays(-31)), "Api", [], Tools(1)).Proposals);
        // A plain denial keeps the existing rule: the identical change fails the finish until the evidence changes.
        using var identical = Finish("1.00");
        Assert.Throws<DomainException>(() => AnalysisService.ValidateResult(identical.RootElement, Declined(null, DateTimeOffset.UtcNow.AddDays(-3)), "Api", [], Tools(1)));
    }

    [Fact]
    public void AFollowUpTheUserDismissedIsNotRaisedAgain()
    {
        var state = DemoData.Create(); state.Mode = "Monitor";
        state.Investigations.Add(new Investigation { NextSteps = [new() { Id = "old", Title = "Check  the inverter firmware", Status = "closed", DecidedAt = DateTimeOffset.UtcNow.AddDays(-1), DecisionNote = "Already current." }] });
        var tools = new List<ToolEvidence> { new("log", "mcp", "get_log", DateTimeOffset.UtcNow, true, "{}", []) };
        JsonObject Step(string title) => new() { ["title"] = title, ["rationale"] = "r", ["suggestedAction"] = "a", ["verification"] = "v", ["uncertainty"] = "u", ["evidenceReferences"] = new JsonArray("log") };
        using var reply = Finish("1.00", new JsonArray(Step("check the inverter firmware"), Step("Check the CT clamp")));
        var steps = new List<string>();
        var result = AnalysisService.ValidateResult(reply.RootElement, state, "Api", steps, tools);
        Assert.Equal("Check the CT clamp", Assert.Single(result.Investigation.NextSteps).Title);
        Assert.Contains(steps, s => s.Contains("not raised; you dismissed it"));
    }

    // ---- Configuration file changes ----

    static JsonObject FileChange(string file = "apps.yaml", string snippet = "  export_today:\n    - sensor.export_today") => new()
    {
        ["file"] = file, ["summary"] = "Add export_today", ["location"] = "pred_bat", ["snippet"] = snippet, ["reason"] = "No export history is recorded."
    };
    static List<ConfigFileChange> ParseFiles(JsonNode? changes)
    {
        using var document = JsonDocument.Parse(new JsonObject { ["fileChanges"] = changes }.ToJsonString());
        return InvestigationFileChanges.Parse(document.RootElement);
    }

    [Fact]
    public void FileChangesParseWithinTheirLimits()
    {
        var replace = FileChange(); replace["before"] = "  import_today: sensor.old";
        var parsed = ParseFiles(new JsonArray(FileChange(), replace.DeepClone()));
        Assert.Single(parsed); // the same snippet twice is one change
        Assert.Equal(("apps.yaml", "pred_bat", "pending"), (parsed[0].File, parsed[0].Location, parsed[0].Status));
        Assert.Null(parsed[0].Before); Assert.False(string.IsNullOrEmpty(parsed[0].Id));
        Assert.Equal("  import_today: sensor.old", Assert.Single(ParseFiles(new JsonArray(replace))).Before);
        Assert.Empty(ParseFiles(null));
        using var missing = JsonDocument.Parse("{}");
        Assert.Empty(InvestigationFileChanges.Parse(missing.RootElement));
        Assert.Single(ParseFiles(new JsonArray(FileChange("config/apps.yml"))));
    }

    [Theory]
    [InlineData("too-many")]
    [InlineData("not-array")]
    [InlineData("unknown-field")]
    [InlineData("parent-path")]
    [InlineData("absolute-path")]
    [InlineData("executable")]
    [InlineData("long-snippet")]
    [InlineData("empty-summary")]
    [InlineData("long-summary")]
    [InlineData("no-reason")]
    [InlineData("same-before")]
    public void MalformedFileChangesAreRejectedForCorrectiveFinish(string defect)
    {
        var change = FileChange();
        JsonNode? content = null;
        switch (defect)
        {
            case "too-many": content = new JsonArray(FileChange(snippet: "a: 1"), FileChange(snippet: "b: 2"), FileChange(snippet: "c: 3")); break;
            case "not-array": content = change.DeepClone(); break;
            case "unknown-field": change["write"] = true; break;
            case "parent-path": change["file"] = "../secrets.yaml"; break;
            case "absolute-path": change["file"] = "/config/apps.yaml"; break;
            case "executable": change["file"] = "install.sh"; break;
            case "long-snippet": change["snippet"] = new string('x', 1501); break;
            case "empty-summary": change["summary"] = " "; break;
            case "long-summary": change["summary"] = new string('s', 301); break;
            case "no-reason": change.Remove("reason"); break;
            case "same-before": change["before"] = "  export_today:\n    - sensor.export_today "; break;
        }
        content ??= new JsonArray(change);
        var error = Assert.Throws<DomainException>(() => ParseFiles(content));
        Assert.Equal(502, error.Status); Assert.Contains("fileChanges", error.Message);
    }

    [Fact]
    public void FileChangesRetireCarryForwardAndVerify()
    {
        var s = new AppState();
        ConfigFileChange Change(string id, string status, string snippet, DateTimeOffset? decided = null) => new() { Id = id, Status = status, File = "apps.yaml", Summary = id, Location = "pred_bat", Snippet = snippet, Reason = "r", DecidedAt = decided };
        var older = new Investigation { Id = "older", FileChanges = [Change("pending", "pending", "a: 1"), Change("applied", "applied", "b: 2"), Change("kept", "pending", "c: 3"), Change("dismissed", "dismissed", "d: 4", DateTimeOffset.UtcNow.AddDays(-2))] };
        var current = new Investigation { Id = "current", FileChanges = [Change("again", "pending", "a:   1"), Change("declined", "pending", "d: 4"), Change("new", "pending", "e: 5")] };
        s.Investigations.AddRange([older, current]);

        InvestigationFileChanges.Reconcile(s, current, ["kept"]);

        Assert.Equal(["new"], current.FileChanges.Select(x => x.Id));
        Assert.Equal("pending", older.FileChanges[0].Status); // re-raised: carried forward instead of duplicated
        Assert.Equal("verified", older.FileChanges[1].Status); Assert.Matches(@"^Verified by the \d\d:\d\d check$", older.FileChanges[1].ClosedReason); Assert.DoesNotContain("Z", older.FileChanges[1].ClosedReason);
        Assert.Equal("pending", older.FileChanges[2].Status);
        Assert.Equal("dismissed", older.FileChanges[3].Status);
        Assert.Contains(current.Steps, x => x.Contains("already open"));
        Assert.Contains(current.Steps, x => x.Contains("you dismissed the same change"));

        var later = new Investigation { Id = "later" }; s.Investigations.Add(later);
        InvestigationFileChanges.Reconcile(s, later, null); // a reply that says nothing keeps everything open
        Assert.Equal("pending", current.FileChanges[0].Status);
        InvestigationFileChanges.Reconcile(s, later, []);
        Assert.Equal("retired", current.FileChanges[0].Status);
    }

    [Fact]
    public void LegacyRecordsWithoutTheNewFieldsStillLoad()
    {
        var investigation = JsonSerializer.Deserialize<Investigation>("""{"fileChanges":null,"thread":null,"nextSteps":[{"id":"a","thread":null}]}""", JsonDefaults.Options)!;
        Assert.Empty(investigation.FileChanges); Assert.Empty(investigation.Thread); Assert.Empty(investigation.NextSteps[0].Thread);
        Assert.Empty(JsonSerializer.Deserialize<Proposal>("""{"thread":null}""", JsonDefaults.Options)!.Thread);
    }

    // ---- Investigation integration: schema, brief and verification ----

    static string InvestigationFinish(object? fileChanges = null, string[]? keep = null) => JsonSerializer.Serialize(new
    {
        action = "finish", verdict = "problem", title = "Export history is missing", summary = "apps.yaml lacks export_today.",
        evidence = new[] { "No export_today entry." }, evidenceReferences = new[] { "configuration" }, proposals = Array.Empty<object>(),
        fileChanges, keepFollowUps = keep
    });

    [Fact]
    public async Task InvestigationsPublishFileChangesAndTheNextBriefCarriesDeclinesAndAppliedChanges()
    {
        using var db = new DataStore(directory);
        var finishes = new Queue<string>([
            InvestigationFinish(new[] { new { file = "apps.yaml", summary = "Add export_today", location = "pred_bat", snippet = "  export_today:\n    - sensor.export_today", reason = "No export history." } }),
            InvestigationFinish(keep: []),
        ]);
        var provider = new Provider((_, _) => finishes.Dequeue());
        var http = new HttpClient(provider); var config = Config();
        var state = new StateService(db, new NoWriter(), true);
        await state.MutateAsync(s => s.Ai = new AiPreferences { Provider = "Api", Model = "fixture" });
        var analysis = new AnalysisService(state, db, new AiModelClient(http, new ChatGptAuth(http, Path.Combine(directory, "auth")), config), new Factory(http), config);

        await analysis.RunAsync(new("Why is export missing?"));
        var published = state.Read().Investigations.Last();
        var change = Assert.Single(published.FileChanges);
        Assert.Equal(("apps.yaml", "pending"), (change.File, change.Status));
        Assert.Contains("fileChanges", provider.Prompts[0]);

        var proposal = state.Read().Proposals.Single(p => p.Status == "Pending");
        await state.MutateAsync(s =>
        {
            ChangeEngine.Deny(s, proposal.Id, "We cook late.");
            InvestigationFileChanges.MarkApplied(s, published.Id, change.Id);
        });
        await analysis.RunAsync(new());

        var brief = provider.Prompts[1];
        Assert.Contains("Declined by the user", brief);
        Assert.Contains("We cook late.", brief);
        Assert.Contains($"file change id {change.Id}", brief);
        Assert.Contains("says they applied it", brief);
        var verified = state.Read().Investigations.Single(i => i.Id == published.Id).FileChanges.Single();
        Assert.Equal("verified", verified.Status);
        http.Dispose();
    }
}
