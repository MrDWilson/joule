using Joule;
using Xunit;
namespace Joule.Tests;

public class ExperimentIntervalTests
{
    [Theory][InlineData(1e308,true)][InlineData(1e307,false)]
    public async Task ExtremeFiniteForecastIntervalsNeverPublishInfiniteExperimentMetrics(double forecast,bool unavailable)
    {
        var path=Path.Combine(Path.GetTempPath(),"predbat-experiment-overflow-"+Guid.NewGuid().ToString("N"));
        try
        {
            using var db=new DataStore(path);var start=DateTimeOffset.UtcNow.AddDays(-4);
            db.SavePlan(new PlanSnapshot{Source="Predbat",At=start,CollectedAt=start,Slots=Enumerable.Range(0,6).Select(i=>new PlanSlot(start.AddMinutes(5+i*5),forecast,null,0,null,50,null,25,0,"Demand",0,5)).ToList()});
            db.SaveTelemetry(Enumerable.Range(0,7).Select(i=>new TelemetrySample("load","sensor.load",start.AddMinutes(5+i*5),10+i,"kWh","HomeAssistant",(10+i).ToString(),"kWh")).ToList(),TimeSpan.FromMinutes(10));
            var rows=db.ReadMatchedForecastEstimates("load",start,start.AddHours(1),start);
            Assert.Equal(6,rows.Count);Assert.All(rows,row=>Assert.True(double.IsFinite(row.Forecast)));
            var measured=new ExperimentEvaluator(null!,db).Measure("load",start,start.AddHours(1),start);
            if(unavailable)Assert.Null(measured);
            else
            {
                Assert.NotNull(measured);Assert.True(double.IsFinite(measured.Error));
                Assert.Equal(6,measured.Error/forecast,10);
            }
            var state=new StateService(db,null!,true);
            await state.MutateAsync(s=>
            {
                ChangeEngine.Edit(s,"load_scaling","1.00",s.Revision,"extreme forecast evidence");
                s.Experiments.Last().StartedAt=start;s.Revisions.Last().At=start;
            });
            await new ExperimentEvaluator(state,db).EvaluateAsync(default);
            var experiment=state.Read(false).Experiments.Last();
            if(unavailable){Assert.Null(experiment.CurrentError);Assert.Equal(0,experiment.CurrentForecastCoverage);}
            else {Assert.NotNull(experiment.CurrentError);Assert.True(double.IsFinite(experiment.CurrentError.Value));}
            Assert.DoesNotContain("Infinity",System.Text.Json.JsonSerializer.Serialize(db.Load(),JsonDefaults.Options));
        }
        finally{if(Directory.Exists(path))Directory.Delete(path,true);}
    }
    [Theory][InlineData(false)][InlineData(true)]
    public async Task ConfigurationChangesWithinBaselineRemainVisibleConfounders(bool fileChange)
    {
        var path=Path.Combine(Path.GetTempPath(),"predbat-baseline-change-"+Guid.NewGuid().ToString("N"));
        try
        {
            using var db=new DataStore(path);var state=new StateService(db,null!,true);
            var now=DateTimeOffset.UtcNow;
            await state.MutateAsync(s=>
            {
                if(fileChange)s.Experiments.Add(new Experiment{FileVersionId="fixture-file-version",StartedAt=now.AddDays(-4),Status="Closed"});
                else
                {
                    ChangeEngine.Edit(s,"pv_scaling","0.95",s.Revision,"prior baseline adjustment");
                    s.Revisions.Last().At=now.AddDays(-4);s.Experiments.Last().StartedAt=now.AddDays(-4);
                    ChangeEngine.Decide(s,s.Experiments.Last().Id,"keep","accepted",0,s.Revision);
                }
                var proposal=s.Proposals[0];proposal.BaseRevision=s.Revision;
                ChangeEngine.Approve(s,proposal.Id,false);
                s.Revisions.Last().At=now.AddDays(-3);s.Experiments.Last().StartedAt=now.AddDays(-3);
            });
            await new ExperimentEvaluator(state,db).EvaluateAsync(default);
            var experiment=state.Read().Experiments.Last();
            Assert.Contains(experiment.Confounders,x=>x.Contains("baseline",StringComparison.OrdinalIgnoreCase)&&x.Contains(fileChange?"files":"configuration",StringComparison.OrdinalIgnoreCase));
            Assert.Equal("Needs review",experiment.Status);
        }
        finally{if(Directory.Exists(path))Directory.Delete(path,true);}
    }
    [Fact] public async Task LaterConfigurationChangeRequiresExperimentReview()
    {
        var path = Path.Combine(Path.GetTempPath(), "predbat-confounder-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var db = new DataStore(path);
            var state = new StateService(db, null!, true);
            await state.MutateAsync(s => ChangeEngine.Approve(s, s.Proposals[0].Id, true));
            await state.MutateAsync(s => ChangeEngine.Edit(s, "pv_scaling", "0.90", s.Revision, "Concurrent tuning"));
            await new ExperimentEvaluator(state, db).EvaluateAsync(default);
            Assert.Equal("Needs review", state.Read().Experiments.First().Status);
            Assert.Equal("0.90", ChangeEngine.Find(state.Read(), "pv_scaling").Value);
            Assert.Equal(3, state.Read().Revision);
            Assert.Equal(2,state.Read().Experiments.Count);
        }
        finally { if (Directory.Exists(path)) Directory.Delete(path, true); }
    }
    [Theory][InlineData(true)][InlineData(false)]
    public async Task BaselineDistinguishesReviewedCatalogueChangeFromInitialSnapshot(bool catalogueChanged)
    {
        var path=Path.Combine(Path.GetTempPath(),"predbat-baseline-catalogue-"+Guid.NewGuid().ToString("N"));
        try
        {
            using var db=new DataStore(path);var state=new StateService(db,null!,true);var now=DateTimeOffset.UtcNow;
            await state.MutateAsync(s=>
            {
                if(catalogueChanged)
                {
                    s.Settings.Add(new Setting{Key="new_external_setting",Type="boolean",Value="on"});
                    s.Revisions.Add(new ConfigRevision{Id=s.Revision+1,At=now.AddDays(-4),Source="Predbat",Reason="Configuration change observed outside this app",Values=s.Settings.ToDictionary(x=>x.Key,x=>x.Value)});
                    s.Experiments.Add(new Experiment{Title="External setting catalogue change",RevisionId=s.Revision,StartedAt=now.AddDays(-4),Status="Closed"});
                }
                else s.Revisions[0].At=now.AddDays(-4);
                var proposal=s.Proposals[0];proposal.BaseRevision=s.Revision;ChangeEngine.Approve(s,proposal.Id,false);
                s.Revisions.Last().At=now.AddDays(-3);s.Experiments.Last().StartedAt=now.AddDays(-3);
            });
            await new ExperimentEvaluator(state,db).EvaluateAsync(default);
            var trial=state.Read(false).Experiments.Last();
            Assert.Equal(catalogueChanged,trial.Confounders.Any(x=>x.Contains("baseline",StringComparison.OrdinalIgnoreCase)&&x.Contains("configuration",StringComparison.OrdinalIgnoreCase)));
            Assert.Equal("Needs review",trial.Status);
        }
        finally{if(Directory.Exists(path))Directory.Delete(path,true);}
    }
    [Fact] public void FollowUpExcludesForecastsMadeBeforeExperimentStarted()
    {
        var path = Path.Combine(Path.GetTempPath(), "predbat-period-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var db = new DataStore(path);
            var start = DateTimeOffset.UtcNow.AddDays(-1);
            db.SavePlan(new PlanSnapshot { At = start.AddHours(-1), Slots = [new PlanSlot(start.AddHours(1), 2, .5, 0, 0, 50, 50, 20, 10, "Self-use", .1)] });
            var evaluator = new ExperimentEvaluator(null!, db);
            Assert.Null(evaluator.Measure("load", start, start.AddHours(2), forecastNotBefore: start));
        }
        finally { if (Directory.Exists(path)) Directory.Delete(path, true); }
    }
    [Fact] public void HourlyForecastCannotBeComparedWithHalfHourlyActual()
    {
        var path = Path.Combine(Path.GetTempPath(), "predbat-interval-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var db = new DataStore(path);
            var start = DateTimeOffset.UtcNow.AddDays(-1);
            db.SavePlan(new PlanSnapshot { At = start.AddHours(-1), Slots = [new PlanSlot(start, 2, .5, 0, 0, 50, 50, 20, 10, "Self-use", .1, 60)] });
            var evaluator = new ExperimentEvaluator(null!, db);
            Assert.Null(evaluator.Measure("load", start, start.AddHours(2)));
            db.SavePlan(new PlanSnapshot { At = start.AddMinutes(-30), Slots = [new PlanSlot(start, 1, .5, 0, 0, 50, 50, 20, 10, "Self-use", .1, 30)] });
            db.SaveTelemetry([
                new("load", "sensor.load", start, 10, "kWh", "HomeAssistant", "10", "kWh"),
                new("load", "sensor.load", start.AddMinutes(30), 10.5, "kWh", "HomeAssistant", "10.5", "kWh")
            ], TimeSpan.FromMinutes(31));
            Assert.Equal(.5, evaluator.Measure("load", start, start.AddHours(2))!.Error, 6);
        }
        finally { if (Directory.Exists(path)) Directory.Delete(path, true); }
    }
}
