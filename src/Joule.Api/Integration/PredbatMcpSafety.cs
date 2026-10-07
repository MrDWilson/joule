using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Joule;

/// <summary>Redacts untrusted MCP evidence before it reaches prompts, storage or browser responses. A Home Assistant
/// "!secret name" reference is not a credential (it names an entry in secrets.yaml), so "mcp_secret: !secret predbat_mcp_secret"
/// survives.</summary>
public static class PredbatMcpSafety
{
    static readonly Regex Sensitive = new("(?:^key$|_key|api.?key|access.?key|private.?key|password|secret|token|authorization|credential|username|email|account_number|mpan|site_id|plant_id|hub_serial)", RegexOptions.IgnoreCase|RegexOptions.CultureInvariant,TimeSpan.FromMilliseconds(100));
    static readonly Regex CredentialText = new("""Bearer\s+\S+|https?://[^\s"'<>]*(?:@|\?)[^\s"'<>]*|(?<key>\b[\w.-]{0,128}(?:password|secret|token|api.?key|access.?key|private.?key|authorization|username|_key\b)[\w.-]{0,128}["']?\s*[:=](?!\s*["']?!secret\b)\s*)(?:"(?:\\.|[^"\\])*"|'(?:\\.|[^'\\])*'|[^\r\n,}]+)""",RegexOptions.IgnoreCase|RegexOptions.CultureInvariant,TimeSpan.FromMilliseconds(100));
    internal static bool SensitiveKey(string key) => Sensitive.IsMatch(key);
    internal static string[] Secrets(IConfiguration? config) => config?.AsEnumerable().Where(x=>!string.IsNullOrEmpty(x.Value)&&SensitiveKey(x.Key)).SelectMany(x=>new[] { x.Value!,x.Value!.Trim() }).Where(x=>x.Length>0).Distinct().ToArray() ?? [];
    internal static string CleanText(string value,string[] secrets)
    {
        foreach(var secret in secrets) { value=value.Replace(secret,"[redacted]",StringComparison.Ordinal); value=value.Replace(Uri.EscapeDataString(secret),"[redacted]",StringComparison.OrdinalIgnoreCase); }
        // A "key: value" credential keeps its key, so a redacted YAML or JSON line still reads as that setting.
        try { return CredentialText.Replace(value,m=>m.Groups["key"].Success?m.Groups["key"].Value+"[redacted]":"[redacted]"); } catch(RegexMatchTimeoutException) { return "[redacted: complex content]"; }
    }
    internal static JsonNode? Clean(JsonElement element,string[] secrets,int depth=0)
    {
        if(depth>24) return JsonValue.Create("[omitted: nesting limit]");
        if(element.ValueKind==JsonValueKind.Object) {
            var obj=new JsonObject(); foreach(var property in element.EnumerateObject()) {
                var key=CleanText(property.Name,secrets);
                obj[key]=SensitiveKey(property.Name)?JsonValue.Create("[redacted]"):Clean(property.Value,secrets,depth+1);
            } return obj;
        }
        if(element.ValueKind==JsonValueKind.Array) { var array=new JsonArray(); foreach(var item in element.EnumerateArray()) array.Add(Clean(item,secrets,depth+1)); return array; }
        if(element.ValueKind==JsonValueKind.String) {
            var value=element.GetString()!;
            if(depth<20 && (value.TrimStart().StartsWith('{')||value.TrimStart().StartsWith('['))) {
                try { using var embedded=JsonDocument.Parse(value,new JsonDocumentOptions { MaxDepth=24 }); return JsonValue.Create(Clean(embedded.RootElement,secrets,depth+1)?.ToJsonString()); } catch(JsonException) {}
            }
            return JsonValue.Create(CleanText(value,secrets));
        }
        return element.ValueKind==JsonValueKind.Undefined ? null : JsonNode.Parse(element.GetRawText());
    }
    internal static JsonNode? CleanSchema(JsonElement element,string[] secrets,int depth=0)
    {
        if(depth>24) return JsonValue.Create("[omitted: nesting limit]");
        if(element.ValueKind==JsonValueKind.Object) {
            var obj=new JsonObject(); foreach(var property in element.EnumerateObject()) {
                if(property.Name is "default" or "examples" or "example") continue;
                obj[CleanText(property.Name,secrets)]=CleanSchema(property.Value,secrets,depth+1);
            } return obj;
        }
        if(element.ValueKind==JsonValueKind.Array) { var array=new JsonArray(); foreach(var item in element.EnumerateArray()) array.Add(CleanSchema(item,secrets,depth+1)); return array; }
        return element.ValueKind==JsonValueKind.String ? JsonValue.Create(CleanText(element.GetString()!,secrets)) : JsonNode.Parse(element.GetRawText());
    }
    public static string FormatRequest(string name,JsonElement arguments,IConfiguration? configuration=null)
    {
        var secrets=Secrets(configuration);
        var cleanName=CleanText(name,secrets); if(cleanName.Length>100) cleanName=cleanName[..100];
        if(arguments.ValueKind==JsonValueKind.Undefined || arguments.GetRawText().Length>16384) return JsonSerializer.Serialize(new { name=cleanName, arguments="[omitted: argument limit]" });
        var cleanArgs=Clean(arguments,secrets);
        if(name=="get_apps_config" && cleanArgs is JsonObject obj && arguments.TryGetProperty("key",out var selector) && selector.ValueKind==JsonValueKind.String) obj["key"]=CleanText(selector.GetString()!,secrets);
        var output=new JsonObject { ["name"]=cleanName,["arguments"]=cleanArgs }.ToJsonString();
        return output.Length<=4096 ? output : JsonSerializer.Serialize(new { name=cleanName,arguments="[omitted: argument limit]" });
    }
}
