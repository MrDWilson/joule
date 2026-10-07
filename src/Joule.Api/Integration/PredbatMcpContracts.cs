using System.Text.Json;

namespace Joule;

public record McpToolDefinition(string Name, string Description, JsonElement InputSchema);
/// <param name="SignIn">How Joule signed in to Predbat's MCP when connected: "token" (an OAuth access token Predbat issued for its
/// mcp_secret) or "secret" (an older Predbat without the token endpoint, sent the secret directly). Null when not connected.</param>
public record McpDiscovery(bool Configured, bool Connected, DateTimeOffset? CheckedAt, List<McpToolDefinition> Tools, string? Error, string? SignIn = null);
public record McpReadResult(bool Success, string ResultJson, bool Truncated, string? Error);

public interface IPredbatMcpClient
{
    bool Configured { get; }
    McpDiscovery Status { get; }
    Task<McpDiscovery> DiscoverAsync(CancellationToken ct = default);
    Task<McpReadResult> CallReadOnlyAsync(string name, JsonElement arguments, CancellationToken ct = default);
}
