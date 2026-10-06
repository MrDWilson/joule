using System.Net;
using System.Text;
using Microsoft.Extensions.Configuration;
using Joule;
using Xunit;

/// <summary>Retry ladder, rate limits, stream resets and failure diagnostics of the model client, against fake handlers only.</summary>
public sealed class AiProviderResilienceTests : IDisposable
{
    readonly string dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
    public void Dispose() { if (Directory.Exists(dir)) Directory.Delete(dir, true); }

    const string Ok = "{\"id\":\"chatcmpl-fixture\",\"choices\":[{\"finish_reason\":\"stop\",\"message\":{\"content\":\"{}\"}}],\"usage\":{\"prompt_tokens\":100,\"completion_tokens\":7,\"prompt_tokens_details\":{\"cached_tokens\":64},\"completion_tokens_details\":{\"reasoning_tokens\":3}}}";
    static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    static HttpResponseMessage Success() => Json(HttpStatusCode.OK, Ok);

    (AiModelClient Client, Scripted Handler, List<TimeSpan> Waits) Build(Func<int, HttpRequestMessage, HttpResponseMessage> respond, Dictionary<string, string?>? settings = null)
    {
        var handler = new Scripted(respond);
        var http = new HttpClient(handler);
        var values = new Dictionary<string, string?> { ["Ai:ApiKey"] = "fixture-key" };
        foreach (var (k, v) in settings ?? []) values[k] = v;
        var config = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var waits = new List<TimeSpan>();
        var client = new AiModelClient(http, new ChatGptAuth(http, dir), config)
        {
            RetryDelays = [TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(20), TimeSpan.FromMinutes(1)],
            RetryJitter = 0,
            Delay = (wait, _) => { waits.Add(wait); return Task.CompletedTask; }
        };
        return (client, handler, waits);
    }

