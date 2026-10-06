using Joule;
using Xunit;
namespace Joule.Tests;
public class ControlLedgerTests
{
    [Fact] public void ManualEditsCreateLedgerAndKeepRecordsDecision()
    {
        var s = DemoData.Create(); ChangeEngine.Edit(s,"load_scaling","1.00",s.Revision,"manual trial");
        var e = Assert.Single(s.Experiments);
        ChangeEngine.Decide(s,e.Id,"keep","Looks acceptable",0,s.Revision);
        Assert.Equal("Kept",e.Status); Assert.Equal("Looks acceptable",Assert.Single(e.Decisions).Notes);
    }
    [Fact] public void LaterManualEditBlocksAutomaticTuningUntilDecision()
    {
        var s = DemoData.Create(); s.Mode="Auto"; ChangeEngine.Permission(s,"load_scaling",true);
        ChangeEngine.Edit(s,"pv_scaling","0.90",s.Revision,"manual trial");
        s.Proposals[0].BaseRevision=s.Revision;
        Assert.False(ChangeEngine.CanAutoApply(s,s.Proposals[0]));
    }
    [Fact] public void FullRestoreRecordsClosedRestorationWithoutStartingAnotherTrial()
    {
        var s=DemoData.Create();
        ChangeEngine.Edit(s,"load_scaling","1.00",s.Revision,"manual trial");
        ChangeEngine.Decide(s,s.Experiments.Single().Id,"keep","accepted",0,s.Revision);
        ChangeEngine.Restore(s,1,s.Revision);
        Assert.Equal(3,s.Revision);
        Assert.Equal("1.08",ChangeEngine.Find(s,"load_scaling").Value);
        // A restore is recorded in the ledger but opens no trial of its own.
        Assert.Single(s.Experiments);
        Assert.DoesNotContain(s.Experiments,e=>e.RevisionId==s.Revision);
        Assert.DoesNotContain(s.Experiments,ChangeEngine.IsOpen);
    }
    [Fact] public void MonitorBlocksManualWrites()
    {
        var s = DemoData.Create(); s.Mode="Monitor";
        Assert.Throws<DomainException>(()=>ChangeEngine.Edit(s,"load_scaling","1.00",s.Revision,"manual"));
    }
    [Fact] public void RevertEligibilityExplainsConflictAndExtendRequiresOpenTrial()
    {
        var s = DemoData.Create(); ChangeEngine.Edit(s,"load_scaling","1.00",s.Revision,"trial");
        var e=s.Experiments.Single(); ChangeEngine.Edit(s,"load_scaling","0.95",s.Revision,"later");
        Assert.Contains("later",ChangeEngine.RollbackEligibility(s,e).Reason,StringComparison.OrdinalIgnoreCase);
        ChangeEngine.Decide(s,e.Id,"close","confounded",0,s.Revision);
        Assert.Throws<DomainException>(()=>ChangeEngine.Decide(s,e.Id,"extend","",3,s.Revision));
    }
    [Fact] public void KeptTrialNeverRemainsAutomaticallyRollbackEligible()
    {
        var s=DemoData.Create();s.Mode="Auto";ChangeEngine.Permission(s,"load_scaling",true);ChangeEngine.Approve(s,s.Proposals[0].Id,false,true);
        var e=s.Experiments.Single();ChangeEngine.Decide(s,e.Id,"keep","accepted",0,s.Revision);
        Assert.False(ChangeEngine.RollbackEligibility(s,e).AutomaticEligible);
        Assert.Throws<DomainException>(()=>ChangeEngine.Revert(s,e.RevisionId,s.Revision,true));
    }

