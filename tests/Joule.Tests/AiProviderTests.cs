using System.Net;
using System.Text;
using Microsoft.Extensions.Configuration;
using Joule;
using Xunit;

public class AiProviderTests
{
    [Fact]
    public async Task SubscriptionStreamRequiresCompletedAndReadsUsage()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("data: {\"type\":\"response.output_text.delta\",\"delta\":\"partial\"}\n\ndata: {\"type\":\"response.completed\",\"response\":{\"status\":\"completed\",\"output\":[{\"type\":\"message\",\"content\":[{\"type\":\"output_text\",\"text\":\"{\\\"action\\\":\\\"finish\\\"}\"}]}],\"usage\":{\"input_tokens\":21,\"output_tokens\":9}}}\n\n"));
        var result = await AiModelClient.ReadResponsesStreamAsync(stream, default);
        Assert.Equal("{\"action\":\"finish\"}", result.Text);
        Assert.Equal(21, result.InputTokens);
        Assert.Equal(9, result.OutputTokens);
    }
    [Theory]
    [InlineData("data: {\"type\":\"response.output_text.delta\",\"delta\":\"partial\"}\n\n")]
    [InlineData("data: {\"type\":\"response.failed\",\"response\":{\"error\":{\"code\":\"subscription_sharing_usage_limit_exceeded\"}}}\n\n")]
    [InlineData("data: {\"type\":\"response.incomplete\",\"response\":{}}\n\n")]
    public async Task IncompleteOrFailedStreamIsNeverSuccess(string events)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(events));
        await Assert.ThrowsAnyAsync<DomainException>(() => AiModelClient.ReadResponsesStreamAsync(stream, default));
    }
    [Fact]
    public void SubscriptionRequestAsksForAJsonObjectReply()
    {
        using var body = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(AiModelClient.ResponsesRequestBody("fixture-model", "prompt mentioning JSON")));
        var root = body.RootElement;
        Assert.Equal("json_object", root.GetProperty("text").GetProperty("format").GetProperty("type").GetString());
        Assert.False(root.GetProperty("store").GetBoolean()); Assert.True(root.GetProperty("stream").GetBoolean()); Assert.Equal("fixture-model", root.GetProperty("model").GetString());
    }
    [Fact]
    public async Task WrongAuthStateNeverExchangesCode()
    {
        using var http = new HttpClient(new NoNetwork());
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try {
            var auth = new ChatGptAuth(http, dir);
            await auth.StartAsync(default);
            await Assert.ThrowsAsync<DomainException>(() => auth.CompleteAsync("code", "wrong", "issued", null, default));
            Assert.False(auth.Connected);
        } finally { Directory.Delete(dir, true); }
    }
    [Fact]
    public async Task ApiFailureDoesNotLeakResponseBodyOrFallback()
    {
        using var http = new HttpClient(new FailureHandler());
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try {
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> { ["Ai:ApiKey"]="private" }).Build();
            var client = new AiModelClient(http, new ChatGptAuth(http, dir), config);
            var error = await Assert.ThrowsAnyAsync<DomainException>(() => client.CompleteAsync("OpenAI", "model", "prompt", default));
            Assert.DoesNotContain("sensitive", error.Message);
            Assert.Contains("429", error.Message);
        } finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }
    [Fact]
    public async Task DeltaOutputCannotGrowWithoutBoundAcrossSmallEvents()
    {
        var delta = System.Text.Json.JsonSerializer.Serialize(new { type = "response.output_text.delta", delta = new string('x', AiModelClient.MaxReplyCharacters / 2 + 1) });
        var complete = "{\"type\":\"response.completed\",\"response\":{\"status\":\"completed\"}}";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes($"data: {delta}\n\ndata: {delta}\n\ndata: {complete}\n\n"));
        await Assert.ThrowsAsync<DomainException>(() => AiModelClient.ReadResponsesStreamAsync(stream, default));
    }
    [Fact]
    public async Task OverallSubscriptionWireBytesAreBoundedEvenWhenNoOutputTextIsProduced()
    {
        var ignored = "data: " + System.Text.Json.JsonSerializer.Serialize(new { type = "response.reasoning.delta", delta = new string('x', 10000) }) + "\n\n";
        var complete = "data: {\"type\":\"response.completed\",\"response\":{\"status\":\"completed\",\"output\":[{\"content\":[{\"type\":\"output_text\",\"text\":\"{}\"}]}]}}\n\n";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat(ignored, 450)) + complete));
        await Assert.ThrowsAsync<DomainException>(() => AiModelClient.ReadResponsesStreamAsync(stream, default));
    }
    [Fact]
    public async Task ApiResponseBytesAreBoundedBeforeJsonParsingAndModelOutputAcceptance()
    {
        using var http = new HttpClient(new OversizedApiHandler());
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Ai:ApiKey"] = "fixture" }).Build();
            var client = new AiModelClient(http, new ChatGptAuth(http, dir), config);
            await Assert.ThrowsAsync<DomainException>(() => client.CompleteAsync("Api", "fixture", "prompt", default));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }
    sealed class OversizedApiHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(new { choices = new[] { new { finish_reason = "stop", message = new { content = "{}" } } }, padding = new string('x', 4_100_000) })) });
    }
    sealed class NoNetwork : HttpMessageHandler { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token) => throw new Exception("Unexpected network"); }
    [Theory]
    [InlineData("data: {\"type\":\"error\"}\n\n", true)]
    [InlineData("data: {\"type\":\"response.failed\",\"response\":{\"error\":{\"code\":\"server_error\"}}}\n\n", true)]
    [InlineData("data: {\"type\":\"response.output_text.delta\",\"delta\":\"partial\"}\n\n", true)]
    [InlineData("data: {\"type\":\"response.failed\",\"response\":{\"error\":{\"code\":\"subscription_sharing_usage_limit_exceeded\"}}}\n\n", false)]
    [InlineData("data: {\"type\":\"response.incomplete\",\"response\":{}}\n\n", false)]
    // OpenAI documents usage_unavailable and user_unavailable as 503s to retry with bounded backoff.
    [InlineData("data: {\"type\":\"response.failed\",\"response\":{\"error\":{\"code\":\"subscription_sharing_usage_unavailable\"}}}\n\n", true)]
    [InlineData("data: {\"type\":\"error\",\"code\":\"subscription_sharing_user_unavailable\",\"message\":\"try later\"}\n\n", true)]
    [InlineData("data: {\"type\":\"error\",\"code\":\"context_length_exceeded\"}\n\n", false)]
    [InlineData("data: not json\n\n", true)]
    [InlineData("data: {\"delta\":\"no type\"}\n\n", true)]
    public async Task StreamHiccupsAreRetryableButUsageLimitsAreNot(string events, bool transient)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(events));
        var error = await Assert.ThrowsAnyAsync<DomainException>(() => AiModelClient.ReadResponsesStreamAsync(stream, default));
        Assert.Equal(transient, error is TransientModelException);
    }
    [Fact]
    public async Task ServerErrorsAreRetriedAndThenSucceed()
    {
        var handler = new FlakyApiHandler(failures: 2);
        using var http = new HttpClient(handler);
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try {
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> { ["Ai:ApiKey"]="private" }).Build();
            var client = new AiModelClient(http, new ChatGptAuth(http, dir), config) { RetryDelays = [TimeSpan.Zero, TimeSpan.Zero] };
            var reply = await client.CompleteAsync("OpenAI", "model", "prompt", default);
            Assert.Equal("{}", reply.Text); Assert.Equal(3, handler.Calls);
            // A third consecutive failure is reported, and usage limits (429) are never retried.
            handler.Reset(failures: 3);
            await Assert.ThrowsAsync<TransientModelException>(() => client.CompleteAsync("OpenAI", "model", "prompt", default)); Assert.Equal(3, handler.Calls);
            using var limited = new HttpClient(new FailureHandler());
            var limitedClient = new AiModelClient(limited, new ChatGptAuth(limited, dir), config) { RetryDelays = [TimeSpan.Zero, TimeSpan.Zero] };
            var error = await Assert.ThrowsAnyAsync<DomainException>(() => limitedClient.CompleteAsync("OpenAI", "model", "prompt", default));
            Assert.IsNotType<TransientModelException>(error);
        } finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }
    sealed class FlakyApiHandler(int failures) : HttpMessageHandler
    {
        int remaining = failures; public int Calls;
        public void Reset(int failures) { remaining = failures; Calls = 0; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Calls++;
            if (remaining-- > 0) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("busy") });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"choices\":[{\"finish_reason\":\"stop\",\"message\":{\"content\":\"{}\"}}],\"usage\":{\"prompt_tokens\":1,\"completion_tokens\":1}}", Encoding.UTF8, "application/json") });
        }
    }
    [Fact]
    public async Task StreamErrorKeepsTheProviderCodeAndDetailWithoutTokens()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("data: {\"type\":\"error\",\"error\":{\"type\":\"invalid_request_error\",\"code\":\"context_length_exceeded\",\"message\":\"Too long for Bearer abc.def-123\",\"param\":\"input\"}}\n\n"));
        // Behaviour change: a too-long prompt is rejected outright; retrying the same body cannot help.
        var error = await Assert.ThrowsAsync<ProviderModelException>(() => AiModelClient.ReadResponsesStreamAsync(stream, default));
        Assert.Contains("(context_length_exceeded)", error.Message); Assert.Equal(ModelFailureKind.Rejected, error.Kind); Assert.Equal("input", error.Info.Param);
        Assert.Contains("param=input", error.Detail); Assert.Contains("Too long", error.Detail); Assert.DoesNotContain("abc.def-123", error.Detail);
        Assert.DoesNotContain("abc.def-123", error.Message);
    }
    sealed class FailureHandler : HttpMessageHandler { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent("sensitive upstream detail") }); }
}
