using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Joule;
using Xunit;

namespace Joule.Tests;

// These scripted external boundaries verify evidence access and budgets, not LLM diagnosis quality.
public sealed class InvestigationContextTests : IDisposable
{
    readonly string directory = Path.Combine(Path.GetTempPath(), "predbat-context-" + Guid.NewGuid().ToString("N"));
    sealed class NoWriter : IPredbatClient
    {
        public int Writes;
        public bool Configured => true;
        public bool WritesEnabled => false;
        public Task<LiveSnapshot> CollectAsync(CancellationToken ct = default) => Task.FromResult(new LiveSnapshot([], null, "{}", "{}"));
        public Task ApplyAsync(List<Change> c, List<Setting> s, CancellationToken ct = default)
        { Writes++; throw new InvalidOperationException("No writes in an investigation."); }
    }
    sealed class Factory(HttpClient http) : IHttpClientFactory { public HttpClient CreateClient(string name) => http; }
    sealed class Mcp : IPredbatMcpClient
    {
        public int Calls;
        public bool Configured => true;
        public List<McpToolDefinition> Tools = [
            new("get_plan", "Read plan", JsonSerializer.SerializeToElement(new { type = "object", description = new string('p', 14000), properties = new { } })),
            new("get_state", "Read states", JsonSerializer.SerializeToElement(new { type = "object", description = new string('s', 14000), properties = new { } })),
            new("get_log", "Read operational logs", JsonSerializer.SerializeToElement(new { type = "object", properties = new { filter = new { type = "string", @enum = new[] { "all", "warnings" } } } })),
            new("get_apps", "Masked configuration and service hooks", JsonSerializer.SerializeToElement(new { type = "object", properties = new { masked = new { type = "boolean", @const = true }, filter = new { type = "string" } } }))
        ];
        public McpDiscovery Status => new(true, true, DateTimeOffset.UtcNow, Tools, null);
        public Task<McpDiscovery> DiscoverAsync(CancellationToken ct = default) => Task.FromResult(Status);
        public Task<McpReadResult> CallReadOnlyAsync(string name, JsonElement arguments, CancellationToken ct = default)
        {
            Calls++;
            // Mimic real MCP's JSON inside a text content block; the useful late line is well beyond old prompt clipping.
            var log = JsonSerializer.Serialize(new { lines = Enumerable.Range(0, 180).Select(i => new { line_number = i, text = i == 170 ? "LATE_SERVICE_HOOK mode changed after the charge command" : $"INFO line {i}: " + new string('x', 100) }).ToArray() });
            return Task.FromResult(new McpReadResult(true, JsonSerializer.Serialize(new { content = new[] { new { type = "text", text = log } } }), false, null));
        }
    }
    sealed class Provider(Func<int, string, string> reply) : HttpMessageHandler
    {
        public List<string> Prompts = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            var prompt = body.RootElement.GetProperty("messages")[0].GetProperty("content").GetString()!;
            Prompts.Add(prompt);
            var response = reply(Prompts.Count - 1, prompt);
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { choices = new[] { new { finish_reason = "stop", message = new { content = response } } }, usage = new { prompt_tokens = prompt.Length / 4, completion_tokens = 30 } }), Encoding.UTF8, "application/json") };
        }
    }
    sealed class PausingProvider : HttpMessageHandler
    {
        public readonly TaskCompletionSource Paused = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Cancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var turn = Interlocked.Increment(ref Calls);
            if (turn == 2)
            {
                Paused.TrySetResult();
                try { await Task.Delay(Timeout.Infinite, ct); }
                finally { Cancelled.TrySetResult(); }
            }
            var reply = turn == 1 ? """{"action":"mcp","tool":"get_log","arguments":{"filter":"all"}}""" : Finish();
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new
            {
                choices = new[] { new { finish_reason = "stop", message = new { content = reply } } },
                usage = new { prompt_tokens = 120, completion_tokens = 30 }
            }), Encoding.UTF8, "application/json") };
        }
    }
    static string Finish(int count = 1) => JsonSerializer.Serialize(new { action = "finish", verdict = "problem", title = "Operational investigation", summary = "Fixture findings with explicit uncertainty.", evidence = Enumerable.Range(0, count).Select(i => "Evidence " + i).ToArray(), evidenceReferences = new[] { "configuration" }, proposals = Array.Empty<object>() });
    async Task<(StateService State, AnalysisService Analysis, NoWriter Writer)> Services(DataStore db, HttpClient http, Mcp mcp)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Ai:ApiKey"] = "fixture-provider-secret" }).Build();
        var writer = new NoWriter(); var state = new StateService(db, writer, true);
        await state.MutateAsync(s => { s.Proposals.Clear(); s.Ai = new AiPreferences { Provider = "Api", Model = "fixture" }; });
        var factory = new Factory(http);
        return (state, new AnalysisService(state, db, new AiModelClient(http, new ChatGptAuth(http, Path.Combine(directory, "auth")), config), factory, config, mcp: mcp), writer);
    }
    [Fact]
    public async Task ProductiveInvestigationExceedsTwelveResponsesAndKeepsEarlierEvidenceReachable()
    {
        using var db = new DataStore(directory); var mcp = new Mcp(); var finishes = 0;
        using var provider = new Provider((turn, prompt) => turn < 18
            ? JsonSerializer.Serialize(new { action = "mcp", tool = "get_log", arguments = new { filter = "all", search = "phase-" + turn } })
            : turn == 18 ? """{"action":"evidence","inventory":true,"offset":0,"limit":5}""" : Finish(finishes++ == 0 ? 7 : 6));
        using var http = new HttpClient(provider); var services = await Services(db, http, mcp);
        await services.Analysis.RunAsync(new("Why did the overnight battery charge fail?"));
        var investigation = services.State.Read().Investigations.Last();
        Assert.Equal("Completed", investigation.Status); Assert.Equal(18, mcp.Calls); Assert.Equal(21, provider.Prompts.Count);
        Assert.Equal(2, finishes); Assert.All(provider.Prompts, p => Assert.InRange(p.Length, 1, InvestigationTranscript.PromptLimit));
        Assert.True(provider.Prompts.Sum(p => (long)p.Length) > 350000);
        var oldId = investigation.ToolEvidence.First(t => t.Kind == "mcp").Id;
        Assert.Contains(oldId, provider.Prompts[18]); // The transcript keeps every evidence ID visible for the whole investigation.
        Assert.Contains(oldId, provider.Prompts[19]);
        Assert.Contains(investigation.ToolEvidence.Last().Id, provider.Prompts.Last());
        Assert.Contains("Why did the overnight battery charge fail?", provider.Prompts.Last());
        Assert.Contains("configuration", provider.Prompts.Last());
    }
    [Fact]
    public async Task FullCatalogRemainsDiscoverableAndSelectedSchemaIsRetrievable()
    {
        using var db = new DataStore(directory); var mcp = new Mcp();
        using var provider = new Provider((turn, _) => turn == 0 ? """{"action":"schema","tool":"get_apps"}""" : Finish());
        using var http = new HttpClient(provider); var services = await Services(db, http, mcp);
        await services.Analysis.RunAsync(new());
        var evidence = Assert.Single(services.State.Read().Investigations.Last().ToolEvidence, t => t.Kind == "schema");
        Assert.True(evidence.Success); Assert.Contains("masked", evidence.ResultJson); Assert.Contains("const", evidence.ResultJson);
        Assert.All(mcp.Tools, tool => Assert.Contains(tool.Name, provider.Prompts[0])); Assert.Equal(0, mcp.Calls);
    }
    [Fact]
    public async Task ArchivedLogSearchReachesLateDecodedContentAndDeclaresContinuation()
    {
        using var db = new DataStore(directory); var mcp = new Mcp(); string? sourceId = null;
        using var provider = new Provider((turn, prompt) =>
        {
            if (turn == 0) return """{"action":"mcp","tool":"get_log","arguments":{"filter":"all"}}""";
            if (turn == 1)
            {
                sourceId = Regex.Matches(prompt, "tool-[a-f0-9]{32}").First().Value;
                return JsonSerializer.Serialize(new { action = "evidence", id = sourceId, search = "LATE_SERVICE_HOOK", offset = 0, limit = 100 });
            }
            return Finish();
        });
        using var http = new HttpClient(provider); var services = await Services(db, http, mcp);
        await services.Analysis.RunAsync(new());
        var investigation = services.State.Read().Investigations.Last();
        var page = Assert.Single(investigation.ToolEvidence, t => t.Kind == "evidence"); Assert.True(page.Success);
        using var result = JsonDocument.Parse(page.ResultJson);
        Assert.Equal(sourceId, result.RootElement.GetProperty("sourceEvidenceId").GetString());
        Assert.True(result.RootElement.GetProperty("offset").GetInt32() > 6000);
        Assert.True(result.RootElement.GetProperty("nextOffset").GetInt32() > result.RootElement.GetProperty("offset").GetInt32());
        Assert.Contains("LATE_SERVICE_HOOK mode changed after the charge command", provider.Prompts[2]);
        Assert.DoesNotContain("\\\"line_number\\\"", provider.Prompts[2]); Assert.Equal(1, mcp.Calls);
    }
    [Fact]
    public async Task EvidenceLookupCannotReadAnotherInvestigationsArchive()
    {
        using var db = new DataStore(directory); var mcp = new Mcp(); var oldId = "tool-" + new string('a', 32);
        using var provider = new Provider((turn, _) => turn == 0 ? JsonSerializer.Serialize(new { action = "evidence", id = oldId }) : Finish());
        using var http = new HttpClient(provider); var services = await Services(db, http, mcp);
        await services.State.MutateAsync(s => s.Investigations.Add(new Investigation { Title = "Prior", Summary = "Prior", ToolEvidence = [new(oldId, "mcp", "Earlier log", DateTimeOffset.UtcNow, true, "{\"secretOfEarlierRun\":\"PRIVATE_OLDER_EVIDENCE\"}", [])] }));
        await services.Analysis.RunAsync(new());
        var page = Assert.Single(services.State.Read().Investigations.Last().ToolEvidence, t => t.Kind == "evidence");
        Assert.False(page.Success); Assert.Contains("this investigation", page.Error!);
        Assert.All(provider.Prompts, p => Assert.DoesNotContain("PRIVATE_OLDER_EVIDENCE", p));
    }
    [Fact]
    public async Task PromptDirectsIncidentEvidenceAndAvailablePeriodWithoutRepeatingOldSummaries()
    {
        using var db = new DataStore(directory); var mcp = new Mcp();
        using var provider = new Provider((_, _) => Finish()); using var http = new HttpClient(provider); var services = await Services(db, http, mcp);
        await services.State.MutateAsync(s => s.Investigations.Add(new Investigation { Title = "Prior review", Summary = "OLD_SEVEN_DAY_COVERAGE_REFRAIN", Evidence = ["Unchanged old caveat"] }));
        await services.Analysis.RunAsync(new());
        var prompt = Assert.Single(provider.Prompts);
        Assert.Contains("filter\":\"all", prompt); Assert.Contains("Calling service", prompt); Assert.Contains("plan_vs_actual", prompt);
        Assert.Contains("status='observed'", prompt); Assert.Contains("off is normal between runs", prompt);
        Assert.Contains("report only new or changed findings", prompt); Assert.Contains("snapshots", prompt);
        // Previous findings are shown as known context, clearly labelled, so the model builds on them instead of rediscovering them.
        Assert.Contains("known findings, not fresh evidence", prompt); Assert.Contains("OLD_SEVEN_DAY_COVERAGE_REFRAIN", prompt);
        Assert.Contains("Sensor health", prompt); Assert.Contains("Current Predbat plan", prompt); Assert.Contains("Plan versus actual", prompt);
    }
    [Fact]
    public async Task ThreeIdenticalReadsRequireASpecificBlockerFinishRatherThanLooping()
    {
        using var db = new DataStore(directory); var mcp = new Mcp();
        using var provider = new Provider((_, prompt) => prompt.Contains("repeated an identical read and result three times")
            ? Finish() : """{"action":"mcp","tool":"get_log","arguments":{"filter":"all"}}""");
        using var http = new HttpClient(provider); var services = await Services(db, http, mcp);
        await services.Analysis.RunAsync(new());
        Assert.Equal("Completed", services.State.Read().Investigations.Last().Status);
        Assert.Equal(3, mcp.Calls); Assert.Equal(4, provider.Prompts.Count);
        Assert.Contains("specific blocker", provider.Prompts.Last());
    }
    [Fact]
    public async Task RevisitingEvidenceBetweenNewReadsDoesNotForceAProductiveRunToFinish()
    {
        using var db = new DataStore(directory); var mcp = new Mcp();
        string[] reads = ["A", "B", "A", "C", "A", "D"];
        using var provider = new Provider((turn, prompt) =>
        {
            Assert.DoesNotContain("repeated an identical read and result three times", prompt);
            return turn < reads.Length
                ? JsonSerializer.Serialize(new { action = "mcp", tool = "get_log", arguments = new { filter = "all", search = reads[turn] } })
                : Finish();
        });
        using var http = new HttpClient(provider); var services = await Services(db, http, mcp);
        await services.Analysis.RunAsync(new());
        Assert.Equal("Completed", services.State.Read().Investigations.Last().Status);
        Assert.Equal(reads.Length, mcp.Calls); Assert.Equal(reads.Length + 1, provider.Prompts.Count);
    }
    [Fact]
    public async Task AlternatingPreviouslySeenReadsStillRequiresABlockerFinish()
    {
        using var db = new DataStore(directory); var mcp = new Mcp();
        using var provider = new Provider((turn, prompt) => turn >= 8 || prompt.Contains("repeated an identical read and result three times")
            ? Finish()
            : JsonSerializer.Serialize(new { action = "mcp", tool = "get_log", arguments = new { filter = "all", search = turn % 2 == 0 ? "A" : "B" } }));
        using var http = new HttpClient(provider); var services = await Services(db, http, mcp);
        await services.Analysis.RunAsync(new());
        Assert.Equal("Completed", services.State.Read().Investigations.Last().Status);
        Assert.Equal(6, mcp.Calls); Assert.Equal(7, provider.Prompts.Count);
        Assert.Contains("specific blocker", provider.Prompts.Last());
    }
    [Fact]
    public async Task CancellationRetainsEvidenceAndOwnsRunUntilFailureAndUsageAreSaved()
    {
        using var db = new DataStore(directory); var mcp = new Mcp();
        using var provider = new PausingProvider(); using var http = new HttpClient(provider);
        var services = await Services(db, http, mcp);
        var before = services.State.Read(); var usageBefore = before.Usage.Count;
        var run = services.Analysis.RunAsync(new("Check the interrupted charge investigation."));
        await provider.Paused.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var locked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // Hold the mutation lock so cancellation cleanup must wait to persist.
        var blocker = Task.Run(() => services.State.MutateAsync(_ =>
        {
            locked.TrySetResult(); release.Task.GetAwaiter().GetResult();
        }));
        try
        {
            await locked.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(services.Analysis.Cancel());
            await provider.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(services.Analysis.Running);
            Assert.False(run.IsCompleted);
            Assert.False(services.Analysis.Start(new(), CancellationToken.None));
        }
        finally { release.TrySetResult(); }
        await blocker.WaitAsync(TimeSpan.FromSeconds(5));
        await run.WaitAsync(TimeSpan.FromSeconds(5));
        var after = services.State.Read(); var failed = after.Investigations.Last();
        Assert.False(services.Analysis.Running); Assert.False(services.Analysis.Cancel());
        Assert.Equal("Interrupted", failed.Status); Assert.Equal("stopped", failed.FailureKind); Assert.Null(failed.Verdict); Assert.Contains("You stopped this check", after.AnalysisError!);
        Assert.Single(failed.ToolEvidence, e => e.Kind == "mcp" && e.Success);
        Assert.Equal(usageBefore + 1, after.Usage.Count);
        Assert.Equal("Interrupted", after.Usage.Last().Status);
        Assert.Equal(120, after.Usage.Last().InputTokens); Assert.Equal(30, after.Usage.Last().OutputTokens);
        Assert.Empty(after.Proposals); Assert.Equal(before.Revision, after.Revision); Assert.Equal(0, services.Writer.Writes);
        await services.Analysis.RunAsync(new());
        Assert.Equal("Completed", services.State.Read().Investigations.Last().Status);
        Assert.False(services.Analysis.Running); Assert.Equal(3, provider.Calls); Assert.Equal(1, mcp.Calls);
    }
    [Fact]
    public void FailedSourcePageCannotAcquireSuccessByBeingReadAgain()
    {
        var failed = new ToolEvidence("failed", "mcp", "get_log", DateTimeOffset.UtcNow, false, "{\"error\":\"source failed\"}", [], "Unavailable");
        using var page = JsonDocument.Parse(JsonSerializer.Serialize(InvestigationContext.Page([failed], "failed"), JsonDefaults.Options));
        Assert.False(page.RootElement.GetProperty("sourceSuccess").GetBoolean());
    }
    public void Dispose() { try { Directory.Delete(directory, true); } catch { } }

    [Fact]
    public async Task ConfigurationSearchFindsSettingsBySubstringWithTheirRanges()
    {
        using var db = new DataStore(directory); var mcp = new Mcp();
        using var provider = new Provider((turn, _) => turn == 0 ? """{"action":"configuration","search":"scaling"}""" : Finish());
        using var http = new HttpClient(provider); var services = await Services(db, http, mcp);
        await services.Analysis.RunAsync(new("Which scaling settings exist?"));
        var investigation = services.State.Read().Investigations.Last();
        var search = Assert.Single(investigation.ToolEvidence, t => t.Kind == "configuration" && t.Id != "configuration");
        Assert.True(search.Success); Assert.Contains("load_scaling", search.ResultJson); Assert.Contains("\"total\":", search.ResultJson); Assert.Contains("\"max\":", search.ResultJson);
        Assert.Contains("Runtime setting search: scaling", search.Request);
    }
}
