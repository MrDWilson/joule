using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using DuckDB.NET.Data;
using Microsoft.Extensions.Configuration;
using Joule;
using Xunit;

namespace Joule.Tests;

/// <summary>Plan capture keeps Predbat's whole row: split states, targets, reasons, overrides and rate types.</summary>
public class PlanCaptureTests : IDisposable
{
    readonly string directory = Path.Combine(Path.GetTempPath(), "predbat-plan-capture-" + Guid.NewGuid().ToString("N"));

    // Shaped like Predbat v9.3.5 /api/plan_data (live 5 Oct 2026), values synthesised. Templates use test wording.
    internal const string PlanData = """
    {"unchanged":false,"plan":{"timestamp":"2026-10-05T10:50:00.876219+01:00","soc_max":13.5,
     "reason_templates":{"demand_before_export_falling":"Until {split_time}, the battery covers the house.","export_high_rate":"Exporting to {target_percent}% at {rate_kw}kW ({rate}p).","charge_low_rate":"Charging to {target_percent}% at {rate_kw}kW ({rate}p).","hold_for_car":"Held for the car.","freeze_export":"Solar goes to the grid.","hold_charge_at_target":"Already at {target_percent}%.","demand_falling":"Battery covers the house.","manual_override_charge":"You set this slot to charge."},
     "rows":[
      {"time":"2026-10-05T18:00:00+0100","import_rate":31.73,"export_rate":24.9,"import_rate_adjusted":33.39,"export_rate_adjusted":23.66,"state":"Exp","state_target":"34","state_override":"","state_mixed":null,"state_html":" &searr;</td><td bgcolor=#FFFF00>Exp&searr;","state_text":" &searr;","state_color":"#FFFFFF","state2_text":"Exp&searr;","state2_color":"#FFFF00","reasons":[{"code":"demand_before_export_falling","params":{"split_time":"18:10"}},{"code":"export_high_rate","params":{"target_percent":34,"rate":"24.90","rate_kw":"5.00"}}],"pv_forecast":0.0,"pv_forecast10":0.0,"load_forecast":0.62,"load_forecast10":0.7,"clipped":0,"car_charging":0.0,"soc_percent":71,"soc_change":-2.04,"cost_change":-0.31,"total_cost":1.63,"split":true},
      {"time":"2026-10-05T18:30:00+0100","import_rate":31.73,"export_rate":26.23,"state":"Exp","state_target":"34","state_override":"","state_mixed":null,"state_text":"Exp&searr;","state2_text":null,"reasons":[{"code":"export_high_rate","params":{"target_percent":34,"rate":"26.23","rate_kw":"5.00"}}],"pv_forecast":0.0,"load_forecast":0.6,"soc_percent":56,"soc_change":-2.73,"cost_change":-0.6,"total_cost":1.32,"split":false},
      {"time":"2026-10-05T19:00:00+0100","import_rate":31.73,"export_rate":12.0,"state":"Demand","state_target":"","state_override":"","state_text":"&#128663;","state2_text":null,"reasons":[{"code":"hold_for_car","params":{}}],"pv_forecast":0.0,"load_forecast":0.5,"car_charging":3.7,"soc_percent":36,"soc_change":0.0,"cost_change":0.2,"total_cost":0.72,"split":false},
      {"time":"2026-10-05T19:30:00+0100","import_rate":31.73,"export_rate":12.0,"state":"FrzExp","state_target":"","state_override":"","state_text":"FrzExp&rarr;","state2_text":null,"reasons":[{"code":"freeze_export","params":{}}],"pv_forecast":0.1,"load_forecast":0.5,"soc_percent":36,"soc_change":-0.3,"cost_change":0.0,"total_cost":0.92,"split":false},
      {"time":"2026-10-05T20:00:00+0100","import_rate":31.73,"export_rate":12.99,"state":"Exp","state_target":"4","state_override":"","state_text":"Exp&searr;","state2_text":null,"reasons":[{"code":"export_high_rate","params":{"target_percent":4,"rate":"12.99","rate_kw":"5.00"}}],"pv_forecast":0.0,"load_forecast":0.4,"soc_percent":4,"soc_change":0.0,"cost_change":0.13,"total_cost":0.92,"split":false},
      {"time":"2026-10-05T20:30:00+0100","import_rate":31.73,"export_rate":12.99,"state":"HoldChrg","state_target":"36","state_override":"","state_text":"HoldChrg&rarr;","state2_text":null,"reasons":[{"code":"hold_charge_at_target","params":{"target_percent":36}}],"pv_forecast":0.0,"load_forecast":0.4,"soc_percent":4,"soc_change":0.0,"cost_change":0.13,"total_cost":1.05,"split":false},
      {"time":"2026-10-05T23:30:00+0100","import_rate":6.67,"export_rate":12.99,"import_rate_adjusted":7.02,"export_rate_adjusted":12.34,"export_rate_adjust_type":"copy","state":"Exp","state_target":"15","state_override":"","state_mixed":null,"state_text":"Chrg&nearr;","state2_text":"Exp&searr;","reasons":[{"code":"charge_low_rate","params":{"target_percent":19,"rate":"6.67","rate_kw":"5.00"}},{"code":"export_high_rate","params":{"target_percent":15,"rate":"12.99","rate_kw":"5.00"}}],"pv_forecast":0.0,"load_forecast":0.3,"soc_percent":4,"soc_change":1.54,"cost_change":0.11,"total_cost":1.18,"split":true},
      {"time":"2026-10-06T00:00:00+0100","import_rate":6.67,"export_rate":12.32,"import_rate_adjust_type":"copy","export_rate_adjust_type":"copy","state":"Chrg","state_target":"100","state_override":"Manual charge","state_text":"Chrg&nearr; &#8526;","state2_text":null,"reasons":[{"code":"charge_low_rate","params":{"target_percent":100,"rate":"6.67","rate_kw":"5.00"}},{"code":"manual_override_charge","params":{}}],"pv_forecast":0.0,"load_forecast":0.3,"soc_percent":15,"soc_change":2.4,"cost_change":0.21,"total_cost":0.0,"split":false}
     ]}}
    """;

