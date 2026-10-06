using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Joule;
using Xunit;

namespace Joule.Tests;

/// <summary>
/// The facts the AI is given: glossary labels instead of Predbat codes, battery levels at the start and end of each half-hour, the
/// arbitrage margin from the home's own loss settings, the headline net cost, sensor explanations from stored data, and the replay of
/// the 5 Oct 02:10/03:13 "plan churn" findings that the arbitrage rule now treats as intended.
/// </summary>
public sealed class AnalysisBriefTests : IDisposable
{
    readonly string directory = Path.Combine(Path.GetTempPath(), "joule-brief-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(directory, true); } catch { } }
    static readonly TimeZoneInfo London = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");
    // The live parameters on 5 Oct: battery_loss 0.04, battery_loss_discharge 0.04, inverter_loss 0.01, metric_battery_cycle 0.
    static readonly ArbitrageParameters Live = new(0.04, 0.04, 0.01, 0, 0.1);

    static PlanVsActualSlot Slot(DateTimeOffset t, string key, double socPlanned, double? socStart, double? socEnd, double imp, double exp,
        double? gridIn = null, double? gridOut = null, double? charged = null, double? discharged = null, double? target = null, string? reason = null) =>
        new(t, 30, key, socPlanned, socStart, socEnd, imp, exp, 0.4, 0.38, 0, 0, gridIn, gridOut, charged, discharged, null, t.AddMinutes(-5), 3, 1)
        { ActionKey = key, TargetPercent = target, ReasonText = reason };

    [Fact]
    public void PlanRowsUseGlossaryLabelsWithTargetReasonAndBatteryStartAndEnd()
    {
        var t = new DateTimeOffset(2026, 10, 4, 8, 0, 0, TimeSpan.Zero); // 09:00 BST
        var rows = InvestigationBrief.PlanRows([Slot(t, "freeze-export", 21, 20, 19, 6.67, 12.28, reason: "Spare solar goes to the grid; the battery isn't worth discharging to export this slot."), Slot(t.AddMinutes(30), "charge", 21, 19, 30, 6.67, 12.28, target: 94)], London, Live);
        Assert.StartsWith("Sun 04 Oct 09:00 | plan: Export solar, don't charge battery — Spare solar goes to the grid", rows[0]);
        Assert.Contains("battery planned 21%→21%, actual 20%→19%", rows[0]);
        Assert.Contains("plan: Charge from the grid (target 94%)", rows[1]);
        Assert.Contains("battery planned 21%→-", rows[1]);
        var legend = InvestigationBrief.PlanRowsLegend(Live);
        Assert.Contains("START of the half-hour", legend);
        Assert.Contains("“Export solar, don't charge battery”: The battery won't charge; it may still discharge for the house", legend);
        foreach (var code in new[] { "FrzExp", "FrzChrg", "HoldChrg", "HoldExp", "Chrg", "Exp " })
        { Assert.DoesNotContain(code, legend); Assert.All(rows, r => Assert.DoesNotContain(code, r)); }
    }

    [Fact]
    public void ArbitrageMarginUsesTheLiveLossSettings()
    {
        var parameters = ArbitrageParameters.From([
            new Setting { Key = "battery_loss", Value = "0.04" }, new Setting { Key = "battery_loss_discharge", Value = "0.04" },
            new Setting { Key = "inverter_loss", Value = "0.01" }, new Setting { Key = "metric_battery_cycle", Value = "0" }, new Setting { Key = "metric_min_improvement_export", Value = "0.1" }]);
        Assert.Equal(Live, parameters);
        // 12.28 × 0.9504 − 0 − 6.67 ÷ 0.9504 = 11.671 − 7.018 = +4.65p per kWh: profitable, so not churn.
        Assert.Equal(4.653, parameters.MarginPence(6.67, 12.28), 3);
        Assert.True(parameters.Profitable(6.67, 12.28));
        // At 7.0p export the same cycle loses money after losses and must still be flagged.
        Assert.Equal(-0.365, parameters.MarginPence(6.67, 7.0), 3);
        Assert.False(parameters.Profitable(6.67, 7.0));
        // Battery wear makes a thin margin unprofitable.
        Assert.False((Live with { CycleCostPence = 5 }).Profitable(6.67, 12.28));
        var t = new DateTimeOffset(2026, 10, 5, 1, 30, 0, TimeSpan.Zero);
        var slots = new[] { Slot(t, "export", 61, 61, 51, 6.67, 12.28, 1.22, 1.70, 1.09, 2.92), Slot(t.AddMinutes(30), "charge", 55, 51, 31, 6.67, 12.28, 0.84, 2.78, 0.75, 3.03) };
        var exports = InvestigationBrief.Arbitrage(slots, Live);
        Assert.Equal(2, exports.Count); Assert.All(exports, x => Assert.True(x.Profitable));
        Assert.Equal(4.653 * 1.70 + 4.653 * 2.78, exports.Sum(x => x.EarnedPence), 1);
        var summary = InvestigationBrief.ArbitrageSummary(slots, Live, London);
        Assert.Contains("earned about +21p", summary); Assert.Contains("intended arbitrage, not a fault", summary);
    }

    [Fact]
    public void ImpactPenceIsMeasuredGridCostMinusThePlannedCost()
    {
        var t = new DateTimeOffset(2026, 10, 5, 1, 30, 0, TimeSpan.Zero);
        var slots = new[] { Slot(t, "export", 61, 61, 51, 6.67, 12.28, 1.22, 1.70), Slot(t.AddMinutes(30), "charge", 55, 51, 31, 6.67, 12.28, 0.84, 2.78), Slot(t.AddMinutes(60), "charge", 55, 31, 38, 6.67, 12.28, null, null) };
        var planned = new Dictionary<DateTimeOffset, double> { [t] = -0.25, [t.AddMinutes(30)] = 0.10, [t.AddMinutes(60)] = 0.10 };
        // (1.22×6.67 − 1.70×12.28 + 25) + (0.84×6.67 − 2.78×12.28 − 10) = 12.262 + (−38.534) = −26.3p: cheaper than the plan expected.
        Assert.Equal(-26.3, InvestigationBrief.ImpactPence(slots, planned));
        Assert.Null(InvestigationBrief.ImpactPence(slots[2..], planned));
        Assert.Null(InvestigationBrief.ImpactPence(slots, new Dictionary<DateTimeOffset, double>()));
    }

    [Fact]
    public void TheMoneyBriefQuotesTheNetCostNotTheMatchedFigure()
    {
        // The live 5 Oct shape before the accounting fix: import £1.80, export £0.70, matched-period "net" £0.06.
        var from = new DateTimeOffset(2026, 10, 4, 23, 0, 0, TimeSpan.Zero); var to = from.AddHours(10);
        var summary = new EnergySummary(from, to, [], 1.80, 0.70, 0.06, 0.3, 10800, ["HomeAssistant"], [])
        {
            NetCostGbp = 1.10, ImportCostCoverage = 1, ExportCostCoverage = 0.72,
            CostGaps = [new("grid_export", from, from.AddMinutes(167), "offline")]
        };
        var text = InvestigationBrief.MoneyBrief(summary, London, "Today so far");
        Assert.StartsWith("Today so far: net cost ≈ £1.10 (this is the figure to quote).", text);
        Assert.Contains("Paid for import £1.80", text); Assert.Contains("earned from export ≈ £0.70 (72% of the time measured)", text);
        Assert.Contains("Export meter not priced 00:00–02:47 (offline)", text);
        var matched = text.Split('\n').Single(l => l.Contains("£0.06"));
        Assert.StartsWith("Only while both meters were reporting", matched); Assert.Contains("never quote it as the day's cost", matched);
    }

    static TelemetryStatus Status(params (string Metric, LatestTelemetry Reading)[] readings) =>
        new(false, true, "Europe/London", DateTimeOffset.UtcNow, null, readings.ToDictionary(r => r.Metric, r => r.Reading.EntityId), [], 15, readings.ToDictionary(r => r.Metric, r => r.Reading));

    [Fact]
    public void SensorExplanationsComeFromTheStoredProfileNotTheClock()
    {
        // 14:00 BST: the old clock rule ("night is 19:00–09:00") would have called this a fault; the profile says it is expected.
        var afternoon = new DateTimeOffset(2026, 10, 5, 13, 0, 0, TimeSpan.Zero);
        var quiet = new LatestTelemetry(null, "kWh", afternoon, "idle", "sensor.my_home_solar_generated", "HomeAssistant", "unknown") { Expected = true, Reason = "Solar meter asleep (normal overnight); counted as 0.", Profile = "solar_daily" };
        var text = InvestigationBrief.TelemetryExplanations(Status(("pv", quiet)), London);
        Assert.True(text.Contains("Solar meter (sensor.my_home_solar_generated): no number at 14:00, status idle, behaves like a solar daily. Expected, not a fault: Solar meter asleep"), text);
        // 02:00 BST: dark, but the stored readings say the sensor should be reporting, so it is not excused by the hour.
        var night = new LatestTelemetry(null, "kWh", afternoon.AddHours(-12), "unavailable", "sensor.grid_export_today", "HomeAssistant", "unavailable") { Expected = false, Reason = "Export meter offline.", LastObservedAt = afternoon.AddHours(-13) };
        var nightText = InvestigationBrief.TelemetryExplanations(Status(("grid_export", night)), London);
        Assert.Contains("Not expected: Export meter offline. Last good reading Mon 05 Oct 01:00.", nightText);
        Assert.True(InvestigationBrief.DailyCounterUnknown(Status(("grid_export", night))));
        Assert.Contains("template sensor", InvestigationBrief.DailyCounterPlaybook);
        var gaps = new Dictionary<string, EnergyMetricSummary> { ["grid_export"] = new(1.2, 0, 0.8, 1) { Gaps = [new(afternoon.AddHours(-14), afternoon.AddHours(-11), "offline", 0.4)] } };
        Assert.Contains("Today's unaccounted time: 00:00–03:00 (offline, the counter proves 0.40 kWh flowed)", InvestigationBrief.TelemetryExplanations(Status(("grid_export", night)), London, gaps));
    }

    // ---- Replay of the 5 Oct 02:10Z and 03:13Z checks against a store holding that night's plan and meter readings ----

    sealed class NoWriter : IPredbatClient
    {
        public bool Configured => true; public bool WritesEnabled => false;
        public Task<LiveSnapshot> CollectAsync(CancellationToken ct = default) => Task.FromResult(new LiveSnapshot([], null, "{}", "{}"));
        public Task ApplyAsync(List<Change> c, List<Setting> s, CancellationToken ct = default) => throw new InvalidOperationException();
    }
    sealed class Factory(HttpClient http) : IHttpClientFactory { public HttpClient CreateClient(string name) => http; }
    sealed class Provider(Func<string, string> reply) : HttpMessageHandler
    {
        public List<string> Prompts = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri!.Host == "raw.githubusercontent.com") return new(HttpStatusCode.ServiceUnavailable);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            var prompt = body.RootElement.GetProperty("messages")[0].GetProperty("content").GetString()!; Prompts.Add(prompt);
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { choices = new[] { new { finish_reason = "stop", message = new { content = reply(prompt) } } }, usage = new { prompt_tokens = 1000, completion_tokens = 50 } }), Encoding.UTF8, "application/json") };
        }
    }

    /// <summary>That night's numbers (BST 02:00–03:30): a charge, then Predbat exporting and recharging at 6.67p in / <paramref name="export"/>p out.</summary>
    static DateTimeOffset SeedNight(DataStore db, double export)
    {
        // Same numbers, replayed four hours ago so the brief's 24-hour window and the finished-slot rule both include them.
        var now = DateTimeOffset.UtcNow.AddHours(-4);
        var start = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, now.Minute < 30 ? 0 : 30, 0, TimeSpan.Zero);
        db.SavePlan(new PlanSnapshot { Source = "Predbat", At = start.AddMinutes(-5), CollectedAt = start.AddMinutes(-5), Slots = [
            new(start, .4, null, 0, null, 49, null, 6.67, export, "Chrg", .17),
            new(start.AddMinutes(30), .4, null, 0, null, 61, null, 6.67, export, "Exp", -.25),
            new(start.AddMinutes(60), .4, null, 0, null, 55, null, 6.67, export, "Chrg", .10)] });
        var perSlot = new Dictionary<string, double[]> { ["grid_import"] = [2.70, 1.22, 0.84], ["grid_export"] = [0, 1.70, 2.78], ["battery_charge"] = [2.58, 1.09, 0.75], ["battery_discharge"] = [0, 2.92, 3.03] };
        var samples = new List<TelemetrySample>();
        foreach (var (metric, deltas) in perSlot)
        {
            var total = 100.0;
            for (var minute = -5; minute <= 95; minute += 5)
            {
                if (minute > 0 && minute <= 90) total += deltas[(minute - 1) / 30] / 6;
                var at = start.AddMinutes(minute);
                samples.Add(new(metric, "sensor." + metric, at, Math.Round(total, 4), "kWh", "HomeAssistant", total.ToString(CultureInfo.InvariantCulture), "kWh", at));
            }
        }
        foreach (var (minute, soc) in new[] { (0, 49.0), (30, 61.0), (60, 51.0), (90, 31.0) })
            samples.Add(new("soc", "sensor.soc", start.AddMinutes(minute), soc, "%", "HomeAssistant", soc.ToString(CultureInfo.InvariantCulture), "%", start.AddMinutes(minute)));
        db.SaveTelemetry(samples);
        return start;
    }

    async Task<Investigation> Replay(double export, string title, string summary)
    {
        using var db = new DataStore(directory);
        var start = SeedNight(db, export);
        var finish = JsonSerializer.Serialize(new
        {
            action = "finish", verdict = "problem", title, summary, category = "Plan instability and unnecessary battery cycling",
            evidence = new[] { "Frozen plans for 02:30–03:00 changed from Export to Charge and back; import 6.67p/kWh." }, evidenceReferences = new[] { "configuration" },
            impactWindow = new { from = start, to = start.AddMinutes(90) }
        });
        using var provider = new Provider(_ => finish); using var http = new HttpClient(provider);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Ai:ApiKey"] = "fixture" }).Build();
        var state = new StateService(db, new NoWriter(), true);
        var analysis = new AnalysisService(state, db, new AiModelClient(http, new ChatGptAuth(http, Path.Combine(directory, "auth")), config) { RetryDelays = [] }, new Factory(http), config);
        await state.MutateAsync(s =>
        {
            s.Investigations.Clear(); s.Proposals.Clear(); s.Ai = new AiPreferences { Provider = "Api", Model = "fixture" };
            foreach (var (key, value) in new[] { ("battery_loss", "0.04"), ("battery_loss_discharge", "0.04"), ("inverter_loss", "0.01"), ("metric_battery_cycle", "0") })
                if (s.Settings.FirstOrDefault(x => x.Key == key) is { } existing) existing.Value = value; else s.Settings.Add(new Setting { Key = key, Value = value, Name = key });
        });
        await analysis.RunAsync(new("Why did the battery discharge during the cheap window?"));
        // The brief shows each half-hour's margin and what the export earned (1.70 kWh × 4.65p ≈ 7.9p at 12.28p).
        Assert.Matches(export > 12 ? @"export pays \+4\.7p/kWh, earned \+7\.9p" : @"export pays -0\.4p/kWh", provider.Prompts[0]);
        return state.Read().Investigations.Single();
    }

    [Fact]
    public async Task TheFiveOctoberChurnFindingsReplayAsIntendedArbitrageNotProblems()
    {
        var first = await Replay(12.28, "Plan churn discharged 2.92 kWh during a 6.67p cheap-import slot", "Predbat repeatedly reversed between charging and exporting during 02:30–03:00, cycling the battery instead of following one stable action.");
        Assert.Equal("Completed", first.Status); Assert.NotEqual("problem", first.Verdict); Assert.Equal("finding", first.Verdict); Assert.Equal("info", first.Severity);
        Assert.Contains("intended arbitrage, not a fault", first.Evidence[0]); Assert.Contains("working as intended", first.Headline);
        Assert.NotNull(first.ImpactPence);
        var second = await Replay(12.28, "Charge plan reversed for 20 minutes, exporting 2.78 kWh in a cheap slot", "Predbat switched from charging to export for 20 minutes during the 03:00–03:30 cheap slot.");
        Assert.NotEqual("problem", second.Verdict);
    }

    [Fact]
    public void CalibrationSuggestionsCarryTheMeasuredSeriesAndNeverAMadeUpSaving()
    {
        using var db = new DataStore(directory);
        var start = SeedNight(db, 12.28);
        var proposal = new Proposal { Changes = [new("battery_rate_max_scaling", "1.0", "0.67")] };
        var state = new AppState { Settings = [new Setting { Key = "battery_rate_max_scaling", Value = "1.0" }] };
        var series = ProposalEstimates.Calibration(db, proposal, state, DateTimeOffset.UtcNow, London, days: 1);
        Assert.NotNull(series);
        Assert.Equal("kW", series!.Unit); Assert.Equal(1.0, series.Assumed);
        // Two planned charge windows that night: 2.58 kWh in the first half-hour (5.16 kW) and 0.75 kWh in the last (1.5 kW).
        Assert.Equal([5.16, 1.5], series.Points.Select(p => p.Value));
        Assert.Equal(start, series.Points[0].At);
        Assert.StartsWith("Not estimated", ProposalEstimates.SavingEstimate(proposal));
        Assert.Contains("not what your home uses", ProposalEstimates.SavingEstimate(new Proposal { Changes = [new("load_scaling", "1.08", "1.0")] }));
        Assert.Null(ProposalEstimates.Calibration(db, new Proposal { Changes = [new("combine_charge_slots", "off", "on")] }, state, DateTimeOffset.UtcNow, London));
    }

    [Fact]
    public async Task ASensorFindingThatMentionsExportIsNotTreatedAsArbitrage()
    {
        var finding = await Replay(12.28, "Export meter reads unknown during the cheap window", "The grid export sensor went unavailable while Predbat exported in the cheap window.");
        Assert.Equal("problem", finding.Verdict);
    }

    [Fact]
    public async Task ExportingAtALossInACheapWindowIsStillAProblem()
    {
        var finding = await Replay(7.0, "Plan churn discharged 2.92 kWh during a 6.67p cheap-import slot", "Predbat exported stored energy at 7.0p after buying it at 6.67p.");
        Assert.Equal("problem", finding.Verdict); Assert.DoesNotContain(finding.Evidence, e => e.Contains("intended arbitrage"));
        // Measured against the plan this night's loss is under 5p, so it stays a problem but is marked as a small money effect.
        Assert.InRange(finding.ImpactPence!.Value, -5, 5); Assert.Equal("info", finding.Severity);
    }
}
