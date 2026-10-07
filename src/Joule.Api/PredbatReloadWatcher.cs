using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Joule;

/// <summary>How Predbat took a changed apps.yaml. Ok false means Joule should put the file back. Reloaded is true when Joule saw the
/// reload happen (in Predbat's log, or Predbat going away and coming back); Note says what happened, in words.</summary>
public sealed record ReloadOutcome(bool Ok, bool Reloaded, string Note);

/// <summary>What the edit touched, so an error Predbat logs after reloading can be tied to it: the names of the settings it changed
/// (export_today) and the entity ids and words in the new lines (sensor.export_today).</summary>
public sealed record ReloadContext(IReadOnlyList<string> Keys, IReadOnlyList<string> Terms)
{
    public static readonly ReloadContext None = new([], []);

    /// <summary>From the edited key names ("pred_bat › export_today") and the snippet Joule wrote.</summary>
    public static ReloadContext For(IEnumerable<string> keyNames, string snippet)
    {
        var keys = keyNames.Select(n => Regex.Replace(n.Split('›')[^1], @"\[\d+\]", "").Trim()).Where(k => k.Length >= 3).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var terms = new List<string>();
        foreach (var line in snippet.Split('\n'))
        {
            var text = line.Trim();
            if (text.StartsWith('#')) continue;
            // The value: after "key:" or a list dash, without quotes, a trailing comment or a !secret reference.
            var value = Regex.Replace(text, @"^(-\s*)?([^:#'""\s][^:#]*:\s*)?", "", RegexOptions.None, TimeSpan.FromMilliseconds(100));
            value = Regex.Replace(value, @"\s+#.*$", "", RegexOptions.None, TimeSpan.FromMilliseconds(100)).Trim().Trim('"', '\'').Trim();
            if (value.StartsWith('!') || value.StartsWith('[') || value.Contains("redacted", StringComparison.OrdinalIgnoreCase) || value.Contains('•')) continue;
            // Entity ids anywhere in it, and the whole value when it is a word rather than a number or yes/no.
            terms.AddRange(Regex.Matches(value, @"\b[a-z_]+\.[a-z0-9_]+\b", RegexOptions.None, TimeSpan.FromMilliseconds(100)).Select(m => m.Value));
            if (value.Length >= 4 && !double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _) && !Regex.IsMatch(value, @"^(true|false|yes|no|on|off|null)$", RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100)))
                terms.Add(value);
        }
        return new(keys, terms.Distinct(StringComparer.OrdinalIgnoreCase).ToList());
    }
}

public interface IPredbatReloadWatcher
{
    /// <summary>Watches Predbat after apps.yaml was written at <paramref name="writtenAt"/>, for at most a few minutes.</summary>
    Task<ReloadOutcome> WatchAsync(DateTimeOffset writtenAt, ReloadContext edit, CancellationToken ct);
}

/// <summary>The demo has no Predbat to reload; it says so instead of pretending.</summary>
public sealed class DemoReloadWatcher : IPredbatReloadWatcher
{
    public Task<ReloadOutcome> WatchAsync(DateTimeOffset writtenAt, ReloadContext edit, CancellationToken ct) =>
        Task.FromResult(new ReloadOutcome(true, true, "The demo has no real Predbat. On your own install Joule now watches Predbat reload apps.yaml and checks its log for errors."));
}