    static PlanSnapshot Parse(string json = PlanData) => PredbatClient.ParsePlan(JsonNode.Parse(json)!.AsObject())!;

    [Fact]
    public void SplitChargeThenExportSlotIsChargeExportNotExport()
    {
        var slot = Parse().Slots.Single(x => x.Time == DateTimeOffset.Parse("2026-10-05T22:30:00Z"));
        Assert.Equal("Exp", slot.RawAction);
        Assert.Equal("charge-export", slot.ActionKey);
        Assert.Equal("charge-export", slot.Action);
        Assert.Equal("charge", slot.PrimaryAction);
        Assert.Equal("export", slot.SecondaryAction);
        Assert.Equal("Exp", slot.State2);
        Assert.Equal("Charge, then export", slot.ActionLabel);
        Assert.Equal(15, slot.TargetPercent);
        Assert.Equal("Charging to 19% at 5.00kW (6.67p). Exporting to 15% at 5.00kW (12.99p).", slot.ReasonText);
        Assert.Equal("copy", slot.ExportRateType);
        Assert.True(slot.RateEstimated);
        Assert.Equal(7.02, slot.ImportRateAdjusted);
    }

    [Fact]
    public void DemandThenExportSplitKeepsTheSplitTime()
    {
        var slot = Parse().Slots[0];
        Assert.Equal("export", slot.ActionKey);
        Assert.Equal("demand", slot.PrimaryAction);
        Assert.Equal("export", slot.SecondaryAction);
        Assert.Equal("18:10", slot.SplitTime);
        Assert.Equal("Power your home until 18:10, then export battery to the grid", slot.ActionLabel);
        Assert.Equal(34, slot.TargetPercent);
        Assert.Equal(56, slot.SocForecastEnd);
        Assert.Equal(-2.04, slot.SocChangeKwh);
        Assert.Equal(0.0, slot.Pv10); Assert.Equal(0.7, slot.Load10);
        Assert.Equal(1.63, slot.TotalCost);
        Assert.Equal(2, slot.Reasons!.Count);
        Assert.Equal("34", slot.Reasons[1].Params["target_percent"]);
    }

    [Fact]
    public void CarHoldFreezeExportHoldChargeAndOverridesAreNamed()
    {
        var slots = Parse().Slots;
        var car = slots[2];
        Assert.Equal("demand", car.ActionKey); Assert.Equal("hold-for-car", car.ActionId);
        Assert.Equal("Hold battery for the car", car.ActionLabel); Assert.Equal(3.7, car.CarKwh); Assert.Equal("Held for the car.", car.ReasonText);
        var freeze = slots[3];
        Assert.Equal("FrzExp", freeze.RawAction); Assert.Equal("freeze-export", freeze.Action); Assert.Equal("Export solar, don't charge battery", freeze.ActionLabel);
        Assert.Null(freeze.TargetPercent);
        var held = slots[5];
        Assert.Equal("hold-charge", held.ActionKey); Assert.Equal(36, held.TargetPercent);
        var manual = slots[^1];
        Assert.Equal("charge", manual.ActionKey); Assert.Equal("Manual charge", manual.Override);
        Assert.Equal(100, manual.TargetPercent);
        Assert.Contains("You set this slot to charge.", manual.ReasonText);
        Assert.Equal("copy", manual.ImportRateType);
        // The last slot's end level comes from its own planned change and the battery size.
        Assert.Equal(Math.Round(15 + 2.4 / 13.5 * 100, 2), manual.SocForecastEnd);
    }

