using Joule;
using Xunit;
namespace Joule.Tests;

public class StorageTests : IDisposable
{
    readonly string path = Path.Combine(Path.GetTempPath(), "predbat-tests-" + Guid.NewGuid().ToString("N"));
    [Fact] public void ChangesAndPermissionsSurviveRestart()
    {
        using (var db = new DataStore(path))
        {
            var s = DemoData.Create(); ChangeEngine.Permission(s, "load_scaling", true);
            ChangeEngine.Edit(s, "load_scaling", "1.00", 1, "Test"); db.Save(s);
        }
        using var reopened = new DataStore(path);
        var state = reopened.Load()!;
        Assert.Equal(2, state.Revision);
        Assert.True(state.Settings.Single(x => x.Key == "load_scaling").AutoAllowed);
    }
    [Fact] public void FrozenPlanCanBeReopenedAndQueried()
    {
        using var db = new DataStore(path); var p = DemoData.Plan(1);
        db.SavePlan(p);
        Assert.Equal(p.Slots.Count, db.GetPlan(p.Id)!.Slots.Count);
        var result = db.Query("SELECT count(*) AS count FROM plan_slots");
        Assert.Contains(p.Slots.Count.ToString(), System.Text.Json.JsonSerializer.Serialize(result));
    }
    [Theory] [InlineData("DELETE FROM plan_slots")] [InlineData("SELECT 1; DROP TABLE plans")] [InlineData("COPY (SELECT 1) TO '/tmp/no'")]
    public void EvidenceQueriesCannotMutate(string sql)
    {
        using var db = new DataStore(path);
        Assert.Throws<DomainException>(() => db.Query(sql));
    }
    [Fact] public void ExternalFileQueriesAreBlocked()
    {
        using var db = new DataStore(path);
        Assert.ThrowsAny<Exception>(() => db.Query("SELECT * FROM read_text('/etc/hosts')"));
    }
    [Fact] public void ExpensiveEvidenceQueryIsInterruptedAndStorageRemainsUsable()
    {
        using var db = new DataStore(path);
        var timer = System.Diagnostics.Stopwatch.StartNew();
        Assert.ThrowsAny<Exception>(() => db.Query("SELECT sum(i) FROM range(1000000000000) t(i)", timeout: TimeSpan.FromMilliseconds(100)));
        // Uninterrupted, this query runs for many minutes. The bound is generous because the 100 ms deadline fires from a timer
        // that can run seconds late on a busy two-core CI runner (over 5 s there once).
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(30), $"The query ran for {timer.Elapsed.TotalSeconds:0.0} s.");
        db.Save(DemoData.Create()); Assert.NotNull(db.Load());
    }
    [Fact] public void RepeatedNativeCurveCollectionRemainsDiagnosticWithoutInventingActualEnergy()
    {
        using var db = new DataStore(path);
        const string raw = """
        {"predbat.load_energy_actual":{"state":"3","attributes":{"results":{"2020-01-01T10:00:00Z":1,"2020-01-01T10:30:00Z":2}}}}
        """;
        db.SaveSource(raw, "{}"); db.SaveSource(raw, "{}");
        Assert.Empty(db.Query("SELECT * FROM actual_energy"));
        // Observations are written when a value changes (or hourly), so an identical repeat adds nothing.
        Assert.Single(db.Query("SELECT * FROM observations"));
        db.SaveSource(raw.Replace("\"state\":\"3\"", "\"state\":\"4\""), "{}");
        Assert.Equal(2,db.Query("SELECT * FROM observations").Count);
    }
    [Fact] public void FrozenForecastEnrichesFromLaterActualAndPreservesMissingPv()
    {
        using var db = new DataStore(path);
        var at = DateTimeOffset.Parse("2020-01-01T10:00:00Z");
        var p = new PlanSnapshot { At = at.AddHours(-1), Source = "Predbat", Slots = [new(at, 2, null, 1, null, 50, null, 20, 10, "Demand", .2)] };
        db.SavePlan(p);
        db.SaveSource("""{"predbat.load_energy_actual":{"state":"3","attributes":{"results":{"2020-01-01T10:00:00Z":1,"2020-01-01T10:30:00Z":2}}}}""", "{}");
        db.SaveTelemetry([new("load","sensor.house",at,1,"kWh","HomeAssistant","1","kWh"),new("load","sensor.house",at.AddMinutes(30),2,"kWh","HomeAssistant","2","kWh")],TimeSpan.FromMinutes(30));
        Assert.Equal(1, db.GetPlan(p.Id)!.Slots[0].LoadActual);
        Assert.Null(db.GetPlan(p.Id)!.Slots[0].PvActual);
        Assert.Null(p.Slots[0].LoadActual);
    }
    public void Dispose() { if (Directory.Exists(path)) Directory.Delete(path, true); }
}
