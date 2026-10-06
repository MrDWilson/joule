using Stopwatch = System.Diagnostics.Stopwatch;
using Microsoft.Extensions.Configuration;
using Joule;
using Xunit;

/// <summary>AppState retention: old history moves to DuckDB, stays readable through the paged endpoints, and reads stay fast.</summary>
public class StateRetentionTests : IDisposable
{
    readonly string path = Path.Combine(Path.GetTempPath(), "joule-retention-" + Guid.NewGuid().ToString("N"));
    static readonly IConfiguration NoConfig = new ConfigurationBuilder().Build();
    public void Dispose() { try { Directory.Delete(path, true); } catch (IOException) { } }

    sealed class ReadOnlyPredbat : IPredbatClient
    {
        public bool Configured => false;
        public bool WritesEnabled => false;
        public Task<LiveSnapshot> CollectAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task ApplyAsync(List<Change> changes, List<Setting> settings, CancellationToken ct = default) => throw new NotSupportedException();
    }

    static readonly string Paragraph = string.Concat(Enumerable.Repeat("The battery charged to 80% overnight while the plan expected 95%; the cheap window was shorter than forecast. ", 6));

    /// <summary>Realistically sized synthetic investigations (about 2 KB of prose each), one every <paramref name="spacing"/>, newest last.</summary>
    static List<Investigation> Synthetic(int count, TimeSpan spacing, DateTimeOffset newest) => Enumerable.Range(0, count).Select(n => new Investigation
    {
        Id = $"inv-{n:D6}",
        At = newest - spacing * (count - 1 - n),
        Title = $"Overnight charge #{n} fell short",
        Summary = Paragraph,
        Category = "Battery",
        Steps = ["Read the plan", "Compared state of charge", "Checked the tariff window"],
        Evidence = ["SoC 80% at 05:30", "Plan target 95%"],
        Provider = "Demo",
        Verdict = n % 3 == 0 ? "no_change" : "problem",
    }).ToList();

    [Fact]
    public void OldInvestigationsActivitiesAndUsageMoveToTheArchive()
    {
        var now = DateTimeOffset.UtcNow;
        using var db = new DataStore(path);
        var state = new AppState { DataSource = "Live" };
        state.Investigations = Synthetic(400, TimeSpan.FromHours(6), now.AddHours(-1));
        // An old investigation that is still open, and one referenced by a pending proposal, stay live.
        state.Investigations[0].NextSteps.Add(new InvestigationNextStep { Id = "open-step", Title = "Check the inverter limit" });
        state.Proposals.Add(new Proposal { Title = "Raise charge rate", InvestigationId = state.Investigations[1].Id, CreatedAt = now.AddDays(-200) });
        state.Activities = Enumerable.Range(0, 900).Select(n => new Activity(now.AddMinutes(-900 + n), "analysis", $"step {n}")).ToList();
        state.Usage = Enumerable.Range(0, 120).Select(n => new UsageRecord(now.AddDays(-n), "Api", "fixture", 1000 + n, 10, 0.01, n % 10 == 0 ? "Failed" : "Completed")).ToList();

        db.Save(state);

        Assert.Equal(RetentionPolicy.Investigations + 2, state.Investigations.Count);
        Assert.Contains(state.Investigations, i => i.Id == "inv-000000");
        Assert.Contains(state.Investigations, i => i.Id == "inv-000001");
        Assert.Equal(400 - state.Investigations.Count, db.ArchivedInvestigationCount);
        Assert.Equal(RetentionPolicy.Activities, state.Activities.Count);
        Assert.Equal("step 899", state.Activities[^1].Message);
        Assert.All(state.Usage, u => Assert.True(u.At >= now - RetentionPolicy.Usage));

        // Persisted state is the trimmed one, and archived rows are still reachable.
        var reloaded = db.Load(false)!;
        Assert.Equal(state.Investigations.Count, reloaded.Investigations.Count);
        Assert.Equal(RetentionPolicy.Activities, reloaded.Activities.Count);
        Assert.Equal("Overnight charge #5 fell short", db.ReadArchivedInvestigation("inv-000005")!.Title);
        Assert.Equal(900 - RetentionPolicy.Activities, db.ReadArchivedActivities(null, null, 1000, false).Count);

        // Saving again is idempotent: nothing is archived twice.
        db.Save(state);
        Assert.Equal(400 - state.Investigations.Count, db.ArchivedInvestigationCount);
    }

