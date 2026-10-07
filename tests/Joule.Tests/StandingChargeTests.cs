using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using Joule;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Joule.Tests;

/// <summary>The standing charge: found from the Octopus Energy sensors, stored per day, prorated over windows (clock changes included),
/// kept out of the energy net cost and named in reports and the AI brief.</summary>
public sealed class StandingChargeTests : IDisposable
{
    readonly string path = Path.Combine(Path.GetTempPath(), "predbat-standing-" + Guid.NewGuid().ToString("N"));
    static readonly TimeZoneInfo London = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");
    static (double, string, bool)? Flat(DateOnly _) => (60, "sensor", false);

    public void Dispose() { try { Directory.Delete(path, true); } catch (IOException) { } }

    static DateTimeOffset LocalMidnight(int year, int month, int day) => TimeZoneInfo.ConvertTimeToUtc(new DateTime(year, month, day), London);

    [Fact]
    public void AWholeDayIsOneDaysChargeAndPartOfADayIsItsShare()
    {
        var midnight = LocalMidnight(2026, 10, 5);
        Assert.Equal(.60, StandingCharge.Prorate(midnight, midnight.AddDays(1), London, Flat)!.Gbp, 9);
        Assert.Equal(.15, StandingCharge.Prorate(midnight, midnight.AddHours(6), London, Flat)!.Gbp, 9);
        // 18:00 to 06:00 the next day: a quarter of each day.
        Assert.Equal(.30, StandingCharge.Prorate(midnight.AddHours(18), midnight.AddHours(30), London, Flat)!.Gbp, 9);
        Assert.Null(StandingCharge.Prorate(midnight, midnight, London, Flat));
        Assert.Null(StandingCharge.Prorate(midnight, midnight.AddDays(1), London, _ => null));
    }

    [Fact]
    public void ClockChangeDaysStillAddUpToOneDaysCharge()
    {
        // 29 March 2026 is 23 hours long in London, 25 October 2026 is 25 hours.
        var spring = LocalMidnight(2026, 3, 29); var springEnd = LocalMidnight(2026, 3, 30);
        Assert.Equal(TimeSpan.FromHours(23), springEnd - spring);
        Assert.Equal(.60, StandingCharge.Prorate(spring, springEnd, London, Flat)!.Gbp, 9);
        Assert.Equal(.60 * 12 / 23, StandingCharge.Prorate(spring, spring.AddHours(12), London, Flat)!.Gbp, 6);

        var autumn = LocalMidnight(2026, 10, 25); var autumnEnd = LocalMidnight(2026, 10, 26);
        Assert.Equal(TimeSpan.FromHours(25), autumnEnd - autumn);
        Assert.Equal(.60, StandingCharge.Prorate(autumn, autumnEnd, London, Flat)!.Gbp, 9);
        Assert.Equal(.30, StandingCharge.Prorate(autumn, autumn.AddHours(12.5), London, Flat)!.Gbp, 9);
        // A week across the change is seven days' charge, not 7 × 24 hours' worth.
        Assert.Equal(4.20, StandingCharge.Prorate(LocalMidnight(2026, 10, 22), LocalMidnight(2026, 10, 29), London, Flat)!.Gbp, 9);
    }

    [Fact]
    public void EachDayUsesItsOwnRate()
    {
        var first = new DateOnly(2026, 10, 5);
        (double, string, bool)? Rate(DateOnly d) => d == first ? (50, "sensor", false) : (70, "manual", false);
        var a = StandingCharge.Prorate(LocalMidnight(2026, 10, 5).AddHours(12), LocalMidnight(2026, 10, 6).AddHours(12), London, Rate)!;
        Assert.Equal(.25 + .35, a.Gbp, 9);
        Assert.Equal(70, a.PencePerDay); Assert.Equal("manual", a.Source);
    }

    [Theory]
    [InlineData("sensor.octopus_energy_electricity_21l1234567_1900012345678_current_rate", "sensor.octopus_energy_electricity_21l1234567_1900012345678_current_standing_charge")]
    [InlineData("sensor.octopus_energy_electricity_21l1234567_1900012345678_export_current_rate", null)]
    [InlineData("sensor.octopus_energy_electricity_21l1234567_1900012345678_previous_rate", null)]
    [InlineData("sensor.my_tariff_rate", null)]
    [InlineData(null, null)]
    public void FindsTheOctopusStandingChargeSensorOnTheSameMeter(string? rate, string? expected) =>
        Assert.Equal(expected, StandingCharge.FromOctopusRate(rate));

