using System.Collections.Concurrent;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.AspNetCore.StaticFiles;

namespace Joule;

/// <summary>The running build's version, shown by /api/health and the UI footer.</summary>
public static class AppVersion
{
    static readonly string informational = typeof(AppVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
    /// <summary>Semantic version without build metadata, for example "1.0.0".</summary>
    public static string Current { get; } = informational.Split('+')[0];
    /// <summary>Short source revision when the build recorded one, otherwise null.</summary>
    public static string? Build { get; } = informational.Contains('+') ? informational.Split('+')[1] is { Length: > 0 } sha ? sha[..Math.Min(12, sha.Length)] : null : null;
}

/// <summary>Deployment authentication settings, validated once at startup.</summary>
/// <param name="SecureCookie">Secure flag on the sign-in cookie: true/false from App__SecureCookie, or null to follow the request scheme.</param>
public sealed record AppAuthOptions(bool Demo, bool NoAuth, string? AccessKey, TimeSpan SessionLifetime, string FrameAncestors, bool? SecureCookie = null)
{
    public const int MinimumKeyLength = 16;
    public bool KeyRequired => !NoAuth && !string.IsNullOrWhiteSpace(AccessKey);

    /// <summary>Reads App:* settings. Returns a one-line, user-facing error instead of throwing, so startup can exit cleanly.</summary>
    public static (AppAuthOptions? Options, string? Error) From(IConfiguration configuration)
    {
        var demoText = configuration["App:Demo"]?.Trim();
        var demo = true;
        if (!string.IsNullOrEmpty(demoText) && !bool.TryParse(demoText, out demo)) return (null, $"App__Demo must be true or false (it is \"{demoText}\").");
        var mode = configuration["App:AuthMode"] ?? "AccessKey";
        if (!string.Equals(mode, "AccessKey", StringComparison.OrdinalIgnoreCase) && !string.Equals(mode, "None", StringComparison.OrdinalIgnoreCase))
            return (null, $"App__AuthMode must be AccessKey or None (it is \"{mode}\").");
        var noAuth = string.Equals(mode, "None", StringComparison.OrdinalIgnoreCase);
        var key = configuration["App:AccessKey"];
        if (!noAuth && string.IsNullOrWhiteSpace(key) && !demo)
            return (null, "Set App__AccessKey (at least 16 characters, for example the output of `openssl rand -base64 24`) before starting live mode, or set App__AuthMode=None when a proxy in front of Joule handles sign-in.");
        if (!noAuth && !string.IsNullOrWhiteSpace(key) && key.Trim().Length < MinimumKeyLength)
            return (null, $"App__AccessKey is too short. Use at least {MinimumKeyLength} characters, for example the output of `openssl rand -base64 24`.");
        var days = configuration.GetValue("App:SessionDays", 30.0);
        if (!double.IsFinite(days) || days < 0 || days > 400) return (null, "App__SessionDays must be between 0 and 400 (0 turns remembered sign-in off).");
        var frame = (configuration["App:FrameAncestors"] ?? "").Trim();
        if (frame.Length > 0 && !frame.Split(' ', StringSplitOptions.RemoveEmptyEntries).All(x => x is "'self'" || Uri.TryCreate(x, UriKind.Absolute, out var u) && u.Scheme is "http" or "https"))
            return (null, "App__FrameAncestors must be a space-separated list of http(s) origins or 'self', for example https://homeassistant.example.com.");
        // Behind a TLS-terminating proxy Kestrel sees plain HTTP, so the owner can force the Secure flag.
        var secure = (configuration["App:SecureCookie"] ?? "").Trim();
        bool? secureCookie = null;
        if (secure.Length > 0 && !secure.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            if (!bool.TryParse(secure, out var flag)) return (null, $"App__SecureCookie must be true, false or auto (it is \"{secure}\").");
            secureCookie = flag;
        }
        return (new(demo, noAuth, noAuth ? null : key?.Trim(), TimeSpan.FromDays(days), frame.Length == 0 ? "'none'" : frame, secureCookie), null);
    }

