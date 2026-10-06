using Joule;
using Xunit;
namespace Joule.Tests;
public class ExperimentFinancialTests
{
    static EnergySummary Cost(double cost,double coverage=1)=>new(DateTimeOffset.UtcNow.AddDays(-3),DateTimeOffset.UtcNow,[],cost,0,cost,coverage,3*86400,[],[]);
    [Fact] public void BetterCostNeverRollsBackForWorseForecast()=>Assert.False(ExperimentEvaluator.ShouldFinancialRollback(new(.1,144),new(.3,144),TimeSpan.FromDays(3),Cost(9),Cost(6),true));
    [Fact] public void MissingCostCoverageNeverRollsBack()=>Assert.False(ExperimentEvaluator.ShouldFinancialRollback(new(.1,144),new(.3,144),TimeSpan.FromDays(3),Cost(9),Cost(15,.8),true));
    [Fact] public void ConfoundersNeverPermitAutomaticFinancialRollback()=>Assert.False(ExperimentEvaluator.ShouldFinancialRollback(new(.1,144),new(.3,144),TimeSpan.FromDays(3),Cost(9),Cost(15),false));
    [Fact] public void CoveredDeteriorationCanTriggerPermissionReview()=>Assert.True(ExperimentEvaluator.ShouldFinancialRollback(new(.1,144),new(.3,144),TimeSpan.FromDays(3),Cost(9),Cost(15),true));
    [Fact] public void LargeFiniteCostsNormalizeBeforeMultiplyingAndRemainComparable()
    {
        var baseline=Cost(1e308);var current=Cost(1.5e308);
        var daily=ExperimentEvaluator.DailyCost(baseline);
        Assert.NotNull(daily);Assert.True(double.IsFinite(daily.Value));Assert.Equal(1d/3,daily.Value/1e308,12);
        Assert.True(ExperimentEvaluator.ShouldFinancialRollback(new(.1,144),new(.3,144),TimeSpan.FromDays(3),baseline,current,true));
    }
    [Fact] public void UnrepresentableDailyCostsAreUnavailableAndNeverTriggerRollback()
    {
        var current=Cost(1e308) with{From=DateTimeOffset.UtcNow.AddHours(-1),To=DateTimeOffset.UtcNow,CostObservedSeconds=3600};
        Assert.Null(ExperimentEvaluator.DailyCost(current));
        Assert.False(ExperimentEvaluator.ShouldFinancialRollback(new(.1,144),new(.3,144),TimeSpan.FromDays(3),Cost(9),current,true));
    }
    [Fact] public void ContextCoverageAndWeatherChangesRemainVisible()
    {
        var before=Cost(9) with{Metrics=new(){["load"]=new(10,3*86400,1,0),["pv"]=new(10,3*86400,1,0),["ev"]=new(0,3*86400,1,0)}};
        var after=before with{Metrics=new(before.Metrics){["pv"]=new(20,3*86400,1,0)}};
        Assert.Contains(ExperimentEvaluator.CompareContext(before,after),x=>x.Contains("pv changed"));
        Assert.Contains(ExperimentEvaluator.CompareContext(before,Cost(15,.8)),x=>x.Contains("coverage"));
    }
}

public class ExperimentOffsetEvidenceTests:IDisposable
{
    readonly string path=Path.Combine(Path.GetTempPath(),"predbat-offset-"+Guid.NewGuid().ToString("N"));
    [Fact] public async Task OffsetMeterTimesQualifyUsingCoveredRatesAndGapsBlockDecision()
    {
        using var db=new DataStore(path);
        var start=DateTimeOffset.UtcNow.AddDays(-6);var change=start.AddDays(3);var end=start.AddDays(6);
        var slots=Enumerable.Range(0,288).Select(i=>new PlanSlot(start.AddMinutes(i*30),1.1,null,0,null,50,null,25,0,"Demand",0)).ToList();
        db.SavePlan(new PlanSnapshot{Source="Predbat",At=start.AddHours(-1),CollectedAt=start.AddHours(-1),Slots=slots});
        db.SavePlan(new PlanSnapshot{Source="Predbat",At=change,CollectedAt=change,Slots=slots.Where(x=>x.Time>=change).Select(x=>x with{LoadForecast=2}).ToList()});
        var samples=new List<TelemetrySample>();double imported=0;
        for(var i=-1;i<=1729;i++)
        {
            var at=start.AddMinutes(i*5).AddSeconds(7);if(i>=0)imported+=at<change?.1:.2;
            foreach(var metric in new[]{"load","pv","ev","grid_import","grid_export","import_tariff","export_tariff"})
            {
                var value=metric switch{"load"=>100+i/6.0,"grid_import"=>10+imported,"import_tariff"=>25.0,_=>0.0};
                samples.Add(new(metric,"sensor."+metric,at,value,metric.EndsWith("tariff")?"p/kWh":"kWh","HomeAssistant",value.ToString(),"kWh"));
            }
        }
        foreach(var batch in samples.Chunk(700))db.SaveTelemetry(batch);
        var evaluator=new ExperimentEvaluator(null!,db);var before=evaluator.Measure("load",start,change);var after=evaluator.Measure("load",change,end,change);
        var baseline=db.ReadEnergySummary(start,change);var current=db.ReadEnergySummary(change,end);
        Assert.NotNull(before);Assert.NotNull(after);Assert.True(before.CoveredSeconds/(3*86400)>.99);Assert.True(after.CoveredSeconds/(3*86400)>.99);
        Assert.Empty(ExperimentEvaluator.CompareContext(baseline,current));
        Assert.True(ExperimentEvaluator.ShouldFinancialRollback(before,after,TimeSpan.FromDays(3),baseline,current,true));
        var state=new StateService(db,null!,true);
        await state.MutateAsync(s=>
        {
            ChangeEngine.SetMode(s,"Auto");ChangeEngine.Permission(s,"load_scaling",true);ChangeEngine.Approve(s,s.Proposals[0].Id,false,true);
            s.Experiments[0].StartedAt=change;s.Experiments[0].ReviewAt=end.AddDays(1);s.Revisions.Last().At=change;
        });
        await new ExperimentEvaluator(state,db).EvaluateAsync(default);
        Assert.Equal("Rolled back",state.Read().Experiments.First().Status);Assert.Equal(3,state.Read().Revision);
        // Behaviour change: one unreadable import reading is a 10-minute outage whose energy the counter proves, so it is spread
        // (estimated timing, £0.10 of estimated cost) and no longer blocks the decision. A three-hour outage still does: the energy in it
        // cannot be placed or priced.
        var bad=samples.First(x=>x.Metric=="grid_import"&&x.Time>=change.AddDays(1));db.SaveTelemetry([bad with{Value=null,Status="invalid"}]);
        var blip=db.ReadEnergySummary(change,end);
        Assert.True(ExperimentEvaluator.CostUsable(blip));Assert.True(blip.EstimatedCostGbp>0);
        db.SaveTelemetry(samples.Where(x=>x.Metric=="grid_import"&&x.Time>=change.AddDays(1.5)&&x.Time<change.AddDays(1.5).AddHours(3)).Select(x=>x with{Value=null,Status="invalid"}));
        var missing=db.ReadEnergySummary(change,end);
        Assert.False(ExperimentEvaluator.CostUsable(missing));Assert.True(missing.UnpricedGridKwh>1);
        Assert.False(ExperimentEvaluator.ShouldFinancialRollback(before,after,TimeSpan.FromDays(3),baseline,missing,true));
    }
    public void Dispose(){if(Directory.Exists(path))Directory.Delete(path,true);}
}