    [Theory]
    [InlineData(0.5368, "GBP", 53.68)]
    [InlineData(0.5368, "£/day", 53.68)]
    [InlineData(53.68, "p/day", 53.68)]
    [InlineData(53.68, "p", 53.68)]
    [InlineData(53.68, "W", null)]
    [InlineData(-1, "p/day", null)]
    public void ReadsTheChargeInPencePerDay(double value, string unit, double? expected)
    {
        var pence = StandingCharge.Pence(value, unit);
        if (expected is null) Assert.Null(pence); else Assert.Equal(expected.Value, pence!.Value, 9);
    }

    [Fact]
    public void SetupSuggestsTheStandingChargeSensorFromPredbatsAppsYamlOrItsName()
    {
        const string entity = "sensor.octopus_energy_electricity_21l1234567_1900012345678_current_standing_charge";
        using var state = System.Text.Json.JsonDocument.Parse("{\"" + entity + "\":" + """{"state":"0.5368","attributes":{"unit_of_measurement":"GBP","friendly_name":"Current Standing Charge Electricity"}},""" +
            """ "sensor.octopus_energy_gas_123_456_current_standing_charge":{"state":"0.31","attributes":{"unit_of_measurement":"GBP"}},""" +
            """ "sensor.octopus_energy_electricity_21l1234567_1900012345678_current_accumulative_cost":{"state":"1.2","attributes":{"unit_of_measurement":"GBP"}}}""");
        var found = SensorCandidates.Find(state.RootElement)["standing_charge"];
        Assert.Equal(entity, Assert.Single(found).Entity);
        // Predbat's own apps.yaml names it as metric_standing_charge, usually as a regex.
        var apps = SetupConfigEndpoints.AppsValues("pred_bat:\n  metric_standing_charge: 're:(sensor.(octopus_energy_|)electricity_[0-9a-z]+_[0-9a-z]+_current_standing_charge)'\n");
        Assert.Equal(entity, SetupConfigEndpoints.Resolve(apps["metric_standing_charge"][0], state.RootElement));
    }

    static HomeAssistantOptions Options(Dictionary<string, string?> entities)
    {
        var values = new Dictionary<string, string?> { ["HomeAssistant:BaseUrl"] = "http://ha.test", ["HomeAssistant:AccessToken"] = "test-secret" };
        foreach (var (key, value) in entities) values["HomeAssistant:Entities:" + key] = value;
        return new(new ConfigurationBuilder().AddInMemoryCollection(values).Build());
    }

    [Fact]
    public async Task TheOctopusSensorIsReadWithoutBeingMappedAndSkippedQuietlyWhenItIsMissing()
    {
        const string rate = "sensor.octopus_energy_electricity_21l1234567_1900012345678_current_rate";
        const string standing = "sensor.octopus_energy_electricity_21l1234567_1900012345678_current_standing_charge";
        var options = Options(new() { ["Load"] = "sensor.house_energy", ["ImportTariff"] = rate });
        Assert.Equal(standing, options.Entities["standing_charge"]);
        Assert.Contains("standing_charge", options.DerivedEntities);
        // A mapping in Setup wins over the worked-out sensor.
        Assert.Equal("sensor.my_standing", Options(new() { ["ImportTariff"] = rate, ["StandingCharge"] = "sensor.my_standing" }).Entities["standing_charge"]);

        var states = $$$"""[{"entity_id":"sensor.house_energy","state":"12","attributes":{"unit_of_measurement":"kWh"}},{"entity_id":"{{{rate}}}","state":"0.2541","attributes":{"unit_of_measurement":"GBP/kWh"}},{"entity_id":"{{{standing}}}","state":"0.5368","attributes":{"unit_of_measurement":"GBP","start":"2026-10-01T00:00:00+01:00"}}]""";
        var read = await new HomeAssistantClient(new HttpClient(new Handler(_ => new(HttpStatusCode.OK) { Content = new StringContent(states) })), options).CollectAsync(default);
        var sample = Assert.Single(read, x => x.Metric == "standing_charge");
        Assert.Equal(53.68, sample.Value!.Value, 9); Assert.Equal("p/day", sample.Unit); Assert.Equal("observed", sample.Status);

        var without = """[{"entity_id":"sensor.house_energy","state":"12","attributes":{"unit_of_measurement":"kWh"}},{"entity_id":"sensor.octopus_energy_electricity_21l1234567_1900012345678_current_rate","state":"0.2541","attributes":{"unit_of_measurement":"GBP/kWh"}}]""";
        var quiet = await new HomeAssistantClient(new HttpClient(new Handler(_ => new(HttpStatusCode.OK) { Content = new StringContent(without) })), options).CollectAsync(default);
        Assert.DoesNotContain(quiet, x => x.Metric == "standing_charge");
        Assert.Contains("standing_charge", TelemetryCollectionService.OptionalMetrics);
    }

