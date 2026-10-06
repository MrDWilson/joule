using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Joule;
using Xunit;

public class ApplicationAuthTests
{
    // Access keys must be at least 16 characters (AppAuthOptions.MinimumKeyLength).
    const string TestKey = "test-key-0123456789";
    [Fact]
    public async Task NoAuthStartsLiveWithoutKeyAndAllowsDashboardRequests()
    {
        await using var app = new RunningApp("None");
        Assert.True(await app.Start(), app.Log);
        // Health is public and reports only status and version; the demo flag moved to /api/state.
        var health = await app.Http.GetStringAsync("api/health");
        Assert.Contains("\"status\":\"ok\"", health);
        Assert.DoesNotContain("demo", health);
        Assert.Contains("\"demo\":false", await app.Http.GetStringAsync("api/state"));
        var mcp = await app.Http.GetStringAsync("api/mcp/status");
        Assert.Contains("\"configured\":false", mcp);
        Assert.Contains("\"connected\":false", mcp);
        app.Http.DefaultRequestHeaders.Add("X-Joule-Request", "1");
        Assert.Equal(HttpStatusCode.OK, (await app.Http.PostAsJsonAsync("api/mcp/discover", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await app.Http.PostAsJsonAsync("api/mode", new { mode = "Recommend" })).StatusCode);
        Assert.Contains("\"mode\":\"Recommend\"", await app.Http.GetStringAsync("api/state"));
    }

    [Fact]
    public async Task NoAuthIgnoresLeftoverApplicationKey()
    {
        await using var app = new RunningApp("None", "old-config-key");
        Assert.True(await app.Start(), app.Log);
        Assert.Equal(HttpStatusCode.OK, (await app.Http.GetAsync("api/state")).StatusCode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("AccessKey")]
    public async Task StandaloneStillRequiresCorrectKey(string? mode)
    {
        await using var app = new RunningApp(mode, TestKey);
        Assert.True(await app.Start(), app.Log);
        var challenge = await app.Http.GetAsync("api/state");
        Assert.Equal(HttpStatusCode.Unauthorized, challenge.StatusCode);
        Assert.Contains("\"authMode\":\"AccessKey\"", await challenge.Content.ReadAsStringAsync());
        app.Http.DefaultRequestHeaders.Add("X-Access-Key", "wrong-key");
        Assert.Equal(HttpStatusCode.Unauthorized, (await app.Http.PostAsJsonAsync("api/mode", new { mode = "Recommend" })).StatusCode);
        app.Http.DefaultRequestHeaders.Remove("X-Access-Key");
        app.Http.DefaultRequestHeaders.Add("X-Access-Key", TestKey);
        Assert.Equal(HttpStatusCode.OK, (await app.Http.GetAsync("api/state")).StatusCode);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("AccessKey", false)]
    [InlineData("Externla", false)]
    [InlineData("Externla", true)]
    public async Task MissingKeyOrInvalidAuthModeFailsClosed(string? mode, bool demo)
    {
        await using var app = new RunningApp(mode, demo: demo);
        Assert.False(await app.Start(), app.Log);
        // A configuration mistake exits with code 2 and one readable line, not an unhandled-exception stack trace.
        Assert.Equal(2, app.ExitCode);
        Assert.True(app.Log.Contains(mode == "Externla" ? "App__AuthMode" : "App__AccessKey"), app.Log);
        Assert.DoesNotContain("Unhandled exception", app.Log);
        Assert.DoesNotContain("   at ", app.Log);
    }

    // The dashboard sends X-Joule-Request; X-PredbatAI-Request is the pre-rename name and is still accepted.
    // Neither one lets another site through.
    [Theory]
    [InlineData("X-Joule-Request")]
    [InlineData("X-PredbatAI-Request")]
    public async Task NoAuthRejectsCrossSiteAndSimpleFormMutations(string requestHeader)
    {
        await using var app = new RunningApp("None");
        Assert.True(await app.Start(), app.Log);
        Assert.Equal(HttpStatusCode.Forbidden, (await app.Http.PostAsJsonAsync("api/mode", new { mode = "Recommend" })).StatusCode);
        app.Http.DefaultRequestHeaders.Add(requestHeader, "1");
        foreach (var origin in new[] { "https://elsewhere.test", "null", "not-a-url", "http://127.0.0.1:1" })
        {
            app.Http.DefaultRequestHeaders.Remove("Origin");
            app.Http.DefaultRequestHeaders.TryAddWithoutValidation("Origin", origin);
            Assert.Equal(HttpStatusCode.Forbidden, (await app.Http.PostAsJsonAsync("api/mode", new { mode = "Recommend" })).StatusCode);
        }
        app.Http.DefaultRequestHeaders.Remove("Origin");
        app.Http.DefaultRequestHeaders.Add("Sec-Fetch-Site", "cross-site");
        Assert.Equal(HttpStatusCode.Forbidden, (await app.Http.PostAsJsonAsync("api/mode", new { mode = "Recommend" })).StatusCode);
        app.Http.DefaultRequestHeaders.Remove("Sec-Fetch-Site");
        // A reverse proxy may terminate TLS and must preserve the public Host header.
        app.Http.DefaultRequestHeaders.Host = "joule.example.test";
        app.Http.DefaultRequestHeaders.Add("Origin", "https://joule.example.test");
        Assert.Equal(HttpStatusCode.OK, (await app.Http.PostAsJsonAsync("api/mode", new { mode = "Recommend" })).StatusCode);
    }

    [Theory]
    [InlineData("None")]
    [InlineData("AccessKey")]
    public async Task SameOriginDashboardWorksWhenProxyRewritesHost(string mode)
    {
        await using var app = new RunningApp(mode, TestKey);
        Assert.True(await app.Start(), app.Log);
        app.Http.DefaultRequestHeaders.Host = "joule:5080";
        app.Http.DefaultRequestHeaders.Add("Origin", "https://energy.example.test");
        app.Http.DefaultRequestHeaders.Add("Sec-Fetch-Site", "same-origin");
        app.Http.DefaultRequestHeaders.Add("X-Joule-Request", "1");
        app.Http.DefaultRequestHeaders.Add("X-Access-Key", TestKey);
        var connect = await app.Http.PostAsJsonAsync("api/ai/chatgpt/start", new { });
        // This is a valid dashboard request, but the public browser cannot reach
        // a callback listener on the server's loopback address.
        Assert.Equal(HttpStatusCode.Conflict, connect.StatusCode);
        Assert.Contains("local_sign_in_required", await connect.Content.ReadAsStringAsync());
        Assert.Contains("\"chatGptLocalSignInAvailable\":false", await app.Http.GetStringAsync("api/state"));
        Assert.Equal(HttpStatusCode.OK, (await app.Http.GetAsync("api/mcp/status")).StatusCode);
        var discovery = await app.Http.PostAsJsonAsync("api/mcp/discover", new { });
        Assert.Equal(HttpStatusCode.OK, discovery.StatusCode);
        Assert.Contains("\"configured\":false", await discovery.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, (await app.Http.PostAsJsonAsync("api/mode", new { mode = "Recommend" })).StatusCode);
        Assert.Contains("\"mode\":\"Recommend\"", await app.Http.GetStringAsync("api/state"));
        if (mode == "None")
        {
            app.Http.DefaultRequestHeaders.Remove("X-Joule-Request");
            Assert.Equal(HttpStatusCode.Forbidden, (await app.Http.PostAsJsonAsync("api/ai/chatgpt/start", new { })).StatusCode);
        }
        else
        {
            app.Http.DefaultRequestHeaders.Remove("X-Access-Key");
            Assert.Equal(HttpStatusCode.Unauthorized, (await app.Http.PostAsJsonAsync("api/ai/chatgpt/start", new { })).StatusCode);
        }
    }

    [Fact]
    public async Task MeasuredHistoryIsKeyProtectedReadOnlyAndRejectsCrossSiteReads()
    {
        await using var app = new RunningApp("AccessKey", TestKey, demo: true);
        Assert.True(await app.Start(), app.Log);
        var to = DateTimeOffset.UtcNow.AddDays(-1); var from = to.AddHours(-2);
        var query = $"api/telemetry/history?from={Uri.EscapeDataString(from.ToString("O"))}&to={Uri.EscapeDataString(to.ToString("O"))}&slotMinutes=30";
        Assert.Equal(HttpStatusCode.Unauthorized, (await app.Http.GetAsync(query)).StatusCode);
        app.Http.DefaultRequestHeaders.Add("X-Access-Key", TestKey);
        var body = await app.Http.GetStringAsync(query);
        Assert.Contains("\"slotMinutes\":30", body); Assert.Contains("\"slots\":[", body);
        Assert.Equal(HttpStatusCode.BadRequest, (await app.Http.GetAsync(query.Replace("slotMinutes=30", "slotMinutes=7"))).StatusCode);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await app.Http.PostAsJsonAsync(query, new { })).StatusCode);
        app.Http.DefaultRequestHeaders.Add("Sec-Fetch-Site", "cross-site");
        Assert.Equal(HttpStatusCode.Forbidden, (await app.Http.GetAsync(query)).StatusCode);
    }

