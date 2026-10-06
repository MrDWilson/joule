using Joule;
using Xunit;
namespace Joule.Tests;

public sealed class ReportServiceTests : IDisposable
{
    readonly string directory = Path.Combine(Path.GetTempPath(), "predbat-reports-" + Guid.NewGuid().ToString("N"));
    static readonly TimeZoneInfo London = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");
    sealed class NoWriter : IPredbatClient
    {
        public bool Configured => false;
        public bool WritesEnabled => false;
        public Task<LiveSnapshot> CollectAsync(CancellationToken ct = default) => throw new InvalidOperationException();
        public Task ApplyAsync(List<Change> c, List<Setting> s, CancellationToken ct = default) => throw new InvalidOperationException();
    }
    static EnergyMetricSummary Metric(double? kwh, double coverage = 1, params EnergyGap[] gaps) => new(kwh, 3600, coverage, 0) { Gaps = gaps.ToList(), State = "complete" };
    /// <summary>The live 5 Oct shape at 09:53: import £1.80, export £0.70, net £1.10, the car on the house meter.</summary>
    static EnergySummary LiveMorning(DateTimeOffset from, DateTimeOffset to, params EnergyGap[] exportGaps) => new(from, to, new()
    {
        ["load"] = Metric(16.908), ["pv"] = Metric(8.344), ["grid_import"] = Metric(26.962), ["grid_export"] = Metric(6.3336, 1, exportGaps),
        ["battery_charge"] = Metric(23.599), ["battery_discharge"] = Metric(11.535), ["ev"] = Metric(4.585),
    }, 1.8007, 0.6970, 0.0639, 0.744, 3600, ["HomeAssistant"], ["Engine caveat that must never reach a person."])
    { NetCostGbp = 1.1037, ImportCostCoverage = 1, ExportCostCoverage = 1, LoadIncludesEv = true };

    StateService Live(DataStore db) { db.Save(new AppState { DataSource = "Live" }); return new StateService(db, new NoWriter(), false); }
    static List<TelemetrySample> LoadSamples(DateTimeOffset from, params double[] values) =>
        values.Select((v, i) => new TelemetrySample("load", "sensor.house", from.AddMinutes(5 * i), v, "kWh", "HA fixture", v.ToString(System.Globalization.CultureInfo.InvariantCulture), "kWh")).ToList();

