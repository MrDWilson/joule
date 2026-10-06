using System.Globalization;

namespace Joule;

/// <summary>Collector status. LatestReadings keep their values however old (see LatestTelemetry.AgeSeconds/Stale/Expected/Reason).
/// Profiles gives each energy meter's sensor profile; Issues lists the unexpected problems behind Error, one per sensor.</summary>
public record TelemetryStatus(bool Demo,bool Configured,string TimeZone,DateTimeOffset? LastCollection,string? Error,Dictionary<string,string> EntityMappings,string[] MissingMappings,double MaxGapMinutes,Dictionary<string,LatestTelemetry> LatestReadings,bool HomeAssistantDirect=false,string? LastSource=null,DateTimeOffset? FirstObservationAt=null)
{
    public Dictionary<string,string> Profiles { get; init; } = [];
    public List<TelemetryIssue> Issues { get; init; } = [];
    public bool? LoadIncludesEv { get; init; }
}
/// <summary>An unexpected sensor problem: metric, plain-English message, and since when.</summary>
public record TelemetryIssue(string Metric,string Message,DateTimeOffset? Since);

public sealed class TelemetryCollectionService
{
    /// <summary>Sensors that are legitimately idle or absent for long periods (an EV charger between sessions, optional forecast feeds). Their gaps are recorded per metric but never fail a collection.</summary>
    public static readonly string[] OptionalMetrics=["ev","intelligent_slots","alternative_forecast"];
    /// <summary>An unexpected outage becomes a collection error only after this long: brief Home Assistant restarts are normal.</summary>
    public static readonly TimeSpan CounterOutageGrace=TimeSpan.FromMinutes(30),StateOutageGrace=TimeSpan.FromMinutes(15);
    readonly DataStore db;readonly HomeAssistantClient client;readonly HomeAssistantOptions options;
    readonly SemaphoreSlim collecting=new(1,1);
    readonly bool demo;
    DateTimeOffset? last;string? error;string? lastSource;bool intervalRulesChecked;List<TelemetryIssue> issues=[];
    public TelemetryCollectionService(DataStore db,HomeAssistantClient client,HomeAssistantOptions options,IConfiguration config)
    {
        this.db=db;this.client=client;this.options=options;demo=config.GetValue("App:Demo",true);
        db.ConfigureTelemetry(options.TelemetrySettings);
    }
    public TelemetryStatus Status()=>new(demo,client.Configured,options.TimeZone,last,error,new(options.Entities),TelemetrySchema.EnergyMetrics.Concat(["soc","import_tariff","export_tariff"]).Where(m=>!options.Entities.ContainsKey(m)).ToArray(),options.MaxGap.TotalMinutes,db.ReadLatestTelemetry(options.MaxGap),options.DirectConfigured,lastSource,db.ReadFirstObservationAt())
    {
        Profiles=db.ReadSensorProfiles(),Issues=issues,LoadIncludesEv=db.LoadIncludesEv()
    };
    public async Task CollectAsync(CancellationToken ct)
    {
        await collecting.WaitAsync(ct);
        try
        {
            if(!intervalRulesChecked){db.EnsureIntervalRules(options.MaxGap);intervalRulesChecked=true;}
            if(demo)DemoTelemetry.Seed(db);
            else
            {
                if(!client.Configured)throw new DomainException("Configure explicit Home Assistant entity mappings, plus either the Home Assistant URL and server token or the Predbat URL for mirrored readings.",503);
                var readings=await client.CollectAsync(ct);lastSource=readings.FirstOrDefault()?.Source;db.SaveTelemetry(readings,options.MaxGap,alignToSource:true);
                // Readings are persisted and the poll recorded before per-sensor health is judged, so partial data and LastCollection survive a failing sensor.
                last=DateTimeOffset.UtcNow;
                if(client.LastReadFailed){issues=[new("all","Home Assistant could not be read.",null)];throw new DomainException(MissingReadingsMessage(readings,lastSource),502);}
                issues=Problems(db,readings);
                if(issues.Count>0)throw new DomainException($"Sensors without a usable reading: {string.Join(", ",issues.Select(x=>$"{x.Metric} ({x.Message})"))}. Other sensors continue collecting (source: {lastSource??"none"}).",502);
            }
            last=DateTimeOffset.UtcNow;error=null;
        }
        catch(Exception e) when(e is not OperationCanceledException || !ct.IsCancellationRequested){error=e is DomainException?e.Message:"HA collection failed. Check endpoint, token, units and sensor timestamps.";throw;}
        finally{collecting.Release();}
    }
    /// <summary>
    /// Sensor problems worth raising. A mapping that is wrong (not found, unreadable, unsupported unit) is raised at once. An idle or
    /// offline reading is raised only when it is not normal for the sensor's profile and has lasted longer than the grace period:
    /// 30 minutes for energy counters, 15 for battery level and tariffs. Optional sensors (EV, forecasts) never fail a collection.
    /// </summary>
    public static List<TelemetryIssue> Problems(DataStore db,IEnumerable<TelemetrySample> readings)
    {
        var result=new List<TelemetryIssue>();
        foreach(var sample in readings.Where(x=>x.Status!="observed" && !OptionalMetrics.Contains(x.Metric)))
        {
            if(sample.Status is "not_found" or "invalid" or "unsupported_unit"){result.Add(new(sample.Metric,Reason(sample),sample.Time));continue;}
            if(db.OutageExpected(sample.Metric))continue;
            var duration=db.OutageDuration(sample.Metric)??TimeSpan.Zero;
            var grace=TelemetrySchema.EnergyMetrics.Contains(sample.Metric)?CounterOutageGrace:StateOutageGrace;
            if(duration<grace)continue;
            result.Add(new(sample.Metric,$"{(sample.Status=="idle"?"unknown":"offline")} for {Minutes(duration)}",sample.Time-duration));
        }
        return result;
    }
    static string Minutes(TimeSpan span)=>span.TotalMinutes<90?$"{Math.Round(span.TotalMinutes)} min":$"{span.TotalHours.ToString("0.#",CultureInfo.InvariantCulture)} h";
    /// <summary>Names each sensor without a usable reading and why, e.g. "pv (unavailable)", "ev (unknown)" or "load (unsupported unit W)".</summary>
    public static string MissingReadingsMessage(IEnumerable<TelemetrySample> missing,string? source)=>$"Sensors without a usable reading: {string.Join(", ",missing.Select(x=>$"{x.Metric} ({Reason(x)})"))}. Other sensors continue collecting (source: {source??"none"}).";
    static string Reason(TelemetrySample sample)
    {
        var raw=sample.RawState.Length>60?sample.RawState[..60]+"…":sample.RawState;
        return sample.Status switch{"unsupported_unit"=>$"unsupported unit {sample.RawUnit}","invalid"=>$"invalid state {raw}","not_found"=>"entity not found in Home Assistant: check the mapping",_=>string.IsNullOrEmpty(raw)?sample.Status:raw};
    }
}
public sealed class HomeAssistantTelemetryWorker(TelemetryCollectionService service,HomeAssistantOptions options):BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while(!stoppingToken.IsCancellationRequested)
        {
            try{await service.CollectAsync(stoppingToken);}catch(OperationCanceledException)when(stoppingToken.IsCancellationRequested){break;}catch(Exception){ }
            try{await Task.Delay(options.PollInterval,stoppingToken);}catch(OperationCanceledException){break;}
        }
    }
}
public static class HomeAssistantIntegration
{
    public static IServiceCollection AddHomeAssistantTelemetry(this IServiceCollection services,IConfiguration configuration)
    {
        services.AddSingleton(new HomeAssistantOptions(configuration));
        services.AddHttpClient("homeassistant",c=>c.Timeout=TimeSpan.FromSeconds(25)).ConfigurePrimaryHttpMessageHandler(()=>new HttpClientHandler{AllowAutoRedirect=false});
        services.AddSingleton(sp=>new HomeAssistantClient(sp.GetRequiredService<IHttpClientFactory>().CreateClient("homeassistant"),sp.GetRequiredService<HomeAssistantOptions>(),sp.GetService<IPredbatClient>() as IPredbatEntityReader));
        services.AddSingleton<TelemetryCollectionService>();services.AddHostedService<HomeAssistantTelemetryWorker>();return services;
    }
    public static IEndpointRouteBuilder MapHomeAssistantTelemetry(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/telemetry/status",(TelemetryCollectionService service)=>service.Status());
        app.MapGet("/api/telemetry/plans/{id}/evidence",(string id,DataStore db)=>db.ReadPlanEvidence(id) is {} evidence ? Results.Ok(evidence) : Results.NotFound(new {error="Plan not found."}));
        app.MapGet("/api/telemetry/schema",()=>new {description=TelemetrySchema.Description,tables=TelemetrySchema.Tables});
        app.MapGet("/api/telemetry/summary",(DateTimeOffset from,DateTimeOffset to,DataStore db)=>db.ReadEnergySummary(from,to));
        app.MapGet("/api/telemetry/trends",(DateTimeOffset from,DateTimeOffset to,bool? compact,int? stepMinutes,DataStore db)=>db.ReadObservedMeterTrends(from,to,compact??false,stepMinutes??30));
        app.MapGet("/api/telemetry/history",(DateTimeOffset from,DateTimeOffset to,int? slotMinutes,DataStore db)=>db.ReadMeasuredHistory(from,to,slotMinutes??30));
        app.MapGet("/api/telemetry/daily",(DateTimeOffset from,DateTimeOffset to,DataStore db,HomeAssistantOptions options)=>db.GetDailySummaries(from,to,options.TimeZone));
        app.MapGet("/api/telemetry/samples",(DateTimeOffset from,DateTimeOffset to,string? metric,int? offset,int? limit,DataStore db)=>db.ReadTelemetrySamples(from,to,metric,offset??0,limit??200));
        app.MapGet("/api/telemetry/profiles",(DataStore db)=>new {profiles=db.ReadSensorProfiles(),loadIncludesEv=db.LoadIncludesEv(),descriptions=new Dictionary<string,string>{
            [SensorProfiles.DailyCounter]="Resets at local midnight; unknown before the first reading of the day counts as zero.",
            [SensorProfiles.SolarDaily]="Daily solar counter; unknown counts as zero only when the counter proves it or the sun is down.",
            [SensorProfiles.SessionCounter]="Restarts with each charging session; unknown between sessions counts as zero.",
            [SensorProfiles.LifetimeCounter]="Only rises; unknown is an outage."}});
        app.MapPost("/api/telemetry/collect",async(TelemetryCollectionService service,CancellationToken ct)=>{await service.CollectAsync(ct);return Results.Ok(service.Status());});
        return app;
    }
}