    [Theory]
    [InlineData("cross-site", "X-Joule-Request")]
    [InlineData("same-site", "X-Joule-Request")]
    [InlineData("cross-site", "X-PredbatAI-Request")]
    [InlineData("same-site", "X-PredbatAI-Request")]
    public async Task OtherSitesCannotStartChatGptEvenWithMatchingHost(string fetchSite, string requestHeader)
    {
        await using var app = new RunningApp("None");
        Assert.True(await app.Start(), app.Log);
        app.Http.DefaultRequestHeaders.Host = "energy.example.test";
        app.Http.DefaultRequestHeaders.Add("Origin", "https://energy.example.test");
        app.Http.DefaultRequestHeaders.Add("Sec-Fetch-Site", fetchSite);
        app.Http.DefaultRequestHeaders.Add(requestHeader, "1");
        Assert.Equal(HttpStatusCode.Forbidden, (await app.Http.PostAsJsonAsync("api/ai/chatgpt/start", new { })).StatusCode);
    }

    private sealed class RunningApp : IAsyncDisposable
    {
        readonly string directory = Path.Combine(OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath(), "joule-auth-test-" + Guid.NewGuid());
        readonly Process process;
        readonly TaskCompletionSource<string?> ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        readonly List<string> log = [];
        public string Log { get { lock (log) return string.Join('\n', log); } }
        public int ExitCode => process.ExitCode;
        // Header-key semantics only: a remembered-sign-in cookie would authenticate requests these tests send without a key
        // (SessionAuthTests covers the cookie).
        public HttpClient Http { get; } = new(new HttpClientHandler { UseCookies = false }) { Timeout = TimeSpan.FromSeconds(10) };