    [Fact]
    public void RecentInvestigationsAreKeptEvenWhenThereAreMany()
    {
        var now = DateTimeOffset.UtcNow;
        using var db = new DataStore(path);
        var state = new AppState { DataSource = "Live", Investigations = Synthetic(350, TimeSpan.FromMinutes(30), now) };
        db.Save(state);
        // 350 runs in ~7 days are all inside the 8-day floor that the weekly report reads.
        Assert.Equal(350, state.Investigations.Count);
        Assert.Equal(0, db.ArchivedInvestigationCount);
    }

    [Fact]
    public void PagingWalksLiveAndArchivedInvestigationsNewestFirstWithoutGapsOrRepeats()
    {
        var now = DateTimeOffset.UtcNow;
        using var db = new DataStore(path);
        var state = new AppState { DataSource = "Live", Investigations = Synthetic(450, TimeSpan.FromHours(8), now) };
        db.Save(state);
        Assert.True(db.ArchivedInvestigationCount > 0);
        var sanitizer = new InvestigationReadSanitizer(NoConfig);
        var seen = new List<InvestigationListItem>(); string? cursor = null; var pages = 0;
        do
        {
            var page = StateEndpoints.Investigations(state, db, sanitizer, cursor, 37, null, null);
            Assert.Equal(450, page.Total);
            seen.AddRange(page.Items); cursor = page.NextCursor; pages++;
        } while (cursor is not null && pages < 50);
        Assert.Equal(450, seen.Count);
        Assert.Equal(450, seen.Select(x => x.Id).Distinct().Count());
        Assert.Equal(seen.OrderByDescending(x => x.At).Select(x => x.Id), seen.Select(x => x.Id));
        Assert.Contains(seen, x => x.Archived);
        Assert.Contains(seen, x => !x.Archived);
        Assert.All(seen, x => Assert.False(string.IsNullOrEmpty(x.Headline)));
        // Filters work across both sources.
        var quiet = StateEndpoints.Investigations(state, db, sanitizer, null, 100, "no_change", null);
        Assert.Equal(100, quiet.Items.Count);
        Assert.All(quiet.Items, x => Assert.Equal("no_change", x.Verdict));
        Assert.Equal(-1, quiet.Total);
        Assert.Throws<DomainException>(() => StateEndpoints.Investigations(state, db, sanitizer, "%%%", 10, null, null));
    }

    [Fact]
    public void ArchivedInvestigationsAreFoundByIdWithEvidence()
    {
        var now = DateTimeOffset.UtcNow;
        using var db = new DataStore(path);
        var investigations = Synthetic(260, TimeSpan.FromDays(1), now);
        investigations[0].ToolEvidence.Add(new ToolEvidence("tool-1", "sql", "SELECT 1", now.AddDays(-259), true, "{\"rows\":1}", []));
        var state = new AppState { DataSource = "Live", Investigations = investigations };
        db.Save(state);
        Assert.DoesNotContain(state.Investigations, i => i.Id == "inv-000000");
        var archived = StateEndpoints.ReadArchived(db, NoConfig, "inv-000000")!;
        Assert.Equal("tool-1", Assert.Single(archived.ToolEvidence).Id);
        Assert.Null(StateEndpoints.ReadArchived(db, NoConfig, "missing"));
    }

    [Fact]
    public void ActivitiesPageForwardForProgressAndBackwardForHistory()
    {
        var now = DateTimeOffset.UtcNow;
        using var db = new DataStore(path);
        var state = new AppState { DataSource = "Live", Activities = Enumerable.Range(0, 800).Select(n => new Activity(now.AddMinutes(-800 + n), n % 2 == 0 ? "analysis" : "configuration", $"a{n}")).ToList() };
        db.Save(state);
        var latest = StateEndpoints.Activities(state, db, null, null, 50, null);
        Assert.Equal(Enumerable.Range(750, 50).Select(n => $"a{n}"), latest.Items.Select(a => a.Message));
        Assert.True(latest.More);
        // Back through history, across the archive boundary (the oldest 300 are archived).
        var older = StateEndpoints.Activities(state, db, null, latest.Oldest, 400, null);
        Assert.Equal(Enumerable.Range(350, 400).Select(n => $"a{n}"), older.Items.Select(a => a.Message));
        var oldest = StateEndpoints.Activities(state, db, null, older.Oldest, 500, null);
        Assert.Equal(Enumerable.Range(0, 350).Select(n => $"a{n}"), oldest.Items.Select(a => a.Message));
        Assert.False(oldest.More);
        // Live progress: only what happened after the last one seen.
        var since = StateEndpoints.Activities(state, db, state.Activities[^3].At, null, 50, null);
        Assert.Equal(["a798", "a799"], since.Items.Select(a => a.Message));
        var fromArchive = StateEndpoints.Activities(state, db, now.AddMinutes(-800 + 289).AddSeconds(1), null, 20, "analysis");
        Assert.Equal(["a290", "a292", "a294"], fromArchive.Items.Take(3).Select(a => a.Message));
    }

