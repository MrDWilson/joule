using Joule;
using Xunit;

namespace Joule.Tests;

/// <summary>The running demo's seeded history, scripted answers and wording agree with themselves.</summary>
public sealed class DemoContentTests : IDisposable
{
    readonly string path = Path.Combine(Path.GetTempPath(), "predbat-demo-content-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { if (Directory.Exists(path)) Directory.Delete(path, true); }

    [Fact]
    public void SampleHistoryCarriesAKeptAndARunningTrialWithMatchingRevisions()
    {
        var s = DemoData.Create(history: true);
        var kept = Assert.Single(s.Experiments, e => e.Status == "Kept");
        var running = Assert.Single(s.Experiments, e => e.Status == "Running");
        Assert.All(s.Experiments, e => Assert.True(e.Seeded));
        Assert.Equal("battery_loss", Assert.Single(s.Revisions.Single(r => r.Id == kept.RevisionId).Changes).Key);
        var change = Assert.Single(s.Revisions.Single(r => r.Id == running.RevisionId).Changes);
        Assert.Equal(("pv_scaling", "0.95"), (change.Key, change.After));
        Assert.Equal("0.95", s.Settings.Single(x => x.Key == "pv_scaling").Value);
        Assert.True(running.ReviewAt > DateTimeOffset.UtcNow && running.StartedAt < DateTimeOffset.UtcNow.AddDays(-3));
        Assert.True(running.BaselineForecastCoverage >= .99 && running.CurrentForecastCoverage >= .99);
        Assert.NotNull(running.CurrentCostGbpPerDay); Assert.False(string.IsNullOrWhiteSpace(running.Result));
        // The pending suggestion builds on the latest revision, was made a minute after its finding, and shares its confidence.
        var proposal = s.Proposals.Single();
        var finding = s.Investigations.Single(i => i.Id == proposal.InvestigationId);
        Assert.Equal(s.Revision, proposal.BaseRevision);
        Assert.Equal(finding.At.AddMinutes(1), proposal.CreatedAt);
        Assert.Equal(finding.Confidence, proposal.Confidence);
        Assert.DoesNotContain("saving is an estimate", finding.Summary);
        Assert.Equal(2, s.Claims.Count);
    }

    [Fact]
    public void SampleChecksAreAutomaticAndCountTowardsTodaysAllowance()
    {
        var s = DemoData.Create(history: true); var now = DateTimeOffset.UtcNow;
        Assert.True(s.Ai.Scheduled);
        Assert.All(s.Investigations, i => Assert.True(i.Request.Scheduled));
        var failed = s.Investigations.Single(i => i.Status == "Failed");
        Assert.True(failed.At < now.AddDays(-1));
        var zone = DemoHouse.Zone; var today = TimeZoneInfo.ConvertTime(now, zone).Date;
        var checksToday = s.Investigations.Count(i => TimeZoneInfo.ConvertTime(i.At, zone).Date == today);
        Assert.Equal(checksToday, InvestigationScheduler.RunsToday(s.Usage, now, zone));
        Assert.All(s.Usage, u => Assert.Equal(DemoData.SampleModel, u.Model));
    }

    [Fact]
    public void ExportCheckSaysJouleStillReadsTheMeterAndItsToDoIsAboutExport()
    {
        var check = DemoData.Create().Investigations.Single(i => i.FileChanges.Count > 0);
        Assert.Contains("Joule still reads the export meter", check.Summary);
        var todo = Assert.Single(check.NextSteps);
        Assert.Contains("export", todo.Title, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("EV", todo.Title);
    }

    [Fact]
    public void AskJouleAnswersTheStarterQuestionsFromTheSampleMeters()
    {
        using var db = new DataStore(path);
        var now = CivilTime.FirstValidInstant(new DateTime(2026, 10, 5, 19, 30, 0), DemoHouse.Zone);
        DemoTelemetry.Seed(db, now);
        var yesterday = DemoAnswers.Answer("Why was yesterday expensive?", db, DemoHouse.Zone, now);
        Assert.Equal("You asked: “Why was yesterday expensive?”", yesterday.Headline);
        Assert.StartsWith("Sunday cost", yesterday.Title);
        Assert.StartsWith("Sunday ", yesterday.Plain); Assert.Contains("7-day average", yesterday.Plain); Assert.DoesNotContain("cost −", yesterday.Plain);
        var night = DemoAnswers.Answer("Did last night's charge go to plan?", db, DemoHouse.Zone, now);
        Assert.StartsWith("Yes.", night.Plain); Assert.Equal("Last night's charge went to plan", night.Title); Assert.Contains("at 05:30", night.Plain); Assert.Contains("kWh at an average 7.0p/kWh", night.Plain);
        var reserve = DemoAnswers.Answer("Is my battery reserve right?", db, DemoHouse.Zone, now);
        Assert.Contains("The reserve is 4%", reserve.Plain);
        var other = DemoAnswers.Answer("Should I get a heat pump?", db, DemoHouse.Zone, now);
        Assert.Equal("You asked: “Should I get a heat pump?”", other.Headline);
        Assert.Contains("You asked: “Should I get a heat pump?”", other.Summary);
        foreach (var answer in new[] { yesterday, night, reserve, other })
        {
            Assert.InRange(answer.Plain.Length, 1, InvestigationQuality.PlainLimit);
            Assert.DoesNotContain("LLM", answer.Summary);
        }
        Assert.DoesNotContain("LLM", DemoAnswers.Answer(null, db, DemoHouse.Zone, now).Summary);
    }

    [Theory]
    [InlineData("Charge 23:30–05:30 local at 7p.", "Charge 23:30–05:30 at 7p.")]
    [InlineData("Export from 16:10 local time.", "Export from 16:10.")]
    [InlineData("The window 20:30-23:30 (local) ran on battery.", "The window 20:30-23:30 ran on battery.")]
    [InlineData("A local supplier.", "A local supplier.")]
    public void TimesDropTheLocalSuffix(string text, string expected) => Assert.Equal(expected, InvestigationQuality.Sanitise(text, DemoHouse.Zone));

    [Fact]
    public void ShellMessagesAreOneShortSentenceWithoutANextTry()
    {
        var zone = DemoHouse.Zone; var now = CivilTime.FirstValidInstant(new DateTime(2026, 10, 5, 19, 8, 0), zone);
        var restart = InvestigationQuality.Describe(new OperationCanceledException(), "ChatGpt", false, true, zone, now);
        Assert.Equal("Stopped when Joule restarted at 19:08. Nothing was changed.", InvestigationQuality.ShellMessage(restart, now, zone));
        var busy = new InvestigationQuality.FailureDescription("Failed", "provider_busy", "ChatGPT didn't answer (server busy). Joule tried 6 times over 13 min. Nothing was changed.", null, null, true, null);
        var shell = InvestigationQuality.ShellMessage(busy, now, zone);
        Assert.Equal("ChatGPT didn't answer (server busy). Nothing was changed.", shell);
        Assert.DoesNotContain("Next try", shell); Assert.DoesNotContain("kept below", shell);
    }

    [Fact]
    public void MinimumImprovementSettingsArePencePerKwh()
    {
        foreach (var key in new[] { "metric_min_improvement", "metric_min_improvement_export" })
        {
            var setting = DemoData.Create().Settings.Single(x => x.Key == key);
            Assert.Equal("p/kWh", setting.Unit);
        }
    }
}