/// <summary>
/// Scripted demo meters driven by the demo plan's household (<see cref="DemoHouse"/>), so battery level, charge, discharge, import,
/// export and prices agree with the plan everywhere. They behave like a real installation: daily counters that reset at local midnight
/// (with last_reset), solar that reads unknown from midnight until the sun is up, grid export that reads unknown until its first export
/// of the day, and an EV charger session counter that reads unknown between sessions (00:30–02:00 every night, and 19:00–20:30 on car
/// evenings). The whole-house load meter includes the car.
/// </summary>
public static class DemoTelemetry
{
    /// <summary>History seeded on first start: 35 days from local midnight, so the 30-day view is full and 7 days has a comparison.</summary>
    public const int SeedDays = 35;
    /// <summary>
    /// Demo meter outages, in local time. Every night a 20-minute dropout across 03:00–03:30, which the counters bridge as an estimated
    /// (≈) half-hour; and once, three days before the history was seeded, a 40-minute outage 15:10–15:50 after the solar peak. Load and
    /// solar report "unavailable" in both; the battery level only in the short one, so the battery card has a reading all day.
    /// </summary>
    public static bool InOutage(DateTimeOffset at, DateTimeOffset seededAt, TimeZoneInfo? zone = null) => ShortOutage(at, zone) || LongOutage(at, seededAt, zone);
    static bool ShortOutage(DateTimeOffset at, TimeZoneInfo? zone)
    {
        var local = TimeZoneInfo.ConvertTime(at, zone ?? DemoHouse.Zone); var minute = local.Hour * 60 + local.Minute;
        return minute is > 3 * 60 and < 3 * 60 + 30;
    }
    static bool LongOutage(DateTimeOffset at, DateTimeOffset seededAt, TimeZoneInfo? zone)
    {
        var local = TimeZoneInfo.ConvertTime(at, zone ?? DemoHouse.Zone); var minute = local.Hour * 60 + local.Minute;
        return DateOnly.FromDateTime(local.Date) == DemoHouse.LocalDate(seededAt, zone).AddDays(-3) && minute is > 15 * 60 + 10 and < 15 * 60 + 50;
    }
    /// <summary>The meters' readings at <paramref name="at"/>: daily counters (kWh since local midnight), the EV session counter
    /// (null between sessions), battery level and prices.</summary>
    public static Dictionary<string, double?> Readings(DateTimeOffset at, TimeZoneInfo? zone = null)
    {
        zone ??= DemoHouse.Zone;
        var date = DemoHouse.LocalDate(at, zone);
        var day = DemoHouse.Measured(date);
        var slots = Math.Min(48, (at - DemoHouse.Midnight(date, zone)).TotalMinutes / 30);
        double Counter(double[] perSlot) => Math.Round(DemoHouse.Day.Counter(perSlot, slots), 4);
        var pv = Counter(day.Pv); var export = Counter(day.Export);
        var local = TimeZoneInfo.ConvertTime(at, zone);
        return new()
        {
            ["load"] = Math.Round(DemoHouse.Day.Counter(day.Load, slots) + DemoHouse.Day.Counter(day.Car, slots), 4),
            ["pv"] = pv > 0 ? pv : null,
            ["grid_import"] = Counter(day.Import),
            ["grid_export"] = export > 0 ? export : null,
            ["battery_charge"] = Counter(day.Charge),
            ["battery_discharge"] = Counter(day.Discharge),
            ["ev"] = EvSession(day, slots),
            ["soc"] = Math.Round(day.LevelAt(slots), 2),
            ["import_tariff"] = DemoHouse.ImportRate(local),
            ["export_tariff"] = DemoHouse.ExportRate(local),
        };
    }
    /// <summary>EV session energy (kWh) <paramref name="slots"/> half-hours after midnight, or null between sessions. Each session
    /// counter starts from zero and holds its total at the moment the session ends.</summary>
    static double? EvSession(DemoHouse.Day day, double slots)
    {
        var slot = (int)Math.Floor(slots); var fraction = slots - slot;
        if (fraction == 0 && slot > 0 && day.Car[slot - 1] > 0) { slot--; fraction = 1; }
        if (slot >= 48 || day.Car[slot] <= 0) return null;
        var first = slot; while (first > 0 && day.Car[first - 1] > 0) first--;
        double total = day.Car[slot] * fraction;
        for (var i = first; i < slot; i++) total += day.Car[i];
        return total > 0 ? Math.Round(total, 4) : null;
    }
    /// <summary>When the charger last went idle: the end of the latest session before <paramref name="at"/>.</summary>
    static DateTimeOffset EvIdleSince(DateTimeOffset at, TimeZoneInfo zone)
    {
        for (var date = DemoHouse.LocalDate(at, zone); ; date = date.AddDays(-1))
        {
            var day = DemoHouse.Measured(date); var midnight = DemoHouse.Midnight(date, zone);
            for (var slot = 47; slot >= 0; slot--)
            {
                var end = midnight.AddMinutes((slot + 1) * 30);
                if (day.Car[slot] > 0 && (slot == 47 || day.Car[slot + 1] <= 0) && end <= at) return end;
            }
        }
    }
    public static void Seed(DataStore db) => Seed(db, DateTimeOffset.UtcNow);
    public static void Seed(DataStore db, DateTimeOffset now)
    {
        var zone = db.TelemetryZone;
        var end = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, now.Minute / 5 * 5, 0, TimeSpan.Zero);
        var start = DemoHouse.Midnight(DemoHouse.LocalDate(now, zone).AddDays(-SeedDays), zone);
        var existing = db.ReadTelemetrySamples(start, end.AddMicroseconds(1), "load", 0, 1);
        if (existing.Count > 0)
        {
            if (db.ReadLatestTelemetry().GetValueOrDefault("load") is { } latest) start = latest.Time.AddMinutes(5);
        }
        if (start > end) return;
        var batch = new List<TelemetrySample>();
        for (var at = start; at <= end; at = at.AddMinutes(5))
        {
            var midnight = DemoHouse.Midnight(DemoHouse.LocalDate(at, zone), zone);
            var reset = TimeZoneInfo.ConvertTime(midnight, zone).ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture);
            var shortOutage = ShortOutage(at, zone); var outage = shortOutage || LongOutage(at, now, zone);
            foreach (var (metric, value) in Readings(at, zone))
            {
                var unit = metric == "soc" ? "%" : metric.EndsWith("tariff") ? "p/kWh" : "kWh";
                var attributes = metric == "ev" ? "{\"state_class\":\"total_increasing\"}" : TelemetrySchema.EnergyMetrics.Contains(metric) ? $"{{\"state_class\":\"total\",\"last_reset\":\"{reset}\"}}" : "{}";
                const string source = "Demo telemetry (scripted)";
                if (outage && (metric is "load" or "pv" || metric == "soc" && shortOutage)) batch.Add(new(metric, "demo." + metric, at, null, unit, source, "unavailable", "", at, attributes, "unavailable"));
                // Home Assistant's last_updated for an unknown state is when the sensor went unknown: the end of the charging session,
                // or local midnight for a daily counter that has not reported yet today.
                else if (value is null) batch.Add(new(metric, "demo." + metric, at, null, unit, source, "unknown", "", metric == "ev" ? EvIdleSince(at, zone) : midnight, attributes, "idle"));
                else batch.Add(new(metric, "demo." + metric, at, value, unit, source, value.Value.ToString(CultureInfo.InvariantCulture), unit, at, attributes));
            }
        }
        // The first seed is five weeks of history: load it in bulk and derive once. Later top-ups are a few polls.
        if (existing.Count == 0) db.ImportTelemetry(batch); else db.SaveTelemetry(batch);
    }
}
