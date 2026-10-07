using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Joule;

/// <summary>How Predbat took a changed apps.yaml. Ok false means Joule should put the file back. Reloaded is true when Joule saw the
/// reload happen (in Predbat's log, or Predbat going away and coming back); Note says what happened, in words.</summary>
public sealed record ReloadOutcome(bool Ok, bool Reloaded, string Note);

public interface IPredbatReloadWatcher
{
    /// <summary>Watches Predbat after apps.yaml was written at <paramref name="writtenAt"/>, for at most a few minutes.</summary>
    Task<ReloadOutcome> WatchAsync(DateTimeOffset writtenAt, CancellationToken ct);
}

/// <summary>The demo has no Predbat to reload; it says so instead of pretending.</summary>
public sealed class DemoReloadWatcher : IPredbatReloadWatcher
{
    public Task<ReloadOutcome> WatchAsync(DateTimeOffset writtenAt, CancellationToken ct) =>
        Task.FromResult(new ReloadOutcome(true, true, "The demo has no real Predbat. On your own install Joule now watches Predbat reload apps.yaml and checks its log for errors."));
}

/// <summary>
/// Predbat watches apps.yaml itself: within a few seconds of a change it logs "Stopping Predbat due to file changes...." and stops, and
/// its add-on or container starts it again with the new file (Predbat's docs: "the change will automatically be detected and Predbat will
/// be reloaded"). A file it can't load stops it starting, and a bad entry shows as an error in its log. So Joule watches for that
/// reload (in the log through Predbat's MCP, or Predbat's web page going away and coming back), waits for Predbat to answer again, then
/// reads the errors logged since the edit. Predbat not coming back, or a configuration error after the reload, means the edit failed.
/// </summary>
public sealed class PredbatReloadWatcher(IHttpClientFactory http, IConfiguration configuration, Func<TimeZoneInfo> zone, IPredbatMcpClient? mcp = null) : IPredbatReloadWatcher
{
    public TimeSpan Poll { get; init; } = TimeSpan.FromSeconds(5);
    /// <summary>How long to wait for any sign that Predbat noticed the change.</summary>
    public TimeSpan ReloadWindow { get; init; } = TimeSpan.FromSeconds(90);
    /// <summary>How long Predbat may take to answer again once it has stopped.</summary>
    public TimeSpan ComeBackWindow { get; init; } = TimeSpan.FromMinutes(3);
    /// <summary>After Predbat answers again: time for it to load apps.yaml and log any problem with it.</summary>
    public TimeSpan Settle { get; init; } = TimeSpan.FromSeconds(20);

