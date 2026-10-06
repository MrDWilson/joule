using System.Text.Json;
using Joule;
using Xunit;

namespace Joule.Tests;

public sealed class InvestigationLifecycleTests : IDisposable
{
    readonly string path = Path.Combine(Path.GetTempPath(), "joule-lifecycle-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(path, true); } catch { } }
    static readonly List<ToolEvidence> Tools = [new("configuration", "configuration", "settings", DateTimeOffset.UtcNow, true, "{}", [])];
    static JsonDocument Finish(string extra = "") => JsonDocument.Parse("{\"action\":\"finish\",\"title\":\"No material change since the last review\",\"summary\":\"Nothing new.\",\"evidence\":[\"All slots matched.\"],\"evidenceReferences\":[\"configuration\"]" + extra + "}");

    [Fact]
    public void SharedMemoryStoresDeduplicatesAndRemovesFacts()
    {
        using var db = new DataStore(path);
        var fact = db.AddMemory("  There is a 10 kW   heat pump. ", "user");
        Assert.Equal("There is a 10 kW heat pump.", fact.Text);
        Assert.Equal(fact.Id, db.AddMemory("there is a 10 kw heat pump.", "model", "inv-1").Id); // same fact, case-insensitive
        Assert.Single(db.ListMemory());
        Assert.Throws<DomainException>(() => db.AddMemory("", "user"));
        Assert.Throws<DomainException>(() => db.AddMemory(new string('x', DataStore.MemoryTextLimit + 1), "user"));
        Assert.True(db.DeleteMemory(fact.Id)); Assert.False(db.DeleteMemory(fact.Id)); Assert.Empty(db.ListMemory());
    }

    [Fact]
    public void FollowUpsNotCarriedForwardAreRetiredAndSilenceKeepsThem()
    {
        var earlier = new Investigation { Id = "earlier", NextSteps = [new() { Id = "a", Title = "Fix hook" }, new() { Id = "b", Title = "Check tariff" }] };
        var current = new Investigation { Id = "current" };
        var state = new AppState { Investigations = [earlier, current] };
        AnalysisService.RetireFollowUps(state, current, null);
        Assert.All(earlier.NextSteps, step => Assert.Equal("open", step.Status));
        AnalysisService.RetireFollowUps(state, current, ["a"]);
        Assert.Equal("open", earlier.NextSteps[0].Status); Assert.Equal("closed", earlier.NextSteps[1].Status); Assert.NotNull(earlier.NextSteps[1].ClosedAt);
    }

    [Fact]
    public void NewerProposalOnTheSameSettingSupersedesThePendingOne()
    {
        var old = new Proposal { Title = "Old", Status = "Pending", InvestigationId = "earlier", Changes = [new("battery_rate_max_scaling", "1.0", "0.5")] };
        var unrelated = new Proposal { Title = "Other", Status = "Pending", InvestigationId = "earlier", Changes = [new("load_scaling", "1.0", "1.1")] };
        var state = new AppState { Proposals = [old, unrelated] };
        var current = new Investigation { Id = "current" };
        AnalysisService.SupersedeProposals(state, current, [new Proposal { InvestigationId = "current", Changes = [new("battery_rate_max_scaling", "1.0", "0.33")] }]);
        Assert.Equal("Superseded", old.Status); Assert.Equal("Pending", unrelated.Status);
    }

    [Fact]
    public void VerdictDefaultsAndLimitsAreEnforced()
    {
        using var noChange = Finish();
        var result = AnalysisService.ValidateResult(noChange.RootElement, new AppState(), "Api", [], Tools);
        Assert.Equal("no_change", result.Investigation.Verdict);
        using var opportunity = Finish(",\"verdict\":\"opportunity\"");
        Assert.Equal("opportunity", AnalysisService.ValidateResult(opportunity.RootElement, new AppState(), "Api", [], Tools).Investigation.Verdict);
        using var bad = Finish(",\"verdict\":\"celebration\"");
        Assert.Throws<DomainException>(() => AnalysisService.ValidateResult(bad.RootElement, new AppState(), "Api", [], Tools));
        using var longSummary = JsonDocument.Parse("{\"action\":\"finish\",\"title\":\"t\",\"summary\":\"" + new string('s', 1201) + "\",\"evidence\":[\"e\"]}");
        Assert.Throws<DomainException>(() => AnalysisService.ValidateResult(longSummary.RootElement, new AppState(), "Api", [], Tools));
        using var tooManySteps = Finish(",\"nextSteps\":[" + string.Join(",", Enumerable.Range(0, 4).Select(i => "{\"title\":\"t" + i + "\",\"rationale\":\"r\",\"suggestedAction\":\"a\",\"verification\":\"v\",\"uncertainty\":\"u\",\"evidenceReferences\":[\"configuration\"]}")) + "]");
        Assert.Throws<DomainException>(() => AnalysisService.ValidateResult(tooManySteps.RootElement, new AppState(), "Api", [], Tools));
        using var steps = Finish(",\"nextSteps\":[{\"title\":\"Fix hook\",\"rationale\":\"r\",\"suggestedAction\":\"a\",\"verification\":\"v\",\"uncertainty\":\"u\",\"evidenceReferences\":[\"configuration\"]}],\"keepFollowUps\":[\"x\"]");
        var withSteps = AnalysisService.ValidateResult(steps.RootElement, new AppState(), "Api", [], Tools);
        Assert.Equal("finding", withSteps.Investigation.Verdict); Assert.Equal(32, Assert.Single(withSteps.Investigation.NextSteps).Id.Length); Assert.Equal("open", withSteps.Investigation.NextSteps[0].Status);
    }
}
