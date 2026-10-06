using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using Joule;
using Xunit;

public class AiAuthTests
{
    [Fact]
    public async Task AuthorizationUsesPkceExactLoopbackAndStableHost()
    {
        using var fixture = new AuthFixture();
        var first = QueryHelpers.ParseQuery(new Uri(await fixture.Auth.StartAsync(default)).Query);
        var second = QueryHelpers.ParseQuery(new Uri(await new ChatGptAuth(fixture.Http,fixture.Directory).StartAsync(default)).Query);
        Assert.Equal(ChatGptAuth.CallbackUri, first["redirect_uri"].ToString());
        Assert.Equal("dynamic_agent_client", first["client_id"].ToString());
        Assert.Equal("Joule", first["agent_name_hint"].ToString());
        Assert.Equal("S256",first["code_challenge_method"].ToString());
        Assert.Equal(first["ext_agent_host_id"], second["ext_agent_host_id"]);
        Assert.NotEqual(first["state"].ToString(), second["state"].ToString());
    }
    [Theory]
    [InlineData("wrong_nonce")]
    [InlineData("wrong_audience")]
    [InlineData("wrong_signature")]
    [InlineData("expired")]
    [InlineData("wrong_issuer")]
    [InlineData("missing_plan_scope")]
    public async Task InvalidTokenCannotActivateCredentials(string fault)
    {
        using var fixture = new AuthFixture { Fault = fault };
        var q = QueryHelpers.ParseQuery(new Uri(await fixture.Auth.StartAsync(default)).Query);
        fixture.Nonce = q["nonce"].ToString();
        await Assert.ThrowsAsync<DomainException>(() => fixture.Auth.CompleteAsync("test-code",q["state"].ToString(),"oaiapp_test",null,default));
        Assert.False(fixture.Auth.Connected);
        Assert.False(File.Exists(Path.Combine(fixture.Directory,"chatgpt-credentials.json")));
    }
    [Fact]
    public async Task ValidTokenPersistsWithOwnerOnlyPermissionsAndReturningMismatchKeepsAccount()
    {
        using var fixture = new AuthFixture();
        await fixture.Login();
        Assert.True(fixture.Auth.Connected);
        Assert.Equal("me@example.test",fixture.Auth.Email);
        var saved = Path.Combine(fixture.Directory,"chatgpt-credentials.json");
        if (!OperatingSystem.IsWindows()) Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite,File.GetUnixFileMode(saved));
        var returning = QueryHelpers.ParseQuery(new Uri(await fixture.Auth.StartAsync(default)).Query);
        Assert.Equal("oaiapp_test",returning["client_id"].ToString());
        Assert.True(returning.ContainsKey("id_token_hint"));
        fixture.Nonce = returning["nonce"].ToString(); fixture.Subject = "different-user";
        await Assert.ThrowsAsync<DomainException>(() => fixture.Auth.CompleteAsync("test-code",returning["state"].ToString(),null,null,default));
        Assert.Equal("me@example.test",fixture.Auth.Email);
        Assert.True(new ChatGptAuth(fixture.Http,fixture.Directory).Connected);
        await fixture.Auth.DisconnectAsync(default);
        Assert.False(fixture.Auth.Connected); Assert.False(File.Exists(saved));
    }
    [Fact]
    public async Task MissingIssuedClientAndDeniedCallbackDoNotExchangeCode()
    {
        using var fixture = new AuthFixture();
        var q = QueryHelpers.ParseQuery(new Uri(await fixture.Auth.StartAsync(default)).Query);
        await Assert.ThrowsAsync<DomainException>(() => fixture.Auth.CompleteAsync("test-code",q["state"].ToString(),null,null,default));
        Assert.Equal(0,fixture.Requests);
        q = QueryHelpers.ParseQuery(new Uri(await fixture.Auth.StartAsync(default)).Query);
        await Assert.ThrowsAsync<DomainException>(() => fixture.Auth.CompleteAsync("",q["state"].ToString(),null,"access_denied",default));
        Assert.Equal(0,fixture.Requests);
    }

    [Fact]
    public async Task ConcurrentRefreshesRotateOnceAndPersistTogether()
    {
        using var fixture = new AuthFixture();
        await fixture.Login();
        var path = Path.Combine(fixture.Directory,"chatgpt-credentials.json");
        var stored = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(path))!;
        stored["expires_at"] = DateTimeOffset.UtcNow.AddMinutes(-1);
        await File.WriteAllTextAsync(path,stored.ToJsonString());
        var resumed = new ChatGptAuth(fixture.Http,fixture.Directory);
        var tokens = await Task.WhenAll(Enumerable.Range(0,8).Select(_ => resumed.AccessTokenAsync(default)));
        Assert.All(tokens,token => Assert.Equal("rotated-access",token));
        Assert.Equal(1, fixture.Refreshes);
        stored = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(path))!;
        Assert.Equal("rotated-refresh",stored["refresh_token"]!.GetValue<string>());
        Assert.Equal("rotated-access",stored["access_token"]!.GetValue<string>());
    }

    [Fact]
    public async Task SubscriptionInferenceUsesPublicResponsesAndRequiredFieldsOnly()
    {
        using var fixture = new AuthFixture();
        await fixture.Login();
        var config = new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build();
        var client = new AiModelClient(fixture.Http,fixture.Auth,config);
        var reply = await client.CompleteAsync("ChatGpt","account-selected-model","context JSON please",default);
        Assert.Equal("{}",reply.Text);
        Assert.Equal("https://api.openai.com/v1/responses",fixture.InferenceUrl);
        using var sent = JsonDocument.Parse(fixture.InferenceBody!);
        Assert.False(sent.RootElement.GetProperty("store").GetBoolean());
        Assert.True(sent.RootElement.GetProperty("stream").GetBoolean());
        Assert.Equal(JsonValueKind.Array,sent.RootElement.GetProperty("input").ValueKind);
        Assert.False(sent.RootElement.TryGetProperty("max_output_tokens",out _));
        Assert.False(sent.RootElement.TryGetProperty("previous_response_id",out _));
    }

    static HttpResponseMessage Completed(string text = "{}") => new(HttpStatusCode.OK) { Content = new StringContent("data: {\"type\":\"response.created\",\"response\":{\"id\":\"resp_fixture\"}}\n\ndata: {\"type\":\"response.completed\",\"response\":{\"status\":\"completed\",\"output\":[{\"type\":\"message\",\"content\":[{\"type\":\"output_text\",\"text\":" + JsonSerializer.Serialize(text) + "}]}]}}\n\n") };
    static HttpResponseMessage Error(HttpStatusCode status, string body, string? requestId = null)
    {
        var response = new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        if (requestId is not null) response.Headers.Add("x-request-id", requestId);
        return response;
    }
    static AiModelClient Client(AuthFixture fixture) => new(fixture.Http, fixture.Auth, new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build())
    { RetryDelays = [TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero], RetryJitter = 0 };
    static string? Effort(string body) { using var doc = JsonDocument.Parse(body); return doc.RootElement.TryGetProperty("reasoning", out var r) ? r.GetProperty("effort").GetString() : null; }

    [Fact]
    public async Task ARevokedAccessTokenIsRefreshedOnceAndTheCallRetried()
    {
        using var fixture = new AuthFixture();
        await fixture.Login();
        fixture.Inference = call => call == 1 ? Error(HttpStatusCode.Unauthorized, "{\"error\":{\"code\":\"invalid_authentication\"}}") : Completed();
        var reply = await Client(fixture).CompleteAsync("ChatGpt", "model", "prompt JSON", default, "high");
        Assert.Equal("{}", reply.Text);
        Assert.Equal(1, fixture.Refreshes);
        Assert.Equal(["fake-access", "rotated-access"], fixture.InferenceTokens);
        Assert.Equal("resp_fixture", reply.ResponseId);
    }

    [Fact]
    public async Task AStill401AfterRefreshAsksTheUserToReconnect()
    {
        using var fixture = new AuthFixture();
        await fixture.Login();
        fixture.Inference = _ => Error(HttpStatusCode.Unauthorized, "{\"error\":{\"code\":\"subscription_sharing_invalid_user\",\"message\":\"no\"}}", "req_401");
        var error = await Assert.ThrowsAsync<ProviderModelException>(() => Client(fixture).CompleteAsync("ChatGpt", "model", "prompt JSON", default));
        Assert.Equal(ModelFailureKind.SignIn, error.Kind); Assert.Equal("subscription_sharing_invalid_user", error.Code); Assert.Equal("req_401", error.Info.RequestId);
        Assert.Contains("Reconnect ChatGPT", error.Message);
        Assert.Equal(1, fixture.Refreshes); Assert.Equal(2, fixture.InferenceBodies.Count);
        // Credentials are kept: a rejected call is not proof that the refresh token is unusable.
        Assert.True(fixture.Auth.Connected);
    }

    [Fact]
    public async Task ForcedRefreshIsSkippedWhenAnotherCallerAlreadyRenewedTheToken()
    {
        using var fixture = new AuthFixture();
        await fixture.Login();
        Assert.Equal("rotated-access", await fixture.Auth.ForceRefreshAsync("fake-access", default));
        Assert.Equal("rotated-access", await fixture.Auth.ForceRefreshAsync("fake-access", default));
        Assert.Equal(1, fixture.Refreshes);
    }

    [Fact]
    public async Task ModelCatalogRetriesOnceAfterA401()
    {
        using var fixture = new AuthFixture();
        await fixture.Login();
        fixture.Models = call => call == 1 ? Error(HttpStatusCode.Unauthorized, "{}") : new(HttpStatusCode.OK) { Content = new StringContent("{\"models\":[{\"slug\":\"m1\",\"display_name\":\"Model one\",\"visibility\":\"list\"}]}") };
        var models = await fixture.Auth.ModelsAsync(default);
        Assert.Equal("m1", Assert.Single(models).Id);
        Assert.Equal(["fake-access", "rotated-access"], fixture.ModelTokens);
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task ATemporaryAuthServerProblemIsTransientAndKeepsCredentials(HttpStatusCode status)
    {
        using var fixture = new AuthFixture();
        await fixture.Login();
        await ExpireAsync(fixture);
        var resumed = new ChatGptAuth(fixture.Http, fixture.Directory);
        fixture.RefreshReply = () => new HttpResponseMessage(status) { Content = new StringContent("{\"error\":\"temporarily_unavailable\"}") };
        var error = await Assert.ThrowsAsync<TransientModelException>(() => resumed.AccessTokenAsync(default));
        Assert.Equal("sign_in_unavailable", error.Code);
        Assert.DoesNotContain("Start sign-in again", error.Message); Assert.Contains("temporarily unavailable", error.Message);
        Assert.True(resumed.Connected); Assert.True(File.Exists(Path.Combine(fixture.Directory, "chatgpt-credentials.json")));
    }

    [Fact]
    public async Task AnAuthServerHiccupDuringAnInvestigationIsRetriedByTheClient()
    {
        using var fixture = new AuthFixture();
        await fixture.Login();
        await ExpireAsync(fixture);
        var resumed = new ChatGptAuth(fixture.Http, fixture.Directory);
        var refreshes = 0;
        fixture.RefreshReply = () => ++refreshes == 1
            ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { access_token = "rotated-access", refresh_token = "rotated-refresh", token_type = "Bearer", expires_in = 3600 })) };
        var client = new AiModelClient(fixture.Http, resumed, new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build()) { RetryDelays = [TimeSpan.Zero], RetryJitter = 0 };
        var reply = await client.CompleteAsync("ChatGpt", "model", "prompt JSON", default);
        Assert.Equal(2, reply.Attempts); Assert.Equal(["rotated-access"], fixture.InferenceTokens);
    }

    [Theory]
    [InlineData("invalid_grant")]
    [InlineData("refresh_token_expired")]
    [InlineData("refresh_token_reused")]
    public async Task AnUnusableRefreshTokenIsClearedAndAsksForReconnectKeepingTheRegistration(string code)
    {
        using var fixture = new AuthFixture();
        await fixture.Login();
        await ExpireAsync(fixture);
        var resumed = new ChatGptAuth(fixture.Http, fixture.Directory);
        fixture.RefreshReply = () => new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent($"{{\"error\":\"{code}\",\"error_description\":\"Refresh token is no longer valid\"}}") };
        var error = await Assert.ThrowsAsync<ProviderModelException>(() => resumed.AccessTokenAsync(default));
        Assert.Equal(ModelFailureKind.SignIn, error.Kind); Assert.Equal(code, error.Code); Assert.Contains("Reconnect ChatGPT", error.Message);
        Assert.False(resumed.Connected); Assert.False(File.Exists(Path.Combine(fixture.Directory, "chatgpt-credentials.json")));
        var again = QueryHelpers.ParseQuery(new Uri(await resumed.StartAsync(default)).Query);
        Assert.Equal("oaiapp_test", again["client_id"].ToString());
    }

    [Fact]
    public async Task AnUnknownRefreshRejectionAsksForReconnectButKeepsCredentials()
    {
        using var fixture = new AuthFixture();
        await fixture.Login();
        await ExpireAsync(fixture);
        var resumed = new ChatGptAuth(fixture.Http, fixture.Directory);
        fixture.RefreshReply = () => new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("{\"error\":\"access_denied\"}") };
        var error = await Assert.ThrowsAsync<ProviderModelException>(() => resumed.AccessTokenAsync(default));
        Assert.Equal(ModelFailureKind.SignIn, error.Kind); Assert.True(resumed.Connected);
    }

    [Fact]
    public async Task A400NamingTheReasoningParameterIsResentWithoutItAndRemembered()
    {
        using var fixture = new AuthFixture();
        await fixture.Login();
        fixture.Inference = call => call == 1 ? Error(HttpStatusCode.BadRequest, "{\"error\":{\"type\":\"invalid_request_error\",\"code\":\"unsupported_parameter\",\"param\":\"reasoning.effort\",\"message\":\"Unsupported parameter: 'reasoning.effort' is not supported with this model.\"}}") : Completed();
        var client = Client(fixture);
        var reply = await client.CompleteAsync("ChatGpt", "plain-model", "prompt JSON", default, "high");
        Assert.True(reply.ReasoningDropped); Assert.Null(reply.ReasoningEffort);
        Assert.Equal("high", Effort(fixture.InferenceBodies[0])); Assert.Null(Effort(fixture.InferenceBodies[1]));
        Assert.True(client.ModelRejectsReasoning("plain-model"));
        var next = await client.CompleteAsync("ChatGpt", "plain-model", "prompt JSON", default, "high");
        Assert.Equal(3, fixture.InferenceBodies.Count); Assert.Null(Effort(fixture.InferenceBodies[2])); Assert.True(next.ReasoningDropped);
    }

    [Theory]
    [InlineData("{\"error\":{\"type\":\"invalid_request_error\",\"code\":\"invalid_value\",\"param\":\"input\",\"message\":\"bad input\"}}", "invalid_value")]
    [InlineData("{\"error\":{\"type\":\"invalid_request_error\",\"code\":\"model_not_found\",\"param\":\"model\",\"message\":\"The model does not exist\"}}", "model_not_found")]
    [InlineData("{\"error\":{\"code\":\"subscription_sharing_unsupported_capability\",\"param\":\"tools\",\"message\":\"unsupported\"}}", "subscription_sharing_unsupported_capability")]
    public async Task AnyOther400IsReportedWithItsCodeAndNotSilentlyResent(string body, string code)
    {
        using var fixture = new AuthFixture();
        await fixture.Login();
        fixture.Inference = _ => Error(HttpStatusCode.BadRequest, body, "req_bad");
        var client = Client(fixture);
        var error = await Assert.ThrowsAsync<ProviderModelException>(() => client.CompleteAsync("ChatGpt", "model", "prompt JSON", default, "high"));
        Assert.Single(fixture.InferenceBodies); Assert.Equal(ModelFailureKind.Rejected, error.Kind); Assert.Equal(code, error.Code);
        Assert.Equal("req_bad", error.Info.RequestId); Assert.Contains($"\"code\":\"{code}\"", error.Detail); Assert.Contains("x-request-id=req_bad", error.Detail);
        Assert.False(client.ModelRejectsReasoning("model"));
    }

    [Fact]
    public async Task AStreamErrorOnTheSecondTurnIsRetriedAndTheRequestIdIsLogged()
    {
        // The live 05:16 and 07:20 failures: the first call succeeded, the next returned a bare stream "error" event.
        using var fixture = new AuthFixture();
        await fixture.Login();
        fixture.Inference = call =>
        {
            if (call != 2) return Completed();
            var failed = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("data: {\"type\":\"response.created\",\"response\":{\"id\":\"resp_err\"}}\n\ndata: {\"type\":\"error\",\"code\":\"server_error\",\"message\":\"An error occurred while processing your request.\",\"sequence_number\":2}\n\n") };
            failed.Headers.Add("x-request-id", "req_turn2"); failed.Headers.Add("x-ratelimit-remaining-tokens", "0");
            return failed;
        };
        var logger = new ListLogger();
        var client = new AiModelClient(fixture.Http, fixture.Auth, new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(), logger) { RetryDelays = [TimeSpan.Zero], RetryJitter = 0 };
        await client.CompleteAsync("ChatGpt", "model", "turn one JSON", default, "medium", new() { Turn = 1 });
        var progress = new List<ModelRetryNotice>();
        var reply = await client.CompleteAsync("ChatGpt", "model", "turn two JSON", default, "medium", new() { Turn = 2, Progress = new Sync(progress.Add) });
        Assert.Equal(2, reply.Attempts);
        var notice = Assert.Single(progress);
        Assert.Equal("ChatGPT hiccup (server_error), trying again in under a second (2 of 2).", notice.Message);
        Assert.Equal("req_turn2", notice.RequestId);
        var failure = Assert.Single(logger.Lines, l => l.Contains("AI provider call failed"));
        Assert.Contains("turn 2", failure); Assert.Contains("request=req_turn2", failure); Assert.Contains("response=resp_err", failure);
        Assert.Contains("code=server_error", failure); Assert.Contains("Provider detail", failure);
        // Rate-limit headers on the HTTP 200 stream are what separate throttling from an outage.
        Assert.Contains("x-ratelimit-remaining-tokens=0", failure);
        Assert.DoesNotContain("fake-access", string.Join("\n", logger.Lines));
    }

    [Fact]
    public async Task AnAnswerCutOffAtTheOutputCapIsRetriedOnceWithLessReasoning()
    {
        using var fixture = new AuthFixture();
        await fixture.Login();
        fixture.Inference = call => call == 1
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("data: {\"type\":\"response.incomplete\",\"response\":{\"status\":\"incomplete\",\"incomplete_details\":{\"reason\":\"max_output_tokens\"}}}\n\n") }
            : Completed();
        var reply = await Client(fixture).CompleteAsync("ChatGpt", "model", "prompt JSON", default, "high");
        Assert.Equal("medium", reply.ReasoningEffort);
        Assert.Equal(["high", "medium"], fixture.InferenceBodies.Select(Effort));
    }

    [Fact]
    public async Task AContentFilterIsReportedWithoutRetrying()
    {
        using var fixture = new AuthFixture();
        await fixture.Login();
        fixture.Inference = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("data: {\"type\":\"response.incomplete\",\"response\":{\"status\":\"incomplete\",\"incomplete_details\":{\"reason\":\"content_filter\"}}}\n\n") };
        var error = await Assert.ThrowsAsync<ProviderModelException>(() => Client(fixture).CompleteAsync("ChatGpt", "model", "prompt JSON", default, "high"));
        Assert.Equal(ModelFailureKind.ContentFilter, error.Kind); Assert.Single(fixture.InferenceBodies);
        Assert.Contains("declined", error.Message);
    }

    [Fact]
    public async Task ATimedOutStepIsRetriedOnceWithLessReasoning()
    {
        using var fixture = new AuthFixture();
        await fixture.Login();
        fixture.Inference = call => call == 1 ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new Stall()) } : Completed();
        var client = Client(fixture); client.CallTimeout = TimeSpan.FromMilliseconds(150);
        var reply = await client.CompleteAsync("ChatGpt", "model", "prompt JSON", default, "high");
        Assert.Equal(["high", "medium"], fixture.InferenceBodies.Select(Effort)); Assert.Equal(2, reply.Attempts);
    }

    [Fact]
    public async Task UsageLimitAfterStreamingBeganIsNotRetried()
    {
        using var fixture = new AuthFixture();
        await fixture.Login();
        fixture.Inference = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("data: {\"type\":\"response.created\",\"response\":{\"id\":\"resp_lim\"}}\n\ndata: {\"type\":\"response.failed\",\"response\":{\"status\":\"failed\",\"error\":{\"code\":\"subscription_sharing_usage_limit_exceeded\",\"message\":\"limit\"}}}\n\n") };
        var error = await Assert.ThrowsAsync<ProviderModelException>(() => Client(fixture).CompleteAsync("ChatGpt", "model", "prompt JSON", default));
        Assert.Equal(ModelFailureKind.UsageLimit, error.Kind); Assert.Single(fixture.InferenceBodies);
        Assert.Contains("ChatGPT plan", error.Message); Assert.Contains("Usage", error.Message); Assert.Equal("resp_lim", error.Info.ResponseId);
    }

    [Fact]
    public async Task InstructionsGoInTheResponsesInstructionsField()
    {
        using var fixture = new AuthFixture();
        await fixture.Login();
        await Client(fixture).CompleteAsync("ChatGpt", "model", "prompt JSON", default, null, new() { Instructions = "Stable rules" });
        using var sent = JsonDocument.Parse(fixture.InferenceBody!);
        Assert.Equal("Stable rules", sent.RootElement.GetProperty("instructions").GetString());
        Assert.All(sent.RootElement.GetProperty("input").EnumerateArray(), item => Assert.Equal("user", item.GetProperty("role").GetString()));
    }

    static async Task ExpireAsync(AuthFixture fixture)
    {
        var path = Path.Combine(fixture.Directory, "chatgpt-credentials.json");
        var stored = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(path))!;
        stored["expires_at"] = DateTimeOffset.UtcNow.AddMinutes(-1);
        await File.WriteAllTextAsync(path, stored.ToJsonString());
    }
    sealed class Sync(Action<ModelRetryNotice> report) : IProgress<ModelRetryNotice> { public void Report(ModelRetryNotice value) => report(value); }
    sealed class ListLogger : Microsoft.Extensions.Logging.ILogger<AiModelClient>
    {
        public List<string> Lines { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel level) => true;
        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel level, Microsoft.Extensions.Logging.EventId id, TState state, Exception? exception, Func<TState, Exception?, string> formatter) { lock (Lines) Lines.Add(formatter(state, exception)); }
    }
    sealed class Stall : Stream
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) { await Task.Delay(Timeout.Infinite, ct); return 0; }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    sealed class AuthFixture : HttpMessageHandler
    {
        public readonly string Directory = Path.Combine(Path.GetTempPath(),Guid.NewGuid().ToString("N"));
        public HttpClient Http { get; }
        public ChatGptAuth Auth { get; }
        readonly RSA signing = RSA.Create(2048);
        public string Nonce = "";
        public string Subject = "test-subject";
        public string? Fault;
        public int Requests;
        public int Refreshes;
        public string? InferenceBody;
        public string? InferenceUrl;
        /// <summary>Scripts inference replies by call number; null keeps the default completed reply.</summary>
        public Func<int, HttpResponseMessage>? Inference;
        public List<string> InferenceBodies { get; } = [];
        public List<string?> InferenceTokens { get; } = [];
        public Func<HttpResponseMessage>? RefreshReply;
        public Func<int, HttpResponseMessage>? Models;
        public List<string?> ModelTokens { get; } = [];
        public AuthFixture() { Http = new HttpClient(this); Auth = new ChatGptAuth(Http, Directory); }
        public async Task Login()
        {
            var q = QueryHelpers.ParseQuery(new Uri(await Auth.StartAsync(default)).Query);
            Nonce = q["nonce"].ToString();
            await Auth.CompleteAsync("test-code",q["state"].ToString(),"oaiapp_test",null,default);
        }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {
            Requests++;
            if (request.RequestUri!.AbsolutePath == "/v1/responses")
            {
                InferenceUrl = request.RequestUri.ToString();
                InferenceBody = await request.Content!.ReadAsStringAsync(ct);
                InferenceBodies.Add(InferenceBody); InferenceTokens.Add(request.Headers.Authorization?.Parameter);
                if (Inference is not null) return Inference(InferenceBodies.Count);
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("data: {\"type\":\"response.completed\",\"response\":{\"status\":\"completed\",\"output\":[{\"type\":\"message\",\"content\":[{\"type\":\"output_text\",\"text\":\"{}\"}]}]}}\n\n") };
            }
            if (request.Content is not null && (await request.Content.ReadAsStringAsync(ct)).Contains("grant_type=refresh_token"))
            {
                Refreshes++;
                if (RefreshReply is not null) return RefreshReply();
                return await Reply(new { access_token="rotated-access",refresh_token="rotated-refresh",token_type="Bearer",expires_in=3600 });
            }
            if (request.RequestUri!.AbsolutePath == "/v1/models" && Models is not null)
            {
                ModelTokens.Add(request.Headers.Authorization?.Parameter);
                return Models(ModelTokens.Count);
            }
            if (request.RequestUri!.AbsolutePath.EndsWith("jwks.json"))
            {
                var key = signing.ExportParameters(false);
                return await Reply(new { keys = new[] { new { kid="test-key", kty="RSA", alg="RS256", use="sig", n=Url(key.Modulus!), e=Url(key.Exponent!) } } });
            }
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var head = Url(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { alg="RS256",kid="test-key" })));
            var claims = Url(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { iss=Fault == "wrong_issuer" ? "https://bad.test" : "https://auth.openai.com", aud=Fault == "wrong_audience" ? "another-client" : "oaiapp_test", sub=Subject, email="me@example.test", nonce=Fault == "wrong_nonce" ? "wrong" : Nonce, exp=Fault == "expired" ? now-100 : now+3600,iat=now })));
            var signature = signing.SignData(Encoding.ASCII.GetBytes(head+"."+claims),HashAlgorithmName.SHA256,RSASignaturePadding.Pkcs1);
            if (Fault == "wrong_signature") signature[0] ^= 0xff;
            return await Reply(new { access_token="fake-access",refresh_token="fake-refresh",token_type="Bearer",expires_in=3600,scope=Fault == "missing_plan_scope" ? "openid" : "openid resource.invoke chatgpt.tokens.use.direct",id_token=head+"."+claims+"."+Url(signature) });
        }
        static Task<HttpResponseMessage> Reply(object value) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content=new StringContent(JsonSerializer.Serialize(value)) });
        static string Url(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+','-').Replace('/','_');
        protected override void Dispose(bool disposing) { if (disposing) { signing.Dispose(); if (System.IO.Directory.Exists(Directory)) System.IO.Directory.Delete(Directory,true); } base.Dispose(disposing); }
    }
}
