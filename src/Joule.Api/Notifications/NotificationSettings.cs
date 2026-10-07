using System.Globalization;
using System.Text.RegularExpressions;

namespace Joule;

/// <summary>A field of a notification channel, as Setup shows it. Key is the full setting key (Notifications:Ntfy:Topic).</summary>
public sealed record PushChannelField(string Key, string Label, string Kind, bool Secret, bool Required, string? Placeholder = null, string? Note = null);
/// <summary>A way to reach your phone: its own fields, plus the Enabled, Events and QuietHours every channel has.</summary>
public sealed record PushChannelInfo(string Id, string Name, string Description, List<PushChannelField> Fields)
{
    public string Prefix => $"Notifications:{Id}";
    public string EnabledKey => Prefix + ":Enabled";
    public string EventsKey => Prefix + ":Events";
    public string QuietKey => Prefix + ":QuietHours";
}
/// <summary>An event a channel can send: id, the words Setup shows, and one line about it.</summary>
public sealed record PushEventInfo(string Id, string Label, string Description);

/// <summary>
/// The phone notification channels (ntfy, Pushover, Home Assistant, Telegram, Discord/Slack, a JSON webhook) and the events they
/// can send. Every value is a Setup setting: saved in settings.json, overridden by its environment variable (Notifications__Ntfy__Topic)
/// and read live, so changing one never needs a restart.
/// </summary>
public static class PushCatalogue
{
    public const string Summary = "summary";
    public const string PublicUrlKey = "App:PublicUrl", OfflineMinutesKey = "Notifications:OfflineMinutes", SummaryTimeKey = "Notifications:SummaryTime", MaxPerHourKey = "Notifications:MaxPerHour";
    public const int DefaultOfflineMinutes = 30, DefaultMaxPerHour = 6;
    public const string DefaultSummaryTime = "08:00";

    public static readonly IReadOnlyList<PushEventInfo> Events =
    [
        new(NotificationInbox.NeedsYou, "Something needs you", "A new suggestion, file edit, to-do or trial to decide."),
        new(NotificationInbox.Problem, "A problem found", "An AI check found a problem or an opportunity worth your attention."),
        new(NotificationInbox.Unfinished, "Checks keep not finishing", "AI checks didn't finish twice or more in a row."),
        new(NotificationInbox.Offline, "Something offline", "Predbat, Home Assistant or a sensor hasn't answered for a while."),
        new(Summary, "Daily summary", "One message a day: what needs you, what was found and yesterday's figures."),
    ];
    public static readonly string DefaultEvents = string.Join(",", Events.Where(e => e.Id != Summary).Select(e => e.Id));