    sealed class Scripted(Func<int, HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Calls;
        public List<string> Bodies { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            Bodies.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct));
            return respond(Calls, request);
        }
    }

    sealed class Recorder : IProgress<ModelRetryNotice>
    {
        public List<ModelRetryNotice> Notices { get; } = [];
        public void Report(ModelRetryNotice value) => Notices.Add(value);
    }

    [Fact]
    public void DefaultLadderOutlastsAMultiMinuteOutage()
    {
        using var http = new HttpClient();
        var client = new AiModelClient(http, new ChatGptAuth(http, dir), new ConfigurationBuilder().Build());
        // The 07:20 failure retried for only 40 s; live outages lasted minutes.
        Assert.True(client.RetryDelays[0] <= TimeSpan.FromSeconds(10));
        Assert.True(client.RetryDelays.Aggregate(TimeSpan.Zero, (a, b) => a + b) >= TimeSpan.FromMinutes(10));
        Assert.Equal(client.RetryDelays.OrderBy(x => x), client.RetryDelays);
        Assert.InRange(client.RetryJitter, 0.05, 0.5);
    }

    [Fact]
    public async Task TransientFailuresFollowTheLadderReportProgressAndGiveUpWithAttemptCount()
    {
        var (client, handler, waits) = Build((_, _) => Json(HttpStatusCode.ServiceUnavailable, "{\"error\":{\"code\":\"server_is_overloaded\",\"message\":\"busy\"}}"));
        var progress = new Recorder();
        var error = await Assert.ThrowsAsync<TransientModelException>(() => client.CompleteAsync("Api", "fixture", "prompt", default, null, new() { Progress = progress, Turn = 3 }));
        Assert.Equal(4, handler.Calls);
        Assert.Equal([TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(20), TimeSpan.FromMinutes(1)], waits);
        Assert.Equal(ModelFailureKind.Transient, error.Kind); Assert.Equal("server_is_overloaded", error.Code);
        Assert.Equal(4, error.Info.Attempts); Assert.Equal(3, error.Info.Turn); Assert.Equal(503, error.Info.HttpStatus);
        Assert.Contains("Joule tried 4 times", error.Message);
        Assert.Equal([2, 3, 4], progress.Notices.Select(n => n.NextAttempt));
        Assert.All(progress.Notices, n => Assert.Equal(4, n.MaxAttempts));
        Assert.Equal("The AI service hiccup (server_is_overloaded), trying again in 20 s (3 of 4).", progress.Notices[1].Message);
        Assert.Equal(TimeSpan.FromSeconds(20), progress.Notices[1].Delay);
    }

    [Fact]
    public async Task JitterSpreadsRetriesWithinTheConfiguredFraction()
    {
        var (client, _, waits) = Build((call, _) => call < 3 ? Json(HttpStatusCode.BadGateway, "") : Success());
        client.RetryJitter = 0.2;
        await client.CompleteAsync("Api", "fixture", "prompt", default);
        Assert.InRange(waits[0].TotalSeconds, 4, 6); Assert.InRange(waits[1].TotalSeconds, 16, 24);
    }

    [Fact]
    public async Task ACallerSuppliedLadderReplacesTheDefault()
    {
        var (client, handler, waits) = Build((_, _) => Json(HttpStatusCode.InternalServerError, ""));
        await Assert.ThrowsAsync<TransientModelException>(() => client.CompleteAsync("Api", "fixture", "prompt", default, null, new() { RetryDelays = [TimeSpan.FromSeconds(3)] }));
        Assert.Equal(2, handler.Calls); Assert.Equal([TimeSpan.FromSeconds(3)], waits);
    }

    [Fact]
    public async Task SuccessCarriesCachedAndReasoningTokensRequestIdAndAttempts()
    {
        var (client, _, _) = Build((call, _) =>
        {
            var response = call == 1 ? Json(HttpStatusCode.BadGateway, "") : Success();
            response.Headers.Add("x-request-id", $"req_fixture{call}");
            return response;
        });
        var reply = await client.CompleteAsync("Api", "fixture", "prompt", default);
        Assert.Equal(100, reply.InputTokens); Assert.Equal(64, reply.CachedInputTokens); Assert.Equal(3, reply.ReasoningTokens);
        Assert.Equal("req_fixture2", reply.RequestId); Assert.Equal("chatcmpl-fixture", reply.ResponseId); Assert.Equal(2, reply.Attempts); Assert.Equal("fixture", reply.Model);
    }

    [Fact]
    public async Task RateLimitHonoursAShortRetryAfter()
    {
        var (client, handler, waits) = Build((call, _) =>
        {
            if (call > 1) return Success();
            var limited = Json(HttpStatusCode.TooManyRequests, "{\"error\":{\"code\":\"rate_limit_exceeded\",\"message\":\"Rate limit reached for tokens per min.\"}}");
            limited.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(7));
            return limited;
        });
        var progress = new Recorder();
        await client.CompleteAsync("Api", "fixture", "prompt", default, null, new() { Progress = progress });
        Assert.Equal(2, handler.Calls);
        Assert.InRange(waits.Single().TotalSeconds, 7, 8);
        Assert.Equal(ModelFailureKind.RateLimited, progress.Notices.Single().Kind);
        Assert.Contains("slow down", progress.Notices.Single().Message);
    }

    [Theory]
    [InlineData("x-ratelimit-reset-tokens", "1.5s", 1.5)]
    [InlineData("retry-after-ms", "2500", 2.5)]
    [InlineData("x-ratelimit-reset-requests", "1m30s", 90)]
    public async Task RateLimitReadsOpenAiResetHeaders(string header, string value, double seconds)
    {
        var (client, _, waits) = Build((call, _) =>
        {
            if (call > 1) return Success();
            var limited = Json(HttpStatusCode.TooManyRequests, "{\"error\":{\"code\":\"rate_limit_exceeded\"}}");
            limited.Headers.TryAddWithoutValidation(header, value);
            return limited;
        });
        await client.CompleteAsync("Api", "fixture", "prompt", default);
        Assert.InRange(waits.Single().TotalSeconds, seconds, seconds + 0.5);
    }

    [Fact]
    public async Task RateLimitWaitHintInTheMessageIsHonoured()
    {
        var (client, _, waits) = Build((call, _) => call > 1 ? Success() : Json(HttpStatusCode.TooManyRequests, "{\"error\":{\"code\":\"rate_limit_exceeded\",\"message\":\"Rate limit reached. Please try again in 11.054s.\"}}"));
        await client.CompleteAsync("Api", "fixture", "prompt", default);
        Assert.InRange(waits.Single().TotalSeconds, 11.05, 11.6);
    }

    [Fact]
    public async Task ALongRateLimitEndsTheCallWithItsResetTime()
    {
        var (client, handler, waits) = Build((_, _) =>
        {
            var limited = Json(HttpStatusCode.TooManyRequests, "{\"error\":{\"code\":\"rate_limit_exceeded\"}}");
            limited.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromMinutes(20));
            return limited;
        });
        var before = DateTimeOffset.UtcNow;
        var error = await Assert.ThrowsAsync<TransientModelException>(() => client.CompleteAsync("Api", "fixture", "prompt", default));
        Assert.Equal(1, handler.Calls); Assert.Empty(waits);
        Assert.Equal(ModelFailureKind.RateLimited, error.Kind); Assert.Equal(TimeSpan.FromMinutes(20), error.Info.RetryAfter);
        Assert.InRange(error.Info.ResetsAt!.Value, before.AddMinutes(19), DateTimeOffset.UtcNow.AddMinutes(21));
        Assert.Contains("wait about 20 min", error.Message);
    }

    [Theory]
    [InlineData("{\"error\":{\"code\":\"subscription_sharing_usage_limit_exceeded\",\"message\":\"limit\"}}")]
    [InlineData("{\"error\":{\"code\":\"insufficient_quota\",\"type\":\"insufficient_quota\"}}")]
    [InlineData("{\"error\":{\"type\":\"usage_limit_reached\",\"message\":\"The usage limit has been reached\",\"resets_in_seconds\":3600}}")]
    public async Task UsageLimitsAreNeverRetriedEvenWithARetryAfter(string body)
    {
        var (client, handler, waits) = Build((_, _) =>
        {
            var limited = Json(HttpStatusCode.TooManyRequests, body);
            limited.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(1));
            return limited;
        });
        var error = await Assert.ThrowsAsync<ProviderModelException>(() => client.CompleteAsync("Api", "fixture", "prompt", default));
        Assert.Equal(1, handler.Calls); Assert.Empty(waits);
        Assert.Equal(ModelFailureKind.UsageLimit, error.Kind);
        Assert.Contains("usage limit", error.Message);
    }

    [Fact]
    public async Task UsageLimitResetTimeIsKeptWhenTheProviderGivesIt()
    {
        var resetsAt = DateTimeOffset.UtcNow.AddHours(3);
        var (client, _, _) = Build((_, _) => Json(HttpStatusCode.TooManyRequests, $"{{\"error\":{{\"type\":\"usage_limit_reached\",\"resets_at\":{resetsAt.ToUnixTimeSeconds()}}}}}"));
        var error = await Assert.ThrowsAsync<ProviderModelException>(() => client.CompleteAsync("Api", "fixture", "prompt", default));
        Assert.Equal(resetsAt.ToUnixTimeSeconds(), error.Info.ResetsAt!.Value.ToUnixTimeSeconds());
    }

    [Fact]
    public async Task ABare429IsNotHammered()
    {
        var (client, handler, _) = Build((_, _) => Json(HttpStatusCode.TooManyRequests, "upstream text"));
        var error = await Assert.ThrowsAsync<ProviderModelException>(() => client.CompleteAsync("Api", "fixture", "prompt", default));
        Assert.Equal(1, handler.Calls); Assert.Equal(ModelFailureKind.RateLimited, error.Kind); Assert.Contains("HTTP 429", error.Message);
        Assert.DoesNotContain("upstream text", error.Message);
    }

    [Fact]
    public async Task PreStreamAdmissionDetailBodyIsKeptForTheLogNotTheUser()
    {
        var (client, handler, _) = Build((_, _) => Json(HttpStatusCode.Forbidden, "{\"detail\":\"Direct routing not permitted in this region\"}"));
        var error = await Assert.ThrowsAsync<ProviderModelException>(() => client.CompleteAsync("Api", "fixture", "prompt", default));
        Assert.Equal(1, handler.Calls); Assert.Equal(ModelFailureKind.Rejected, error.Kind);
        Assert.Contains("Direct routing not permitted", error.Detail); Assert.DoesNotContain("Direct routing", error.Message);
    }

    [Fact]
    public async Task NetworkFailureIsRetried()
    {
        var (client, handler, _) = Build((call, _) => call == 1 ? throw new HttpRequestException(HttpRequestError.ConnectionError, "connection refused") : Success());
        var reply = await client.CompleteAsync("Api", "fixture", "prompt", default);
        Assert.Equal(2, handler.Calls); Assert.Equal(2, reply.Attempts);
    }

    [Fact]
    public async Task AConnectionResetWhileReadingTheBodyIsRetried()
    {
        var (client, handler, _) = Build((call, _) => call == 1
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new ResettingStream("{\"choices\":[")) }
            : Success());
        var reply = await client.CompleteAsync("Api", "fixture", "prompt", default);
        Assert.Equal("{}", reply.Text); Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task ASubscriptionStreamResetIsTransientAndKeepsTheResponseId()
    {
        var events = "data: {\"type\":\"response.created\",\"response\":{\"id\":\"resp_fixture1\",\"status\":\"in_progress\"}}\n\ndata: {\"type\":\"response.output_text.delta\",\"delta\":\"{\\\"act\"}\n\n";
        var error = await Assert.ThrowsAsync<TransientModelException>(() => AiModelClient.ReadResponsesStreamAsync(new ResettingStream(events), default, "req_fixture"));
        Assert.Equal("stream_interrupted", error.Code); Assert.Equal("resp_fixture1", error.Info.ResponseId); Assert.Equal("req_fixture", error.Info.RequestId);
        Assert.Contains("HttpIOException", error.Detail); Assert.Contains("response.output_text.delta", error.Detail);
        Assert.Equal("req_fixture · resp_fixture1", error.Info.Reference);
    }

    [Fact]
    public async Task AMalformedStreamEventIsTransient()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("data: {\"type\":\"response.created\",\"response\":{\"id\":\"resp_x\"}}\n\ndata: {oops\n\n"));
        var error = await Assert.ThrowsAsync<TransientModelException>(() => AiModelClient.ReadResponsesStreamAsync(stream, default));
        Assert.Equal("malformed_event", error.Code); Assert.Equal("resp_x", error.Info.ResponseId);
    }

    [Theory]
    [InlineData("data: {\"type\":\"error\",\"code\":\"server_error\",\"message\":\"The server had an error\",\"param\":null,\"sequence_number\":4}\n\n", "server_error")]
    [InlineData("data: {\"type\":\"error\",\"error\":{\"type\":\"server_error\",\"code\":null,\"message\":\"An error occurred\"}}\n\n", "server_error")]
    [InlineData("data: {\"type\":\"response.failed\",\"response\":{\"id\":\"resp_1\",\"status\":\"failed\",\"error\":{\"code\":\"server_error\",\"message\":\"boom\"}}}\n\n", "server_error")]
    [InlineData("data: {\"type\":\"error\",\"message\":\"Something went wrong\"}\n\n", null)]
    public async Task StreamErrorShapesKeepTheirCode(string events, string? code)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(events));
        var error = await Assert.ThrowsAsync<TransientModelException>(() => AiModelClient.ReadResponsesStreamAsync(stream, default));
        Assert.Equal(code, error.Code); Assert.Equal(ModelFailureKind.Transient, error.Kind);
        // The message never shows the meaningless event type "error" as if it were a cause.
        Assert.DoesNotContain("(error)", error.Message);
        Assert.Contains("message=", error.Detail);
    }

    [Fact]
    public async Task StreamRateLimitCarriesItsWaitHint()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("data: {\"type\":\"response.failed\",\"response\":{\"error\":{\"code\":\"rate_limit_exceeded\",\"message\":\"Rate limit reached for gpt in organization on tokens per min (TPM): Limit 30000. Please try again in 1m12s.\"}}}\n\n"));
        var error = await Assert.ThrowsAsync<TransientModelException>(() => AiModelClient.ReadResponsesStreamAsync(stream, default));
        Assert.Equal(ModelFailureKind.RateLimited, error.Kind); Assert.Equal(TimeSpan.FromSeconds(72), error.Info.RetryAfter);
    }

    [Theory]
    [InlineData("data: {\"type\":\"response.incomplete\",\"response\":{\"status\":\"incomplete\",\"incomplete_details\":{\"reason\":\"max_output_tokens\"}}}\n\n", ModelFailureKind.Incomplete, "max_output_tokens")]
    [InlineData("data: {\"type\":\"response.incomplete\",\"response\":{\"status\":\"incomplete\",\"incomplete_details\":{\"reason\":\"content_filter\"}}}\n\n", ModelFailureKind.ContentFilter, "content_filter")]
    [InlineData("data: {\"type\":\"response.completed\",\"response\":{\"status\":\"incomplete\",\"incomplete_details\":{\"reason\":\"max_output_tokens\"}}}\n\n", ModelFailureKind.Incomplete, "max_output_tokens")]
    public async Task IncompleteResponsesKeepTheirReason(string events, ModelFailureKind kind, string reason)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(events));
        var error = await Assert.ThrowsAsync<ProviderModelException>(() => AiModelClient.ReadResponsesStreamAsync(stream, default));
        Assert.Equal(kind, error.Kind); Assert.Equal(reason, error.Info.IncompleteReason);
        Assert.Contains($"reason={reason}", error.Detail);
        Assert.DoesNotContain("response.incomplete", error.Message);
    }

    [Fact]
    public async Task CompletedStreamReportsCachedAndReasoningTokensAndResponseId()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("data: {\"type\":\"response.created\",\"response\":{\"id\":\"resp_ok\"}}\n\ndata: {\"type\":\"response.completed\",\"response\":{\"id\":\"resp_ok\",\"status\":\"completed\",\"output\":[{\"content\":[{\"type\":\"output_text\",\"text\":\"{}\"}]}],\"usage\":{\"input_tokens\":18038,\"input_tokens_details\":{\"cached_tokens\":17920},\"output_tokens\":282,\"output_tokens_details\":{\"reasoning_tokens\":200}}}}\n\n"));
        var reply = await AiModelClient.ReadResponsesStreamAsync(stream, default, "req_ok");
        Assert.Equal(17920, reply.CachedInputTokens); Assert.Equal(200, reply.ReasoningTokens); Assert.Equal("resp_ok", reply.ResponseId); Assert.Equal("req_ok", reply.RequestId);
    }

    [Fact]
    public async Task AStepThatRunsPastTheCallTimeoutIsRetriedOnceThenReportedAsATimeout()
    {
        var (client, handler, _) = Build((_, request) => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new StallingStream()) });
        client.CallTimeout = TimeSpan.FromMilliseconds(100);
        var error = await Assert.ThrowsAsync<TransientModelException>(() => client.CompleteAsync("Api", "fixture", "prompt", default));
        Assert.Equal(ModelFailureKind.Timeout, error.Kind); Assert.Equal(2, handler.Calls);
        Assert.Contains("took more than", error.Message);
    }

    [Fact]
    public async Task StoppingDuringARetryWaitIsACancellationNotAProviderFailure()
    {
        var (client, handler, _) = Build((_, _) => Json(HttpStatusCode.ServiceUnavailable, ""));
        using var stop = new CancellationTokenSource();
        client.Delay = (_, ct) => { stop.Cancel(); return Task.Delay(Timeout.Infinite, ct); };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.CompleteAsync("Api", "fixture", "prompt", stop.Token));
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task AConfiguredFallbackModelIsTriedOnceAfterRetriesRunOutAndIsLabelled()
    {
        var models = new List<string>();
        Scripted? handler = null;
        var progress = new Recorder();
        var scripted = Build((_, _) =>
        {
            using var body = System.Text.Json.JsonDocument.Parse(handler!.Bodies[^1]);
            models.Add(body.RootElement.GetProperty("model").GetString()!);
            return models[^1] == "steady-model" ? Success() : Json(HttpStatusCode.ServiceUnavailable, "");
        }, new() { ["Ai:FallbackModel"] = "steady-model" });
        handler = scripted.Handler;
        var reply = await scripted.Client.CompleteAsync("Api", "preview-model", "prompt", default, null, new() { Progress = progress });
        Assert.Equal(["preview-model", "preview-model", "preview-model", "preview-model", "steady-model"], models);
        Assert.True(reply.FallbackModelUsed); Assert.Equal("steady-model", reply.Model);
        Assert.Contains("trying steady-model once", progress.Notices[^1].Message);
    }

    [Fact]
    public async Task NoFallbackIsUsedForAUsageLimit()
    {
        var (client, handler, _) = Build((_, _) => Json(HttpStatusCode.TooManyRequests, "{\"error\":{\"code\":\"insufficient_quota\"}}"), new() { ["Ai:FallbackModel"] = "steady-model" });
        await Assert.ThrowsAsync<ProviderModelException>(() => client.CompleteAsync("Api", "preview-model", "prompt", default));
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task AFaultyProgressHandlerCannotBreakTheRetry()
    {
        var (client, _, _) = Build((call, _) => call == 1 ? Json(HttpStatusCode.BadGateway, "") : Success());
        var reply = await client.CompleteAsync("Api", "fixture", "prompt", default, null, new() { Progress = new Throwing() });
        Assert.Equal(2, reply.Attempts);
    }
    sealed class Throwing : IProgress<ModelRetryNotice> { public void Report(ModelRetryNotice value) => throw new InvalidOperationException("handler bug"); }

    [Fact]
    public async Task InstructionsAreSentAsASystemMessageToTheApiProvider()
    {
        var (client, handler, _) = Build((_, _) => Success());
        await client.CompleteAsync("Api", "fixture", "prompt mentioning JSON", default, null, new() { Instructions = "Stable instructions" });
        using var body = System.Text.Json.JsonDocument.Parse(handler.Bodies.Single());
        var messages = body.RootElement.GetProperty("messages");
        Assert.Equal("system", messages[0].GetProperty("role").GetString()); Assert.Equal("Stable instructions", messages[0].GetProperty("content").GetString());
        Assert.Equal("user", messages[1].GetProperty("role").GetString());
    }

    [Theory]
    [InlineData("length", ModelFailureKind.Incomplete)]
    [InlineData("content_filter", ModelFailureKind.ContentFilter)]
    public async Task ApiFinishReasonsAreClassified(string finish, ModelFailureKind kind)
    {
        var (client, handler, _) = Build((_, _) => Json(HttpStatusCode.OK, $"{{\"choices\":[{{\"finish_reason\":\"{finish}\",\"message\":{{\"content\":\"{{}}\"}}}}]}}"));
        var error = await Assert.ThrowsAsync<ProviderModelException>(() => client.CompleteAsync("Api", "fixture", "prompt", default));
        Assert.Equal(kind, error.Kind); Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData("high", "medium")]
    [InlineData("medium", "low")]
    [InlineData("low", "low")]
    [InlineData(null, null)]
    public void LoweringReasoningStepsDownOneNotch(string? effort, string? lower) => Assert.Equal(lower, AiModelClient.Lower(effort));

    [Fact]
    public void ResponsesRequestCarriesInstructionsOnlyWhenGivenAndNeverUnsupportedPlanFields()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(AiModelClient.ResponsesRequestBody("m", "prompt JSON", "high", "Be brief"));
        using var body = System.Text.Json.JsonDocument.Parse(json);
        Assert.Equal("Be brief", body.RootElement.GetProperty("instructions").GetString());
        Assert.Equal("high", body.RootElement.GetProperty("reasoning").GetProperty("effort").GetString());
        foreach (var unsupported in new[] { "max_output_tokens", "previous_response_id", "prompt_cache_key", "temperature", "truncation", "metadata", "user", "include" })
            Assert.False(body.RootElement.TryGetProperty(unsupported, out _), unsupported);
        Assert.DoesNotContain("\"system\"", json);
        using var plain = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(AiModelClient.ResponsesRequestBody("m", "prompt JSON")));
        Assert.False(plain.RootElement.TryGetProperty("instructions", out _)); Assert.False(plain.RootElement.TryGetProperty("reasoning", out _));
    }

    /// <summary>Returns the given bytes, then fails the way a dropped TLS/HTTP connection does.</summary>
    sealed class ResettingStream(string prefix) : Stream
    {
        readonly byte[] bytes = Encoding.UTF8.GetBytes(prefix); int position;
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (position >= bytes.Length) throw new HttpIOException(HttpRequestError.ResponseEnded, "The response ended prematurely.");
            var n = Math.Min(count, bytes.Length - position); Array.Copy(bytes, position, buffer, offset, n); position += n; return n;
        }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (position >= bytes.Length) throw new HttpIOException(HttpRequestError.ResponseEnded, "The response ended prematurely.");
            var n = Math.Min(buffer.Length, bytes.Length - position); bytes.AsMemory(position, n).CopyTo(buffer); position += n; return ValueTask.FromResult(n);
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>A body that never produces a byte until cancelled, like a reasoning model thinking forever.</summary>
    sealed class StallingStream : Stream
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
}