        public RunningApp(string? mode, string? key = null, bool demo = false)
        {
            Directory.CreateDirectory(directory);
            var dotnet = Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "../../../dotnet" + (OperatingSystem.IsWindows() ? ".exe" : "")));
            var start = new ProcessStartInfo(dotnet) { WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            start.ArgumentList.Add(typeof(StateService).Assembly.Location);
            foreach (var name in start.Environment.Keys.Where(x => new[] { "App__", "Predbat__", "HomeAssistant__", "ConfigFiles__", "Ai__", "ASPNETCORE_", "DOTNET_" }.Any(prefix => x.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))).ToArray())
                start.Environment.Remove(name);
            start.Environment["ASPNETCORE_URLS"] = "http://127.0.0.1:0";
            start.Environment["App__DataDirectory"] = directory;
            start.Environment["App__Demo"] = demo.ToString();
            if (mode != null) start.Environment["App__AuthMode"] = mode;
            if (key != null) start.Environment["App__AccessKey"] = key;
            process = new Process { StartInfo = start, EnableRaisingEvents = true };
            process.OutputDataReceived += (_, e) => Record(e.Data);
            process.ErrorDataReceived += (_, e) => Record(e.Data);
            process.Exited += (_, _) => ready.TrySetResult(null);
        }

        void Record(string? line)
        {
            if (line == null) return;
            lock (log) log.Add(line);
            var match = Regex.Match(line, @"Now listening on: (http://127\.0\.0\.1:\d+)");
            if (match.Success) ready.TrySetResult(match.Groups[1].Value);
        }

        public async Task<bool> Start()
        {
            process.Start(); process.BeginOutputReadLine(); process.BeginErrorReadLine();
            var url = await ready.Task.WaitAsync(TimeSpan.FromSeconds(30));
            if (url == null) { await process.WaitForExitAsync(); return false; }
            Http.BaseAddress = new Uri(url);
            return true;
        }

        public async ValueTask DisposeAsync()
        {
            Http.Dispose();
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(); process.Dispose();
            Directory.Delete(directory, recursive: true);
        }
    }
}