    [Fact]
    public void UsageCombinesArchiveAndLiveByHouseholdDay()
    {
        var now = new DateTimeOffset(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);
        using var db = new DataStore(path);
        // 23:30 UTC on 4 Oct is 00:30 on 5 Oct in London (BST).
        var state = new AppState
        {
            DataSource = "Live",
            Usage = [new(now.AddDays(-60), "Api", "m", 10, 1, 0.5, "Completed"), new(new(2026, 10, 4, 23, 30, 0, TimeSpan.Zero), "Api", "m", 100, 10, 0.25, "Completed"), new(now.AddHours(-1), "ChatGpt", "m", 200, 20, null, "Failed")]
        };
        // Retention uses the real clock; archive explicitly for this fixed-date test.
        db.Save(state);
        var report = StateEndpoints.Usage(state, db, 90, "Europe/London", now);
        Assert.Equal(3, report.Runs);
        Assert.Equal(1, report.Failed);
        Assert.False(report.CostComplete);
        Assert.Equal(0.75, report.EstimatedUsd);
        Assert.Equal(["2026-08-06", "2026-10-05"], report.Daily.Select(d => d.Date));
        Assert.Equal(2, report.Daily[^1].Runs);
        var week = StateEndpoints.Usage(state, db, 7, "Europe/London", now);
        Assert.Equal(2, week.Runs);
        Assert.Equal(new DateTimeOffset(2026, 9, 28, 23, 0, 0, TimeSpan.Zero), week.From);
        Assert.Equal(new DateTimeOffset(2026, 10, 5, 23, 0, 0, TimeSpan.Zero), week.To);
    }

    [Fact]
    public void EvidenceQueriesCannotReadTheArchive()
    {
        using var db = new DataStore(path);
        foreach (var table in new[] { "archived_investigations", "archived_activities", "archived_usage" })
            Assert.Throws<DomainException>(() => db.Query($"SELECT * FROM {table}"));
    }

    [Fact]
    public void ReadingStateWithTenThousandInvestigationsStaysUnderFiftyMilliseconds()
    {
        var now = DateTimeOffset.UtcNow;
        using (var seed = new DataStore(path))
        {
            var state = new AppState { DataSource = "Live", Investigations = Synthetic(10_000, TimeSpan.FromHours(1), now) };
            state.Activities = Enumerable.Range(0, 5000).Select(n => new Activity(now.AddMinutes(-5000 + n), "analysis", $"mcp: step {n} " + new string('x', 200))).ToList();
            seed.Save(state);
            Assert.True(seed.ArchivedInvestigationCount > 9000);
        }
        using var db = new DataStore(path);
        var service = new StateService(db, new ReadOnlyPredbat(), false);
        for (var i = 0; i < 3; i++) service.Read(false); // warm up JIT and caches
        var timings = Enumerable.Range(0, 9).Select(_ => { var sw = Stopwatch.StartNew(); service.Read(false); return sw.Elapsed.TotalMilliseconds; }).Order().ToList();
        Assert.True(timings[timings.Count / 2] < 50, $"median read {timings[timings.Count / 2]:F1} ms");
        var sanitizer = new InvestigationReadSanitizer(NoConfig);
        var deep = Stopwatch.StartNew();
        var page = StateEndpoints.Investigations(service.Read(false), db, sanitizer, StateEndpoints.EncodeCursor(now.AddDays(-300), "inv-999999"), 20, null, null);
        Assert.Equal(20, page.Items.Count);
        Assert.True(page.Items.All(x => x.Archived));
        Assert.True(deep.ElapsedMilliseconds < 500, $"deep page {deep.ElapsedMilliseconds} ms");
    }
}
