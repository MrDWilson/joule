using Joule;
using Xunit;
namespace Joule.Tests;

/// <summary>
/// The bell's inbox. The old bell derived its list from the state on every render and kept no read state on the server: the
/// "Needs you" row always counted as new, reports only stopped counting once opened on the Reports page, and findings were
/// "seen" only in one browser's localStorage. So the badge could never be cleared. These pin the server-side inbox that replaced it.
/// </summary>
public class NotificationInboxTests
{
    static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    static AppState Demo() => DemoData.Create();

    static AppState Synced(AppState s, DateTimeOffset? at = null)
    {
        NotificationInbox.Sync(s, NotificationInbox.Collect(s, at ?? Now), at ?? Now, false);
        NotificationInbox.Present(s, at ?? Now);
        return s;
    }

    [Fact]
    public void EachThingThatNeedsYouIsItsOwnNotificationAndSyncingAgainAddsNothing()
    {
        var s = Synced(Demo());
        Assert.Contains(s.Inbox, i => i.Event == NotificationInbox.NeedsYou && i.Label == "Suggestion" && i.Link == "#/insights/suggestions");
        Assert.Contains(s.Inbox, i => i.Label == "File edit");
        Assert.Contains(s.Inbox, i => i.Label == "To-do");
        Assert.Contains(s.Inbox, i => i.Event == NotificationInbox.Problem && i.Link.StartsWith("#/insights/inv/"));
        // A no-change check is not news.
        Assert.DoesNotContain(s.Inbox, i => i.Title.Contains("Solar forecast tracks"));
        var count = s.Inbox.Count;
        Assert.True(NotificationInbox.Unread(s) > 0);
        var added = NotificationInbox.Sync(s, NotificationInbox.Collect(s, Now.AddMinutes(1)), Now.AddMinutes(1), false);
        Assert.Empty(added);
        Assert.Equal(count, s.Inbox.Count);
    }

    [Fact]
    public void MarkAllAsReadClearsTheBadgeEvenWhileThingsStillNeedYou()
    {
        var s = Synced(Demo());
        NotificationInbox.MarkAllRead(s, Now);
        NotificationInbox.Present(s, Now);
        Assert.Equal(0, NotificationInbox.Unread(s));
        // Still listed (they still need you), just not new.
        Assert.Contains(s.Inbox, i => i.Open && i.DismissedAt is null);
        // And a later sync doesn't bring them back as new.
        NotificationInbox.Sync(s, NotificationInbox.Collect(s, Now.AddMinutes(5)), Now.AddMinutes(5), false);
        NotificationInbox.Present(s, Now.AddMinutes(5));
        Assert.Equal(0, NotificationInbox.Unread(s));
    }

    [Fact]
    public void HandlingTheThingDropsItFromTheBadgeAtOnce()
    {
        var s = Synced(Demo());
        var before = NotificationInbox.Unread(s);
        var proposal = s.Proposals.First(p => p.Status == "Pending");
        var item = s.Inbox.Single(i => i.Key == $"proposal:{proposal.Id}");
        // Turned down in Insights: no sync needed for the badge to drop, Present sees it.
        proposal.Status = "Denied";
        NotificationInbox.Present(s, Now);
        Assert.False(item.Open);
        Assert.Equal(before - 1, NotificationInbox.Unread(s));
        // The next sync records when it was handled.
        NotificationInbox.Sync(s, NotificationInbox.Collect(s, Now), Now, false);
        Assert.NotNull(item.ResolvedAt);
    }

    [Fact]
    public void DismissingOneOrClearingAllSticksAndNothingIsRecreated()
    {
        var s = Synced(Demo());
        var first = s.Inbox[0];
        NotificationInbox.Dismiss(s, first.Id, Now);
        Assert.NotNull(first.DismissedAt);
        Assert.NotNull(first.ReadAt);
        NotificationInbox.DismissAll(s, Now);
        NotificationInbox.Sync(s, NotificationInbox.Collect(s, Now.AddHours(1)), Now.AddHours(1), false);
        NotificationInbox.Present(s, Now.AddHours(1));
        Assert.All(s.Inbox, i => Assert.NotNull(i.DismissedAt));
        Assert.Equal(0, NotificationInbox.Unread(s));
        Assert.Empty(NotificationEndpoints.Inbox(s).Items);
        Assert.Throws<DomainException>(() => NotificationInbox.MarkRead(s, "gone", Now));
    }

