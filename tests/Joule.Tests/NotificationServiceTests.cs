using System.Net;
using System.Text.Json;
using Joule;
using Microsoft.Extensions.Configuration;
using Xunit;
namespace Joule.Tests;

/// <summary>The delivery worker: what is sent, when, how often, and what the log says. Channels talk to a fake handler only.</summary>
public class NotificationServiceTests : IDisposable
{
    readonly string directory = Path.Combine(Path.GetTempPath(), "joule-notify-" + Guid.NewGuid());
    public void Dispose() { try { Directory.Delete(directory, true); } catch (IOException) { } }

    sealed class NoPredbat : IPredbatClient
    {
        public bool Configured => true;
        public bool WritesEnabled => false;
        public Task<LiveSnapshot> CollectAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task ApplyAsync(List<Change> changes, List<Setting> settings, CancellationToken ct = default) => throw new NotImplementedException();
    }

    sealed class Rig : IDisposable
    {
        public required DataStore Db { get; init; }
        public required StateService State { get; init; }
        public required NotificationService Service { get; init; }
        public required ManualClock Clock { get; init; }
        public required FakePushHandler Handler { get; init; }
        public required SavedSettings Saved { get; init; }
        public Task Tick() => Service.TickAsync(default);
        public List<JsonElement> Bodies() => Handler.Sent.Select(s => JsonDocument.Parse(s.Body).RootElement.Clone()).ToList();
        public void Dispose() => Db.Dispose();
    }

    Rig Make(Dictionary<string, string?> values, Func<HttpRequestMessage, HttpResponseMessage>? answer = null, DateTimeOffset? start = null, InboxEnvironment? env = null)
    {
        var builder = new ConfigurationBuilder();
        var saved = SavedSettings.Attach(builder, builder.Build(), directory);
        saved.Save(new Dictionary<string, string?>(values) { ["Notifications:Ntfy:Enabled"] = values.GetValueOrDefault("Notifications:Ntfy:Enabled") ?? "true", ["Notifications:Ntfy:Topic"] = "joule-test" });
        var clock = new ManualClock(start ?? DateTimeOffset.UtcNow);
        var db = new DataStore(Path.Combine(directory, "db"));
        var state = new StateService(db, new NoPredbat(), true);
        var handler = new FakePushHandler(answer);
        var service = new NotificationService(state, new PushSettings(saved, builder.Build()), () => new HttpClient(handler), Path.Combine(directory, "notifications.json"), clock,
            () => env ?? new InboxEnvironment(true, true, null, 30), () => null, "Europe/London");
        return new Rig { Db = db, State = state, Service = service, Clock = clock, Handler = handler, Saved = saved };
    }

    static Task AddProposal(StateService state, string title) => state.MutateAsync(s => s.Proposals.Add(new Proposal { Title = title, Status = "Pending", CreatedAt = DateTimeOffset.UtcNow }));

    [Fact]
    public async Task WhatWasAlreadyThereIsNotSentButEachNewThingIsSentOnce()
    {
        using var rig = Make(new() { ["App:PublicUrl"] = "https://joule.example.com/" });
        await rig.Tick();
        // The first pass fills the bell from what's already there without buzzing the phone.
        Assert.Empty(rig.Handler.Sent);
        Assert.NotEmpty(rig.State.Read(false).Inbox);
        await AddProposal(rig.State, "Raise the export threshold");
        await rig.Tick();
        await rig.Tick();
        var body = Assert.Single(rig.Bodies());
        Assert.Equal("Joule · Suggestion", body.GetProperty("title").GetString());
        Assert.StartsWith("Raise the export threshold", body.GetProperty("message").GetString());
        Assert.Equal("https://joule.example.com/#/insights/suggestions", body.GetProperty("click").GetString());
        var log = rig.Service.Log();
        Assert.Equal("sent", log[0].Status);
        Assert.Equal("ntfy", log[0].ChannelName);
    }

    [Fact]
    public async Task OnlyTheChosenEventsAreSent()
    {
        // 03:00 in London: long before the summary is due.
        using var rig = Make(new() { ["Notifications:Ntfy:Events"] = "offline,summary" }, start: new DateTimeOffset(2026, 10, 7, 2, 0, 0, TimeSpan.Zero));
        await rig.Tick();
        await AddProposal(rig.State, "Not wanted on this channel");
        await rig.Tick();
        Assert.Empty(rig.Handler.Sent);
        // Still in the bell.
        Assert.Contains(rig.State.Read(false).Inbox, i => i.Title == "Not wanted on this channel");
    }

    [Fact]
    public async Task QuietHoursHoldMessagesAndDropWhatYouHandledMeanwhile()
    {
        // 23:00 in London (BST) on a made-up night.
        var night = new DateTimeOffset(2026, 10, 6, 22, 0, 0, TimeSpan.Zero);
        using var rig = Make(new() { ["Notifications:Ntfy:QuietHours"] = "22:00-07:00" }, start: night);
        await rig.Tick();
        await AddProposal(rig.State, "Handled before morning");
        await AddProposal(rig.State, "Still waiting in the morning");
        await rig.Tick();
        Assert.Empty(rig.Handler.Sent);
        Assert.All(rig.Service.Log().Where(l => !l.Test).Take(2), l => Assert.Equal("held", l.Status));
        await rig.State.MutateAsync(s => s.Proposals.Single(p => p.Title == "Handled before morning").Status = "Denied");
        rig.Clock.Now = new DateTimeOffset(2026, 10, 7, 6, 1, 0, TimeSpan.Zero);
        await rig.Tick();
        var body = Assert.Single(rig.Bodies());
        Assert.StartsWith("Still waiting in the morning", body.GetProperty("message").GetString());
        Assert.Contains(rig.Service.Log(), l => l.Status == "skipped" && l.Error!.Contains("handled"));
    }

