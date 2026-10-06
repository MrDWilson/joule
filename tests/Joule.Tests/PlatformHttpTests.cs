using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Joule;
using Xunit;

/// <summary>Response compression, caching, security headers, fallbacks, health and startup failures, against the real process.</summary>
[Collection(ChildProcessCollection.Name)]
public class PlatformHttpTests
{
    const string Key = "platform-test-key-0123456789";
    static Dictionary<string, string?> NoAuth => new() { ["App__AuthMode"] = "None" };

    [Fact]
    public async Task StateIsCompressedWithBrotliAndGzip()
    {
        await using var app = new JouleProcess(NoAuth);
        Assert.True(await app.Start(), app.Log);
        foreach (var (encoding, decode) in new (string, Func<Stream, Stream>)[] { ("br", s => new BrotliStream(s, CompressionMode.Decompress)), ("gzip", s => new GZipStream(s, CompressionMode.Decompress)) })
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "api/state");
            request.Headers.TryAddWithoutValidation("Accept-Encoding", encoding);
            using var response = await app.Http.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(encoding, Assert.Single(response.Content.Headers.ContentEncoding));
            var wire = await response.Content.ReadAsByteArrayAsync();
            using var body = new StreamReader(decode(new MemoryStream(wire)));
            var json = JsonDocument.Parse(await body.ReadToEndAsync());
            Assert.True(json.RootElement.TryGetProperty("state", out _));
            Assert.True(wire.Length < 120_000);
        }
    }

    [Fact]
    public async Task HashedAssetsAreImmutableAndTheShellIsRevalidated()
    {
        await using var app = new JouleProcess(NoAuth, withWebRoot: true);
        Assert.True(await app.Start(), app.Log);
        using var asset = await app.Http.GetAsync("assets/index-abc123.js");
        Assert.Equal(HttpStatusCode.OK, asset.StatusCode);
        Assert.Equal("public, max-age=31536000, immutable", asset.Headers.CacheControl?.ToString());
        foreach (var path in new[] { "", "index.html", "today", "insights/inv/123" })
        {
            using var shell = await app.Http.GetAsync(path);
            Assert.Equal(HttpStatusCode.OK, shell.StatusCode);
            Assert.Equal("no-cache", shell.Headers.CacheControl?.ToString());
            Assert.Contains("<div id=\"root\">", await shell.Content.ReadAsStringAsync());
        }
        using var icon = await app.Http.GetAsync("favicon.svg");
        Assert.Equal("public, max-age=3600", icon.Headers.CacheControl?.ToString());
        // Large static files are compressed too.
        using var request = new HttpRequestMessage(HttpMethod.Get, "assets/index-abc123.js");
        request.Headers.TryAddWithoutValidation("Accept-Encoding", "br");
        using var compressed = await app.Http.SendAsync(request);
        Assert.Equal("br", Assert.Single(compressed.Content.Headers.ContentEncoding));
    }

    [Fact]
    public async Task EveryResponseCarriesSecurityHeaders()
    {
        await using var app = new JouleProcess(NoAuth, withWebRoot: true);
        Assert.True(await app.Start(), app.Log);
        foreach (var path in new[] { "", "api/state", "api/health", "api/nonexistent" })
        {
            using var response = await app.Http.GetAsync(path);
            var csp = string.Join(";", response.Headers.GetValues("Content-Security-Policy"));
            Assert.Contains("default-src 'self'", csp);
            Assert.Contains("frame-ancestors 'none'", csp);
            Assert.Contains("img-src 'self' data:", csp);
            // Recharts and Radix rely on inline styles; scripts stay same-origin only.
            Assert.Contains("style-src 'self' 'unsafe-inline'", csp);
            Assert.Contains("script-src 'self';", csp);
            Assert.DoesNotContain("unsafe-eval", csp);
            Assert.Equal("DENY", Assert.Single(response.Headers.GetValues("X-Frame-Options")));
            Assert.Contains("camera=()", Assert.Single(response.Headers.GetValues("Permissions-Policy")));
            Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
            Assert.Equal("no-referrer", Assert.Single(response.Headers.GetValues("Referrer-Policy")));
        }
    }

    [Fact]
    public async Task FramingCanBeAllowedForAHomeAssistantPanel()
    {
        var env = NoAuth; env["App__FrameAncestors"] = "https://homeassistant.example.test";
        await using var app = new JouleProcess(env);
        Assert.True(await app.Start(), app.Log);
        using var response = await app.Http.GetAsync("api/health");
        Assert.Contains("frame-ancestors https://homeassistant.example.test", string.Join(";", response.Headers.GetValues("Content-Security-Policy")));
        Assert.False(response.Headers.Contains("X-Frame-Options"));
    }

    [Fact]
    public async Task UnknownApiPathsAnswerJson404AndWrongMethodsStay405()
    {
        await using var app = new JouleProcess(NoAuth, withWebRoot: true);
        Assert.True(await app.Start(), app.Log);
        using var missing = await app.Http.GetAsync("api/nonexistent");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal("application/json", missing.Content.Headers.ContentType?.MediaType);
        Assert.Contains("\"error\"", await missing.Content.ReadAsStringAsync());
        using var nested = await app.Http.GetAsync("api/some/deeper/path");
        Assert.Equal(HttpStatusCode.NotFound, nested.StatusCode);
        app.Http.DefaultRequestHeaders.Add("X-Joule-Request", "1");
        using var wrongMethod = await app.Http.PostAsJsonAsync("api/plans/timeline", new { });
        Assert.Equal(HttpStatusCode.MethodNotAllowed, wrongMethod.StatusCode);
        Assert.Contains("\"error\"", await wrongMethod.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task HealthIsPublicAndReportsTheVersion()
    {
        await using var app = new JouleProcess(new() { ["App__AuthMode"] = "AccessKey", ["App__AccessKey"] = Key, ["App__Demo"] = "false" });
        Assert.True(await app.Start(), app.Log);
        Assert.Equal(HttpStatusCode.Unauthorized, (await app.Http.GetAsync("api/state")).StatusCode);
        using var health = await app.Http.GetAsync("api/health");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        var json = JsonDocument.Parse(await health.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("ok", json.GetProperty("status").GetString());
        Assert.Equal(AppVersion.Current, json.GetProperty("version").GetString());
        Assert.Matches(@"^\d+\.\d+\.\d+", json.GetProperty("version").GetString());
        Assert.False(json.TryGetProperty("demo", out _));
        // Uptime monitors often probe with HEAD; it must not fall through to the JSON 404.
        using var head = await app.Http.SendAsync(new HttpRequestMessage(HttpMethod.Head, "api/health"));
        Assert.Equal(HttpStatusCode.OK, head.StatusCode);
        // Only reads are public: anything else on /api/health still needs the key.
        Assert.Equal(HttpStatusCode.Unauthorized, (await app.Http.PostAsJsonAsync("api/health", new { })).StatusCode);
    }

    [Fact]
    public async Task HealthSwitchProbesTheRunningServer()
    {
        await using var app = new JouleProcess(NoAuth);
        Assert.True(await app.Start(), app.Log);
        await using var probe = new JouleProcess(new() { ["ASPNETCORE_URLS"] = app.BaseAddress!.ToString().TrimEnd('/').Replace("127.0.0.1", "0.0.0.0") }, false, "--health");
        Assert.Equal(0, await probe.RunToExit(TimeSpan.FromSeconds(30)));
        Assert.Contains("\"status\":\"ok\"", probe.Log);
        await using var nothing = new JouleProcess(new() { ["ASPNETCORE_URLS"] = "http://127.0.0.1:9" }, false, "--health");
        Assert.Equal(1, await nothing.RunToExit(TimeSpan.FromSeconds(30)));
    }

    [Theory]
    [InlineData("AccessKey", "short-key", "too short")]
    [InlineData("AccessKey", "", "App__AccessKey")]
    [InlineData("Sometimes", null, "App__AuthMode")]
    public async Task ConfigurationMistakesExitWithCodeTwoAndOneLine(string mode, string? key, string message)
    {
        await using var app = new JouleProcess(new() { ["App__Demo"] = "false", ["App__AuthMode"] = mode, ["App__AccessKey"] = key });
        Assert.False(await app.Start(), app.Log);
        Assert.Equal(2, app.ExitCode);
        Assert.Contains(message, app.Log);
        var lines = app.Log.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Single(lines);
        Assert.StartsWith("Joule cannot start:", lines[0]);
    }

    [Fact]
    public async Task ShortKeysAreRejectedInDemoToo()
    {
        await using var app = new JouleProcess(new() { ["App__AuthMode"] = "AccessKey", ["App__AccessKey"] = "abc" });
        Assert.False(await app.Start(), app.Log);
        Assert.Equal(2, app.ExitCode);
    }

    [Fact]
    public async Task UnchangedStateAnswers304UntilSomethingChanges()
    {
        await using var app = new JouleProcess(NoAuth);
        Assert.True(await app.Start(), app.Log);
        foreach (var path in new[] { "api/state", "api/state?view=header", "api/settings", "api/investigations", "api/activities", "api/usage" })
        {
            using var first = await app.Http.GetAsync(path);
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
            var tag = first.Headers.ETag?.ToString();
            Assert.False(string.IsNullOrEmpty(tag), path);
            Assert.True(first.Headers.CacheControl is { Private: true, NoCache: true, NoStore: false }, path);
            using var again = new HttpRequestMessage(HttpMethod.Get, path);
            again.Headers.TryAddWithoutValidation("If-None-Match", tag);
            using var notModified = await app.Http.SendAsync(again);
            Assert.True(notModified.StatusCode == HttpStatusCode.NotModified, path);
            Assert.Empty(await notModified.Content.ReadAsByteArrayAsync());
        }
        using var before = await app.Http.GetAsync("api/state?view=header");
        var oldTag = before.Headers.ETag!.ToString();
        app.Http.DefaultRequestHeaders.Add("X-Joule-Request", "1");
        Assert.Equal(HttpStatusCode.OK, (await app.Http.PostAsJsonAsync("api/mode", new { mode = "Monitor" })).StatusCode);
        using var stale = new HttpRequestMessage(HttpMethod.Get, "api/state?view=header");
        stale.Headers.TryAddWithoutValidation("If-None-Match", oldTag);
        using var changed = await app.Http.SendAsync(stale);
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        Assert.NotEqual(oldTag, changed.Headers.ETag?.ToString());
        Assert.Equal("Monitor", JsonDocument.Parse(await changed.Content.ReadAsStringAsync()).RootElement.GetProperty("mode").GetString());
    }

    [Fact]
    public async Task HeaderIsSmallAndLegacyPayloadDropsThePlansList()
    {
        await using var app = new JouleProcess(NoAuth);
        Assert.True(await app.Start(), app.Log);
        var full = JsonDocument.Parse(await app.Http.GetStringAsync("api/state")).RootElement;
        Assert.False(full.TryGetProperty("plans", out _));
        Assert.True(full.GetProperty("state").TryGetProperty("investigations", out _));
        Assert.Equal(full.ToString(), JsonDocument.Parse(await app.Http.GetStringAsync("api/state?full=1")).RootElement.ToString());
        var header = await app.Http.GetByteArrayAsync("api/state?view=header");
        using var gz = new MemoryStream();
        using (var z = new GZipStream(gz, CompressionLevel.Fastest, true)) z.Write(header);
        Assert.True(gz.Length < 30_000, $"header gzip {gz.Length} bytes");
        var root = JsonDocument.Parse(header).RootElement;
        Assert.False(root.TryGetProperty("state", out _));
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(HttpStatusCode.BadRequest, (await app.Http.GetAsync("api/state?view=everything")).StatusCode);
    }

    [Fact]
    public async Task NoAuthAcceptsTheNewJouleRequestHeader()
    {
        await using var app = new JouleProcess(NoAuth);
        Assert.True(await app.Start(), app.Log);
        Assert.Equal(HttpStatusCode.Forbidden, (await app.Http.PostAsJsonAsync("api/mode", new { mode = "Recommend" })).StatusCode);
        using var joule = new HttpRequestMessage(HttpMethod.Post, "api/mode") { Content = JsonContent.Create(new { mode = "Recommend" }) };
        joule.Headers.Add("X-Joule-Request", "1");
        Assert.Equal(HttpStatusCode.OK, (await app.Http.SendAsync(joule)).StatusCode);
        using var legacy = new HttpRequestMessage(HttpMethod.Post, "api/mode") { Content = JsonContent.Create(new { mode = "Monitor" }) };
        legacy.Headers.Add("X-PredbatAI-Request", "1");
        Assert.Equal(HttpStatusCode.OK, (await app.Http.SendAsync(legacy)).StatusCode);
    }

    [Fact]
    public async Task NoAuthOnEveryInterfaceLogsALoudWarning()
    {
        // Listening on 0.0.0.0 with port 0; the warning is decided from the configured URL, before any request.
        await using var app = new JouleProcess(new() { ["App__AuthMode"] = "None", ["ASPNETCORE_URLS"] = "http://0.0.0.0:0" });
        Assert.True(await app.Start(), app.Log);
        Assert.Contains("NO APP AUTHENTICATION", app.Log);
    }

    [Fact]
    public void ChatGptEmailIsMasked()
    {
        Assert.Equal("jo.…@example.com", WebSecurity.MaskEmail("jo.bloggs@example.com"));
        Assert.Equal("a…@example.com", WebSecurity.MaskEmail("al@example.com"));
        Assert.Null(WebSecurity.MaskEmail(null));
        Assert.DoesNotContain("bloggs", WebSecurity.MaskEmail("jo.bloggs@example.com"));
    }

    [Fact]
    public void AllInterfaceBindingsAreRecognised()
    {
        Assert.True(AppAuthOptions.ListensOnAllInterfaces("http://0.0.0.0:5080"));
        Assert.True(AppAuthOptions.ListensOnAllInterfaces("http://127.0.0.1:5080;http://*:80"));
        Assert.True(AppAuthOptions.ListensOnAllInterfaces("http://+:5080"));
        Assert.True(AppAuthOptions.ListensOnAllInterfaces("http://[::]:5080"));
        Assert.False(AppAuthOptions.ListensOnAllInterfaces("http://127.0.0.1:5080"));
        Assert.False(AppAuthOptions.ListensOnAllInterfaces(null));
    }
}
