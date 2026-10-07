using System.Net;
using System.Text;
using System.Text.Json;
using Joule;
using Microsoft.Extensions.Configuration;
using Xunit;
namespace Joule.Tests;

/// <summary>Answers every request with a scripted response and keeps what was sent. Nothing leaves the machine.</summary>
sealed class FakePushHandler(Func<HttpRequestMessage, HttpResponseMessage>? answer = null) : HttpMessageHandler
{
    public List<(HttpRequestMessage Request, string Body)> Sent { get; } = [];
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
        lock (Sent) Sent.Add((request, body));
        return answer?.Invoke(request) ?? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
    }
}

/// <summary>Each phone channel builds the request its service expects, and failures read as plain words without secrets. All values are made up.</summary>
public class PushChannelTests : IDisposable
{
    readonly string directory = Path.Combine(Path.GetTempPath(), "joule-push-" + Guid.NewGuid());
    public void Dispose() { try { Directory.Delete(directory, true); } catch (IOException) { } }

    PushSettings Settings(Dictionary<string, string?> values, Dictionary<string, string?>? environment = null)
    {
        var builder = new ConfigurationBuilder().AddInMemoryCollection(environment ?? []);
        var saved = SavedSettings.Attach(builder, builder.Build(), directory);
        if (values.Count > 0) saved.Save(values);
        return new PushSettings(saved, builder.Build());
    }
    PushChannelSettings Channel(string id, Dictionary<string, string?> values) => Settings(values).Channel(PushCatalogue.Channel(id)!);

    static readonly PushMessage Message = new("Joule · Suggestion", "Lower the battery reserve to 10%\nSaves about £3 a month", "https://joule.example.com/#/insights/suggestions", NotificationInbox.NeedsYou);
    static readonly HomeAssistantTarget Ha = new(new Uri("http://ha.local:8123/"), "ha-token-000");

    static async Task<(HttpRequestMessage Request, string Body, PushResult Result)> Send(PushChannelSettings channel, Func<HttpRequestMessage, HttpResponseMessage>? answer = null, HomeAssistantTarget? ha = null)
    {
        var handler = new FakePushHandler(answer);
        using var request = PushChannels.Build(channel, Message, ha);
        var result = await PushChannels.SendAsync(new HttpClient(handler), request, channel.Info.Name, default, PushChannels.ShowsReply(channel.Info));
        var (sent, body) = Assert.Single(handler.Sent);
        return (sent, body, result);
    }

    [Fact]
    public async Task NtfyPublishesJsonToTheServerRootWithTheTopicLinkAndToken()
    {
        var (request, body, result) = await Send(Channel("Ntfy", new() { ["Notifications:Ntfy:Topic"] = "joule-test-topic", ["Notifications:Ntfy:Token"] = "tk_madeup" }));
        Assert.True(result.Ok);
        Assert.Equal("https://ntfy.sh/", request.RequestUri!.ToString());
        Assert.Equal("Bearer tk_madeup", request.Headers.Authorization!.ToString());
        using var json = JsonDocument.Parse(body);
        Assert.Equal("joule-test-topic", json.RootElement.GetProperty("topic").GetString());
        Assert.Equal("Joule · Suggestion", json.RootElement.GetProperty("title").GetString());
        Assert.Contains("£3", json.RootElement.GetProperty("message").GetString());
        Assert.Equal(Message.Url, json.RootElement.GetProperty("click").GetString());
        var own = await Send(Channel("Ntfy", new() { ["Notifications:Ntfy:Topic"] = "t", ["Notifications:Ntfy:Url"] = "https://push.example.com/ntfy", ["Notifications:Ntfy:Token"] = null }));
        Assert.Equal("https://push.example.com/ntfy/", own.Request.RequestUri!.ToString());
        Assert.Null(own.Request.Headers.Authorization);
    }

    [Fact]
    public async Task PushoverPostsTheFormWithItsLink()
    {
        var (request, body, result) = await Send(Channel("Pushover", new() { ["Notifications:Pushover:UserKey"] = "u-madeup", ["Notifications:Pushover:AppToken"] = "a-madeup" }));
        Assert.True(result.Ok);
        Assert.Equal(PushChannels.PushoverApi, request.RequestUri);
        var form = body.Split('&').Select(p => p.Split('=')).ToDictionary(p => p[0], p => Uri.UnescapeDataString(p[1].Replace('+', ' ')));
        Assert.Equal("a-madeup", form["token"]);
        Assert.Equal("u-madeup", form["user"]);
        Assert.Equal("Joule · Suggestion", form["title"]);
        Assert.Equal(Message.Url, form["url"]);
    }

