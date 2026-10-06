using System.Text.Json;

namespace Joule;
public record AlternativeForecastSlot(DateTimeOffset Time,int DurationMinutes,double PredbatKwh,double AlternativeKwh,double? ActualKwh);
public record AlternativeForecastComparison(bool Available,string Reason,string Methodology,string? EntityId,string? Source,DateTimeOffset? CapturedAt,List<AlternativeForecastSlot> Slots,int MatchedSlots,double? PredbatMaeKwhPerHalfHour,double? AlternativeMaeKwhPerHalfHour)
{
    public string Status { get; init; } = "unknown";
    public bool NativeLoadMl { get; init; }
}
public static class AlternativeForecastService
{
    const string Method="Uses alternative evidence available by the app's first collection of the selected plan. Predbat generation and app collection times are distinct. Native Predbat LoadML is read from retained Predbat state and converted from cumulative kWh only at exact plan boundaries; resets and missing boundaries are excluded, with no interpolation. Native LoadML can exclude EV charging and may already drive the selected plan, so its curve alone cannot establish an independent same-target accuracy comparison. Explicit interval-energy sensors are scored only against complete measured actuals captured after their intervals; MAE is normalized to kWh per half-hour on the same intervals. No model is trained, no forecast is switched, and no financial savings are established.";
    public static AlternativeForecastComparison Compare(DataStore db,string planId)
    {
        var plan=db.GetPlan(planId)??throw new DomainException("Plan not found.",404);
        var sample=plan.CollectedAt is {} captured?db.ReadAlternativeForecastAt(captured):null;
        if(sample is null && plan.Source=="Predbat" && plan.CollectedAt is not null)sample=db.ReadNativeLoadMlAt(plan);
        var native=sample is not null && (IsNativeLoadMl(sample.EntityId)||sample.Source=="Predbat retained state");
        AlternativeForecastComparison Missing(string why,string status="invalid_data")=>new(false,why,Method,sample?.EntityId,sample?.Source,sample?.Time,[],0,null,null){Status=status,NativeLoadMl=native};
        if(plan.CollectedAt is null)return Missing("The app collection timestamp of this historical plan is unknown. A frozen pre-slot comparison cannot be established.","unknown_capture");
        if(sample is null)return Missing("Predbat has not published a LoadML forecast in the evidence captured for this plan. LoadML is optional: enable load_ml_enable in Predbat to train it using the existing load_today history, then collect a new plan. An explicitly mapped alternative sensor can also be used.","not_published");
        if(sample.Status is "invalid_source" or "ambiguous_source")return Missing(sample.RawState,sample.Status);
        if(sample.Status!="observed")return Missing("The alternative forecast source was unavailable when collected.","source_unavailable");
        if(plan.CollectedAt-sample.Time>TimeSpan.FromDays(1))return Missing("The alternative forecast snapshot is stale: it is more than a day older than this plan.","stale");
        try
        {
            using var json=JsonDocument.Parse(sample.AttributesJson);var root=json.RootElement;
            var points=new List<(DateTimeOffset Time,int Duration,double Energy)>();
            if(native)
            {
                if(sample.RawState!="active")return Missing($"Predbat LoadML reports {sample.RawState}. Its model is not ready to publish a usable comparison yet.",sample.RawState is "not_initialized" or "training" or "insufficient_data"?"warmup":sample.RawState.Contains("stale",StringComparison.OrdinalIgnoreCase)?"stale":"model_unavailable");
                if(sample.SourceUpdatedAt is {} updated && (updated>sample.Time || sample.Time-updated>TimeSpan.FromHours(2)))return Missing("The native Predbat LoadML publish timestamp is stale or later than collection. Collect a freshly published forecast.","stale");
                var entries=root.GetProperty("results");
                if(entries.ValueKind!=JsonValueKind.Object || entries.EnumerateObject().Count() is <2 or >10000)return Missing("Predbat LoadML has no usable future cumulative forecast boundaries.","missing_future_intervals");
                var cumulative=new SortedDictionary<DateTimeOffset,double>();
                foreach(var entry in entries.EnumerateObject())
                {
                    if(!ForecastTime(entry.Name,out var stamp)||!entry.Value.TryGetDouble(out var energy)||!double.IsFinite(energy)||energy<0||stamp>sample.Time.AddDays(7)||!cumulative.TryAdd(stamp,energy))return Missing("Native Predbat LoadML contains invalid cumulative times or energy values.");
                }
                foreach(var slot in plan.Slots.Where(s=>s.DurationMinutes is >0 and <=1440))
                {
                    var end=slot.Time.AddMinutes(slot.DurationMinutes);
                    if(!cumulative.TryGetValue(slot.Time,out var boundaryStart)||!cumulative.TryGetValue(end,out var boundaryEnd)||boundaryEnd<boundaryStart)continue;
                    var within=cumulative.Where(p=>p.Key>=slot.Time && p.Key<=end).ToList();
                    if(within.Zip(within.Skip(1)).Any(pair=>pair.Second.Value<pair.First.Value))continue;
                    points.Add((slot.Time,slot.DurationMinutes,boundaryEnd-boundaryStart));
                }
            }
            else
            {
                if(root.GetProperty("forecast_unit").GetString()!="kWh" || root.GetProperty("forecast_kind").GetString()!="interval_energy")return Missing("The alternative sensor must explicitly declare forecast_unit=kWh and forecast_kind=interval_energy. Power and arbitrary cumulative readings are not interchangeable.","unsupported_source");
                var entries=root.GetProperty("forecast");
                if(entries.ValueKind!=JsonValueKind.Array || entries.GetArrayLength() is <1 or >2048)return Missing("The alternative forecast must contain 1–2048 explicit intervals.");
                foreach(var entry in entries.EnumerateArray())
                {
                    var time=entry.GetProperty("time");
                    if(time.ValueKind!=JsonValueKind.String || !ForecastTime(time.GetString()!,out var stamp) || !entry.GetProperty("duration_minutes").TryGetInt32(out var duration) || duration is <1 or >1440 || !entry.GetProperty("load_kwh").TryGetDouble(out var energy) || !double.IsFinite(energy) || energy<0 || stamp>sample.Time.AddDays(7))return Missing("Alternative forecast intervals have invalid times, durations or energy values.");
                    points.Add((stamp,duration,energy));
                }
            }
            points=points.OrderBy(p=>p.Time).ToList();
            for(var i=1;i<points.Count;i++)if(points[i-1].Time.AddMinutes(points[i-1].Duration)>points[i].Time)return Missing("Alternative forecast intervals overlap or repeat.");
            var slots=new List<AlternativeForecastSlot>();
            foreach(var slot in plan.Slots.Where(s=>s.Time>=plan.At && s.Time>=plan.CollectedAt && s.Time>=sample.Time && s.DurationMinutes>0).OrderBy(s=>s.Time))
            {
                var point=points.FindIndex(p=>p.Time==slot.Time && p.Duration==slot.DurationMinutes);
                if(point<0)continue;
                if(!double.IsFinite(slot.LoadForecast)||slot.LoadForecast<0)return Missing("The Predbat forecast contains invalid energy.");
                // This plan was captured before the slot, so an actual embedded in its
                // frozen payload cannot establish a completed measurement. Resolve
                // authoritative observations collected after the target interval.
                var observed=native?null:db.ReadCompleteObservedLoad(slot.Time,slot.DurationMinutes);
                var actual=observed is {} n && double.IsFinite(n) && n>=0 ? observed : null;
                slots.Add(new(slot.Time,slot.DurationMinutes,slot.LoadForecast,points[point].Energy,actual));
            }
            if(slots.Count==0)return Missing(native?"Predbat LoadML has no exact future plan boundaries without a cumulative reset. Collect a new plan after its next forecast publication.":"No exact common forecast intervals are available. The sensor must provide interval starts and durations matching the Predbat plan.","missing_future_intervals");
            for(var i=1;i<slots.Count;i++)if(slots[i-1].Time.AddMinutes(slots[i-1].DurationMinutes)>slots[i].Time)return Missing("Predbat forecast intervals overlap or repeat.");
            var measured=slots.Where(s=>s.ActualKwh!=null).ToList();var minutes=measured.Sum(s=>(double)s.DurationMinutes);
            double? Score(Func<AlternativeForecastSlot,double> forecast)=>minutes>0 ? measured.Sum(s=>Math.Abs(forecast(s)-s.ActualKwh!.Value)*(30/minutes)) : null;
            var predbat=Score(s=>s.PredbatKwh);var alternative=Score(s=>s.AlternativeKwh);
            if(predbat is {} a&&!double.IsFinite(a)||alternative is {} b&&!double.IsFinite(b))return Missing("Forecast comparison exceeds the supported numeric range.");
            return new(true,native?"The native Predbat LoadML curve is available. Accuracy scoring is unavailable until its load/EV target scope is verified; it may exclude EV charging or already supply the selected plan's forecast.":measured.Count>0?"Both forecasts scored on the same complete observed intervals.":"Comparable forecasts are available; no common complete actual intervals are available yet.",Method,sample.EntityId,sample.Source,sample.Time,slots,measured.Count,predbat,alternative){Status=native?"scope_unverified":measured.Count>0?"scored":"awaiting_actuals",NativeLoadMl=native};
        }
        catch(Exception ex) when(ex is JsonException or KeyNotFoundException or InvalidOperationException or ArgumentOutOfRangeException)
        {return Missing("The sensor attributes do not match the documented alternative forecast contract. Raw attributes remain available to investigations.");}
    }
    internal static bool IsNativeLoadMl(string id)=>System.Text.RegularExpressions.Regex.IsMatch(id,@"^sensor\.[a-z0-9_]+_load_ml_forecast$");
    static bool ForecastTime(string text,out DateTimeOffset stamp)
    {
        text=System.Text.RegularExpressions.Regex.Replace(text,@"([+-]\d{2})(\d{2})$","$1:$2");
        stamp=default;
        return System.Text.RegularExpressions.Regex.IsMatch(text,@"(?:Z|[+-]\d{2}:\d{2})$",System.Text.RegularExpressions.RegexOptions.IgnoreCase)&&DateTimeOffset.TryParse(text,System.Globalization.CultureInfo.InvariantCulture,System.Globalization.DateTimeStyles.None,out stamp);
    }
}
public partial class DataStore
{
    internal TelemetrySample? ReadNativeLoadMlAt(PlanSnapshot plan)
    {
        lock(gate)
        {
            // PredbatClient fetches state before stamping plan receipt. Its associated
            // source snapshot therefore existed by CollectedAt, even though disk writes follow it.
            using var cmd=Command("SELECT id,recorded_at,state_json,state_gz FROM source_snapshots WHERE id=? OR recorded_at<=? ORDER BY CASE WHEN id=? THEN 0 ELSE 1 END,recorded_at DESC LIMIT 1",plan.Id,plan.CollectedAt,plan.Id);
            using var reader=cmd.ExecuteReader();if(!reader.Read())return null;
            var captured=reader.GetString(0)==plan.Id?plan.CollectedAt!.Value:Stamp(reader.GetValue(1));
            TelemetrySample Invalid(string reason,string status="invalid_source")=>new("alternative_forecast","",captured,null,"","Predbat retained state",reason,"",Status:status);
            try
            {
                using var json=JsonDocument.Parse(SnapshotText(reader,2,3));
                if(json.RootElement.ValueKind!=JsonValueKind.Object)return Invalid("The retained Predbat state is not a valid entity object. Collect a new plan.");
                var entities=json.RootElement.EnumerateObject().Where(e=>AlternativeForecastService.IsNativeLoadMl(e.Name)).ToList();
                if(entities.Count==0)return null;
                if(entities.Count>1)return Invalid("Multiple native Predbat LoadML forecasts were captured. The source is ambiguous; choose an explicit instance rather than comparing an inferred one.","ambiguous_source");
                var entity=entities[0];if(entity.Value.ValueKind!=JsonValueKind.Object||!entity.Value.TryGetProperty("attributes",out var attrs)||attrs.ValueKind!=JsonValueKind.Object)return Invalid("The retained native LoadML entity has invalid attributes. Collect a new plan.");
                var raw=entity.Value.TryGetProperty("state",out var state)?state.ToString():"unknown";
                DateTimeOffset? updated=entity.Value.TryGetProperty("last_updated",out var update)&&DateTimeOffset.TryParse(update.ToString(),out var at)?at:null;
                return new("alternative_forecast",entity.Name,captured,null,"kWh","Predbat retained state",raw,"kWh",updated,attrs.GetRawText(),raw is "unknown" or "unavailable"?"unavailable":"observed");
            }
            catch(JsonException){return Invalid("The retained Predbat state is malformed. The source cannot be compared; collect a new plan.");}
        }
    }
    internal double? ReadCompleteObservedLoad(DateTimeOffset start,int durationMinutes)
    {
        var end=start.AddMinutes(durationMinutes);
        if(end>DateTimeOffset.UtcNow)return null;
        lock(gate)
        {
            var measured=ReadEnergySummary(start,end).Metrics["load"];
            return measured.CoverageFraction>=1-1e-8?measured.EnergyKwh:null;
        }
    }
    public TelemetrySample? ReadAlternativeForecastAt(DateTimeOffset cutoff)
    {
        lock(gate)
        {
            using var cmd=Command("SELECT time FROM telemetry_samples WHERE metric='alternative_forecast' AND time<=? ORDER BY time DESC LIMIT 1",cutoff);
            var result=cmd.ExecuteScalar();if(result is null || result is DBNull)return null;
            var at=Stamp(result);return SamplesInternal(at,at.AddMicroseconds(1),"alternative_forecast",0,1).FirstOrDefault();
        }
    }
}
