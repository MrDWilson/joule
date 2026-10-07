using System.Text.Json;

namespace Joule;

public sealed record SetupPredbat(bool Configured, string? Address, DateTimeOffset? LastCollection, string? Error, string? Version, bool WritesEnabled);
/// <summary>One meter: its environment variable, what it is mapped to (if anything), the latest reading and, when it isn't
/// mapped, the Home Assistant sensors that look like it.</summary>
public sealed record SetupMeter(string Metric, string EnvVar, bool Required, string? Entity, string? Status, string? Unit, double? Value, string? Profile, List<SensorCandidate> Candidates)
{
    /// <summary>Where the sensor came from when Joule found it in Predbat ("load_today in apps.yaml", "name and unit match"); null when chosen or unmapped.</summary>
    public string? FoundFrom { get; init; }
    /// <summary>Predbat offers more than one sensor that could be this meter, or one Joule isn't sure of: the person chooses.</summary>
    public bool NeedsChoice { get; init; }
    /// <summary>Left unmapped on purpose (the value "none").</summary>
    public bool Declined { get; init; }
}
public sealed record SetupSensors(bool Configured, bool Direct, bool ViaPredbat, string? Address, DateTimeOffset? LastCollection, string? Error, List<SetupMeter> Meters);
public sealed record SetupMcp(bool Configured, bool Connected, int Tools, bool CanReadApps, string? Error, DateTimeOffset? CheckedAt, string? SignIn = null);
/// <summary>GET /api/setup: the setup checklist's progress (the same steps as the state header) plus what each step needs.</summary>
public sealed record SetupStatus(bool Demo, SetupProgress Progress, SetupPredbat Predbat, SetupSensors Sensors, SetupMcp Mcp);
/// <summary>GET /api/setup/predbat-apps: Predbat's apps.yaml as Predbat's MCP returns it (already masked by Predbat), with any
/// value that still looks secret hidden again. Read-only.</summary>
public sealed record PredbatAppsView(bool Available, string? Reason, string? Text, bool Truncated, DateTimeOffset At);

/// <summary>The setup checklist's data: connection status, the meters and sensor suggestions, and a read-only apps.yaml view.</summary>
public static class SetupEndpoints
{
    static readonly object candidateGate = new();
    static (string? Key, Dictionary<string, List<SensorCandidate>> Value) candidateCache;

    public static WebApplication MapSetupEndpoints(this WebApplication app)
    {
        app.MapGet("/api/setup", (IServiceProvider services, IConfiguration configuration) => Status(services, configuration, DateTimeOffset.UtcNow));
        app.MapGet("/api/setup/predbat-apps", async (IServiceProvider services, CancellationToken ct) => await PredbatApps(services.GetService<IPredbatMcpClient>(), DateTimeOffset.UtcNow, ct));
        return app;
    }

