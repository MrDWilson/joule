namespace Joule;

/// <summary>A notification setting as Setup shows it: never a secret's value, only whether it is set and where from.</summary>
public sealed record PushFieldView(string Key, string EnvVar, string Label, string Kind, bool Secret, bool Required, string? Value, bool Set, string? Source, string? Placeholder, string? Note);
public sealed record PushChannelView(string Id, string Name, string Description, bool Enabled, bool Ready, string? Problem, List<string> Events, string? QuietHours,
    PushFieldView EnabledField, PushFieldView EventsField, PushFieldView QuietField, List<PushFieldView> Fields);
/// <summary>GET /api/push/settings: every channel, the shared settings, the events and the delivery log.</summary>
public sealed record PushSettingsView(bool CanSave, string? Locked, bool HomeAssistantReady, string? PublicUrl, int OfflineMinutes, string SummaryTime, int MaxPerHour,
    List<PushFieldView> General, List<PushEventInfo> Events, List<PushChannelView> Channels, List<PushLogEntry> Log);
public sealed record PushSaveRequest(Dictionary<string, string?>? Values);
public sealed record InboxSummary(List<InboxItem> Items, int Unread);

/// <summary>
/// The notifications inbox (the bell) and phone notifications. Inbox: mark one read, dismiss one, mark all read, clear all.
/// Phone: the channel settings (saved like Setup's other settings, applied at once without a restart), a test send per channel,
/// Home Assistant's notify services, and the delivery log.
/// </summary>
public static class NotificationEndpoints
{
    public static WebApplication MapNotificationEndpoints(this WebApplication app)
    {
        app.MapGet("/api/inbox", (StateService state) => Inbox(state.Read(false)));
        app.MapPost("/api/inbox/{id}/read", async (string id, StateService state, CancellationToken ct) => await Change(state, s => NotificationInbox.MarkRead(s, id, DateTimeOffset.UtcNow), ct));
        app.MapPost("/api/inbox/{id}/dismiss", async (string id, StateService state, CancellationToken ct) => await Change(state, s => NotificationInbox.Dismiss(s, id, DateTimeOffset.UtcNow), ct));
        app.MapPost("/api/inbox/read-all", async (StateService state, CancellationToken ct) => await Change(state, s => NotificationInbox.MarkAllRead(s, DateTimeOffset.UtcNow), ct));
        app.MapPost("/api/inbox/dismiss-all", async (StateService state, CancellationToken ct) => await Change(state, s => NotificationInbox.DismissAll(s, DateTimeOffset.UtcNow), ct));

        app.MapGet("/api/push/settings", (NotificationService push, SavedSettings saved, AppAuthOptions auth) => View(push, saved, auth));
        app.MapPost("/api/push/settings", (PushSaveRequest request, HttpContext context, NotificationService push, SavedSettings saved, AppAuthOptions auth) =>
        {
            SetupConfigEndpoints.EnsureCanSave(saved, auth, context.Request);
            var values = request.Values ?? [];
            var allowed = PushCatalogue.Fields().Select(f => f.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (values.Keys.FirstOrDefault(k => !allowed.Contains(k)) is { } other) throw new DomainException($"{other} isn't a notification setting.", 400);
            if (values.Count > 0) saved.Save(values);
            return View(push, saved, auth);
        });
        app.MapPost("/api/push/test/{channel}", async (string channel, HttpContext context, NotificationService push, SavedSettings saved, AppAuthOptions auth, CancellationToken ct) =>
        {
            SetupConfigEndpoints.EnsureCanSave(saved, auth, context.Request);
            var result = await push.TestAsync(channel, ct);
            return new { ok = result.Ok, error = result.Error };
        });
        app.MapGet("/api/push/home-assistant/services", async (HttpContext context, NotificationService push, SavedSettings saved, AppAuthOptions auth, IHttpClientFactory clients, CancellationToken ct) =>
        {
            SetupConfigEndpoints.EnsureCanSave(saved, auth, context.Request);
            if (push.HomeAssistant is not { } target) return new { services = new List<string>(), error = (string?)"Joule needs Home Assistant's address and token first (Setup › Sensors)." };
            try { return new { services = await PushChannels.HomeAssistantServicesAsync(clients.CreateClient("notify"), target, ct), error = (string?)null }; }
            catch (DomainException e) { return new { services = new List<string>(), error = (string?)e.Message }; }
            catch (Exception e) when (e is HttpRequestException or IOException or System.Text.Json.JsonException || e is OperationCanceledException && !ct.IsCancellationRequested)
            { return new { services = new List<string>(), error = (string?)"Joule couldn't read Home Assistant's services." }; }
        });
        app.MapGet("/api/push/log", (NotificationService push) => push.Log());
        return app;
    }

    static async Task<InboxSummary> Change(StateService state, Action<AppState> edit, CancellationToken ct)
    {
        await state.MutateAsync(edit, ct);
        return Inbox(state.Read(false));
    }

    /// <summary>The bell's list: not dismissed, newest first, at most 50; Unread counts open, unread items only.</summary>
    public static InboxSummary Inbox(AppState presented) =>
        new(presented.Inbox.Where(i => i.DismissedAt is null).OrderByDescending(i => i.At).Take(50).ToList(), NotificationInbox.Unread(presented));

    public static PushSettingsView View(NotificationService push, SavedSettings saved, AppAuthOptions auth)
    {
        var settings = push.Settings;
        var ha = push.HomeAssistant;
        PushFieldView Field(string key, string label, string kind, bool secret, bool required, string? placeholder = null, string? note = null)
        {
            var value = settings.Get(key);
            return new(key, key.Replace(":", "__"), label, kind, secret, required, secret ? null : value, value is not null, saved.Source(key), placeholder, note);
        }
        var channels = PushCatalogue.Channels.Select(info =>
        {
            var c = settings.Channel(info);
            var problem = PushChannels.Problem(c, ha);
            return new PushChannelView(info.Id, info.Name, info.Description, c.Enabled, problem is null, problem,
                PushCatalogue.Events.Select(e => e.Id).Where(c.Events.Contains).ToList(), c.Quiet?.ToString(),
                Field(info.EnabledKey, "On", "bool", false, false), Field(info.EventsKey, "Send", "events", false, false), Field(info.QuietKey, "Quiet hours", "quietHours", false, false),
                info.Fields.Select(f => Field(f.Key, f.Label, f.Kind, f.Secret, f.Required, f.Placeholder, f.Note)).ToList());
        }).ToList();
        var general = new List<PushFieldView>
        {
            Field(PushCatalogue.PublicUrlKey, "Joule's address for links", "url", false, false, "https://joule.example.com", "Where you open Joule from your phone. Notifications then link straight to the item."),
            Field(PushCatalogue.OfflineMinutesKey, "Offline after (minutes)", "minutes", false, false, PushCatalogue.DefaultOfflineMinutes.ToString()),
            Field(PushCatalogue.SummaryTimeKey, "Daily summary at", "time", false, false, PushCatalogue.DefaultSummaryTime),
            Field(PushCatalogue.MaxPerHourKey, "At most (messages an hour, per channel)", "perHour", false, false, PushCatalogue.DefaultMaxPerHour.ToString()),
        };
        var locked = SetupConfigEndpoints.LockReasonFor(saved, auth);
        return new(locked is null, locked, ha is not null, settings.PublicUrl, settings.OfflineMinutes, settings.SummaryTime.ToString("HH:mm"), settings.MaxPerHour,
            general, PushCatalogue.Events.ToList(), channels, push.Log());
    }
}

public static class NotificationServiceRegistration
{
    /// <summary>The inbox sync and phone delivery worker. Its queue and log live in notifications.json (demo-notifications.json for the demo).</summary>
    public static IServiceCollection AddNotifications(this IServiceCollection services, IConfiguration configuration, string dataDirectory, bool demo)
    {
        services.AddSingleton(sp => new PushSettings(sp.GetRequiredService<SavedSettings>(), configuration));
        services.AddSingleton(sp =>
        {
            var factory = sp.GetRequiredService<IHttpClientFactory>();
            var options = sp.GetService<HomeAssistantOptions>();
            var settings = sp.GetRequiredService<PushSettings>();
            var sanitizer = new InvestigationReadSanitizer(configuration);
            var clock = sp.GetRequiredService<TimeProvider>();
            // Offline alerts count silence from no earlier than this, so time Joule itself was stopped never reads as Predbat being down.
            var started = clock.GetUtcNow();
            return new NotificationService(sp.GetRequiredService<StateService>(), settings, () => factory.CreateClient("notify"),
                Path.Combine(dataDirectory, demo ? "demo-notifications.json" : "notifications.json"), clock,
                () =>
                {
                    TelemetryStatus? telemetry = null;
                    try { telemetry = sp.GetService<TelemetryCollectionService>()?.Status(); } catch (Exception e) when (e is not OperationCanceledException) { }
                    return new InboxEnvironment(demo, sp.GetRequiredService<IPredbatClient>().Configured, telemetry, settings.OfflineMinutes, started);
                },
                () => options is { BaseUri: { } uri, AccessToken: { Length: > 0 } token } ? new HomeAssistantTarget(uri, token) : null,
                options?.TimeZone ?? "Europe/London", sp.GetRequiredService<ILogger<NotificationService>>(), sanitizer.Clean);
        });
        services.AddHostedService<NotificationWorker>();
        return services;
    }
}