    [Fact]
    public void ExportAtTheReserveIsShownAsAHeldExport()
    {
        var slot = Parse().Slots[4];
        Assert.Equal("Exp", slot.RawAction);
        Assert.Equal("hold-export", slot.ActionKey);
        Assert.Equal("Export paused at minimum level", slot.ActionLabel);
        Assert.Equal(4, slot.TargetPercent);
        // A real export that falls towards its target stays an export.
        Assert.Equal("export", Parse().Slots[1].ActionKey);
    }

    [Fact]
    public void OlderPlanRowsWithoutDetailStillParse()
    {
        var plan = Parse("""{"plan":{"timestamp":"2026-10-02T10:00:00+00:00","rows":[{"time":"2026-10-02T10:30:00+0000","load_forecast":0.6,"pv_forecast":0.3,"soc_percent":72,"import_rate":25,"export_rate":15,"state":"Chrg","cost_change":0.12},{"time":"2026-10-02T11:00:00+0000","load_forecast":0.6,"pv_forecast":0.3,"soc_percent":80,"import_rate":25,"export_rate":15,"state":"Mystery","cost_change":0.12}]}}""");
        var slot = plan.Slots[0];
        Assert.Equal("charge", slot.Action); Assert.Equal("Chrg", slot.RawAction); Assert.Null(slot.TargetPercent); Assert.Null(slot.ReasonText); Assert.Null(slot.RateEstimated);
        Assert.Equal(80, slot.SocForecastEnd);
        Assert.Equal("Mystery", plan.Slots[1].Action); Assert.Equal("unknown", plan.Slots[1].ActionKey); Assert.Equal("Other Predbat state", plan.Slots[1].ActionLabel);
    }

    [Fact]
    public void StoredPlansAndSlotsCarryTheDetailColumns()
    {
        using var db = new DataStore(directory);
        var plan = Parse(); plan.CollectedAt = DateTimeOffset.UtcNow;
        db.SavePlan(plan, "{}", PlanData);
        var stored = db.GetPlan(plan.Id)!;
        Assert.Equal("charge-export", stored.Slots[6].ActionKey);
        Assert.Equal(15, stored.Slots[6].TargetPercent);
        Assert.NotNull(stored.Slots[6].ReasonText);
        var rows = db.Query("SELECT action, raw_action, action_key, secondary_action, target_percent, reason_text, export_rate_type FROM plan_slots WHERE raw_action='Exp' AND secondary_action IS NOT NULL ORDER BY time");
        Assert.Equal(2, rows.Count);
        Assert.Equal("export", rows[0]["action"]); Assert.Equal("charge-export", rows[1]["action_key"]); Assert.Equal(15d, rows[1]["target_percent"]);
        Assert.Equal("copy", rows[1]["export_rate_type"]);
    }

    [Fact]
    public void LegacyStoredActionsAreNormalisedOnceAndReadConsistently()
    {
        var at = DateTimeOffset.Parse("2026-10-02T10:00:00Z");
        using (var db = new DataStore(directory))
        {
            // A plan stored by the previous version: hand-mapped "Charge" next to raw "FrzExp", no detail columns.
            db.SavePlan(new PlanSnapshot { Id = "legacy", Source = "Predbat", At = at, CollectedAt = at, Slots = [new(at, 1, null, 0, null, 50, null, 7, 15, "Charge", .1), new(at.AddMinutes(30), 1, null, 0, null, 60, null, 7, 15, "FrzExp", 0)] });
        }
        Execute("UPDATE plan_slots SET action=raw_action, action_key=NULL, raw_action=NULL, primary_action=NULL; DELETE FROM store_migrations WHERE name='plan-actions-v1'");
        Execute("UPDATE plans SET payload=replace(replace(payload, '\"action\":\"charge\"', '\"action\":\"Charge\"'), '\"action\":\"freeze-export\"', '\"action\":\"FrzExp\"')");
        Execute("UPDATE plans SET payload=regexp_replace(payload, ',\"(rawAction|actionKey|actionId|actionLabel|primaryAction)\":(\"[^\"]*\"|null)', '', 'g')");
        using var reopened = new DataStore(directory);
        var rows = reopened.Query("SELECT action, raw_action, action_key FROM plan_slots ORDER BY time");
        Assert.Equal("charge", rows[0]["action"]); Assert.Equal("Charge", rows[0]["raw_action"]);
        Assert.Equal("freeze-export", rows[1]["action"]); Assert.Equal("FrzExp", rows[1]["raw_action"]);
        var plan = reopened.GetPlan("legacy")!;
        Assert.Equal("charge", plan.Slots[0].Action); Assert.Equal("Charge", plan.Slots[0].RawAction); Assert.Equal("Charge from the grid", plan.Slots[0].ActionLabel);
        Assert.Equal("freeze-export", plan.Slots[1].ActionKey);
        Assert.Single(reopened.Query("SELECT count(DISTINCT action) AS n FROM plan_slots WHERE action IN ('charge','Charge','Chrg')"), r => Convert.ToInt64(r["n"]) == 1);
    }

