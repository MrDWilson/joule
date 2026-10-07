using System.Text.Json;
using Joule;
using Microsoft.Extensions.Configuration;
using Xunit;
namespace Joule.Tests;

/// <summary>Watching Predbat take a changed apps.yaml: it logs "Stopping Predbat due to file changes", restarts, and may log errors.</summary>
public class PredbatReloadWatcherTests
{
    /// <summary>Predbat's MCP, scripted: whether get_status answers on each call, and what get_log returns for each filter.</summary>
    sealed class ScriptedMcp(Func<int, bool> answers, string stopLog, string errorLog) : IPredbatMcpClient
    {
        int statusCalls;
        public List<string> Logs { get; } = [];
        public bool Configured => true;
        public McpDiscovery Status => new(true, true, DateTimeOffset.UtcNow, [], null);
        public Task<McpDiscovery> DiscoverAsync(CancellationToken ct = default) => Task.FromResult(Status);
        public Task<McpReadResult> CallReadOnlyAsync(string name, JsonElement arguments, CancellationToken ct = default)
        {
            if (name == "get_status")
                return Task.FromResult(answers(statusCalls++) ? new McpReadResult(true, "{}", false, null) : new McpReadResult(false, "", false, "unreachable"));
            var filter = arguments.GetProperty("filter").GetString()!;
            Logs.Add(filter + " from " + arguments.GetProperty("start").GetString());
            return Task.FromResult(new McpReadResult(true, JsonSerializer.Serialize(new { lines = filter == "errors" ? errorLog : stopLog }), false, null));
        }
    }
    sealed class NoHttp : IHttpClientFactory { public HttpClient CreateClient(string name) => new(); }

    static PredbatReloadWatcher Watcher(IPredbatMcpClient? mcp, string? baseUrl = null) =>
        new(new NoHttp(), new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Predbat:BaseUrl"] = baseUrl }).Build(), () => TimeZoneInfo.Utc, mcp)
        {
            Poll = TimeSpan.FromMilliseconds(5), ReloadWindow = TimeSpan.FromMilliseconds(200), ComeBackWindow = TimeSpan.FromMilliseconds(200), Settle = TimeSpan.Zero,
        };
    const string Stopped = "2026-10-07 14:00:05.123: Info: Stopping Predbat due to file changes....";

    [Fact]
    public async Task AReloadWithoutErrorsIsConfirmed()
    {
        // Up before the edit, up once, then down while it restarts, then back.
        var mcp = new ScriptedMcp(call => call is 0 or 1 or >= 4, Stopped, "");
        var outcome = await Watcher(mcp).WatchAsync(new DateTimeOffset(2026, 10, 7, 14, 0, 0, TimeSpan.Zero), CancellationToken.None);
        Assert.Equal(new ReloadOutcome(true, true, "Predbat reloaded apps.yaml and logged no errors."), outcome);
        // Log reads are bounded to the edit (Predbat's host-local time).
        Assert.Contains("errors from 2026-10-07 14:00:00", mcp.Logs);
    }

    [Fact]
    public async Task PredbatNotComingBackIsAFailure()
    {
        var mcp = new ScriptedMcp(call => call is 0 or 1, Stopped, "");
        var outcome = await Watcher(mcp).WatchAsync(DateTimeOffset.UtcNow, CancellationToken.None);
        Assert.False(outcome.Ok);
        Assert.StartsWith("Predbat stopped to reload apps.yaml and hadn't started again", outcome.Note);
    }

    [Fact]
    public async Task AConfigurationErrorAfterTheReloadIsAFailureQuotedWithoutItsTimestamp()
    {
        var mcp = new ScriptedMcp(_ => true, Stopped, "2026-10-07 14:00:20.001: Error: config item export_today points at sensor.missing which was not found\n2026-10-07 14:00:21.001: Error: inverter timeout");
        var outcome = await Watcher(mcp).WatchAsync(DateTimeOffset.UtcNow, CancellationToken.None);
        Assert.False(outcome.Ok);
        Assert.Equal("Predbat reported a problem after reloading: “Error: config item export_today points at sensor.missing which was not found”", outcome.Note);
    }

    [Fact]
    public async Task UnrelatedErrorsDoNotUndoTheEdit()
    {
        var mcp = new ScriptedMcp(_ => true, Stopped, "2026-10-07 14:00:21.001: Error: inverter timeout talking to GivTCP");
        var outcome = await Watcher(mcp).WatchAsync(DateTimeOffset.UtcNow, CancellationToken.None);
        Assert.True(outcome.Ok);
        Assert.Equal("Predbat reloaded apps.yaml. It logged 1 error since, none about its configuration.", outcome.Note);
    }

    [Fact]
    public async Task NoSignOfAReloadIsReportedHonestlyButNotUndone()
    {
        var outcome = await Watcher(new ScriptedMcp(_ => true, "", "")).WatchAsync(DateTimeOffset.UtcNow, CancellationToken.None);
        Assert.Equal((true, false), (outcome.Ok, outcome.Reloaded));
        Assert.Contains("didn't see it reload", outcome.Note);
    }

    [Fact]
    public async Task WithoutPredbatOrWhenItWasAlreadyDownJouleCannotJudgeAndLeavesTheEdit()
    {
        var unconnected = await Watcher(null).WatchAsync(DateTimeOffset.UtcNow, CancellationToken.None);
        Assert.Equal((true, false), (unconnected.Ok, unconnected.Reloaded));
        Assert.Contains("isn't connected to Predbat", unconnected.Note);
        var down = await Watcher(new ScriptedMcp(_ => false, "", "")).WatchAsync(DateTimeOffset.UtcNow, CancellationToken.None);
        Assert.Equal((true, false), (down.Ok, down.Reloaded));
        Assert.Contains("wasn't answering when Joule made the edit", down.Note);
    }
}