    /// <summary>scheme://host[:port]/path only: never credentials, query or fragment.</summary>
    public static string? SafeAddress(string? url) =>
        Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var u) && u.Scheme is "http" or "https" ? $"{u.Scheme}://{u.Authority}{u.AbsolutePath.TrimEnd('/')}" : null;

    public static SetupStatus Status(IServiceProvider services, IConfiguration configuration, DateTimeOffset now)
    {
        var stateService = services.GetRequiredService<StateService>();
        var state = stateService.Read(false);
        var client = services.GetRequiredService<IPredbatClient>();
        var mcp = services.GetService<IPredbatMcpClient>();
        TelemetryStatus? telemetry = null;
        try { telemetry = services.GetService<TelemetryCollectionService>()?.Status(); } catch (Exception e) when (e is not OperationCanceledException) { }
        var options = services.GetService<HomeAssistantOptions>();
        var auth = services.GetRequiredService<ChatGptAuth>();
        var model = services.GetRequiredService<AiModelClient>();
        var db = services.GetRequiredService<DataStore>();
        var demo = stateService.Demo;
        var context = new StateContext(demo, client.WritesEnabled, client.Configured, false, services.GetRequiredService<InvestigationScheduler>().Status(now),
            model.ApiConfigured, auth.Connected, null, false, null, [], 0, telemetry, mcp?.Configured ?? false, options?.TimeZone ?? "Europe/London", now);
        var progress = StateProjection.Setup(state, context);

        var version = StateProjection.PredbatVersion(state.Settings.FirstOrDefault(s => s.Key == "version")?.Value ?? state.Settings.FirstOrDefault(s => s.Key == "update")?.Value);
        var predbat = new SetupPredbat(client.Configured, SafeAddress(configuration["Predbat:BaseUrl"]), state.LastCollection, state.CollectionError, version, demo || client.WritesEnabled);

        var mappings = telemetry?.EntityMappings ?? options?.Entities ?? [];
        var unmapped = SensorCandidates.EnvNames.Keys.Any(m => !mappings.ContainsKey(m));
        var candidates = !demo && unmapped ? Candidates(db, state.LastCollection) : [];
        Dictionary<string, string> profiles = [];
        try { profiles = db.ReadSensorProfiles(); } catch (Exception e) when (e is not OperationCanceledException) { }
        var meters = SensorCandidates.EnvNames.Select(x =>
        {
            mappings.TryGetValue(x.Key, out var entity);
            var reading = telemetry?.LatestReadings.GetValueOrDefault(x.Key);
            var profile = options is not null && SensorProfiles.Override(options.Profiles, x.Key) is { } p ? p : profiles.GetValueOrDefault(x.Key);
            return new SetupMeter(x.Key, $"HomeAssistant__Entities__{x.Value}", SensorCandidates.Required.Contains(x.Key), entity, reading?.Status, reading?.Unit, reading?.Value,
                entity is null ? null : profile, entity is null ? candidates.GetValueOrDefault(x.Key) ?? [] : [])
            {
                FoundFrom = entity is not null && telemetry?.Detected.GetValueOrDefault(x.Key) is { } found && found.Entity == entity ? found.From : null,
                NeedsChoice = entity is null && (telemetry?.NeedsChoice.Contains(x.Key) ?? false),
                Declined = telemetry?.Declined.Contains(x.Key) ?? false,
            };
        }).ToList();
        var direct = telemetry?.HomeAssistantDirect ?? false;
        var sensors = new SetupSensors(telemetry?.Configured ?? false, direct, !direct && client.Configured, SafeAddress(configuration["HomeAssistant:BaseUrl"]),
            telemetry?.LastCollection, telemetry?.Error, meters);

        var discovery = mcp?.Status;
        var mcpStatus = new SetupMcp(mcp?.Configured ?? false, discovery?.Connected ?? false, discovery?.Tools.Count ?? 0, discovery?.Tools.Any(t => t.Name == "get_apps") ?? false,
            discovery?.Error, discovery?.CheckedAt, discovery?.SignIn);
        return new(demo, progress, predbat, sensors, mcpStatus);
    }

    /// <summary>Sensor suggestions from the newest stored Predbat state, recomputed only when a new collection arrives.</summary>
    static Dictionary<string, List<SensorCandidate>> Candidates(DataStore db, DateTimeOffset? lastCollection)
    {
        var key = lastCollection?.ToString("O");
        lock (candidateGate)
        {
            if (key is not null && candidateCache.Key == key) return candidateCache.Value;
            Dictionary<string, List<SensorCandidate>> found = [];
            try
            {
                if (db.ReadLatestSourceState() is { Length: > 0 and < 16_000_000 } raw)
                {
                    using var json = JsonDocument.Parse(raw);
                    found = SensorCandidates.Find(json.RootElement);
                }
            }
            catch (Exception e) when (e is JsonException or InvalidOperationException or IOException) { }
            candidateCache = (key, found);
            return found;
        }
    }

    public static async Task<PredbatAppsView> PredbatApps(IPredbatMcpClient? mcp, DateTimeOffset now, CancellationToken ct)
    {
        if (mcp is null || !mcp.Configured) return new(false, "Predbat's MCP connection isn't set up.", null, false, now);
        using var args = JsonDocument.Parse("{\"masked\":true}");
        var result = await mcp.CallReadOnlyAsync("get_apps", args.RootElement, ct);
        if (!result.Success) return new(false, result.Error ?? "Predbat didn't return its apps.yaml.", null, result.Truncated, now);
        var text = AppsText(result.ResultJson);
        if (text is null) return new(false, result.Truncated ? "apps.yaml is too large to show here." : "Predbat's reply had no apps.yaml in it.", null, result.Truncated, now);
        var json = text.TrimStart().StartsWith('{');
        return new(true, null, ConfigFileMask.Render(ConfigFileMask.Mask(json ? "apps.json" : "apps.yaml", text, keepSafeValues: true)), result.Truncated, now);
    }

    /// <summary>The text parts of an MCP tool result ({"result":{"content":[{"type":"text","text":…}]}}), joined.</summary>
    internal static string? AppsText(string resultJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(resultJson);
            if (!doc.RootElement.TryGetProperty("result", out var r) || r.ValueKind != JsonValueKind.Object || !r.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array) return null;
            var parts = content.EnumerateArray().Where(c => c.ValueKind == JsonValueKind.Object && c.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String).Select(c => c.GetProperty("text").GetString()!).ToList();
            if (parts.Count == 0) return null;
            var text = string.Join('\n', parts);
            // Some Predbat versions wrap the file in a JSON envelope such as {"apps": "…yaml…"}.
            try
            {
                using var inner = JsonDocument.Parse(text);
                if (inner.RootElement.ValueKind == JsonValueKind.Object && inner.RootElement.EnumerateObject().ToList() is [{ Value.ValueKind: JsonValueKind.String } only]) return only.Value.GetString();
            }
            catch (JsonException) { }
            return text;
        }
        catch (JsonException) { return null; }
    }
}
