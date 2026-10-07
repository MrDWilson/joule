using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Joule;

/// <summary>One setting as Setup shows it. Secrets carry only <see cref="Set"/>, never their value.</summary>
/// <param name="Source">"environment" (wins, can't be changed in Setup), "saved" (from Setup) or null (default).</param>
/// <param name="Pending">Saved, but Joule hasn't restarted with it yet.</param>
public sealed record SetupConfigField(string Key, string EnvVar, string Kind, bool Secret, string? Value, bool Set, string? Source, bool Pending);
/// <summary>GET /api/setup/config: what Setup can change, and whether it may.</summary>
/// <param name="CanSave">False only for a demo whose App__Demo comes from the environment (a public demo can't be switched by visitors).</param>
/// <param name="AccessKeyNeeded">Going live needs an access key: none is set and sign-in isn't handled by a proxy (App__AuthMode=None).</param>
public sealed record SetupConfigView(bool Demo, bool CanSave, string? Locked, string SettingsFile, bool InContainer, bool AccessKeyNeeded, bool RestartPending, List<SetupConfigField> Fields);
public sealed record SetupSaveRequest(Dictionary<string, string?>? Values, bool Restart);
public sealed record PredbatTestRequest(string? Url);
/// <summary>The result of asking an address for Predbat's /api/state.</summary>
public sealed record PredbatProbe(string Url, bool Ok, string? Version, int Entities, string? Error, long Milliseconds);
/// <summary>A meter Setup can map: the current mapping, and the suggested sensor with where the suggestion came from.</summary>
public sealed record MeterSuggestion(string Metric, string Key, string EnvVar, string? Current, string? Entity, string? From, string? State, string? Unit, List<SensorCandidate> Alternatives)
{
    /// <summary>Sure enough for Joule to use without asking (see <see cref="SetupConfigEndpoints.Detect"/>).</summary>
    public bool Confident { get; init; }
    /// <summary>The current mapping was found automatically from Predbat rather than chosen.</summary>
    public bool Auto { get; init; }
    /// <summary>The person chose to leave this meter unmapped (the value "none").</summary>
    public bool Declined { get; init; }
}
public sealed record MeterDetection(string? AppsSource, string? AppsError, bool HaveEntities, List<MeterSuggestion> Meters)
{
    /// <summary>Predbat's metric_standing_charge when apps.yaml gives it as a number of pounds a day, in pence a day.</summary>
    public double? StandingChargePence { get; init; }
}

/// <summary>
/// Setup's write side: save settings into the data directory (environment variables still win), test and find Predbat, and
/// suggest Home Assistant sensors from Predbat's own apps.yaml. Saving can restart Joule in-process so the settings apply.
/// </summary>
public static class SetupConfigEndpoints
{
    /// <summary>Where Predbat's web interface usually is, as seen from Joule (in Docker or not).</summary>
    public static readonly string[] PredbatGuesses =
    [
        "http://predbat:5052",              // Predbat's Docker container on the same network
        "http://host.docker.internal:5052", // Predbat on the Docker host (compose.yaml maps host.docker.internal)
        "http://homeassistant.local:5052",  // the Home Assistant add-on, port 5052 opened in its Network settings
        "http://homeassistant:5052",
        "http://172.17.0.1:5052",           // the default Docker bridge's gateway, which is the host
        "http://127.0.0.1:5052",            // Joule running natively next to Predbat
    ];

    /// <summary>Predbat apps.yaml keys that name the sensor for each meter. Battery charge and discharge have none.</summary>
    public static readonly IReadOnlyDictionary<string, string[]> AppsKeys = new Dictionary<string, string[]>
    {
        ["load"] = ["load_today"], ["pv"] = ["pv_today"], ["grid_import"] = ["import_today"], ["grid_export"] = ["export_today"],
        ["soc"] = ["soc_percent"], ["ev"] = ["car_charging_energy"], ["import_tariff"] = ["metric_octopus_import"], ["export_tariff"] = ["metric_octopus_export"],
        ["standing_charge"] = ["metric_standing_charge"],
    };