    [Fact]
    public void RetainedSnapshotsBackfillSplitStatesTargetsAndReasons()
    {
        var plan = Parse(); plan.Id = "backfill"; plan.CollectedAt = DateTimeOffset.UtcNow;
        // Store the plan the way the previous version did: only state, mapped through Chrg/Exp, no detail.
        foreach (var i in Enumerable.Range(0, plan.Slots.Count))
        {
            var s = plan.Slots[i]; var old = s.RawAction switch { "Chrg" => "Charge", "Exp" => "Export", var raw => raw! };
            plan.Slots[i] = new PlanSlot(s.Time, s.LoadForecast, null, s.PvForecast, null, s.SocForecast, null, s.ImportRate, s.ExportRate, old, s.Cost, s.DurationMinutes);
        }
        using (var db = new DataStore(directory)) db.SavePlan(plan, "{}", PlanData);
        Execute("UPDATE plans SET detail_version=0");
        Execute("UPDATE plan_slots SET target_percent=NULL, reason_text=NULL, secondary_action=NULL");
        using var reopened = new DataStore(directory);
        Assert.False(reopened.PlanDetailBackfillComplete());
        Assert.Equal(1, reopened.BackfillPlanDetails());
        Assert.True(reopened.PlanDetailBackfillComplete());
        Assert.Equal(0, reopened.BackfillPlanDetails());
        var slot = reopened.GetPlan("backfill")!.Slots[6];
        Assert.Equal("charge-export", slot.ActionKey); Assert.Equal("Exp", slot.RawAction); Assert.Equal(15, slot.TargetPercent); Assert.NotNull(slot.ReasonText);
        var row = Assert.Single(reopened.Query("SELECT action_key, target_percent, captured_at FROM plan_slots WHERE secondary_action='export' AND primary_action='charge'"));
        Assert.Equal(15d, row["target_percent"]);
        Assert.NotNull(row["captured_at"]);
        Assert.Equal(plan.Slots.Count, reopened.Query("SELECT * FROM plan_slots WHERE snapshot_id='backfill'").Count);
    }

    [Fact]
    public async Task CollectionThroughTheClientCapturesTheWholeRow()
    {
        var handler = new Fixture(path => path == "/api/state" ? "{}" : PlanData);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Predbat:BaseUrl"] = "http://predbat.test" }).Build();
        var result = await new PredbatClient(new HttpClient(handler), config).CollectAsync(default);
        Assert.Equal("charge-export", result.Plan!.Slots[6].ActionKey);
        var json = JsonSerializer.Serialize(result.Plan, JsonDefaults.Options);
        foreach (var field in new[] { "actionKey", "actionLabel", "targetPercent", "reasonText", "state2", "rawAction", "splitTime", "secondaryAction" }) Assert.Contains($"\"{field}\":", json);
    }

    /// <summary>Set JOULE_LIVE_PLAN to a saved Predbat /api/plan_data response to check a real plan parses completely.</summary>
    [Fact]
    public void ARealPredbatPlanParsesWithLabelsTargetsAndReasons()
    {
        var path = Environment.GetEnvironmentVariable("JOULE_LIVE_PLAN");
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
        var plan = Parse(File.ReadAllText(path));
        Assert.NotEmpty(plan.Slots);
        Assert.All(plan.Slots, s =>
        {
            Assert.NotEqual("unknown", s.ActionKey);
            Assert.False(string.IsNullOrWhiteSpace(s.ActionLabel));
            Assert.False(string.IsNullOrWhiteSpace(s.ReasonText));
            if (s.ActionKey is "charge" or "export" or "charge-export") Assert.NotNull(s.TargetPercent);
        });
        Console.WriteLine(string.Join("\n", plan.Slots.Where(s => s.ActionKey != "demand").Select(s => $"{s.Time:dd HH:mm} {s.RawAction,-8} {s.ActionKey,-14} {s.TargetPercent,5} {s.ActionLabel} | {s.ReasonText}")));
    }

    void Execute(string sql)
    {
        using var connection = new DuckDBConnection($"Data Source={Path.Combine(directory, "predbat.duckdb")}"); connection.Open();
        using var command = connection.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery();
    }

    sealed class Fixture(Func<string, string> reply) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(reply(request.RequestUri!.AbsolutePath)) });
    }

    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
}
