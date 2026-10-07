using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Joule;

/// <summary>
/// One notification in the bell (and, for the events a channel subscribes to, on your phone). Created once per thing, by its
/// <see cref="Key"/>, and kept server-side so reading, dismissing and handling it stick across browsers and restarts.
/// </summary>
public sealed class InboxItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..16];
    /// <summary>What it is about, stable across checks: "proposal:{id}", "todo:{hash of the title}", "offline:predbat:{since}"…</summary>
    public string Key { get; set; } = "";
    /// <summary>needs_you, problem, unfinished, offline or report: the event a notification channel can subscribe to.</summary>
    public string Event { get; set; } = "";
    /// <summary>The small chip: Suggestion, To-do, File edit, Trial to decide, Found something, Didn't finish, Offline, Report.</summary>
    public string Label { get; set; } = "";
    /// <summary>May hold AI or server text (setting keys, codes): the browser renders it as plain text.</summary>
    public string Title { get; set; } = "";
    public string? Detail { get; set; }
    /// <summary>Where it opens in Joule, as a hash route ("#/insights/inv/abc").</summary>
    public string Link { get; set; } = "#/today";
    /// <summary>accent, or warn for something that went wrong (a check that didn't finish, something offline).</summary>
    public string Tone { get; set; } = "accent";
    public DateTimeOffset At { get; set; }
    public DateTimeOffset? ReadAt { get; set; }
    public DateTimeOffset? DismissedAt { get; set; }
    /// <summary>When the thing stopped needing you (approved, turned down, back online…). It then never counts in the badge.</summary>
    public DateTimeOffset? ResolvedAt { get; set; }
    /// <summary>Computed on read: the thing it is about still wants you. Only open, unread, undismissed items count in the badge.</summary>
    public bool Open { get; set; } = true;
}

public partial class AppState
{
    List<InboxItem> inbox = [];
    /// <summary>The bell's notifications, newest last. See NotificationInbox.</summary>
    public List<InboxItem> Inbox { get => inbox; set => inbox = value ?? []; }
    /// <summary>When the inbox was first filled. Items found by that first pass are never pushed to a phone.</summary>
    public DateTimeOffset? InboxSyncedAt { get; set; }
}

/// <summary>Something that should be in the inbox now. Push is false for things that only belong in the bell (reports, minor findings).</summary>
public sealed record InboxCandidate(string Key, string Event, string Label, string Title, string? Detail, string Link, string Tone, DateTimeOffset At, bool Push = true);

/// <summary>
/// What the inbox needs from outside AppState to tell whether Predbat, Home Assistant or a sensor has gone quiet. Started is when
/// this process started: silence is measured from the later of the last answer and that, so time Joule itself was off (a reboot,
/// an upgrade) never counts as Predbat or Home Assistant being offline.
/// </summary>
public sealed record InboxEnvironment(bool Demo, bool PredbatConfigured, TelemetryStatus? Telemetry, int OfflineMinutes, DateTimeOffset? Started = null);

/// <summary>
/// The notifications inbox. Items are made from the state by <see cref="Collect"/> (what needs you, findings, checks that keep
/// failing, reports, and with an environment, things offline) and merged by <see cref="Sync"/>: a new key becomes a new item, an
/// item whose thing has been handled is resolved, and nothing is ever re-created for a key already in the inbox, so reading or
/// dismissing an item sticks. <see cref="Present"/> marks each item open or not on every read, so handling something in Joule
/// drops it from the badge at once, without waiting for the worker.
/// </summary>
public static class NotificationInbox
{
    public const string NeedsYou = "needs_you", Problem = "problem", Unfinished = "unfinished", Offline = "offline", Report = "report";
    /// <summary>Findings older than this when first seen aren't added (so the first pass after an upgrade doesn't fill the bell).</summary>
    public static readonly TimeSpan FindingWindow = TimeSpan.FromHours(48);
    /// <summary>Handled or dismissed items are kept this long, then dropped.</summary>
    public static readonly TimeSpan KeepClosed = TimeSpan.FromDays(14);
    public const int MaxItems = 200;

