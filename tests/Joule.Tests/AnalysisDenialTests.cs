using System.Text.Json;
using Joule;
using Xunit;

namespace Joule.Tests;

public sealed class AnalysisDenialTests
{
    static List<ToolEvidence> Tools(int observed) =>
    [
        new("measurement", "query", "SELECT load_actual FROM actual_energy ORDER BY time", DateTimeOffset.UtcNow, true, $"{{\"rows\":[{{\"load_actual\":{observed}}}]}}", []),
        new("docs", "documentation", "scaling", DateTimeOffset.UtcNow, true, "{}", [
            new("doc-scaling", "v9.3.3", "docs/customisation.md", "https://raw.githubusercontent.com/springfall2008/batpred/v9.3.3/docs/customisation.md", "fixture-sha", 1, 2, "input_number.predbat_load_scaling and input_number.predbat_pv_scaling set forecast scaling.", DateTimeOffset.UtcNow)
        ])
    ];

    static (AppState State, JsonDocument Reply) Scenario(bool deniedBundle, bool proposedBundle)
    {
        var state = DemoData.Create(); state.Proposals.Clear(); state.Investigations.Clear();
        var prior = new Investigation { ToolEvidence = Tools(1) }; state.Investigations.Add(prior);
        List<Change> Changes(bool bundle) => bundle
            ? [new("load_scaling", "1.08", "1.00"), new("pv_scaling", "1.00", "0.90")]
            : [new("load_scaling", "1.08", "1.00")];
        state.Proposals.Add(new Proposal { Status = "Denied", InvestigationId = prior.Id, Changes = Changes(deniedBundle) });
        var changes = Changes(proposedBundle);
        var reply = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            action = "finish", title = "Forecast finding", summary = "Review the forecast", evidence = new[] { "Observed load is unchanged." },
            proposals = new[] { new {
                title = "Review forecast settings", summary = "Test forecast scaling", expectedEffect = "A hypothesis", tradeoff = "Accuracy may worsen",
                evidence = new[] { "Observed load is unchanged." }, evidenceReferences = new[] { "measurement" },
                documentationReferences = changes.Select(c => new { settingKey = c.Key, referenceId = "doc-scaling" }),
                changes = changes.Select(c => new { key = c.Key, after = c.After })
            } }
        }));
        return (state, reply);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void SplittingOrBundlingCannotRepeatDeniedSettingsWithUnchangedEvidence(bool deniedBundle, bool proposedBundle)
    {
        var (state, reply) = Scenario(deniedBundle, proposedBundle);
        using (reply)
        {
            var error = Assert.Throws<DomainException>(() => AnalysisService.ValidateResult(reply.RootElement, state, "Api", [], Tools(1)));
            Assert.Contains("denied", error.Message, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void SplitProposalCanUseChangedEvidenceFromTheSameHistoricalQuery()
    {
        var (state, reply) = Scenario(true, false);
        using (reply) Assert.Single(AnalysisService.ValidateResult(reply.RootElement, state, "Api", [], Tools(2)).Proposals);
    }
}
