using Joule;
using Microsoft.Extensions.Configuration;
using Xunit;
namespace Joule.Tests;

public class TelemetryTests : IDisposable
{
    readonly string path = Path.Combine(Path.GetTempPath(), "telemetry-" + Guid.NewGuid().ToString("N"));
    static readonly DateTimeOffset At = DateTimeOffset.Parse("2020-01-01T10:00:00Z");
    static TelemetrySample Sample(string metric, int minute, double? value, string source = "HomeAssistant") => new(metric, "sensor." + metric, At.AddMinutes(minute), value, metric.EndsWith("tariff") ? "p/kWh" : "kWh", source, value?.ToString() ?? "unavailable", "kWh", Status: value is null ? "invalid" : "observed");
    [Fact] public void MissingSensorsStayNullAndObservedCostsRequireBothFlows()
    {
        using var db = new DataStore(path);
        db.SaveTelemetry([Sample("load",0,10),Sample("load",5,10.5),Sample("grid_import",0,1),Sample("grid_import",5,2),Sample("import_tariff",0,25)]);
        var result = db.ReadEnergySummary(At,At.AddMinutes(10));
        Assert.Equal(.5,result.Metrics["load"].EnergyKwh); Assert.Equal(.5,result.Metrics["load"].CoverageFraction);
        Assert.Null(result.Metrics["pv"].EnergyKwh); Assert.Equal(.25,result.ImportCostGbp);
        Assert.Null(result.ObservedNetCostGbp); Assert.Equal(0,result.CostCoverageFraction);
    }
    [Fact] public void SameMeterReadThroughAnotherTransportKeepsIntervalObserved()
    {
        using var db = new DataStore(path);
        db.SaveTelemetry([Sample("load",0,10),Sample("load",5,10.5,"Predbat mirror")]);
        var metric = db.ReadEnergySummary(At,At.AddMinutes(5)).Metrics["load"];
        Assert.Equal(.5,metric.EnergyKwh); Assert.Equal(1,metric.CoverageFraction);
    }
    [Fact] public void DifferentEntityForSameMetricStillInvalidatesInterval()
    {
        using var db = new DataStore(path);
        db.SaveTelemetry([Sample("load",0,10),new("load","sensor.other_meter",At.AddMinutes(5),10.5,"kWh","HomeAssistant","10.5","kWh")]);
        Assert.Null(db.ReadEnergySummary(At,At.AddMinutes(5)).Metrics["load"].EnergyKwh);
    }
    [Fact] public void RecentTimelineJoinsLatestPreSlotForecastWithMeasuredActuals()
    {
        using var db = new DataStore(path);
        // Later pre-slot capture wins; a capture made after the slot started is not a forecast of it.
        db.SavePlan(new PlanSnapshot { Source="Predbat", At=At.AddHours(-1),CollectedAt=At.AddHours(-1), Slots=[new(At,3,null,1,null,50,null,20,10,"Demand",.2)] });
        db.SavePlan(new PlanSnapshot { Source="Predbat", At=At.AddMinutes(-30),CollectedAt=At.AddMinutes(-30), Slots=[new(At,2,null,1,null,48,null,20,10,"Demand",.2)] });
        db.SavePlan(new PlanSnapshot { Source="Predbat", At=At.AddMinutes(5),CollectedAt=At.AddMinutes(5), Slots=[new(At,9,null,1,null,50,null,20,10,"Demand",.2)] });
        // A slot whose only capture is after its start has no usable forecast and is omitted.
        db.SavePlan(new PlanSnapshot { Source="Predbat", At=At.AddMinutes(35),CollectedAt=At.AddMinutes(35), Slots=[new(At.AddMinutes(30),7,null,1,null,50,null,20,10,"Demand",.2)] });
        var samples = new List<TelemetrySample>();
        for (var m = 0; m <= 30; m += 5) { samples.Add(Sample("load",m,10+m/60d)); samples.Add(Sample("pv",m,4+m/120d)); }
        samples.Add(Sample("soc",30,55));
        db.SaveTelemetry(samples);
        var slot = Assert.Single(db.ReadRecentTimeline(At.AddHours(-2), At.AddHours(1)));
        Assert.Equal(At, slot.Time); Assert.Equal(2, slot.LoadForecast); Assert.Equal(48, slot.SocForecast);
        Assert.Equal(.5, slot.LoadActual!.Value, 6); Assert.Equal(.25, slot.PvActual!.Value, 6); Assert.Equal(55, slot.SocActual);
    }
    // Behaviour change: a reset counts its new value (Home Assistant total_increasing); an invalid reading or a 40-minute collection gap
    // between two readings of the same meter becomes one "spread" interval with the counter difference (timing estimated). Only the
    // first five minutes are slot-precise measurement here; the rest is estimated, and nothing is missing.
    [Fact] public void ResetCountsItsNewValueAndShortOutagesAreSpreadNotDropped()
    {
        using var db = new DataStore(path);
        db.SaveTelemetry([Sample("load",0,10),Sample("load",5,11),Sample("load",10,0),Sample("load",15,null),Sample("load",20,1),Sample("load",60,4)]);
        var load = db.ReadEnergySummary(At,At.AddMinutes(60)).Metrics["load"];
        Assert.Equal(5,load.EnergyKwh); Assert.Equal(3600,load.ObservedSeconds); Assert.Equal(600,load.MeasuredSeconds);
        Assert.Equal(4,load.EstimatedKwh); Assert.Equal(0,load.MissingIntervals); Assert.Equal("estimated",load.State);
    }
    [Fact] public void OutageLongerThanTwoHoursStaysAGapWithTheCounterDifferenceRecorded()
    {
        using var db = new DataStore(path);
        db.SaveTelemetry([Sample("load",0,10),Sample("load",5,11),Sample("load",150,14),Sample("load",155,14.5)]);
        var load = db.ReadEnergySummary(At,At.AddMinutes(155)).Metrics["load"];
        Assert.Equal(1.5,load.EnergyKwh!.Value,9); Assert.Equal(1,load.MissingIntervals); Assert.Equal(3,load.MissingKwh!.Value,9);
        var gap = Assert.Single(load.Gaps); Assert.Equal(At.AddMinutes(5),gap.From); Assert.Equal(At.AddMinutes(150),gap.To); Assert.Equal("no_samples",gap.Reason);
    }
    [Fact] public void DuplicatePollingCannotDoubleCountAndLateSamplesSplitIntervals()
    {
        using var db = new DataStore(path);
        db.SaveTelemetry([Sample("load",0,10),Sample("load",10,12)]);
        db.SaveTelemetry([Sample("load",0,10),Sample("load",5,11),Sample("load",10,12)]);
        Assert.Equal(2,db.ReadEnergySummary(At,At.AddMinutes(10)).Metrics["load"].EnergyKwh);
        Assert.Equal(2,Convert.ToInt32(db.Query("SELECT count(*) n FROM telemetry_intervals")[0]["n"]));
    }
    [Fact] public void FullCoverageEnrichesDurationMatchingWithoutInventingPartialSlot()
    {
        using var db = new DataStore(path);
        var p = new PlanSnapshot { Source="Predbat", At=At.AddHours(-1),CollectedAt=At.AddHours(-1), Slots=[new(At,3,null,2,null,50,null,25,15,"Demand",.2,10),new(At.AddMinutes(10),3,null,2,null,50,null,25,15,"Demand",.2,30)] };
        db.SavePlan(p); db.SaveTelemetry([Sample("load",0,10),Sample("load",5,11),Sample("load",10,12)]);
        Assert.Equal(2,db.GetPlan(p.Id)!.Slots[0].LoadActual); Assert.Null(db.GetPlan(p.Id)!.Slots[1].LoadActual);
    }
    // Behaviour change: a tariff change inside an interval used to leave the interval unpriced. The energy is now split at the rate
    // change (uniform use within the interval): 2 of the 5 minutes at 25p and 3 at 30p.
    [Fact] public void MeterTariffsDetermineObservedNetCostAndTariffChangeInsideIntervalIsSplitAtTheChange()
    {
        using var db = new DataStore(path);
        db.SaveTelemetry([Sample("grid_import",0,1),Sample("grid_import",5,3),Sample("grid_export",0,1),Sample("grid_export",5,2),Sample("import_tariff",0,25),Sample("export_tariff",0,15)]);
        var s = db.ReadEnergySummary(At,At.AddMinutes(5));
        Assert.Equal(.35,s.ObservedNetCostGbp!.Value,6); Assert.Equal(1,s.CostCoverageFraction); Assert.Equal(.35,s.NetCostGbp!.Value,6);
        db.SaveTelemetry([Sample("import_tariff",2,30)]);
        var split = db.ReadEnergySummary(At,At.AddMinutes(5));
        Assert.Equal(2*(.4*.25+.6*.30),split.ImportCostGbp!.Value,9); Assert.Equal(2*(.4*.25+.6*.30)-.15,split.ObservedNetCostGbp!.Value,9);
    }
    [Fact] public void DateFilteredPagingCanReachOldPlansBeyondOneHundred()
    {
        using var db = new DataStore(path);
        for(var i=0;i<125;i++) db.SavePlan(new PlanSnapshot { At=At.AddMinutes(i),CollectedAt=At.AddMinutes(i),Source="Predbat" });
        var page = db.ListPlans(At,At.AddDays(1),100,100);
        Assert.Equal(125,page.Total);Assert.Equal(25,page.Items.Count);Assert.Equal(At,page.Items.Last().At);
    }
    [Fact] public void LocalDailyBoundariesRespectDstInsteadOfAssumingTwentyFourHours()
    {
        using var db = new DataStore(path);
        var days=db.GetDailySummaries(DateTimeOffset.Parse("2026-10-24T23:00:00Z"),DateTimeOffset.Parse("2026-10-26T00:00:00Z"));
        Assert.Single(days);Assert.Equal(25,(days[0].To-days[0].From).TotalHours);
    }
    [Fact] public void MatchedForecastUsesLatestPreSlotForecastAndExactMeasuredCadence()
    {
        using var db=new DataStore(path);
        db.SavePlan(new PlanSnapshot {Source="Predbat",At=At.AddHours(-1),CollectedAt=At.AddHours(-1),Slots=[new(At,3,null,1,null,50,null,20,10,"Demand",.2)]});
        db.SavePlan(new PlanSnapshot {Source="Predbat",At=At.AddMinutes(-30),CollectedAt=At.AddMinutes(-30),Slots=[new(At,2,null,1,null,50,null,20,10,"Demand",.2)]});
        db.SaveTelemetry(Enumerable.Range(0,7).Select(i=>Sample("load",i*5,10+i*.1)));
        var row=Assert.Single(db.ReadMatchedForecasts("load",At,At.AddHours(1)));
        Assert.Equal(2,row.Forecast);Assert.Equal(.6,row.Actual,6);Assert.Equal(1800,row.DurationSeconds);
        Assert.Empty(db.ReadMatchedForecasts("load",At,At.AddHours(1),At));
    }
    // Behaviour change: jittered poll times used to leave the plan actual null even though the boundary-allocated evidence was 3 kWh.
    // Plan slots now expose that boundary allocation as the actual, labelled "boundary" so it is never mistaken for an exact measurement.
    [Fact] public void IrregularMeterEvidenceShowsPartialActualAndBoundaryAllocatedPlanActual()
    {
        using var db=new DataStore(path);
        var p=new PlanSnapshot {Source="Predbat",At=At.AddHours(-1),CollectedAt=At.AddHours(-1),Slots=[new(At,3,null,1,null,50,null,20,10,"Demand",.2)]};db.SavePlan(p);
        var points=Enumerable.Range(0,8).Select(i=>Sample("load",(i-1)*5,10+i*.5) with {Time=At.AddMinutes((i-1)*5).AddSeconds(7)}).ToList();
        db.SaveTelemetry(points);
        var evidence=Assert.Single(db.ReadPlanEvidence(p.Id)!.Slots);
        var slot=db.GetPlan(p.Id)!.Slots[0];
        Assert.Equal(3,slot.LoadActual!.Value,6);Assert.Equal("boundary",slot.LoadActualMethod);
        // Behaviour change: the evidence summary now prorates the meter intervals that straddle the slot edges, so it matches the estimate.
        Assert.Equal(3,evidence.Actual.Metrics["load"].EnergyKwh!.Value,9);Assert.Equal(1,evidence.Actual.Metrics["load"].CoverageFraction,6);
        Assert.Equal(3,evidence.EstimatedLoadKwh!.Value,6);Assert.Contains("uniform",evidence.EstimateMethod);Assert.Null(evidence.EstimatedPvKwh);
        // Behaviour change: an unreadable reading inside the slot no longer blanks it. The counters either side prove the energy, so the
        // actual is still given, labelled "estimated"; the measured-only boundary estimate stays null.
        db.SaveTelemetry([points[3] with {Value=null,Status="invalid"}]);
        Assert.Null(db.ReadPlanEvidence(p.Id)!.Slots[0].EstimatedLoadKwh);
        Assert.Equal(3,db.GetPlan(p.Id)!.Slots[0].LoadActual!.Value,6);Assert.Equal("estimated",db.GetPlan(p.Id)!.Slots[0].LoadActualMethod);
    }
    [Fact] public void LatestReadingRetainsMissingValueStatusAndSource()
    {
        using var db=new DataStore(path);db.SaveTelemetry([Sample("soc",0,50) with {Unit="%"},Sample("soc",5,null) with {Unit="%"}]);
        var latest=db.ReadLatestTelemetry()["soc"];Assert.Null(latest.Value);Assert.Equal("invalid",latest.Status);Assert.Equal(At.AddMinutes(5),latest.Time);
    }
    [Fact] public void RepeatedPlanTimestampRetainsOneForecastAndStillCollectsLaterActuals()
    {
        using var db=new DataStore(path);var p=new PlanSnapshot {At=At.AddHours(-1),CollectedAt=At.AddHours(-1),Source="Predbat",Slots=[new(At,3,null,1,null,50,null,20,10,"Demand",.2)]};db.SavePlan(p);
        db.SavePlan(new PlanSnapshot {At=p.At,CollectedAt=p.At,Source="Predbat",Slots=p.Slots},"""{"predbat.load_energy_actual":{"state":"3","attributes":{"results":{"2020-01-01T10:00:00Z":1,"2020-01-01T10:30:00Z":2}}}}""","{}");
        db.SaveTelemetry([Sample("load",0,1),Sample("load",30,2)],TimeSpan.FromMinutes(30));
        Assert.Equal(1,db.ListPlans(null,null).Total);Assert.Equal(1,db.GetPlan(p.Id)!.Slots[0].LoadActual);
    }
    [Fact] public void SequentialLiveCollectionsFormConsecutiveIntervals()
    {
        using var db=new DataStore(path);db.SaveTelemetry([Sample("load",0,10)]);db.SaveTelemetry([Sample("load",5,11)]);db.SaveTelemetry([Sample("load",10,12)]);
        Assert.Equal(2,db.ReadEnergySummary(At,At.AddMinutes(10)).Metrics["load"].EnergyKwh);
    }
    [Fact] public void IrregularForecastComparisonAllocatesForecastAcrossWholeMeasuredInterval()
    {
        using var db=new DataStore(path);
        db.SavePlan(new PlanSnapshot {Source="Predbat",At=At.AddHours(-1),CollectedAt=At.AddHours(-1),Slots=[new(At,3,null,1,null,50,null,20,10,"Demand",.2),new(At.AddMinutes(30),6,null,1,null,50,null,20,10,"Demand",.2)]});
        db.SaveTelemetry([Sample("load",25,10),Sample("load",35,11)]);
        var row=Assert.Single(db.ReadMatchedForecastEstimates("load",At,At.AddHours(1)));
        Assert.Equal(1.5,row.Forecast);Assert.Equal(1,row.Actual);Assert.Equal(600,row.DurationSeconds);Assert.Contains("uniform",row.Method);
        Assert.Empty(db.ReadMatchedForecastEstimates("load",At,At.AddHours(1),At));
    }
    [Fact] public void ComparisonRejectsMeasuredIntervalWithoutCompleteForecastCoverage()
    {
        using var db=new DataStore(path);
        db.SavePlan(new PlanSnapshot {Source="Predbat",At=At.AddHours(-1),CollectedAt=At.AddHours(-1),Slots=[new(At,3,null,1,null,50,null,20,10,"Demand",.2)]});
        db.SaveTelemetry([Sample("load",25,10),Sample("load",35,11)]);
        Assert.Empty(db.ReadMatchedForecastEstimates("load",At,At.AddHours(1)));
    }
    // Behaviour change: readings are now keyed at each sensor's own update time, so two meters almost never share interval boundaries.
    // The matched figure therefore covers the stretch both meters measured (1–5 min here), prorating each interval within it, instead
    // of requiring common boundaries. The headline net cost is import minus export.
    [Fact] public void DifferentGridMeterSupportsMatchOnlyWhereBothMetersMeasured()
    {
        using var db=new DataStore(path);
        db.SaveTelemetry([Sample("grid_import",0,1),Sample("grid_import",5,3),Sample("grid_export",1,1),Sample("grid_export",6,2),Sample("import_tariff",0,25),Sample("export_tariff",0,15)]);
        var s=db.ReadEnergySummary(At,At.AddMinutes(10));Assert.Equal(.5,s.ImportCostGbp);Assert.Equal(.15,s.ExportCreditGbp);Assert.Equal(.35,s.NetCostGbp!.Value,9);
        Assert.Equal(.5*.8-.15*.8,s.ObservedNetCostGbp!.Value,9);Assert.Equal(240,s.CostObservedSeconds,6);Assert.Equal(.4,s.CostCoverageFraction,6);
    }
    [Fact] public void UnequalGridCadencesWithSameWholeSupportProvideMatchedNetCost()
    {
        using var db=new DataStore(path);
        db.SaveTelemetry([Sample("grid_import",0,1),Sample("grid_import",5,2),Sample("grid_import",10,3),Sample("grid_export",0,1),Sample("grid_export",10,2),Sample("import_tariff",0,25),Sample("export_tariff",0,15)]);
        var summary=db.ReadEnergySummary(At,At.AddMinutes(10));Assert.Equal(.35,summary.ObservedNetCostGbp!.Value,6);Assert.Equal(600,summary.CostObservedSeconds);Assert.Equal(1,summary.CostCoverageFraction);
    }
    [Fact] public async Task DemoCollectorSeedsBoundedRealisticTelemetryAndLiveCannotSeedIt()
    {
        using var db=new DataStore(path);
        var config=new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build();var options=new HomeAssistantOptions(config);
        var service=new TelemetryCollectionService(db,new HomeAssistantClient(new HttpClient(),options),options,config);
        await service.CollectAsync(default);
        var status=service.Status();Assert.True(status.Demo);Assert.NotNull(status.LastCollection);Assert.Null(status.Error);Assert.True((status.LatestReadings["soc"].Value??status.LatestReadings["soc"].LastObservedValue) is >0 and <100);
        var now=DateTimeOffset.UtcNow;var summary=db.ReadEnergySummary(now.AddDays(-2),now.AddDays(-1));Assert.True(summary.CostCoverageFraction>.99);Assert.NotNull(summary.ObservedNetCostGbp);Assert.All(summary.Sources,s=>Assert.StartsWith("Demo",s));
        var livePath=Path.Combine(path,"live");using var liveDb=new DataStore(livePath);
        var liveConfig=new Microsoft.Extensions.Configuration.ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{["App:Demo"]="false"}).Build();var liveOptions=new HomeAssistantOptions(liveConfig);
        var live=new TelemetryCollectionService(liveDb,new HomeAssistantClient(new HttpClient(),liveOptions),liveOptions,liveConfig);
        await Assert.ThrowsAsync<DomainException>(()=>live.CollectAsync(default));Assert.Empty(liveDb.ReadLatestTelemetry());
    }
    public void Dispose(){ if(Directory.Exists(path)) Directory.Delete(path,true); }
}
