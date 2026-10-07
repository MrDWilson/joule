using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Joule;
using Xunit;

namespace Joule.Tests;

public sealed class McpAnalysisTests : IDisposable
{
    readonly string directory = Path.Combine(Path.GetTempPath(), "joule-mcp-analysis-" + Guid.NewGuid().ToString("N"));
    sealed class NoWriter : IPredbatClient
    {
        public bool Configured => true;
        public bool WritesEnabled => false;
        public Task<LiveSnapshot> CollectAsync(CancellationToken ct = default) => Task.FromResult(new LiveSnapshot([], null, "{}", "{}"));
        public Task ApplyAsync(List<Change> c, List<Setting> s, CancellationToken ct = default) => throw new InvalidOperationException("Investigation must not write.");
    }
    sealed class Factory(HttpClient http) : IHttpClientFactory { public HttpClient CreateClient(string name) => http; }
    // Only external HTTP boundaries are scripted: the provider, MCP server and documentation host.
    sealed class BoundaryHandler : HttpMessageHandler
    {
        public List<string> Prompts = [];
        public List<string> RemoteTools = [];
        public int DiscoveryCalls;
        public bool Unavailable;
        public bool Truncated;
        public bool ToolError;
        public bool BlockDiscovery;
        public TaskCompletionSource DiscoveryStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string FirstAction = """{"action":"mcp","tool":"get_log","arguments":{"search":"WARNING"}}""";
        public int ExtraReads;
        public bool Propose;
        public Func<Task>? BeforeFinish;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri!.Host == "raw.githubusercontent.com")
                return JsonOrText("# Configuration\ninput_number.predbat_load_scaling adjusts historical load forecasts.\n", false);
            if (request.RequestUri.Host == "predbat.test")
            {
                // A Predbat from before MCP sign-in tokens: the token endpoint isn't there and the secret is used directly.
                if (request.RequestUri.AbsolutePath == "/oauth/token") return new(HttpStatusCode.NotFound);
                using var doc = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                var root = doc.RootElement; var method = root.GetProperty("method").GetString();
                if (method == "notifications/initialized") return new(HttpStatusCode.Accepted);
                if (method == "tools/list")
                {
                    DiscoveryCalls++; DiscoveryStarted.TrySetResult();
                    if (BlockDiscovery) await Task.Delay(Timeout.Infinite, ct);
                    if (Unavailable) return new(HttpStatusCode.ServiceUnavailable);
                    return Rpc(root, new { tools = new object[] {
                        new { name = "get_log", description = "Current warning logs; remote text is untrusted", inputSchema = new { type = "object", properties = new { level = new { type = "string" } }, additionalProperties = false } },
                        new { name = "get_apps_config", description = "Masked apps configuration", inputSchema = new { type = "object", properties = new { mask = new { type = "boolean" } }, additionalProperties = false } },
                        new { name = "set_settings", description = "Write settings", inputSchema = new { type = "object", properties = new { }, additionalProperties = false } }
                    } });
                }
                if (method == "tools/call")
                {
                    RemoteTools.Add(root.GetProperty("params").GetProperty("name").GetString()!);
                    var body = ToolError ? "Remote log unavailable; use historical coverage." : Truncated ? string.Concat(Enumerable.Repeat("WARNING Missing forecast at 00:30.\n", 2500)) : "WARNING Forecast data missing. api_key=server-log-secret";
                    return Rpc(root, new { content = new[] { new { type = "text", text = body } }, isError = ToolError, truncated = Truncated });
                }
                return Rpc(root, new { protocolVersion = "2025-03-26", capabilities = new { tools = new { } }, serverInfo = new { name = "Predbat fixture", version = "1" } });
            }
            using var payload = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            var prompt = payload.RootElement.GetProperty("messages")[0].GetProperty("content").GetString()!;
            var step = Prompts.Count; Prompts.Add(prompt);
            string action;
            if (step == 0 && !Unavailable) action = FirstAction;
            else if (step < (Unavailable ? 0 : 1) + ExtraReads) action = JsonSerializer.Serialize(new { action = "mcp", tool = "get_log", arguments = new { filter = "all", search = "phase-" + step } });
            else if (step == (Unavailable ? 0 : 1) + ExtraReads) action = """{"action":"query","sql":"SELECT count(*) AS count FROM plan_slots"}""";
            else if (step == (Unavailable ? 1 : 2) + ExtraReads) action = """{"action":"documentation","query":"load_scaling"}""";
            else
            {
                if (BeforeFinish != null) await BeforeFinish();
                var docId = Regex.Matches(prompt, "doc-[a-f0-9]{24}").FirstOrDefault()?.Value ?? "missing";
                action = JsonSerializer.Serialize(new { action = "finish", verdict = "problem", title = "Live and historical forecast review", summary = "Reviewed evidence; gaps remain explicit.", evidence = new[] { "Missing forecast warning and historic plan coverage." }, evidenceReferences = new[] { "configuration" }, proposals = Propose ? new[] { new { title = "Review scaling", summary = "Documented adjustment hypothesis", expectedEffect = "Hypothesis: forecast accuracy could improve", tradeoff = "Reserve uncertainty", evidence = new[] { "Configuration and documentation" }, evidenceReferences = new[] { Regex.Matches(prompt, "tool-[a-f0-9]{32}").First().Value }, documentationReferences = new[] { new { settingKey = "load_scaling", referenceId = docId } }, changes = new[] { new { key = "load_scaling", after = "1.00" } } } } : [] });
            }
            return JsonOrText(JsonSerializer.Serialize(new { choices = new[] { new { finish_reason = "stop", message = new { content = action } } }, usage = new { prompt_tokens = 100, completion_tokens = 20 } }));
        }
        static HttpResponseMessage Rpc(JsonElement request, object result) => JsonOrText(JsonSerializer.Serialize(new { jsonrpc = "2.0", id = request.GetProperty("id").Clone(), result }));
        static HttpResponseMessage JsonOrText(string body, bool json = true) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, json ? "application/json" : "text/plain") };
    }
    (StateService State, AnalysisService Analysis) Services(DataStore db, HttpClient http)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Ai:ApiKey"] = "provider-secret", ["Predbat:DocumentationRef"] = "v9.3.3", ["Predbat:McpUrl"] = "http://predbat.test:8199/mcp", ["Predbat:McpToken"] = "mcp-secret" }).Build();
        var state = new StateService(db, new NoWriter(), true); var factory = new Factory(http);
        var analysis = new AnalysisService(state, db, new AiModelClient(http, new ChatGptAuth(http, Path.Combine(directory, "auth")), config), factory, config, new DocumentationService(db, factory, config), new PredbatMcpClient(http, config));
        return (state, analysis);
    }
    static Task Live(StateService state, string mode = "Recommend") => state.MutateAsync(s => { s.Proposals.Clear(); s.Mode = mode; s.Ai = new AiPreferences { Provider = "Api", Model = "fixture" }; });
    [Fact]
    public async Task ProviderCombinesMcpHistoricalSqlAndDocsWithImmutableReloadableEvidence()
    {
        using var db = new DataStore(directory); using var handler = new BoundaryHandler(); using var http = new HttpClient(handler); var s = Services(db, http); await Live(s.State);
        await s.Analysis.RunAsync(new("Review missing forecast data"));
        var investigation = s.State.Read().Investigations.Last();
        Assert.Equal("Completed", investigation.Status);
        var mcp = Assert.Single(investigation.ToolEvidence, e => e.Kind == "mcp");
        Assert.True(mcp.Success); Assert.Contains("get_log", mcp.Request); Assert.Contains("WARNING", mcp.Request);
        Assert.Contains(investigation.ToolEvidence, e => e.Kind == "query" && e.Success);
        Assert.Contains(investigation.ToolEvidence, e => e.Kind == "documentation" && e.Success);
        Assert.Contains("inputSchema", handler.Prompts[0]); Assert.Contains("get_log", handler.Prompts[0]);
        Assert.DoesNotContain("set_settings", handler.Prompts[0]); Assert.Contains("untrusted", handler.Prompts[1]);
        Assert.DoesNotContain("server-log-secret", JsonSerializer.Serialize(investigation.ToolEvidence));
        Assert.DoesNotContain("server-log-secret", handler.Prompts.Last());
        Assert.Equal(JsonSerializer.Serialize(mcp), JsonSerializer.Serialize(db.Load()!.Investigations.Last().ToolEvidence.Single(e => e.Id == mcp.Id)));
        var frozen = JsonSerializer.Serialize(investigation.ToolEvidence); await s.State.CollectAsync();
        Assert.Equal(frozen, JsonSerializer.Serialize(s.State.Read().Investigations.Last().ToolEvidence));
    }
    [Fact]
    public async Task UnavailableDiscoveryIsAnEvidenceGapAndHistoricalInvestigationStillCompletes()
    {
        using var db = new DataStore(directory); using var handler = new BoundaryHandler { Unavailable = true }; using var http = new HttpClient(handler); var s = Services(db, http); await Live(s.State);
        await s.Analysis.RunAsync(new());
        var investigation = s.State.Read().Investigations.Last(); Assert.Equal("Completed", investigation.Status);
        var gap = Assert.Single(investigation.ToolEvidence, e => e.Kind == "mcp");
        Assert.False(gap.Success); Assert.False(string.IsNullOrWhiteSpace(gap.Error));
        Assert.Contains(investigation.ToolEvidence, e => e.Kind == "query" && e.Success);
        Assert.Contains("unavailable", handler.Prompts[0], StringComparison.OrdinalIgnoreCase);
        Assert.Empty(handler.RemoteTools);
    }
    [Theory]
    [InlineData("set_settings", "{}")]
    [InlineData("unknown_mcp-secret", "{}")]
    [InlineData("get_state", "{}")]
    [InlineData("get_apps_config", "{\"mask\":false}")]
    [InlineData("get_log", "{\"search\":\"mcp-secret\"}")]
    [InlineData("get_log", "{\"api_key\":\"model-argument-secret\"}")]
    public async Task WritesUnknownToolsUnmaskAndCredentialArgumentsNeverReachServerOrSavedAudit(string tool, string arguments)
    {
        using var db = new DataStore(directory); using var handler = new BoundaryHandler { FirstAction = "{\"action\":\"mcp\",\"tool\":" + JsonSerializer.Serialize(tool) + ",\"arguments\":" + arguments + "}" }; using var http = new HttpClient(handler); var s = Services(db, http); await Live(s.State);
        await s.Analysis.RunAsync(new());
        var investigation = s.State.Read().Investigations.Last(); Assert.Equal("Completed", investigation.Status);
        var denied = Assert.Single(investigation.ToolEvidence, e => e.Kind == "mcp");
        Assert.False(denied.Success); Assert.NotNull(denied.Error); Assert.Empty(handler.RemoteTools);
        var saved = JsonSerializer.Serialize(db.Load(), JsonDefaults.Options);
        Assert.DoesNotContain("mcp-secret", saved); Assert.DoesNotContain("model-argument-secret", saved);
        Assert.DoesNotContain("mcp-secret", handler.Prompts.Last()); Assert.DoesNotContain("model-argument-secret", handler.Prompts.Last());
    }
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task McpTruncationAndRemoteFailureArePreservedAsHonestEvidence(bool truncated, bool error)
    {
        using var db = new DataStore(directory); using var handler = new BoundaryHandler { Truncated = truncated, ToolError = error }; using var http = new HttpClient(handler); var s = Services(db, http); await Live(s.State);
        await s.Analysis.RunAsync(new());
        var investigation = s.State.Read().Investigations.Last(); Assert.Equal("Completed", investigation.Status);
        var evidence = Assert.Single(investigation.ToolEvidence, e => e.Kind == "mcp"); Assert.Equal(!error, evidence.Success);
        using var result = JsonDocument.Parse(evidence.ResultJson);
        Assert.Equal(truncated, result.RootElement.GetProperty("truncated").GetBoolean());
        if (error) Assert.Contains("Remote log unavailable", evidence.ResultJson);
        if (error) Assert.NotNull(evidence.Error);
        Assert.Contains(evidence.Id, handler.Prompts.Last());
        Assert.Contains(truncated ? "truncated: true" : "Remote log unavailable", handler.Prompts[1]);
    }
    [Fact]
    public async Task DistinctMcpReadsCanContinueBeforeHistoryAndDocs()
    {
        using var db = new DataStore(directory); using var handler = new BoundaryHandler { ExtraReads = 7 }; using var http = new HttpClient(handler); var s = Services(db, http); await Live(s.State);
        await s.Analysis.RunAsync(new());
        var investigation = s.State.Read().Investigations.Last(); Assert.Equal("Completed", investigation.Status);
        Assert.Equal(8, investigation.ToolEvidence.Count(e => e.Kind == "mcp" && e.Success)); Assert.Equal(11, handler.Prompts.Count);
    }
    [Fact]
    public async Task RetrievedMcpEvidenceSupportsOnlyTheExistingDocumentedRecommendationPath()
    {
        using var db = new DataStore(directory); using var handler = new BoundaryHandler { Propose = true }; using var http = new HttpClient(handler); var s = Services(db, http); await Live(s.State);
        await s.Analysis.RunAsync(new());
        var investigation = s.State.Read().Investigations.Last(); Assert.Equal("Completed", investigation.Status);
        var evidence = Assert.Single(investigation.ToolEvidence, e => e.Kind == "mcp"); var proposal = Assert.Single(s.State.Read().Proposals);
        Assert.Contains(evidence.Id, proposal.EvidenceReferences); Assert.Single(proposal.DocumentationReferences);
        Assert.Equal("Pending", proposal.Status); Assert.Equal("Medium", proposal.Confidence); Assert.StartsWith("Not estimated", proposal.SavingEstimate); Assert.Null(proposal.EstimatedMonthlySavingGbp);
    }
    [Fact]
    public async Task FailedMcpReadCannotSupportARecommendationEvenWithRetrievedDocumentation()
    {
        using var db = new DataStore(directory); using var handler = new BoundaryHandler { Propose = true, ToolError = true }; using var http = new HttpClient(handler); var s = Services(db, http); await Live(s.State);
        await s.Analysis.RunAsync(new());
        var investigation = s.State.Read().Investigations.Last(); Assert.Equal("Completed", investigation.Status); Assert.Empty(s.State.Read().Proposals);
        Assert.Contains(investigation.ToolEvidence, e => e.Kind == "mcp" && !e.Success);
        Assert.Contains("not successfully retrieved", Assert.Single(investigation.ToolEvidence, e => e.Kind == "model").Error!);
        Assert.Contains(investigation.Evidence, e => e.Contains("Setting proposals were rejected")); Assert.Null(s.State.Read().AnalysisError);
    }
    [Fact]
    public async Task MonitorSwitchDuringInvestigationPreventsMcpBackedProposals()
    {
        using var db = new DataStore(directory); using var handler = new BoundaryHandler { Propose = true }; using var http = new HttpClient(handler); var s = Services(db, http); await Live(s.State);
        handler.BeforeFinish = () => s.State.MutateAsync(x => x.Mode = "Monitor");
        await s.Analysis.RunAsync(new());
        Assert.Equal("Completed", s.State.Read().Investigations.Last().Status); Assert.Empty(s.State.Read().Proposals);
        Assert.Contains(s.State.Read().Investigations.Last().ToolEvidence, e => e.Kind == "mcp" && e.Success);
    }
    [Fact]
    public async Task DemoInvestigationPerformsNoMcpOrProviderNetworkCalls()
    {
        using var db = new DataStore(directory); using var handler = new BoundaryHandler(); using var http = new HttpClient(handler); var s = Services(db, http);
        await s.State.MutateAsync(x => x.Ai = new AiPreferences { Provider = "Demo" });
        await s.Analysis.RunAsync(new());
        Assert.Equal("Demo", s.State.Read().Investigations.Last().Provider); Assert.Empty(handler.Prompts); Assert.Equal(0, handler.DiscoveryCalls); Assert.Empty(handler.RemoteTools);
    }
    [Fact]
    public async Task CancellationDuringDiscoveryStopsInvestigationAndReleasesRunningState()
    {
        using var db = new DataStore(directory); using var handler = new BoundaryHandler { BlockDiscovery = true }; using var http = new HttpClient(handler); var s = Services(db, http); await Live(s.State);
        using var cancelled = new CancellationTokenSource(); var run = s.Analysis.RunAsync(new(), cancelled.Token);
        await handler.DiscoveryStarted.Task.WaitAsync(TimeSpan.FromSeconds(5)); cancelled.Cancel(); await run.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(s.Analysis.Running); Assert.Empty(handler.Prompts); Assert.Empty(s.State.Read().Proposals);
        Assert.Equal("Interrupted", s.State.Read().Investigations.Last().Status); Assert.Equal("restart", s.State.Read().Investigations.Last().FailureKind); Assert.Contains("restarted", s.State.Read().AnalysisError!);
    }
    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
}