    /// <summary>True when any configured URL listens on every interface.</summary>
    public static bool ListensOnAllInterfaces(string? urls) =>
        (urls ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(u => u.Contains("://0.0.0.0", StringComparison.Ordinal) || u.Contains("://*", StringComparison.Ordinal) || u.Contains("://+", StringComparison.Ordinal) || u.Contains("://[::]", StringComparison.Ordinal));
}

/// <summary>
/// Remembered sign-in. After a correct access key, the browser receives an HttpOnly SameSite=Strict cookie holding an expiry
/// and an HMAC derived from the key, so a new tab or a phone home-screen app does not ask again. Rotating the key invalidates
/// every session. Mutations authenticated only by the cookie must still carry the dashboard request header.
/// </summary>
public sealed class SessionCookies(AppAuthOptions options, TimeProvider? clock = null)
{
    public const string Name = "joule_session";
    readonly TimeProvider time = clock ?? TimeProvider.System;
    readonly byte[] signingKey = options.AccessKey is { Length: > 0 } key ? HMACSHA256.HashData(Encoding.UTF8.GetBytes(key), "joule-session-v1"u8) : [];
    public bool Enabled => signingKey.Length > 0 && options.SessionLifetime > TimeSpan.Zero;

    public string Issue(DateTimeOffset? at = null)
    {
        var expires = (at ?? time.GetUtcNow()).Add(options.SessionLifetime).ToUnixTimeSeconds();
        return $"{expires}.{Sign(expires)}";
    }
    /// <summary>Expiry of a well-formed, correctly signed and unexpired value; otherwise null.</summary>
    public DateTimeOffset? Validate(string? value)
    {
        if (!Enabled || string.IsNullOrEmpty(value) || value.Length > 128) return null;
        var dot = value.IndexOf('.');
        if (dot <= 0 || !long.TryParse(value.AsSpan(0, dot), out var expires)) return null;
        var expected = Encoding.ASCII.GetBytes(Sign(expires));
        if (!CryptographicOperations.FixedTimeEquals(expected, Encoding.ASCII.GetBytes(value[(dot + 1)..]))) return null;
        var at = DateTimeOffset.FromUnixTimeSeconds(expires);
        // Reject expiries beyond the configured lifetime too: a shortened lifetime applies to existing cookies.
        var now = time.GetUtcNow();
        return at > now && at <= now.Add(options.SessionLifetime).AddMinutes(5) ? at : null;
    }
    string Sign(long expires) => Base64Url(HMACSHA256.HashData(signingKey, Encoding.ASCII.GetBytes($"v1|{expires}")));
    static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public void Append(HttpContext context) => context.Response.Cookies.Append(Name, Issue(), new CookieOptions
    {
        HttpOnly = true, SameSite = SameSiteMode.Strict, Path = "/", IsEssential = true,
        Secure = Secure(context), MaxAge = options.SessionLifetime
    });
    public void Clear(HttpContext context) => context.Response.Cookies.Delete(Name, new CookieOptions { HttpOnly = true, SameSite = SameSiteMode.Strict, Path = "/", Secure = Secure(context) });
    /// <summary>App__SecureCookie when set; otherwise whether Kestrel itself saw HTTPS (forwarded headers are not trusted).</summary>
    public bool Secure(HttpContext context) => options.SecureCookie ?? context.Request.IsHttps;
}

/// <summary>
/// Slows down key guessing. Five distinct wrong keys from one address inside ten minutes are free (typos happen); after that
/// each further new guess must wait an exponentially growing delay capped at five minutes. A correct key clears the record.
/// Only distinct keys count: a tab that keeps polling with the same mistyped (or since-rotated) key is not guessing, so it
/// gets a plain 401 every time instead of pushing every client behind the same proxy into a lockout. Keys are remembered
/// only as HMAC fingerprints under a per-process random key, never in clear.
/// </summary>
public sealed class AccessKeyThrottle(TimeProvider? clock = null)
{
    public const int FreeAttempts = 5;
    /// <summary>Distinct wrong keys remembered per address; later new keys still count, they are just not remembered.</summary>
    public const int RememberedKeys = 64;
    static readonly TimeSpan window = TimeSpan.FromMinutes(10), maxDelay = TimeSpan.FromMinutes(5);
    readonly TimeProvider time = clock ?? TimeProvider.System;
    readonly byte[] fingerprintKey = RandomNumberGenerator.GetBytes(32);
    readonly ConcurrentDictionary<string, Entry> failures = new();