    static readonly Dictionary<string, string> metricLabels = new()
    {
        ["load"] = "Home use", ["pv"] = "Solar", ["grid_import"] = "Grid import", ["grid_export"] = "Grid export", ["battery_charge"] = "Battery charge",
        ["battery_discharge"] = "Battery discharge", ["ev"] = "Car charging", ["soc"] = "Battery level", ["import_tariff"] = "Import price", ["export_tariff"] = "Export price",
    };

    /// <summary>
    /// Everything that belongs in the inbox now. With <paramref name="creating"/> false the time windows are relaxed (a finding
    /// older than two days, a trial that was due) so the result answers "is this item's thing still open?" rather than "add this".
    /// Without an environment nothing is said about Predbat, Home Assistant or sensors being offline.
    /// </summary>
    public static List<InboxCandidate> Collect(AppState s, DateTimeOffset now, InboxEnvironment? env = null, bool creating = true)
    {
        var items = new List<InboxCandidate>();
        foreach (var p in s.Proposals.Where(p => p.Status == "Pending"))
            items.Add(new($"proposal:{p.Id}", NeedsYou, "Suggestion", Text(p.Title, "A suggested settings change"), "Approve or turn it down.", "#/insights/suggestions", "accent", p.CreatedAt));
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var newestFirst = s.Investigations.OrderByDescending(i => i.At).ThenByDescending(i => i.Id, StringComparer.Ordinal).ToList();
        foreach (var i in newestFirst)
        {
            foreach (var c in i.FileChanges)
            {
                var key = "file:" + Hash(Fold(c.File) + "|" + Fold(c.Snippet));
                if (!InvestigationFileChanges.IsOpen(c) || !seen.Add(key)) continue;
                // An edit you've marked applied waits for the next check to confirm it, not for you.
                if (c.Status == "applied") continue;
                items.Add(new(key, NeedsYou, "File edit", Text(c.Summary, $"An edit to {c.File}"), string.IsNullOrWhiteSpace(c.File) ? null : c.File, "#/insights/suggestions", "accent", i.At));
            }
            foreach (var step in i.NextSteps)
            {
                var key = "todo:" + Hash(Fold(step.Title));
                if ((step.Status ?? "open") == "closed" || !seen.Add(key)) continue;
                items.Add(new(key, NeedsYou, "To-do", Text(step.Title, "Something to look at"), null, "#/insights/suggestions", "accent", i.At));
            }
        }
        foreach (var e in s.Experiments.Where(e => ChangeEngine.IsOpen(e) && (!creating || e.ReviewAt <= now)))
            items.Add(new($"trial:{e.Id}", NeedsYou, "Trial to decide", Text(e.Title, "A trial"), "Its review date has come: keep it or undo it.", "#/insights/experiments", "accent", e.ReviewAt));

        foreach (var i in newestFirst)
        {
            if (i.Status != "Completed" || i.RepeatOf is not null || i.DismissedAt is not null || i.Verdict is null or "no_change") continue;
            if (creating && now - i.At > FindingWindow) continue;
            var worth = i.Verdict is "problem" or "opportunity" && i.Severity != "info";
            items.Add(new($"finding:{i.Id}", Problem, "Found something", Text(i.Headline, Text(i.Title, "New finding")), StateProjection.Shorten(i.Plain, 200),
                $"#/insights/inv/{Uri.EscapeDataString(i.Id)}", "accent", i.At, worth));
        }
        if (UnfinishedStreak(newestFirst) is { } streak)
            items.Add(streak);

        foreach (var n in s.Notifications.Where(n => n.ReadAt is null && !string.IsNullOrEmpty(n.ReportId)))
        {
            var report = s.Reports.FirstOrDefault(r => r.Id == n.ReportId);
            if (report is null || report.ReadAt is not null) continue;
            items.Add(new($"report:{n.ReportId}", Report, "Report", Text(n.Title, "A new report"), string.IsNullOrWhiteSpace(n.Message) ? null : n.Message,
                $"#/energy/reports?report={Uri.EscapeDataString(n.ReportId)}", "accent", n.At, false));
        }
        if (env is not null) items.AddRange(OfflineAlerts(s, now, env));
        return items;
    }

