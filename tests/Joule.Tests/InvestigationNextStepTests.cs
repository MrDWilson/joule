using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Joule;
using Xunit;

namespace Joule.Tests;

public sealed class InvestigationNextStepTests : IDisposable
{
    readonly string directory = Path.Combine(Path.GetTempPath(), "predbat-next-steps-" + Guid.NewGuid().ToString("N"));

    static List<ToolEvidence> Tools() =>
    [
        new("control-log", "mcp", "get_log: charging period", DateTimeOffset.UtcNow, true,
            "{\"events\":[\"charge_start: backup\",\"discharge_stop: self_consumption\"]}", []),
        new("service-config", "mcp", "get_config: service hooks", DateTimeOffset.UtcNow, true,
            "{\"discharge_stop_service\":\"self_consumption\"}", [])
    ];
    static JsonObject NextStep() => new()
    {
        ["title"] = "Review conflicting Tesla service hooks",
        ["rationale"] = "The recorded discharge-stop call follows charge-start and selects self_consumption.",
        ["suggestedAction"] = "Review the discharge-stop hook so it does not undo an active backup charging command.",
        ["verification"] = "During the next authorized charging window, verify the final mode, measured battery charging power and SOC rise.",
        ["uncertainty"] = "The control sequence is consistent with the failure; the separate 1.67 versus 5.0 kW charging limit remains unverified.",
        ["evidenceReferences"] = new JsonArray("control-log", "service-config")
    };
    static JsonObject Finish(JsonNode? nextSteps) => new()
    {
        ["action"] = "finish", ["title"] = "Overnight charging needs a control review",
        ["summary"] = "The service-hook ordering may interrupt planned charging; confirm the result against observed battery state.",
        ["evidence"] = new JsonArray("Charge-start selects backup before discharge-stop selects self_consumption."),
        ["nextSteps"] = nextSteps, ["proposals"] = new JsonArray()
    };
    static JsonElement Steps(Investigation investigation) => JsonSerializer.SerializeToElement(investigation, JsonDefaults.Options).GetProperty("nextSteps");
    static (Investigation Investigation, List<Proposal> Proposals) Validate(JsonObject result, AppState? state = null, List<ToolEvidence>? tools = null)
    {
        using var json = JsonDocument.Parse(result.ToJsonString());
        return AnalysisService.ValidateResult(json.RootElement, state ?? DemoData.Create(), "Api", [], tools ?? Tools());
    }

    [Theory]
    [InlineData("Monitor")]
    [InlineData("Recommend")]
    [InlineData("Auto")]
    public void ServiceHookDiagnosisRetainsManualActionAndVerificationWithoutAuthorizingAnyChange(string mode)
    {
        var state = DemoData.Create(); state.Mode = mode;
        var before = JsonSerializer.Serialize(state, JsonDefaults.Options);
        var result = Validate(Finish(new JsonArray(NextStep())), state);

        Assert.Empty(result.Proposals);
        var step = Assert.Single(Steps(result.Investigation).EnumerateArray());
        Assert.Equal("Review conflicting Tesla service hooks", step.GetProperty("title").GetString());
        Assert.Contains("next authorized charging window", step.GetProperty("verification").GetString());
        Assert.Contains("remains unverified", step.GetProperty("uncertainty").GetString());
        Assert.Equal(new[] { "control-log", "service-config" }, step.GetProperty("evidenceReferences").EnumerateArray().Select(x => x.GetString()));
        Assert.False(step.TryGetProperty("changes", out _));
        Assert.Equal(before, JsonSerializer.Serialize(state, JsonDefaults.Options));
    }

    [Theory]
    [InlineData("object")]
    [InlineData("too-many")]
    [InlineData("empty-action")]
    [InlineData("long-action")]
    [InlineData("no-verification")]
    [InlineData("no-uncertainty")]
    [InlineData("no-references")]
    [InlineData("unknown-reference")]
    [InlineData("failed-reference")]
    [InlineData("model-reference")]
    [InlineData("execution-payload")]
    public void MalformedOrUnsupportedManualAdviceIsRejectedForCorrectiveFinish(string defect)
    {
        var step = NextStep(); var tools = Tools();
        switch (defect)
        {
            case "empty-action": step["suggestedAction"] = " "; break;
            case "long-action": step["suggestedAction"] = new string('x', 2001); break;
            case "no-verification": step.Remove("verification"); break;
            case "no-uncertainty": step.Remove("uncertainty"); break;
            case "no-references": step["evidenceReferences"] = new JsonArray(); break;
            case "unknown-reference": step["evidenceReferences"] = new JsonArray("invented"); break;
            case "failed-reference": tools[0] = tools[0] with { Success = false }; break;
            case "model-reference": tools[0] = tools[0] with { Kind = "model" }; break;
            case "execution-payload": step["changes"] = new JsonArray(new JsonObject { ["key"] = "discharge_stop_service", ["after"] = "backup" }); break;
        }
        JsonNode content = defect == "object" ? step : defect == "too-many" ? new JsonArray(Enumerable.Range(0, 6).Select(_ => (JsonNode)NextStep()).ToArray()) : new JsonArray(step);
        var error = Assert.Throws<DomainException>(() => Validate(Finish(content), tools: tools));
        Assert.Equal(502, error.Status); Assert.Contains("nextSteps", error.Message);
    }