    sealed class Entry(DateTimeOffset now)
    {
        public int Failures;
        public DateTimeOffset Last = now;
        public readonly HashSet<string> Keys = new(StringComparer.Ordinal);
    }

    /// <summary>Opaque in-memory fingerprint of a presented key.</summary>
    public string Fingerprint(string key) => Convert.ToBase64String(HMACSHA256.HashData(fingerprintKey, Encoding.UTF8.GetBytes(key)));

    /// <summary>Seconds the caller must wait before another attempt counts, or 0 when it may try now.</summary>
    public int RetryAfterSeconds(string client)
    {
        if (!failures.TryGetValue(client, out var entry)) return 0;
        var now = time.GetUtcNow();
        lock (entry)
        {
            if (now - entry.Last > window) { failures.TryRemove(new(client, entry)); return 0; }
            if (entry.Failures < FreeAttempts) return 0;
            var until = entry.Last + Delay(entry.Failures);
            return until > now ? (int)Math.Ceiling((until - now).TotalSeconds) : 0;
        }
    }

    /// <summary>True when this address already presented this wrong key inside the current window (a repeat, not a guess).</summary>
    public bool AlreadyCounted(string client, string fingerprint)
    {
        if (!failures.TryGetValue(client, out var entry)) return false;
        lock (entry) return time.GetUtcNow() - entry.Last <= window && entry.Keys.Contains(fingerprint);
    }

    /// <summary>Records a wrong key. A repeat of a key already counted in the window is ignored; null always counts.</summary>
    public void Failure(string client, string? fingerprint = null)
    {
        var now = time.GetUtcNow();
        if (failures.Count > 10_000)
            foreach (var stale in failures.Where(x => now - x.Value.Last > window).ToList()) failures.TryRemove(stale);
        while (true)
        {
            var entry = failures.GetOrAdd(client, _ => new Entry(now));
            lock (entry)
            {
                // Removed by a concurrent expiry or success: start again on the live entry.
                if (!failures.TryGetValue(client, out var current) || !ReferenceEquals(current, entry)) continue;
                if (now - entry.Last > window) { entry.Failures = 0; entry.Keys.Clear(); }
                if (fingerprint is not null)
                {
                    if (entry.Keys.Contains(fingerprint)) return;
                    if (entry.Keys.Count < RememberedKeys) entry.Keys.Add(fingerprint);
                }
                entry.Failures++;
                entry.Last = now;
                return;
            }
        }
    }
    public void Success(string client) => failures.TryRemove(client, out _);
    static TimeSpan Delay(int count) => TimeSpan.FromSeconds(Math.Min(maxDelay.TotalSeconds, Math.Pow(2, Math.Min(20, count - FreeAttempts + 1))));
}

public static class WebSecurity
{
    /// <summary>Dashboard request headers. X-Joule-Request is the new name; X-PredbatAI-Request is still accepted.</summary>
    public static readonly string[] RequestHeaders = ["X-Joule-Request", "X-PredbatAI-Request"];
    public static bool HasRequestHeader(HttpRequest request) => RequestHeaders.Any(h => request.Headers[h] == "1");

    public static string ContentSecurityPolicy(AppAuthOptions options) =>
        // Recharts and Radix set inline style attributes and <style> elements; scripts stay same-origin only.
        $"default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data: blob:; font-src 'self' data:; connect-src 'self'; manifest-src 'self'; worker-src 'self'; object-src 'none'; base-uri 'self'; form-action 'self'; frame-ancestors {options.FrameAncestors}";
    public const string PermissionsPolicy = "accelerometer=(), camera=(), geolocation=(), gyroscope=(), magnetometer=(), microphone=(), payment=(), usb=(), bluetooth=(), serial=(), hid=(), interest-cohort=()";

    public static void ApplyHeaders(HttpContext context, AppAuthOptions options)
    {
        var headers = context.Response.Headers;
        headers["X-Content-Type-Options"] = "nosniff";
        headers["Referrer-Policy"] = "no-referrer";
        headers["Content-Security-Policy"] = ContentSecurityPolicy(options);
        headers["Permissions-Policy"] = PermissionsPolicy;
        headers["Cross-Origin-Opener-Policy"] = "same-origin";
        headers["Cross-Origin-Resource-Policy"] = "same-origin";
        // X-Frame-Options cannot list origins; omit it when the owner allowed embedding (for example a Home Assistant panel).
        if (options.FrameAncestors == "'none'") headers["X-Frame-Options"] = "DENY";
    }