    const string StopLine = "due to file changes";
    static readonly Regex ConfigError = new(@"apps\.yaml|\byaml\b|config|traceback|exception|keyerror|valueerror|not found|invalid|failed to (load|parse)|could not (load|parse)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    bool UseMcp => mcp is { Configured: true };

    public async Task<ReloadOutcome> WatchAsync(DateTimeOffset writtenAt, CancellationToken ct)
    {
        if (!UseMcp && string.IsNullOrWhiteSpace(configuration["Predbat:BaseUrl"]))
            return new(true, false, "Joule isn't connected to Predbat, so it couldn't watch Predbat reload apps.yaml.");
        // Predbat takes a few seconds to notice a change, so this reads how it was before the edit.
        if (!await AnswersAsync(ct))
            return new(true, false, "Predbat wasn't answering when Joule made the edit, so Joule couldn't check it reloaded. Check Predbat once it's back.");
        var started = DateTimeOffset.UtcNow;
        bool sawStop = false, sawDown = false;
        // 1. Did Predbat notice the change?
        while (DateTimeOffset.UtcNow - started < ReloadWindow)
        {
            await Task.Delay(Poll, ct);
            if (!await AnswersAsync(ct)) { sawDown = true; break; }
            if (UseMcp && await StopLoggedAsync(writtenAt, ct)) { sawStop = true; break; }
        }
        if (!sawStop && !sawDown)
            return new(true, false, "Predbat kept running, but Joule didn't see it reload apps.yaml. Predbat normally restarts within seconds of a change; if the change doesn't take effect, restart Predbat.");
        // 2. Is it back?
        var back = false; var since = DateTimeOffset.UtcNow;
        while (DateTimeOffset.UtcNow - since < ComeBackWindow)
        {
            await Task.Delay(Poll, ct);
            if (await AnswersAsync(ct)) { back = true; break; }
        }
        if (!back) return new(false, true, $"Predbat stopped to reload apps.yaml and hadn't started again {Minutes(ComeBackWindow)} later.");
        // 3. Did it complain about the file?
        if (!UseMcp) return new(true, true, "Predbat restarted with the new apps.yaml and is answering again. Joule can't read Predbat's log without its MCP connection, so check it if a setting doesn't take effect.");
        await Task.Delay(Settle, ct);
        if (!await AnswersAsync(ct)) return new(false, true, "Predbat started again after the edit but stopped answering soon after.");
        var errors = await ErrorsSinceAsync(writtenAt, ct);
        if (errors is null) return new(true, true, "Predbat reloaded apps.yaml and is answering again. Joule couldn't read its log to check for errors.");
        var problem = errors.FirstOrDefault(l => ConfigError.IsMatch(l));
        if (problem is not null) return new(false, true, $"Predbat reported a problem after reloading: “{Shorten(problem)}”");
        return new(true, true, errors.Count == 0
            ? "Predbat reloaded apps.yaml and logged no errors."
            : $"Predbat reloaded apps.yaml. It logged {errors.Count} error{(errors.Count == 1 ? "" : "s")} since, none about its configuration.");
    }

    static string Minutes(TimeSpan t) => t.TotalMinutes >= 1 ? $"{t.TotalMinutes:0} minute{(t.TotalMinutes >= 2 ? "s" : "")}" : $"{t.TotalSeconds:0} seconds";
    string Shorten(string line)
    {
        var clean = PredbatMcpSafety.CleanText(Regex.Replace(line.Trim(), @"^\S*\d{4}-\d{2}-\d{2}[ T][\d:.,]+\S*\s*", ""), PredbatMcpSafety.Secrets(configuration));
        return clean.Length > 200 ? clean[..200] + "…" : clean;
    }
    string Local(DateTimeOffset t) => TimeZoneInfo.ConvertTime(t, zone()).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    /// <summary>Predbat answers: its MCP status when set up, otherwise its web page.</summary>
    async Task<bool> AnswersAsync(CancellationToken ct)
    {
        if (UseMcp)
        {
            try
            {
                using var args = JsonDocument.Parse("{}");
                if ((await mcp!.CallReadOnlyAsync("get_status", args.RootElement, ct)).Success) return true;
            }
            catch (Exception e) when (e is not OperationCanceledException) { }
        }
        var address = configuration["Predbat:BaseUrl"];
        if (string.IsNullOrWhiteSpace(address) || !Uri.TryCreate(address.TrimEnd('/') + "/", UriKind.Absolute, out var uri)) return false;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            using var response = await http.CreateClient("predbat-probe").GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            return (int)response.StatusCode < 500;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested) { return false; }
    }

    async Task<string?> LogAsync(object arguments, CancellationToken ct)
    {
        try
        {
            using var args = JsonDocument.Parse(JsonSerializer.Serialize(arguments));
            var result = await mcp!.CallReadOnlyAsync("get_log", args.RootElement, ct);
            return result.Success ? InvestigationContext.Text(result.ResultJson).Content : null;
        }
        catch (Exception e) when (e is not OperationCanceledException) { return null; }
    }

    async Task<bool> StopLoggedAsync(DateTimeOffset writtenAt, CancellationToken ct) =>
        (await LogAsync(new { filter = "all", search = StopLine, start = Local(writtenAt.AddSeconds(-5)), max_lines = 20 }, ct))?.Contains(StopLine, StringComparison.OrdinalIgnoreCase) == true;

    async Task<List<string>?> ErrorsSinceAsync(DateTimeOffset writtenAt, CancellationToken ct)
    {
        var text = await LogAsync(new { filter = "errors", start = Local(writtenAt), max_lines = 200 }, ct);
        if (text is null) return null;
        return text.Split('\n').Select(l => Regex.Replace(l.Trim(), @"^[a-z_]{1,24}:\s+(?=\S)", "", RegexOptions.None, TimeSpan.FromMilliseconds(100))).Where(l => l.Length > 8 && Regex.IsMatch(l, @"\berror\b|traceback|exception", RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100))).Take(200).ToList();
    }
}
