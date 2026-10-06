using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Joule;
using Xunit;

namespace Joule.Tests;

public sealed class DeepAiAuditRegressionTests : IDisposable
{
    readonly string directory = Path.Combine(Path.GetTempPath(), "predbat-deep-ai-" + Guid.NewGuid().ToString("N"));
    static IConfiguration Config() => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Ai:ApiKey"] = "fixture-secret" }).Build();
    sealed class NoWriter : IPredbatClient
    {
        public bool Configured => true;
        public bool WritesEnabled => false;
        public Task<LiveSnapshot> CollectAsync(CancellationToken ct = default) => throw new InvalidOperationException();
        public Task ApplyAsync(List<Change> changes, List<Setting> settings, CancellationToken ct = default) => throw new InvalidOperationException("No writes expected.");
    }
    sealed class Factory(HttpClient http) : IHttpClientFactory { public HttpClient CreateClient(string name) => http; }
    sealed class Provider(string? firstAction = null, bool secrets = false, bool starveDocs = false, bool failAfterRead = false, bool longPreviousDocLine = false) : HttpMessageHandler
    {
        int calls;
        public List<string> Prompts = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri!.Host == "raw.githubusercontent.com")
            {
                var text = starveDocs && request.RequestUri.AbsolutePath.EndsWith("customisation.md")
                    ? string.Join("\n", Enumerable.Range(10, 8).Select(i => $"load_scaling{i} is a different setting.\n" + new string('\n', 12)))
                    : (longPreviousDocLine ? new string('x', 2300) : "# Settings") + "\ninput_number.predbat_load_scaling adjusts historical load forecasts.\n";
                return new(HttpStatusCode.OK) { Content = new StringContent(text) };
            }
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            var prompt = body.RootElement.GetProperty("messages")[0].GetProperty("content").GetString()!;
            Prompts.Add(prompt);
            string reply;
            if (calls++ == 0) reply = firstAction ?? "{\"action\":\"documentation\",\"query\":\"load_scaling\"}";
            else
            {
                if (failAfterRead) return new(HttpStatusCode.TooManyRequests);
                var narrative = secrets ? "Useful observation fixture-secret api_key=model-supplied-secret" : "An honest finding with available evidence.";
                var proposal = new { title = narrative, summary = narrative, expectedEffect = narrative, tradeoff = narrative, evidence = new[] { narrative }, evidenceReferences = new[] { "configuration" }, documentationReferences = new[] { new { settingKey = "load_scaling", referenceId = Regex.Match(prompt, "doc-[a-f0-9]{24}").Value } }, changes = new[] { new { key = "load_scaling", after = "1.00" } } };
                reply = JsonSerializer.Serialize(new { action = "finish", title = narrative, summary = narrative, category = secrets ? narrative : "Review", evidence = new[] { narrative }, evidenceReferences = new[] { "configuration" }, proposals = secrets ? new[] { proposal } : [] });
            }
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { choices = new[] { new { finish_reason = "stop", message = new { content = reply } } }, usage = new { prompt_tokens = 100, completion_tokens = 20 } })) };
        }
    }
    async Task<(StateService State, AnalysisService Analysis)> Services(DataStore db, HttpClient http)
    {
        var state = new StateService(db, new NoWriter(), true, configuration: Config());
        await state.MutateAsync(s => { s.Proposals.Clear(); s.Ai = new() { Provider = "Api", Model = "fixture" }; });
        var analysis = new AnalysisService(state, db, new AiModelClient(http, new ChatGptAuth(http, Path.Combine(directory, "auth")), Config()), new Factory(http), Config());
        return (state, analysis);
    }
    [Fact]
    public async Task ValidFinishNarrativesAreRedactedBeforePersistenceAndFuturePrompts()
    {
        using var db = new DataStore(directory); using var provider = new Provider(secrets: true); using var http = new HttpClient(provider);
        var services = await Services(db, http);
        await services.Analysis.RunAsync(new());
        var proposal = Assert.Single(services.State.Read(false).Proposals);
        Assert.Equal("Completed", services.State.Read(false).Investigations.Last().Status);
        var saved = JsonSerializer.Serialize(db.Load(), JsonDefaults.Options);
        Assert.DoesNotContain("fixture-secret", saved); Assert.DoesNotContain("model-supplied-secret", saved);
        Assert.Contains("[redacted]", proposal.Summary);
        Assert.Equal("1.00", Assert.Single(proposal.Changes).After);
        await services.State.MutateAsync(s => ChangeEngine.Deny(s, proposal.Id));
        await services.Analysis.RunAsync(new());
        Assert.DoesNotContain("fixture-secret", provider.Prompts.Last()); Assert.DoesNotContain("model-supplied-secret", provider.Prompts.Last());
    }
    [Fact]
    public async Task InvestigationQuestionCredentialsAreRedactedWithoutChangingCallerQuestion()
    {
        using var db = new DataStore(directory); using var provider = new Provider(); using var http = new HttpClient(provider);
        var services = await Services(db, http);
        const string question = "Investigate fixture-secret, api_key=model-supplied-secret, and EV charging.";
        var request = new AnalysisRequest(question);
        await services.Analysis.RunAsync(request);
        Assert.Equal(question, request.Question);
        Assert.DoesNotContain("fixture-secret", provider.Prompts[0]); Assert.DoesNotContain("model-supplied-secret", provider.Prompts[0]);
        var saved = JsonSerializer.Serialize(db.Load(), JsonDefaults.Options);
        Assert.DoesNotContain("fixture-secret", saved); Assert.DoesNotContain("model-supplied-secret", saved);
        var result = services.State.ReadInvestigation(services.State.Read(false).Investigations.Last().Id)!;
        Assert.Contains("and EV charging.", result.Request.Question);
        await services.State.MutateAsync(s => s.Investigations.Last().Request = request);
        var legacy = services.State.ReadInvestigation(result.Id)!;
        Assert.DoesNotContain("fixture-secret", legacy.Request.Question); Assert.DoesNotContain("model-supplied-secret", legacy.Request.Question);
        Assert.Equal(question, request.Question);
    }
    [Fact]
    public async Task LegacyProposalNarrativesAreRedactedInReadsAndOutgoingContext()
    {
        using var db = new DataStore(directory); using var provider = new Provider(firstAction: "{\"action\":\"query\",\"sql\":\"SELECT 1 AS count\"}"); using var http = new HttpClient(provider);
        var services = await Services(db, http);
        await services.State.MutateAsync(s => s.Proposals.Add(new Proposal { Status = "Denied", Title = "fixture-secret", Summary = "api_key=model-supplied-secret", ExpectedEffect = "fixture-secret", Tradeoff = "fixture-secret", Evidence = ["fixture-secret"] }));
        var returned = JsonSerializer.Serialize(services.State.Read(false).Proposals, JsonDefaults.Options);
        Assert.DoesNotContain("fixture-secret", returned); Assert.DoesNotContain("model-supplied-secret", returned);
        await services.Analysis.RunAsync(new());
        Assert.DoesNotContain("fixture-secret", provider.Prompts[0]);
    }
    [Fact]
    public async Task LegacyApprovalNarrativesAreRedactedInRevisionExperimentReadsAndFuturePrompts()
    {
        using var db = new DataStore(directory); using var provider = new Provider(firstAction: "{\"action\":\"query\",\"sql\":\"SELECT 1 AS count\"}"); using var http = new HttpClient(provider);
        var services = await Services(db, http);
        string id = "";
        await services.State.MutateAsync(s =>
        {
            var proposal = new Proposal { Title = "fixture-secret", ExpectedEffect = "api_key=model-supplied-secret", BaseRevision = s.Revision, Changes = [new("load_scaling", "1.08", "1.00")] };
            s.Proposals.Add(proposal); id = proposal.Id;
        });
        await services.State.MutateAsync(s => ChangeEngine.Approve(s, id, false));
        var returned = services.State.Read(false);
        var narrative = JsonSerializer.Serialize(new { returned.Revisions, returned.Experiments }, JsonDefaults.Options);
        Assert.DoesNotContain("fixture-secret", narrative); Assert.DoesNotContain("model-supplied-secret", narrative);
        Assert.Equal("1.00", returned.Settings.Single(s => s.Key == "load_scaling").Value);
        await services.Analysis.RunAsync(new());
        Assert.DoesNotContain("fixture-secret", provider.Prompts[0]); Assert.DoesNotContain("model-supplied-secret", provider.Prompts[0]);
    }
    sealed class StalledBody : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) { await Task.Delay(Timeout.Infinite, ct); return 0; }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
    sealed class StalledDocs : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new StalledBody()) });
    }
    [Fact]
    public async Task DocumentationBodyDeadlineReturnsGapsBeforeCallerCancellation()
    {
        using var db = new DataStore(directory); using var handler = new StalledDocs(); using var http = new HttpClient(handler) { Timeout = TimeSpan.FromMilliseconds(40) };
        // Eight 40 ms body deadlines take well under a second; the caller's 10 s leaves room for a loaded parallel test run while still
        // failing if a stalled body waited for the caller instead of its own deadline.
        using var caller = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var result = await new DocumentationService(db, new Factory(http), Config()).SearchAsync("load_scaling", caller.Token);
        Assert.False(caller.IsCancellationRequested); Assert.Empty(result.References);
        Assert.Equal(DocumentationService.PrimaryFiles.Length + 1, result.Gaps.Count);
    }
    [Theory]
    [InlineData(false, "key")]
    [InlineData(true, "key")]
    [InlineData(false, "KEY")]
    public async Task SqlStringCredentialsAreRedactedBeforePromptsAndDurableCompletedOrFailedEvidence(bool fail, string keyName)
    {
        const string action = "{\"action\":\"query\",\"sql\":\"SELECT 'fixture-secret' AS known, 'api_key=model-supplied-secret' AS detail, 'load_scaling' AS key, 1.08 AS value\"}";
        using var db = new DataStore(directory); using var provider = new Provider(firstAction: action.Replace(" AS key", " AS " + keyName), failAfterRead: fail); using var http = new HttpClient(provider);
        var services = await Services(db, http);
        await services.Analysis.RunAsync(new());
        var saved = JsonSerializer.Serialize(db.Load(), JsonDefaults.Options);
        Assert.DoesNotContain("fixture-secret", saved); Assert.DoesNotContain("model-supplied-secret", saved);
        Assert.DoesNotContain("fixture-secret", provider.Prompts[1]); Assert.DoesNotContain("model-supplied-secret", provider.Prompts[1]);
        var investigation = services.State.ReadInvestigation(services.State.Read(false).Investigations.Last().Id)!;
        Assert.Equal(fail ? "Failed" : "Completed", investigation.Status);
        using var result = JsonDocument.Parse(Assert.Single(investigation.ToolEvidence, e => e.Kind == "query").ResultJson);
        var row = result.RootElement.GetProperty("rows")[0];
        Assert.Equal("load_scaling", row.GetProperty(keyName).GetString()); Assert.Equal(1.08, row.GetProperty("value").GetDouble());
    }
    [Theory]
    [InlineData("documentation", "{\"action\":\"documentation\"}")]
    [InlineData("documentation", "{\"action\":\"documentation\",\"query\":42}")]
    [InlineData("documentation", "{\"action\":\"documentation\",\"query\":\"x\"}")]
    [InlineData("summary", "{\"action\":\"summary\",\"from\":42,\"to\":\"2026-01-02T00:00:00Z\"}")]
    [InlineData("summary", "{\"action\":\"summary\",\"from\":\"not-a-date\",\"to\":\"2026-01-02T00:00:00Z\"}")]
    [InlineData("summary", "{\"action\":\"summary\",\"from\":\"2026-01-02T00:00:00Z\",\"to\":\"2026-01-01T00:00:00Z\"}")]
    [InlineData("query", "{\"action\":\"query\",\"sql\":42}")]
    public async Task MalformedReadArgumentsBecomeInspectableGapsAndInvestigationCanFinish(string kind, string action)
    {
        using var db = new DataStore(directory); using var provider = new Provider(firstAction: action); using var http = new HttpClient(provider);
        var services = await Services(db, http);
        await services.Analysis.RunAsync(new());
        var investigation = services.State.ReadInvestigation(services.State.Read(false).Investigations.Last().Id)!;
        Assert.Equal("Completed", investigation.Status); Assert.False(services.Analysis.Running);
        var gap = Assert.Single(investigation.ToolEvidence, e => e.Kind == kind);
        Assert.False(gap.Success); Assert.False(string.IsNullOrWhiteSpace(gap.Error)); Assert.Null(services.State.Read(false).AnalysisError);
    }
    [Fact]
    public async Task ExactSettingDocumentationIsNotStarvedByEarlierLongerSettingNames()
    {
        using var db = new DataStore(directory); using var provider = new Provider(starveDocs: true); using var http = new HttpClient(provider);
        var docs = new DocumentationService(db, new Factory(http), Config());
        var result = await docs.SearchAsync("load_scaling");
        Assert.Contains(result.References, reference => DocumentationService.CoversSetting(reference, "load_scaling"));
    }
    [Fact]
    public async Task DocumentationReferenceLineRangeMatchesExcerptAfterLongPreviousLine()
    {
        using var db = new DataStore(directory); using var provider = new Provider(longPreviousDocLine: true); using var http = new HttpClient(provider);
        var result = await new DocumentationService(db, new Factory(http), Config()).SearchAsync("load_scaling");
        var reference = Assert.Single(result.References, r => r.Path == "docs/customisation.md");
        Assert.Equal(2, reference.StartLine); Assert.Equal(3, reference.EndLine);
        Assert.Contains("input_number.predbat_load_scaling", reference.Excerpt);
    }
    [Fact]
    public async Task DocumentationExcerptIncludesTheDeclaredPrecedingSourceLine()
    {
        using var db = new DataStore(directory); using var provider = new Provider(); using var http = new HttpClient(provider);
        var result = await new DocumentationService(db, new Factory(http), Config()).SearchAsync("load_scaling");
        var reference = Assert.Single(result.References, r => r.Path == "docs/customisation.md");
        Assert.Equal(1, reference.StartLine); Assert.Equal(3, reference.EndLine);
        Assert.StartsWith("# Settings\ninput_number.predbat_load_scaling", reference.Excerpt.Replace("\r\n", "\n"));
    }
    [Fact]
    public async Task SnapshotReadFailureReleasesInvestigationOwnership()
    {
        using var db = new DataStore(directory); using var provider = new Provider(); using var http = new HttpClient(provider);
        var services = await Services(db, http);
        await services.State.MutateAsync(s => s.Investigations[0].Steps = null!);
        await Assert.ThrowsAsync<ArgumentNullException>(() => services.Analysis.RunAsync(new()));
        Assert.False(services.Analysis.Running);
    }
    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
}
