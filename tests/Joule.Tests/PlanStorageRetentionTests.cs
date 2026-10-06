using System.Text;
using System.Text.Json.Nodes;
using DuckDB.NET.Data;
using Joule;
using Xunit;
using Xunit.Abstractions;

namespace Joule.Tests;

/// <summary>Source snapshots stop growing without limit: compaction, duplicate skipping, changed-only observations and a
/// retention pass that waits for the backfill, defaults to a dry run and never removes evidence anything still needs.</summary>
public class PlanStorageRetentionTests(ITestOutputHelper output) : IDisposable
{
    readonly string directory = Path.Combine(Path.GetTempPath(), "predbat-storage-" + Guid.NewGuid().ToString("N"));
    static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-20T12:00:00Z");

    static string State(string status = "Demand", int html = 4000) => new JsonObject
    {
        ["predbat.status"] = new JsonObject { ["state"] = status, ["attributes"] = new JsonObject { ["friendly_name"] = "Status" } },
        ["predbat.plan_html"] = new JsonObject { ["state"] = "", ["attributes"] = new JsonObject { ["html"] = new string('x', html), ["raw"] = new string('y', html), ["friendly_name"] = "Plan" } },
        ["predbat.cost_yesterday"] = new JsonObject { ["state"] = "1.2", ["attributes"] = new JsonObject { ["results"] = new string('z', html), ["unit_of_measurement"] = "£" } },
        ["predbat.savings_yesterday_predbat"] = new JsonObject { ["state"] = "0.4", ["attributes"] = new JsonObject { ["results"] = new string('w', html) } },
        ["predbat.load_energy_actual"] = new JsonObject { ["state"] = "3", ["attributes"] = new JsonObject { ["results"] = new JsonObject { ["2026-10-01T10:00:00Z"] = 1 } } },
        ["sensor.predbat_load_ml_forecast"] = new JsonObject { ["state"] = "active", ["attributes"] = new JsonObject { ["results"] = new JsonObject { ["2026-10-01T10:00:00Z"] = 1 } } },
    }.ToJsonString();

    [Fact]
    public void CompactionDropsDisplayBlobsButKeepsDiagnosticCurves()
    {
        var compact = JsonNode.Parse(DataStore.CompactState(State()))!.AsObject();
        Assert.Null(compact["predbat.plan_html"]!["attributes"]!["html"]);
        Assert.Equal("Plan", (string?)compact["predbat.plan_html"]!["attributes"]!["friendly_name"]);
        Assert.Null(compact["predbat.cost_yesterday"]!["attributes"]!["results"]);
        Assert.Equal("£", (string?)compact["predbat.cost_yesterday"]!["attributes"]!["unit_of_measurement"]);
        Assert.Equal("1.2", (string?)compact["predbat.cost_yesterday"]!["state"]);
        Assert.Null(compact["predbat.savings_yesterday_predbat"]!["attributes"]!["results"]);
        Assert.NotNull(compact["predbat.load_energy_actual"]!["attributes"]!["results"]);
        Assert.NotNull(compact["sensor.predbat_load_ml_forecast"]!["attributes"]!["results"]);
        Assert.Equal("Demand", (string?)compact["predbat.status"]!["state"]);
        Assert.True(DataStore.CompactState(State()).Length < State().Length / 3);
        Assert.Equal("{broken", DataStore.CompactState("{broken"));

        var plan = DataStore.CompactPlan(PlanCaptureTests.PlanData);
        Assert.DoesNotContain("state_color", plan); Assert.DoesNotContain("state_html", plan); Assert.DoesNotContain("state2_color", plan);
        // Everything the parser reads survives.
        var parsed = PredbatClient.ParsePlan(JsonNode.Parse(plan)!.AsObject())!;
        Assert.Equal("charge-export", parsed.Slots[6].ActionKey); Assert.Equal("18:10", parsed.Slots[0].SplitTime);
    }

    [Fact]
    public void IdenticalConsecutiveSnapshotsAndUnchangedObservationsAreSkipped()
    {
        using var db = new DataStore(directory);
        db.SaveSourceAt(Now, State(), "{}");
        db.SaveSourceAt(Now.AddMinutes(5), State(), "{}");
        Assert.Equal(1, Count("SELECT count(*) FROM source_snapshots"));
        db.SaveSourceAt(Now.AddMinutes(10), State("Charging"), "{}");
        Assert.Equal(2, Count("SELECT count(*) FROM source_snapshots"));
        // Only the status changed, so only it gets a new observation; an hour later every entity is written again.
        Assert.Equal(6 + 1, db.Query("SELECT * FROM observations").Count);
        db.SaveSourceAt(Now.AddMinutes(75), State("Charging"), "{}");
        Assert.Equal(7 + 6, db.Query("SELECT * FROM observations").Count);
        Assert.Equal(2, Count("SELECT count(*) FROM source_snapshots"));
        Assert.Equal(0, Count("SELECT count(*) FROM source_snapshots WHERE state_json LIKE '%xxxx%'"));
    }

