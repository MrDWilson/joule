using System.Text.Json;

namespace Joule;

/// <summary>One message on its way to one channel (or a record of one that went). Items lists the inbox items it carries.</summary>
public sealed class PushDelivery
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];
    public string Channel { get; set; } = "";
    /// <summary>The inbox item key, "summary:2026-10-07" for a daily summary, or "test".</summary>
    public string Key { get; set; } = "";
    public string Event { get; set; } = "";
    public string Title { get; set; } = "";
    public string Body { get; set; } = "";
    public string? Link { get; set; }
    public bool Urgent { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset NextAt { get; set; }
    public int Attempts { get; set; }
    /// <summary>queued, held (quiet hours or the hourly limit), retrying, sent, failed or skipped.</summary>
    public string Status { get; set; } = "queued";
    public string? Error { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public List<string> Items { get; set; } = [];
}

/// <summary>What the delivery worker keeps between restarts: the queue, the log, and when each channel last sent.</summary>
public sealed class PushBook
{
    public List<PushDelivery> Queue { get; set; } = [];
    /// <summary>Finished deliveries (and tests), newest last, at most <see cref="NotificationService.LogSize"/>.</summary>
    public List<PushDelivery> Log { get; set; } = [];
    /// <summary>Send times per channel within the last hour, for the hourly limit.</summary>
    public Dictionary<string, List<DateTimeOffset>> Sent { get; set; } = [];
    /// <summary>The local date of each channel's last daily summary.</summary>
    public Dictionary<string, string> Summaries { get; set; } = [];
}

/// <summary>One row of Setup's delivery log.</summary>
public sealed record PushLogEntry(string Id, string Channel, string ChannelName, string Title, string Status, string? Error, int Attempts, DateTimeOffset At, DateTimeOffset? NextAt, bool Test);

/// <summary>
/// Keeps the notifications inbox in step with Joule's state and sends what each channel subscribes to. Every tick it
/// 1. merges what needs you, findings, unfinished checks, reports and offline alerts into the inbox (NotificationInbox.Sync);
/// 2. queues each new item for every enabled channel that wants its event (nothing found by the first pass after an upgrade is sent);
/// 3. queues the daily summary when its time comes;
/// 4. sends what's due: never during a channel's quiet hours (held until they end, then sent only if it still needs you), at most
///    Notifications:MaxPerHour messages an hour per channel, several due at once as one message, and failures retried after 1, 5
///    and 30 minutes before giving up. Each inbox item goes to each channel at most once.
/// </summary>
public sealed class NotificationService
{
    public const int LogSize = 100, MaxAttempts = 4;
    static readonly TimeSpan[] Backoff = [TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(30)];
    /// <summary>A summary that couldn't go at its time (Joule was off) is still sent within this long, not later.</summary>
    static readonly TimeSpan SummaryGrace = TimeSpan.FromHours(3);

    readonly StateService state;
    readonly PushSettings settings;
    readonly Func<HttpClient> http;
    readonly string path;
    readonly TimeProvider clock;
    readonly Func<InboxEnvironment> environment;
    readonly Func<HomeAssistantTarget?> homeAssistant;
    readonly TimeZoneInfo zone;
    readonly ILogger? log;
    readonly Func<string?, string?> clean;
    readonly SemaphoreSlim gate = new(1, 1);
    PushBook book;

    public NotificationService(StateService state, PushSettings settings, Func<HttpClient> http, string path, TimeProvider clock,
        Func<InboxEnvironment> environment, Func<HomeAssistantTarget?> homeAssistant, string timeZone, ILogger? log = null, Func<string?, string?>? clean = null)
    {
        // Text from AI checks is masked for configured secrets before it leaves Joule, as it is for the browser.
        // The channels' own tokens and webhook addresses are masked too: they can change in Setup at any time, so they're read each time.
        var masker = clean ?? (t => t);
        this.clean = t =>
        {
            var text = masker(t);
            var secrets = settings.SecretValues();
            return text is null || secrets.Length == 0 ? text : PredbatMcpSafety.CleanText(text, secrets);
        };
        this.state = state; this.settings = settings; this.http = http; this.path = path; this.clock = clock; this.environment = environment;
        this.homeAssistant = homeAssistant; this.log = log;
        try { zone = TimeZoneInfo.FindSystemTimeZoneById(timeZone); } catch (Exception e) when (e is TimeZoneNotFoundException or InvalidTimeZoneException) { zone = TimeZoneInfo.Utc; }
        book = Load(path);
    }

    public PushSettings Settings => settings;
    public HomeAssistantTarget? HomeAssistant => homeAssistant();

    static PushBook Load(string path)
    {
        try { return File.Exists(path) ? JsonSerializer.Deserialize<PushBook>(File.ReadAllText(path), JsonDefaults.Options) ?? new() : new(); }
        // A damaged file loses only the queue and log: notifications carry on.
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException) { return new(); }
    }

    void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(book, JsonDefaults.Options));
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>The delivery log, newest first, with anything still queued at the top.</summary>
    public List<PushLogEntry> Log(int limit = 50)
    {
        lock (book)
            return book.Queue.OrderByDescending(d => d.CreatedAt).Concat(Enumerable.Reverse(book.Log)).Take(limit)
                .Select(d => new PushLogEntry(d.Id, d.Channel, PushCatalogue.Channel(d.Channel)?.Name ?? d.Channel, d.Title, d.Status, d.Error, d.Attempts,
                    d.FinishedAt ?? d.CreatedAt, d.FinishedAt is null ? d.NextAt : null, d.Key == "test")).ToList();
    }

    public async Task TickAsync(CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            var now = clock.GetUtcNow();
            var added = await SyncInboxAsync(now, ct);
            var channels = settings.Channels().Where(c => c.Enabled).ToList();
            lock (book)
            {
                foreach (var (item, push) in added)
                {
                    if (!push) continue;
                    foreach (var c in channels.Where(c => c.Events.Contains(item.Event)))
                        book.Queue.Add(new PushDelivery
                        {
                            Channel = c.Info.Id, Key = item.Key, Event = item.Event, Title = $"Joule · {item.Label}", Body = clean(item.Title) + (clean(item.Detail) is { Length: > 0 } d ? "\n" + d : ""),
                            Link = settings.Link(item.Link), Urgent = item.Event is NotificationInbox.Offline, CreatedAt = now, NextAt = now, Items = [item.Id],
                        });
                }
                QueueSummaries(channels, now);
            }
            await SendDueAsync(channels, now, ct);
        }
        finally { gate.Release(); }
    }

    /// <summary>Merges the inbox; only writes the state when something changed. Returns the items added after the first pass.</summary>
    async Task<List<(InboxItem Item, bool Push)>> SyncInboxAsync(DateTimeOffset now, CancellationToken ct)
    {
        var env = environment();
        // The raw state, as MutateAsync below sees it: keys built from text masked for the browser would never match.
        var copy = state.ReadRaw();
        var before = JsonSerializer.Serialize(copy.Inbox, JsonDefaults.Options);
        var synced = copy.InboxSyncedAt is not null;
        NotificationInbox.Sync(copy, NotificationInbox.Collect(copy, now, env), now, true);
        if (synced && before == JsonSerializer.Serialize(copy.Inbox, JsonDefaults.Options)) return [];
        List<(InboxItem, bool)> added = [];
        var first = false;
        await state.MutateAsync(s =>
        {
            first = s.InboxSyncedAt is null;
            added = NotificationInbox.Sync(s, NotificationInbox.Collect(s, now, env), now, true);
        }, ct);
        return first ? [] : added;
    }

    void QueueSummaries(List<PushChannelSettings> channels, DateTimeOffset now)
    {
        var local = TimeZoneInfo.ConvertTime(now, zone);
        var due = local.Date + settings.SummaryTime.ToTimeSpan();
        if (local.DateTime < due || local.DateTime - due > SummaryGrace) return;
        var date = local.ToString("yyyy-MM-dd");
        var wanting = channels.Where(c => c.Events.Contains(PushCatalogue.Summary) && book.Summaries.GetValueOrDefault(c.Info.Id) != date).ToList();
        if (wanting.Count == 0) return;
        var (title, body) = SummaryText(state.ReadRaw(), now);
        body = clean(body) ?? "";
        foreach (var c in wanting)
        {
            book.Summaries[c.Info.Id] = date;
            book.Queue.Add(new PushDelivery { Channel = c.Info.Id, Key = $"summary:{date}", Event = PushCatalogue.Summary, Title = title, Body = body, Link = settings.Link("#/today"), CreatedAt = now, NextAt = now });
        }
        Save();
    }

    /// <summary>The daily summary: what needs you, what was found in the last day, anything offline and the latest report's line (from the last 36 hours).</summary>
    public static (string Title, string Body) SummaryText(AppState s, DateTimeOffset now)
    {
        NotificationInbox.Present(s, now);
        var open = s.Inbox.Where(i => i.Open && i.DismissedAt is null).ToList();
        var lines = new List<string>();
        var needs = open.Where(i => i.Event == NotificationInbox.NeedsYou).GroupBy(i => i.Label).Select(g => $"{g.Count()} {Plural(g.Key, g.Count())}").ToList();
        lines.Add(needs.Count > 0 ? "Needs you: " + string.Join(", ", needs) + "." : "Nothing needs you.");
        var found = open.Count(i => i.Event == NotificationInbox.Problem && now - i.At <= TimeSpan.FromDays(1));
        if (found > 0) lines.Add($"Found in the last day: {found} {(found == 1 ? "thing" : "things")} worth a look.");
        foreach (var o in open.Where(i => i.Event is NotificationInbox.Offline or NotificationInbox.Unfinished)) lines.Add(o.Title + ".");
        var report = s.Notifications.Where(n => !string.IsNullOrEmpty(n.ReportId) && now - n.At <= TimeSpan.FromHours(36)).OrderByDescending(n => n.At).FirstOrDefault();
        if (report is { Message.Length: > 0 }) lines.Add($"{report.Title}: {report.Message}");
        return ("Joule · Daily summary", string.Join("\n", lines));
    }
    static string Plural(string label, int n) => label switch
    {
        "Suggestion" => n == 1 ? "suggestion" : "suggestions",
        "To-do" => n == 1 ? "to-do" : "to-dos",
        "File edit" => n == 1 ? "file edit" : "file edits",
        "Trial to decide" => n == 1 ? "trial to decide" : "trials to decide",
        _ => label.ToLowerInvariant(),
    };

    async Task SendDueAsync(List<PushChannelSettings> enabled, DateTimeOffset now, CancellationToken ct)
    {
        List<(PushChannelSettings Channel, List<PushDelivery> Batch)> work = [];
        AppState? presented = null;
        lock (book)
        {
            if (book.Queue.Count == 0) return;
            foreach (var group in book.Queue.Where(d => d.NextAt <= now).GroupBy(d => d.Channel).ToList())
            {
                var channel = enabled.FirstOrDefault(c => c.Info.Id == group.Key);
                if (channel is null) { foreach (var d in group) Finish(d, "skipped", "The channel was turned off before it was sent.", now); continue; }
                var due = group.ToList();
                // Quiet hours: hold everything but the summary (sent at the time you chose) until they end.
                if (channel.Quiet is { } quiet && quiet.Contains(TimeOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime)))
                {
                    var end = quiet.EndAfter(now, zone);
                    foreach (var d in due.Where(d => d.Event != PushCatalogue.Summary)) { d.Status = "held"; d.NextAt = end; d.Error = "Quiet hours"; }
                    due.RemoveAll(d => d.Event != PushCatalogue.Summary);
                    if (due.Count == 0) continue;
                }
                // Something you have already seen, handled or cleared in Joule isn't worth a buzz any more.
                presented ??= Presented(now);
                foreach (var d in due.Where(d => d.Items.Count > 0).ToList())
                {
                    if (d.Items.All(id => presented.Inbox.FirstOrDefault(i => i.Id == id) is not { Open: true, ReadAt: null, DismissedAt: null }))
                    { Finish(d, "skipped", "Already seen or handled in Joule before it could be sent.", now); due.Remove(d); }
                }
                if (due.Count == 0) continue;
                var sent = book.Sent.TryGetValue(channel.Info.Id, out var times) ? times : book.Sent[channel.Info.Id] = [];
                sent.RemoveAll(t => now - t >= TimeSpan.FromHours(1));
                if (sent.Count >= settings.MaxPerHour)
                {
                    var free = sent.Min() + TimeSpan.FromHours(1);
                    foreach (var d in due) { d.Status = "held"; d.NextAt = free; d.Error = $"Hourly limit ({settings.MaxPerHour} a hour)"; }
                    continue;
                }
                work.Add((channel, due));
            }
            Save();
        }
        foreach (var (channel, batch) in work)
        {
            var message = Combine(batch, settings);
            PushResult result;
            try { using var request = PushChannels.Build(channel, message, homeAssistant()); result = await PushChannels.SendAsync(http(), request, channel.Info.Name, ct, PushChannels.ShowsReply(channel.Info)); }
            catch (DomainException e) { result = new(false, e.Message, false); }
            lock (book)
            {
                var at = clock.GetUtcNow();
                if (result.Ok) book.Sent[channel.Info.Id].Add(at);
                foreach (var d in batch)
                {
                    d.Attempts++;
                    if (result.Ok) Finish(d, "sent", null, at);
                    else if (result.Retry && d.Attempts < MaxAttempts) { d.Status = "retrying"; d.Error = result.Error; d.NextAt = at + Backoff[Math.Min(d.Attempts - 1, Backoff.Length - 1)]; }
                    else Finish(d, "failed", result.Error, at);
                }
                Save();
            }
            if (!result.Ok) log?.LogWarning("Notification to {Channel} failed: {Error}", channel.Info.Name, result.Error);
        }
    }

    AppState Presented(DateTimeOffset now) { var s = state.ReadRaw(); NotificationInbox.Present(s, now); return s; }

    /// <summary>One message for everything due on a channel at once: the item itself, or a short list.</summary>
    public static PushMessage Combine(IReadOnlyList<PushDelivery> batch, PushSettings settings)
    {
        var first = batch[0];
        if (batch.Count == 1) return new(first.Title, first.Body, first.Link, first.Event, first.Urgent);
        var lines = batch.Take(6).Select(d => "• " + d.Title.Replace("Joule · ", "") + ": " + d.Body.Split('\n')[0]).ToList();
        if (batch.Count > 6) lines.Add($"…and {batch.Count - 6} more.");
        return new($"Joule · {batch.Count} notifications", string.Join("\n", lines), settings.Link("#/today"), batch.Any(d => d.Urgent) ? NotificationInbox.Offline : first.Event, batch.Any(d => d.Urgent));
    }

    void Finish(PushDelivery d, string status, string? error, DateTimeOffset at)
    {
        d.Status = status; d.Error = error; d.FinishedAt = at;
        book.Queue.Remove(d);
        book.Log.Add(d);
        if (book.Log.Count > LogSize) book.Log.RemoveRange(0, book.Log.Count - LogSize);
    }

    /// <summary>Sends a test message to one channel now (quiet hours and the hourly limit don't apply) and logs it.</summary>
    public async Task<PushResult> TestAsync(string channelId, CancellationToken ct)
    {
        var info = PushCatalogue.Channel(channelId) ?? throw new DomainException("Unknown notification channel.", 404);
        var channel = settings.Channel(info);
        var link = settings.Link("#/setup/notifications");
        var message = new PushMessage("Joule · Test", $"{info.Name} works. Joule will send the events you chose here." + (link is null ? " Set App__PublicUrl to add a link back to Joule." : ""), link, "test");
        PushResult result;
        try { using var request = PushChannels.Build(channel, message, homeAssistant()); result = await PushChannels.SendAsync(http(), request, info.Name, ct, PushChannels.ShowsReply(info)); }
        catch (DomainException e) { result = new(false, e.Message, false); }
        await gate.WaitAsync(ct);
        try
        {
            lock (book)
            {
                var now = clock.GetUtcNow();
                var d = new PushDelivery { Channel = info.Id, Key = "test", Event = "test", Title = "Test message", Body = message.Body, CreatedAt = now, NextAt = now, Attempts = 1 };
                Finish(d, result.Ok ? "sent" : "failed", result.Error, now);
                Save();
            }
        }
        finally { gate.Release(); }
        return result;
    }
}

/// <summary>Runs the notification service once before Joule starts answering, then every 20 seconds.</summary>
public sealed class NotificationWorker(NotificationService service, ILogger<NotificationWorker> log) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(20);
    /// <summary>Fills the inbox before Joule starts answering, so the first page already has its notifications.</summary>
    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        try { await service.TickAsync(cancellationToken); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception e) { log.LogWarning("Notifications couldn't be updated at startup: {Type}. Trying again shortly.", e.GetType().Name); }
        await base.StartAsync(cancellationToken);
    }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await Task.Delay(Interval, stoppingToken); } catch (OperationCanceledException) { break; }
            try { await service.TickAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception e) { log.LogWarning("Notifications couldn't be updated: {Type}. Trying again shortly.", e.GetType().Name); }
        }
    }
}
