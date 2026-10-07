using System.Text.Json;

namespace Joule;

/// <summary>What detection found last time, kept in the data folder so a restart uses it straight away.</summary>
public sealed record DetectedSensorsFile(DateTimeOffset At, string? AppsSource, Dictionary<string, DetectedSensor> Sensors, List<string> NeedsChoice, double? FixedStandingChargePence);

/// <summary>
/// Finds the Home Assistant sensors nobody chose, from Predbat, so a new install needs no sensor configuration: on the first poll
/// once Joule is live, then every six hours (every half hour while Predbat can't be read). Sure answers are used straight away
/// (Setup shows "Found automatically from Predbat"); where Predbat offers several sensors, or Joule isn't sure, Setup asks.
/// A sensor chosen in the environment or in Setup, including "none", is never changed. Not used in the demo.
/// </summary>
public sealed class SensorAutoDetect
{
    public static readonly TimeSpan Interval = TimeSpan.FromHours(6), Retry = TimeSpan.FromMinutes(30);
    public const string FileName = "detected-sensors.json";
    readonly HomeAssistantOptions options;
    readonly string? path;
    readonly Func<CancellationToken, Task<MeterDetection>> detect;
    readonly TimeProvider clock;
    readonly ILogger? logger;
    DateTimeOffset? lastAttempt;
    bool lastOk;

    /// <param name="path">Where to keep what was found; null keeps it in memory only.</param>
    public SensorAutoDetect(HomeAssistantOptions options, string? path, Func<CancellationToken, Task<MeterDetection>> detect, TimeProvider? clock = null, ILogger? logger = null)
    {
        this.options = options; this.path = path; this.detect = detect; this.clock = clock ?? TimeProvider.System; this.logger = logger;
        if (Load() is { } saved) { options.ApplyDetected(saved.Sensors, saved.NeedsChoice, saved.FixedStandingChargePence); LastDetected = saved.At; AppsSource = saved.AppsSource; }
    }

    /// <summary>When Predbat was last read for sensors successfully.</summary>
    public DateTimeOffset? LastDetected { get; private set; }
    /// <summary>Where apps.yaml came from last time, or null when only names and units were used.</summary>
    public string? AppsSource { get; private set; }

    public bool Due => lastAttempt is not { } at || clock.GetUtcNow() - at >= (lastOk ? Interval : Retry);

    public async Task RunIfDueAsync(CancellationToken ct)
    {
        if (!Due) return;
        lastAttempt = clock.GetUtcNow(); lastOk = false;
        MeterDetection found;
        try { found = await detect(ct); }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested) { logger?.LogWarning("Finding sensors from Predbat failed: {Type}", e.GetType().Name); return; }
        // Without Predbat's entity list nothing can be checked, so what was found before stays in use.
        if (!found.HaveEntities) return;
        var (sensors, needsChoice) = Choose(found, options.Chosen);
        options.ApplyDetected(sensors, needsChoice, found.StandingChargePence);
        LastDetected = lastAttempt; AppsSource = found.AppsSource; lastOk = true;
        Save(new(lastAttempt.Value, found.AppsSource, sensors, needsChoice, found.StandingChargePence));
    }

    /// <summary>
    /// The sensors to use without asking (each sure suggestion for a meter nobody chose) and the meters to ask about: those with a
    /// suggestion Joule isn't sure of, or more than one sensor that could be it.
    /// </summary>
    public static (Dictionary<string, DetectedSensor> Use, List<string> Ask) Choose(MeterDetection detection, IReadOnlySet<string> chosen)
    {
        var use = new Dictionary<string, DetectedSensor>(StringComparer.Ordinal); var ask = new List<string>();
        foreach (var meter in detection.Meters)
        {
            if (chosen.Contains(meter.Metric) || meter.Entity is null) continue;
            if (meter.Confident) use[meter.Metric] = new(meter.Entity, meter.From ?? "Predbat");
            else ask.Add(meter.Metric);
        }
        return (use, ask);
    }

    DetectedSensorsFile? Load()
    {
        if (path is null || !File.Exists(path)) return null;
        try { return JsonSerializer.Deserialize<DetectedSensorsFile>(File.ReadAllText(path), JsonDefaults.Options) is { Sensors: not null, NeedsChoice: not null } file ? file : null; }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException or NotSupportedException) { return null; }
    }

    void Save(DetectedSensorsFile file)
    {
        if (path is null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(file, new JsonSerializerOptions(JsonDefaults.Options) { WriteIndented = true }) + "\n");
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { logger?.LogWarning("Couldn't save the sensors found from Predbat: {Type}", e.GetType().Name); }
    }
}
