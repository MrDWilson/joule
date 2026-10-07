using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Joule;
using Xunit;

namespace Joule.Tests;

/// <summary>
/// Signing in to Predbat's MCP server. The stand-in follows Predbat's web_mcp.py: /oauth/token issues HS256 access tokens for
/// the client_credentials grant (signed with sha256("jwt_signing_key_" + mcp_secret), audience = the resource asked for, or
/// http://localhost:{mcp_port}), and /mcp tries the bearer value as a token first, logging a failure when it isn't one, before
/// accepting the raw secret as the legacy method. An older Predbat has no token endpoint at all.
/// </summary>
public class PredbatMcpSignInTests
{
    const string Secret = "fixture-mcp-secret";
    static readonly DateTimeOffset T0 = new(2026, 10, 7, 9, 0, 0, TimeSpan.Zero);

    static IConfiguration Config(string url = "http://predbat.test:8199/mcp") => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["Predbat:McpToken"] = Secret, ["Predbat:McpUrl"] = url,
    }).Build();
    static JsonElement Args(string json = "{}") => JsonDocument.Parse(json).RootElement.Clone();

    sealed class FakePredbat(ManualClock clock) : HttpMessageHandler
    {
        public string McpSecret { get; set; } = Secret;
        /// <summary>False: a Predbat from before OAuth, where /oauth/token falls through to the 404 default route.</summary>
        public bool SupportsOAuth { get; set; } = true;
        /// <summary>False: PyJWT is missing, so the token endpoint answers 500 server_error.</summary>
        public bool CanSignTokens { get; set; } = true;
        /// <summary>The Host header Predbat sees, when a proxy rewrites it.</summary>
        public string? HostSeen { get; set; }
        public int ExpiresIn { get; set; } = 30 * 86400;
        public List<string> Log { get; } = [];
        public List<Dictionary<string, string>> TokenRequests { get; } = [];
        public List<string> McpMethods { get; } = [];
        string JwtKey => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"jwt_signing_key_{McpSecret}")));

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var host = HostSeen ?? request.RequestUri!.Authority;
            if (request.RequestUri!.AbsolutePath == "/oauth/token") return await Token(request, ct);
            if (request.RequestUri.AbsolutePath != "/mcp") { Log.Add($"MCP: Default route for {request.RequestUri.AbsolutePath} - 404 Not Found"); return new(HttpStatusCode.NotFound); }
            var auth = request.Headers.Authorization;
            if (auth?.Scheme != "Bearer" || auth.Parameter is null) return Unauthorized();
            if (Verify(auth.Parameter, $"http://{host}") is { } client) Log.Add($"MCP POST: Authenticated via OAuth token (client: {client})");
            else if (auth.Parameter == McpSecret) Log.Add("MCP POST: Authenticated via legacy bearer token");
            else { Log.Add("MCP POST: Invalid token"); return Unauthorized(); }
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            var method = body.RootElement.GetProperty("method").GetString()!;
            McpMethods.Add(method);
            var id = body.RootElement.TryGetProperty("id", out var value) ? value.Clone() : Args("null");
            object result = method switch
            {
                "initialize" => new { protocolVersion = "2024-11-05", capabilities = new { tools = new { } }, serverInfo = new { name = "predbat-mcp", version = "1.0.0" } },
                "tools/list" => new { tools = new[] { new { name = "get_status", description = "Get the current Predbat system status", inputSchema = Args("{\"type\":\"object\",\"properties\":{}}") } } },
                "tools/call" => new { content = new[] { new { type = "text", text = "{\"success\":true,\"data\":{\"status\":\"Idle\"}}" } } },
                _ => new { },
            };
            return Json(new { jsonrpc = "2.0", id, result });
        }

        async Task<HttpResponseMessage> Token(HttpRequestMessage request, CancellationToken ct)
        {
            if (!SupportsOAuth) { Log.Add("MCP: Default route for /oauth/token - 404 Not Found"); return new(HttpStatusCode.NotFound); }
            Assert.Null(request.Headers.Authorization);
            Assert.Equal("application/x-www-form-urlencoded", request.Content!.Headers.ContentType!.MediaType);
            var form = (await request.Content.ReadAsStringAsync(ct)).Split('&').Select(p => p.Split('=', 2)).ToDictionary(p => Uri.UnescapeDataString(p[0]), p => Uri.UnescapeDataString(p[1].Replace('+', ' ')));
            TokenRequests.Add(form);
            Log.Add($"MCP OAuth: Token request received");
            if (form.GetValueOrDefault("grant_type") != "client_credentials") return Json(new { error = "unsupported_grant_type" }, HttpStatusCode.BadRequest);
            if (!CanSignTokens) { Log.Add("MCP OAuth: Error in token endpoint: PyJWT is not installed."); return Json(new { error = "server_error" }, HttpStatusCode.InternalServerError); }
            if (form.GetValueOrDefault("client_secret") != McpSecret) { Log.Add($"MCP OAuth: Invalid credentials for client_id: {form.GetValueOrDefault("client_id")}"); return Json(new { error = "invalid_client" }, HttpStatusCode.Unauthorized); }
            var resource = form.GetValueOrDefault("resource") ?? "http://localhost:8199";
            var token = Sign(new { sub = form["client_id"], client_id = form["client_id"], aud = resource, scope = form.GetValueOrDefault("scope") ?? "mcp:read mcp:write mcp:control", type = "access", iat = clock.Now.ToUnixTimeSeconds(), exp = clock.Now.AddSeconds(ExpiresIn).ToUnixTimeSeconds() });
            return Json(new { access_token = token, token_type = "Bearer", expires_in = ExpiresIn, scope = form.GetValueOrDefault("scope") });
        }

        static string B64(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        static byte[] FromB64(string text) { var s = text.Replace('-', '+').Replace('_', '/'); return Convert.FromBase64String(s.PadRight(s.Length + (4 - s.Length % 4) % 4, '=')); }
        string Sign(object payload)
        {
            var head = B64(JsonSerializer.SerializeToUtf8Bytes(new { alg = "HS256", typ = "JWT" })) + "." + B64(JsonSerializer.SerializeToUtf8Bytes(payload));
            return head + "." + B64(HMACSHA256.HashData(Encoding.UTF8.GetBytes(JwtKey), Encoding.UTF8.GetBytes(head)));
        }
        /// <summary>verify_access_token: the request's own audience first, then Predbat's default one.</summary>
        string? Verify(string token, string expectedAudience)
        {
            var parts = token.Split('.');
            if (parts.Length != 3) { Log.Add($"MCP: Token {token} failed: Not enough segments"); return null; }
            var signature = B64(HMACSHA256.HashData(Encoding.UTF8.GetBytes(JwtKey), Encoding.UTF8.GetBytes(parts[0] + "." + parts[1])));
            if (signature != parts[2]) { Log.Add("MCP: Token verification failed: Signature verification failed"); return null; }
            using var payload = JsonDocument.Parse(FromB64(parts[1]));
            var p = payload.RootElement;
            if (p.GetProperty("exp").GetInt64() <= clock.Now.ToUnixTimeSeconds()) { Log.Add("MCP: Access token expired"); return null; }
            var audience = p.GetProperty("aud").GetString();
            if (audience != expectedAudience && audience != "http://localhost:8199") { Log.Add("MCP: Invalid token audience: Audience doesn't match"); return null; }
            return p.GetProperty("type").GetString() == "access" ? p.GetProperty("client_id").GetString() : null;
        }
        static HttpResponseMessage Unauthorized() => Json(new { jsonrpc = "2.0", id = (int?)null, error = new { code = -32600, message = "Unauthorized" } }, HttpStatusCode.Unauthorized);
        static HttpResponseMessage Json(object body, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
    }

    static bool Failed(string line) => line.Contains("failed", StringComparison.OrdinalIgnoreCase) || line.Contains("Invalid", StringComparison.Ordinal) || line.Contains("legacy", StringComparison.Ordinal);

    [Fact]
    public async Task CurrentPredbatIssuesAnAccessTokenAndLogsNoFailedSignIns()
    {
        var clock = new ManualClock(T0); var predbat = new FakePredbat(clock);
        using var client = new PredbatMcpClient(new HttpClient(predbat), Config(), clock);
        var status = await client.DiscoverAsync();
        Assert.True(status.Connected, status.Error);
        Assert.Equal("token", status.SignIn);
        for (var i = 0; i < 3; i++) Assert.True((await client.CallReadOnlyAsync("get_status", Args())).Success);

        // One token for the whole session, asked for as Predbat's own docs describe: client credentials, read scope, this server.
        var form = Assert.Single(predbat.TokenRequests);
        Assert.Equal("client_credentials", form["grant_type"]);
        Assert.Equal(Secret, form["client_secret"]);
        Assert.Equal("joule", form["client_id"]);
        Assert.Equal("mcp:read", form["scope"]);
        Assert.Equal("http://predbat.test:8199", form["resource"]);
        // Every MCP request signed in with the token; nothing in Predbat's log reads as a failed or legacy sign-in.
        Assert.Equal(predbat.McpMethods.Count, predbat.Log.Count(l => l == "MCP POST: Authenticated via OAuth token (client: joule)"));
        Assert.DoesNotContain(predbat.Log, Failed);
        Assert.DoesNotContain(Secret, JsonSerializer.Serialize(client.Status));
    }

    [Fact]
    public async Task APredbatWithoutTheTokenEndpointGetsTheSecretAndIsAskedAgainLater()
    {
        var clock = new ManualClock(T0); var predbat = new FakePredbat(clock) { SupportsOAuth = false };
        using var client = new PredbatMcpClient(new HttpClient(predbat), Config(), clock);
        var status = await client.DiscoverAsync();
        Assert.True(status.Connected, status.Error);
        Assert.Equal("secret", status.SignIn);
        Assert.True((await client.CallReadOnlyAsync("get_status", Args())).Success);
        Assert.Contains("MCP POST: Authenticated via legacy bearer token", predbat.Log);
        // Asked once, not before every request.
        Assert.Single(predbat.Log, l => l.Contains("/oauth/token"));

        // Predbat is updated; a few hours later Joule asks again and moves to a token.
        predbat.SupportsOAuth = true; clock.Now += PredbatMcpClient.LegacyRecheck + TimeSpan.FromMinutes(1);
        predbat.Log.Clear();
        Assert.True((await client.CallReadOnlyAsync("get_status", Args())).Success);
        Assert.Single(predbat.TokenRequests);
        Assert.Contains("MCP POST: Authenticated via OAuth token (client: joule)", predbat.Log);
        Assert.DoesNotContain(predbat.Log, Failed);
    }

    [Fact]
    public async Task APredbatThatCannotSignTokensStillWorksWithTheSecret()
    {
        var clock = new ManualClock(T0); var predbat = new FakePredbat(clock) { CanSignTokens = false };
        using var client = new PredbatMcpClient(new HttpClient(predbat), Config(), clock);
        var status = await client.DiscoverAsync();
        Assert.True(status.Connected, status.Error);
        Assert.Equal("secret", status.SignIn);
    }

    [Fact]
    public async Task AWrongSecretIsReportedPlainlyAndNeverSentToMcp()
    {
        var clock = new ManualClock(T0); var predbat = new FakePredbat(clock) { McpSecret = "a-different-secret" };
        using var client = new PredbatMcpClient(new HttpClient(predbat), Config(), clock);
        var status = await client.DiscoverAsync();
        Assert.False(status.Connected);
        Assert.Null(status.SignIn);
        Assert.Contains("rejected the MCP secret", status.Error);
        Assert.Contains("Predbat__McpToken", status.Error);
        Assert.Empty(predbat.McpMethods);
        Assert.DoesNotContain(predbat.Log, l => l.StartsWith("MCP POST", StringComparison.Ordinal));

        // An older Predbat with the wrong secret says the same, from the MCP request itself.
        var old = new FakePredbat(clock) { McpSecret = "a-different-secret", SupportsOAuth = false };
        using var legacy = new PredbatMcpClient(new HttpClient(old), Config(), clock);
        Assert.Contains("rejected the MCP secret", (await legacy.DiscoverAsync()).Error);
    }

    [Fact]
    public async Task BehindAProxyThatChangesTheHostTheTokenFallsBackToPredbatsDefaultAudience()
    {
        var clock = new ManualClock(T0); var predbat = new FakePredbat(clock) { HostSeen = "predbat-internal:8199" };
        using var client = new PredbatMcpClient(new HttpClient(predbat), Config(), clock);
        Assert.True((await client.DiscoverAsync()).Connected);
        Assert.True((await client.CallReadOnlyAsync("get_status", Args())).Success);
        Assert.Equal(2, predbat.TokenRequests.Count);
        Assert.False(predbat.TokenRequests[1].ContainsKey("resource"));
        // One refused request while finding out, then nothing more.
        Assert.Single(predbat.Log, l => l == "MCP POST: Invalid token");
        Assert.Equal(predbat.McpMethods.Count, predbat.Log.Count(l => l.StartsWith("MCP POST: Authenticated via OAuth token", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task TheTokenIsRenewedBeforeItRunsOut()
    {
        var clock = new ManualClock(T0); var predbat = new FakePredbat(clock) { ExpiresIn = 3600 };
        using var client = new PredbatMcpClient(new HttpClient(predbat), Config(), clock);
        Assert.True((await client.DiscoverAsync()).Connected);
        clock.Now += TimeSpan.FromMinutes(50);
        Assert.True((await client.CallReadOnlyAsync("get_status", Args())).Success);
        Assert.Single(predbat.TokenRequests);
        clock.Now += TimeSpan.FromMinutes(5); // 55 minutes in: inside the last tenth of the hour
        Assert.True((await client.CallReadOnlyAsync("get_status", Args())).Success);
        Assert.Equal(2, predbat.TokenRequests.Count);
        Assert.DoesNotContain(predbat.Log, Failed);
    }

    [Fact]
    public async Task ARefusedTokenIsReplacedOnceAndThenReported()
    {
        var clock = new ManualClock(T0); var predbat = new FakePredbat(clock);
        using var client = new PredbatMcpClient(new HttpClient(predbat), Config(), clock);
        Assert.True((await client.DiscoverAsync()).Connected);
        // mcp_secret changed in Predbat: tokens signed with the old one stop working, and the old secret can't get a new one.
        predbat.McpSecret = "rotated-secret";
        var result = await client.CallReadOnlyAsync("get_status", Args());
        Assert.False(result.Success);
        Assert.Contains("rejected the MCP secret", result.Error);
    }

    [Theory]
    [InlineData("http://predbat:8199/mcp", "http://predbat:8199/oauth/token")]
    [InlineData("http://predbat:8199/", "http://predbat:8199/oauth/token")]
    [InlineData("http://predbat:8199", "http://predbat:8199/oauth/token")]
    [InlineData("https://home.example/predbat-mcp/mcp", "https://home.example/predbat-mcp/oauth/token")]
    public void TheTokenEndpointSitsBesideTheMcpEndpoint(string mcp, string expected) =>
        Assert.Equal(expected, PredbatMcpClient.TokenEndpoint(new Uri(mcp)).AbsoluteUri);
}
