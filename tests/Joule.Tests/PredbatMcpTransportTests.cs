using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Joule;
using Xunit;

namespace Joule.Tests;

public class PredbatMcpTransportTests
{
    const string Token = "fixture-mcp-credential";
    static JsonElement Args(string json = "{}") => JsonDocument.Parse(json).RootElement.Clone();
    static IConfiguration Config(string? token = Token, string? url = null) => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> {
        ["Predbat:BaseUrl"] = "http://predbat.test:5052/base", ["Predbat:McpToken"] = token, ["Predbat:McpUrl"] = url
    }).Build();
    static readonly object[] Catalog = [ Tool("get_status"), Tool("get_apps", "{\"type\":\"object\",\"properties\":{\"masked\":{\"type\":\"boolean\"}}}"), Tool("set_config"), Tool("unknown_reader") ];
    static object Tool(string name, string schema = "{\"type\":\"object\",\"properties\":{}}") => new { name, description = "Inspect " + Token, inputSchema = Args(schema), annotations = new { readOnlyHint = true } };
    static HttpResponseMessage Json(object result, JsonElement id) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { jsonrpc="2.0", id, result }), Encoding.UTF8, "application/json") };
    sealed class Server : HttpMessageHandler
    {
        public List<(string Method, Uri Uri, JsonElement Body)> Requests { get; } = [];
        public Func<HttpRequestMessage, JsonElement, HttpResponseMessage>? Reply { get; set; }
        public Func<HttpRequestMessage, JsonElement, Task<HttpResponseMessage?>>? AsyncReply { get; set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? Args() : Args(await request.Content.ReadAsStringAsync(ct));
            Requests.Add((request.Method.Method, request.RequestUri!, body));
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme); Assert.Equal(Token,request.Headers.Authorization?.Parameter);
            if (AsyncReply is not null && await AsyncReply(request,body) is { } asyncReply) return asyncReply;
            if (Reply?.Invoke(request,body) is { } reply) return reply;
            if(request.Method==HttpMethod.Delete) return new(HttpStatusCode.NoContent);
            var id = body.TryGetProperty("id", out var value) ? value : Args("null");
            return body.GetProperty("method").GetString() switch {
                "initialize" => Json(new { protocolVersion="2024-11-05", capabilities=new { tools=new {} }, serverInfo=new { name="Predbat",version="1.0.1" } },id),
                "notifications/initialized" => Json(new {},id),
                "tools/list" => Json(new { tools=Catalog },id),
                "tools/call" => Json(new { content=new[] { new { type="text", text="{\"status\":\"ok\",\"api_key\":\"secret-value\"}" } } },id),
                _ => throw new InvalidOperationException()
            };
        }
    }
    [Fact] public async Task DiscoveryNegotiatesLegacyPredbatAndFiltersEvenMisleadingReadHints()
    {
        var server=new Server(); using var client=new PredbatMcpClient(new HttpClient(server), Config());
        Assert.False(client.Status.Connected); Assert.Empty(server.Requests);
        var status=await client.DiscoverAsync();
        Assert.True(status.Connected,status.Error); Assert.Equal(new[]{"get_status","get_apps"},status.Tools.Select(x=>x.Name));
        Assert.DoesNotContain(Token,JsonSerializer.Serialize(status));
        Assert.All(server.Requests,r=>Assert.Equal("http://predbat.test:8199/mcp",r.Uri.AbsoluteUri));
        Assert.Equal(new[]{"initialize","notifications/initialized","tools/list"},server.Requests.Select(x=>x.Body.GetProperty("method").GetString()));
        status.Tools.Clear(); Assert.Equal(2,client.Status.Tools.Count);
    }
    [Theory] [InlineData("set_config","{}")] [InlineData("unknown_reader","{}")] [InlineData("get_apps","{\"masked\":false}")] [InlineData("get_status","[]")] [InlineData("get_status","{\"token\":\"leak\"}")]
    public async Task UnsafeCallsAreRejectedBeforeAnyNetwork(string name,string arguments)
    {
        var server=new Server(); using var client=new PredbatMcpClient(new HttpClient(server),Config());
        Assert.False((await client.CallReadOnlyAsync(name,Args(arguments))).Success); Assert.Empty(server.Requests);
    }
    [Fact] public async Task MaskedAppsAreForcedAndNestedTextCredentialsAreSanitized()
    {
        var server=new Server(); using var client=new PredbatMcpClient(new HttpClient(server),Config());
        var result=await client.CallReadOnlyAsync("get_apps",Args());
        Assert.True(result.Success,result.Error); Assert.DoesNotContain("secret-value",result.ResultJson);
        Assert.True(server.Requests.Last().Body.GetProperty("params").GetProperty("arguments").GetProperty("masked").GetBoolean());
        using var json=JsonDocument.Parse(result.ResultJson); Assert.False(json.RootElement.GetProperty("truncated").GetBoolean());
    }
    [Fact] public async Task MissingTokenAndSecretUrlDoNotSendHttp()
    {
        var server=new Server(); using var disabled=new PredbatMcpClient(new HttpClient(server),Config(null));
        Assert.False(disabled.Configured); Assert.False((await disabled.DiscoverAsync()).Connected);
        using var invalid=new PredbatMcpClient(new HttpClient(server),Config(url:"https://user:password@host/mcp?token=leak"));
        Assert.False((await invalid.DiscoverAsync()).Connected); Assert.DoesNotContain("password",JsonSerializer.Serialize(invalid.Status)); Assert.Empty(server.Requests);
    }
    [Fact] public async Task RedirectAndRemoteErrorsDoNotExposeBodiesOrCredentials()
    {
        var server=new Server { Reply=(_,_)=>new(HttpStatusCode.TemporaryRedirect) { Headers={Location=new Uri("https://evil.test/?token="+Token)},Content=new StringContent(Token) } };
        using var client=new PredbatMcpClient(new HttpClient(server),Config());
        var status=await client.DiscoverAsync(); Assert.False(status.Connected); Assert.DoesNotContain(Token,JsonSerializer.Serialize(status)); Assert.Single(server.Requests);
    }
    [Fact] public async Task RefusedMcpListenerReportsEnableAndPortGuidance()
    {
        // Close a real listener, reproducing the deployed connection refusal without external networking.
        var listener=new TcpListener(IPAddress.Loopback,0); listener.Start();
        var port=((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        using var client=new PredbatMcpClient(new HttpClient(new SocketsHttpHandler {UseProxy=false}),Config(url:$"http://127.0.0.1:{port}/mcp"));
        var status=await client.DiscoverAsync();
        Assert.True(status.Configured); Assert.False(status.Connected); Assert.Empty(status.Tools);
        Assert.Contains("refused",status.Error); Assert.Contains("mcp_enable",status.Error); Assert.Contains("8199",status.Error);
        Assert.DoesNotContain(Token,JsonSerializer.Serialize(status));
    }
    [Theory]
    [InlineData(HttpRequestError.NameResolutionError,"hostname")]
    [InlineData(HttpRequestError.ConnectionError,"reachable")]
    public async Task NetworkFailuresHaveSafeActionableDiagnosticsForDiscoveryAndReads(HttpRequestError cause,string guidance)
    {
        var server=new Server { Reply=(_,_)=>throw new HttpRequestException(cause,"Bearer "+Token+" remote-private-error") };
        using var client=new PredbatMcpClient(new HttpClient(server),Config());
        var status=await client.DiscoverAsync(); var read=await client.CallReadOnlyAsync("get_status",Args());
        Assert.False(status.Connected); Assert.False(read.Success);
        Assert.Contains(guidance,status.Error); Assert.Equal(status.Error,read.Error);
        var serialized=JsonSerializer.Serialize(new {status,read});
        Assert.DoesNotContain(Token,serialized); Assert.DoesNotContain("remote-private-error",serialized);
    }
    [Fact] public async Task CallerCancellationPropagatesAndDoesNotPoisonCachedStatus()
    {
        var server=new Server(); using var client=new PredbatMcpClient(new HttpClient(server),Config());
        using var ct=new CancellationTokenSource(); ct.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>client.DiscoverAsync(ct.Token)); Assert.Empty(server.Requests);
        Assert.True((await client.DiscoverAsync()).Connected);
    }
    [Fact] public async Task DiscoveryPaginatesAndPreservesConfigKeySchema()
    {
        var pages=0;
        var server=new Server { Reply=(_,body)=>body.TryGetProperty("method",out var m)&&m.GetString()=="tools/list" ?
            ++pages==1 ? Json(new { tools=new[]{Tool("get_apps_config","{\"type\":\"object\",\"properties\":{\"key\":{\"type\":\"string\"}},\"required\":[\"key\"]}")},nextCursor="next" },body.GetProperty("id")) : Json(new { tools=new[]{Tool("get_status")} },body.GetProperty("id")) : null! };
        using var client=new PredbatMcpClient(new HttpClient(server),Config());
        var status=await client.DiscoverAsync(); Assert.True(status.Connected,status.Error); Assert.Equal(2,status.Tools.Count);
        Assert.Equal("string",status.Tools[0].InputSchema.GetProperty("properties").GetProperty("key").GetProperty("type").GetString());
        Assert.Equal("next",server.Requests.Last().Body.GetProperty("params").GetProperty("cursor").GetString());
    }
    [Fact] public async Task StreamableSessionExpiresReconnectsAndIsDeletedOnDispose()
    {
        var initializes=0; var expired=false;
        var server=new Server { Reply=(request,body)=> {
            if(request.Method==HttpMethod.Delete) return new(HttpStatusCode.NoContent);
            var method=body.GetProperty("method").GetString();
            if(method=="initialize") {
                Assert.False(request.Headers.Contains("MCP-Session-Id"));
                var reply=Json(new { protocolVersion="2025-03-26",capabilities=new { tools=new {} } },body.GetProperty("id")); reply.Headers.Add("MCP-Session-Id","session-"+ ++initializes); return reply;
            }
            Assert.True(request.Headers.Contains("MCP-Session-Id"));
            Assert.Equal("2025-03-26",request.Headers.GetValues("MCP-Protocol-Version").Single());
            if(method=="tools/call"&&!expired) { expired=true; return new(HttpStatusCode.NotFound); }
            return null!;
        } };
        var client=new PredbatMcpClient(new HttpClient(server),Config());
        var result=await client.CallReadOnlyAsync("get_status",Args()); Assert.True(result.Success,result.Error); Assert.Equal(2,initializes);
        await client.DisposeAsync(); Assert.Equal("DELETE",server.Requests.Last().Method);
    }
    [Fact] public async Task SseNotificationsBeforeMatchingResponseAreRead()
    {
        var server=new Server { Reply=(_,body)=>body.TryGetProperty("method",out var m)&&m.GetString()=="tools/call" ?
            new(HttpStatusCode.OK) { Content=new StringContent("data: {\"jsonrpc\":\"2.0\",\"method\":\"notifications/progress\"}\n\ndata: "+JsonSerializer.Serialize(new { jsonrpc="2.0",id=body.GetProperty("id"),result=new { content=new[]{new {type="text",text="SSE evidence"}} } })+"\n\n",Encoding.UTF8,"text/event-stream") } : null! };
        using var client=new PredbatMcpClient(new HttpClient(server),Config());
        var result=await client.CallReadOnlyAsync("get_status",Args()); Assert.True(result.Success,result.Error); Assert.Contains("SSE evidence",result.ResultJson);
    }
    [Fact] public async Task EmbeddedFailureWithoutIsErrorIsHonestAndSanitized()
    {
        var server=new Server { Reply=(_,body)=>body.TryGetProperty("method",out var m)&&m.GetString()=="tools/call" ? Json(new {content=new[]{new {type="text",text="{\"success\":false,\"error\":\"Bearer "+Token+"\"}"}}},body.GetProperty("id")):null! };
        using var client=new PredbatMcpClient(new HttpClient(server),Config());
        var result=await client.CallReadOnlyAsync("get_status",Args()); Assert.False(result.Success); Assert.DoesNotContain(Token,result.ResultJson);
    }
    [Fact] public async Task SafeRequestRemovesSecretsFromRejectedArguments()
    {
        var request=PredbatMcpSafety.FormatRequest("get_apps",Args("{\"password\":\"private\",\"filter\":\"Bearer "+Token+"\",\"nested\":{\"api_key\":\"other-private\"}}"),Config());
        Assert.DoesNotContain("private",request); Assert.DoesNotContain(Token,request);
        using var parsed=JsonDocument.Parse(request);
    }
    [Fact] public async Task CancellationWaitingForAnotherDiscoveryCannotCloseItsSession()
    {
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var server=new Server { AsyncReply=async (_,body)=> {
            if(body.TryGetProperty("method",out var method)&&method.GetString()=="tools/list") { entered.TrySetResult(); await release.Task; }
            return null;
        }, Reply=(_,body)=>body.TryGetProperty("method",out var m)&&m.GetString()=="initialize" ? WithSession(Json(new {protocolVersion="2025-03-26",capabilities=new {tools=new {}}},body.GetProperty("id"))) : null! };
        using var client=new PredbatMcpClient(new HttpClient(server),Config());
        var first=client.DiscoverAsync(); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var ct=new CancellationTokenSource(); var second=client.DiscoverAsync(ct.Token); ct.Cancel();
        try { await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>second); Assert.DoesNotContain(server.Requests,r=>r.Method=="DELETE"); }
        finally { release.TrySetResult(); await first; }
        Assert.True((await first).Connected);
    }
    static HttpResponseMessage WithSession(HttpResponseMessage response) { response.Headers.Add("MCP-Session-Id","session-live"); return response; }
    [Fact] public async Task CamelCaseConfiguredKeysCannotLeakThroughUnstructuredResults()
    {
        var config=new ConfigurationBuilder().AddConfiguration(Config()).AddInMemoryCollection(new Dictionary<string,string?> { ["Ai:ApiKey"]="camel-private-key",["App:AccessKey"]="app-private-key" }).Build();
        var server=new Server { Reply=(_,body)=>body.TryGetProperty("method",out var m)&&m.GetString()=="tools/call" ? Json(new {content=new[]{new {type="text",text="camel-private-key app-private-key"}}},body.GetProperty("id")) : null! };
        using var client=new PredbatMcpClient(new HttpClient(server),config);
        var result=await client.CallReadOnlyAsync("get_status",Args()); Assert.True(result.Success); Assert.DoesNotContain("private-key",result.ResultJson);
    }
    [Fact] public async Task LongPlainTextIsRetainedWithoutRedactionTimeout()
    {
        var server=new Server { Reply=(_,body)=>body.TryGetProperty("method",out var m)&&m.GetString()=="tools/call" ? Json(new {content=new[]{new {type="text",text=new string('x',80000)}}},body.GetProperty("id")) : null! };
        using var client=new PredbatMcpClient(new HttpClient(server),Config());
        var result=await client.CallReadOnlyAsync("get_status",Args()); Assert.False(result.Truncated); Assert.DoesNotContain("complex content",result.ResultJson);
        using var parsed=JsonDocument.Parse(result.ResultJson);Assert.Equal(80000,parsed.RootElement.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!.Length);
    }
    [Fact] public async Task DisposalCannotWaitIndefinitelyForAnActiveRead()
    {
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var server=new Server { AsyncReply=async (_,body)=> { if(body.TryGetProperty("method",out var m)&&m.GetString()=="tools/call") { entered.TrySetResult(); await release.Task; } return null; } };
        var client=new PredbatMcpClient(new HttpClient(server),Config()); var active=client.CallReadOnlyAsync("get_status",Args());
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try { await client.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3)); }
        finally { release.TrySetResult(); await active; }
    }
    [Fact] public async Task LiteralKeyCredentialsAreRedactedWhileConfigSelectorRemainsUseful()
    {
        var server=new Server { Reply=(_,body)=>body.TryGetProperty("method",out var m)&&m.GetString()=="tools/call" ? Json(new {content=new[]{new {type="text",text="{\"key\":\"private-credential\"}"}}},body.GetProperty("id")):null! };
        using var client=new PredbatMcpClient(new HttpClient(server),Config());
        var result=await client.CallReadOnlyAsync("get_status",Args()); Assert.DoesNotContain("private-credential",result.ResultJson);
        Assert.Contains("forecast_solar",PredbatMcpSafety.FormatRequest("get_apps_config",Args("{\"key\":\"forecast_solar\"}"),Config()));
    }
    [Theory] [InlineData("{\"error\":\"read unavailable\"}",false,false)] [InlineData("{\"success\":true,\"data\":{\"truncated\":true,\"lines\":[\"partial\"]}}",true,true)] [InlineData("{\"success\":true,\"data\":{\"omitted\":{\"large_series\":{\"length\":10000}}}}",true,true)]
    public async Task ApplicationErrorAndRemotePartialMarkersAreHonest(string text,bool success,bool truncated)
    {
        var server=new Server { Reply=(_,body)=>body.TryGetProperty("method",out var m)&&m.GetString()=="tools/call" ? Json(new {content=new[]{new {type="text",text}}},body.GetProperty("id")) : null! };
        using var client=new PredbatMcpClient(new HttpClient(server),Config()); var result=await client.CallReadOnlyAsync("get_status",Args());
        Assert.Equal(success,result.Success); Assert.Equal(truncated,result.Truncated);
        using var parsed=JsonDocument.Parse(result.ResultJson); Assert.Equal(truncated,parsed.RootElement.GetProperty("truncated").GetBoolean());
    }
    [Fact] public async Task EffectiveTrimmedCredentialCannotLeakThroughResponsesSchemasOrRequests()
    {
        var server=new Server { Reply=(_,body)=>body.TryGetProperty("method",out var m)&&m.GetString()=="tools/call" ? Json(new {content=new[]{new {type="text",text=Token}}},body.GetProperty("id")) : null! };
        using var client=new PredbatMcpClient(new HttpClient(server),Config(" "+Token+" "));
        var result=await client.CallReadOnlyAsync("get_status",Args()); Assert.DoesNotContain(Token,result.ResultJson);
        Assert.DoesNotContain(Token,JsonSerializer.Serialize(client.Status));
        Assert.DoesNotContain(Token,PredbatMcpSafety.FormatRequest("get_log",Args("{\"search\":\""+Token+"\"}"),Config(" "+Token+" ")));
        var count=server.Requests.Count; Assert.False((await client.CallReadOnlyAsync("get_log",Args("{\"search\":\""+Token+"\"}"))).Success); Assert.Equal(count,server.Requests.Count);
    }
    [Theory] [InlineData("2026-10-02 WARN {\"api_key\":\"remote-private-value\"}")] [InlineData("INFO {'password': 'remote-private-value'}")] [InlineData("debug {\"accessKey\": \"remote-private-value\"}")] [InlineData("WARN {\"password\":\"part1,remote-private-value}\"}")] [InlineData("INFO {'api_key': 'part1}remote-private-value'}")]
    public async Task QuotedCredentialAssignmentsInPrefixedLogsAreRedacted(string text)
    {
        var server=new Server { Reply=(_,body)=>body.TryGetProperty("method",out var m)&&m.GetString()=="tools/call" ? Json(new {content=new[]{new {type="text",text}}},body.GetProperty("id")) : null! };
        using var client=new PredbatMcpClient(new HttpClient(server),Config()); var result=await client.CallReadOnlyAsync("get_status",Args());
        Assert.DoesNotContain("remote-private-value",result.ResultJson);
    }
    [Fact] public async Task TimedOutDiscoveryWaiterReportsFailureWithoutChangingTheActiveSession()
    {
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var server=new Server { AsyncReply=async (_,body)=> { if(body.TryGetProperty("method",out var m)&&m.GetString()=="tools/call") { entered.TrySetResult(); await release.Task; } return null; } };
        var config=new ConfigurationBuilder().AddConfiguration(Config()).AddInMemoryCollection(new Dictionary<string,string?> { ["Predbat:McpTimeoutSeconds"]="1" }).Build();
        using var client=new PredbatMcpClient(new HttpClient(server),config); Assert.True((await client.DiscoverAsync()).Connected);
        var active=client.CallReadOnlyAsync("get_status",Args()); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try { var waited=await client.DiscoverAsync(); Assert.False(waited.Connected); Assert.Contains("timed out",waited.Error); Assert.True(client.Status.Connected); }
        finally { release.TrySetResult(); await active; }
    }
    [Fact] public async Task OversizedResultsAreBoundedValidJsonWithExplicitTruncation()
    {
        var server=new Server { Reply=(_,body)=> body.TryGetProperty("method",out var m)&&m.GetString()=="tools/call" ? Json(new { content=new[]{new {type="text",text=new string('x',800000)}} },body.GetProperty("id")): null! };
        using var client=new PredbatMcpClient(new HttpClient(server),Config());
        var result=await client.CallReadOnlyAsync("get_status",Args()); Assert.True(result.Truncated); Assert.True(result.ResultJson.Length<=512*1024);
        using var parsed=JsonDocument.Parse(result.ResultJson); Assert.True(parsed.RootElement.GetProperty("truncated").GetBoolean());
    }
    [Fact] public async Task LargeSanitizedEvidencePreservesLateControlEventsForArchivedPaging()
    {
        var payload=JsonSerializer.Serialize(new { lines=new string('x',100000), operation_mode="self_consumption", charge_freeze_service="backup", api_key="private-fixture-key" });
        var server=new Server { Reply=(_,body)=>body.TryGetProperty("method",out var m)&&m.GetString()=="tools/call" ? Json(new { content=new[]{new {type="text",text=payload}} },body.GetProperty("id")):null! };
        using var client=new PredbatMcpClient(new HttpClient(server),Config());
        var result=await client.CallReadOnlyAsync("get_status",Args());
        Assert.True(result.Success,result.Error);Assert.False(result.Truncated);
        Assert.Contains("self_consumption",result.ResultJson);Assert.Contains("backup",result.ResultJson);Assert.DoesNotContain("private-fixture-key",result.ResultJson);
        using var parsed=JsonDocument.Parse(result.ResultJson);
        using var content=JsonDocument.Parse(parsed.RootElement.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!);
        Assert.Equal(100000,content.RootElement.GetProperty("lines").GetString()!.Length);
    }
    [Fact] public async Task FirstZeroBasedLogLineCanBeReadBackInFull()
    {
        var server=new Server { Reply=(_,body)=>body.TryGetProperty("method",out var m)&&m.GetString()=="tools/list" ? Json(new {tools=new[]{Tool("get_log")}},body.GetProperty("id")):null! };
        using var client=new PredbatMcpClient(new HttpClient(server),Config());
        var result=await client.CallReadOnlyAsync("get_log",Args("{\"line_number\":0,\"context\":0}"));
        Assert.True(result.Success,result.Error);Assert.Contains(server.Requests,r=>r.Body.TryGetProperty("method",out var m)&&m.GetString()=="tools/call");
    }
    [Fact] public async Task PerLineLogClippingIsReportedEvenWhenWholeResponseClaimsComplete()
    {
        var server=new Server { Reply=(_,body)=>body.TryGetProperty("method",out var m)&&m.GetString()=="tools/call" ? Json(new { content=new[]{new {type="text",text="{\"success\":true,\"data\":{\"truncated\":false,\"lines\":[{\"line_number\":0,\"line\":\"partial\",\"truncated_chars\":1200}]}}"}} },body.GetProperty("id")):null! };
        using var client=new PredbatMcpClient(new HttpClient(server),Config());
        var result=await client.CallReadOnlyAsync("get_status",Args());
        Assert.True(result.Success,result.Error);Assert.True(result.Truncated);Assert.Contains("truncated_chars",result.ResultJson);
    }
    [Theory]
    [InlineData(true,false)] [InlineData(false,false)]
    [InlineData(true,true)] [InlineData(false,true)]
    public async Task FailedResponseBodiesReturnSafeFailuresAndRecover(bool discovery,bool invalidUtf8)
    {
        var fail=true;
        var server=new Server { Reply=(_,body)=> {
            if(!body.TryGetProperty("method",out var method))return null!;
            if(method.GetString()=="initialize")return WithSession(Json(new {protocolVersion="2025-03-26",capabilities=new {tools=new {}}},body.GetProperty("id")));
            if(method.GetString()!=(discovery?"tools/list":"tools/call")||!fail)return null!;
            fail=false;
            var content=new StreamContent(invalidUtf8?new MemoryStream([0x64,0x61,0x74,0x61,0x3a,0x20,0xc3,0x28]):new InterruptedBody());
            content.Headers.ContentType=new(invalidUtf8?"text/event-stream":"application/json");
            return new(HttpStatusCode.OK){Content=content};
        } };
        using var client=new PredbatMcpClient(new HttpClient(server),Config());
        if(discovery)
        {
            var result=await client.DiscoverAsync();Assert.False(result.Connected);Assert.NotNull(result.Error);
        }
        else
        {
            var result=await client.CallReadOnlyAsync("get_status",Args());Assert.False(result.Success);Assert.NotNull(result.Error);
            Assert.DoesNotContain("remote-private-error",result.ResultJson);Assert.DoesNotContain(Token,result.ResultJson);
        }
        Assert.False(client.Status.Connected);Assert.Empty(client.Status.Tools);
        Assert.DoesNotContain("remote-private-error",JsonSerializer.Serialize(client.Status));Assert.DoesNotContain(Token,JsonSerializer.Serialize(client.Status));
        Assert.True((await client.CallReadOnlyAsync("get_status",Args())).Success);Assert.True(client.Status.Connected);
    }
    sealed class InterruptedBody:MemoryStream
    {
        public InterruptedBody():base(Encoding.UTF8.GetBytes("{\"jsonrpc\":\"2.0\",")){}
        public override ValueTask<int> ReadAsync(Memory<byte> buffer,CancellationToken ct=default)
            =>Position<Length?base.ReadAsync(buffer,ct):ValueTask.FromException<int>(new IOException("Bearer "+Token+" remote-private-error"));
    }
    [Fact]
    public void SecretReferencesAreNotRedactedButCredentialsAre()
    {
        string Clean(string text) => PredbatMcpSafety.CleanText(text, []);
        Assert.Equal("mcp_secret: !secret predbat_mcp_secret", Clean("mcp_secret: !secret predbat_mcp_secret"));
        Assert.Equal("  ha_key:   !secret ha_token", Clean("  ha_key:   !secret ha_token"));
        Assert.DoesNotContain("hunter2", Clean("mcp_secret: hunter2-not-a-reference"));
        Assert.DoesNotContain("secretive", Clean("api_key=!secretive-value"));
    }

}
