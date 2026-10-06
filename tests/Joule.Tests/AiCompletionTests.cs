using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Joule;
using Xunit;

namespace Joule.Tests;
public sealed class AiCompletionTests : IDisposable
{
    readonly string directory = Path.Combine(Path.GetTempPath(), "joule-completion-" + Guid.NewGuid().ToString("N"));
    sealed class NoWriter : IPredbatClient
    {
        public bool Configured => true;
        public bool WritesEnabled => false;
        public Task<LiveSnapshot> CollectAsync(CancellationToken ct = default) => Task.FromResult(new LiveSnapshot([], null, "{}", "{}"));
        public Task ApplyAsync(List<Change> c, List<Setting> s, CancellationToken ct = default) => throw new InvalidOperationException("AI must never write in Recommend mode.");
    }
    sealed class Factory(HttpClient http) : IHttpClientFactory { public HttpClient CreateClient(string name) => http; }
    sealed class ScriptedHandler(bool docsAvailable = true, bool invalid = false, bool malformed = false, string sql = "SELECT count(*) AS count FROM plan_slots", bool wrapInProse = false, bool proseFirst = false, bool nullProposals = false, bool firstFinishTooMany = false, string malformedText = "not a JSON document") : HttpMessageHandler
    {
        public int Calls; public int Scripted; public int Finishes; public int DocsCalls; public bool FailedDocs; public List<string> Prompts = [];
        public Func<Task>? BeforeFinish;
        public Func<string, string>? TransformReply;
        public int FailAtCall;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri!.Host == "raw.githubusercontent.com")
            {
                DocsCalls++;
                if (!docsAvailable || FailedDocs) return new(HttpStatusCode.ServiceUnavailable);
                return new(HttpStatusCode.OK) { Content = new StringContent("# Configuration\ninput_number.predbat_load_scaling adjusts historical load forecasts.\n\ninput_number.predbat_pv_scaling adjusts the PV forecast.\n") };
            }
            using var payload = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            var prompt = payload.RootElement.GetProperty("messages")[0].GetProperty("content").GetString()!; Prompts.Add(prompt);
            Calls++;
            if (Calls == FailAtCall) return new(HttpStatusCode.TooManyRequests) { Content = new StringContent("private upstream failure body") };
            string Finish(int proposalCount)
            {
                var proposal = new { title = "Review load scaling", summary = "A documented forecast adjustment", expectedEffect = "Hypothesis: improve forecast error; no native replan has been run.", tradeoff = "Could reduce forecast reserve margin", evidence = new[] { "Historical query and setting documentation" }, evidenceReferences = new[] { Regex.Matches(prompt, "tool-[a-f0-9]{32}").First().Value }, documentationReferences = new[] { new { settingKey = "load_scaling", referenceId = invalid ? "invented-ref" : Regex.Matches(prompt, "doc-[a-f0-9]{24}").FirstOrDefault()?.Value ?? "missing" } }, changes = new[] { new { key = "load_scaling", after = "1.00" } } };
                return JsonSerializer.Serialize(new { action = "finish", verdict = "problem", title = "Forecast evidence review", summary = "Forecast adjustment is a hypothesis; no causal savings are established.", evidence = new[] { "Reviewed stored forecasts and pinned documentation." }, evidenceReferences = new[] { "configuration" }, proposals = nullProposals ? null : (object)Enumerable.Repeat(proposal, proposalCount).ToArray() });
            }
            if (proseFirst && Calls == 1) return Reply("I will start by looking at the stored plan slots and then decide what to query.");
            // A corrective finish retry is answered with a corrected finish, like a real model would.
            if (prompt.Contains("finish reply was rejected")) return Reply(Finish(1));
            var step = Scripted++ % 3;
            if (step == 2 && BeforeFinish != null) await BeforeFinish();
            var reply = step == 0 ? JsonSerializer.Serialize(new { action = "query", sql }) : step == 1 ? "{\"action\":\"documentation\",\"query\":\"load_scaling\"}" : Finish(firstFinishTooMany && Finishes++ == 0 ? 4 : 1);
            if (malformed) reply = malformedText;
            if (wrapInProse) reply = "Here is my next step:\n```json\n" + reply + "\n```\nLet me know if you need anything else.";
            return Reply(reply);
        }
        HttpResponseMessage Reply(string reply) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { choices = new[] { new { finish_reason = "stop", message = new { content = TransformReply?.Invoke(reply) ?? reply } } }, usage = new { prompt_tokens = 100, completion_tokens = 20 } }), Encoding.UTF8, "application/json") };
    }
    static IConfiguration Config() => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Ai:ApiKey"] = "fixture-secret", ["Predbat:DocumentationRef"] = "v9.3.3" }).Build();
    (StateService State, AnalysisService Analysis, DocumentationService Docs) Services(DataStore db, HttpClient http)
    {
        var state = new StateService(db, new NoWriter(), true); var configuration = Config(); var docs = new DocumentationService(db, new Factory(http), configuration);
        var analysis = new AnalysisService(state, db, new AiModelClient(http, new ChatGptAuth(http, Path.Combine(directory, "auth")), configuration), new Factory(http), configuration, docs);
        return (state, analysis, docs);
    }
    [Fact] public async Task FakeProviderQueriesDocumentsProposesAndPersistsImmutableEvidence()
    {
        using var db = new DataStore(directory); using var handler = new ScriptedHandler(); using var http = new HttpClient(handler); var s = Services(db, http);
        await s.State.MutateAsync(x => { x.Proposals.Clear(); x.Ai = new AiPreferences { Provider = "Api", Model = "fixture", InputUsdPerMillion = 2, OutputUsdPerMillion = 10 }; });
        var from = DateTimeOffset.UtcNow.AddDays(-3); var to = DateTimeOffset.UtcNow.AddDays(-1);
        await s.Analysis.RunAsync(new("Investigate forecast error and EV charging without battery causality claims", from, to));
        var investigation = s.State.Read().Investigations.Last(); var proposal = Assert.Single(s.State.Read().Proposals);
        Assert.Equal("Completed", investigation.Status); Assert.Equal(3, investigation.ToolEvidence.Count); Assert.Single(proposal.DocumentationReferences); Assert.Equal(s.State.Read().Revision, proposal.BaseRevision); Assert.Null(proposal.EstimatedMonthlySavingGbp);
        Assert.Contains("Investigate forecast error", handler.Prompts.First()); Assert.Equal(from, investigation.Request.From); Assert.NotNull(db.Load()!.Investigations.Last().ToolEvidence[1].ResultJson);
        var frozen = JsonSerializer.Serialize(investigation.ToolEvidence, JsonDefaults.Options);
        Assert.Equal("Completed", s.State.Read().Usage.Last().Status); Assert.NotNull(s.State.Read().Usage.Last().EstimatedUsd);
        await s.State.MutateAsync(x => ChangeEngine.Deny(x, proposal.Id)); await s.Analysis.RunAsync(new());
        Assert.Equal("Completed", s.State.Read().Usage.Last().Status); Assert.Null(s.State.Read().AnalysisError); Assert.Single(s.State.Read().Proposals);
        Assert.Contains("denied previously", Assert.Single(s.State.Read().Investigations.Last().ToolEvidence, t => t.Kind == "model").Error!);
        Assert.Contains(s.State.Read().Investigations.Last().Evidence, e => e.Contains("Setting proposals were rejected"));
        Assert.Contains("SELECT count(*) AS count FROM plan_slots", handler.Prompts[3]);
        await s.State.CollectAsync(); Assert.Equal(frozen, JsonSerializer.Serialize(s.State.Read().Investigations.Single(i => i.Id == investigation.Id).ToolEvidence, JsonDefaults.Options));
    }
    [Theory]
    [InlineData("SELECT range AS n FROM range(201)")]
    [InlineData("SELECT repeat('x', 5000) AS text")]
    public async Task QueryEvidenceDeclaresRowAndCellClipping(string sql)
    {
        using var db = new DataStore(directory); using var handler = new ScriptedHandler(sql: sql); using var http = new HttpClient(handler); var s = Services(db, http);
        await s.State.MutateAsync(x => { x.Proposals.Clear(); x.Ai = new AiPreferences { Provider = "Api", Model = "fixture" }; });
        await s.Analysis.RunAsync(new());
        var evidence = s.State.Read().Investigations.Last().ToolEvidence.Single(x => x.Kind == "query");
        Assert.True(evidence.Success);
        using var result = JsonDocument.Parse(evidence.ResultJson);
        Assert.True(result.RootElement.GetProperty("truncated").GetBoolean());
    }
    [Fact]
    public async Task DenialDuringProviderResponsePreventsRepeatingTheChange()
    {
        using var db = new DataStore(directory); using var handler = new ScriptedHandler(); using var http = new HttpClient(handler); var services = Services(db, http);
        await services.State.MutateAsync(s => { s.Proposals.Clear(); s.Ai = new AiPreferences { Provider = "Api", Model = "fixture" }; });
        await services.Analysis.RunAsync(new());
        var prior = Assert.Single(services.State.Read(false).Proposals);
        handler.BeforeFinish = () => services.State.MutateAsync(s => ChangeEngine.Deny(s, prior.Id));
        await services.Analysis.RunAsync(new());
        Assert.Equal("Denied", Assert.Single(services.State.Read(false).Proposals).Status);
        Assert.Equal("Completed", services.State.Read(false).Usage.Last().Status); Assert.Null(services.State.Read(false).AnalysisError);
        Assert.Contains("denied previously", Assert.Single(services.State.Read().Investigations.Last().ToolEvidence, t => t.Kind == "model").Error!);
    }
    [Fact] public async Task RepeatedCitationOfUnretrievedEvidenceKeepsFindingsWithoutTheCitation()
    {
        using var db = new DataStore(directory); using var handler = new ScriptedHandler(); using var http = new HttpClient(handler); var s = Services(db, http);
        const string invented = "tool-00000000000000000000000000000000";
        handler.TransformReply = reply => reply.Replace("\"evidenceReferences\":[\"configuration\"]", $"\"evidenceReferences\":[\"configuration\",\"{invented}\"]");
        await s.State.MutateAsync(x => { x.Proposals.Clear(); x.Ai = new AiPreferences { Provider = "Api", Model = "fixture" }; });
        await s.Analysis.RunAsync(new());
        var investigation = s.State.Read().Investigations.Last();
        Assert.Equal("Completed", investigation.Status); Assert.Null(s.State.Read().AnalysisError);
        Assert.DoesNotContain(invented, investigation.EvidenceReferences); Assert.Contains("configuration", investigation.EvidenceReferences);
        Assert.Contains(investigation.Evidence, e => e.Contains("never retrieved were removed"));
    }
    [Theory] [InlineData(false, false)] [InlineData(true, true)]
    public async Task UnretrievedOrInventedDocumentationRejectsProposalAndKeepsToolAudit(bool available, bool invalid)
    {
        using var db = new DataStore(directory); using var http = new HttpClient(new ScriptedHandler(available, invalid)); var s = Services(db, http);
        await s.State.MutateAsync(x => { x.Proposals.Clear(); x.Ai = new AiPreferences { Provider = "Api", Model = "fixture", InputUsdPerMillion = 2 }; });
        await s.Analysis.RunAsync(new());
        Assert.Empty(s.State.Read().Proposals); Assert.Equal("Completed", s.State.Read().Usage.Last().Status); Assert.NotNull(s.State.Read().Usage.Last().EstimatedUsd); Assert.Equal("Completed", s.State.Read().Investigations.Last().Status);
        Assert.Contains(s.State.Read().Investigations.Last().Evidence, e => e.Contains("Setting proposals were rejected")); Assert.Null(s.State.Read().AnalysisError);
        var evidence = s.State.Read().Investigations.Last().ToolEvidence; Assert.Equal(4, evidence.Count);
        var rejected = evidence.Last(); Assert.Equal("model", rejected.Kind); Assert.False(rejected.Success); Assert.Contains("documentation", rejected.Error!); Assert.Contains("Review load scaling", rejected.ResultJson);
    }
    [Fact] public async Task VersionedPrimaryCorpusRemainsSearchableOfflineAndDetectsCorruptCache()
    {
        using var db = new DataStore(directory); using var handler = new ScriptedHandler(); using var http = new HttpClient(handler); var docs = new DocumentationService(db, new Factory(http), Config());
        var first = await docs.SearchAsync("load_scaling"); Assert.NotEmpty(first.References); Assert.Equal(DocumentationService.PrimaryFiles.Length, handler.DocsCalls);
        handler.FailedDocs = true; var offline = await new DocumentationService(db, new Factory(http), Config()).SearchAsync("load_scaling");
        Assert.Equal(first.References.Select(r => r.Id), offline.References.Select(r => r.Id)); Assert.Equal(DocumentationService.PrimaryFiles.Length, handler.DocsCalls);
        Assert.True(DocumentationService.CoversSetting(first.References.First(), "load_scaling")); Assert.False(DocumentationService.CoversSetting(first.References.First(), "invented_setting"));
        foreach (var file in Directory.GetFiles(Path.Combine(directory, "documentation"), "*.json", SearchOption.AllDirectories)) await File.WriteAllTextAsync(file, "{corrupt");
        Assert.Empty((await docs.SearchAsync("load_scaling")).References);
    }
    [Fact] public async Task MalformedProviderOutputGetsOneRetryThenFailsWithRecordedExcerpt()
    {
        using var db = new DataStore(directory); using var handler = new ScriptedHandler(malformed: true); using var http = new HttpClient(handler); var s = Services(db, http);
        await s.State.MutateAsync(x => { x.Proposals.Clear(); x.Ai = new AiPreferences { Provider = "Api", Model = "fixture", InputUsdPerMillion = 2 }; });
        await s.Analysis.RunAsync(new());
        Assert.Empty(s.State.Read().Proposals); Assert.Equal("Failed", s.State.Read().Investigations.Last().Status); Assert.Null(s.State.Read().Usage.Last().EstimatedUsd);
        Assert.Equal(2, handler.Calls); Assert.Equal(200, s.State.Read().Usage.Last().InputTokens); Assert.Contains("single JSON object", handler.Prompts[1]);
        Assert.Contains("corrective retry", s.State.Read().AnalysisError!);
        var rejected = Assert.Single(s.State.Read().Investigations.Last().ToolEvidence, t => t.Kind == "model");
        Assert.False(rejected.Success); Assert.Contains("not a JSON document", rejected.ResultJson); Assert.Contains("JSON", rejected.Error!);
    }
    [Fact] public async Task JsonWrappedInProseAndFencesIsStillParsed()
    {
        using var db = new DataStore(directory); using var handler = new ScriptedHandler(wrapInProse: true); using var http = new HttpClient(handler); var s = Services(db, http);
        await s.State.MutateAsync(x => { x.Proposals.Clear(); x.Ai = new AiPreferences { Provider = "Api", Model = "fixture" }; });
        await s.Analysis.RunAsync(new());
        Assert.Equal("Completed", s.State.Read().Investigations.Last().Status); Assert.Single(s.State.Read().Proposals); Assert.Equal(3, handler.Calls);
    }
    [Fact] public async Task OneProseReplyGetsASingleCorrectiveRetryAndTheRunCompletes()
    {
        using var db = new DataStore(directory); using var handler = new ScriptedHandler(proseFirst: true); using var http = new HttpClient(handler); var s = Services(db, http);
        await s.State.MutateAsync(x => { x.Proposals.Clear(); x.Ai = new AiPreferences { Provider = "Api", Model = "fixture" }; });
        await s.Analysis.RunAsync(new());
        var investigation = s.State.Read().Investigations.Last();
        Assert.Equal("Completed", investigation.Status); Assert.Single(s.State.Read().Proposals); Assert.Equal(4, handler.Calls);
        Assert.Contains("single JSON object", handler.Prompts[1]); Assert.Contains(investigation.Steps, step => step.Contains("corrective retry"));
    }
    [Fact] public async Task NullProposalsFieldMeansNoProposalsAndThePromptStatesTheLimit()
    {
        using var db = new DataStore(directory); using var handler = new ScriptedHandler(nullProposals: true); using var http = new HttpClient(handler); var s = Services(db, http);
        await s.State.MutateAsync(x => { x.Proposals.Clear(); x.Ai = new AiPreferences { Provider = "Api", Model = "fixture" }; });
        await s.Analysis.RunAsync(new());
        Assert.Equal("Completed", s.State.Read().Investigations.Last().Status); Assert.Empty(s.State.Read().Proposals); Assert.Contains("at most three", handler.Prompts[0], StringComparison.OrdinalIgnoreCase);
    }
    [Fact] public async Task RejectedFinishReplyGetsOneCorrectiveRetryCarryingTheReason()
    {
        using var db = new DataStore(directory); using var handler = new ScriptedHandler(firstFinishTooMany: true); using var http = new HttpClient(handler); var s = Services(db, http);
        await s.State.MutateAsync(x => { x.Proposals.Clear(); x.Ai = new AiPreferences { Provider = "Api", Model = "fixture" }; });
        await s.Analysis.RunAsync(new());
        var investigation = s.State.Read().Investigations.Last();
        Assert.Equal("Completed", investigation.Status); Assert.Single(s.State.Read().Proposals); Assert.Equal(4, handler.Calls);
        Assert.Contains("Too many or invalid model proposals", handler.Prompts[3]); Assert.Contains("finish reply was rejected", handler.Prompts[3]); Assert.Contains(investigation.Steps, step => step.Contains("finish reply rejected"));
    }
    [Fact] public async Task LongRejectedRepliesAreRetainedInFull()
    {
        using var db = new DataStore(directory); using var handler = new ScriptedHandler(malformed: true, malformedText: new string('x', 5000)); using var http = new HttpClient(handler); var s = Services(db, http);
        await s.State.MutateAsync(x => { x.Proposals.Clear(); x.Ai = new AiPreferences { Provider = "Api", Model = "fixture" }; });
        await s.Analysis.RunAsync(new());
        var rejected = Assert.Single(s.State.Read().Investigations.Last().ToolEvidence, t => t.Kind == "model");
        Assert.Contains("\"truncated\":false", rejected.ResultJson); Assert.Contains("\"length\":5000", rejected.ResultJson);
    }
    [Fact] public async Task InitialConfigurationContextIsCompactAndMatchesStoredEvidence()
    {
        using var db = new DataStore(directory); using var handler = new ScriptedHandler(); using var http = new HttpClient(handler); var s = Services(db, http);
        await s.State.MutateAsync(x => { x.Proposals.Clear(); x.Ai = new AiPreferences { Provider = "Api", Model = "fixture" }; });
        await s.Analysis.RunAsync(new());
        var prompt = handler.Prompts[0]; var configuration = s.State.Read().Investigations.Last().ToolEvidence.Single(t => t.Id == "configuration");
        Assert.Contains("key: load_scaling", prompt); Assert.Contains("editable:", prompt); Assert.Contains("entityId:", prompt);
        Assert.Contains("\"key\":\"load_scaling\"", configuration.ResultJson); Assert.Contains("\"editable\":", configuration.ResultJson); Assert.Contains("\"entityId\":", configuration.ResultJson);
        foreach (var text in new[] { prompt, configuration.ResultJson })
        {
            Assert.DoesNotContain("autoCooldownHours", text); Assert.DoesNotContain("autoMaxStep", text); Assert.DoesNotContain("\"documentation\":", text); Assert.DoesNotContain("\"description\":", text);
        }
        Assert.Contains("Initial settings", prompt);
        // The prompt embeds the configuration JSON directly rather than as an escaped string.
        Assert.DoesNotContain("\\\"key\\\":", prompt);
    }
    [Fact] public void CompactViewCapsLongOptionListsAndKeepsShortOnesWhole()
    {
        var longList = new Setting { Key = "manual_soc", Type = "select", Options = Enumerable.Range(0, 338).Select(i => $"[{i}]").ToList() };
        var shortList = new Setting { Key = "mode", Type = "select", Options = Enumerable.Range(0, 12).Select(i => $"opt{i}").ToList() };
        var views = JsonDocument.Parse(JsonSerializer.Serialize(AnalysisService.CompactSettings([longList, shortList]), JsonDefaults.Options)).RootElement;
        var capped = views[0].GetProperty("options"); Assert.Equal(9, capped.GetArrayLength()); Assert.Equal("[7]", capped[7].GetString()); Assert.Contains("330 more", capped[8].GetString()); Assert.Contains("configuration action", capped[8].GetString());
        Assert.Equal(12, views[1].GetProperty("options").GetArrayLength());
    }
    [Fact]
    public async Task RejectedModelEvidenceMasksCredentialsBeforeDurableArchiving()
    {
        using var db = new DataStore(directory); using var handler = new ScriptedHandler(malformed: true, malformedText: "Rejected fixture-secret api_key=model-supplied-secret"); using var http = new HttpClient(handler); var s = Services(db, http);
        await s.State.MutateAsync(x => { x.Proposals.Clear(); x.Ai = new AiPreferences { Provider = "Api", Model = "fixture" }; });
        await s.Analysis.RunAsync(new());
        var investigation = s.State.Read().Investigations.Last(); Assert.Equal("Failed", investigation.Status);
        var rejected = Assert.Single(investigation.ToolEvidence, t => t.Kind == "model");
        Assert.Contains("[redacted]", rejected.ResultJson);
        var saved = JsonSerializer.Serialize(db.Load(), JsonDefaults.Options);
        Assert.DoesNotContain("fixture-secret", saved); Assert.DoesNotContain("model-supplied-secret", saved);
    }
    [Fact]
    public async Task FinishCorrectionMasksModelTextAndDynamicValidationReasonBeforePromptsAndLogs()
    {
        using var db = new DataStore(directory); using var handler = new ScriptedHandler(); using var http = new HttpClient(handler); var s = Services(db, http);
        var finishes = 0;
        handler.TransformReply = reply =>
        {
            var root = System.Text.Json.Nodes.JsonNode.Parse(reply)!;
            if (root["action"]!.GetValue<string>() == "finish" && finishes++ == 0)
            {
                root["summary"] = "fixture-secret api_key=model-supplied-secret";
                root["proposals"]![0]!["documentationReferences"]![0]!["settingKey"] = "fixture-secret";
            }
            return root.ToJsonString();
        };
        await s.State.MutateAsync(x => { x.Proposals.Clear(); x.Ai = new AiPreferences { Provider = "Api", Model = "fixture" }; });
        await s.Analysis.RunAsync(new());
        Assert.Equal("Completed", s.State.Read().Investigations.Last().Status); Assert.Single(s.State.Read().Proposals);
        Assert.Contains("finish reply was rejected", handler.Prompts.Last()); Assert.Contains("[redacted]", handler.Prompts.Last());
        Assert.DoesNotContain("fixture-secret", handler.Prompts.Last()); Assert.DoesNotContain("model-supplied-secret", handler.Prompts.Last());
        Assert.DoesNotContain("fixture-secret", JsonSerializer.Serialize(db.Load(), JsonDefaults.Options));
    }
    [Theory]
    [InlineData("evidence")]
    [InlineData("evidenceReferences")]
    [InlineData("category")]
    [InlineData("proposal")]
    public async Task WrongFinishFieldTypesGetTheSameSingleCorrectionAsOtherValidationFailures(string field)
    {
        using var db = new DataStore(directory); using var handler = new ScriptedHandler(); using var http = new HttpClient(handler); var s = Services(db, http);
        var finishes = 0;
        handler.TransformReply = reply =>
        {
            var root = System.Text.Json.Nodes.JsonNode.Parse(reply)!;
            if (root["action"]!.GetValue<string>() == "finish" && finishes++ == 0)
            {
                if (field == "category") root[field] = 42;
                else if (field == "proposal") root["proposals"] = new System.Text.Json.Nodes.JsonArray(42);
                else root[field] = new System.Text.Json.Nodes.JsonArray(new System.Text.Json.Nodes.JsonObject { ["unexpected"] = "object" });
            }
            return root.ToJsonString();
        };
        await s.State.MutateAsync(x => { x.Proposals.Clear(); x.Ai = new AiPreferences { Provider = "Api", Model = "fixture" }; });
        await s.Analysis.RunAsync(new());
        Assert.Equal("Completed", s.State.Read().Investigations.Last().Status); Assert.Single(s.State.Read().Proposals);
        Assert.Equal(4, handler.Calls); Assert.Contains("finish reply was rejected", handler.Prompts.Last());
    }
    sealed class BudgetHandler(bool rejectFirstFinish = false) : HttpMessageHandler
    {
        public List<string> Prompts = [];
        public int Finishes;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            using var payload = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            var prompt = payload.RootElement.GetProperty("messages")[0].GetProperty("content").GetString()!; Prompts.Add(prompt);
            var finish = prompt.Contains("repeated an identical read and result three times", StringComparison.Ordinal);
            var count = finish && rejectFirstFinish && Finishes++ == 0 ? 7 : 6;
            var reply = finish
                ? JsonSerializer.Serialize(new { action = "finish", verdict = "problem", title = "Bounded exploration", summary = "Reviewed available configuration within the evidence budget.", category = "Broad findings", evidence = Enumerable.Range(0, count).Select(i => $"Finding {i}: some prompt results are clipped; full results remain archived.").ToArray(), evidenceReferences = new[] { "configuration" }, proposals = Array.Empty<object>() })
                : "{\"action\":\"configuration\"}";
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { choices = new[] { new { finish_reason = "stop", message = new { content = reply } } }, usage = new { prompt_tokens = prompt.Length / 4, completion_tokens = 20 } })) };
        }
    }
    [Fact]
    public async Task BroadExplorationKeepsFullArchiveButBoundsEveryPromptAndTotalInputGracefully()
    {
        using var db = new DataStore(directory); using var handler = new BudgetHandler(); using var http = new HttpClient(handler); var s = Services(db, http);
        await s.State.MutateAsync(x =>
        {
            x.Proposals.Clear(); x.Ai = new AiPreferences { Provider = "Api", Model = "fixture" };
            x.Settings.Add(new Setting { Key = "manual_soc_audit", Type = "select", Value = "option0", Description = new string('d', 20000), Options = Enumerable.Range(0, 2000).Select(i => "option" + i + new string('x', 100)).ToList() });
        });
        await s.Analysis.RunAsync(new("Explore configuration broadly"));
        var investigation = s.State.Read().Investigations.Last(); Assert.Equal("Completed", investigation.Status);
        Assert.InRange(handler.Prompts.Count, 2, 12); Assert.All(handler.Prompts, p => Assert.True(p.Length <= InvestigationTranscript.PromptLimit, $"Prompt length {p.Length}"));
        Console.WriteLine($"Prompt budget fixture: {handler.Prompts.Count} responses, {handler.Prompts.Max(p => p.Length)} maximum prompt characters, {handler.Prompts.Sum(p => (long)p.Length)} cumulative input characters.");
        Assert.Contains("repeated an identical read and result three times", handler.Prompts.Last());
        Assert.Contains(handler.Prompts, p => p.Contains("promptTruncated", StringComparison.Ordinal));
        var archived = db.Load()!.Investigations.Last().ToolEvidence.First(t => t.Kind == "configuration" && t.Id != "configuration");
        Assert.True(archived.ResultJson.Length > 200000); Assert.Contains("option1999", archived.ResultJson);
        Assert.DoesNotContain("option1999", handler.Prompts.Last());
    }
    [Fact]
    public async Task StructurallyCorruptDocumentationCacheBecomesAnOfflineGapInsteadOfCrashing()
    {
        using var db = new DataStore(directory); using var handler = new ScriptedHandler(); using var http = new HttpClient(handler); var docs = new DocumentationService(db, new Factory(http), Config());
        Assert.NotEmpty((await docs.SearchAsync("load_scaling")).References);
        foreach (var file in Directory.GetFiles(Path.Combine(directory, "documentation"), "*.json", SearchOption.AllDirectories))
        {
            var cached = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(file))!; cached["text"] = null;
            await File.WriteAllTextAsync(file, cached.ToJsonString());
        }
        handler.FailedDocs = true;
        var result = await docs.SearchAsync("load_scaling"); Assert.Empty(result.References); Assert.NotEmpty(result.Gaps);
        Assert.NotNull(docs.Status());
    }
    [Fact]
    public async Task ProviderFailureAfterAToolDoesNotMislabelThatValidActionAsRejectedModelOutput()
    {
        using var db = new DataStore(directory); using var handler = new ScriptedHandler { FailAtCall = 2 }; using var http = new HttpClient(handler); var s = Services(db, http);
        await s.State.MutateAsync(x => { x.Proposals.Clear(); x.Ai = new AiPreferences { Provider = "Api", Model = "fixture" }; });
        await s.Analysis.RunAsync(new());
        var investigation = s.State.Read().Investigations.Last(); Assert.Equal("Failed", investigation.Status);
        Assert.Contains(investigation.ToolEvidence, t => t.Kind == "query" && t.Success); Assert.DoesNotContain(investigation.ToolEvidence, t => t.Kind == "model");
        Assert.Contains("429", s.State.Read().AnalysisError!); Assert.Equal(2, handler.Calls); Assert.Empty(s.State.Read().Proposals);
        Assert.Equal(100, s.State.Read().Usage.Last().InputTokens); Assert.DoesNotContain("private upstream", JsonSerializer.Serialize(db.Load()));
    }
    [Fact]
    public async Task ConfigurationKeysAndOptionsFilterCanRetrieveDetailsOmittedFromCompactLists()
    {
        using var db = new DataStore(directory); using var handler = new ScriptedHandler(); using var http = new HttpClient(handler); var s = Services(db, http);
        handler.TransformReply = reply =>
        {
            var root = System.Text.Json.Nodes.JsonNode.Parse(reply)!;
            return root["action"]!.GetValue<string>() == "query"
                ? "{\"action\":\"configuration\",\"keys\":[\"manual_soc_audit\"],\"optionsFilter\":\"needle\"}"
                : reply;
        };
        await s.State.MutateAsync(x =>
        {
            x.Proposals.Clear(); x.Ai = new AiPreferences { Provider = "Api", Model = "fixture" };
            x.Settings.Add(new Setting { Key = "manual_soc_audit", Type = "select", Description = "Exact requested configuration detail", Options = [.. Enumerable.Range(0, 200).Select(i => "slot-" + i), "needle:last"] });
        });
        await s.Analysis.RunAsync(new()); Assert.Equal("Completed", s.State.Read().Investigations.Last().Status);
        var evidence = s.State.Read().Investigations.Last().ToolEvidence.First(t => t.Kind == "configuration" && t.Id != "configuration");
        using var result = JsonDocument.Parse(evidence.ResultJson); var selected = Assert.Single(result.RootElement.GetProperty("settings").EnumerateArray());
        Assert.Equal("manual_soc_audit", selected.GetProperty("key").GetString()); Assert.Equal("Exact requested configuration detail", selected.GetProperty("description").GetString());
        Assert.Equal("needle:last", Assert.Single(selected.GetProperty("options").EnumerateArray()).GetString());
        Assert.Contains("needle:last", handler.Prompts[1]); Assert.Contains("optionsFilter", handler.Prompts[0]);
    }
    [Fact]
    public async Task ConfigurationInventoryPagesReachKeysBeyondTheInitialPromptExcerpt()
    {
        using var db = new DataStore(directory); using var handler = new ScriptedHandler(); using var http = new HttpClient(handler); var s = Services(db, http);
        handler.TransformReply = reply =>
        {
            var root = System.Text.Json.Nodes.JsonNode.Parse(reply)!;
            return root["action"]!.GetValue<string>() == "query" ? "{\"action\":\"configuration\",\"inventory\":true,\"offset\":200,\"limit\":20}" : reply;
        };
        await s.State.MutateAsync(x =>
        {
            x.Proposals.Clear(); x.Ai = new AiPreferences { Provider = "Api", Model = "fixture" };
            x.Settings.AddRange(Enumerable.Range(0, 250).Select(i => new Setting { Key = $"audit-key-{i:D3}", Type = "select", Options = Enumerable.Range(0, 100).Select(v => "option" + v).ToList() }));
        });
        await s.Analysis.RunAsync(new()); Assert.Equal("Completed", s.State.Read().Investigations.Last().Status);
        var evidence = s.State.Read().Investigations.Last().ToolEvidence.First(t => t.Kind == "configuration" && t.Id != "configuration");
        using var result = JsonDocument.Parse(evidence.ResultJson); var inventory = result.RootElement.GetProperty("inventory");
        // Offsets count the demo's own settings first, however many the sample household has.
        var demoSettings = DemoData.Create().Settings.Count;
        Assert.Equal(20, inventory.GetArrayLength()); Assert.Equal($"audit-key-{200 - demoSettings:D3}", inventory[0].GetProperty("key").GetString()); Assert.Equal($"audit-key-{219 - demoSettings:D3}", inventory[19].GetProperty("key").GetString());
        Assert.Equal(250 + demoSettings, result.RootElement.GetProperty("totalSettings").GetInt32()); Assert.Equal(220, result.RootElement.GetProperty("nextOffset").GetInt32());
        Assert.Contains($"audit-key-{219 - demoSettings:D3}", handler.Prompts[1]); Assert.Contains("inventory", handler.Prompts[0]);
    }
    [Theory]
    [InlineData("offset", "\"wrong\"")]
    [InlineData("offset", "{}")]
    [InlineData("limit", "\"wrong\"")]
    [InlineData("limit", "null")]
    public async Task MalformedInventoryNumberTypesAreToolGapsRatherThanInvestigationCrashes(string field, string value)
    {
        using var db = new DataStore(directory); using var handler = new ScriptedHandler(nullProposals: true); using var http = new HttpClient(handler); var s = Services(db, http);
        handler.TransformReply = reply =>
        {
            var root = System.Text.Json.Nodes.JsonNode.Parse(reply)!;
            return root["action"]!.GetValue<string>() == "query" ? "{\"action\":\"configuration\",\"inventory\":true,\"" + field + "\":" + value + "}" : reply;
        };
        await s.State.MutateAsync(x => { x.Proposals.Clear(); x.Ai = new AiPreferences { Provider = "Api", Model = "fixture" }; });
        await s.Analysis.RunAsync(new()); Assert.Equal("Completed", s.State.Read().Investigations.Last().Status);
        var gap = s.State.Read().Investigations.Last().ToolEvidence.First(t => t.Kind == "configuration" && t.Id != "configuration");
        Assert.False(gap.Success); Assert.Contains("Inventory requires", gap.Error!); Assert.Empty(s.State.Read().Proposals);
    }
    [Fact]
    public async Task LargeContextReservesOneCorrectedFinishWithoutIncreasingTheHardBudget()
    {
        using var db = new DataStore(directory); using var handler = new BudgetHandler(rejectFirstFinish: true); using var http = new HttpClient(handler); var s = Services(db, http);
        await s.State.MutateAsync(x =>
        {
            x.Proposals.Clear(); x.Ai = new AiPreferences { Provider = "Api", Model = "fixture" };
            x.Settings.Add(new Setting { Key = "manual_soc_retry", Type = "select", Description = new string('d', 20000), Options = Enumerable.Range(0, 2000).Select(i => "option" + i + new string('x', 100)).ToList() });
        });
        await s.Analysis.RunAsync(new("Explore broadly and summarize the available findings"));
        var investigation = s.State.Read().Investigations.Last(); Assert.Equal("Completed", investigation.Status); Assert.Equal(6, investigation.Evidence.Count);
        Assert.Equal(2, handler.Finishes); Assert.Contains(investigation.Steps, step => step.Contains("finish reply rejected"));
        Assert.InRange(handler.Prompts.Count, 3, 12); Assert.All(handler.Prompts, prompt => Assert.True(prompt.Length <= InvestigationTranscript.PromptLimit));
        var total = handler.Prompts.Sum(prompt => (long)prompt.Length);
        Assert.Contains("finish reply was rejected", handler.Prompts.Last());
        Assert.Contains("title: 1–140", handler.Prompts[0]); Assert.Contains("summary: 1–1,200", handler.Prompts[0]); Assert.Contains("category: 1–100", handler.Prompts[0]); Assert.Contains("evidence: 1–6", handler.Prompts[0]); Assert.Contains("500 characters", handler.Prompts[0]);
        Console.WriteLine($"Corrected finish fixture: {handler.Prompts.Count} responses, {handler.Prompts.Max(prompt => prompt.Length)} maximum prompt characters, {total} cumulative input characters.");
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RejectedAdvisoryProposalsRetainOnlyValidFindingsAndNeverCreateApprovableChanges(bool invalidFindings)
    {
        using var db = new DataStore(directory); using var handler = new ScriptedHandler(); using var http = new HttpClient(handler); var s = Services(db, http);
        var finishes = 0;
        handler.TransformReply = reply =>
        {
            var root = System.Text.Json.Nodes.JsonNode.Parse(reply)!;
            if (root["action"]!.GetValue<string>() == "finish")
            {
                var first = finishes++ == 0;
                root["evidence"] = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(Enumerable.Range(0, first ? 7 : 6).Select(i => $"Finding {i}: telemetry coverage needs review.")));
                if (!first && invalidFindings) root["evidenceReferences"] = new System.Text.Json.Nodes.JsonArray("invented-reference");
                root["proposals"]![0]!["title"] = "Repair telemetry integrations fixture-secret api_key=model-supplied-secret";
                root["proposals"]![0]!["changes"] = new System.Text.Json.Nodes.JsonArray();
                root["proposals"]![0]!["documentationReferences"] = new System.Text.Json.Nodes.JsonArray();
            }
            return root.ToJsonString();
        };
        await s.State.MutateAsync(x => { x.Proposals.Clear(); x.Ai = new AiPreferences { Provider = "Api", Model = "fixture" }; });
        await s.Analysis.RunAsync(new());
        var investigation = s.State.Read().Investigations.Last(); Assert.Empty(s.State.Read().Proposals); Assert.Equal(2, finishes);
        Assert.Equal("Completed", investigation.Status);
        var rejected = Assert.Single(investigation.ToolEvidence, tool => tool.Kind == "model"); Assert.False(rejected.Success); Assert.Contains("Repair telemetry integrations", rejected.ResultJson);
        Assert.Contains("[redacted]", rejected.ResultJson);
        var persisted = JsonSerializer.Serialize(db.Load(), JsonDefaults.Options); Assert.DoesNotContain("fixture-secret", persisted); Assert.DoesNotContain("model-supplied-secret", persisted);
        Assert.Equal(rejected.ResultJson, db.Load()!.Investigations.Last().ToolEvidence.Single(tool => tool.Kind == "model").ResultJson);
        if (invalidFindings)
        {
            // An invented citation after the correction is removed; the findings themselves are kept.
            Assert.Empty(investigation.EvidenceReferences); Assert.Null(s.State.Read().AnalysisError);
            Assert.Contains(investigation.Evidence, evidence => evidence.Contains("never retrieved were removed"));
        }
        else
        {
            Assert.Contains(investigation.Evidence, evidence => evidence.Contains("Setting proposals were rejected"));
            Assert.Contains(investigation.Steps, step => step.Contains("findings retained without approvable proposals"));
            Assert.Contains("Proposal changes are invalid", rejected.Error!); Assert.Null(s.State.Read().AnalysisError);
        }
        Assert.Contains("Operational advice", handler.Prompts.Last()); Assert.Contains("proposals:[]", handler.Prompts.Last());
    }
    [Fact] public void TargetedRequestsRejectAmbiguousRangesAndLongQuestions()
    {
        Assert.Throws<DomainException>(() => new AnalysisRequest("question", DateTimeOffset.UtcNow.AddDays(-1)).Validate());
        Assert.Throws<DomainException>(() => new AnalysisRequest(new string('a', 2001)).Validate());
        Assert.Throws<DomainException>(() => new AnalysisRequest(null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(-1)).Validate());
    }
    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }

    [Fact]
    public async Task ProposalWithoutDocumentationReferencesGetsThemAttachedByTheServer()
    {
        using var db = new DataStore(directory); using var handler = new ScriptedHandler(); using var http = new HttpClient(handler); var s = Services(db, http);
        handler.TransformReply = reply =>
        {
            var node = System.Text.Json.Nodes.JsonNode.Parse(reply)!.AsObject();
            if (node["action"]?.GetValue<string>() == "finish") foreach (var proposal in (node["proposals"] as System.Text.Json.Nodes.JsonArray ?? []).OfType<System.Text.Json.Nodes.JsonObject>()) proposal.Remove("documentationReferences");
            return node.ToJsonString();
        };
        await s.State.MutateAsync(x => { x.Proposals.Clear(); x.Ai = new AiPreferences { Provider = "Api", Model = "fixture" }; });
        await s.Analysis.RunAsync(new("Fix the load forecast"));
        var investigation = s.State.Read().Investigations.Last(); Assert.Equal("Completed", investigation.Status);
        var proposal = Assert.Single(s.State.Read().Proposals);
        Assert.Equal("load_scaling", Assert.Single(proposal.DocumentationReferences).SettingKey);
        Assert.Contains(investigation.Steps, step => step.Contains("attached by the server"));
        Assert.Equal("problem", investigation.Verdict);
    }
}