    [Fact]
    public async Task HomeAssistantCallsTheNotifyServiceWithItsOwnTokenAndAPhoneLink()
    {
        var channel = Channel("HomeAssistant", new() { ["Notifications:HomeAssistant:Service"] = "notify.mobile_app_test_phone" });
        Assert.Contains("Home Assistant's address and token", PushChannels.Problem(channel, null));
        var (request, body, result) = await Send(channel, ha: Ha);
        Assert.True(result.Ok);
        Assert.Equal("http://ha.local:8123/api/services/notify/mobile_app_test_phone", request.RequestUri!.ToString());
        Assert.Equal("Bearer ha-token-000", request.Headers.Authorization!.ToString());
        using var json = JsonDocument.Parse(body);
        Assert.Equal(Message.Url, json.RootElement.GetProperty("data").GetProperty("url").GetString());
        Assert.Equal(Message.Url, json.RootElement.GetProperty("data").GetProperty("clickAction").GetString());
    }

    [Fact]
    public async Task HomeAssistantServicesArePickedFromItsServiceList()
    {
        var handler = new FakePushHandler(_ => new(HttpStatusCode.OK) { Content = new StringContent("""
            [{"domain":"light","services":{"turn_on":{}}},{"domain":"notify","services":{"persistent_notification":{},"mobile_app_test_phone":{},"send_message":{}}}]
            """) });
        var services = await PushChannels.HomeAssistantServicesAsync(new HttpClient(handler), Ha, default);
        Assert.Equal(["notify.mobile_app_test_phone", "notify.persistent_notification", "notify.send_message"], services);
        Assert.Equal("http://ha.local:8123/api/services", handler.Sent[0].Request.RequestUri!.ToString());
    }

    [Fact]
    public async Task TelegramSendsToTheChatAndNeverRepeatsTheTokenInAnError()
    {
        var channel = Channel("Telegram", new() { ["Notifications:Telegram:BotToken"] = "123456:madeup-token", ["Notifications:Telegram:ChatId"] = "-1001234" });
        var (request, body, result) = await Send(channel);
        Assert.True(result.Ok);
        Assert.Equal("https://api.telegram.org/bot123456:madeup-token/sendMessage", request.RequestUri!.ToString());
        using var json = JsonDocument.Parse(body);
        Assert.Equal("-1001234", json.RootElement.GetProperty("chat_id").GetString());
        Assert.Contains(Message.Url!, json.RootElement.GetProperty("text").GetString());
        var refused = await Send(channel, _ => new(HttpStatusCode.BadRequest) { Content = new StringContent("""{"ok":false,"description":"Bad Request: chat not found"}""") });
        Assert.False(refused.Result.Ok);
        Assert.False(refused.Result.Retry);
        Assert.Contains("chat not found", refused.Result.Error);
        var notOk = await Send(channel, _ => new(HttpStatusCode.OK) { Content = new StringContent("""{"ok":false,"description":"Forbidden: bot was blocked by the user"}""") });
        Assert.False(notOk.Result.Ok);
        Assert.Contains("blocked", notOk.Result.Error);
        var down = await Send(channel, _ => throw new HttpRequestException("connect failed https://api.telegram.org/bot123456:madeup-token", null, null));
        Assert.True(down.Result.Retry);
        Assert.DoesNotContain("madeup-token", down.Result.Error);
    }

    [Theory]
    [InlineData("https://discord.com/api/webhooks/1/madeup", "content")]
    [InlineData("https://hooks.slack.com/services/T0/B0/madeup", "text")]
    [InlineData("https://chat.example.com/hooks/madeup", "text")]
    public async Task ChatWebhooksUseDiscordsOrSlacksShape(string url, string field)
    {
        var (request, body, result) = await Send(Channel("Chat", new() { ["Notifications:Chat:WebhookUrl"] = url }));
        Assert.True(result.Ok);
        Assert.Equal(url, request.RequestUri!.ToString());
        using var json = JsonDocument.Parse(body);
        Assert.Contains("Lower the battery reserve", json.RootElement.GetProperty(field).GetString());
    }

    [Fact]
    public async Task TheJsonWebhookCarriesTheEventAndAnOptionalBearerToken()
    {
        var (request, body, _) = await Send(Channel("Webhook", new() { ["Notifications:Webhook:Url"] = "https://example.com/hook?key=madeup", ["Notifications:Webhook:Token"] = "wh-madeup" }));
        Assert.Equal("https://example.com/hook?key=madeup", request.RequestUri!.ToString());
        Assert.Equal("Bearer wh-madeup", request.Headers.Authorization!.ToString());
        using var json = JsonDocument.Parse(body);
        Assert.Equal("joule", json.RootElement.GetProperty("source").GetString());
        Assert.Equal("needs_you", json.RootElement.GetProperty("event").GetString());
        Assert.Equal(Message.Url, json.RootElement.GetProperty("url").GetString());
    }

