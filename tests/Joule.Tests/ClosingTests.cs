using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Joule;
using Xunit;

namespace Joule.Tests;

/// <summary>
/// Closing things: every open item type closes as done, not needed or dismissed (with an optional note); closing an item closes its
/// copies from other checks and, when nothing else is open, its check's findings; a reply that agrees it isn't needed closes it and
/// says so; reopening undoes exactly what closed; and a closed finding isn't raised again for 30 days. The provider is scripted.
/// </summary>
public sealed class ClosingTests : IDisposable
{
    readonly string directory = Path.Combine(Path.GetTempPath(), "joule-closing-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(directory, true); } catch { } }

    sealed class NoWriter : IPredbatClient
    {
        public bool Configured => true;
        public bool WritesEnabled => false;
        public Task<LiveSnapshot> CollectAsync(CancellationToken ct = default) => Task.FromResult(new LiveSnapshot([], null, "{}", "{}"));
        public Task ApplyAsync(List<Change> c, List<Setting> s, CancellationToken ct = default) => throw new InvalidOperationException("No writes.");
    }
    sealed class Factory(HttpClient http) : IHttpClientFactory { public HttpClient CreateClient(string name) => http; }
    sealed class Provider(Func<int, string, string> reply) : HttpMessageHandler
    {
        public List<string> Prompts = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri!.Host == "raw.githubusercontent.com") return new(HttpStatusCode.ServiceUnavailable);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            var messages = body.RootElement.GetProperty("messages");
            var prompt = messages[messages.GetArrayLength() - 1].GetProperty("content").GetString()!;
            Prompts.Add(prompt);
            var text = reply(Prompts.Count - 1, prompt);
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { choices = new[] { new { finish_reason = "stop", message = new { content = text } } }, usage = new { prompt_tokens = 900, completion_tokens = 60 } }), Encoding.UTF8, "application/json") };
        }
    }
    static IConfiguration Config() => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Ai:ApiKey"] = "fixture-secret" }).Build();
    static string Verdict(string verdict, string reply, bool? retire = null) => JsonSerializer.Serialize(new { verdict, reply, memory = (string?)null, retire });

    static InvestigationNextStep Step(string id, string title) => new() { Id = id, Title = title, Rationale = "r", SuggestedAction = "a", Verification = "v", Uncertainty = "u" };
    static Investigation Finding(string id, params InvestigationNextStep[] steps) =>
        new() { Id = id, Title = $"Finding {id}", Summary = "s", Evidence = ["e"], Status = "Completed", Verdict = "problem", NextSteps = [.. steps] };

    // ---- Closing one item ----

    [Fact]
    public void NotNeededClosesTheToDoItsCopiesAndTheFindingWhenNothingElseIsOpen()
    {
        var older = Finding("old", Step("s-old", "Ask the installer about the CT clamp"));
        var newer = Finding("new", Step("s-new", "Ask the installer  about the CT clamp"), Step("s-other", "Restart the inverter"));
        var s = new AppState { DataSource = "Live", Investigations = [older, newer] };

        RecommendationDecisions.DismissFollowUp(s, "new", "s-new", null, RecommendationDecisions.Outcome("not_needed"));

        // The inbox shows the to-do once; closing it must not uncover the older copy.
        Assert.All([older.NextSteps[0], newer.NextSteps[0]], x => { Assert.Equal("closed", x.Status); Assert.Equal("Not needed", x.ClosedReason); Assert.NotNull(x.DecidedAt); });
        // The older check had nothing else open, so it closed as resolved; the newer one still has a to-do.
        Assert.NotNull(older.DismissedAt); Assert.Equal("resolved", older.ClosedReason);
        Assert.Null(newer.DismissedAt);

        RecommendationDecisions.DismissFollowUp(s, "new", "s-other", null, "done");
        Assert.Equal("Done by user", newer.NextSteps[1].ClosedReason);
        Assert.Equal("resolved", newer.ClosedReason);
        Assert.Contains(s.Activities, a => a.Message.Contains("is waiting for you any more"));

        // Reopening brings back the to-do, its copy, and the checks that closed only because of it.
        InsightsDecisions.ReopenFollowUp(s, "new", "s-new");
        Assert.Equal("open", older.NextSteps[0].Status); Assert.Equal("open", newer.NextSteps[0].Status);
        Assert.Null(older.DismissedAt); Assert.Null(older.ClosedReason); Assert.Null(newer.DismissedAt);
        Assert.Equal("closed", newer.NextSteps[1].Status); // closed separately: stays closed
    }

    [Fact]
    public void FileEditsCloseAsNotNeededOrDoneAndCopiesFollow()
    {
        ConfigFileChange Edit(string id) => new() { Id = id, File = "apps.yaml", Summary = "Add export_today", Location = "pred_bat", Snippet = "  export_today: sensor.x", Reason = "r" };
        var a = Finding("a"); a.FileChanges.Add(Edit("f-a"));
        var b = Finding("b"); b.FileChanges.Add(Edit("f-b"));
        var s = new AppState { DataSource = "Live", Investigations = [a, b] };

        RecommendationDecisions.DismissFileChange(s, "b", "f-b", "We export through another meter.", "not_needed");
        Assert.All([a.FileChanges[0], b.FileChanges[0]], x => { Assert.Equal("dismissed", x.Status); Assert.Equal("Not needed", x.ClosedReason); });
        Assert.Equal("resolved", a.ClosedReason); Assert.Equal("resolved", b.ClosedReason);
        InsightsDecisions.ReopenFileChange(s, "b", "f-b");
        Assert.Equal("pending", a.FileChanges[0].Status); Assert.Equal("pending", b.FileChanges[0].Status);
        Assert.Null(a.DismissedAt); Assert.Null(b.DismissedAt);

        // Done for a file edit means "I've made it": it waits for the next check, so its finding stays open.
        RecommendationDecisions.DismissFileChange(s, "b", "f-b", null, "done");
        Assert.Equal("applied", b.FileChanges[0].Status); Assert.Null(b.DismissedAt);
        Assert.Throws<DomainException>(() => RecommendationDecisions.Outcome("maybe"));
    }

    [Fact]
    public void ASuggestionClosedAsNotNeededIsSkippedQuietlyNextTime()
    {
        var s = DemoData.Create();
        var p = s.Proposals.First(x => x.Status == "Pending");
        RecommendationDecisions.DeclineProposal(s, p.Id, null, "not_needed");
        Assert.Equal(("Denied", "Not needed"), (p.Status, p.ClosedReason));
        Assert.Null(p.DecisionNote);
        InsightsDecisions.ReopenProposal(s, p.Id);
        Assert.Equal("Pending", p.Status); Assert.Null(p.ClosedReason);
    }

    // ---- Closing a whole finding ----

    [Fact]
    public void DismissingAFindingClosesEverythingFromItAndReopeningUndoesExactlyThat()
    {
        var s = DemoData.Create();
        var finding = Finding("f1", Step("s1", "Check backup mode"), Step("s2", "Restart the inverter"));
        finding.FileChanges.Add(new ConfigFileChange { Id = "c1", File = "apps.yaml", Summary = "Add export_today", Location = "pred_bat", Snippet = "  export_today: x", Reason = "r" });
        s.Investigations.Add(finding);
        var proposal = s.Proposals.First(x => x.Status == "Pending"); proposal.InvestigationId = "f1";
        // Closed on its own earlier: an undo of the finding must leave it closed.
        RecommendationDecisions.DismissFollowUp(s, "f1", "s2", "Done already", "done");

        RecommendationDecisions.DismissFinding(s, "f1", "The inverter has no backup mode.");
        Assert.Equal(("dismissed", "The inverter has no backup mode."), (finding.ClosedReason, finding.DecisionNote));
        Assert.Equal(RecommendationDecisions.WithFindings, finding.NextSteps[0].ClosedReason);
        Assert.Equal("retired", finding.FileChanges[0].Status);
        Assert.Equal(("Denied", RecommendationDecisions.WithFindings), (proposal.Status, proposal.ClosedReason));
        Assert.Throws<DomainException>(() => RecommendationDecisions.DismissFinding(s, "f1", null));

        InsightsDecisions.ReopenFinding(s, "f1");
        Assert.Null(finding.DismissedAt); Assert.Null(finding.ClosedReason);
        Assert.Equal("open", finding.NextSteps[0].Status);
        Assert.Equal("closed", finding.NextSteps[1].Status);
        Assert.Equal("pending", finding.FileChanges[0].Status);
        Assert.Equal("Pending", proposal.Status); Assert.Null(proposal.ClosedReason);
    }

    // ---- The conversation ----

    async Task<(StateService State, RecommendationReplyService Replies, HttpClient Http)> ReplyServices(DataStore db, HttpMessageHandler handler, string provider = "Api")
    {
        var http = new HttpClient(handler); var config = Config();
        var state = new StateService(db, new NoWriter(), true);
        await state.MutateAsync(s => s.Ai = new AiPreferences { Provider = provider, Model = provider == "Demo" ? "" : "fixture" });
        return (state, new RecommendationReplyService(state, db, new AiModelClient(http, new ChatGptAuth(http, Path.Combine(directory, "auth")), config) { RetryDelays = [] }, config) { RetryDelays = [] }, http);
    }

    [Fact]
    public async Task NahAndTheAiAgreesClosesTheItemAndItsFindingAndTheReplySaysSo()
    {
        using var db = new DataStore(directory);
        // The model agrees but (wrongly) leaves it open: "nah" from the user settles it.
        var provider = new Provider((_, _) => Verdict("accept", "Agreed: at about 2p a month it isn't worth the effort.", retire: false));
        var services = await ReplyServices(db, provider);
        await services.State.MutateAsync(s => s.Investigations.Add(Finding("f1", Step("s1", "Move the CT clamp"))));

        var outcome = await services.Replies.ReplyAsync(new("followup", "f1", "s1"), "Nah, not worth it then", CancellationToken.None);

        Assert.True(outcome.Retired); Assert.True(outcome.FindingClosed);
        Assert.EndsWith("Closed — I won't raise this again for 30 days. Nothing else from that check is waiting for you, so the check is closed too.", outcome.Reply);
        var stored = services.State.Read().Investigations.Single(i => i.Id == "f1");
        Assert.Equal(("closed", "Not needed"), (stored.NextSteps[0].Status, stored.NextSteps[0].ClosedReason));
        Assert.Equal("Nah, not worth it then", stored.NextSteps[0].DecisionNote);
        Assert.Equal("resolved", stored.ClosedReason);
        Assert.Equal(outcome.Reply, stored.NextSteps[0].Thread[^1].Text);
        Assert.Contains("use accept with retire true", provider.Prompts[0]);
        services.Http.Dispose();
    }

    [Fact]
    public async Task AnAgreementThatKeepsTheItemSaysItStaysAndAQuestionStillGetsAnAnswer()
    {
        using var db = new DataStore(directory);
        var provider = new Provider((turn, _) => turn == 0 ? Verdict("accept", "Fine, do it at the weekend.", retire: false) : Verdict("accept", "It saves about 2p a month."));
        var services = await ReplyServices(db, provider);
        await services.State.MutateAsync(s => s.Investigations.Add(Finding("f1", Step("s1", "Move the CT clamp"))));

        var later = await services.Replies.ReplyAsync(new("followup", "f1", "s1"), "I'll do it at the weekend", CancellationToken.None);
        Assert.False(later.Retired); Assert.EndsWith("It stays on your list.", later.Reply);
        var question = await services.Replies.ReplyAsync(new("followup", "f1", "s1"), "Is it worth doing at all?", CancellationToken.None);
        Assert.Equal("answer", question.Verdict); Assert.False(question.Retired);
        Assert.Equal("open", services.State.Read().Investigations.Single(i => i.Id == "f1").NextSteps[0].Status);
        services.Http.Dispose();
    }

    [Fact]
    public async Task ReplyingNotNeededOnTheFindingClosesItAsNotNeeded()
    {
        using var db = new DataStore(directory);
        var services = await ReplyServices(db, new Provider((_, _) => Verdict("accept", "Agreed.")));
        await services.State.MutateAsync(s => s.Investigations.Add(Finding("f1", Step("s1", "Move the CT clamp"))));
        var outcome = await services.Replies.ReplyAsync(new("finding", "f1"), "Leave it, it's fine", CancellationToken.None);
        Assert.True(outcome.Retired); Assert.False(outcome.FindingClosed);
        Assert.EndsWith(RecommendationDecisions.ClosedLine, outcome.Reply);
        var stored = services.State.Read().Investigations.Single(i => i.Id == "f1");
        Assert.Equal("not_needed", stored.ClosedReason);
        Assert.Equal(RecommendationDecisions.WithFindings, stored.NextSteps[0].ClosedReason);
        services.Http.Dispose();
    }

    [Theory]
    [InlineData("nah", true)]
    [InlineData("Not needed, thanks", true)]
    [InlineData("leave it", true)]
    [InlineData("We'll skip it", true)]
    [InlineData("Is it not needed?", false)]
    [InlineData("Why is this needed", false)]
    [InlineData("I'll do it tomorrow", false)]
    public void RecognisesNotNeeded(string note, bool expected) => Assert.Equal(expected, RecommendationReplyService.SaysNotNeeded(note));

    [Fact]
    public async Task TheScriptedDemoClosesOnNah()
    {
        using var db = new DataStore(directory);
        var services = await ReplyServices(db, new Provider((_, _) => throw new InvalidOperationException("The demo must not call a provider.")), "Demo");
        var demo = services.State.Read().Investigations.First(i => i.NextSteps.Any(x => x.Status == "open"));
        var step = demo.NextSteps.First(x => x.Status == "open");
        var outcome = await services.Replies.ReplyAsync(new("followup", demo.Id, step.Id), "nah, leave it", CancellationToken.None);
        Assert.True(outcome.Retired);
        Assert.Contains(RecommendationDecisions.ClosedLine, outcome.Reply);
        services.Http.Dispose();
    }

    // ---- Not raised again ----

    async Task<(StateService State, AnalysisService Analysis)> AnalysisServices(DataStore db, HttpMessageHandler handler)
    {
        var http = new HttpClient(handler); var config = Config();
        var state = new StateService(db, new NoWriter(), true);
        var model = new AiModelClient(http, new ChatGptAuth(http, Path.Combine(directory, "auth")), config) { RetryDelays = [] };
        var analysis = new AnalysisService(state, db, model, new Factory(http), config);
        await state.MutateAsync(s => { s.Proposals.Clear(); s.Investigations.Clear(); s.Usage.Clear(); s.Ai = new AiPreferences { Provider = "Api", Model = "fixture", MaxRunsPerDay = 50 }; });
        return (state, analysis);
    }
    static string Finish(string title, string summary, object? extra = null)
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(new { action = "finish", verdict = "problem", title, summary, evidence = new[] { "Predbat logged it 14 times between 02:00 and 06:00." }, evidenceReferences = new[] { "configuration" } }))!.AsObject();
        if (extra != null) foreach (var (k, v) in System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(extra))!.AsObject().ToList()) node[k] = v?.DeepClone();
        return node.ToJsonString();
    }

    [Fact]
    public async Task AFindingYouClosedIsNotRaisedAgainFor30Days()
    {
        using var db = new DataStore(directory);
        var provider = new Provider((_, _) => Finish("Export meter reads unknown after midnight (sensor.grid_export_today)", "sensor.grid_export_today reads unknown until the first export.",
            new { nextSteps = new[] { new { title = "Ask about the export meter", rationale = "r", suggestedAction = "a", verification = "v", uncertainty = "u", evidenceReferences = new[] { "configuration" } } } }));
        var s = await AnalysisServices(db, provider);
        await s.Analysis.RunAsync(new());
        var first = s.State.Read().Investigations.Single();
        await s.State.MutateAsync(x => RecommendationDecisions.DismissFinding(x, first.Id, "The export meter is fine.", "dismissed"));

        await s.Analysis.RunAsync(new());
        var again = s.State.Read().Investigations.OrderBy(i => i.At).Last();
        Assert.Equal(first.Id, again.RepeatOf);
        Assert.NotNull(again.DismissedAt); Assert.Equal("repeat", again.ClosedReason);
        Assert.Empty(again.NextSteps);
        Assert.Contains(again.Steps, x => x.Contains("you closed the same finding"));
        // The brief tells the next check what the user decided, in plain words.
        Assert.Contains("the user disputed this finding: The export meter is fine.", provider.Prompts[^1]);
    }

    [Fact]
    public async Task AFindingThatResolvedItselfMayBeRaisedAgain()
    {
        using var db = new DataStore(directory);
        var provider = new Provider((_, _) => Finish("Export meter reads unknown after midnight (sensor.grid_export_today)", "sensor.grid_export_today reads unknown until the first export.",
            new { nextSteps = new[] { new { title = "Ask about the export meter", rationale = "r", suggestedAction = "a", verification = "v", uncertainty = "u", evidenceReferences = new[] { "configuration" } } } }));
        var s = await AnalysisServices(db, provider);
        await s.Analysis.RunAsync(new());
        var first = s.State.Read().Investigations.Single();
        await s.State.MutateAsync(x => RecommendationDecisions.DismissFollowUp(x, first.Id, first.NextSteps[0].Id, null, "done"));
        Assert.Equal("resolved", s.State.Read().Investigations.Single().ClosedReason);

        await s.Analysis.RunAsync(new());
        var again = s.State.Read().Investigations.OrderBy(i => i.At).Last();
        Assert.Null(again.DismissedAt); Assert.Null(again.RepeatOf);
    }
}
