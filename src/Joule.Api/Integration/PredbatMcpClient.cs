using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Joule;

public static class PredbatMcpRegistration
{
    public static IServiceCollection AddPredbatMcp(this IServiceCollection services,IConfiguration configuration)
        => services.AddSingleton<IPredbatMcpClient>(_=>new PredbatMcpClient(configuration));
}

/// <summary>
/// Bounded read-only MCP over Predbat's legacy JSON HTTP and Streamable HTTP JSON/SSE.
/// Sign-in: Predbat's MCP server (web_mcp.py) is an OAuth 2.1 authorization server. Joule exchanges the configured mcp_secret
/// for a signed access token (client_credentials at /oauth/token, scope mcp:read, resource = this server) and sends that token,
/// so Predbat checks a signature instead of trying to decode the raw secret as a token, which it logs as a failure on every
/// request ("MCP: Token … failed: Not enough segments"). Only a Predbat without the token endpoint (404/405, an unsupported
/// grant or a server error) is sent the secret itself, as before; Joule asks for a token again every few hours.
/// </summary>
public sealed class PredbatMcpClient : IPredbatMcpClient, IAsyncDisposable, IDisposable
{
    // Retain bounded sanitized evidence for archival/paging. Prompt pages have a
    // separate smaller budget; discarding the tail here makes it unrecoverable.
    const int WireLimit=1024*1024, OutputLimit=512*1024;
    static readonly HashSet<string> ReadTools=new(StringComparer.Ordinal) { "get_status","get_plan","get_config","get_apps","get_apps_config","get_log","get_state","get_entities","search_entities","get_entity_state","get_entity_history" };
    static readonly Dictionary<string,string[]> Arguments=new(StringComparer.Ordinal) {
        ["get_status"]=[],["get_plan"]=[],["get_config"]=["filter"],["get_apps"]=["filter","masked"],["get_apps_config"]=["key"],
        ["get_log"]=["filter","search","pattern","hours","start","end","line_number","context","max_lines"],
        ["get_state"]=["keys","filter","max_bytes"],["get_entities"]=["filter"],["search_entities"]=["pattern","limit"],
        ["get_entity_state"]=["entity_id","attributes"],["get_entity_history"]=["entity_id","start","end","bucket_minutes","attribute"]
    };
    readonly HttpClient http;
    readonly Uri? endpoint;
    readonly string? token;
    readonly string[] secrets;
    readonly TimeSpan timeout;
    readonly SemaphoreSlim gate=new(1,1);
    readonly string? configurationError;
    readonly Uri? tokenEndpoint;
    readonly TimeProvider clock;
    /// <summary>How long a Predbat without the token endpoint is sent the secret directly before Joule asks for a token again.</summary>
    internal static readonly TimeSpan LegacyRecheck=TimeSpan.FromHours(6);
    const string ClientId="joule";
    string? accessToken;
    DateTimeOffset accessTokenRefresh;
    DateTimeOffset legacyUntil=DateTimeOffset.MinValue;
    bool defaultAudience;
    string? signIn;
    McpDiscovery status;
    string? session;
    string? protocol;
    long requestId;
    bool initialized;
    int disposeStarted;
    public PredbatMcpClient(IConfiguration configuration) : this(new HttpClient(new SocketsHttpHandler { AllowAutoRedirect=false,MaxResponseHeadersLength=16,ConnectTimeout=TimeSpan.FromSeconds(5) }) { Timeout=Timeout.InfiniteTimeSpan },configuration) {}
    internal PredbatMcpClient(HttpClient http,IConfiguration configuration,TimeProvider? clock=null)
    {
        this.http=http; this.clock=clock??TimeProvider.System; token=configuration["Predbat:McpToken"]?.Trim(); secrets=PredbatMcpSafety.Secrets(configuration);
        timeout=TimeSpan.FromSeconds(Math.Clamp(configuration.GetValue("Predbat:McpTimeoutSeconds",20),1,60));
        if(!string.IsNullOrWhiteSpace(token)) {
            if(token.Any(c=>c<'!'||c>'~')) configurationError="MCP credential configuration is invalid.";
            else {
                var url=configuration["Predbat:McpUrl"];
                if(string.IsNullOrWhiteSpace(url) && Uri.TryCreate(configuration["Predbat:BaseUrl"],UriKind.Absolute,out var baseUri))
                    url=new UriBuilder(baseUri) { Port=8199,Path="/mcp",Query="",Fragment="",UserName="",Password="" }.Uri.AbsoluteUri;
                if(Uri.TryCreate(url,UriKind.Absolute,out var parsed)&&parsed.Scheme is "http" or "https"&&string.IsNullOrEmpty(parsed.UserInfo)&&string.IsNullOrEmpty(parsed.Query)&&string.IsNullOrEmpty(parsed.Fragment)) { endpoint=parsed; tokenEndpoint=TokenEndpoint(parsed); }
                else configurationError="MCP endpoint configuration is invalid; use HTTP(S) without credentials, query or fragment.";
            }
        }
        status=new(Configured,false,null,[],configurationError);
    }
    /// <summary>Predbat serves /oauth/token next to /mcp: http://predbat:8199/mcp → http://predbat:8199/oauth/token (a proxy prefix is kept).</summary>
    internal static Uri TokenEndpoint(Uri mcp)
    {
        var path=mcp.AbsolutePath.TrimEnd('/'); var parent=path[..(path.LastIndexOf('/')+1)];
        return new UriBuilder(mcp) { Path=(parent.StartsWith('/')?parent:"/"+parent)+"oauth/token",Query="",Fragment="" }.Uri;
    }
    public bool Configured => !string.IsNullOrWhiteSpace(token);
    public McpDiscovery Status { get { var s=Volatile.Read(ref status); return s with { Tools=s.Tools.Select(t=>t with { InputSchema=t.InputSchema.Clone() }).ToList() }; } }
    public async Task<McpDiscovery> DiscoverAsync(CancellationToken ct=default)
    {
        ct.ThrowIfCancellationRequested(); using var deadline=CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(timeout);
        var entered=false;
        try {
            await gate.WaitAsync(deadline.Token); entered=true; ThrowIfDisposed();
            if(!Configured||endpoint is null) { SetStatus(false,[],configurationError??"MCP is not configured."); return Status; }
            try { await DiscoverCore(deadline.Token); }
            catch(SessionExpiredException) { await ResetSession(deadline.Token); await DiscoverCore(deadline.Token); }
            return Status;
        } catch(OperationCanceledException) when(!ct.IsCancellationRequested) { if(entered) { SetStatus(false,[],"MCP discovery timed out."); await CleanupAfterFailure(); return Status; } return new(Configured,false,DateTimeOffset.UtcNow,[],"MCP discovery timed out while waiting for another operation."); }
        catch(OperationCanceledException) { if(entered) await CleanupAfterFailure(); throw; }
        catch(Exception e) when(e is HttpRequestException or IOException or DecoderFallbackException or JsonException or McpProtocolException or InvalidOperationException) { SetStatus(false,[],SafeError(e)); await CleanupAfterFailure(); return Status; }
        finally { if(entered) gate.Release(); }
    }
    async Task DiscoverCore(CancellationToken ct)
    {
        await Initialize(ct);
        var catalogSize=0; var tools=new List<McpToolDefinition>(); var names=new HashSet<string>(StringComparer.Ordinal); var cursors=new HashSet<string>(); string? cursor=null;
        for(var page=0;page<8;page++) {
            var result=await Rpc("tools/list",cursor is null?new {}:new { cursor },ct);
            if(!result.TryGetProperty("tools",out var list)||list.ValueKind!=JsonValueKind.Array) throw new McpProtocolException("MCP discovery returned an invalid tool list.");
            foreach(var item in list.EnumerateArray()) {
                if(tools.Count>=64) throw new McpProtocolException("MCP tool catalog exceeds the supported limit.");
                if(!item.TryGetProperty("name",out var nameValue)||nameValue.ValueKind!=JsonValueKind.String) continue;
                var name=nameValue.GetString()!; if(!ReadTools.Contains(name)) continue;
                if(!names.Add(name)) throw new McpProtocolException("MCP discovery returned duplicate tools.");
                if(!item.TryGetProperty("inputSchema",out var schema)||schema.ValueKind!=JsonValueKind.Object||schema.GetRawText().Length>16384) throw new McpProtocolException("MCP tool schema is invalid or too large.");
                var clean=PredbatMcpSafety.CleanSchema(schema,secrets)!;
                if(name=="get_apps" && clean is JsonObject obj && obj["properties"] is JsonObject props) props["masked"]=new JsonObject { ["type"]="boolean",["const"]=true,["default"]=true,["description"]="Always true; unmasked configuration is unavailable." };
                var description=item.TryGetProperty("description",out var desc)&&desc.ValueKind==JsonValueKind.String?PredbatMcpSafety.CleanText(desc.GetString()!,secrets):name;
                if(description.Length>2048) description=description[..2048]+" [truncated]";
                catalogSize+=name.Length+description.Length+clean.ToJsonString().Length;
                if(catalogSize>40000) throw new McpProtocolException("MCP tool catalog exceeds the evidence size limit.");
                tools.Add(new(name,description,JsonSerializer.SerializeToElement(clean)));
            }
            cursor=result.TryGetProperty("nextCursor",out var next)&&next.ValueKind==JsonValueKind.String?next.GetString():null;
            if(string.IsNullOrEmpty(cursor)) { SetStatus(true,tools,null); return; }
            if(cursor.Length>2048||!cursors.Add(cursor)) throw new McpProtocolException("MCP discovery pagination is invalid.");
        }
        throw new McpProtocolException("MCP discovery exceeds the page limit.");
    }
    public async Task<McpReadResult> CallReadOnlyAsync(string name,JsonElement arguments,CancellationToken ct=default)
    {
        ct.ThrowIfCancellationRequested();
        if(!ValidateArguments(name,arguments,out var error)) return Failure(error!);
        if(!Configured||endpoint is null) return Failure(configurationError??"MCP is not configured.");
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(timeout); var entered=false;
        try {
            await gate.WaitAsync(deadline.Token); entered=true; ThrowIfDisposed();
            for(var attempt=0;attempt<2;attempt++) {
                try {
                    if(!initialized||!status.Connected) await DiscoverCore(deadline.Token);
                    var tool=status.Tools.Find(t=>t.Name==name); if(tool is null) return Failure("Read tool is unavailable in the discovered MCP catalog.");
                    var safeArgs=JsonNode.Parse(arguments.GetRawText())!.AsObject(); if(name=="get_apps") safeArgs["masked"]=true;
                    if(!SchemaValid(tool.InputSchema,JsonSerializer.SerializeToElement(safeArgs))) return Failure("Arguments do not satisfy the discovered read-tool schema.");
                    var result=await Rpc("tools/call",new { name,arguments=safeArgs },deadline.Token);
                    return BoundedResult(result);
                } catch(SessionExpiredException) when(attempt==0) { await ResetSession(deadline.Token); }
            }
            return Failure("MCP session expired.");
        } catch(OperationCanceledException) when(!ct.IsCancellationRequested) { if(entered) { SetStatus(false,[],"MCP read timed out."); await CleanupAfterFailure(); } return Failure("MCP read timed out."); }
        catch(OperationCanceledException) { if(entered) await CleanupAfterFailure(); throw; }
        catch(Exception e) when(e is HttpRequestException or IOException or DecoderFallbackException or JsonException or McpProtocolException or InvalidOperationException) { var message=SafeError(e); SetStatus(false,[],message); await CleanupAfterFailure(); return Failure(message,e is ResponseLimitException); }
        finally { if(entered) gate.Release(); }
    }
    async Task Initialize(CancellationToken ct)
    {
        if(initialized) return;
        var result=await Rpc("initialize",new { protocolVersion="2025-03-26",capabilities=new {},clientInfo=new { name="Joule",version="1.0" } },ct);
        protocol=result.TryGetProperty("protocolVersion",out var version)?version.GetString():null;
        if(protocol is not ("2024-11-05" or "2025-03-26" or "2025-06-18" or "2025-11-25")) throw new McpProtocolException("MCP server negotiated an unsupported protocol version.");
        if(!result.TryGetProperty("capabilities",out var caps)||!caps.TryGetProperty("tools",out _)) throw new McpProtocolException("MCP server does not advertise tools.");
        await SendNotification("notifications/initialized",ct); initialized=true;
    }
    HttpRequestMessage Request(HttpMethod method,object? body=null)
    {
        var request=new HttpRequestMessage(method,endpoint);
        request.Headers.Accept.Add(new("application/json")); request.Headers.Accept.Add(new("text/event-stream"));
        if(session is not null) request.Headers.Add("MCP-Session-Id",session);
        if(protocol is not null) request.Headers.Add("MCP-Protocol-Version",protocol);
        if(body is not null) request.Content=new StringContent(JsonSerializer.Serialize(body),Encoding.UTF8,"application/json");
        return request;
    }
    async Task<JsonElement> Rpc(string method,object parameters,CancellationToken ct)
    {
        var id=Interlocked.Increment(ref requestId);
        using var response=await Send(()=>Request(HttpMethod.Post,new { jsonrpc="2.0",id,method,@params=parameters }),ct);
        CheckHttp(response);
        if(method=="initialize"&&response.Headers.TryGetValues("MCP-Session-Id",out var sessions)) {
            var candidate=sessions.SingleOrDefault(); if(candidate is null||candidate.Length>1024||candidate.Any(c=>c<'!'||c>'~')) throw new McpProtocolException("MCP session header is invalid."); session=candidate;
        }
        if(response.Content.Headers.ContentLength>WireLimit) throw new ResponseLimitException();
        await using var raw=await response.Content.ReadAsStreamAsync(ct); await using var limited=new LimitedStream(raw,WireLimit);
        if(response.Content.Headers.ContentType?.MediaType=="text/event-stream") {
            using var reader=new StreamReader(limited,new UTF8Encoding(false,true)); var data=new StringBuilder();
            while(await reader.ReadLineAsync(ct) is { } line) {
                if(line.Length==0) { if(data.Length>0) { using var doc=JsonDocument.Parse(data.ToString()); data.Clear(); if(Matching(doc.RootElement,id,out var result)) return result; } }
                else if(line.StartsWith("data:",StringComparison.Ordinal)) { if(data.Length>0) data.Append('\n'); data.Append(line[5..].TrimStart(' ')); }
            }
            if(data.Length>0) { using var doc=JsonDocument.Parse(data.ToString()); if(Matching(doc.RootElement,id,out var result)) return result; }
            throw new McpProtocolException("MCP stream ended without the requested response.");
        }
        using(var document=await JsonDocument.ParseAsync(limited,new JsonDocumentOptions { MaxDepth=32 },ct)) {
            if(Matching(document.RootElement,id,out var result)) return result;
            throw new McpProtocolException("MCP returned a mismatched response.");
        }
    }
    static bool Matching(JsonElement message,long id,out JsonElement result)
    {
        result=default;
        if(message.ValueKind!=JsonValueKind.Object||!message.TryGetProperty("jsonrpc",out var version)||version.GetString()!="2.0") throw new McpProtocolException("MCP returned an invalid JSON-RPC response.");
        if(!message.TryGetProperty("id",out var responseId)) { if(message.TryGetProperty("method",out _)) return false; throw new McpProtocolException("MCP response has no request id."); }
        if(responseId.ValueKind!=JsonValueKind.Number||!responseId.TryGetInt64(out var number)||number!=id) return false;
        if(message.TryGetProperty("error",out _)) throw new McpProtocolException("MCP server returned a protocol error.");
        if(!message.TryGetProperty("result",out result)) throw new McpProtocolException("MCP response has no result."); result=result.Clone(); return true;
    }
    async Task SendNotification(string method,CancellationToken ct)
    {
        using var response=await Send(()=>Request(HttpMethod.Post,new { jsonrpc="2.0",method }),ct); CheckHttp(response);
        // Predbat 9.3.3 replies 200 with id:null to initialized; compliant servers reply 202.
    }
    /// <summary>
    /// Sends an MCP request with Joule's credential. A token Predbat refuses (401) is replaced once: the second token names no
    /// resource, so it carries Predbat's default audience, which Predbat accepts whatever Host header a proxy passes on.
    /// </summary>
    async Task<HttpResponseMessage> Send(Func<HttpRequestMessage> build,CancellationToken ct)
    {
        for(var attempt=0;;attempt++) {
            var (credential,oauth)=await Credential(ct);
            var request=build(); request.Headers.Authorization=new AuthenticationHeaderValue("Bearer",credential);
            var response=await http.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,ct);
            if(response.StatusCode!=HttpStatusCode.Unauthorized||!oauth||attempt>0) return response;
            response.Dispose(); request.Dispose(); accessToken=null; defaultAudience=true;
        }
    }
    /// <summary>The bearer value for the next request: a current access token, a new one from Predbat, or the secret itself when this Predbat has no token endpoint.</summary>
    async Task<(string Value,bool OAuth)> Credential(CancellationToken ct)
    {
        var now=clock.GetUtcNow();
        if(accessToken is not null&&now<accessTokenRefresh) return (accessToken,true);
        accessToken=null;
        if(now<legacyUntil||tokenEndpoint is null) { signIn="secret"; return (token!,false); }
        var form=new List<KeyValuePair<string,string>> { new("grant_type","client_credentials"),new("client_id",ClientId),new("client_secret",token!),new("scope","mcp:read") };
        // RFC 8707: the token is for this MCP server. Predbat compares it with http://{Host} of each request.
        if(!defaultAudience) form.Add(new("resource","http://"+endpoint!.Authority));
        using var request=new HttpRequestMessage(HttpMethod.Post,tokenEndpoint) { Content=new FormUrlEncodedContent(form) };
        request.Headers.Accept.Add(new("application/json"));
        using var response=await http.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,ct);
        if(response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new McpProtocolException("Predbat rejected the MCP secret. Check that Predbat__McpToken matches mcp_secret in Predbat's apps.yaml.");
        if(response.IsSuccessStatusCode&&await ReadToken(response,ct) is var (value,lifetime)) {
            accessToken=value; signIn="token";
            // Renew early: a tenth of the lifetime (at most an hour) before it ends.
            accessTokenRefresh=now+lifetime-TimeSpan.FromTicks(Math.Min(lifetime.Ticks/10,TimeSpan.FromHours(1).Ticks));
            return (value,true);
        }
        // No token endpoint (an older Predbat), an unsupported grant, or Predbat can't sign tokens: send the secret as before.
        legacyUntil=now+LegacyRecheck; signIn="secret";
        return (token!,false);
    }
    static async Task<(string Value,TimeSpan Lifetime)?> ReadToken(HttpResponseMessage response,CancellationToken ct)
    {
        if(response.Content.Headers.ContentLength>64*1024) return null;
        try {
            await using var raw=await response.Content.ReadAsStreamAsync(ct); await using var limited=new LimitedStream(raw,64*1024);
            using var json=await JsonDocument.ParseAsync(limited,new JsonDocumentOptions { MaxDepth=8 },ct);
            var root=json.RootElement;
            if(root.ValueKind!=JsonValueKind.Object||!root.TryGetProperty("access_token",out var tokenValue)||tokenValue.ValueKind!=JsonValueKind.String) return null;
            var value=tokenValue.GetString()!;
            // A JWT: three base64url parts. Anything else would have Predbat log a failed decode again.
            if(value.Length is < 16 or > 8192||value.Split('.').Length!=3||value.Any(c=>!(char.IsAsciiLetterOrDigit(c)||c is '-' or '_' or '.'))) return null;
            if(root.TryGetProperty("token_type",out var type)&&!string.Equals(type.ValueKind==JsonValueKind.String?type.GetString():null,"Bearer",StringComparison.OrdinalIgnoreCase)) return null;
            var seconds=root.TryGetProperty("expires_in",out var expires)&&expires.ValueKind==JsonValueKind.Number&&expires.TryGetDouble(out var s)&&double.IsFinite(s)?s:3600;
            return (value,TimeSpan.FromSeconds(Math.Clamp(seconds,60,30*86400)));
        } catch(Exception e) when(e is JsonException or DecoderFallbackException or ResponseLimitException) { return null; }
    }
    void CheckHttp(HttpResponseMessage response)
    {
        if(response.StatusCode==HttpStatusCode.NotFound&&session is not null) throw new SessionExpiredException();
        if(response.StatusCode==HttpStatusCode.Unauthorized) throw new McpProtocolException(signIn=="token"
            ? "Predbat refused Joule's MCP access token. Check that Predbat__McpToken matches mcp_secret in Predbat's apps.yaml."
            : "Predbat rejected the MCP secret. Check that Predbat__McpToken matches mcp_secret in Predbat's apps.yaml.");
        if(!response.IsSuccessStatusCode) throw new McpProtocolException($"MCP HTTP request failed ({(int)response.StatusCode}).");
    }
    bool ValidateArguments(string name,JsonElement args,out string? error)
    {
        error="MCP call denied by the local read-only policy.";
        if(!ReadTools.Contains(name)||args.ValueKind!=JsonValueKind.Object||args.GetRawText().Length>4096) return false;
        var argumentNames=new HashSet<string>(StringComparer.Ordinal);
        foreach(var p in args.EnumerateObject()) {
            if(!argumentNames.Add(p.Name)) return false;
            if(!Arguments[name].Contains(p.Name,StringComparer.Ordinal)) return false;
            if(p.Name=="masked" && p.Value.ValueKind!=JsonValueKind.True) return false;
            if(p.Name=="attributes" && p.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return false;
            if(p.Name=="keys") { if(p.Value.ValueKind!=JsonValueKind.Array||p.Value.GetArrayLength()>50||p.Value.EnumerateArray().Any(v=>v.ValueKind!=JsonValueKind.String||UnsafeString(v.GetString()!))) return false; continue; }
            if(p.Name is "hours" or "line_number" or "context" or "max_lines" or "max_bytes" or "limit" or "bucket_minutes") {
                var max=p.Name switch { "hours"=>720,"context"=>50,"max_lines"=>5000,"max_bytes"=>262144,"limit"=>200,"bucket_minutes"=>43200,_=>int.MaxValue };
                if(p.Value.ValueKind!=JsonValueKind.Number||!p.Value.TryGetDouble(out var n)||!double.IsFinite(n)||n<(p.Name is "context" or "line_number"?0:1)||n>max||p.Name!="hours"&&Math.Truncate(n)!=n) return false; continue;
            }
            if(p.Name is "masked" or "attributes") continue;
            if(p.Value.ValueKind!=JsonValueKind.String||UnsafeString(p.Value.GetString()!)||p.Value.GetString()!.Length>512) return false;
            if(name=="get_log"&&p.Name=="filter"&&p.Value.GetString() is not ("all" or "info" or "warnings" or "errors")) return false;
        }
        foreach(var required in name switch { "get_apps_config"=>new[]{"key"},"search_entities"=>new[]{"pattern"},"get_entity_state"=>new[]{"entity_id"},"get_entity_history"=>new[]{"entity_id","start","end"},_=>Array.Empty<string>() }) if(!args.TryGetProperty(required,out var v)||string.IsNullOrWhiteSpace(v.GetString())) return false;
        if(name=="get_entity_history") { if(!DateTimeOffset.TryParse(args.GetProperty("start").GetString(),out var start)||!DateTimeOffset.TryParse(args.GetProperty("end").GetString(),out var end)||end<=start||end-start>TimeSpan.FromDays(30)) return false; var bucket=args.TryGetProperty("bucket_minutes",out var b)?b.GetDouble():30; if((end-start).TotalMinutes/bucket>500) return false; }
        error=null; return true;
    }
    bool UnsafeString(string value) => value.Any(c=>c=='\0'||c=='\r'||c=='\n') || !string.Equals(value,PredbatMcpSafety.CleanText(value,secrets),StringComparison.Ordinal);
    static bool SchemaValid(JsonElement schema,JsonElement args)
    {
        if(schema.TryGetProperty("required",out var required)&&required.ValueKind==JsonValueKind.Array) foreach(var item in required.EnumerateArray()) if(item.ValueKind!=JsonValueKind.String||!args.TryGetProperty(item.GetString()!,out _)) return false;
        if(!schema.TryGetProperty("properties",out var props)||props.ValueKind!=JsonValueKind.Object) return true;
        foreach(var arg in args.EnumerateObject()) if(props.TryGetProperty(arg.Name,out var spec)&&spec.ValueKind==JsonValueKind.Object) {
            if(spec.TryGetProperty("type",out var type)&&type.ValueKind==JsonValueKind.String) {
                var valid=type.GetString() switch { "string"=>arg.Value.ValueKind==JsonValueKind.String,"boolean"=>arg.Value.ValueKind is JsonValueKind.True or JsonValueKind.False,"integer"=>arg.Value.ValueKind==JsonValueKind.Number&&arg.Value.TryGetInt64(out _),"number"=>arg.Value.ValueKind==JsonValueKind.Number,"array"=>arg.Value.ValueKind==JsonValueKind.Array,"object"=>arg.Value.ValueKind==JsonValueKind.Object,_=>false }; if(!valid) return false;
            }
            if(spec.TryGetProperty("enum",out var choices)&&choices.ValueKind==JsonValueKind.Array&&!choices.EnumerateArray().Any(v=>JsonElement.DeepEquals(v,arg.Value))) return false;
        }
        return true;
    }
    McpReadResult BoundedResult(JsonElement result)
    {
        var clean=PredbatMcpSafety.Clean(result,secrets); var failed=ApplicationFailed(result); var truncated=Partial(result);
        if(result.TryGetProperty("content",out var content)&&content.ValueKind==JsonValueKind.Array) foreach(var item in content.EnumerateArray()) if(item.TryGetProperty("text",out var text)&&text.ValueKind==JsonValueKind.String) {
            try { using var embedded=JsonDocument.Parse(text.GetString()!); failed|=ApplicationFailed(embedded.RootElement); truncated|=Partial(embedded.RootElement); } catch(JsonException) {}
        }
        truncated|=clean?.ToJsonString().Contains("[omitted: nesting limit]",StringComparison.Ordinal)==true || clean?.ToJsonString().Contains("[redacted: complex content]",StringComparison.Ordinal)==true;
        var output=new JsonObject { ["truncated"]=truncated,["result"]=clean }.ToJsonString();
        if(output.Length<=OutputLimit) return new(!failed,output,truncated,failed?"MCP read tool reported a failure.":null);
        // Preserve valid JSON rather than cutting arbitrary source text mid-value.
        var preview=clean?.ToJsonString()??"null"; var originalCharacters=preview.Length; if(preview.Length>32000) preview=preview[..32000];
        return new(!failed,JsonSerializer.Serialize(new { truncated=true,reason="MCP result exceeds the archived evidence limit. Omitted content was not retained; narrow the remote read.",originalCharacters,preview }),true,failed?"MCP read tool reported a failure.":null);
    }
    static bool ApplicationFailed(JsonElement value)
    {
        if(value.ValueKind!=JsonValueKind.Object) return false;
        return value.TryGetProperty("isError",out var flag)&&flag.ValueKind==JsonValueKind.True
            || value.TryGetProperty("success",out var success)&&success.ValueKind==JsonValueKind.False
            || value.TryGetProperty("error",out var error)&&HasContent(error);
    }
    static bool HasContent(JsonElement value) => value.ValueKind switch {
        JsonValueKind.Null or JsonValueKind.Undefined or JsonValueKind.False => false,
        JsonValueKind.String => !string.IsNullOrWhiteSpace(value.GetString()),
        JsonValueKind.Object => value.EnumerateObject().Any(), JsonValueKind.Array => value.GetArrayLength()>0, _ => true
    };
    static bool Partial(JsonElement value,int depth=0)
    {
        if(depth>24) return true;
        if(value.ValueKind==JsonValueKind.Array) return value.EnumerateArray().Any(v=>Partial(v,depth+1));
        if(value.ValueKind!=JsonValueKind.Object) return false;
        foreach(var property in value.EnumerateObject()) {
            if(property.Name=="truncated"&&property.Value.ValueKind==JsonValueKind.True || property.Name=="truncated_chars"&&property.Value.ValueKind==JsonValueKind.Number&&property.Value.TryGetDouble(out var dropped)&&dropped>0 || property.Name=="omitted"&&HasContent(property.Value) || Partial(property.Value,depth+1)) return true;
        }
        return false;
    }
    static McpReadResult Failure(string error,bool truncated=false) => new(false,JsonSerializer.Serialize(new { truncated,error }),truncated,error);
    void SetStatus(bool connected,List<McpToolDefinition> tools,string? error) => Volatile.Write(ref status,new(Configured,connected,DateTimeOffset.UtcNow,tools,error,connected?signIn:null));
    static string SafeError(Exception error) => error switch {
        McpProtocolException => error.Message,
        HttpRequestException { InnerException: SocketException { SocketErrorCode: SocketError.ConnectionRefused } } =>
            "MCP connection was refused. Check that Predbat has mcp_enable: true and mcp_secret configured, and that its MCP port (default 8199) is reachable from this app.",
        HttpRequestException { HttpRequestError: HttpRequestError.NameResolutionError } =>
            "MCP hostname could not be resolved. Check Predbat:McpUrl or Predbat:BaseUrl and the shared Docker network.",
        HttpRequestException { HttpRequestError: HttpRequestError.ConnectionError } =>
            "MCP endpoint could not be reached. Check that Predbat's MCP service is enabled and its MCP port is reachable from this app.",
        _ => "MCP connection failed or returned an invalid response."
    };
    async Task ResetSession(CancellationToken ct) { await CloseSession(ct); initialized=false; protocol=null; }
    async Task CleanupAfterFailure() { using var cleanup=new CancellationTokenSource(TimeSpan.FromSeconds(1)); await ResetSession(cleanup.Token); }
    async Task CloseSession(CancellationToken ct) { var previous=session; session=null; if(previous is null) return; try { using var request=Request(HttpMethod.Delete); request.Headers.Add("MCP-Session-Id",previous); request.Headers.Authorization=new AuthenticationHeaderValue("Bearer",accessToken??token); using var response=await http.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,ct); } catch(Exception e) when(e is HttpRequestException or OperationCanceledException or ObjectDisposedException) {} }
    void ThrowIfDisposed() { if(Volatile.Read(ref disposeStarted)!=0) throw new InvalidOperationException("MCP client is disposed."); }
    public async ValueTask DisposeAsync()
    {
        if(Interlocked.Exchange(ref disposeStarted,1)!=0) return;
        using var ct=new CancellationTokenSource(TimeSpan.FromSeconds(1)); var entered=false;
        try { await gate.WaitAsync(ct.Token); entered=true; await CloseSession(ct.Token); }
        catch(OperationCanceledException) {}
        finally { http.Dispose(); if(entered) gate.Release(); }
    }
    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();
    class McpProtocolException(string message) : Exception(message);
    sealed class SessionExpiredException() : McpProtocolException("MCP session expired.");
    sealed class ResponseLimitException() : McpProtocolException("MCP response exceeds the transport byte limit.");
    sealed class LimitedStream(Stream inner,int limit) : Stream
    {
        int read;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer,CancellationToken ct=default) { var count=await inner.ReadAsync(buffer[..Math.Min(buffer.Length,Math.Max(1,limit-read+1))],ct); read+=count; if(read>limit) throw new ResponseLimitException(); return count; }
        public override Task<int> ReadAsync(byte[] buffer,int offset,int count,CancellationToken ct) => ReadAsync(buffer.AsMemory(offset,count),ct).AsTask();
        public override int Read(byte[] buffer,int offset,int count) { var n=inner.Read(buffer,offset,Math.Min(count,Math.Max(1,limit-read+1))); read+=n; if(read>limit) throw new ResponseLimitException(); return n; }
        public override bool CanRead=>true; public override bool CanSeek=>false; public override bool CanWrite=>false; public override long Length=>throw new NotSupportedException(); public override long Position { get=>throw new NotSupportedException(); set=>throw new NotSupportedException(); }
        public override void Flush()=>throw new NotSupportedException(); public override long Seek(long offset,SeekOrigin origin)=>throw new NotSupportedException(); public override void SetLength(long value)=>throw new NotSupportedException(); public override void Write(byte[] buffer,int offset,int count)=>throw new NotSupportedException();
    }
}