    [Fact] public void RuntimeRevertDoesNotDecideAssociatedFileTrials()
    {
        var s=DemoData.Create();ChangeEngine.Edit(s,"load_scaling","1.00",s.Revision,"runtime trial");
        var fileTrial=new Experiment{RevisionId=s.Revision,FileVersionId="file-version",Title="external files",Status="Needs review"};
        s.Experiments.Add(fileTrial);ChangeEngine.Revert(s,s.Revision,s.Revision);
        Assert.Equal("Needs review",fileTrial.Status);Assert.Empty(fileTrial.Decisions);
        Assert.False(ChangeEngine.RollbackEligibility(s,fileTrial).Eligible);
    }
    [Fact] public void LaterClosedUnrelatedChangeStillBlocksAutomaticRollback()
    {
        var s=DemoData.Create();s.Mode="Auto";ChangeEngine.Permission(s,"load_scaling",true);
        ChangeEngine.Approve(s,s.Proposals[0].Id,false,true);var trial=s.Experiments.Single();
        ChangeEngine.Edit(s,"pv_scaling","0.95",s.Revision,"later adjustment");
        ChangeEngine.Decide(s,s.Experiments.Last().Id,"keep","accepted",0,s.Revision);
        Assert.False(ChangeEngine.RollbackEligibility(s,trial).AutomaticEligible);
        Assert.Throws<DomainException>(()=>ChangeEngine.Revert(s,trial.RevisionId,s.Revision,true));
        Assert.Equal("1.00",ChangeEngine.Find(s,"load_scaling").Value);Assert.Equal(3,s.Revision);
    }
    [Fact] public void ClosedLaterFileChangesStillBlockAutomaticRollback()
    {
        var s=DemoData.Create();s.Mode="Auto";ChangeEngine.Permission(s,"load_scaling",true);
        ChangeEngine.Approve(s,s.Proposals[0].Id,false,true);var trial=s.Experiments.Single();
        trial.StartedAt=DateTimeOffset.UtcNow.AddHours(-4);s.Revisions.Last().At=trial.StartedAt;
        s.Experiments.Add(new Experiment{FileVersionId="changed-files",StartedAt=trial.StartedAt.AddHours(2),Status="Closed"});
        Assert.False(ChangeEngine.RollbackEligibility(s,trial).AutomaticEligible);
        Assert.Throws<DomainException>(()=>ChangeEngine.Revert(s,trial.RevisionId,s.Revision,true));
        Assert.Equal("1.00",ChangeEngine.Find(s,"load_scaling").Value);Assert.Equal(2,s.Revision);
    }
    [Theory] [InlineData("step")] [InlineData("maximum")] [InlineData("minimum")] [InlineData("permission")] [InlineData("mode")] [InlineData("risk")] [InlineData("editable")]
    public void AutomaticRollbackRechecksCurrentUserLimits(string change)
    {
        var s=DemoData.Create();s.Mode="Auto";ChangeEngine.Permission(s,"load_scaling",true);ChangeEngine.Approve(s,s.Proposals[0].Id,false,true);
        var e=s.Experiments.Single();
        switch(change)
        {
            case "step": ChangeEngine.PermissionBounds(s,"load_scaling",true,null,null,.01,72);break;
            case "maximum": ChangeEngine.PermissionBounds(s,"load_scaling",true,null,1.02,.1,72);break;
            case "minimum": ChangeEngine.PermissionBounds(s,"load_scaling",true,1.1,null,.1,72);break;
            case "permission": ChangeEngine.Permission(s,"load_scaling",false);break;
            case "mode": ChangeEngine.SetMode(s,"Recommend");break;
            case "risk": ChangeEngine.Find(s,"load_scaling").Risk="High";break;
            case "editable": ChangeEngine.Find(s,"load_scaling").Editable=false;break;
        }
        Assert.False(ChangeEngine.RollbackEligibility(s,e).AutomaticEligible);
        Assert.Throws<DomainException>(()=>ChangeEngine.Revert(s,e.RevisionId,s.Revision,true));
        Assert.Equal("1.00",ChangeEngine.Find(s,"load_scaling").Value);
    }

}
