namespace Joule;

public record ObservedMeterTrendPoint(string Metric,DateTimeOffset Start,DateTimeOffset End,double? AverageKw,string Source,string? EntityId,string Status);

/// <summary>
/// A compact power series for one meter: average kW per fixed step (30 minutes) from <see cref="Start"/>, with a status per step
/// (measured, idle, estimated, missing, pending). PeakKw is the highest measured step average, which is what a household actually drew;
/// short meter intervals cannot inflate it. Estimated (spread) steps are drawn as ≈ and never set the peak.
/// </summary>
public record MeterPowerSeries(string Metric,string? EntityId,DateTimeOffset Start,int StepMinutes,double?[] Kw,string[] Status,double? PeakKw,DateTimeOffset? PeakAt);

public record ObservedMeterTrends(DateTimeOffset From,DateTimeOffset To,List<ObservedMeterTrendPoint> Intervals,bool Truncated,int Limit,string Method)
{
    /// <summary>Half-hour power series for load, pv, ev and (when the load meter includes EV charging) home.</summary>
    public List<MeterPowerSeries> Series { get; init; } = [];
}

public partial class DataStore
{
    /// <param name="compact">When true, only the half-hour series are returned (a few KB); the per-interval list is left empty.</param>
    public ObservedMeterTrends ReadObservedMeterTrends(DateTimeOffset from,DateTimeOffset to,bool compact=false,int stepMinutes=30)
    {
        if(to<=from || to-from>TimeSpan.FromDays(7))throw new DomainException("Choose a positive meter-trend period of at most seven days.",400);
        if(stepMinutes is not (15 or 30 or 60))throw new DomainException("Choose a trend step of 15, 30 or 60 minutes.",400);
        const int limit=1000;
        var now=Clock.GetUtcNow();
        var completedThrough=to<now?to:now;
        lock(gate)
        {
            var points=new List<ObservedMeterTrendPoint>();var truncated=false;
            if(!compact)
            {
                using var cmd=Command("""
                    SELECT i.metric,i.start_time,i.end_time,i.energy_kwh,i.source,i.status,s.entity_id
                    FROM telemetry_intervals i LEFT JOIN telemetry_samples s ON s.metric=i.metric AND s.time=i.start_time
                    WHERE i.metric IN ('load','pv') AND i.start_time>=? AND i.end_time<=?
                    ORDER BY i.end_time DESC,i.metric ASC,i.start_time DESC LIMIT ?
                    """,from,completedThrough,limit+1);
                using var reader=cmd.ExecuteReader();
                while(reader.Read())
                {
                    var start=Stamp(reader.GetValue(1));var end=Stamp(reader.GetValue(2));var status=reader.GetString(5);
                    double? average=null;
                    if(status=="observed" && !reader.IsDBNull(3))
                    {
                        var energy=reader.GetDouble(3);var span=(end-start).TotalHours;
                        var derived=span>0?energy/span:double.NaN;
                        if(energy>=0 && double.IsFinite(derived))average=derived;
                        else status="invalid_numeric_range";
                    }
                    points.Add(new(reader.GetString(0),start,end,average,reader.GetString(4),reader.IsDBNull(6)?null:reader.GetString(6),status));
                }
                truncated=points.Count>limit;if(truncated)points.RemoveAt(limit);
            }
            var step=TimeSpan.FromMinutes(stepMinutes);
            var first=new DateTimeOffset(from.UtcDateTime.Ticks/step.Ticks*step.Ticks,TimeSpan.Zero);if(first<from)first+=step;
            var intervals=LoadIntervals(first,to,["load","pv","ev"]);
            var byMetric=new[]{"load","pv","ev"}.ToDictionary(m=>m,m=>intervals.Where(x=>x.Metric==m).ToList());
            var includesEv=byMetric["ev"].Count>0?LoadIncludesEv():false;
            var series=new List<MeterPowerSeries>();
            var bins=new List<DateTimeOffset>();for(var t=first;t+step<=to;t+=step)bins.Add(t);
            var hours=step.TotalHours;
            MeterPowerSeries Build(string metric,Func<DateTimeOffset,(double? Value,string Status)> at)
            {
                var kw=new double?[bins.Count];var status=new string[bins.Count];double? peak=null;DateTimeOffset? peakAt=null;
                for(var i=0;i<bins.Count;i++)
                {
                    if(bins[i]+step>completedThrough){status[i]="pending";continue;}
                    var (value,state)=at(bins[i]);status[i]=state;
                    if(value is {} v && double.IsFinite(v)){kw[i]=Math.Round(v/hours,3);if(state is "measured" or "idle" && (peak is null || kw[i]>peak)){peak=kw[i];peakAt=bins[i];}}
                }
                return new(metric,LatestSample(metric)?.EntityId,first,stepMinutes,kw,status,peak,peakAt);
            }
            foreach(var metric in new[]{"load","pv","ev"})
            {
                if(byMetric[metric].Count==0 && LatestSample(metric) is null)continue;
                series.Add(Build(metric,t=>{var a=Allocate(byMetric[metric],t,t+step);return (a.Value,a.Status);}));
            }
            if(includesEv==true)series.Add(Build("home",t=>
            {
                var l=Allocate(byMetric["load"],t,t+step);var e=Allocate(byMetric["ev"],t,t+step);
                if(l.Value is not {} lv || e.Value is not {} ev)return (null,"missing");
                return (Math.Max(0,lv-ev),l.Status=="estimated"||e.Status=="estimated"?"estimated":"measured");
            }));
            return new(from,to,points.OrderBy(p=>p.Start).ThenBy(p=>p.Metric,StringComparer.Ordinal).ThenBy(p=>p.End).ToList(),truncated,limit,
                "Series: average kW per half hour from the meters' cumulative counters (spread intervals marked estimated, never the peak; peak is the highest measured half-hour average). Intervals (omitted when compact): average kW for each whole observed meter interval, only completed intervals within the period, no interpolation; invalid, reset, gap and spread intervals have null average power. The latest 1000 combined load/PV intervals are retained if truncated, in chronological order. These averages do not change authoritative energy totals or forecast comparisons.")
            {Series=series};
        }
    }
}