    [Fact]
    public void ManualAdviceCanCitePinnedPrimaryDocumentationWithoutCreatingASettingProposal()
    {
        var tools = Tools();
        tools.Add(new("docs", "documentation", "Tesla service hooks", DateTimeOffset.UtcNow, true, "{}",
            [new("tesla-template", "v9.3.3", "templates/tesla_powerwall.yaml", "https://raw.githubusercontent.com/springfall2008/batpred/v9.3.3/templates/tesla_powerwall.yaml", "fixture-sha", 1, 4, "discharge_stop_service: allow_export never", DateTimeOffset.UtcNow)]));
        var step = NextStep(); step["evidenceReferences"] = new JsonArray("control-log", "tesla-template");
        var result = Validate(Finish(new JsonArray(step)), tools: tools);
        Assert.Empty(result.Proposals);
        Assert.Contains("tesla-template", Steps(result.Investigation).GetRawText());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LegacyMissingOrNullNextStepsRemainValidFindings(bool explicitNull)
    {
        var finish = Finish(null); if (!explicitNull) finish.Remove("nextSteps");
        Assert.Empty(Steps(Validate(finish).Investigation).EnumerateArray());
        var legacy = JsonSerializer.Deserialize<Investigation>(explicitNull ? "{\"nextSteps\":null}" : "{}", JsonDefaults.Options)!;
        Assert.Empty(Steps(legacy).EnumerateArray());
    }

    [Fact]
    public async Task ManualAdviceSurvivesRestartAndIsRedactedOnSummaryAndDetailCopies()
    {
        var step = NextStep();
        foreach (var field in new[] { "title", "rationale", "suggestedAction", "verification", "uncertainty" }) step[field] = $"{step[field]} configured-private-secret api_key=model-supplied-secret";
        var investigation = JsonSerializer.Deserialize<Investigation>(new JsonObject
        {
            ["id"] = "legacy-manual-action", ["title"] = "Archived manual follow-up", ["nextSteps"] = new JsonArray(step)
        }.ToJsonString(), JsonDefaults.Options)!;
        investigation.ToolEvidence = Tools();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Ai:ApiKey"] = "configured-private-secret" }).Build();
        using var http = new HttpClient();
        using (var db = new DataStore(directory))
        {
            db.Save(new AppState { DataSource = "Live", Investigations = [investigation] });
            var service = new StateService(db, new PredbatClient(http, config), false, configuration: config);
            var summary = service.Read(false).Investigations.Single();
            var detail = service.ReadInvestigation(investigation.Id)!;
            Assert.Empty(summary.ToolEvidence); Assert.Equal(2, detail.ToolEvidence.Count);
            foreach (var read in new[] { summary, detail })
            {
                var text = Steps(read).GetRawText();
                Assert.DoesNotContain("configured-private-secret", text); Assert.DoesNotContain("model-supplied-secret", text);
                Assert.Contains("[redacted]", text); Assert.Contains("control-log", text);
            }
            await service.MutateAsync(s => s.AnalysisError = "Unrelated state update");
            // Sanitizing a read copy must not rewrite the historical decision/evidence record.
            Assert.Contains("configured-private-secret", Steps(db.Load(false)!.Investigations.Single()).GetRawText());
        }
        using var reopened = new DataStore(directory);
        var restored = new StateService(reopened, new PredbatClient(http, config), false, configuration: config);
        Assert.Single(Steps(restored.Read(false).Investigations.Single()).EnumerateArray());
        Assert.DoesNotContain("configured-private-secret", Steps(restored.ReadInvestigation(investigation.Id)!).GetRawText());
        Assert.Equal(2, restored.ReadInvestigation(investigation.Id)!.ToolEvidence.Count);
    }

    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
}
