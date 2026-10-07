using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Joule;
using Xunit;

/// <summary>The bell's read/dismiss endpoints and the phone settings, against the real process (demo, no sign-in).</summary>
[Collection(ChildProcessCollection.Name)]
public class NotificationHttpTests
{
    static Dictionary<string, string?> NoAuth => new() { ["App__AuthMode"] = "None" };

    static async Task<HttpResponseMessage> Post(JouleProcess app, string path, object? body = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body ?? new { }) };
        request.Headers.Add("X-Joule-Request", "1");
        return await app.Http.SendAsync(request);
    }

    [Fact]
    public async Task TheBellCanBeReadDismissedAndClearedAndItSticks()
    {
        await using var app = new JouleProcess(NoAuth);
        Assert.True(await app.Start(), app.Log);
        // The worker fills the inbox shortly after start.
        InboxSummary? inbox = null;
        for (var i = 0; i < 60 && (inbox?.Items.Count ?? 0) == 0; i++)
        {
            inbox = await app.Http.GetFromJsonAsync<InboxSummary>("api/inbox", JsonDefaults.Options);
            if (inbox!.Items.Count == 0) await Task.Delay(500);
        }
        Assert.NotEmpty(inbox!.Items);
        Assert.Equal(inbox.Items.Count(i => i.Open && i.ReadAt is null), inbox.Unread);
        Assert.True(inbox.Unread > 0);

        // The state carries the same inbox for the browser.
        using (var state = JsonDocument.Parse(await app.Http.GetStringAsync("api/state")))
            Assert.Equal(JsonValueKind.Array, state.RootElement.GetProperty("state").GetProperty("inbox").ValueKind);

        var first = inbox.Items[0];
        var read = await (await Post(app, $"api/inbox/{first.Id}/read")).Content.ReadFromJsonAsync<InboxSummary>(JsonDefaults.Options);
        Assert.NotNull(read!.Items.Single(i => i.Id == first.Id).ReadAt);
        Assert.Equal(inbox.Unread - 1, read.Unread);

        var dismissed = await (await Post(app, $"api/inbox/{first.Id}/dismiss")).Content.ReadFromJsonAsync<InboxSummary>(JsonDefaults.Options);
        Assert.DoesNotContain(dismissed!.Items, i => i.Id == first.Id);

        var all = await (await Post(app, "api/inbox/read-all")).Content.ReadFromJsonAsync<InboxSummary>(JsonDefaults.Options);
        Assert.Equal(0, all!.Unread);
        Assert.NotEmpty(all.Items);
        var cleared = await (await Post(app, "api/inbox/dismiss-all")).Content.ReadFromJsonAsync<InboxSummary>(JsonDefaults.Options);
        Assert.Empty(cleared!.Items);
        Assert.Equal(HttpStatusCode.NotFound, (await Post(app, "api/inbox/nothing-here/read")).StatusCode);

        // Without the dashboard's header (another site's form), nothing changes.
        using var bare = await app.Http.PostAsync("api/inbox/read-all", JsonContent.Create(new { }));
        Assert.Equal(HttpStatusCode.Forbidden, bare.StatusCode);
    }

    [Fact]
    public async Task APublicDemoCantSetUpPhoneNotifications()
    {
        await using var app = new JouleProcess(NoAuth);
        Assert.True(await app.Start(), app.Log);
        Assert.Equal(HttpStatusCode.Forbidden, (await Post(app, "api/push/settings", new { values = new Dictionary<string, string?> { ["Notifications:Ntfy:Topic"] = "visitor" } })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Post(app, "api/push/test/Ntfy")).StatusCode);
        var view = await app.Http.GetFromJsonAsync<PushSettingsView>("api/push/settings", JsonDefaults.Options);
        Assert.False(view!.CanSave);
        Assert.NotNull(view.Locked);
    }

    [Fact]
    public async Task PhoneSettingsSaveWithoutARestartAndNeverSendSecretsBack()
    {
        await using var app = new JouleProcess(new(NoAuth) { ["App__Demo"] = "false" });
        Assert.True(await app.Start(), app.Log);
        var saved = await Post(app, "api/push/settings", new { values = new Dictionary<string, string?> { ["Notifications:Ntfy:Topic"] = "joule-made-up", ["Notifications:Ntfy:Token"] = "tk_made_up_secret", ["Notifications:Ntfy:Enabled"] = "true" } });
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        var text = await app.Http.GetStringAsync("api/push/settings");
        Assert.DoesNotContain("tk_made_up_secret", text);
        var view = JsonSerializer.Deserialize<PushSettingsView>(text, JsonDefaults.Options)!;
        var ntfy = view.Channels.Single(c => c.Id == "Ntfy");
        Assert.True(ntfy.Enabled);
        Assert.True(ntfy.Ready);
        Assert.Equal("joule-made-up", ntfy.Fields.Single(f => f.Key == "Notifications:Ntfy:Topic").Value);
        Assert.True(ntfy.Fields.Single(f => f.Key == "Notifications:Ntfy:Token").Set);
        Assert.Null(ntfy.Fields.Single(f => f.Key == "Notifications:Ntfy:Token").Value);
        Assert.DoesNotContain("tk_made_up_secret", await app.Http.GetStringAsync("api/setup/config"));

        // Only notification settings can be saved here.
        Assert.Equal(HttpStatusCode.BadRequest, (await Post(app, "api/push/settings", new { values = new Dictionary<string, string?> { ["Predbat:BaseUrl"] = "http://127.0.0.1:1" } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Post(app, "api/push/settings", new { values = new Dictionary<string, string?> { ["Notifications:Ntfy:Topic"] = "not a topic" } })).StatusCode);

        // A channel that isn't set up says what's missing instead of sending.
        using var test = await Post(app, "api/push/test/Telegram");
        using var result = JsonDocument.Parse(await test.Content.ReadAsStringAsync());
        Assert.False(result.RootElement.GetProperty("ok").GetBoolean());
        Assert.Contains("bot token", result.RootElement.GetProperty("error").GetString());
        var log = await app.Http.GetFromJsonAsync<List<PushLogEntry>>("api/push/log", JsonDefaults.Options);
        Assert.Contains(log!, l => l.Test && l.Status == "failed" && l.Channel == "Telegram");
    }
}
