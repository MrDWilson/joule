using System.Globalization;
using System.Text.Json;

namespace Joule;

/// <summary>
/// How a mapped sensor behaves, which decides how Joule reads its gaps, drops and Home Assistant "unknown" states.
/// <list type="bullet">
/// <item><c>daily_counter</c>: resets at local midnight (Home Assistant <c>last_reset</c>). "unknown" before the first reading of the day means nothing has flowed yet.</item>
/// <item><c>solar_daily</c>: a daily counter for solar. "unknown" counts as zero only when the counter proves it or the sun is down (Predbat's PV forecast ≤ 0.01 kWh).</item>
/// <item><c>session_counter</c>: restarts with each charging session; "unknown" between sessions is a known zero.</item>
/// <item><c>lifetime_counter</c>: only rises; "unknown" is an outage.</item>
/// <item><c>price</c> and <c>state</c>: tariffs, battery level and other non-energy readings.</item>
/// </list>
/// </summary>
public static class SensorProfiles
{
    public const string DailyCounter = "daily_counter", SolarDaily = "solar_daily", SessionCounter = "session_counter", LifetimeCounter = "lifetime_counter", Price = "price", State = "state";
    public static readonly string[] All = [DailyCounter, SolarDaily, SessionCounter, LifetimeCounter, Price, State];
    public static bool IsDaily(string profile) => profile is DailyCounter or SolarDaily;
    public static bool IsCounter(string profile) => profile is DailyCounter or SolarDaily or SessionCounter or LifetimeCounter;
    /// <summary>A reset (or a reading) this close to local midnight belongs to the daily reset.</summary>
    public static readonly TimeSpan MidnightTolerance = TimeSpan.FromMinutes(15);
    /// <summary>A charger meter whose readings never fall to this is a lifetime total, not a per-session counter.</summary>
    public const double LifetimeFloorKwh = 100;

    /// <summary>Profiles for metrics whose kind is fixed by what they measure.</summary>
    public static string? Fixed(string metric) => metric switch
    {
        "import_tariff" or "export_tariff" => Price,
        "soc" or "intelligent_slots" or "alternative_forecast" => State,
        _ => null
    };

    /// <summary>Profile used before there is any history to judge from.</summary>
    public static string Default(string metric) => Fixed(metric) ?? metric switch { "ev" => SessionCounter, "pv" => SolarDaily, _ => LifetimeCounter };