    public static WebApplication MapSetupConfigEndpoints(this WebApplication app, bool inContainer)
    {
        app.MapGet("/api/setup/config", (SavedSettings saved, AppAuthOptions auth) => View(saved, auth, inContainer));
        app.MapPost("/api/setup/config", (SetupSaveRequest request, SavedSettings saved, AppAuthOptions auth, HttpContext context, IHostApplicationLifetime lifetime) =>
        {
            EnsureCanSave(saved, auth, context.Request);
            var values = request.Values ?? [];
            if (values.Count > 0)
            {
                if (values.TryGetValue("App:Demo", out var demo) && demo == "false" && !auth.NoAuth && !HasAccessKey(saved, values))
                    throw new DomainException("Choose an access key before switching to your own Predbat: it keeps your dashboard private.", 400);
                saved.Save(values);
            }
            if (request.Restart) context.Response.OnCompleted(() => { JouleRestart.Request(lifetime); return Task.CompletedTask; });
            return Results.Ok(new { ok = true, restarting = request.Restart });
        });
        app.MapPost("/api/setup/predbat/test", async (PredbatTestRequest request, HttpContext context, SavedSettings saved, AppAuthOptions auth, IConfiguration configuration, IHttpClientFactory clients, CancellationToken ct) =>
        {
            EnsureCanSave(saved, auth, context.Request);
            var configured = configuration["Predbat:BaseUrl"];
            var url = string.IsNullOrWhiteSpace(request.Url) ? configured : request.Url.Trim();
            if (string.IsNullOrWhiteSpace(url)) throw new DomainException("Enter Predbat's address first.", 400);
            if (SavedSettings.Validate(SavedSettings.Field("Predbat:BaseUrl")!, url) is { } problem) throw new DomainException(problem, 400);
            // Predbat's bearer token goes only to the address it was configured for.
            var token = SameAddress(url, configured) ? configuration["Predbat:AccessToken"] : null;
            return await Probe(clients.CreateClient("predbat-probe"), url, token, TimeSpan.FromSeconds(8), ct);
        });
        app.MapPost("/api/setup/predbat/find", async (HttpContext context, SavedSettings saved, AppAuthOptions auth, IConfiguration configuration, IHttpClientFactory clients, CancellationToken ct) =>
        {
            EnsureCanSave(saved, auth, context.Request);
            var guesses = PredbatGuesses.ToList();
            // Predbat's add-on usually lives on the same host as Home Assistant.
            if (Uri.TryCreate(configuration["HomeAssistant:BaseUrl"], UriKind.Absolute, out var ha)) guesses.Insert(0, $"http://{ha.Host}:5052");
            var http = clients.CreateClient("predbat-probe");
            var results = await Task.WhenAll(guesses.Distinct().Select(u => Probe(http, u, null, TimeSpan.FromSeconds(4), ct)));
            return results.OrderByDescending(r => r.Ok).ToList();
        });
        app.MapGet("/api/setup/meters/detect", async (IServiceProvider services, CancellationToken ct) => await DetectLive(services, ct));
        return app;
    }

    static bool HasAccessKey(SavedSettings saved, IReadOnlyDictionary<string, string?> values) =>
        values.TryGetValue("App:AccessKey", out var key) ? !string.IsNullOrWhiteSpace(key) : saved.External.Contains("App:AccessKey") || saved.Saved.ContainsKey("App:AccessKey");

    static string? LockReason(SavedSettings saved, AppAuthOptions auth) =>
        auth.Demo && saved.External.Contains("App:Demo") && !auth.KeyRequired
            ? "Demo mode is fixed by App__Demo in Joule's environment, so this page can't switch it. Remove that line (or set App__Demo=false) and restart Joule."
            : null;

