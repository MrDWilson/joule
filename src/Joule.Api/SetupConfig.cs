using System.Diagnostics;
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
public sealed record MeterSuggestion(string Metric, string Key, string EnvVar, string? Current, string? Entity, string? From, string? State, string? Unit, List<SensorCandidate> Alternatives);
public sealed record MeterDetection(string? AppsSource, string? AppsError, bool HaveEntities, List<MeterSuggestion> Meters);

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
        app.MapGet("/api/setup/meters/detect", async (IServiceProvider services, IConfiguration configuration, IHttpClientFactory clients, CancellationToken ct) =>
            await Detect(services.GetRequiredService<DataStore>(), services.GetService<IPredbatMcpClient>(), configuration, services.GetService<HomeAssistantOptions>(), ct,
                await ReadPredbatState(clients.CreateClient("predbat-probe"), configuration, ct)));
        return app;
    }

    static bool HasAccessKey(SavedSettings saved, IReadOnlyDictionary<string, string?> values) =>
        values.TryGetValue("App:AccessKey", out var key) ? !string.IsNullOrWhiteSpace(key) : saved.External.Contains("App:AccessKey") || saved.Saved.ContainsKey("App:AccessKey");

    internal static string? LockReasonFor(SavedSettings saved, AppAuthOptions auth) => LockReason(saved, auth);
    static string? LockReason(SavedSettings saved, AppAuthOptions auth) =>
        auth.Demo && saved.External.Contains("App:Demo") && !auth.KeyRequired
            ? "Demo mode is fixed by App__Demo in Joule's environment, so this page can't switch it. Remove that line (or set App__Demo=false) and restart Joule."
            : null;

    /// <summary>
    /// Saving and probing are open to anyone who can use the API. That is the signed-in owner, except in a demo without an access key:
    /// there they are allowed only when the demo isn't pinned by App__Demo (a fresh install), so a public demo can't be taken over.
    /// </summary>
    /// The dashboard's request header is always required here, even without sign-in, so no other page can drive these endpoints.
    internal static void EnsureCanSave(SavedSettings saved, AppAuthOptions auth, HttpRequest request)
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
            var pending = !f.Live && source != "environment" && current.GetValueOrDefault(f.Key) != saved.Effective.GetValueOrDefault(f.Key);
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
    static async Task<string?> ReadPredbatState(HttpClient http, IConfiguration configuration, CancellationToken ct)
    {
        if (!Uri.TryCreate(configuration["Predbat:BaseUrl"]?.TrimEnd('/') + "/", UriKind.Absolute, out var root) || root.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(root.UserInfo)) return null;
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

    /// <param name="liveState">Predbat's /api/state read now; without it, the newest stored copy (which holds only Predbat's own entities).</param>
    public static async Task<MeterDetection> Detect(DataStore db, IPredbatMcpClient? mcp, IConfiguration configuration, HomeAssistantOptions? options, CancellationToken ct, string? liveState = null)
    {
        // Every Home Assistant entity Predbat sees.
        JsonDocument? state = null;
        try { if ((liveState ?? db.ReadLatestSourceState()) is { Length: > 0 and < 32_000_000 } raw) state = JsonDocument.Parse(raw); }
        catch (Exception e) when (e is JsonException or InvalidOperationException or IOException) { }
        using var _ = state;
        var root = state?.RootElement ?? default;
        var (apps, source, error) = await AppsYaml(mcp, configuration, ct);
        var fromApps = apps is null ? [] : AppsValues(apps);
        var guesses = state is null ? [] : SensorCandidates.Find(root, 4);
        var current = options?.Entities ?? [];
        var meters = SensorCandidates.EnvNames.Select(x =>
        {
            var metric = x.Key; var key = "HomeAssistant:Entities:" + x.Value;
            string? entity = null, from = null;
            foreach (var appsKey in AppsKeys.GetValueOrDefault(metric) ?? [])
            {
                if (!fromApps.TryGetValue(appsKey, out var values)) continue;
                foreach (var value in values)
                {
                    if (Resolve(value, root) is { } resolved) { entity = resolved; from = $"{appsKey} in apps.yaml"; break; }
                }
                if (entity is not null) break;
            }
            var alternatives = guesses.GetValueOrDefault(metric) ?? [];
            if (entity is null && alternatives.Count > 0) { entity = alternatives[0].Entity; from = "name and unit match"; }
            var reading = entity is not null && root.ValueKind == JsonValueKind.Object && root.TryGetProperty(entity, out var e) ? e : default;
            return new MeterSuggestion(metric, key, key.Replace(":", "__"), current.GetValueOrDefault(metric), entity, from,
                reading.ValueKind == JsonValueKind.Object ? Text(reading, "state") : null, reading.ValueKind == JsonValueKind.Object ? Text(reading, "attributes", "unit_of_measurement") : null,
                alternatives.Where(a => a.Entity != entity).ToList());
        }).ToList();
        return new(source, error, state is not null, meters);
    }

    /// <summary>apps.yaml as text: from Predbat's MCP when it's set up, otherwise from a mounted copy under ConfigFiles:Root.</summary>
    static async Task<(string? Text, string? Source, string? Error)> AppsYaml(IPredbatMcpClient? mcp, IConfiguration configuration, CancellationToken ct)
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
    /// The values of the meter keys in Predbat's apps.yaml (or its JSON form), as written: a single entity, a list (one per inverter),
    /// a {template} or a re:regex. A small reader for the flat key: value / key: [- item] shape apps.yaml uses; nothing else is parsed.
    /// </summary>
    public static Dictionary<string, List<string>> AppsValues(string text)
    {
        var wanted = AppsKeys.Values.SelectMany(x => x).ToHashSet(StringComparer.Ordinal);
        var result = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        if (text.TrimStart().StartsWith('{'))
        {
            try
            {
                using var json = JsonDocument.Parse(text);
                void Walk(JsonElement e, int depth)
                {
                    if (depth > 4 || e.ValueKind != JsonValueKind.Object) return;
                    foreach (var p in e.EnumerateObject())
                    {
                        if (wanted.Contains(p.Name) && !result.ContainsKey(p.Name))
                        {
                            var values = p.Value.ValueKind == JsonValueKind.String ? [p.Value.GetString()!] : p.Value.ValueKind == JsonValueKind.Array ? p.Value.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString()!).ToList() : [];
                            if (values.Count > 0) result[p.Name] = values;
                        }
                        else Walk(p.Value, depth + 1);
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
            if (!m.Success || !wanted.Contains(m.Groups[2].Value) || result.ContainsKey(m.Groups[2].Value)) continue;
            var indent = m.Groups[1].Value.Length;
            var inline = Scalar(m.Groups[3].Value);
            var values = new List<string>();
            if (inline.StartsWith('[') && inline.EndsWith(']')) values.AddRange(inline[1..^1].Split(',').Select(Scalar).Where(v => v.Length > 0));
            else if (inline.Length > 0) values.Add(inline);
            else
                for (var j = i + 1; j < lines.Length; j++)
                {
                    if (string.IsNullOrWhiteSpace(lines[j]) || lines[j].TrimStart().StartsWith('#')) continue;
                    var item = Regex.Match(lines[j], @"^(\s*)-\s*(.+)$");
                    if (!item.Success || item.Groups[1].Value.Length < indent) break;
                    values.Add(Scalar(item.Groups[2].Value));
                }
            if (values.Count > 0) result[m.Groups[2].Value] = values;
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

    /// <summary>
    /// The entity an apps.yaml value names, checked against the entities Predbat sees: a plain entity id as is, a {template} (such as
    /// {geserial}) or a re:regex (matched from the start, as Predbat does) against the entity list, first match in order. Null when nothing matches.
    /// </summary>
    public static string? Resolve(string value, JsonElement entities)
    {
        var v = value.Trim();
        var plain = Regex.IsMatch(v, "^[a-z_]+\\.[a-z0-9_]+$");
        if (entities.ValueKind != JsonValueKind.Object) return plain ? v : null;
        if (plain) return v;
        Regex pattern;
        try
        {
            pattern = v.StartsWith("re:", StringComparison.Ordinal)
                ? new Regex("^(?:" + v[3..] + ")", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100))
                : v.Contains('{') && Regex.IsMatch(v, "^[a-z_]+\\.[a-z0-9_{}]+$")
                    ? new Regex("^" + Regex.Replace(Regex.Escape(v), @"\\\{[a-z0-9_]+}", "[a-z0-9_]+?") + "$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100))
                    : null!;
        }
        catch (ArgumentException) { return null; }
        if (pattern is null) return null;
        try { return entities.EnumerateObject().Select(e => e.Name).Where(n => Regex.IsMatch(n, "^[a-z_]+\\.[a-z0-9_]+$") && pattern.IsMatch(n)).Order(StringComparer.Ordinal).FirstOrDefault(); }
        catch (RegexMatchTimeoutException) { return null; }
    }
}