    // Optimal is Brotli quality 4 / gzip level 6. Measured on a copy of a live state (780 KB of JSON): 109 KB br and
    // 125 KB gzip in about 6 ms, against 131 KB / 203 KB at Fastest. Unchanged polls are 304s and skip this entirely.
    public static IServiceCollection AddJouleCompression(this IServiceCollection services) => services
        .Configure<BrotliCompressionProviderOptions>(o => o.Level = System.IO.Compression.CompressionLevel.Optimal)
        .Configure<GzipCompressionProviderOptions>(o => o.Level = System.IO.Compression.CompressionLevel.Optimal)
        .AddResponseCompression(o =>
        {
            // API responses carry no secrets that attacker-chosen input is reflected beside, so BREACH-style
            // compression oracles do not apply; compressing over HTTPS saves ~90% of every poll.
            o.EnableForHttps = true;
            o.Providers.Add<BrotliCompressionProvider>(); o.Providers.Add<GzipCompressionProvider>();
            o.MimeTypes = ResponseCompressionDefaults.MimeTypes.Concat(["application/json", "image/svg+xml", "application/manifest+json"]).Distinct().ToArray();
        });

    /// <summary>Hashed Vite assets never change, so cache them for a year; index.html must always be revalidated after a deploy.</summary>
    public static void StaticCacheHeaders(StaticFileResponseContext c)
    {
        var path = c.Context.Request.Path;
        c.Context.Response.Headers.CacheControl =
            c.File.Name.Equals("index.html", StringComparison.OrdinalIgnoreCase) ? "no-cache"
            : path.StartsWithSegments("/assets") ? "public, max-age=31536000, immutable"
            : "public, max-age=3600";
    }
    public static StaticFileOptions StaticFiles() => new() { OnPrepareResponse = StaticCacheHeaders };
    public static StaticFileOptions SpaFallback() => new() { OnPrepareResponse = c => c.Context.Response.Headers.CacheControl = "no-cache" };