    /// <summary>
    /// Saving and probing are open to anyone who can use the API. That is the signed-in owner, except in a demo without an access key:
    /// there they are allowed only when the demo isn't pinned by App__Demo (a fresh install), so a public demo can't be taken over.
    /// </summary>
    /// The dashboard's request header is always required here, even without sign-in, so no other page can drive these endpoints.
    static void EnsureCanSave(SavedSettings saved, AppAuthOptions auth, HttpRequest request)
    {
        if (LockReason(saved, auth) is { } reason) throw new DomainException(reason, 403);
        if (!WebSecurity.HasRequestHeader(request)) throw new DomainException("A same-origin dashboard request is required.", 403);
    }

    public static SetupConfigView View(SavedSettings saved, AppAuthOptions auth, bool inContainer)
    {
        var current = saved.Saved;
        var fields = SavedSettings.Fields.Select(f =>
        {
            var source = saved.Source(f.Key);
            var value = source == "environment" ? saved.Effective.GetValueOrDefault(f.Key) : current.GetValueOrDefault(f.Key);
            var pending = source != "environment" && current.GetValueOrDefault(f.Key) != saved.Effective.GetValueOrDefault(f.Key);
            return new SetupConfigField(f.Key, f.EnvVar, f.Kind, f.Secret, f.Secret ? null : f.Key == "Predbat:BaseUrl" || f.Kind == "url" ? SetupEndpoints.SafeAddress(value) ?? value : value,
                !string.IsNullOrEmpty(value), source, pending);
        }).ToList();
        var lockReason = LockReason(saved, auth);
        var keyNeeded = !auth.NoAuth && !saved.External.Contains("App:AccessKey") && !current.ContainsKey("App:AccessKey");
        return new(auth.Demo, lockReason is null, lockReason, inContainer ? "/data/" + SavedSettings.FileName : ShownPath(saved.Path), inContainer, keyNeeded, fields.Any(f => f.Pending), fields);
    }

