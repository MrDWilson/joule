using System.Text.Json;

namespace Joule;

public record McpToolDefinition(string Name, string Description, JsonElement InputSchema);
public record McpDiscovery(bool Configured, bool Connected, DateTimeOffset? CheckedAt, List<McpToolDefinition> Tools, string? Error);
public record McpReadResult(bool Success, string ResultJson, bool Truncated, string? Error);

public interface IPredbatMcpClient
{
    bool Configured { get; }
    McpDiscovery Status { get; }
    Task<McpDiscovery> DiscoverAsync(CancellationToken ct = default);
    Task<McpReadResult> CallReadOnlyAsync(string name, JsonElement arguments, CancellationToken ct = default);
}
