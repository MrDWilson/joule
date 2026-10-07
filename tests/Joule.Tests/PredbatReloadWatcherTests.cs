using System.Text.Json;
using Joule;
using Microsoft.Extensions.Configuration;
using Xunit;
namespace Joule.Tests;

/// <summary>Watching Predbat take a changed apps.yaml: it logs "Stopping Predbat" (stop_all, in predbat.log), restarts, and may log errors.</summary>
public class PredbatReloadWatcherTests
{
    /// <summary>Predbat's MCP, scripted: whether get_status answers on each call, and what get_log returns for each filter (errors before the
    /// edit, read with an end bound, come from <paramref name="earlierErrors"/>).</summary>
    sealed class ScriptedMcp(Func<int, bool> answers, string stopLog, string errorLog, string earlierErrors = "") : IPredbatMcpClient
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
            var end = arguments.TryGetProperty("end", out var e) ? e.GetString() : null;
            Logs.Add(filter + " from " + arguments.GetProperty("start").GetString() + (end is null ? "" : " to " + end));
            var lines = filter != "errors" ? stopLog : end is null ? errorLog : earlierErrors;
            return Task.FromResult(new McpReadResult(true, JsonSerializer.Serialize(new { lines }), false, null));
        }
    }
    sealed class NoHttp : IHttpClientFactory { public HttpClient CreateClient(string name) => new(); }

    static readonly ReloadContext Edit = ReloadContext.For(["pred_bat › export_today"], "  export_today:\n    - sensor.export_today");

    static PredbatReloadWatcher Watcher(IPredbatMcpClient? mcp, string? baseUrl = null) =>
        new(new NoHttp(), new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Predbat:BaseUrl"] = baseUrl }).Build(), () => TimeZoneInfo.Utc, mcp)
        {
            Poll = TimeSpan.FromMilliseconds(5), ReloadWindow = TimeSpan.FromMilliseconds(200), ComeBackWindow = TimeSpan.FromMilliseconds(200), Settle = TimeSpan.Zero,
        };
    // What predbat.log gets: stop_all() logs "Stopping Predbat"; the "...due to file changes" wording is printed to the console only.
    const string Stopped = "2026-10-07 14:00:05.123: Stopping Predbat";

    [Fact]
    public async Task AReloadWithoutErrorsIsConfirmed()
    {
        // Up before the edit, up once, then down while it restarts, then back.
        var mcp = new ScriptedMcp(call => call is 0 or 1 or >= 4, Stopped, "");
        var outcome = await Watcher(mcp).WatchAsync(new DateTimeOffset(2026, 10, 7, 14, 0, 0, TimeSpan.Zero), Edit, CancellationToken.None);
        Assert.Equal(new ReloadOutcome(true, true, "Predbat reloaded apps.yaml and logged no errors."), outcome);
        // Log reads are bounded to the edit (Predbat's host-local time): the stop line and errors after it, and errors in the hours before.
        Assert.Contains("all from 2026-10-07 14:00:00", mcp.Logs);
        Assert.Contains("errors from 2026-10-07 14:00:00", mcp.Logs);
        Assert.Contains("errors from 2026-10-07 08:00:00 to 2026-10-07 14:00:00", mcp.Logs);
    }

    [Fact]
    public async Task PredbatNotComingBackIsAFailure()
    {
        var mcp = new ScriptedMcp(call => call is 0 or 1, Stopped, "");
        var outcome = await Watcher(mcp).WatchAsync(DateTimeOffset.UtcNow, Edit, CancellationToken.None);
        Assert.False(outcome.Ok);
        Assert.StartsWith("Predbat stopped to reload apps.yaml and hadn't started again", outcome.Note);
    }

    [Fact]
    public async Task AConfigurationErrorAfterTheReloadIsAFailureQuotedWithoutItsTimestamp()
    {
        var mcp = new ScriptedMcp(_ => true, Stopped, "2026-10-07 14:00:20.001: Error: inverter timeout\n2026-10-07 14:00:21.001: Error: config item export_today points at sensor.missing which was not found");
        var outcome = await Watcher(mcp).WatchAsync(DateTimeOffset.UtcNow, Edit, CancellationToken.None);
        Assert.False(outcome.Ok);
        Assert.Equal("Predbat reported a problem after reloading: “Error: config item export_today points at sensor.missing which was not found”", outcome.Note);
    }

    [Fact]
    public async Task UnrelatedErrorsDoNotUndoTheEdit()
    {
        var mcp = new ScriptedMcp(_ => true, Stopped, "2026-10-07 14:00:21.001: Error: inverter timeout talking to GivTCP");
        var outcome = await Watcher(mcp).WatchAsync(DateTimeOffset.UtcNow, Edit, CancellationToken.None);
        Assert.True(outcome.Ok);
        Assert.Equal("Predbat reloaded apps.yaml. It logged 1 error since, none about this edit.", outcome.Note);
    }

    [Fact]
    public async Task AnErrorPredbatWasAlreadyLoggingDoesNotUndoTheEditButANewOneAboutTheEditDoes()
    {
        // Predbat logged this cloud error all morning; it mentions an exception, "not found" and "invalid", but nothing about the edit.
        const string recurring = "Error: Exception raised fetching Solcast forecast: 404 not found, invalid response";
        var before = $"2026-10-07 09:00:00.100: {recurring}\n2026-10-07 12:30:00.200: {recurring}";
        var after = $"2026-10-07 14:01:00.300: {recurring}";
        var outcome = await Watcher(new ScriptedMcp(_ => true, Stopped, after, before)).WatchAsync(DateTimeOffset.UtcNow, Edit, CancellationToken.None);
        Assert.True(outcome.Ok, outcome.Note);
        Assert.Equal("Predbat reloaded apps.yaml. It logged 1 error since, none about this edit.", outcome.Note);

        // The same line when Joule couldn't tell it was recurring (no earlier errors) still isn't about the edit.
        outcome = await Watcher(new ScriptedMcp(_ => true, Stopped, after)).WatchAsync(DateTimeOffset.UtcNow, Edit, CancellationToken.None);
        Assert.True(outcome.Ok, outcome.Note);

        // A new error naming the entity the edit wrote does put the file back, even among the recurring ones.
        var bad = after + "\n2026-10-07 14:01:05.000: Error: sensor.export_today is not a valid entity";
        outcome = await Watcher(new ScriptedMcp(_ => true, Stopped, bad, before)).WatchAsync(DateTimeOffset.UtcNow, Edit, CancellationToken.None);
        Assert.False(outcome.Ok);
        Assert.Equal("Predbat reported a problem after reloading: “Error: sensor.export_today is not a valid entity”", outcome.Note);

        // So does a new traceback once Joule knows what was there before, and anything mentioning apps.yaml.
        var traceback = after + "\n2026-10-07 14:01:06.000: Error: Traceback (most recent call last): KeyError 'soc_kw'";
        Assert.False((await Watcher(new ScriptedMcp(_ => true, Stopped, traceback, before)).WatchAsync(DateTimeOffset.UtcNow, Edit, CancellationToken.None)).Ok);
        var yaml = "2026-10-07 14:01:06.000: Error: Unable to read apps.yaml";
        Assert.False((await Watcher(new ScriptedMcp(_ => true, Stopped, yaml, before)).WatchAsync(DateTimeOffset.UtcNow, Edit, CancellationToken.None)).Ok);
    }

    [Fact]
    public void TheEditContextHoldsTheChangedSettingsAndTheEntitiesWrittenButNoSecretsOrNumbers()
    {
        var edit = ReloadContext.For(["pred_bat › export_today", "pred_bat › battery_rate_max_scaling", "pred_bat › octopus_api_key"],
            "  export_today:\n    - sensor.export_today  # added\n  battery_rate_max_scaling: 0.7\n  octopus_api_key: !secret octopus_api_key\n  timezone: Europe/London\n  ha_key: [redacted]");
        Assert.Equal(["export_today", "battery_rate_max_scaling", "octopus_api_key"], edit.Keys);
        Assert.Equal(["sensor.export_today", "Europe/London"], edit.Terms);
    }

    [Fact]
    public async Task NoSignOfAReloadIsReportedHonestlyButNotUndone()
    {
        var outcome = await Watcher(new ScriptedMcp(_ => true, "", "")).WatchAsync(DateTimeOffset.UtcNow, Edit, CancellationToken.None);
        Assert.Equal((true, false), (outcome.Ok, outcome.Reloaded));
        Assert.Contains("didn't see it reload", outcome.Note);
    }

    [Fact]
    public async Task WithoutPredbatOrWhenItWasAlreadyDownJouleCannotJudgeAndLeavesTheEdit()
    {
        var unconnected = await Watcher(null).WatchAsync(DateTimeOffset.UtcNow, Edit, CancellationToken.None);
        Assert.Equal((true, false), (unconnected.Ok, unconnected.Reloaded));
        Assert.Contains("isn't connected to Predbat", unconnected.Note);
        var down = await Watcher(new ScriptedMcp(_ => false, "", "")).WatchAsync(DateTimeOffset.UtcNow, Edit, CancellationToken.None);
        Assert.Equal((true, false), (down.Ok, down.Reloaded));
        Assert.Contains("wasn't answering when Joule made the edit", down.Note);
    }
}