    [Fact]
    public async Task AWorkedOutSensorThatIsMissingIsNotShownAsMappedOrAsNeedingALook()
    {
        const string rate = "sensor.octopus_energy_electricity_21l1234567_1900012345678_current_rate";
        const string standing = "sensor.octopus_energy_electricity_21l1234567_1900012345678_current_standing_charge";
        var options = Options(new() { ["Load"] = "sensor.house_energy", ["ImportTariff"] = rate });
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["App:Demo"] = "false" }).Build();
        var clock = new ManualClock(Midnight.AddHours(9));
        using var db = new DataStore(path, clock);
        var states = """[{"entity_id":"sensor.house_energy","state":"12","attributes":{"unit_of_measurement":"kWh","state_class":"total_increasing"}},{"entity_id":"sensor.octopus_energy_electricity_21l1234567_1900012345678_current_rate","state":"0.2541","attributes":{"unit_of_measurement":"GBP/kWh"}}]""";
        var respond = (HttpRequestMessage _) => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(states) };
        var service = new TelemetryCollectionService(db, new HomeAssistantClient(new HttpClient(new Handler(r => respond(r))), options, clock: clock), options, config, clock);
        await service.CollectAsync(default);
        var status = service.Status();
        Assert.False(status.EntityMappings.ContainsKey("standing_charge"));
        Assert.False(status.LatestReadings.ContainsKey("standing_charge"));
        Assert.DoesNotContain("standing_charge", status.MissingMappings);
        Assert.Equal(rate, status.EntityMappings["import_tariff"]);
        Assert.False(options.ShownEntities(db.ReadLatestTelemetry()).ContainsKey("standing_charge"));

        // Home Assistant unreachable: nothing is stored for the worked-out sensor either.
        respond = _ => new HttpResponseMessage(HttpStatusCode.InternalServerError);
        clock.Now = clock.Now.AddMinutes(5);
        await Assert.ThrowsAnyAsync<Exception>(() => service.CollectAsync(default));
        Assert.False(service.Status().LatestReadings.ContainsKey("standing_charge"));

        // Once it gives a reading, it shows like any other sensor.
        states = states.TrimEnd(']') + $$$""",{"entity_id":"{{{standing}}}","state":"0.5368","attributes":{"unit_of_measurement":"GBP"}}]""";
        respond = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(states) };
        clock.Now = clock.Now.AddMinutes(5);
        await service.CollectAsync(default);
        status = service.Status();
        Assert.Equal(standing, status.EntityMappings["standing_charge"]);
        Assert.Equal("observed", status.LatestReadings["standing_charge"].Status);
    }

    [Fact]
    public void ClearingTheTypedFigureRemovesItFromEveryDayItWasUsed()
    {
        var clock = new ManualClock(Midnight.AddDays(-1).AddHours(1));
        using var db = new DataStore(path, clock);
        Day(db, Midnight.AddDays(-1), 24, null);
        db.SaveStandingChargePreferences(new(99, null)); // mistyped
        clock.Now = Midnight.AddHours(6);
        Day(db, Midnight, 6, null);
        db.RecordManualStandingCharge();
        Assert.Equal(.99, db.ReadEnergySummary(Midnight.AddDays(-1), Midnight).StandingChargeGbp!.Value, 6);

        db.SaveStandingChargePreferences(new(null, null, ClearManual: true));
        db.RecordManualStandingCharge();
        Assert.Null(db.ReadEnergySummary(Midnight.AddDays(-1), Midnight).StandingChargeGbp);
        Assert.Null(db.ReadStandingCharge(null, null).ManualPencePerDay);

        // Retyped: today takes it, and yesterday assumes it.
        db.SaveStandingChargePreferences(new(55, null));
        var yesterday = db.ReadEnergySummary(Midnight.AddDays(-1), Midnight);
        Assert.Equal(.55, yesterday.StandingChargeGbp!.Value, 6); Assert.True(yesterday.StandingChargeAssumed);
    }

    static readonly DateTimeOffset Midnight = LocalMidnight(2026, 10, 5);
    static TelemetrySample Meter(string metric, DateTimeOffset at, double value) =>
        new(metric, "sensor." + metric, at, value, "kWh", "HomeAssistant", value.ToString(CultureInfo.InvariantCulture), "kWh", at, "{\"state_class\":\"total\",\"last_reset\":\"2026-10-05T00:00:00+01:00\"}");
    static TelemetrySample Rate(string metric, DateTimeOffset at, double pence) =>
        new(metric, "sensor." + metric, at, pence, "p/kWh", "HomeAssistant", (pence / 100).ToString(CultureInfo.InvariantCulture), "GBP/kWh", at);
    static TelemetrySample Standing(DateTimeOffset at, double pence) =>
        new("standing_charge", "sensor.octopus_standing", at, pence, "p/day", "HomeAssistant", (pence / 100).ToString(CultureInfo.InvariantCulture), "GBP", at);

    /// <summary>Five-minute polls from local midnight: 1 kWh imported per hour at 20p, nothing exported.</summary>
    static void Day(DataStore db, DateTimeOffset midnight, int hours, double? standingPence)
    {
        var polls = new List<TelemetrySample>();
        for (var m = 0; m <= hours * 60; m += 5)
        {
            var at = midnight.AddMinutes(m);
            polls.AddRange([Meter("grid_import", at, m / 60d), Meter("grid_export", at, 0), Rate("import_tariff", at, 20), Rate("export_tariff", at, 15)]);
            if (standingPence is { } p) polls.Add(Standing(at, p));
        }
        db.SaveTelemetry(polls);
    }

    [Fact]
    public void TheSummaryCarriesTheChargeSoFarBesideTheEnergyNetCost()
    {
        var clock = new ManualClock(Midnight.AddHours(12));
        using var db = new DataStore(path, clock);
        Day(db, Midnight, 12, 60);
        var s = db.ReadEnergySummary(Midnight, Midnight.AddDays(1));
        Assert.Equal(12 * .20, s.NetCostGbp!.Value, 6);
        // Only the half of today that has happened so far carries a charge.
        Assert.Equal(.30, s.StandingChargeGbp!.Value, 6);
        Assert.Equal(60, s.StandingChargePencePerDay); Assert.Equal("sensor", s.StandingChargeSource);
        Assert.True(s.StandingChargeIncluded); Assert.False(s.StandingChargeAssumed);
        Assert.Equal(12 * .20 + .30, s.NetCostWithStandingChargeGbp!.Value, 6);

        db.SaveStandingChargePreferences(new(null, IncludeInNet: false));
        var excluded = db.ReadEnergySummary(Midnight, Midnight.AddDays(1));
        Assert.False(excluded.StandingChargeIncluded);
        Assert.Equal(s.NetCostGbp, excluded.NetCostGbp);
        Assert.Equal(.30, excluded.StandingChargeGbp!.Value, 6);
    }

    [Fact]
    public void NoChargeBeforeJoulesFirstReading()
    {
        var clock = new ManualClock(Midnight.AddHours(18));
        using var db = new DataStore(path, clock);
        Day(db, Midnight.AddHours(12), 6, 60);
        // Readings began at noon: the day so far is noon to 18:00, a quarter of the day.
        Assert.Equal(.15, db.ReadEnergySummary(Midnight, Midnight.AddDays(1)).StandingChargeGbp!.Value, 6);
        Assert.Null(db.ReadEnergySummary(Midnight.AddDays(-1), Midnight).StandingChargeGbp);
    }

    [Fact]
    public void TheRateIsKeptPerDayAndTheSensorWinsOverTheManualFigure()
    {
        var clock = new ManualClock(Midnight.AddDays(-1));
        using var db = new DataStore(path, clock);
        // Yesterday Joule had only the manual figure; today the sensor reports a new rate.
        db.SaveStandingChargePreferences(new(50, null));
        Day(db, Midnight.AddDays(-1), 24, null);
        clock.Now = Midnight.AddHours(6);
        Day(db, Midnight, 6, 62.5);
        db.RecordManualStandingCharge();

        var yesterday = db.ReadEnergySummary(Midnight.AddDays(-1), Midnight);
        Assert.Equal(.50, yesterday.StandingChargeGbp!.Value, 6); Assert.Equal("manual", yesterday.StandingChargeSource);
        var today = db.ReadEnergySummary(Midnight, Midnight.AddDays(1));
        Assert.Equal(.625 / 4, today.StandingChargeGbp!.Value, 6); Assert.Equal("sensor", today.StandingChargeSource);

        var view = db.ReadStandingCharge("sensor.octopus_standing", "octopus");
        Assert.Equal(62.5, view.TodayPencePerDay); Assert.Equal("sensor", view.TodaySource); Assert.Equal(50, view.ManualPencePerDay);
        Assert.Equal([new DateOnly(2026, 10, 5), new DateOnly(2026, 10, 4)], view.Recent.Select(x => x.Day));

        Assert.Throws<DomainException>(() => db.SaveStandingChargePreferences(new(-3, null)));
        Assert.Throws<DomainException>(() => db.SaveStandingChargePreferences(new(double.NaN, null)));
    }

    [Fact]
    public void DaysBeforeTheFirstRecordedRateAssumeIt()
    {
        var clock = new ManualClock(Midnight.AddDays(-2));
        using var db = new DataStore(path, clock);
        Day(db, Midnight.AddDays(-2), 24, null);
        clock.Now = Midnight.AddHours(1);
        db.SaveStandingChargePreferences(new(55, null));
        var early = db.ReadEnergySummary(Midnight.AddDays(-2), Midnight.AddDays(-1));
        Assert.Equal(.55, early.StandingChargeGbp!.Value, 6);
        Assert.True(early.StandingChargeAssumed);
    }

    [Fact]
    public void ReportsAndTheAiBriefNameTheChargeAndItsRate()
    {
        var from = Midnight; var to = Midnight.AddDays(1);
        var summary = new EnergySummary(from, to, new() { ["grid_import"] = new(10, 86400, 1, 0) { State = "complete" }, ["grid_export"] = new(0, 86400, 1, 0) { State = "complete" } },
            2.00, 0, 2.00, 1, 86400, [], [])
        { NetCostGbp = 2.00, ImportCostCoverage = 1, ExportCostCoverage = 1, StandingChargeGbp = .5368, StandingChargePencePerDay = 53.68, StandingChargeSource = "sensor", StandingChargeIncluded = true, NetCostWithStandingChargeGbp = 2.5368 };
        var text = ReportService.Describe(summary, from, to, London, demo: false);
        Assert.Contains("Net cost £2.54, including the £0.54 standing charge (53.68p a day).", text);
        Assert.DoesNotContain("aren't included", text);
        Assert.Equal("Net cost £2.54", ReportService.Headline(summary));

        var excluded = ReportService.Describe(summary with { StandingChargeIncluded = false }, from, to, London, demo: false);
        Assert.Contains("Net cost £2.00; the £0.54 standing charge (53.68p a day) isn't included.", excluded);

        // Included (the default): the AI is told to quote the dashboard's figure, with the energy-only one as secondary.
        var brief = InvestigationBrief.MoneyBrief(summary, London, "Today");
        Assert.StartsWith("Today: net cost £2.54 including the standing charge (this is the figure to quote; it matches the owner's dashboard).", brief);
        Assert.Contains("Energy only, without the standing charge: £2.00 (what trials and like-for-like comparisons use; never quote it as the cost)", brief);
        Assert.Contains("Standing charge for this period £0.54 (53.68p a day, from the standing charge sensor), included in the net cost above", brief);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(brief, "figure to quote"));
        Assert.DoesNotContain("£2.00 (this is the figure to quote)", brief);

        // Left out by the owner: the energy-only figure is the one to quote, and the bill with the charge is stated alongside.
        var left = InvestigationBrief.MoneyBrief(summary with { StandingChargeIncluded = false }, London, "Today");
        Assert.StartsWith("Today: net cost £2.00 (this is the figure to quote).", left);
        Assert.Contains("not in the net cost above; with it the bill is £2.54", left);
        Assert.Contains("leave the standing charge out of the dashboard's headline", left);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(left, "figure to quote"));
        Assert.Contains("Standing charge not known", InvestigationBrief.MoneyBrief(summary with { StandingChargeGbp = null, StandingChargePencePerDay = null }, London, "Today"));
    }

    [Fact]
    public void AnOctopusImportRateFoundFromPredbatBringsItsStandingChargeSensorUnlessOneWasChosenOrDeclined()
    {
        const string rate = "sensor.octopus_energy_electricity_21l1234567_1900012345678_current_rate";
        const string standing = "sensor.octopus_energy_electricity_21l1234567_1900012345678_current_standing_charge";
        var options = Options(new() { ["Load"] = "sensor.house_energy" });
        Assert.False(options.Entities.ContainsKey("standing_charge"));

        // Detection finds the import rate: the standing charge sensor on the same meter is worked out from it.
        options.ApplyDetected(new Dictionary<string, DetectedSensor> { ["import_tariff"] = new(rate, "metric_octopus_import in apps.yaml") }, [], null);
        Assert.Equal(standing, options.Entities["standing_charge"]);
        Assert.Contains("standing_charge", options.DerivedEntities);
        Assert.False(options.Detected.ContainsKey("standing_charge"));

        // A later run that finds the standing charge itself (metric_standing_charge) replaces the worked-out one.
        options.ApplyDetected(new Dictionary<string, DetectedSensor> { ["import_tariff"] = new(rate, "x"), ["standing_charge"] = new("sensor.predbat_standing", "metric_standing_charge in apps.yaml") }, [], null);
        Assert.Equal("sensor.predbat_standing", options.Entities["standing_charge"]);
        Assert.Empty(options.DerivedEntities);

        // And one that loses the import rate drops the worked-out sensor with it.
        options.ApplyDetected(new Dictionary<string, DetectedSensor> { ["import_tariff"] = new(rate, "x") }, [], null);
        Assert.Contains("standing_charge", options.DerivedEntities);
        options.ApplyDetected(new Dictionary<string, DetectedSensor>(), [], null);
        Assert.False(options.Entities.ContainsKey("standing_charge"));
        Assert.Empty(options.DerivedEntities);
        Assert.Equal("sensor.house_energy", options.Entities["load"]);

        // "none" keeps the standing charge unmapped: never worked out, whether the rate is configured or found.
        var declined = Options(new() { ["ImportTariff"] = rate, ["StandingCharge"] = "none" });
        Assert.False(declined.Entities.ContainsKey("standing_charge"));
        Assert.Empty(declined.DerivedEntities);
        var declinedFound = Options(new() { ["StandingCharge"] = "none" });
        declinedFound.ApplyDetected(new Dictionary<string, DetectedSensor> { ["import_tariff"] = new(rate, "x") }, [], null);
        Assert.False(declinedFound.Entities.ContainsKey("standing_charge"));
    }

    [Fact]
    public void ANumberInPredbatsAppsYamlIsTheRateWithoutASensorOrATypedFigure()
    {
        var clock = new ManualClock(Midnight.AddHours(6));
        using var db = new DataStore(path, clock);
        Day(db, Midnight, 6, null);
        db.RecordManualStandingCharge(48);
        var s = db.ReadEnergySummary(Midnight, Midnight.AddDays(1));
        Assert.Equal(.12, s.StandingChargeGbp!.Value, 6); Assert.Equal("predbat", s.StandingChargeSource);
        Assert.Equal("predbat", db.ReadStandingCharge(null, null).TodaySource);
        Assert.Contains("the figure in Predbat's apps.yaml", InvestigationBrief.MoneyBrief(s, London, "Today"));

        // The owner's own figure wins over Predbat's; clearing it brings Predbat's back.
        db.SaveStandingChargePreferences(new(55, null));
        db.RecordManualStandingCharge(48);
        Assert.Equal("manual", db.ReadStandingCharge(null, null).TodaySource);
        db.SaveStandingChargePreferences(new(null, null, ClearManual: true));
        db.RecordManualStandingCharge(48);
        var view = db.ReadStandingCharge(null, null);
        Assert.Equal("predbat", view.TodaySource); Assert.Equal(48, view.TodayPencePerDay);

        // A sensor reading still wins over both.
        clock.Now = Midnight.AddHours(7);
        db.SaveTelemetry([Standing(clock.Now, 53.68)]);
        db.RecordManualStandingCharge(48);
        Assert.Equal("sensor", db.ReadStandingCharge(null, null).TodaySource);
    }

    sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(respond(request));
    }
}