/// <summary>
/// Predbat watches apps.yaml itself: within a few seconds of a change it stops (its log, predbat.log, gets "Stopping Predbat"; the
/// "...due to file changes" wording goes only to its console), and its add-on or container starts it again with the new file (Predbat's
/// docs: "the change will automatically be detected and Predbat will be reloaded"). A file it can't load stops it starting, and a bad
/// entry shows as an error in its log. So Joule watches for that reload (the stop line in the log through Predbat's MCP, or Predbat
/// going away and coming back), waits for Predbat to answer again, then reads the errors logged since the edit.
/// Predbat not coming back means the edit failed. An error after the reload counts against the edit only when it is new (Predbat
/// didn't log the same line in the hours before the edit, because many installs log the same cloud or inverter errors all day) and is
/// about the edit: it mentions apps.yaml or YAML, a setting the edit changed, or an entity or value the edit wrote, or it is a new
/// Python traceback.
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
    /// <summary>How far before the edit to read Predbat's errors, to know which ones it was logging anyway.</summary>
    public TimeSpan Baseline { get; init; } = TimeSpan.FromHours(6);

    /// <summary>What Predbat's stop_all() writes to predbat.log when it stops (for a file change, among other reasons; the read is
    /// bounded to after the edit).</summary>
    const string StopLine = "Stopping Predbat";
    static readonly Regex AboutYaml = new(@"apps\.ya?ml|\byaml\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    static readonly Regex Traceback = new(@"traceback", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    bool UseMcp => mcp is { Configured: true };

    public async Task<ReloadOutcome> WatchAsync(DateTimeOffset writtenAt, ReloadContext edit, CancellationToken ct)
    {
        if (!UseMcp && string.IsNullOrWhiteSpace(configuration["Predbat:BaseUrl"]))
            return new(true, false, "Joule isn't connected to Predbat, so it couldn't watch Predbat reload apps.yaml.");
        // Predbat takes a few seconds to notice a change, so this reads how it was before the edit.
        if (!await AnswersAsync(ct))
            return new(true, false, "Predbat wasn't answering when Joule made the edit, so Joule couldn't check it reloaded. Check Predbat once it's back.");
        // The errors Predbat was already logging, read while it is still up.
        var before = UseMcp ? await ErrorsAsync(writtenAt - Baseline, writtenAt, ct) : null;
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
        var errors = await ErrorsAsync(writtenAt, null, ct);
        if (errors is null) return new(true, true, "Predbat reloaded apps.yaml and is answering again. Joule couldn't read its log to check for errors.");
        var known = (before ?? []).Select(InvestigationScheduler.Normalise).ToHashSet();
        var problem = errors.FirstOrDefault(l => !known.Contains(InvestigationScheduler.Normalise(l)) && AboutTheEdit(l, edit, before is not null));
        if (problem is not null) return new(false, true, $"Predbat reported a problem after reloading: “{Shorten(problem)}”");
        return new(true, true, errors.Count == 0
            ? "Predbat reloaded apps.yaml and logged no errors."
            : $"Predbat reloaded apps.yaml. It logged {errors.Count} error{(errors.Count == 1 ? "" : "s")} since, none about this edit.");
    }

    /// <summary>An error is about the edit when it names apps.yaml or YAML, a setting the edit changed, or an entity or value it wrote.
    /// A traceback counts too, but only when Joule could read what Predbat logged before (so it is known to be new).</summary>
    static bool AboutTheEdit(string line, ReloadContext edit, bool baselineKnown) =>
        AboutYaml.IsMatch(line)
        || edit.Keys.Concat(edit.Terms).Any(t => Regex.IsMatch(line, $@"(?<![\w.]){Regex.Escape(t)}(?![\w])", RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100)))
        || baselineKnown && Traceback.IsMatch(line);

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
        (await LogAsync(new { filter = "all", search = StopLine, start = Local(writtenAt), max_lines = 20 }, ct))?.Contains(StopLine, StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>The error lines Predbat logged from <paramref name="from"/> (to <paramref name="to"/>, or now), or null when its log can't be read.</summary>
    async Task<List<string>?> ErrorsAsync(DateTimeOffset from, DateTimeOffset? to, CancellationToken ct)
    {
        var text = await LogAsync(to is { } end
            ? new { filter = "errors", start = Local(from), end = Local(end), max_lines = 500 }
            : (object)new { filter = "errors", start = Local(from), max_lines = 200 }, ct);
        if (text is null) return null;
        return text.Split('\n').Select(l => Regex.Replace(l.Trim(), @"^[a-z_]{1,24}:\s+(?=\S)", "", RegexOptions.None, TimeSpan.FromMilliseconds(100))).Where(l => l.Length > 8 && Regex.IsMatch(l, @"\berror\b|traceback|exception", RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100))).Take(500).ToList();
    }
}