    [Fact]
    public async Task TheHourlyLimitBatchesTheRestIntoOneMessage()
    {
        using var rig = Make(new() { ["Notifications:MaxPerHour"] = "1" });
        await rig.Tick();
        await AddProposal(rig.State, "First");
        await rig.Tick();
        await AddProposal(rig.State, "Second");
        await AddProposal(rig.State, "Third");
        await rig.Tick();
        Assert.Single(rig.Handler.Sent);
        rig.Clock.Now = rig.Clock.Now.AddMinutes(61);
        await rig.Tick();
        Assert.Equal(2, rig.Handler.Sent.Count);
        var batch = rig.Bodies()[1];
        Assert.Equal("Joule · 2 notifications", batch.GetProperty("title").GetString());
        Assert.Contains("Second", batch.GetProperty("message").GetString());
        Assert.Contains("Third", batch.GetProperty("message").GetString());
    }

    [Fact]
    public async Task FailuresAreRetriedWithBackoffThenGivenUp()
    {
        var calls = 0;
        using var rig = Make([], _ => ++calls < 3 ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) : new HttpResponseMessage(HttpStatusCode.OK));
        await rig.Tick();
        await AddProposal(rig.State, "Retry me");
        await rig.Tick();
        Assert.Equal("retrying", rig.Service.Log()[0].Status);
        await rig.Tick(); // not due yet
        Assert.Equal(1, calls);
        rig.Clock.Now = rig.Clock.Now.AddMinutes(2);
        await rig.Tick();
        Assert.Equal(2, calls);
        rig.Clock.Now = rig.Clock.Now.AddMinutes(6);
        await rig.Tick();
        Assert.Equal(3, calls);
        Assert.Equal("sent", rig.Service.Log()[0].Status);
        Assert.Equal(3, rig.Service.Log()[0].Attempts);

        using var refused = Make([], _ => new HttpResponseMessage(HttpStatusCode.Forbidden));
        await refused.Tick();
        await AddProposal(refused.State, "Never accepted");
        await refused.Tick();
        Assert.Equal("failed", refused.Service.Log()[0].Status);
        Assert.Contains("refused the token", refused.Service.Log()[0].Error);
    }

    [Fact]
    public async Task TheDailySummaryGoesOnceAtTheChosenTime()
    {
        // 07:50 in London (BST).
        var morning = new DateTimeOffset(2026, 10, 7, 6, 50, 0, TimeSpan.Zero);
        using var rig = Make(new() { ["Notifications:Ntfy:Events"] = "summary", ["Notifications:SummaryTime"] = "08:00", ["Notifications:Ntfy:QuietHours"] = "22:00-09:00" }, start: morning);
        await rig.Tick();
        Assert.Empty(rig.Handler.Sent);
        rig.Clock.Now = morning.AddMinutes(11);
        await rig.Tick();
        await rig.Tick();
        // Quiet hours don't hold the summary: you chose its time.
        var body = Assert.Single(rig.Bodies());
        Assert.Equal("Joule · Daily summary", body.GetProperty("title").GetString());
        Assert.StartsWith("Needs you: ", body.GetProperty("message").GetString());
        rig.Clock.Now = morning.AddHours(2);
        await rig.Tick();
        Assert.Single(rig.Handler.Sent);
    }

    [Fact]
    public async Task ATestSendIgnoresTheSwitchAndIsLogged()
    {
        using var rig = Make(new() { ["Notifications:Ntfy:Enabled"] = "false" });
        var result = await rig.Service.TestAsync("Ntfy", default);
        Assert.True(result.Ok);
        var body = Assert.Single(rig.Bodies());
        Assert.Equal("Joule · Test", body.GetProperty("title").GetString());
        Assert.Contains("App__PublicUrl", body.GetProperty("message").GetString());
        Assert.True(rig.Service.Log()[0].Test);
        var missing = await rig.Service.TestAsync("Telegram", default);
        Assert.False(missing.Ok);
        Assert.Contains("bot token", missing.Error);
        await Assert.ThrowsAsync<DomainException>(() => rig.Service.TestAsync("Carrier pigeon", default));
    }

    [Fact]
    public async Task TheQueueAndLogSurviveARestart()
    {
        using (var rig = Make([], _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)))
        {
            await rig.Tick();
            await AddProposal(rig.State, "Survives");
            await rig.Tick();
        }
        var builder = new ConfigurationBuilder();
        var saved = SavedSettings.Attach(builder, builder.Build(), directory);
        using var db = new DataStore(Path.Combine(directory, "db"));
        var again = new NotificationService(new StateService(db, new NoPredbat(), true), new PushSettings(saved, builder.Build()), () => new HttpClient(new FakePushHandler()),
            Path.Combine(directory, "notifications.json"), TimeProvider.System, () => new InboxEnvironment(true, true, null, 30), () => null, "Europe/London");
        Assert.Equal("retrying", again.Log()[0].Status);
    }
}
