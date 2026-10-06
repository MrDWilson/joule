using Joule;
using Xunit;

namespace Joule.Tests;

public class ImpactPreviewTests
{
    [Theory][InlineData("duplicate")][InlineData("out of range")][InlineData("read-only")][InlineData("off step")]
    public void NonApplicableChangesCannotReceiveQuantitativePreviews(string invalid)
    {
        var state=DemoData.Create();var proposal=state.Proposals[0];var now=DateTimeOffset.UtcNow;
        switch(invalid)
        {
            case "duplicate":proposal.Changes.Add(proposal.Changes[0]);break;
            case "out of range":proposal.Changes=[new("load_scaling","1.08","2")];break;
            case "read-only":ChangeEngine.Find(state,"load_scaling").Editable=false;break;
            case "off step":proposal.Changes=[new("load_scaling","1.08","1.005")];break;
        }
        var plan=new PlanSnapshot{Slots=[new(now.AddHours(1),1,null,0,null,50,null,30,10,"Self-use",.2)]};
        var result=ImpactPreviewService.Build(state,proposal,plan,now);
        Assert.False(result.Available);Assert.Null(result.CostDeltaLowerGbp);Assert.Null(result.CostDeltaUpperGbp);Assert.Empty(result.Slots);
    }
    [Fact]
    public void ScalingPreviewDescribesTheForecastChangeWithoutAFakeMoneyRange()
    {
        var state = DemoData.Create();
        var now = DateTimeOffset.UtcNow;
        var proposal = state.Proposals[0];
        var plan = new PlanSnapshot { Slots = [
            new(now.AddHours(-1), 10.8, null, 0, null, 50, null, 30, 10, "Self-use", 1),
            new(now.AddHours(1), 10.8, null, 0, null, 50, null, 30, 10, "Self-use", 1)] };
        var preview = ImpactPreviewService.Build(state, proposal, plan, now);
        Assert.True(preview.Available);
        Assert.Single(preview.Slots);
        Assert.Equal(10, preview.Slots[0].ProposedLoadKwh, 6);
        // load_scaling changes what Predbat expects, not what the house uses, so there is no £ "saving".
        Assert.Null(preview.CostDeltaLowerGbp); Assert.Null(preview.CostDeltaUpperGbp);
        Assert.Equal(-.8, preview.LoadDeltaKwh!.Value, 6);
        Assert.StartsWith("Predbat will plan for 0.8 kWh less home use", preview.Description);
        Assert.Contains("not what your home uses", preview.Description);
        Assert.Contains("not a Predbat replan", preview.Methodology);
        Assert.Equal("Europe/London", preview.TimeZone);
    }

    [Fact]
    public void DescriptionNamesThePartOfTheDayInTheHomeTimeZone()
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");
        // 18:00–20:00 BST on 5 Oct is 17:00–19:00 UTC: the evening at home, the afternoon in UTC.
        var start = new DateTimeOffset(2026, 10, 5, 17, 0, 0, TimeSpan.Zero);
        var slots = Enumerable.Range(0, 4).Select(i => new ImpactSlot(start.AddMinutes(30 * i), 30, 1, 0.9, 0, 0, 20, 10)).ToList();
        var text = ImpactPreviewService.Describe(slots, -0.4, 0, zone);
        Assert.Equal("Predbat will plan for 0.4 kWh less home use over the next 2 hours. This changes what Predbat expects, not what your home uses.", text);
        slots.AddRange(Enumerable.Range(0, 2).Select(i => new ImpactSlot(start.AddHours(-10).AddMinutes(30 * i), 30, 1, 0.99, 0, 0, 20, 10)));
        Assert.Contains("mostly in the evening", ImpactPreviewService.Describe(slots.OrderBy(s => s.Time).ToList(), -0.42, 0, zone));
        Assert.Contains("more solar", ImpactPreviewService.Describe(slots, 0, 0.3, zone));
    }

    [Fact]
    public void MixedOrUnsupportedChangesDoNotReceiveInventedQuantitativeEffects()
    {
        var state = DemoData.Create();
        var proposal = state.Proposals[0];
        proposal.Changes.Add(new("invented_dispatch_setting", "1", "2"));
        var result = ImpactPreviewService.Build(state, proposal, DemoData.Plan(), DateTimeOffset.UtcNow);
        Assert.False(result.Available);
        Assert.Null(result.CostDeltaLowerGbp);
    }

    [Fact]
    public void StaleProposalsCannotUseCurrentPlanAsTheirOriginalBaseline()
    {
        var state = DemoData.Create();
        var proposal = state.Proposals[0];
        ChangeEngine.Edit(state, "pv_scaling", ".95", state.Revision, "Changed assumptions");
        Assert.False(ImpactPreviewService.Build(state, proposal, DemoData.Plan(), DateTimeOffset.UtcNow).Available);
    }

    [Fact]
    public void NegativeTariffsNoLongerProduceAnyMoneyFigure()
    {
        var state = DemoData.Create();
        var now = DateTimeOffset.UtcNow;
        var plan = new PlanSnapshot { Slots = [new(now.AddHours(1), 10.8, null, 0, null, 50, null, -5, 15, "Self-use", 0)] };
        var result = ImpactPreviewService.Build(state, state.Proposals[0], plan, now);
        Assert.True(result.Available); Assert.Null(result.CostDeltaLowerGbp); Assert.Null(result.CostDeltaUpperGbp);
        Assert.Contains("less home use", result.Description);
    }

    [Fact]
    public void PlanGeneratedBeforeCurrentConfigurationCannotBeRescaledAsCurrent()
    {
        var state = DemoData.Create(); var now = DateTimeOffset.UtcNow;
        state.Revisions[^1].At = now.AddMinutes(-5);
        var plan = new PlanSnapshot { At = now.AddMinutes(-10), Slots = [new(now.AddHours(1), 1, null, 0, null, 50, null, 30, 10, "Self-use", 0)] };
        Assert.False(ImpactPreviewService.Build(state, state.Proposals[0], plan, now).Available);
    }

    [Fact]
    public void OverflowingForecastChangeIsUnavailableInsteadOfUnserializableInfinity()
    {
        var state = DemoData.Create(); var now = DateTimeOffset.UtcNow;
        var plan = new PlanSnapshot { Slots = [new(now.AddHours(1), 1e308, null, 0, null, 50, null, 30, 10, "Self-use", 1e308), new(now.AddHours(2), 1e308, null, 0, null, 50, null, 30, 10, "Self-use", 1e308),
            new(now.AddHours(3), 1e308, null, 0, null, 50, null, 30, 10, "Self-use", 0), new(now.AddHours(4), double.MaxValue, null, 0, null, 50, null, 30, 10, "Self-use", 1e308)] };
        Assert.False(ImpactPreviewService.Build(state, state.Proposals[0], plan, now).Available);
    }
}
