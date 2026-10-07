using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Joule;

public sealed class HomeAssistantOptions
{
    internal Uri? BaseUri { get; }
    internal string? AccessToken { get; }
    public string TimeZone { get; }
    public TimeSpan MaxGap { get; }
    public TimeSpan PollInterval { get; }
    public Dictionary<string,string> Entities { get; }=[];
    public Dictionary<string,string> Units { get; }=[];
    /// <summary>Metrics whose sensor Joule worked out itself rather than being told (the Octopus standing charge, from the import rate
    /// sensor on the same meter). A worked-out sensor that doesn't exist is skipped quietly.</summary>
    public HashSet<string> DerivedEntities { get; }=[];
    /// <summary>Sensor profile overrides by metric (HomeAssistant:Profiles:Ev=session_counter, …). Unset metrics are detected from history.</summary>
    public Dictionary<string,string> Profiles { get; }=[];
    /// <summary>auto, true or false (HomeAssistant:LoadIncludesEv): whether the load meter includes EV charging.</summary>
    public string LoadIncludesEv { get; }
    public TelemetrySettings TelemetrySettings=>new(TimeZone,Profiles,LoadIncludesEv,MaxGap);
    /// <summary>Home Assistant credentials for direct reads. Mapping presence and the Predbat fallback are judged by HomeAssistantClient.Configured.</summary>
    public bool DirectConfigured=>BaseUri is not null && !string.IsNullOrWhiteSpace(AccessToken);
    public HomeAssistantOptions(IConfiguration config)
    {
        var url=config["HomeAssistant:BaseUrl"];
        if(!string.IsNullOrWhiteSpace(url))
        {
            if(!Uri.TryCreate(url.TrimEnd('/')+"/",UriKind.Absolute,out var uri) || uri.Scheme is not("http" or "https") || !string.IsNullOrEmpty(uri.UserInfo))throw new DomainException("Home Assistant URL must use HTTP(S) without embedded credentials.",400);
            BaseUri=uri;
        }
        AccessToken=config["HomeAssistant:AccessToken"];
        TimeZone=config["HomeAssistant:TimeZone"]??"Europe/London";
        try{TimeZoneInfo.FindSystemTimeZoneById(TimeZone);}catch(TimeZoneNotFoundException){throw new DomainException("Unknown Home Assistant timezone.",400);}
        var poll=config.GetValue("HomeAssistant:PollMinutes",5d);
        // Two missed polls plus jitter are still one update cycle: the default tolerance is max(15 min, 2.5 × the poll interval).
        var gap=config.GetValue("HomeAssistant:MaxGapMinutes",Math.Max(15,2.5*(double.IsFinite(poll)?poll:5)));
        if(!double.IsFinite(gap) || !double.IsFinite(poll) || poll is <1 or >60 || gap<poll || gap>1440)throw new DomainException("Set HA poll interval 1–60 minutes and maximum gap between poll interval and 1440 minutes.",400);
        MaxGap=TimeSpan.FromMinutes(gap);PollInterval=TimeSpan.FromMinutes(poll);
        var names=new Dictionary<string,string>{["Load"]="load",["Pv"]="pv",["GridImport"]="grid_import",["GridExport"]="grid_export",["BatteryCharge"]="battery_charge",["BatteryDischarge"]="battery_discharge",["Ev"]="ev",["Soc"]="soc",["ImportTariff"]="import_tariff",["ExportTariff"]="export_tariff",["StandingCharge"]="standing_charge",["IntelligentSlots"]="intelligent_slots",["AlternativeForecast"]="alternative_forecast"};
        foreach(var (name,metric) in names)
        {
            if(config["HomeAssistant:Entities:"+name] is not {} entity || string.IsNullOrWhiteSpace(entity))continue;
            if(!Regex.IsMatch(entity,"^[a-z_]+\\.[a-z0-9_]+$"))throw new DomainException("Home Assistant entity mapping is invalid.",400);
            Entities[metric]=entity;
            if(config["HomeAssistant:Units:"+name] is {} unit)Units[metric]=unit;
        }
        if(!Entities.ContainsKey(StandingCharge.Metric) && StandingCharge.FromOctopusRate(Entities.GetValueOrDefault("import_tariff")) is {} standing)
        {
            Entities[StandingCharge.Metric]=standing;DerivedEntities.Add(StandingCharge.Metric);
        }
        foreach(var (name,metric) in names)
        {
            if(config["HomeAssistant:Profiles:"+name] is not {} profile || string.IsNullOrWhiteSpace(profile))continue;
            profile=profile.Trim().ToLowerInvariant();
            if(!SensorProfiles.All.Contains(profile))throw new DomainException($"Unknown sensor profile '{profile}' for {name}. Use one of: {string.Join(", ",SensorProfiles.All)}.",400);
            Profiles[metric]=profile;
        }
        LoadIncludesEv=(config["HomeAssistant:LoadIncludesEv"]??"auto").Trim().ToLowerInvariant();
        if(LoadIncludesEv is not ("auto" or "true" or "false"))throw new DomainException("HomeAssistant:LoadIncludesEv must be auto, true or false.",400);
    }
}
public sealed class HomeAssistantClient(HttpClient http,HomeAssistantOptions options,IPredbatEntityReader? fallback=null,TimeProvider? clock=null)
{
    public const string DirectSource="HomeAssistant";
    public const string MirrorSource="Predbat mirror";
    /// <summary>Mappings are always explicit. Readings come from Home Assistant directly when credentials exist, otherwise from Predbat's mirror.</summary>
    public bool Configured=>options.Entities.Count>0 && (options.DirectConfigured || fallback?.Configured==true);
    /// <summary>True when the last collection could not read Home Assistant (directly or through Predbat's mirror) at all.</summary>
    public bool LastReadFailed { get; private set; }
    public async Task<List<TelemetrySample>> CollectAsync(CancellationToken ct)
    {
        if(!Configured)throw new DomainException("Configure explicit entity mappings plus either the HA URL and server token or a Predbat URL for mirrored readings.",503);
        var source=DirectSource;
        var states=options.DirectConfigured?await ReadDirectAsync(ct):null;
        if(states is null && fallback?.Configured==true){source=MirrorSource;states=await ReadMirrorAsync(ct);}
        var observedAt=(clock??TimeProvider.System).GetUtcNow();
        LastReadFailed=states is null;
        if(states is null)return options.Entities.Select(x=>Unavailable(x.Key,x.Value,observedAt,source)).ToList();
        var result=new List<TelemetrySample>();
        foreach(var (metric,entity) in options.Entities)
        {
            try
            {
                var matches=states.OfType<JsonObject>().Where(x=>x["entity_id"]?.ToString()==entity).Take(2).ToList();
                if(matches.Count==0 && options.DerivedEntities.Contains(metric))continue;
                result.Add(matches.Count==1?Parse(metric,entity,matches[0],observedAt,source):matches.Count==0?NotFound(metric,entity,observedAt,source):Unavailable(metric,entity,observedAt,source));
            }
            catch(Exception e) when(e is FormatException or InvalidOperationException or DomainException){result.Add(Unavailable(metric,entity,observedAt,source));}
        }
        return result;
    }
    // All mapped cumulative meters need the same observed endpoint for whole-interval cost matching.
    // One HA state response provides that endpoint; independently timed per-entity responses do not.
    // Any failure returns null so the caller can try Predbat's mirror of the same entities.
    async Task<JsonArray?> ReadDirectAsync(CancellationToken ct)
    {
        const int maxResponseBytes=8*1024*1024;
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(ct);
        if(http.Timeout!=Timeout.InfiniteTimeSpan)deadline.CancelAfter(http.Timeout);
        var requestToken=deadline.Token;
        try
        {
            using var request=new HttpRequestMessage(HttpMethod.Get,new Uri(options.BaseUri!,"api/states"));
            request.Headers.Authorization=new AuthenticationHeaderValue("Bearer",options.AccessToken);
            using var response=await http.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,requestToken);
            if(!response.IsSuccessStatusCode)return null;
            if(response.Content.Headers.ContentLength>maxResponseBytes)throw new DomainException("HA state response is too large.",502);
            await using var stream=await response.Content.ReadAsStreamAsync(requestToken);
            using var content=new MemoryStream();var buffer=new byte[16*1024];
            int count;
            while((count=await stream.ReadAsync(buffer,requestToken))>0)
            {
                if(content.Length+count>maxResponseBytes)throw new DomainException("HA state response is too large.",502);
                content.Write(buffer,0,count);
            }
            return JsonNode.Parse(content.ToArray()) as JsonArray;
        }
        catch(OperationCanceledException) when(!ct.IsCancellationRequested){return null;}
        catch(Exception e) when(e is HttpRequestException or IOException or System.Text.Json.JsonException or FormatException or InvalidOperationException or DomainException){return null;}
    }
    async Task<JsonArray?> ReadMirrorAsync(CancellationToken ct)
    {
        try{return await fallback!.ReadEntityStatesAsync(options.Entities.Values.Distinct().ToArray(),ct);}
        catch(OperationCanceledException) when(!ct.IsCancellationRequested){return null;}
        catch(Exception e) when(e is HttpRequestException or IOException or System.Text.Json.JsonException or FormatException or InvalidOperationException or DomainException){return null;}
    }
    static TelemetrySample Unavailable(string metric,string entity,DateTimeOffset at,string source=DirectSource)=>new(metric,entity,at,null,"",source,"unavailable","",Status:"unavailable");
    /// <summary>The mapped entity is not in Home Assistant's state list: a renamed or deleted sensor, not an offline device.</summary>
    static TelemetrySample NotFound(string metric,string entity,DateTimeOffset at,string source=DirectSource)=>new(metric,entity,at,null,"",source,"entity not found in Home Assistant","",Status:"not_found");
    /// <summary>Normalises a numeric Home Assistant state to Joule's units (kWh, %, p/kWh). Returns the status the reading deserves.</summary>
    public static (double? Value,string Unit,string Status) Normalize(string metric,string rawState,string rawUnit)
    {
        if(rawState.Equals("unknown",StringComparison.OrdinalIgnoreCase))return (null,"","idle");
        if(rawState.Equals("unavailable",StringComparison.OrdinalIgnoreCase))return (null,"","unavailable");
        if(metric is "intelligent_slots" or "alternative_forecast")return (null,"","observed");
        if(!double.TryParse(rawState,NumberStyles.Float,CultureInfo.InvariantCulture,out var n) || !double.IsFinite(n))return (null,"","invalid");
        var normalized=rawUnit.Replace(" ","").ToLowerInvariant();double? value=null;var unit="";
        if(TelemetrySchema.EnergyMetrics.Contains(metric)){unit="kWh";value=normalized switch{"kwh"=>n,"wh"=>n/1000,"mwh"=>n*1000,_=>null};if(value<0)value=null;}
        else if(metric=="soc"){unit="%";value=normalized=="%" && n is >=0 and <=100 ? n : null;}
        else if(metric is "import_tariff" or "export_tariff"){unit="p/kWh";value=normalized switch {"p/kwh" or "pence/kwh"=>n,"£/kwh" or "gbp/kwh"=>n*100,"p/wh"=>n*1000,"£/mwh" or "gbp/mwh"=>n/10,_=>null};}
        else if(metric==StandingCharge.Metric){unit="p/day";value=StandingCharge.Pence(n,rawUnit);}
        if(value is {} converted && !double.IsFinite(converted))return (null,unit,"invalid");
        return (value,unit,value is null?"unsupported_unit":"observed");
    }
    public TelemetrySample Parse(string metric,string entity,JsonObject state,DateTimeOffset observedAt,string source=DirectSource)
    {
        var attrs=state["attributes"] as JsonObject??new();var rawState=state["state"]?.ToString()??"unavailable";var rawUnit=attrs["unit_of_measurement"]?.ToString()??options.Units.GetValueOrDefault(metric)??"";
        var updated=DateTimeOffset.TryParse(state["last_updated"]?.ToString()??state["last_changed"]?.ToString(),CultureInfo.InvariantCulture,DateTimeStyles.None,out var date)?date:(DateTimeOffset?)null;
        // Home Assistant's "unknown" means the sensor has no value right now (solar overnight, an idle EV charger, an export counter
        // before the first export): status idle. "unavailable" means the device is offline. "invalid" is reserved for states that
        // should be numeric but cannot be parsed.
        var (value,unit,status)=Normalize(metric,rawState,rawUnit);
        var time=observedAt;
        var reportedText=state["last_reported"]?.ToString();
        var reported=DateTimeOffset.TryParse(reportedText,CultureInfo.InvariantCulture,DateTimeStyles.None,out var reportDate)?reportDate:(DateTimeOffset?)null;
        // Poll snapshots are observed endpoints. Push-based integrations (Teslemetry, Octopus) only write state when a value
        // changes, so an old last_reported on an unchanged counter is normal, not stale; HA's own unavailable/unknown state is
        // the offline signal. The reported age is recorded as evidence rather than used to discard the reading.
        var safe=Sanitize(attrs).AsObject();
        safe["_collection"]=new JsonObject { ["sampled_at"]=observedAt.ToString("O"),["last_reported"]=reported?.ToString("O"),["reported_age_seconds"]=reported is null?null:Math.Round((observedAt-reported.Value).TotalSeconds),["freshness"]=reportedText is not null?"last_reported_recorded":source==MirrorSource?"predbat_mirror_unknown_device_freshness":"successful_poll_unknown_device_freshness" };
        return new(metric,entity,time,value,unit,source,Redact(rawState),Redact(rawUnit),updated,safe.ToJsonString(),status);
    }
    string Redact(string value)=>!string.IsNullOrEmpty(options.AccessToken)&&value.Contains(options.AccessToken,StringComparison.Ordinal)||Regex.IsMatch(value,@"https?://[^\s/]*@",RegexOptions.IgnoreCase)||Regex.IsMatch(value,"Bearer |password\\s*[:=]|access_token\\s*[:=]|api_key\\s*[:=]",RegexOptions.IgnoreCase)?"[redacted]":value;
    JsonNode Sanitize(JsonNode node)
    {
        if(node is JsonObject obj){var result=new JsonObject();foreach(var (key,value) in obj)if(!Regex.IsMatch(key,"password|secret|token|credential|authorization|auth.?header|api.?key|access.?key|private.?key|connection.?string|email|user.?name|owner",RegexOptions.IgnoreCase)&&value is not null)result[key]=Sanitize(value);return result;}
        if(node is JsonArray list)return new JsonArray(list.Select(x=>x is null?null:Sanitize(x)).ToArray());
        return node is JsonValue v&&v.TryGetValue<string>(out var s)?JsonValue.Create(Redact(s))!:node.DeepClone();
    }
}
