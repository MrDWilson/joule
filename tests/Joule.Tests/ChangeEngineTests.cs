using Joule;
using Xunit;

namespace Joule.Tests;

public class ChangeEngineTests
{
    private static AppState State()
    {
        var state = DemoData.Create();
        state.Mode = "Recommend";
        return state;
    }
    [Fact] public void PermissionsStartOff() => Assert.All(State().Settings, s => Assert.False(s.AutoAllowed));
    [Fact] public void MonitorCannotApprove()
    {
        var s = State(); s.Mode = "Monitor";
        Assert.Throws<DomainException>(() => ChangeEngine.Approve(s, s.Proposals[0].Id, false));
    }
    [Fact] public void ApproveOnceDoesNotGrantFuturePermission()
    {
        var s = State(); var p = s.Proposals[0];
        ChangeEngine.Approve(s, p.Id, false);
        Assert.Equal("Applied", p.Status);
        Assert.False(s.Settings.Single(x => x.Key == p.Changes[0].Key).AutoAllowed);
        Assert.Equal(2, s.Revision);
        Assert.Single(s.Experiments);
    }
    [Fact] public void ApproveAndAllowGrantsPermission()
    {
        var s = State(); var p = s.Proposals[0];
        ChangeEngine.Approve(s, p.Id, true);
        Assert.True(s.Settings.Single(x => x.Key == p.Changes[0].Key).AutoAllowed);
    }
    [Fact] public void AutoRequiresPermissionAndMode()
    {
        var s = State(); var p = s.Proposals[0]; s.Mode = "Auto";
        Assert.False(ChangeEngine.CanAutoApply(s, p));
        s.Settings.Single(x => x.Key == p.Changes[0].Key).AutoAllowed = true;
        Assert.True(ChangeEngine.CanAutoApply(s, p));
        s.Mode = "Recommend"; Assert.False(ChangeEngine.CanAutoApply(s, p));
    }
    [Fact] public void HighRiskSettingStillNeedsApproval()
    {
        var s = State(); var p = s.Proposals[0]; s.Mode = "Auto";
        var setting = s.Settings.Single(x => x.Key == p.Changes[0].Key);
        setting.AutoAllowed = true; setting.Risk = "High";
        Assert.False(ChangeEngine.CanAutoApply(s, p));
    }
    [Fact] public void StaleProposalCannotApply()
    {
        // Stale means one of the proposal's own settings moved; that blocks approval and permission.
        var s = State(); var p = s.Proposals[0];
        ChangeEngine.Edit(s, "load_scaling", "1.05", s.Revision, "Manual edit");
        Assert.Equal(["load_scaling"], ChangeEngine.StaleKeys(s, p));
        Assert.Throws<DomainException>(() => ChangeEngine.Approve(s, p.Id, true));
        Assert.All(s.Settings, x => Assert.False(x.AutoAllowed));
    }
    [Fact] public void UnrelatedEditDoesNotMakeAProposalStale()
    {
        var s = State(); var p = s.Proposals[0];
        ChangeEngine.Edit(s, "pv_scaling", "0.95", s.Revision, "Manual edit");
        Assert.Empty(ChangeEngine.StaleKeys(s, p));
        ChangeEngine.Approve(s, p.Id, false);
        Assert.Equal("Applied", p.Status);
        Assert.Equal("1.00", ChangeEngine.Find(s, "load_scaling").Value);
    }
    [Theory] [InlineData("NaN")] [InlineData("Infinity")] [InlineData("-20")] [InlineData("abc")]
    public void InvalidNumbersRejected(string value)
    {
        var s = State(); Assert.Throws<DomainException>(() => ChangeEngine.Edit(s, "load_scaling", value, s.Revision, "test"));
    }
    [Fact] public void OffStepNumbersCannotBeApprovedOrSelectedAutomatically()
    {
        var s=State();s.Mode="Auto";ChangeEngine.Permission(s,"load_scaling",true);
        var proposal=s.Proposals[0];proposal.Changes=[new("load_scaling","1.08","1.005")];
        Assert.False(ChangeEngine.CanAutoApply(s,proposal));
        Assert.Throws<DomainException>(()=>ChangeEngine.Approve(s,proposal.Id,true));
        Assert.Equal(1,s.Revision);Assert.Equal("Pending",proposal.Status);Assert.Empty(s.Experiments);
        Assert.Equal("1.08",ChangeEngine.Find(s,"load_scaling").Value);
    }
    [Fact] public void UnsupportedEditableSettingTypesCannotBeChanged()
    {
        var s=State();ChangeEngine.Find(s,"load_scaling").Type="unsupported";
        Assert.Throws<DomainException>(()=>ChangeEngine.Edit(s,"load_scaling","1.00",s.Revision,"invalid type"));
        Assert.Equal(1,s.Revision);Assert.Empty(s.Experiments);
    }
    [Theory] [InlineData(0)] [InlineData(-.01)] [InlineData(double.NaN)] [InlineData(double.PositiveInfinity)]
    public void InvalidNumericStepMetadataCannotBeChanged(double step)
    {
        var s=State();ChangeEngine.Find(s,"load_scaling").Step=step;
        Assert.Throws<DomainException>(()=>ChangeEngine.Edit(s,"load_scaling","1.00",s.Revision,"invalid metadata"));
        Assert.Equal(1,s.Revision);
    }
    [Fact] public void LargeNumericRangesStillRejectHalfStepValues()
    {
        var s=State();var setting=ChangeEngine.Find(s,"load_scaling");setting.Min=0;setting.Max=200000;
        Assert.Throws<DomainException>(()=>ChangeEngine.Edit(s,"load_scaling","100000.005",s.Revision,"half step"));
        ChangeEngine.Edit(s,"load_scaling","100000",s.Revision,"whole step");
        Assert.Equal("100000",setting.Value);
    }
    [Fact] public void RevertPreservesUnrelatedLaterEdits()
    {
        var s = State(); ChangeEngine.Edit(s, "load_scaling", "1.00", 1, "First");
        ChangeEngine.Edit(s, "pv_scaling", "0.95", 2, "Second");
        ChangeEngine.Revert(s, 2, 3);
        Assert.Equal("1.08", s.Settings.Single(x => x.Key == "load_scaling").Value);
        Assert.Equal("0.95", s.Settings.Single(x => x.Key == "pv_scaling").Value);
        Assert.Equal(4, s.Revision);
    }
    [Fact] public void RevertRefusesConflictingLaterEdit()
    {
        var s = State(); ChangeEngine.Edit(s, "load_scaling", "1.00", 1, "First");
        ChangeEngine.Edit(s, "load_scaling", "0.95", 2, "Second");
        Assert.Throws<DomainException>(() => ChangeEngine.Revert(s, 2, 3));
    }
    [Fact] public void RestoreCreatesNewRevision()
    {
        var s = State(); ChangeEngine.Edit(s, "load_scaling", "1.00", 1, "First");
        ChangeEngine.Restore(s, 1, 2);
        Assert.Equal(3, s.Revision); Assert.Equal("1.08", s.Settings.Single(x => x.Key == "load_scaling").Value);
    }
    [Fact] public void DeniedProposalCannotApply()
    {
        var s = State(); var p = s.Proposals[0]; ChangeEngine.Deny(s, p.Id);
        Assert.Throws<DomainException>(() => ChangeEngine.Approve(s, p.Id, false));
    }
    [Fact] public void RevokingPermissionStopsAutomaticRollback()
    {
        var s = State(); var p = s.Proposals[0]; ChangeEngine.Approve(s, p.Id, true);
        s.Mode = "Auto"; ChangeEngine.Permission(s, "load_scaling", false);
        Assert.Throws<DomainException>(() => ChangeEngine.Revert(s, 2, 2, true));
    }
    [Fact] public void ApprovingRecordsWhenAndNamesTheTrialAfterTheSuggestion()
    {
        var s = State(); var p = s.Proposals[0]; var before = DateTimeOffset.UtcNow;
        ChangeEngine.Approve(s, p.Id, false);
        Assert.Equal("Applied", p.Status);
        Assert.NotNull(p.DecidedAt); Assert.True(p.DecidedAt >= before);
        Assert.Equal(s.Revision, p.AppliedRevision);
        Assert.Equal(p.Title, s.Experiments.Single().Title);
    }
    [Fact] public void UndoingAnAppliedSuggestionMarksItReverted()
    {
        var s = State(); var p = s.Proposals[0];
        ChangeEngine.Approve(s, p.Id, false);
        var applied = p.AppliedRevision!.Value;
        ChangeEngine.Revert(s, applied, s.Revision);
        Assert.Equal("Reverted", p.Status);
        Assert.Equal("1.08", s.Settings.Single(x => x.Key == "load_scaling").Value);
        Assert.Equal("Rolled back", s.Experiments.Single().Status);
    }
    [Fact] public void UndoingAnUnrelatedEditLeavesAnAppliedSuggestionAlone()
    {
        var s = State(); var p = s.Proposals[0];
        ChangeEngine.Approve(s, p.Id, false);
        ChangeEngine.Edit(s, "pv_scaling", "0.95", s.Revision, "Manual");
        ChangeEngine.Revert(s, s.Revision, s.Revision);
        Assert.Equal("Applied", p.Status);
    }
}
