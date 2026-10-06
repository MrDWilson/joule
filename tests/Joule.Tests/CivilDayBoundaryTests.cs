using Joule;
using Xunit;

namespace Joule.Tests;

public sealed class CivilDayBoundaryTests : IDisposable
{
    readonly string directory = Path.Combine(Path.GetTempPath(), "predbat-civil-days-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("America/Santiago", "2026-09-07T12:00:00Z", "2026-09-06T04:00:00Z", "2026-09-07T03:00:00Z")]
    [InlineData("America/Havana", "2026-11-02T12:00:00Z", "2026-11-01T04:00:00Z", "2026-11-02T05:00:00Z")]
    [InlineData("Pacific/Apia", "2011-12-30T22:00:00Z", "2011-12-29T10:00:00Z", "2011-12-30T10:00:00Z")]
    public void CompletedDailyReportUsesTheWholeLatestExistingCivilDay(string zone, string now, string from, string to)
    {
        var period = ReportService.CompletedPeriod("Daily", DateTimeOffset.Parse(now), zone);
        Assert.Equal(DateTimeOffset.Parse(from), period.From);
        Assert.Equal(DateTimeOffset.Parse(to), period.To);
    }

    [Theory]
    [InlineData("America/Santiago", "2026-09-06T04:00:00Z", "2026-09-07T03:00:00Z")]
    [InlineData("America/Havana", "2026-11-01T04:00:00Z", "2026-11-02T05:00:00Z")]
    public void DailyEvidenceKeepsAllObservedTimeAcrossMidnightTransitions(string zone, string from, string to)
    {
        using var db = new DataStore(directory);
        var summaries = db.GetDailySummaries(DateTimeOffset.Parse(from), DateTimeOffset.Parse(to), zone);
        var day = Assert.Single(summaries);
        Assert.Equal(DateTimeOffset.Parse(from), day.From);
        Assert.Equal(DateTimeOffset.Parse(to), day.To);
    }

    [Fact]
    public void DailyEvidenceOmitsANonexistentCivilDateWithoutDroppingAdjacentDays()
    {
        using var db = new DataStore(directory);
        var summaries = db.GetDailySummaries(DateTimeOffset.Parse("2011-12-29T10:00:00Z"), DateTimeOffset.Parse("2011-12-31T10:00:00Z"), "Pacific/Apia");
        Assert.Equal(2, summaries.Count);
        Assert.Equal(DateTimeOffset.Parse("2011-12-29T10:00:00Z"), summaries[0].From);
        Assert.Equal(DateTimeOffset.Parse("2011-12-30T10:00:00Z"), summaries[0].To);
        Assert.Equal(summaries[0].To, summaries[1].From);
        Assert.Equal(DateTimeOffset.Parse("2011-12-31T10:00:00Z"), summaries[1].To);
    }

    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
}