    /// <summary>Two or more tries in a row that didn't finish (a resumed check counts each try). A check you stopped yourself is skipped.</summary>
    static InboxCandidate? UnfinishedStreak(List<Investigation> newestFirst)
    {
        var streak = new List<Investigation>();
        foreach (var i in newestFirst)
        {
            if (i.Status == "Running" || i.FailureKind == "stopped") continue;
            if (i.Status is "Failed" or "Interrupted") streak.Add(i);
            else break;
        }
        var tries = streak.Sum(i => Math.Max(1, i.Attempts));
        if (tries < 2) return null;
        var latest = streak[0];
        var reason = Text(latest.Headline, Text(latest.Plain, Text(latest.Title, "")));
        return new($"unfinished:{streak[^1].Id}", Unfinished, "Didn't finish", "AI checks keep not finishing",
            $"{tries} tries in a row." + (reason.Length > 0 ? " Latest: " + StateProjection.Shorten(reason, 160) : ""),
            $"#/insights/inv/{Uri.EscapeDataString(latest.Id)}", "warn", latest.At);
    }

    static IEnumerable<InboxCandidate> OfflineAlerts(AppState s, DateTimeOffset now, InboxEnvironment env)
    {
        if (env.Demo) yield break;
        var limit = TimeSpan.FromMinutes(Math.Clamp(env.OfflineMinutes, 5, 1440));
        var started = env.Started ?? DateTimeOffset.MinValue;
        var minutes = (int)limit.TotalMinutes;
        // The detail stays the same for the whole outage (the bell shows how long ago from At), so a long outage doesn't rewrite the inbox every tick.
        if (env.PredbatConfigured && Later(s.LastCollection, started) is { } last && now - last >= limit)
            yield return new($"offline:predbat:{last.ToUnixTimeSeconds()}", Offline, "Offline", "Joule can't reach Predbat",
                $"No answer for {minutes} minutes or more.", "#/setup", "warn", last + limit);
        if (env.Telemetry is { Configured: true, Demo: false } t)
        {
            // After a restart the last reading is only known once one succeeds; a Home Assistant that fails from the start counts from then.
            var read = Later(t.LastCollection, started) ?? (t.Error is not null && env.Started is { } since ? since : null);
            if (read is { } quiet && now - quiet >= limit)
                yield return new($"offline:ha:{quiet.ToUnixTimeSeconds()}", Offline, "Offline", "Joule can't read your sensors",
                    $"No readings for {minutes} minutes or more.", "#/setup/sensors", "warn", quiet + limit);
            else
                foreach (var issue in t.Issues.Where(x => x.Metric != "all" && x.Since is { } since && now - Max(since, started) >= limit))
                    yield return new($"offline:sensor:{issue.Metric}:{issue.Since!.Value.ToUnixTimeSeconds()}", Offline, "Offline",
                        $"{metricLabels.GetValueOrDefault(issue.Metric, issue.Metric)} sensor isn't reporting", Text(issue.Message, null), "#/setup/sensors", "warn", Max(issue.Since.Value, started) + limit);
        }
    }

    static DateTimeOffset? Later(DateTimeOffset? last, DateTimeOffset started) => last is { } at ? Max(at, started) : null;
    static DateTimeOffset Max(DateTimeOffset a, DateTimeOffset b) => a > b ? a : b;