    public static readonly IReadOnlyList<PushChannelInfo> Channels =
    [
        new("Ntfy", "ntfy", "Free push app for Android and iPhone. Pick a hard-to-guess topic and subscribe to it in the app.",
        [
            new("Notifications:Ntfy:Url", "Server", "url", false, false, "https://ntfy.sh", "Leave empty for ntfy.sh, or use your own ntfy server."),
            new("Notifications:Ntfy:Topic", "Topic", "topic", false, true, "joule-a8f3k2", "Anyone who knows the topic can read it on ntfy.sh: make it hard to guess."),
            new("Notifications:Ntfy:Token", "Access token (optional)", "token", true, false, "tk_…", "Only for a protected topic or your own server."),
        ]),
        new("Pushover", "Pushover", "A paid push app. Create an application at pushover.net for its API token.",
        [
            new("Notifications:Pushover:UserKey", "Your user key", "token", true, true),
            new("Notifications:Pushover:AppToken", "Application API token", "token", true, true),
        ]),
        new("HomeAssistant", "Home Assistant", "Sends through Home Assistant's notify service, such as the companion app on your phone. Uses the Home Assistant address and token from Sensors.",
        [
            new("Notifications:HomeAssistant:Service", "Notify service", "service", false, true, "notify.mobile_app_my_phone"),
        ]),
        new("Telegram", "Telegram", "Make a bot with @BotFather, send it a message, then use your chat id.",
        [
            new("Notifications:Telegram:BotToken", "Bot token", "token", true, true, "123456789:AA…"),
            new("Notifications:Telegram:ChatId", "Chat id", "chatId", false, true, "123456789", "Your numeric chat id (or @channelname for a channel the bot posts to)."),
        ]),
        new("Chat", "Discord or Slack", "Posts to a channel through an incoming webhook (Discord, Slack, Mattermost and others that accept the same).",
        [
            new("Notifications:Chat:WebhookUrl", "Webhook address", "webhook", true, true, "https://discord.com/api/webhooks/…"),
        ]),
        new("Webhook", "Webhook (JSON)", "Sends each notification as JSON to an address of yours, for your own automations.",
        [
            new("Notifications:Webhook:Url", "Address", "webhook", true, true, "https://example.com/joule"),
            new("Notifications:Webhook:Token", "Bearer token (optional)", "token", true, false, null, "Sent as Authorization: Bearer … when set."),
        ]),
    ];
    public static PushChannelInfo? Channel(string id) => Channels.FirstOrDefault(c => string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>Every notification setting, for SavedSettings. All of them apply without a restart.</summary>
    public static IEnumerable<SetupField> Fields()
    {
        yield return new(PublicUrlKey, "url", false, true);
        yield return new(OfflineMinutesKey, "minutes", false, true);
        yield return new(SummaryTimeKey, "time", false, true);
        yield return new(MaxPerHourKey, "perHour", false, true);
        foreach (var c in Channels)
        {
            yield return new(c.EnabledKey, "bool", false, true);
            yield return new(c.EventsKey, "events", false, true);
            yield return new(c.QuietKey, "quietHours", false, true);
            foreach (var f in c.Fields) yield return new(f.Key, f.Kind, f.Secret, true);
        }
    }

    static readonly Regex Topic = new("^[A-Za-z0-9_-]{1,64}$", RegexOptions.CultureInvariant);
    static readonly Regex Service = new("^(notify\\.)?[a-z0-9_]{1,100}$", RegexOptions.CultureInvariant);
    static readonly Regex ChatId = new("^(-?[0-9]{1,20}|@[A-Za-z0-9_]{4,64})$", RegexOptions.CultureInvariant);
    static readonly Regex Clock = new("^([01][0-9]|2[0-3]):[0-5][0-9]$", RegexOptions.CultureInvariant);

    /// <summary>A one-line problem with a notification setting's value, or null when it can be saved. Null too for kinds this doesn't know.</summary>
    public static string? Validate(string kind, string v) => kind switch
    {
        "topic" => Topic.IsMatch(v) ? null : "An ntfy topic uses letters, digits, - and _ only, up to 64 characters.",
        "service" => Service.IsMatch(v) ? null : "Enter a notify service like notify.mobile_app_my_phone.",
        "chatId" => ChatId.IsMatch(v) ? null : "A Telegram chat id is a number (like 123456789 or -1001234567890) or @channelname.",
        "webhook" => Uri.TryCreate(v, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" && string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Fragment) && v.Length <= 1000
            ? null : "Enter the full webhook address, starting https://.",
        "events" => v == "none" || v.Split(',').All(e => Events.Any(x => x.Id == e.Trim())) ? null : $"Choose events from: {string.Join(", ", Events.Select(e => e.Id))}.",
        "quietHours" => QuietHours.TryParse(v, out _) ? null : "Quiet hours look like 22:00-07:00.",
        "time" => Clock.IsMatch(v) ? null : "Enter a time like 08:00.",
        "minutes" => int.TryParse(v, NumberStyles.None, CultureInfo.InvariantCulture, out var m) && m is >= 5 and <= 1440 ? null : "Choose 5 to 1440 minutes.",
        "perHour" => int.TryParse(v, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n is >= 1 and <= 60 ? null : "Choose 1 to 60 messages an hour.",
        _ => null,
    };
}

/// <summary>A daily quiet window such as 22:00-07:00 (it may cross midnight). Equal ends mean no quiet hours.</summary>
public readonly record struct QuietHours(TimeOnly Start, TimeOnly End)
{
    public static bool TryParse(string? text, out QuietHours quiet)
    {
        quiet = default;
        var parts = (text ?? "").Split('-', StringSplitOptions.TrimEntries);
        if (parts.Length != 2 || !TimeOnly.TryParseExact(parts[0], "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var start)
            || !TimeOnly.TryParseExact(parts[1], "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var end)) return false;
        quiet = new(start, end);
        return true;
    }
    public bool Contains(TimeOnly t) => Start == End ? false : Start < End ? t >= Start && t < End : t >= Start || t < End;
    /// <summary>When the quiet window that contains <paramref name="now"/> ends, in UTC.</summary>
    public DateTimeOffset EndAfter(DateTimeOffset now, TimeZoneInfo zone)
    {
        var local = TimeZoneInfo.ConvertTime(now, zone);
        var end = local.Date + End.ToTimeSpan();
        if (end <= local.DateTime) end = end.AddDays(1);
        return new DateTimeOffset(end, zone.GetUtcOffset(end)).ToUniversalTime();
    }
    public override string ToString() => $"{Start:HH\\:mm}-{End:HH\\:mm}";
}

/// <summary>One channel's settings as they are now.</summary>
public sealed record PushChannelSettings(PushChannelInfo Info, bool Enabled, IReadOnlySet<string> Events, QuietHours? Quiet, Func<string, string?> Value);

/// <summary>Notification settings read live: the environment wins, then what Setup saved, then the defaults.</summary>
public sealed class PushSettings(SavedSettings saved, IConfiguration configuration)
{
    public string? Get(string key) => saved.Current(key, configuration) is { Length: > 0 } v ? v.Trim() : null;
    public string? PublicUrl => Get(PushCatalogue.PublicUrlKey)?.TrimEnd('/');
    public int OfflineMinutes => int.TryParse(Get(PushCatalogue.OfflineMinutesKey), out var m) && m is >= 5 and <= 1440 ? m : PushCatalogue.DefaultOfflineMinutes;
    public int MaxPerHour => int.TryParse(Get(PushCatalogue.MaxPerHourKey), out var n) && n is >= 1 and <= 60 ? n : PushCatalogue.DefaultMaxPerHour;
    public TimeOnly SummaryTime => TimeOnly.TryParseExact(Get(PushCatalogue.SummaryTimeKey), "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) ? t : new(8, 0);
    public PushChannelSettings Channel(PushChannelInfo info)
    {
        var events = Get(info.EventsKey) is { } list ? list == "none" ? [] : list.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal)
            : PushCatalogue.DefaultEvents.Split(',').ToHashSet(StringComparer.Ordinal);
        return new(info, Get(info.EnabledKey) == "true", events, QuietHours.TryParse(Get(info.QuietKey), out var q) && q.Start != q.End ? q : null, Get);
    }
    public IEnumerable<PushChannelSettings> Channels() => PushCatalogue.Channels.Select(Channel);
    /// <summary>A deep link into Joule for a hash route, when App__PublicUrl is set.</summary>
    public string? Link(string? hash) => PublicUrl is { } root ? root + "/" + (string.IsNullOrEmpty(hash) ? "" : hash.StartsWith('#') ? hash : "#" + hash) : null;
}