    [Theory]
    [InlineData("Webhook", "Notifications:Webhook:Url")]
    [InlineData("Chat", "Notifications:Chat:WebhookUrl")]
    public async Task AnyAddressChannelsNeverEchoTheServersReply(string id, string key)
    {
        // These post to any address you give, so the Test button mustn't become a way to read another server's answers.
        var (_, _, result) = await Send(Channel(id, new() { [key] = "https://example.com/hook" }),
            _ => new(HttpStatusCode.BadRequest) { Content = new StringContent("""{"error":"internal detail from the far side"}""") });
        Assert.False(result.Ok);
        Assert.Contains("HTTP 400", result.Error);
        Assert.DoesNotContain("far side", result.Error);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, false, "refused the token")]
    [InlineData(HttpStatusCode.NotFound, false, "wasn't found")]
    [InlineData(HttpStatusCode.TooManyRequests, true, "slow down")]
    [InlineData(HttpStatusCode.BadGateway, true, "HTTP 502")]
    public async Task FailuresSayWhatHappenedAndWhetherToTryAgain(HttpStatusCode status, bool retry, string words)
    {
        var (_, _, result) = await Send(Channel("Ntfy", new() { ["Notifications:Ntfy:Topic"] = "t" }), _ => new(status) { Content = new StringContent("""{"error":"made-up reason"}""") });
        Assert.False(result.Ok);
        Assert.Equal(retry, result.Retry);
        Assert.Contains(words, result.Error);
        Assert.Contains("made-up reason", result.Error);
    }

    [Fact]
    public void AChannelWithoutItsRequiredFieldsSaysWhatsMissing()
    {
        var channel = Channel("Pushover", new() { ["Notifications:Pushover:UserKey"] = "u" });
        Assert.Equal("Add the application API token.", PushChannels.Problem(channel, null));
        Assert.Equal("Add the user key and application API token.", PushChannels.Problem(Channel("Pushover", new() { ["Notifications:Pushover:UserKey"] = null }), null));
        Assert.Throws<DomainException>(() => PushChannels.Build(channel, Message, null));
    }

    [Fact]
    public void SettingsAreValidatedLiveAndTheEnvironmentWins()
    {
        var saved = SavedSettings.Attach(new ConfigurationBuilder(), new ConfigurationBuilder().Build(), directory);
        Assert.Throws<DomainException>(() => saved.Save(new Dictionary<string, string?> { ["Notifications:Ntfy:Topic"] = "has spaces" }));
        Assert.Throws<DomainException>(() => saved.Save(new Dictionary<string, string?> { ["Notifications:Ntfy:Events"] = "needs_you,everything" }));
        Assert.Throws<DomainException>(() => saved.Save(new Dictionary<string, string?> { ["Notifications:Ntfy:QuietHours"] = "late" }));
        Assert.Throws<DomainException>(() => saved.Save(new Dictionary<string, string?> { ["Notifications:OfflineMinutes"] = "1" }));
        Assert.Throws<DomainException>(() => saved.Save(new Dictionary<string, string?> { ["Notifications:Telegram:ChatId"] = "me" }));
        saved.Save(new Dictionary<string, string?> { ["Notifications:Ntfy:Topic"] = "ok_topic-1", ["Notifications:Ntfy:Events"] = "none", ["Notifications:Ntfy:QuietHours"] = "22:00-07:00", ["Notifications:HomeAssistant:Service"] = "notify.mobile_app_x", ["Notifications:Pushover:AppToken"] = "secret-madeup" });

        var settings = Settings([], new() { ["Notifications:Ntfy:Topic"] = "from-env" });
        var channel = settings.Channel(PushCatalogue.Channel("Ntfy")!);
        Assert.Equal("from-env", channel.Value("Notifications:Ntfy:Topic"));
        Assert.Empty(channel.Events);
        Assert.Equal(new QuietHours(new(22, 0), new(7, 0)), channel.Quiet);
        // Saved notification settings apply without a restart, so Setup never shows them as waiting for one.
        var view = SetupConfigEndpoints.View(SavedSettings.Attach(new ConfigurationBuilder(), new ConfigurationBuilder().Build(), directory), new AppAuthOptions(false, true, null, TimeSpan.FromDays(30), "'none'"), false);
        Assert.False(view.RestartPending);
        var secret = view.Fields.Single(f => f.Key == "Notifications:Pushover:AppToken");
        Assert.True(secret.Set);
        Assert.Null(secret.Value);
    }

    [Theory]
    [InlineData("22:00-07:00", "23:30", true)]
    [InlineData("22:00-07:00", "06:59", true)]
    [InlineData("22:00-07:00", "07:00", false)]
    [InlineData("13:00-14:00", "13:30", true)]
    [InlineData("13:00-14:00", "12:00", false)]
    [InlineData("08:00-08:00", "08:00", false)]
    public void QuietHoursMayCrossMidnight(string window, string at, bool quiet)
    {
        Assert.True(QuietHours.TryParse(window, out var q));
        Assert.Equal(quiet, q.Contains(TimeOnly.Parse(at)));
    }

    [Fact]
    public void QuietHoursEndInTheHouseholdsTimeZone()
    {
        QuietHours.TryParse("22:00-07:00", out var q);
        var london = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");
        // 23:30 BST on 6 October is 22:30 UTC; quiet ends at 07:00 BST, 06:00 UTC.
        Assert.Equal(new DateTimeOffset(2026, 10, 7, 6, 0, 0, TimeSpan.Zero), q.EndAfter(new DateTimeOffset(2026, 10, 6, 22, 30, 0, TimeSpan.Zero), london));
    }
}
