using Joule;
using Xunit;

namespace Joule.Tests;

public sealed class TelemetryGenerationCutoffTests : IDisposable
{
    readonly string directory=Path.Combine(Path.GetTempPath(),"predbat-generation-cutoff-"+Guid.NewGuid().ToString("N"));
    [Theory]
    [InlineData("load",false)] [InlineData("load",true)]
    [InlineData("pv",false)] [InlineData("pv",true)]
    public void LateCollectionCannotQualifyAnOldGenerationAsPostChangeForecast(string metric,bool estimates)
    {
        using var db=new DataStore(directory);
        var cutoff=DateTimeOffset.Parse("2020-01-01T09:00:00Z");var start=cutoff.AddHours(1);var end=start.AddMinutes(30);
        PlanSnapshot Plan(DateTimeOffset generated,DateTimeOffset captured,double forecast)=>new()
        {
            Source="Predbat",At=generated,CollectedAt=captured,
            Slots=[new(start,forecast,null,forecast,null,50,null,25,15,"Demand",.2)]
        };
        db.SavePlan(Plan(cutoff.AddMinutes(-5),cutoff.AddMinutes(5),2));
        db.SaveTelemetry([
            new(metric,"sensor."+metric,start,10,"kWh","HomeAssistant","10","kWh"),
            new(metric,"sensor."+metric,end,11,"kWh","HomeAssistant","11","kWh")
        ],TimeSpan.FromMinutes(30));
        List<MatchedForecast> Matches(DateTimeOffset? notBefore)=>estimates?db.ReadMatchedForecastEstimates(metric,start,end,notBefore):db.ReadMatchedForecasts(metric,start,end,notBefore);
        Assert.Equal(2,Assert.Single(Matches(null)).Forecast);
        Assert.Empty(Matches(cutoff));
        db.SavePlan(Plan(cutoff.AddMinutes(10),cutoff.AddMinutes(15),3));
        Assert.Equal(3,Assert.Single(Matches(cutoff)).Forecast);
    }
    public void Dispose(){if(Directory.Exists(directory))Directory.Delete(directory,true);}
}