    static string Client(HttpContext context) => context.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    /// <summary>
    /// Authentication and request-integrity checks for /api. Returns false after writing a rejection.
    /// GET/HEAD /api/health is public so uptime monitors and container health checks work.
    /// </summary>
    public static async Task<bool> AuthorizeApiAsync(HttpContext context, AppAuthOptions options, SessionCookies sessions, AccessKeyThrottle throttle)
    {
        var request = context.Request;
        if (request.Path.Equals("/api/health", StringComparison.OrdinalIgnoreCase) && (HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method))) return true;
        var cookieSession = false;
        if (options.KeyRequired)
        {
            var client = Client(context);
            var presented = request.Headers["X-Access-Key"].ToString();
            var correct = presented.Length > 0 && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(presented), Encoding.UTF8.GetBytes(options.AccessKey!));
            var fingerprint = presented.Length > 0 && !correct ? throttle.Fingerprint(presented) : null;
            // A key this address already got wrong is not a new guess (a tab polling with a typo or a rotated key):
            // answer 401 again without counting it or making it wait, so it cannot lock out the correct key.
            var repeat = fingerprint is not null && throttle.AlreadyCounted(client, fingerprint);
            var wait = repeat ? 0 : throttle.RetryAfterSeconds(client);
            if (wait > 0 && presented.Length > 0)
            {
                context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                context.Response.Headers.RetryAfter = wait.ToString();
                await context.Response.WriteAsJsonAsync(new { error = $"Too many wrong access keys. Wait {wait} seconds, then try again.", retryAfterSeconds = wait, authMode = "AccessKey" });
                return false;
            }
            if (presented.Length > 0)
            {
                if (!correct)
                {
                    if (!repeat) throttle.Failure(client, fingerprint);
                    context.Response.StatusCode = 401;
                    await context.Response.WriteAsJsonAsync(new { error = "That access key didn't work. Check it and try again.", authMode = "AccessKey", reason = "wrong_key" });
                    return false;
                }
                throttle.Success(client);
                var existing = sessions.Validate(request.Cookies[SessionCookies.Name]);
                // Refresh the remembered sign-in when it is missing or past half its lifetime.
                if (sessions.Enabled && (existing is null || existing - DateTimeOffset.UtcNow < options.SessionLifetime / 2)) sessions.Append(context);
            }
            else if (sessions.Validate(request.Cookies[SessionCookies.Name]) is not null) cookieSession = true;
            else
            {
                if (request.Cookies.ContainsKey(SessionCookies.Name)) sessions.Clear(context);
                context.Response.StatusCode = 401;
                await context.Response.WriteAsJsonAsync(new { error = "Enter your application access key.", authMode = "AccessKey", reason = "key_required" });
                return false;
            }
        }
        var origin = request.Headers.Origin.ToString();
        var fetchSite = request.Headers["Sec-Fetch-Site"].ToString();
        // Browser-generated Fetch Metadata describes the public request even when a proxy
        // rewrites Host. Page scripts cannot forge Sec-* headers. Without that signal,
        // fall back to Origin/Host; never trust arbitrary forwarded or identity headers.
        var sameOriginBrowser = string.Equals(fetchSite, "same-origin", StringComparison.OrdinalIgnoreCase);
        if (string.Equals(fetchSite, "cross-site", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(fetchSite, "same-site", StringComparison.OrdinalIgnoreCase) ||
            (!sameOriginBrowser && origin.Length > 0 && (!Uri.TryCreate(origin, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") ||
            !string.Equals(uri.Authority, request.Host.Value, StringComparison.OrdinalIgnoreCase))))
        { context.Response.StatusCode = 403; await context.Response.WriteAsJsonAsync(new { error = "Cross-origin requests are not permitted." }); return false; }
        // Ambient credentials (no app auth behind a proxy, or the session cookie) need a header a form or
        // cross-site page cannot add without a CORS preflight, which this API never grants.
        if ((options.NoAuth || cookieSession) && !HttpMethods.IsGet(request.Method) && !HttpMethods.IsHead(request.Method) && !HttpMethods.IsOptions(request.Method) && !HasRequestHeader(request))
        { context.Response.StatusCode = 403; await context.Response.WriteAsJsonAsync(new { error = "A same-origin dashboard request is required." }); return false; }
        return true;
    }

    /// <summary>
    /// `dotnet Joule.Api.dll --health`: probes the local /api/health and exits 0 when healthy. The aspnet runtime
    /// image has no curl or wget, so the Docker HEALTHCHECK uses this.
    /// </summary>
    public static async Task<int> ProbeHealthAsync(string? urls)
    {
        var first = (urls ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "http://127.0.0.1:5080";
        var target = first.Replace("://0.0.0.0", "://127.0.0.1").Replace("://[::]", "://127.0.0.1").Replace("://*", "://127.0.0.1").Replace("://+", "://127.0.0.1");
        if (!Uri.TryCreate(target, UriKind.Absolute, out var root)) { Console.Error.WriteLine($"Health check: cannot read the listening address \"{first}\"."); return 1; }
        try
        {
            using var http = new HttpClient(new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true }) { Timeout = TimeSpan.FromSeconds(5) };
            using var response = await http.GetAsync(new Uri(root, "/api/health"));
            var body = await response.Content.ReadAsStringAsync();
            if (response.StatusCode == HttpStatusCode.OK && body.Contains("\"status\":\"ok\"", StringComparison.Ordinal)) { Console.WriteLine(body); return 0; }
            Console.Error.WriteLine($"Health check: {(int)response.StatusCode} {body}");
            return 1;
        }
        catch (Exception e) { Console.Error.WriteLine($"Health check: {e.GetType().Name}: {e.Message}"); return 1; }
    }

    /// <summary>Masks an email for display: "jo.…@example.com". Keeps the domain so the account is recognisable.</summary>
    public static string? MaskEmail(string? email)
    {
        if (string.IsNullOrWhiteSpace(email)) return email;
        var at = email.LastIndexOf('@');
        if (at <= 0) return email.Length <= 2 ? "…" : email[..Math.Min(2, email.Length - 1)] + "…";
        var local = email[..at];
        var keep = local.Length <= 3 ? 1 : 3;
        return local[..keep] + "…" + email[at..];
    }
}
