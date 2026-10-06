using Joule;
using Xunit;

namespace Joule.Tests;

public sealed class LegacyExperimentEvidenceTests : IDisposable
{
    readonly string directory=Path.Combine(Path.GetTempPath(),"predbat-legacy-experiment-"+Guid.NewGuid().ToString("N"));

    static AppState Seed(bool demo,string status="Kept")
    {
        var state=DemoData.Create();state.DataSource=demo?"Demo":"Live";
        ChangeEngine.Edit(state,"load_scaling","1.00",state.Revision,"Historical forecast trial");
        var trial=Assert.Single(state.Experiments);
        trial.Status=status;trial.BaselineError=.25;trial.CurrentError=.75;
        trial.BaselineForecastCoverage=.96;trial.CurrentForecastCoverage=.99;
        trial.BaselineCostGbpPerDay=1.2;trial.CurrentCostGbpPerDay=1.5;
        trial.BaselineCostCoverage=1;trial.CurrentCostCoverage=1;
        trial.ForecastMethod="Historical calculation without source provenance";
        trial.Result="The recorded historical decision is retained.";
        trial.Decisions.Add(new(DateTimeOffset.UtcNow,"keep","Original review note"));
        return state;
    }

    [Theory][InlineData("Kept")][InlineData("Closed")][InlineData("Rolled back")][InlineData("Running")]
    public async Task LegacyLiveForecastScoresAreWithheldWithoutRewritingStoredHistory(string status)
    {
        using var db=new DataStore(directory);var seed=Seed(false,status);db.Save(seed);
        var state=new StateService(db,null!,false);
        for(var pass=0;pass<2;pass++)
        {
            var visible=Assert.Single(state.Read(false).Experiments);
            Assert.Null(visible.BaselineError);Assert.Null(visible.CurrentError);
            Assert.Equal(0,visible.BaselineForecastCoverage);Assert.Equal(0,visible.CurrentForecastCoverage);
            Assert.Contains("unverified",visible.ForecastMethod,StringComparison.OrdinalIgnoreCase);
            Assert.Equal(1.2,visible.BaselineCostGbpPerDay);Assert.Equal(1.5,visible.CurrentCostGbpPerDay);
            Assert.Equal(status,visible.Status);Assert.Equal(seed.Experiments[0].Result,visible.Result);
            Assert.Equal("Original review note",Assert.Single(visible.Decisions).Notes);
            await state.MutateAsync(s=>ChangeEngine.SetMode(s,"Recommend"));
            var stored=Assert.Single(db.Load(false)!.Experiments);
            Assert.Equal(.25,stored.BaselineError);Assert.Equal(.75,stored.CurrentError);
            Assert.Equal(.96,stored.BaselineForecastCoverage);Assert.Equal(.99,stored.CurrentForecastCoverage);
            Assert.Equal(seed.Experiments[0].ForecastMethod,stored.ForecastMethod);
            state=new StateService(db,null!,false);
        }
    }

    [Fact]
    public void LegacyDemoScoresRemainExplicitlySyntheticAndReadable()
    {
        using var db=new DataStore(directory);db.Save(Seed(true));
        var visible=Assert.Single(new StateService(db,null!,true).Read(false).Experiments);
        Assert.Equal(.25,visible.BaselineError);Assert.Equal(.75,visible.CurrentError);
        Assert.Equal(.96,visible.BaselineForecastCoverage);Assert.Equal(.99,visible.CurrentForecastCoverage);
    }

    [Fact]
    public async Task FreshMappedMeterScoringRemainsReadableAfterDecisionAndRestart()
    {
        using var db=new DataStore(directory);var seed=Seed(false,"Running");
        var change=DateTimeOffset.UtcNow.AddDays(-1);
        seed.Revisions.Last().At=change;seed.Experiments[0].StartedAt=change;seed.Experiments[0].ReviewAt=change.AddDays(7);
        db.Save(seed);
        foreach(var start in new[]{change.AddHours(-1),change.AddHours(1)})
        {
            db.SavePlan(new PlanSnapshot{Source="Predbat",At=start.AddMinutes(-10),CollectedAt=start.AddMinutes(-5),Slots=[new(start,1.5,null,0,null,50,null,25,0,"Demand",0)]});
            db.SaveTelemetry([
                new("load","sensor.house",start,10,"kWh","HomeAssistant","10","kWh"),
                new("load","sensor.house",start.AddMinutes(30),11,"kWh","HomeAssistant","11","kWh")
            ],TimeSpan.FromMinutes(30));
        }
        var state=new StateService(db,null!,false);
        await new ExperimentEvaluator(state,db).EvaluateAsync(default);
        var trial=Assert.Single(state.Read(false).Experiments);
        Assert.Equal(.5,trial.BaselineError);Assert.Equal(.5,trial.CurrentError);
        Assert.True(trial.BaselineForecastCoverage>0);Assert.True(trial.CurrentForecastCoverage>0);
        await state.MutateAsync(s=>ChangeEngine.Decide(s,trial.Id,"keep","Reviewed mapped meter evidence",0,s.Revision));
        var restarted=Assert.Single(new StateService(db,null!,false).Read(false).Experiments);
        Assert.Equal(.5,restarted.BaselineError);Assert.Equal(.5,restarted.CurrentError);
        Assert.Equal("Kept",restarted.Status);
        Assert.Equal("Reviewed mapped meter evidence",restarted.Decisions.Last().Notes);
    }

    public void Dispose(){if(Directory.Exists(directory))Directory.Delete(directory,true);}
}