    /// <summary>The settings file as Setup shows it: relative to Joule's working folder when it is inside it, so the page (and its
    /// screenshots) don't show the home directory.</summary>
    static string ShownPath(string path)
    {
        var relative = Path.GetRelativePath(Environment.CurrentDirectory, path);
        return relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative) ? path : relative;
    }

    static bool SameAddress(string? a, string? b) =>
        Uri.TryCreate(a, UriKind.Absolute, out var x) && Uri.TryCreate(b, UriKind.Absolute, out var y) && Uri.Compare(x, y, UriComponents.SchemeAndServer, UriFormat.Unescaped, StringComparison.OrdinalIgnoreCase) == 0;

    /// <summary>Asks <paramref name="url"/> for Predbat's /api/state and says, in plain words, what answered.</summary>
    public static async Task<PredbatProbe> Probe(HttpClient http, string url, string? token, TimeSpan timeout, CancellationToken ct)
    {
        var shown = SetupEndpoints.SafeAddress(url) ?? url;
        var clock = Stopwatch.StartNew();
        PredbatProbe Result(bool ok, string? version, int entities, string? error) => new(shown, ok, version, entities, error, clock.ElapsedMilliseconds);
        if (!Uri.TryCreate(url.TrimEnd('/') + "/", UriKind.Absolute, out var root) || root.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(root.UserInfo))
            return Result(false, null, 0, "That isn't an http(s) address.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(root, "api/state"));
            if (!string.IsNullOrWhiteSpace(token)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            if ((int)response.StatusCode is >= 300 and < 400) return Result(false, null, 0, $"It redirected (HTTP {(int)response.StatusCode}). Use the address Predbat's web page is served from, usually port 5052.");
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) return Result(false, null, 0, $"Predbat refused (HTTP {(int)response.StatusCode}). If its web interface needs a token, set Predbat__AccessToken.");
            if (!response.IsSuccessStatusCode) return Result(false, null, 0, $"Something answered with HTTP {(int)response.StatusCode}, but not Predbat's web interface.");
            await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
            using var limited = new MemoryStream();
            var buffer = new byte[81920]; int read; long total = 0;
            while ((read = await stream.ReadAsync(buffer, deadline.Token)) > 0)
            {
                total += read;
                if (total > 32 * 1024 * 1024) return Result(false, null, 0, "The answer was too large to be Predbat's state.");
                limited.Write(buffer, 0, read);
            }
            limited.Position = 0;
            using var json = await JsonDocument.ParseAsync(limited, cancellationToken: deadline.Token);
            if (json.RootElement.ValueKind != JsonValueKind.Object) return Result(false, null, 0, "Something answered, but not with Predbat's state.");
            var entities = 0; var predbat = 0; string? version = null;
            foreach (var e in json.RootElement.EnumerateObject())
            {
                entities++;
                if (!e.Name.Contains("predbat", StringComparison.OrdinalIgnoreCase)) continue;
                predbat++;
                if (version is null && Regex.IsMatch(e.Name, @"^[a-z_]+\.predbat_version$") && e.Value.ValueKind == JsonValueKind.Object)
                    version = StateProjection.PredbatVersion(Text(e.Value, "attributes", "installed_version") ?? Text(e.Value, "state"));
            }
            return predbat == 0
                ? Result(false, null, entities, "Something answered with JSON, but it has no Predbat entities. Is this Predbat's web interface (usually port 5052)?")
                : Result(true, version, entities, null);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return Result(false, null, 0, $"No answer within {timeout.TotalSeconds:0} seconds."); }
        catch (HttpRequestException e) { return Result(false, null, 0, Explain(e)); }
        catch (JsonException) { return Result(false, null, 0, "Something answered, but not with Predbat's JSON. Check the port: Predbat's web interface is usually 5052."); }
        catch (IOException) { return Result(false, null, 0, "The connection dropped before Predbat finished answering."); }
    }

    static string? Text(JsonElement e, params string[] path)
    {
        foreach (var p in path) { if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(p, out e)) return null; }
        return e.ValueKind == JsonValueKind.String ? e.GetString() : null;
    }

    static string Explain(HttpRequestException e)
    {
        var socket = e.InnerException as SocketException ?? e.InnerException?.InnerException as SocketException;
        return socket?.SocketErrorCode switch
        {
            SocketError.HostNotFound or SocketError.NoData or SocketError.TryAgain => "Joule can't find that name. From inside Docker, use the host's IP address or a container name on the same network.",
            SocketError.ConnectionRefused => "Nothing is listening there. Check the port (Predbat's web interface is usually 5052) and, for the add-on, that the port is opened in its Network settings.",
            SocketError.TimedOut or SocketError.HostUnreachable or SocketError.NetworkUnreachable => "That address can't be reached from Joule.",
            _ => e.HttpRequestError switch
            {
                HttpRequestError.SecureConnectionError => "The HTTPS connection failed. Predbat's own web interface uses plain http.",
                HttpRequestError.NameResolutionError => "Joule can't find that name. From inside Docker, use the host's IP address or a container name on the same network.",
                HttpRequestError.ConnectionError => "Joule couldn't connect to that address.",
                _ => "Joule couldn't read that address.",
            }
        };
    }


    // ---- Meter suggestions --------------------------------------------------------------------------------------------------

    /// <summary>
    /// Predbat's whole /api/state, which mirrors every Home Assistant entity, read now from the configured address. Joule stores only
    /// Predbat's own entities, so suggestions need this live copy; it is used here and never stored or sent to the browser whole.
    /// </summary>
    internal static async Task<string?> ReadPredbatState(HttpClient http, IConfiguration configuration, CancellationToken ct)
    {
        if (PredbatRoot(configuration) is not { } root) return null;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(root, "api/state"));
            if (configuration["Predbat:AccessToken"] is { Length: > 0 } token) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > 32_000_000) return null;
            return await response.Content.ReadAsStringAsync(deadline.Token);
        }
        catch (Exception e) when (e is HttpRequestException or IOException || e is OperationCanceledException && !ct.IsCancellationRequested) { return null; }
    }

    static Uri? PredbatRoot(IConfiguration configuration) =>
        Uri.TryCreate(configuration["Predbat:BaseUrl"]?.TrimEnd('/') + "/", UriKind.Absolute, out var root) && root.Scheme is "http" or "https" && string.IsNullOrEmpty(root.UserInfo) ? root : null;

    /// <summary>Detection against the live Predbat: its whole entity list now, and its apps.yaml from MCP, its web interface or a mounted copy.</summary>
    public static async Task<MeterDetection> DetectLive(IServiceProvider services, CancellationToken ct)
    {
        var configuration = services.GetRequiredService<IConfiguration>();
        var http = services.GetRequiredService<IHttpClientFactory>().CreateClient("predbat-probe");
        return await Detect(services.GetRequiredService<DataStore>(), services.GetService<IPredbatMcpClient>(), configuration, services.GetService<HomeAssistantOptions>(), ct,
            await ReadPredbatState(http, configuration, ct), http);
    }

    /// <summary>
    /// The sensor for each meter. Predbat's apps.yaml comes first (the key that names it, resolved the way Predbat resolves it),
    /// then a name-and-unit match. A suggestion is <see cref="MeterSuggestion.Confident"/>, so Joule can use it without asking,
    /// when apps.yaml names exactly one sensor that Predbat sees with a unit Joule can read, or when one name match is clearly
    /// ahead; and never when the same sensor would be two meters. Several sensors (Predbat adds up one per inverter) are a choice.
    /// </summary>
    /// <param name="liveState">Predbat's /api/state read now; without it, the newest stored copy (which holds only Predbat's own entities).</param>
    /// <param name="predbat">For reading apps.yaml from Predbat's web interface when MCP isn't set up.</param>
    public static async Task<MeterDetection> Detect(DataStore db, IPredbatMcpClient? mcp, IConfiguration configuration, HomeAssistantOptions? options, CancellationToken ct, string? liveState = null, HttpClient? predbat = null)
    {
        // Every Home Assistant entity Predbat sees.
        JsonDocument? state = null;
        try { if ((liveState ?? db.ReadLatestSourceState()) is { Length: > 0 and < 32_000_000 } raw) state = JsonDocument.Parse(raw); }
        catch (Exception e) when (e is JsonException or InvalidOperationException or IOException) { }
        using var _ = state;
        var root = state?.RootElement ?? default;
        var (apps, source, error) = await AppsYaml(mcp, configuration, predbat, ct);
        var args = apps is null ? [] : AppsArgs(apps);
        var ranked = state is null ? [] : SensorCandidates.Ranked(root);
        // A worked-out sensor (the Octopus standing charge) counts as mapped only once it has given a reading.
        Dictionary<string, string> current = [];
        try { current = options?.ShownEntities(db.ReadLatestTelemetry()) ?? []; } catch (Exception e) when (e is not OperationCanceledException) { current = options?.Entities ?? []; }
        var picks = SensorCandidates.EnvNames.Keys.Select(metric =>
        {
            string? entity = null, from = null; var confident = false; List<string> others = [];
            foreach (var appsKey in AppsKeys.GetValueOrDefault(metric) ?? [])
            {
                if (!args.TryGetValue(appsKey, out var values)) continue;
                var resolved = values.Select(v => ResolveAll(v, root, args)).ToList();
                // Like Predbat, the first match of each value; one value per inverter.
                var named = resolved.Where(r => r.Count > 0).Select(r => r[0]).Distinct(StringComparer.Ordinal).ToList();
                if (named.Count == 0) continue;
                entity = named[0]; from = $"{appsKey} in apps.yaml"; others = named.Skip(1).ToList();
                confident = named.Count == 1 && resolved.All(r => r.Count <= 1) && Has(root, entity) && SensorCandidates.UnitFits(metric, Text(root, entity, "attributes", "unit_of_measurement"));
                break;
            }
            var guesses = ranked.GetValueOrDefault(metric) ?? [];
            if (entity is null && guesses.Count > 0) { entity = guesses[0].Candidate.Entity; from = "name and unit match"; confident = SensorCandidates.Clear(guesses); }
            var alternatives = others.Select(e => Candidate(root, e)).Concat(guesses.Select(g => g.Candidate))
                .Where(a => a.Entity != entity).DistinctBy(a => a.Entity).Take(4).ToList();
            return (Metric: metric, Entity: entity, From: from, Confident: confident, Alternatives: alternatives);
        }).ToList();
        // One sensor can't be two meters: a clash is the person's choice.
        var clashes = picks.Where(p => p.Confident && p.Entity is not null).GroupBy(p => p.Entity!).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet(StringComparer.Ordinal);
        var meters = picks.Select(p =>
        {
            var key = "HomeAssistant:Entities:" + SensorCandidates.EnvNames[p.Metric];
            var reading = p.Entity is not null && root.ValueKind == JsonValueKind.Object && root.TryGetProperty(p.Entity, out var e) ? e : default;
            return new MeterSuggestion(p.Metric, key, key.Replace(":", "__"), current.GetValueOrDefault(p.Metric), p.Entity, p.From,
                reading.ValueKind == JsonValueKind.Object ? Text(reading, "state") : null, reading.ValueKind == JsonValueKind.Object ? Text(reading, "attributes", "unit_of_measurement") : null,
                p.Alternatives)
            {
                Confident = p.Confident && !clashes.Contains(p.Entity!),
                Auto = options?.Detected.ContainsKey(p.Metric) ?? false,
                Declined = options?.Declined.Contains(p.Metric) ?? false,
            };
        }).ToList();
        return new(source, error, state is not null, meters) { StandingChargePence = FixedStandingCharge(args) };
    }

    /// <summary>metric_standing_charge given as a number of pounds a day (Predbat allows 0.50 for 50p), in pence; null when it names a sensor.</summary>
    static double? FixedStandingCharge(Dictionary<string, List<string>> args) =>
        args.TryGetValue("metric_standing_charge", out var values) && values is [var only] && double.TryParse(only, NumberStyles.Float, CultureInfo.InvariantCulture, out var pounds) && pounds is >= 0 and < 10
            ? Math.Round(pounds * 100, 3) : null;

    static bool Has(JsonElement entities, string entity) => entities.ValueKind == JsonValueKind.Object && entities.TryGetProperty(entity, out var e) && e.ValueKind == JsonValueKind.Object;
    static SensorCandidate Candidate(JsonElement entities, string entity) => Has(entities, entity)
        ? new(entity, Text(entities, entity, "attributes", "friendly_name"), Text(entities, entity, "attributes", "unit_of_measurement"), Text(entities, entity, "state"))
        : new(entity, null, null, null);

    /// <summary>apps.yaml as text: from Predbat's MCP when it's set up, else Predbat's web interface (the live settings, credentials
    /// masked by Predbat), else a mounted copy under ConfigFiles:Root.</summary>
    static async Task<(string? Text, string? Source, string? Error)> AppsYaml(IPredbatMcpClient? mcp, IConfiguration configuration, HttpClient? predbat, CancellationToken ct)
    {
        string? error = null;
        if (mcp?.Configured == true)
        {
            try
            {
                using var args = JsonDocument.Parse("{\"masked\":true}");
                var result = await mcp.CallReadOnlyAsync("get_apps", args.RootElement, ct);
                if (result.Success && SetupEndpoints.AppsText(result.ResultJson) is { } text) return (text, "Predbat's MCP", null);
                error = result.Error ?? "Predbat's MCP didn't return apps.yaml.";
            }
            catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested) { error = "Predbat's MCP didn't answer."; }
        }
        if (predbat is not null && await LiveApps(predbat, configuration, ct) is { } live) return (live, "Predbat's web interface", null);
        var rootDir = configuration["ConfigFiles:Root"];
        if (!string.IsNullOrWhiteSpace(rootDir))
            foreach (var name in new[] { "apps.yaml", "apps.yml" })
            {
                var path = Path.Combine(rootDir, name);
                try { if (File.Exists(path) && new FileInfo(path).Length < 4 * 1024 * 1024) return (await File.ReadAllTextAsync(path, ct), "apps.yaml in ConfigFiles:Root", null); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { error ??= "apps.yaml couldn't be read."; }
            }
        return (null, null, error);
    }

    /// <summary>
    /// Predbat's /debug_apps_live: apps.yaml rebuilt from its live settings, with patterns already resolved to the entities Predbat
    /// uses and credentials masked (Predbat masks unless asked not to). Null when this Predbat doesn't serve it.
    /// </summary>
    static async Task<string?> LiveApps(HttpClient http, IConfiguration configuration, CancellationToken ct)
    {
        if (PredbatRoot(configuration) is not { } root) return null;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(root, "debug_apps_live"));
            if (configuration["Predbat:AccessToken"] is { Length: > 0 } token) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > 4_000_000) return null;
            var text = await response.Content.ReadAsStringAsync(deadline.Token);
            return text.Length < 4_000_000 && AppsArgs(text).Keys.Any(k => AppsKeys.Values.Any(keys => keys.Contains(k))) ? text : null;
        }
        catch (Exception e) when (e is HttpRequestException or IOException || e is OperationCanceledException && !ct.IsCancellationRequested) { return null; }
    }

    /// <summary>The values of the meter keys in Predbat's apps.yaml (or its JSON form). See <see cref="AppsArgs"/>.</summary>
    public static Dictionary<string, List<string>> AppsValues(string text)
    {
        var wanted = AppsKeys.Values.SelectMany(x => x).ToHashSet(StringComparer.Ordinal);
        return AppsArgs(text).Where(x => wanted.Contains(x.Key)).ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal);
    }

    /// <summary>
    /// Every simple setting in Predbat's apps.yaml (or its JSON form from MCP), as written: a single value, a list (one per inverter),
    /// a {template} or a re:regex. The first occurrence of a key wins. A small reader for the flat key: value / key: [- item] shape
    /// apps.yaml uses; nothing else is parsed. Empty and null values are left out.
    /// </summary>
    public static Dictionary<string, List<string>> AppsArgs(string text)
    {
        var result = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        void Add(string key, List<string> values)
        {
            values = values.Where(v => v.Length > 0 && v is not ("null" or "~" or "None")).ToList();
            if (values.Count > 0) result.TryAdd(key, values);
        }
        if (text.TrimStart().StartsWith('{'))
        {
            try
            {
                using var json = JsonDocument.Parse(text);
                static string? Value(JsonElement v) => v.ValueKind switch { JsonValueKind.String => v.GetString(), JsonValueKind.Number => v.GetRawText(), JsonValueKind.True => "True", JsonValueKind.False => "False", _ => null };
                void Walk(JsonElement e, int depth)
                {
                    if (depth > 4 || e.ValueKind != JsonValueKind.Object) return;
                    foreach (var p in e.EnumerateObject())
                    {
                        if (p.Value.ValueKind == JsonValueKind.Object) Walk(p.Value, depth + 1);
                        else if (p.Value.ValueKind == JsonValueKind.Array) Add(p.Name, p.Value.EnumerateArray().Select(Value).OfType<string>().ToList());
                        else if (Value(p.Value) is { } single) Add(p.Name, [single]);
                    }
                }
                Walk(json.RootElement, 0);
            }
            catch (JsonException) { }
            return result;
        }
        var lines = text.Replace("\r", "").Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var m = Regex.Match(lines[i], @"^(\s*)([A-Za-z0-9_]+)\s*:\s*(.*)$");
            if (!m.Success || result.ContainsKey(m.Groups[2].Value)) continue;
            var indent = m.Groups[1].Value.Length;
            var inline = Scalar(m.Groups[3].Value);
            var values = new List<string>();
            if (inline.StartsWith('[') && inline.EndsWith(']')) values.AddRange(inline[1..^1].Split(',').Select(Scalar));
            else if (inline.Length > 0) values.Add(inline);
            else
                for (var j = i + 1; j < lines.Length; j++)
                {
                    if (string.IsNullOrWhiteSpace(lines[j]) || lines[j].TrimStart().StartsWith('#')) continue;
                    var item = Regex.Match(lines[j], @"^(\s*)-\s*(.+)$");
                    if (!item.Success || item.Groups[1].Value.Length < indent) break;
                    values.Add(Scalar(item.Groups[2].Value));
                }
            Add(m.Groups[2].Value, values);
        }
        return result;
    }

    /// <summary>A YAML scalar without its comment and quotes.</summary>
    static string Scalar(string raw)
    {
        var v = raw.Trim();
        if (v.Length > 1 && (v[0] == '\'' || v[0] == '"'))
        {
            var close = v.IndexOf(v[0], 1);
            return close > 0 ? v[1..close] : v[1..];
        }
        var hash = v.IndexOf(" #", StringComparison.Ordinal);
        return (hash >= 0 ? v[..hash] : v).Trim();
    }

    static readonly Regex EntityShape = new("^[a-z_]+\\.[a-z0-9_]+$", RegexOptions.CultureInvariant);

    /// <summary>The first entity an apps.yaml value names, or null. See <see cref="ResolveAll"/>.</summary>
    public static string? Resolve(string value, JsonElement entities, IReadOnlyDictionary<string, List<string>>? args = null) =>
        ResolveAll(value, entities, args).FirstOrDefault();

    /// <summary>
    /// The entities an apps.yaml value names, checked against the entities Predbat sees, in Predbat's own order: a plain entity id as
    /// is; a re:regex matched the way Predbat matches it (the whole name, keeping the first group when there is one); a {template}
    /// filled from the other settings (such as geserial, itself often a regex whose group is the inverter serial) or, failing that,
    /// with a wildcard. More than one match means Predbat's choice depends on its entity order, so it isn't a sure answer.
    /// </summary>
    public static List<string> ResolveAll(string value, JsonElement entities, IReadOnlyDictionary<string, List<string>>? args = null)
    {
        var v = value.Trim();
        if (EntityShape.IsMatch(v)) return [v];
        if (entities.ValueKind != JsonValueKind.Object) return [];
        var names = entities.EnumerateObject().Select(e => e.Name).Where(n => EntityShape.IsMatch(n)).ToList();
        try
        {
            if (v.StartsWith("re:", StringComparison.Ordinal)) return Matches(v[3..], names).Where(n => EntityShape.IsMatch(n)).Distinct(StringComparer.Ordinal).ToList();
            if (!v.Contains('{') || !Regex.IsMatch(v, "^[a-z_]+\\.[a-z0-9_{}]+$")) return [];
            // Fill each {name} from the setting of that name, as Predbat does.
            var filled = Regex.Replace(v, @"\{([a-z0-9_]+)\}", m =>
            {
                if (args?.GetValueOrDefault(m.Groups[1].Value) is not [var setting]) return m.Value;
                return setting.StartsWith("re:", StringComparison.Ordinal) ? Matches(setting[3..], names).FirstOrDefault() ?? m.Value : setting;
            });
            if (EntityShape.IsMatch(filled)) return [filled];
            var pattern = new Regex("^" + Regex.Replace(Regex.Escape(filled), @"\\\{[a-z0-9_]+}", "[a-z0-9_]+?") + "$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
            return names.Where(n => pattern.IsMatch(n)).ToList();
        }
        catch (Exception e) when (e is ArgumentException or RegexMatchTimeoutException) { return []; }
    }

    /// <summary>Predbat's resolve_arg_re: re.search("^" + pattern + "$", name), keeping group 1 when the pattern has a group.</summary>
    static IEnumerable<string> Matches(string pattern, List<string> names)
    {
        var regex = new Regex("^" + pattern + "$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
        foreach (var name in names)
        {
            var m = regex.Match(name);
            if (m.Success) yield return m.Groups.Count > 1 ? m.Groups[1].Value : m.Value;
        }
    }
}
