namespace Joule;

public sealed class ExperimentEvaluator(StateService state, DataStore db)
{
    public async Task EvaluateAsync(CancellationToken ct)
    {
        var now=DateTimeOffset.UtcNow;
        foreach (var experiment in state.Read(false).Experiments.Where(ChangeEngine.IsOpen))
        {
            if(experiment.FileVersionId!=null || experiment.Seeded)continue;
            var snapshot=state.Read(false); var revision=snapshot.Revisions.FirstOrDefault(x=>x.Id==experiment.RevisionId);
            if(revision==null)continue;
            // Trials only measure tunable settings; legacy trials for Predbat's own controls are closed on upgrade.
            if(revision.Changes.Count>0 && revision.Changes.All(c=>!PredbatSettingsCatalogue.IsTunable(c.Key)))continue;
            var duration=now-experiment.StartedAt;if(duration<=TimeSpan.Zero)continue;if(duration>TimeSpan.FromDays(7))duration=TimeSpan.FromDays(7);
            var end=experiment.StartedAt+duration;var from=experiment.StartedAt-duration;
            var key=revision.Changes.Count==1?revision.Changes[0].Key:"";
            var metric=key=="load_scaling"?"load":key=="pv_scaling"?"pv":null;
            var baseline=metric==null?null:Measure(metric,from,experiment.StartedAt);
            var after=metric==null?null:Measure(metric,experiment.StartedAt,end,forecastNotBefore:experiment.StartedAt);
            var beforeCost=db.ReadEnergySummary(from,experiment.StartedAt);var afterCost=db.ReadEnergySummary(experiment.StartedAt,end);
            var confounders=CompareContext(beforeCost,afterCost);
            if(baseline!=null && after!=null && !ComparableTemporalSupport(baseline,after))confounders.Add("Forecast scoring observation durations changed by more than 10%; different meter cadence may confound accuracy.");
            var baselineCoverage=(baseline?.CoveredSeconds ?? 0)/duration.TotalSeconds;
            var afterCoverage=(after?.CoveredSeconds ?? 0)/duration.TotalSeconds;
            var forecastBad=ShouldRollback(baseline,after,duration) && baselineCoverage>=.95 && afterCoverage>=.95;
            var financiallyWorse=ShouldFinancialRollback(baseline,after,duration,beforeCost,afterCost,confounders.Count==0) && baselineCoverage>=.95 && afterCoverage>=.95;
            await state.MutateAsync(s=>
            {
                var current=s.Experiments.First(x=>x.Id==experiment.Id);if(!ChangeEngine.IsOpen(current))return;
                // Recheck control history under the mutation gate: it may have changed
                // while the measured comparison was being calculated.
                // Catalogue-only revisions have no reversible value delta but still
                // change the baseline. The initial observation alone is not a change.
                if(s.Revisions.Any(x=>x.Id<current.RevisionId && (x.Changes.Count>0 || x.Id!=s.Revisions[0].Id) && x.At>=from && x.At<current.StartedAt))confounders.Add("Configuration changed during the matched baseline window.");
                if(s.Experiments.Any(x=>x.FileVersionId!=null && x.StartedAt>=from && x.StartedAt<current.StartedAt))confounders.Add("Mounted configuration files changed during the matched baseline window.");
                if(s.Revisions.Any(x=>x.Id>current.RevisionId))confounders.Add("Configuration changed during the experiment.");
                if(s.Experiments.Any(x=>x.FileVersionId!=null && x.StartedAt>=current.StartedAt))confounders.Add("Mounted configuration files changed during the experiment.");
                current.ForecastMethod=Experiment.CurrentForecastMethod;
                current.ForecastEvidenceVersion=Experiment.CurrentForecastEvidenceVersion;
                current.BaselineError=baseline?.Error;current.CurrentError=after?.Error;
                current.BaselineForecastCoverage=Math.Min(1,baselineCoverage);current.CurrentForecastCoverage=Math.Min(1,afterCoverage);
                current.BaselineCostCoverage=beforeCost.CostCoverageFraction;current.CurrentCostCoverage=afterCost.CostCoverageFraction;
                current.BaselineCostGbpPerDay=DailyCost(beforeCost);
                current.CurrentCostGbpPerDay=DailyCost(afterCost);
                current.Confounders=confounders;
                var check=ChangeEngine.RollbackEligibility(s,current);
                if(confounders.Count>0){current.Status="Needs review";current.Result="Confounders prevent attributing this change or automatic rollback: "+string.Join(" ",confounders);}
                else if(financiallyWorse && check.AutomaticEligible)
                {
                    ChangeEngine.Revert(s,current.RevisionId,s.Revision,true);
                    current.Result="Covered matched windows show forecast MAE and observed daily net cost deteriorating. Current automatic permissions allowed a revert. This observational comparison does not prove causal savings.";
                }
                else if(forecastBad)
                {
                    current.Status="Needs review";
                    current.Result=current.CurrentCostGbpPerDay is {} currentDaily && current.BaselineCostGbpPerDay is {} baselineDaily && currentDaily<=baselineDaily
                        ?"Forecast error worsened while observed net cost improved or stayed flat. Review the tradeoff; no automatic revert is justified."
                        :"Forecast error worsened, but covered financial deterioration and current rollback eligibility are required. Review the evidence before reverting.";
                }
                else if(now>=current.ReviewAt){current.Status="Inconclusive";current.Result="The review period ended. Review covered costs, forecast metrics and confounders, then keep, extend, close or revert. No causal savings are claimed.";}
                else current.Result="Collecting matched forecast and measured cost evidence. Missing coverage remains unavailable; no causal savings are claimed.";
            },ct);
        }
    }
    internal static List<string> CompareContext(EnergySummary before,EnergySummary after)
    {
        var issues=new List<string>();
        foreach(var key in new[]{"load","pv","ev"})
        {
            if(!before.Metrics.TryGetValue(key,out var b)||!after.Metrics.TryGetValue(key,out var a)||b.CoverageFraction<.9||a.CoverageFraction<.9||b.EnergyKwh==null||a.EnergyKwh==null){issues.Add($"{key} context has insufficient coverage.");continue;}
            var daysBefore=(before.To-before.From).TotalDays;var daysAfter=(after.To-after.From).TotalDays;
            var x=b.EnergyKwh.Value/daysBefore;var y=a.EnergyKwh.Value/daysAfter;
            if(Math.Abs(y-x)>Math.Max(.5,Math.Abs(x)*.25))issues.Add($"{key} changed by more than 25% (weather, load or EV schedule may confound cost).");
        }
        if(DailyCost(before)==null || DailyCost(after)==null || Math.Abs(before.CostCoverageFraction-after.CostCoverageFraction)>.005)issues.Add("Financial coverage or normalized cost is unavailable or unmatched: require finite daily costs, at least 99%, no invalid intervals, at most 30 missing minutes and coverage within 0.5 percentage points.");
        foreach(var flow in new[]{"import","export"})
        {
            var key="grid_"+flow;
            if(!before.Metrics.TryGetValue(key,out var b) || !after.Metrics.TryGetValue(key,out var a) || b.EnergyKwh is null || a.EnergyKwh is null || b.CoverageFraction<.99 || a.CoverageFraction<.99)
            {issues.Add($"Effective {flow} tariff context has insufficient measured coverage.");continue;}
            // A zero flow in both windows has no tariff contribution to net cost.
            if(b.EnergyKwh==0 && a.EnergyKwh==0)continue;
            var bc=flow=="import"?before.ImportCostGbp:before.ExportCreditGbp;
            var ac=flow=="import"?after.ImportCostGbp:after.ExportCreditGbp;
            if(b.EnergyKwh<=0 || a.EnergyKwh<=0 || bc is null || ac is null)
            {issues.Add($"Effective {flow} tariff context is unavailable for a comparable nonzero flow.");continue;}
            var x=bc.Value/b.EnergyKwh.Value;var y=ac.Value/a.EnergyKwh.Value;
            if(Math.Abs(y-x)>Math.Max(.01,Math.Abs(x)*.1))issues.Add($"Effective {flow} tariff changed by more than 10%; tariff or Intelligent slot mix may confound cost.");
        }
        return issues;
    }
    internal static bool CostUsable(EnergySummary s) => TelemetryCostEvidence.Usable(s);
    internal static double? DailyCost(EnergySummary summary)
    {
        if(!CostUsable(summary) || !double.IsFinite(summary.CostObservedSeconds) || summary.ObservedNetCostGbp is not {} cost || !double.IsFinite(cost))return null;
        // Divide the normalization factor first; cost * 86400 can overflow even
        // when the resulting per-day value is finite.
        var daily=cost*(86400/summary.CostObservedSeconds);
        return double.IsFinite(daily)?daily:null;
    }
    public static bool ShouldFinancialRollback(Measurement? before,Measurement? after,TimeSpan elapsed,EnergySummary baseline,EnergySummary current,bool contextMatched)
        => contextMatched && ShouldRollback(before,after,elapsed) && Math.Abs(baseline.CostCoverageFraction-current.CostCoverageFraction)<=.005 && DailyCost(baseline) is {} b && DailyCost(current) is {} a &&
            a > b + Math.Max(.1,Math.Abs(b)*.1);
    public record Measurement(double Error,long Slots,double? ObservedSeconds=null,double? MedianScoredSeconds=null,double? MaximumScoredSeconds=null)
    {
        public double CoveredSeconds=>ObservedSeconds??Slots*1800;
    }
    public static bool ComparableTemporalSupport(Measurement before,Measurement after)
    {
        static bool Close(double a,double b)=>double.IsFinite(a) && double.IsFinite(b) && a>0 && b>0 && Math.Abs(a-b)<=Math.Min(a,b)*.1+1;
        return Close(before.MedianScoredSeconds??1800,after.MedianScoredSeconds??1800) && Close(before.MaximumScoredSeconds??1800,after.MaximumScoredSeconds??1800);
    }
    public static bool ShouldRollback(Measurement? before, Measurement? after, TimeSpan elapsed) => elapsed >= TimeSpan.FromDays(3) && before is { Slots: >= 48, Error: > 0.001 } && after is { Slots: >= 48 } && ComparableTemporalSupport(before,after) && after.Error > before.Error * 1.25;
    internal Measurement? Measure(string metric, DateTimeOffset from, DateTimeOffset to, DateTimeOffset? forecastNotBefore = null)
    {
        var rows=db.ReadMatchedForecastEstimates(metric,from,to,forecastNotBefore).Where(x=>x.DurationSeconds>0).ToList();
        // Compare near-half-hour observations built from consecutive whole meter
        // intervals. A small undershoot admits ordinary receipt-time jitter; the
        // first endpoint past the target overshoots by at most one valid interval.
        // Both windows must have comparable median and maximum scored duration.
        double seconds=0,forecast=0,actual=0,error=0,covered=0;DateTimeOffset? end=null;var durations=new List<double>();
        foreach(var row in rows)
        {
            if(!double.IsFinite(row.Forecast) || !double.IsFinite(row.Actual) || !double.IsFinite(row.DurationSeconds))return null;
            if(end!=row.Time || row.DurationSeconds>1801){seconds=0;forecast=0;actual=0;}
            end=row.Time.AddSeconds(row.DurationSeconds);
            if(row.DurationSeconds>1801)continue;
            seconds+=row.DurationSeconds;forecast+=row.Forecast;actual+=row.Actual;
            if(!double.IsFinite(forecast) || !double.IsFinite(actual) || !double.IsFinite(seconds))return null;
            if(seconds>=1770 && seconds<=1800+row.DurationSeconds)
            {
                error+=Math.Abs(forecast-actual);covered+=seconds;durations.Add(seconds);
                if(!double.IsFinite(error) || !double.IsFinite(covered))return null;
                seconds=0;forecast=0;actual=0;
            }
        }
        durations.Sort();var count=durations.Count;
        var median=count==0?0:count%2==1?durations[count/2]:(durations[count/2-1]+durations[count/2])/2;
        var normalizedError=count==0?0:error*(1800/covered);
        return count==0 || !double.IsFinite(normalizedError)?null:new(normalizedError,count,covered,median,durations[^1]);
    }
}