    /// <summary>
    /// Detects an energy meter's profile from its recent samples (oldest first). The rules, in order:
    /// <list type="number">
    /// <item>A reading whose <c>last_reset</c> attribute lies within 15 minutes of a local midnight → daily (solar_daily for pv).</item>
    /// <item>Counter drops within 15 minutes of local midnight (between readings at most 30 minutes apart) on at least two different days, and no drops at other times → daily.</item>
    /// <item>For pv, any drop → solar_daily (solar never runs in sessions).</item>
    /// <item>At least two drops at other times, or two drops straight after an "unknown" run on a sensor with no <c>last_reset</c> → session_counter.</item>
    /// <item>Otherwise ev → session_counter, unless every reading is above <see cref="LifetimeFloorKwh"/> (a charger's lifetime total
    /// never returns near zero), pv → solar_daily, and anything else → lifetime_counter.</item>
    /// </list>
    /// A drop is any fall greater than <see cref="DataStore.JitterTolerance"/> between consecutive usable readings of the same entity.
    /// </summary>
    public static string Detect(string metric, IReadOnlyList<TelemetrySample> samples, TimeZoneInfo zone)
    {
        if (Fixed(metric) is { } fixedProfile) return fixedProfile;
        var daily = metric == "pv" ? SolarDaily : DailyCounter;
        foreach (var sample in samples.TakeLast(200))
            if (Attributes.LastReset(sample.AttributesJson) is { } reset && NearLocalMidnight(reset, zone)) return daily;
        var midnightDays = new HashSet<DateTime>(); int otherDrops = 0, dropsAfterIdle = 0; TelemetrySample? previous = null; bool idleSince = false;
        foreach (var sample in samples)
        {
            if (sample.Status != "observed" || sample.Value is null) { if (sample.Status == "idle") idleSince = true; continue; }
            if (previous is { Value: { } before } && previous.EntityId == sample.EntityId && sample.Value < before - DataStore.JitterTolerance)
            {
                // Only a drop between readings close together can be pinned to midnight; a drop across a long idle run says nothing about when.
                var midnight = sample.Time - previous.Time <= MidnightTolerance + MidnightTolerance ? LocalMidnightWithin(previous.Time - MidnightTolerance, sample.Time + MidnightTolerance, zone) : null;
                if (midnight is { } m) midnightDays.Add(TimeZoneInfo.ConvertTime(m, zone).Date);
                // A session restarts near zero or after an idle run; a small dip on a busy counter is a glitch, not evidence.
                else if (sample.Value < before / 2 || idleSince) { otherDrops++; if (idleSince && Attributes.LastReset(sample.AttributesJson) is null) dropsAfterIdle++; }
            }
            previous = sample; idleSince = false;
        }
        if (midnightDays.Count >= 2 && otherDrops == 0) return daily;
        // Solar never runs in sessions: any drop on a solar meter is its daily reset.
        if (metric == "pv") return midnightDays.Count + otherDrops > 0 ? SolarDaily : Default(metric);
        if (otherDrops >= 2 || dropsAfterIdle >= 2) return SessionCounter;
        if (metric == "ev" && samples.Where(x => x.Status == "observed" && x.Value is not null).Select(x => x.Value!.Value).DefaultIfEmpty(0).Min() > LifetimeFloorKwh) return LifetimeCounter;
        return Default(metric);
    }

    public static bool NearLocalMidnight(DateTimeOffset at, TimeZoneInfo zone)
    {
        var local = TimeZoneInfo.ConvertTime(at, zone);
        var sinceMidnight = local.TimeOfDay; var untilMidnight = TimeSpan.FromDays(1) - local.TimeOfDay;
        return sinceMidnight <= MidnightTolerance || untilMidnight <= MidnightTolerance;
    }

    /// <summary>The first local midnight in [from,to], if any (DST-aware).</summary>
    public static DateTimeOffset? LocalMidnightWithin(DateTimeOffset from, DateTimeOffset to, TimeZoneInfo zone)
    {
        if (to < from) return null;
        var day = TimeZoneInfo.ConvertTime(from, zone).Date;
        for (var i = 0; i < 3; i++, day = day.AddDays(1))
        {
            var midnight = CivilTime.FirstValidInstant(day, zone);
            if (midnight > to) return null;
            if (midnight >= from) return midnight;
        }
        return null;
    }

    /// <summary>Profile override from configuration: HomeAssistant:Profiles:&lt;Name&gt; (e.g. HomeAssistant__Profiles__Ev=session_counter).</summary>
    public static string? Override(IReadOnlyDictionary<string, string> overrides, string metric) => overrides.TryGetValue(metric, out var p) && All.Contains(p) ? p : null;
}

/// <summary>Reads the few Home Assistant attributes the accounting needs, without parsing every attribute bag.</summary>
public static class Attributes
{
    public static DateTimeOffset? LastReset(string? json)
    {
        if (string.IsNullOrEmpty(json) || !json.Contains("\"last_reset\"", StringComparison.Ordinal)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("last_reset", out var value) && value.ValueKind == JsonValueKind.String &&
                DateTimeOffset.TryParse(value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var at) ? at : null;
        }
        catch (JsonException) { return null; }
    }
    public static string? StateClass(string? json)
    {
        if (string.IsNullOrEmpty(json) || !json.Contains("\"state_class\"", StringComparison.Ordinal)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("state_class", out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        }
        catch (JsonException) { return null; }
    }
}
