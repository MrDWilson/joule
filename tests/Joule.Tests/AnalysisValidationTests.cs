using System.Text.Json;
using Joule;
using Xunit;
namespace Joule.Tests;

public class AnalysisValidationTests
{
    [Fact] public void FailedApiRunHasUnknownCostEvenWithConfiguredPrices()
    {
        var prefs = new AiPreferences { Provider = "Api", InputUsdPerMillion = 2, OutputUsdPerMillion = 10 };
        Assert.Null(AnalysisService.EstimateCost(prefs, 100, 200, "Failed"));
        Assert.Equal(.0022, AnalysisService.EstimateCost(prefs, 100, 200, "Completed")!.Value, 6);
    }
    static JsonDocument Result(string key = "load_scaling", string value = "1.00") => JsonDocument.Parse(JsonSerializer.Serialize(new { action = "finish", title = "A finding", summary = "A hypothesis", evidence = new[] { "Data gap" }, proposals = new[] { new { title = "A proposal", summary = "Why", expectedEffect = "Estimated", tradeoff = "Could worsen", evidence = new[] { "Evidence" }, evidenceReferences = new[] { "configuration" }, documentationReferences = new[] { new { settingKey = key, referenceId = "doc-test" } }, changes = new[] { new { key, after = value } } } } }));
    static List<ToolEvidence> Tools() => [new("configuration", "configuration", "settings", DateTimeOffset.UtcNow, true, "{}", []), new("docs", "documentation", "load_scaling", DateTimeOffset.UtcNow, true, "{}", [new("doc-test", "v9.3.3", "docs/customisation.md", "https://raw.githubusercontent.com/springfall2008/batpred/v9.3.3/docs/customisation.md", "sha", 1, 2, "input_number.predbat_load_scaling adjusts the historical load forecast.", DateTimeOffset.UtcNow)])];
    [Fact] public void UnknownSettingsCannotBecomeProposals()
    {
        using var json = Result("invented_setting");
        Assert.Throws<DomainException>(() => AnalysisService.ValidateResult(json.RootElement, DemoData.Create(), "Api", [], Tools()));
    }
    [Fact] public void MonitorDiscardsModelProposals()
    {
        using var json = Result(); var s = DemoData.Create(); s.Mode = "Monitor";
        Assert.Empty(AnalysisService.ValidateResult(json.RootElement, s, "Api", []).Proposals);
    }
    [Fact] public void ProposalBindsToObservedRevision()
    {
        using var json = Result(); var s = DemoData.Create();
        var result = AnalysisService.ValidateResult(json.RootElement, s, "Api", [], Tools());
        Assert.Equal(1, result.Proposals[0].BaseRevision);
        Assert.Equal("1.08", result.Proposals[0].Changes[0].Before);
        Assert.All(s.Settings, x => Assert.False(x.AutoAllowed));
    }
    [Fact] public void ArbitraryEvidenceStringsDoNotReplaceRetrievedDocumentation()
    {
        using var json = Result(); Assert.Throws<DomainException>(() => AnalysisService.ValidateResult(json.RootElement, DemoData.Create(), "Api", []));
    }
    [Fact] public void DocumentationForDifferentSettingDoesNotAuthorizeChange()
    {
        using var json = Result("pv_scaling", "0.90");
        Assert.Throws<DomainException>(() => AnalysisService.ValidateResult(json.RootElement, DemoData.Create(), "Api", [], Tools()));
    }
    [Fact] public void FailedToolCannotAuthorizeProposalOrInventedEvidenceReference()
    {
        using var json = Result(); var tools = Tools(); tools[0] = tools[0] with { Success = false };
        Assert.Throws<DomainException>(() => AnalysisService.ValidateResult(json.RootElement, DemoData.Create(), "Api", [], tools));
    }
    [Fact] public void SimilarSettingNamesCannotMatchDocumentation()
    {
        var reference = Tools()[1].SourceReferences[0] with { Excerpt = "input_number.predbat_load_scaling10 adjusts the pessimistic scenario." };
        Assert.False(DocumentationService.CoversSetting(reference, "load_scaling"));
        Assert.True(DocumentationService.CoversSetting(reference, "load_scaling10"));
    }
    [Fact] public void RollbackNeedsTimeAndEnoughMatchedData()
    {
        Assert.False(ExperimentEvaluator.ShouldRollback(new(.1, 48), new(.2, 48), TimeSpan.FromHours(1)));
        Assert.False(ExperimentEvaluator.ShouldRollback(new(.1, 47), new(.2, 48), TimeSpan.FromDays(4)));
        Assert.True(ExperimentEvaluator.ShouldRollback(new(.1, 48), new(.2, 48), TimeSpan.FromDays(4)));
    }
}