    [Fact] public void SummaryLeadsWithTheHomeUseTileWhenTheHouseMeterIncludesTheCar()
    {
        var from = new DateTimeOffset(2026, 10, 3, 23, 0, 0, TimeSpan.Zero); var to = from.AddDays(1);
        var summary = LiveMorning(from, to) with { Home = Metric(12.323) };
        var text = ReportService.Describe(summary, from, to, London, demo: false);
        // The page's tile says Home use 12.3 kWh and Car charging 4.6 kWh; the summary uses the same two figures.
        Assert.StartsWith("Your home used 12.3 kWh and the car 4.6 kWh. Solar made 8.3 kWh.", text);
        Assert.DoesNotContain("16.9", text);
    }
    [Fact] public void SummaryReadsAsSentencesWithTheSameRoundingAsThePage()
    {
        var from = new DateTimeOffset(2026, 10, 4, 23, 0, 0, TimeSpan.Zero); var to = new DateTimeOffset(2026, 10, 5, 8, 53, 0, TimeSpan.Zero);
        var text = ReportService.Describe(LiveMorning(from, to), from, to, London, demo: false);
        Assert.Equal("From midnight to 09:53, you used 16.9 kWh, 4.6 kWh of it charging the car. Solar made 8.3 kWh. " +
            "You bought 27.0 kWh from the grid for £1.80 and exported 6.3 kWh, earning £0.70. The battery took in 23.6 kWh and gave back 11.5 kWh. " +
            "Net cost £1.10. Standing charges aren't included.", text);
        Assert.DoesNotContain("coverage", text); Assert.DoesNotContain("grid_import", text); Assert.DoesNotContain("caveat", text); Assert.DoesNotContain("£0.06", text);
    }
    [Fact] public void SummaryNamesLongGapsInLocalTimeAndSaysAboutWhenCostIsIncomplete()
    {
        var from = new DateTimeOffset(2026, 10, 3, 23, 0, 0, TimeSpan.Zero); var to = from.AddDays(1);
        var summary = LiveMorning(from, to, new EnergyGap(from, from.AddMinutes(167), "offline", 0.62), new EnergyGap(to.AddSeconds(-1), to, "no_samples")) with { ExportCostCoverage = 0.88 };
        var text = ReportService.Describe(summary, from, to, London, demo: false);
        Assert.StartsWith("You used 16.9 kWh", text);
        Assert.Contains("Net cost about £1.10.", text);
        Assert.Contains("The export meter was offline 00:00–02:47 (0.6 kWh in that time isn't counted).", text);
        Assert.DoesNotContain("wasn't read", text); // a one-second gap at the edge is not worth a sentence
        var earnings = ReportService.Describe(summary with { NetCostGbp = -2.5, ExportCostCoverage = 1 }, from, to, London, false);
        Assert.Contains("Net earnings £2.50.", earnings);
    }
    [Fact] public void EnergyFromAMeterThatMissedAStretchReadsAsAtLeastAndSpreadEnergyAsAbout()
    {
        var from = new DateTimeOffset(2026, 10, 3, 23, 0, 0, TimeSpan.Zero); var to = from.AddDays(1);
        var summary = LiveMorning(from, to);
        summary.Metrics["load"] = Metric(16.908, 0.9, new EnergyGap(from.AddHours(11), from.AddHours(13.5), "offline")) with { State = "partial" };
        summary.Metrics["pv"] = Metric(8.344) with { State = "estimated", EstimatedKwh = 1.2 };
        var text = ReportService.Describe(summary, from, to, London, demo: false);
        Assert.StartsWith("You used at least 16.9 kWh, 4.6 kWh of it charging the car. Solar made about 8.3 kWh.", text);
        Assert.Contains("You bought 27.0 kWh from the grid", text);
        Assert.Equal("You used at least 16.9 kWh · net cost £1.10", ReportService.Headline(summary));
    }
    [Fact] public void EmptyAndDemoSummariesArePlain()
    {
        var from = new DateTimeOffset(2026, 9, 30, 23, 0, 0, TimeSpan.Zero); var to = from.AddDays(1);
        var empty = new EnergySummary(from, to, new() { ["load"] = Metric(null, 0) }, null, null, null, 0, 0, [], []);
        Assert.Equal("No meter readings for this period.", ReportService.Describe(empty, from, to, London, false));
        Assert.StartsWith("Demo figures from made-up readings. You used", ReportService.Describe(LiveMorning(from, to), from, to, London, true));
    }
    [Fact] public void TitlesSayWhatThePeriodIsInLocalTime()
    {
        var midnight = new DateTimeOffset(2026, 10, 4, 23, 0, 0, TimeSpan.Zero); var morning = new DateTimeOffset(2026, 10, 5, 8, 53, 0, TimeSpan.Zero);
        // A snapshot of part of a day names its date and end time, so it reads the same today, tomorrow and in the bell.
        Assert.Equal("Report · Mon 5 Oct, to 09:53", ReportService.Title("Daily", midnight, morning, morning, London));
        Assert.Equal("Report · Mon 5 Oct, to 09:53", ReportService.Title("Daily", midnight, morning, morning.AddDays(1), London));
        Assert.Equal("Report · Mon 5 Oct, to 09:53", ReportService.NotificationTitle("Daily", midnight, morning, London));
        Assert.Equal("Daily report · Sun 4 Oct", ReportService.NotificationTitle("Daily", midnight.AddDays(-1), midnight, London));
        Assert.Equal("Daily report · Sun 4 Oct", ReportService.Title("Daily", midnight.AddDays(-1), midnight, morning, London));
        Assert.Equal("Weekly report · 28 Sep – 4 Oct", ReportService.Title("Weekly", new(2026, 9, 27, 23, 0, 0, TimeSpan.Zero), midnight, morning, London));
        Assert.Equal("Report · 2 Oct – 4 Oct", ReportService.Title("Custom", new(2026, 10, 1, 23, 0, 0, TimeSpan.Zero), midnight, morning, London));
        Assert.Equal("Daily report · Thu 1 Oct", ReportService.Title("Daily", new(2026, 9, 30, 23, 0, 0, TimeSpan.Zero), new(2026, 10, 1, 23, 0, 0, TimeSpan.Zero), morning, London));
    }
    [Fact] public async Task ReportsMadeByHandStoreOnlyThePeriodRaiseNoNotificationAndRecomputeWhenOpened()
    {
        using var db = new DataStore(directory); var state = Live(db); var service = new ReportService(state, db);
        var from = DateTimeOffset.UtcNow.AddDays(-20); var to = from.AddHours(1);
        db.SaveTelemetry(LoadSamples(from, 10, 10.2));
        var report = await service.GenerateAsync(new("Daily", from, to));
        Assert.False(report.IsDemo); Assert.Null(report.EnergySummary); Assert.Empty(report.Days); Assert.Equal(ReportService.CurrentTextVersion, report.TextVersion);
        Assert.Contains("Your home used at least 0.2 kWh.", report.Summary);
        Assert.Empty(state.Read().Notifications);
        Assert.Equal(report.Id, (await service.GenerateAsync(new("Daily", from, to))).Id); Assert.Single(state.Read().Reports);
        // Later readings (or later accounting fixes) reach the report when it is opened.
        db.SaveTelemetry(LoadSamples(from.AddMinutes(10), 10.5));
        var view = service.View(report.Id);
        Assert.Equal(.5, view.EnergySummary.Metrics["load"].EnergyKwh!.Value, 6); Assert.Contains("Your home used at least 0.5 kWh.", view.Summary); Assert.True(view.HasReadings);
        Assert.Throws<DomainException>(() => service.View("missing"));
        await service.MarkReadAsync(report.Id); Assert.NotNull(db.Load()!.Reports.Single().ReadAt);
    }
    [Fact] public async Task EmptyPeriodsGetNoReportAndNoNotification()
    {
        using var db = new DataStore(directory); var state = Live(db); var service = new ReportService(state, db);
        var manual = await Assert.ThrowsAsync<DomainException>(() => service.GenerateAsync(new()));
        Assert.Equal("There are no meter readings for this period, so there's nothing to report.", manual.Message);
        await service.GenerateDueAsync(DateTimeOffset.UtcNow.Date.AddHours(12));
        Assert.Empty(state.Read().Reports); Assert.Empty(state.Read().Notifications);
        await Assert.ThrowsAsync<DomainException>(() => service.GenerateAsync(new("Weekly", DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1))));
        await Assert.ThrowsAsync<DomainException>(() => service.GenerateAsync(new("Custom")));
    }
    [Fact] public async Task ScheduledReportsNotifyOnceWithAHeadlineAndDoNotDuplicateOnRestart()
    {
        using var db = new DataStore(directory); var state = new StateService(db, new NoWriter(), true); var service = new ReportService(state, db);
        await state.MutateAsync(s => s.ReportPreferences = new ReportPreferences { DailyEnabled = true, WeeklyEnabled = false, HourLocal = 8, TimeZone = "Europe/London", DefaultsVersion = 1 });
        var now = new DateTimeOffset(2026, 9, 28, 10, 0, 0, TimeSpan.Zero);
        var yesterday = ReportService.CompletedPeriod("Daily", now, "Europe/London");
        db.SaveTelemetry(LoadSamples(yesterday.From.AddHours(10), 10, 10.4).Select(s => s with { Source = "Demo", EntityId = "demo.load" }).ToList());
        await service.GenerateDueAsync(now); await new ReportService(new StateService(db, new NoWriter(), true), db).GenerateDueAsync(now);
        var report = Assert.Single(db.Load()!.Reports); var notification = Assert.Single(db.Load()!.Notifications);
        Assert.True(report.IsDemo); Assert.Equal("Daily report · Sun 27 Sep", report.Title); Assert.Equal(report.Title, notification.Title);
        Assert.Equal("You used at least 0.4 kWh", notification.Message); Assert.StartsWith("Demo figures from made-up readings.", report.Summary);
    }
    [Fact] public async Task DailyReportsAreOnByDefaultAndAUsersChoiceSticks()
    {
        Assert.True(new ReportPreferences().DailyEnabled);
        using var db = new DataStore(directory); var state = Live(db); var service = new ReportService(state, db);
        await state.MutateAsync(s => s.ReportPreferences = new ReportPreferences { DailyEnabled = false });   // saved before the default changed
        await service.RepairAsync(DateTimeOffset.UtcNow);
        Assert.True(state.Read(false).ReportPreferences.DailyEnabled);
        await service.SavePreferencesAsync(new ReportPreferences { DailyEnabled = false, TimeZone = "Europe/London", HourLocal = 8 });
        await new ReportService(new StateService(db, new NoWriter(), false), db).RepairAsync(DateTimeOffset.UtcNow);
        Assert.False(db.Load()!.ReportPreferences.DailyEnabled);
        await Assert.ThrowsAsync<DomainException>(() => service.SavePreferencesAsync(new ReportPreferences { HourLocal = 24 }));
    }
    [Theory]
    [InlineData(true, 8, false)]   // a weekly report chosen: these preferences were saved, so daily off was a choice
    [InlineData(false, 7, false)]  // a different hour chosen
    [InlineData(false, 8, true)]   // scheduled reports were delivered before, then daily was turned off
    public async Task AnOffThatWasClearlyChosenBeforeTheUpgradeIsKept(bool weekly, int hour, bool delivered)
    {
        using var db = new DataStore(directory); var state = Live(db); var service = new ReportService(state, db);
        await state.MutateAsync(s =>
        {
            s.ReportPreferences = new ReportPreferences { DailyEnabled = false, WeeklyEnabled = weekly, HourLocal = hour };
            if (delivered) s.Notifications.Add(new InAppNotification { Title = "Daily report · Sun 4 Oct", ReportId = "earlier" });
        });
        await service.RepairAsync(DateTimeOffset.UtcNow);
        var prefs = db.Load()!.ReportPreferences;
        Assert.False(prefs.DailyEnabled); Assert.Equal(1, prefs.DefaultsVersion);
        // And it stays off on every later start.
        await new ReportService(new StateService(db, new NoWriter(), false), db).RepairAsync(DateTimeOffset.UtcNow);
        Assert.False(db.Load()!.ReportPreferences.DailyEnabled);
    }
    [Fact] public async Task RepairRewritesOlderReportsAndQuietlyFillsTheLastWeek()
    {
        using var db = new DataStore(directory); var state = Live(db); var service = new ReportService(state, db);
        var now = new DateTimeOffset(2026, 10, 5, 8, 53, 0, TimeSpan.Zero);
        var sat = new DateTimeOffset(2026, 10, 2, 23, 0, 0, TimeSpan.Zero); // Sat 3 Oct, local midnight
        db.SaveTelemetry(LoadSamples(sat.AddHours(9), 10, 10.6));
        var empty = new EnergyReport { Kind = "Daily", From = sat.AddDays(-2), To = sat.AddDays(-1), Title = "Daily energy report · 1 Oct 2026", Summary = "No authoritative energy intervals…", TimeZone = "Europe/London" };
        var old = new EnergyReport { Kind = "Daily", From = sat, To = sat.AddDays(1), Title = "Daily energy report · 3 Oct 2026", Summary = "load: 0.60 kWh observed (100 % coverage)", TimeZone = "Europe/London",
            EnergySummary = db.ReadEnergySummary(sat, sat.AddDays(1)) };
        await state.MutateAsync(s =>
        {
            s.ReportPreferences = new ReportPreferences { DailyEnabled = false };
            s.Reports.AddRange([empty, old]);
            s.Notifications.Add(new InAppNotification { Title = empty.Title, ReportId = empty.Id }); s.Notifications.Add(new InAppNotification { Title = old.Title, ReportId = old.Id });
        });
        await service.RepairAsync(now);
        var saved = db.Load()!;
        var rewritten = saved.Reports.Single(r => r.Id == old.Id);
        Assert.Equal("Daily report · Sat 3 Oct", rewritten.Title); Assert.StartsWith("Your home used at least 0.6 kWh. Readings began at 09:00, so earlier energy isn't counted.", rewritten.Summary); Assert.EndsWith("Standing charges aren't included.", rewritten.Summary); Assert.Null(rewritten.EnergySummary);
        Assert.Equal("Daily report · Sat 3 Oct", saved.Notifications.Single(n => n.ReportId == old.Id).Title); Assert.Null(saved.Notifications.Single(n => n.ReportId == old.Id).ReadAt);
        Assert.NotNull(saved.Reports.Single(r => r.Id == empty.Id).ReadAt); Assert.NotNull(saved.Notifications.Single(n => n.ReportId == empty.Id).ReadAt);
        // Scheduled reports were delivered before, so daily reports being off now was a choice: kept, and nothing filled in.
        Assert.False(saved.ReportPreferences.DailyEnabled);
        Assert.Equal(2, saved.Reports.Count); Assert.Equal(2, saved.Notifications.Count);
        // Turning daily reports on fills the days that have readings and no report yet; none of them notifies.
        db.SaveTelemetry(LoadSamples(sat.AddDays(1).AddHours(9), 20, 20.3));
        await state.MutateAsync(s => { s.ReportPreferences.DefaultsVersion = 0; s.ReportPreferences.DailyEnabled = true; });
        await service.RepairAsync(now);
        Assert.Contains(db.Load()!.Reports, r => r.Title == "Daily report · Sun 4 Oct" && r.Summary.StartsWith("Your home used at least 0.3 kWh."));
        Assert.Equal(2, db.Load()!.Notifications.Count);
    }
    [Fact] public async Task ReportRetainsItsReportingTimeZoneAfterPreferencesChangeAndReload()
    {
        using var db = new DataStore(directory); var state = Live(db); var service = new ReportService(state, db);
        await state.MutateAsync(s => s.ReportPreferences.TimeZone = "Pacific/Auckland");
        var from = DateTimeOffset.UtcNow.AddDays(-20); var to = from.AddHours(1);
        db.SaveTelemetry(LoadSamples(from, 10, 10.2));
        var report = await service.GenerateAsync(new("Daily", from, to));
        Assert.Equal("Pacific/Auckland", report.TimeZone);
        await state.MutateAsync(s => s.ReportPreferences.TimeZone = "Europe/London");
        var restarted = new StateService(db, new NoWriter(), false);
        Assert.Equal("Europe/London", restarted.Read(false).ReportPreferences.TimeZone);
        Assert.Equal("Pacific/Auckland", restarted.Read(false).Reports.Single().TimeZone);
        Assert.Equal("Pacific/Auckland", new ReportService(restarted, db).View(report.Id).TimeZone);
        Assert.Equal(report.Id, (await new ReportService(restarted, db).GenerateAsync(new("Daily", from, to))).Id);
        var legacy = System.Text.Json.JsonSerializer.Deserialize<EnergyReport>("{\"kind\":\"Daily\"}", JsonDefaults.Options)!;
        Assert.Null(legacy.TimeZone); Assert.Equal(0, legacy.TextVersion);
        var legacyPrefs = System.Text.Json.JsonSerializer.Deserialize<ReportPreferences>("{\"dailyEnabled\":false}", JsonDefaults.Options)!;
        Assert.Equal(0, legacyPrefs.DefaultsVersion);
    }
    [Fact] public async Task ReportingRejectsCrossModeTelemetryContamination()
    {
        using var db = new DataStore(directory); var state = new StateService(db, new NoWriter(), true); var service = new ReportService(state, db);
        var from = DateTimeOffset.UtcNow.AddDays(-20); var to = from.AddHours(1);
        db.SaveTelemetry([new("load", "sensor.house", from, 10, "kWh", "HomeAssistant", "10", "kWh"), new("load", "sensor.house", from.AddMinutes(5), 10.2, "kWh", "HomeAssistant", "10.2", "kWh")]);
        await Assert.ThrowsAsync<DomainException>(() => service.GenerateAsync(new("Daily", from, to))); Assert.Empty(state.Read().Reports);
    }
    [Fact] public void DailyPeriodFollowsLocalMidnightAcrossDst()
    {
        var spring = ReportService.CompletedPeriod("Daily", new DateTimeOffset(2026, 3, 30, 10, 0, 0, TimeSpan.Zero), "Europe/London");
        Assert.Equal(23, (spring.To - spring.From).TotalHours);
        var autumn = ReportService.CompletedPeriod("Daily", new DateTimeOffset(2025, 10, 27, 10, 0, 0, TimeSpan.Zero), "Europe/London");
        Assert.Equal(25, (autumn.To - autumn.From).TotalHours);
    }
    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
}