    [Fact]
    public void ARepeatedToDoIsOneNotification()
    {
        var s = Demo();
        var step = s.Investigations.SelectMany(i => i.NextSteps).First();
        var later = new Investigation { At = Now.AddMinutes(1), Verdict = "no_change", NextSteps = [new InvestigationNextStep { Id = "again", Title = "  " + step.Title.ToUpperInvariant() + " " }] };
        s.Investigations.Add(later);
        Synced(s);
        Assert.Single(s.Inbox, i => i.Label == "To-do" && string.Equals(i.Title.Trim(), step.Title.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ChecksThatKeepFailingAreOneNotificationResolvedWhenACheckFinishes()
    {
        var s = new AppState();
        s.Investigations.Add(new Investigation { Id = "a", At = Now.AddHours(-2), Status = "Failed", FailureKind = "timeout", Headline = "The AI service timed out" });
        Synced(s);
        Assert.DoesNotContain(s.Inbox, i => i.Event == NotificationInbox.Unfinished);
        s.Investigations.Add(new Investigation { Id = "b", At = Now.AddHours(-1), Status = "Failed", FailureKind = "provider_busy", Headline = "The AI service was busy" });
        Synced(s);
        var item = Assert.Single(s.Inbox, i => i.Event == NotificationInbox.Unfinished);
        Assert.Equal("warn", item.Tone);
        Assert.Contains("2 tries in a row", item.Detail);
        Assert.Contains("busy", item.Detail);
        // A third try updates the same notification rather than adding one.
        s.Investigations.Single(i => i.Id == "b").Attempts = 2;
        Synced(s);
        Assert.Contains("3 tries", Assert.Single(s.Inbox, i => i.Event == NotificationInbox.Unfinished).Detail);
        s.Investigations.Add(new Investigation { Id = "c", At = Now, Status = "Completed", Verdict = "no_change" });
        Synced(s);
        Assert.NotNull(item.ResolvedAt);
        Assert.False(item.Open);
    }

    [Fact]
    public void OfflineAlertsComeAfterTheChosenMinutesAndClearWhenBack()
    {
        var s = new AppState { DataSource = "Live", LastCollection = Now.AddMinutes(-20) };
        var env = new InboxEnvironment(false, true, null, 30);
        NotificationInbox.Sync(s, NotificationInbox.Collect(s, Now, env), Now, true);
        Assert.Empty(s.Inbox);
        NotificationInbox.Sync(s, NotificationInbox.Collect(s, Now.AddMinutes(15), env), Now.AddMinutes(15), true);
        var item = Assert.Single(s.Inbox);
        Assert.Equal("Joule can't reach Predbat", item.Title);
        Assert.Equal(NotificationInbox.Offline, item.Event);
        // A sync without the environment (nothing known about time) leaves it alone.
        NotificationInbox.Sync(s, NotificationInbox.Collect(s, Now.AddMinutes(16)), Now.AddMinutes(16), false);
        Assert.Null(item.ResolvedAt);
        s.LastCollection = Now.AddMinutes(17);
        NotificationInbox.Sync(s, NotificationInbox.Collect(s, Now.AddMinutes(18), env), Now.AddMinutes(18), true);
        Assert.NotNull(item.ResolvedAt);
        // The demo never reports itself offline.
        Assert.DoesNotContain(NotificationInbox.Collect(new AppState { LastCollection = Now.AddDays(-1) }, Now, env with { Demo = true }), c => c.Event == NotificationInbox.Offline);
    }

    [Fact]
    public void SilenceIsCountedFromNoEarlierThanJoulesOwnStart()
    {
        var s = new AppState { DataSource = "Live", LastCollection = Now.AddHours(-2) };
        // Up ten minutes: the two hours before were Joule being off, not Predbat.
        Assert.Empty(NotificationInbox.Collect(s, Now, new InboxEnvironment(false, true, null, 30, Now.AddMinutes(-10))));
        var c = Assert.Single(NotificationInbox.Collect(s, Now, new InboxEnvironment(false, true, null, 30, Now.AddMinutes(-40))));
        Assert.Equal($"offline:predbat:{Now.AddMinutes(-40).ToUnixTimeSeconds()}", c.Key);
        Assert.Equal("No answer for 30 minutes or more.", c.Detail);
        // The wording stays the same as the outage goes on.
        Assert.Equal(c, Assert.Single(NotificationInbox.Collect(s, Now.AddMinutes(20), new InboxEnvironment(false, true, null, 30, Now.AddMinutes(-40)))));
    }

    [Fact]
    public void HomeAssistantFailingSinceStartIsReportedOnceTheLimitPasses()
    {
        // After a restart nothing has been read yet; Home Assistant has failed every time since.
        var telemetry = new TelemetryStatus(false, true, "Europe/London", null, "Home Assistant refused the token.", [], [], 15, []);
        Assert.Empty(NotificationInbox.Collect(new AppState(), Now, new InboxEnvironment(false, false, telemetry, 30, Now.AddMinutes(-10))));
        var c = Assert.Single(NotificationInbox.Collect(new AppState(), Now, new InboxEnvironment(false, false, telemetry, 30, Now.AddMinutes(-35))));
        Assert.Equal("Joule can't read your sensors", c.Title);
        // Without an error (still starting up) nothing is said.
        Assert.Empty(NotificationInbox.Collect(new AppState(), Now, new InboxEnvironment(false, false, telemetry with { Error = null }, 30, Now.AddMinutes(-35))));
    }

    [Fact]
    public void ASensorThatStopsReportingIsNamedInWords()
    {
        var telemetry = new TelemetryStatus(false, true, "Europe/London", Now, null, [], [], 15, []) { Issues = [new("grid_import", "offline for 45 min", Now.AddMinutes(-45))] };
        var candidates = NotificationInbox.Collect(new AppState(), Now, new InboxEnvironment(false, false, telemetry, 30));
        var c = Assert.Single(candidates);
        Assert.Equal("Grid import sensor isn't reporting", c.Title);
        Assert.Equal("#/setup/sensors", c.Link);
    }

    [Fact]
    public void ReadingAReportNotificationReadsTheReportsNotificationToo()
    {
        var s = new AppState();
        s.Reports.Add(new EnergyReport { Id = "r1", Title = "Daily report · Sun 4 Oct" });
        s.Notifications.Add(new InAppNotification { Title = "Daily report · Sun 4 Oct", Message = "You used 12 kWh", ReportId = "r1", At = Now });
        Synced(s);
        var item = Assert.Single(s.Inbox);
        Assert.Equal("#/energy/reports?report=r1", item.Link);
        NotificationInbox.MarkRead(s, item.Id, Now);
        Assert.NotNull(s.Notifications[0].ReadAt);
        NotificationInbox.Present(s, Now);
        Assert.Equal(0, NotificationInbox.Unread(s));
    }

    [Fact]
    public void OldFindingsArentAddedAndUndismissingAFindingBringsItBack()
    {
        var s = new AppState();
        s.Investigations.Add(new Investigation { Id = "old", At = Now.AddDays(-3), Status = "Completed", Verdict = "problem", Title = "Old" });
        s.Investigations.Add(new Investigation { Id = "new", At = Now.AddHours(-1), Status = "Completed", Verdict = "problem", Title = "New", Severity = "action" });
        Synced(s);
        var item = Assert.Single(s.Inbox);
        Assert.Equal("finding:new", item.Key);
        s.Investigations.Single(i => i.Id == "new").DismissedAt = Now;
        Synced(s);
        Assert.NotNull(item.ResolvedAt);
        s.Investigations.Single(i => i.Id == "new").DismissedAt = null;
        Synced(s);
        Assert.Null(item.ResolvedAt);
        Assert.True(item.Open);
    }

    [Fact]
    public void HandledItemsAreDroppedAfterTwoWeeks()
    {
        var s = Synced(Demo());
        foreach (var p in s.Proposals) p.Status = "Denied";
        NotificationInbox.Sync(s, NotificationInbox.Collect(s, Now), Now, false);
        var later = Now + NotificationInbox.KeepClosed + TimeSpan.FromHours(1);
        NotificationInbox.Sync(s, NotificationInbox.Collect(s, later), later, false);
        Assert.DoesNotContain(s.Inbox, i => i.Key.StartsWith("proposal:"));
    }
}