    /// <summary>
    /// Merges what belongs in the inbox now into it. Returns the items added (the caller pushes them, except on the very first pass).
    /// <paramref name="timeAware"/> is true when the candidates include the offline checks, so offline items may be resolved.
    /// </summary>
    public static List<(InboxItem Item, bool Push)> Sync(AppState s, IReadOnlyList<InboxCandidate> candidates, DateTimeOffset now, bool timeAware)
    {
        var added = new List<(InboxItem, bool)>();
        var open = Collect(s, now, creating: false).Select(c => c.Key).Concat(candidates.Select(c => c.Key)).ToHashSet(StringComparer.Ordinal);
        var byKey = s.Inbox.GroupBy(i => i.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Last(), StringComparer.Ordinal);
        foreach (var c in candidates)
        {
            if (byKey.TryGetValue(c.Key, out var existing))
            {
                // Keep the wording current (a streak's count, a sensor's message) without making it new again.
                if (existing.ResolvedAt is null) { existing.Title = c.Title; existing.Detail = c.Detail; existing.Link = c.Link; }
                continue;
            }
            var item = new InboxItem { Key = c.Key, Event = c.Event, Label = c.Label, Title = c.Title, Detail = c.Detail, Link = c.Link, Tone = c.Tone, At = c.At > now ? now : c.At };
            s.Inbox.Add(item); byKey[c.Key] = item;
            added.Add((item, c.Push));
        }
        foreach (var item in s.Inbox)
        {
            var stillOpen = item.Event == Offline && !timeAware ? item.ResolvedAt is null : open.Contains(item.Key);
            if (!stillOpen && item.ResolvedAt is null) item.ResolvedAt = now;
            // Un-dismissing a finding brings its notification back (it was never re-created, so it keeps its read state).
            else if (stillOpen && item.ResolvedAt is not null && item.Event != Offline) item.ResolvedAt = null;
        }
        Prune(s, now);
        s.InboxSyncedAt ??= now;
        return added;
    }

    static void Prune(AppState s, DateTimeOffset now)
    {
        s.Inbox.RemoveAll(i => i.ResolvedAt is { } resolved && now - resolved > KeepClosed);
        if (s.Inbox.Count <= MaxItems) return;
        // Closed items go first, oldest first; open ones only if there are somehow hundreds of them.
        var drop = s.Inbox.OrderBy(i => i.ResolvedAt is null && i.DismissedAt is null ? 1 : 0).ThenBy(i => i.At).Take(s.Inbox.Count - MaxItems).ToHashSet();
        s.Inbox.RemoveAll(drop.Contains);
    }

    /// <summary>Sets <see cref="InboxItem.Open"/> on a read copy, from the state as it is now.</summary>
    public static void Present(AppState s, DateTimeOffset now)
    {
        if (s.Inbox.Count == 0) return;
        var open = Collect(s, now, creating: false).Select(c => c.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var item in s.Inbox) item.Open = item.ResolvedAt is null && (item.Event == Offline || open.Contains(item.Key));
    }

    /// <summary>Items that count in the bell's badge: open, unread and not dismissed. Call on a presented copy.</summary>
    public static int Unread(AppState s) => s.Inbox.Count(i => i.Open && i.ReadAt is null && i.DismissedAt is null);

    public static void MarkRead(AppState s, string id, DateTimeOffset now)
    {
        var item = s.Inbox.FirstOrDefault(i => i.Id == id) ?? throw new DomainException("That notification has gone. Refresh to see the latest.", 404);
        Read(s, item, now);
    }
    public static void Dismiss(AppState s, string id, DateTimeOffset now)
    {
        var item = s.Inbox.FirstOrDefault(i => i.Id == id) ?? throw new DomainException("That notification has gone. Refresh to see the latest.", 404);
        Read(s, item, now); item.DismissedAt ??= now;
    }
    public static void MarkAllRead(AppState s, DateTimeOffset now) { foreach (var item in s.Inbox.Where(i => i.ReadAt is null)) Read(s, item, now); }
    /// <summary>Clears the bell: every item is read and dismissed. The things themselves (suggestions, to-dos…) are untouched.</summary>
    public static void DismissAll(AppState s, DateTimeOffset now) { foreach (var item in s.Inbox.Where(i => i.DismissedAt is null)) { Read(s, item, now); item.DismissedAt = now; } }

    static void Read(AppState s, InboxItem item, DateTimeOffset now)
    {
        item.ReadAt ??= now;
        // A report's notification is the same thing as the older report notification: one read state.
        if (item.Key.StartsWith("report:", StringComparison.Ordinal))
            foreach (var n in s.Notifications.Where(n => n.ReportId == item.Key["report:".Length..])) n.ReadAt ??= now;
    }

    static string Text(string? text, string? fallback) => string.IsNullOrWhiteSpace(text) ? fallback ?? "" : text.Trim();
    static string Fold(string? text) => string.Join(' ', (text ?? "").ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)), 0, 8).ToLowerInvariant();
}