    [Fact]
    public void AnUnchangedPlanPollStoresNoSecondSnapshot()
    {
        using var db = new DataStore(directory);
        var plan = new PlanSnapshot { Id = "p1", Source = "Predbat", At = Now, Slots = [new(Now, 1, null, 0, null, 50, null, 7, 15, "Chrg", 0)] };
        db.SavePlan(plan, State(), PlanCaptureTests.PlanData);
        db.SavePlan(new PlanSnapshot { Id = "p1-again", Source = "Predbat", At = Now, Slots = plan.Slots }, State("Charging"), PlanCaptureTests.PlanData);
        Assert.Equal(1, Count("SELECT count(*) FROM source_snapshots"));
        Assert.Equal(1, Count("SELECT count(*) FROM plans"));
        Assert.Equal(1, Count("SELECT count(*) FROM observations WHERE value='Charging'"));
    }

    void Seed(DataStore db)
    {
        // A stored plan with its own snapshot, plus older and newer raw snapshots.
        var plan = new PlanSnapshot { Id = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Source = "Predbat", At = Now.AddDays(-20), CollectedAt = Now.AddDays(-20), Slots = [new(Now.AddDays(-20), 1, null, 0, null, 50, null, 7, 15, "Chrg", 0)] };
        db.SavePlan(plan);
        db.SaveSourceAt(Now.AddDays(-20), State("plan"), PlanCaptureTests.PlanData, plan.Id);
        db.SaveSourceAt(Now.AddDays(-19), State("old-1"), "{}", "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
        db.SaveSourceAt(Now.AddDays(-18), State("old-cited"), "{}", "cccccccccccccccccccccccccccccccc");
        db.SaveSourceAt(Now.AddDays(-16), State("old-in-trial"), "{}", "dddddddddddddddddddddddddddddddd");
        db.SaveSourceAt(Now.AddDays(-2), State("recent"), "{}", "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee");
        Assert.Equal(1, db.BackfillPlanDetails());
        Execute("INSERT INTO investigation_evidence VALUES ('inv-1', '[{\"kind\":\"snapshots\",\"resultJson\":\"{\\\"id\\\":\\\"cccccccccccccccccccccccccccccccc\\\"}\"}]')");
        // Old snapshots stored before compaction existed.
        foreach (var (id, status) in new[] { ("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "old-1"), ("cccccccccccccccccccccccccccccccc", "old-cited") })
            Execute("UPDATE source_snapshots SET compacted=false, state_json='" + State(status, 20000).Replace("'", "''") + "' WHERE id='" + id + "'");
    }

    [Fact]
    public void RetentionDefaultsToADryRunThatChangesNothing()
    {
        using var db = new DataStore(directory); Seed(db);
        var before = Count("SELECT count(*) FROM source_snapshots");
        var result = db.RunSnapshotRetention("anything-unrecognised", Now, 14, Now.AddDays(-17));
        Assert.Equal(DataStore.RetentionDryRun, result.Mode);
        Assert.True(result.Ran);
        Assert.Equal(1, result.Candidates); // old-1 only
        Assert.Equal(2, result.Protected);  // cited by an investigation, inside an open trial's window
        Assert.Equal(0, result.Deleted);
        Assert.Equal(1, result.Uncompacted); Assert.Equal(0, result.Compacted);
        Assert.Contains("Dry run", result.Note);
        Assert.Equal(before, Count("SELECT count(*) FROM source_snapshots"));
        Assert.Equal(2, Count("SELECT count(*) FROM source_snapshots WHERE compacted=false"));
        Assert.Same(result, db.LastRetention);
    }

    [Fact]
    public void EnforcedRetentionKeepsPlansCitedEvidenceTrialWindowsAndRecentSnapshots()
    {
        using var db = new DataStore(directory); Seed(db);
        var result = db.RunSnapshotRetention(DataStore.RetentionEnforce, Now, 14, Now.AddDays(-17));
        Assert.Equal(1, result.Deleted); Assert.Equal(1, result.Compacted);
        Assert.Equal(0, Count("SELECT count(*) FROM source_snapshots WHERE state_json LIKE '%xxxxxxxxxx%'"));
        Assert.Equal(0, Count("SELECT count(*) FROM source_snapshots WHERE id='bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb'"));
        foreach (var kept in new[] { "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "cccccccccccccccccccccccccccccccc", "dddddddddddddddddddddddddddddddd", "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee" })
            Assert.Equal(1, Count($"SELECT count(*) FROM source_snapshots WHERE id='{kept}'"));
        Assert.Equal(0, Count("SELECT count(*) FROM source_snapshots WHERE compacted=false"));
        // The plan's own snapshot still backfills.
        Assert.True(db.PlanDetailBackfillComplete());
    }

    [Fact]
    public void RetentionWaitsForThePlanDetailBackfill()
    {
        using var db = new DataStore(directory); Seed(db);
        Execute("UPDATE plans SET detail_version=0");
        var result = db.RunSnapshotRetention(DataStore.RetentionEnforce, Now, 14);
        Assert.False(result.Ran); Assert.Equal(0, result.Deleted); Assert.Contains("backfill", result.Note);
        Assert.Equal(5, Count("SELECT count(*) FROM source_snapshots"));
        db.BackfillPlanDetails();
        Assert.True(db.RunSnapshotRetention(DataStore.RetentionEnforce, Now, 14).Ran);
        Assert.False(db.RunSnapshotRetention(DataStore.RetentionOff, Now, 14).Ran);
    }

    [Fact]
    public void StorageReportIncludesDatabaseSize()
    {
        using var db = new DataStore(directory); Seed(db); db.Checkpoint();
        var report = db.ReadStorageReport(DataStore.RetentionDryRun, 14);
        Assert.True(report.DbSizeBytes > 0);
        Assert.Equal(5, report.Snapshots);
        Assert.Equal(1, report.DistinctPlanSnapshots);
        Assert.True(report.PlanDetailBackfillComplete);
        Assert.Equal("dry-run", report.RetentionMode);
    }

    /// <summary>Simulates one day of 5-minute polls with Predbat-sized payloads (186 KB HTML plan, 134/92 KB yesterday tables,
    /// a 270 KB plan that changes every 10 minutes and ~280 entities). Set JOULE_STORAGE_BENCH=1 to run (about a minute).</summary>
    [Fact]
    public void SimulatedDayStaysUnderThirtyMegabytes()
    {
        if (Environment.GetEnvironmentVariable("JOULE_STORAGE_BENCH") != "1") return;
        using var db = new DataStore(directory);
        var rows = JsonNode.Parse(PlanCaptureTests.PlanData)!["plan"]!["rows"]!.AsArray();
        string Plan(int n)
        {
            var plan = JsonNode.Parse(PlanCaptureTests.PlanData)!.AsObject();
            var list = new JsonArray();
            for (var i = 0; i < 96; i++) { var row = rows[i % rows.Count]!.DeepClone().AsObject(); row["time"] = Now.AddMinutes(30 * i).ToString("yyyy-MM-ddTHH:mm:ss+0000"); row["load_forecast"] = 0.3 + n % 7 * .01; foreach (var c in new[] { "rate_color_import", "rate_color_export", "pv_color", "load_color", "soc_color", "cost_color", "clipped_color", "extra_color", "car_color" }) row[c] = "#FFFFFF"; list.Add(row); }
            plan["plan"]!["rows"] = list; plan["plan"]!["timestamp"] = Now.AddMinutes(10 * n).ToString("O");
            plan["yesterday"] = new JsonObject { ["rows"] = list.DeepClone() }; plan["baseline"] = new JsonObject { ["rows"] = list.DeepClone() };
            return plan.ToJsonString();
        }
        string BigState(int n)
        {
            var state = JsonNode.Parse(State(n % 3 == 0 ? "Charging" : "Demand", 0))!.AsObject();
            state["predbat.plan_html"]!["attributes"]!["html"] = new string('h', 186_000);
            state["predbat.cost_yesterday"]!["attributes"]!["results"] = new string('c', 134_000);
            state["predbat.savings_yesterday_predbat"]!["attributes"]!["results"] = new string('s', 92_000);
            for (var e = 0; e < 280; e++) state[$"predbat.entity_{e}"] = new JsonObject { ["state"] = e % 20 == 0 ? (n * e % 97).ToString() : "steady", ["attributes"] = new JsonObject { ["friendly_name"] = $"Entity {e}", ["unit_of_measurement"] = "kWh" } };
            return state.ToJsonString();
        }
        db.Checkpoint();
        var before = new FileInfo(Path.Combine(directory, "predbat.duckdb")).Length;
        for (var poll = 0; poll < 288; poll++)
        {
            var planNumber = poll / 2;
            var plan = PredbatClient.ParsePlan(JsonNode.Parse(Plan(planNumber))!.AsObject())!;
            plan.Id = Guid.NewGuid().ToString("N");
            db.SavePlan(plan, BigState(poll), Plan(planNumber));
        }
        db.Checkpoint();
        var after = new FileInfo(Path.Combine(directory, "predbat.duckdb")).Length;
        var report = db.ReadStorageReport(DataStore.RetentionDryRun, 14);
        output.WriteLine($"Simulated day: {(after - before) / 1048576.0:0.0} MB on disk, {report.Snapshots} snapshots ({report.SnapshotBytes / 1048576.0:0.0} MB stored; state {Count("SELECT CAST(coalesce(sum(octet_length(state_gz)),0)+coalesce(sum(strlen(state_json)),0) AS BIGINT) FROM source_snapshots") / 1048576.0:0.0} MB, plan {Count("SELECT CAST(coalesce(sum(octet_length(plan_gz)),0)+coalesce(sum(strlen(plan_json)),0) AS BIGINT) FROM source_snapshots") / 1048576.0:0.0} MB), {report.Observations} observations, {report.Plans} plans ({Count("SELECT CAST(sum(length(payload)) AS BIGINT) FROM plans") / 1048576.0:0.0} MB payload), {report.PlanSlots} slots.");
        Assert.True(after - before < 30L * 1024 * 1024, $"Grew {(after - before) / 1048576.0:0.0} MB");
    }

    sealed class PlanClient : IPredbatClient
    {
        public bool Configured => true;
        public bool WritesEnabled => false;
        public Task<LiveSnapshot> CollectAsync(CancellationToken ct)
        {
            var plan = PredbatClient.ParsePlan(JsonNode.Parse(PlanCaptureTests.PlanData)!.AsObject())!; plan.CollectedAt = DateTimeOffset.UtcNow;
            return Task.FromResult(new LiveSnapshot([new Setting { Key = "load_scaling", EntityId = "input_number.predbat_load_scaling", Value = "1.0", Min = 0, Max = 2, Step = .01 }], plan, State(), PlanCaptureTests.PlanData));
        }
        public Task ApplyAsync(List<Change> changes, List<Setting> settings, CancellationToken ct) => throw new NotSupportedException();
    }

    [Fact]
    public async Task LiveCollectionBackfillsAndRunsADryRunRetentionByDefault()
    {
        using var db = new DataStore(directory);
        // An older plan stored before detail capture, with its raw snapshot retained.
        db.SavePlan(new PlanSnapshot { Id = "older", Source = "Predbat", At = Now.AddDays(-45), CollectedAt = Now.AddDays(-45), Slots = [new(Now.AddDays(-45), 1, null, 0, null, 50, null, 7, 15, "Export", 0)] });
        db.SaveSourceAt(Now.AddDays(-45), State("old"), PlanCaptureTests.PlanData, "older");
        db.SaveSourceAt(Now.AddDays(-44), State("older raw"), "{}");
        var service = new StateService(db, new PlanClient(), false);
        await service.CollectAsync();
        Assert.Equal(DataStore.RetentionDryRun, service.RetentionMode);
        Assert.True(db.PlanDetailBackfillComplete());
        Assert.NotNull(db.LastRetention);
        Assert.Equal(DataStore.RetentionDryRun, db.LastRetention!.Mode);
        Assert.Equal(1, db.LastRetention.Candidates);
        Assert.Equal(0, db.LastRetention.Deleted);
        Assert.Equal(3, Count("SELECT count(*) FROM source_snapshots"));
        Assert.Equal(2, Count("SELECT count(*) FROM plans WHERE detail_version=1"));
    }

    long Count(string sql)
    {
        using var connection = new DuckDBConnection($"Data Source={Path.Combine(directory, "predbat.duckdb")}"); connection.Open();
        using var command = connection.CreateCommand(); command.CommandText = sql; return Convert.ToInt64(command.ExecuteScalar());
    }
    void Execute(string sql)
    {
        using var connection = new DuckDBConnection($"Data Source={Path.Combine(directory, "predbat.duckdb")}"); connection.Open();
        using var command = connection.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery();
    }
    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
}
