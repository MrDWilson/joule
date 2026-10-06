using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Joule;
using Xunit;

namespace Joule.Tests;

public sealed class DiagnosticSnapshotTests : IDisposable
{
    readonly string directory = Path.Combine(Path.GetTempPath(), "predbat-diagnostics-" + Guid.NewGuid().ToString("N"));
    static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value, JsonDefaults.Options);

    [Fact]
    public void HistoricalPlanAndSelectedAttributesRemainInspectableWithoutRawSqlAccess()
    {
        using var db = new DataStore(directory);
        var from = DateTimeOffset.UtcNow.AddMinutes(-1);
        var plan = new PlanSnapshot { Id = "incident-plan", Source = "Predbat", At = from.AddMinutes(-5) };
        db.SavePlan(plan, """{"predbat.status":{"state":"Hold for car","attributes":{"state_target":"backup","reason":"car charging","api_key":"do-not-expose"}},"sensor.predbat_soc":{"state":"14"}}""",
            """{"plan":{"timestamp":"2026-10-03T01:00:00+01:00","rows":[{"reason":"hold_for_car","state_target":"backup","target_soc":100}]}}""");
        var to = DateTimeOffset.UtcNow.AddMinutes(1);
        var index = Json(db.ReadDiagnosticSnapshots(from, to));
        Assert.Equal("incident-plan", index.GetProperty("items")[0].GetProperty("id").GetString());
        Assert.False(index.GetProperty("truncated").GetBoolean());
        var detail = Json(db.ReadDiagnosticSnapshots(from, to, "incident-plan", ["predbat.status"]));
        var text = detail.GetRawText();
        Assert.Contains("hold_for_car", text);
        Assert.Contains("state_target", text);
        Assert.Contains("Hold for car", text);
        Assert.DoesNotContain("sensor.predbat_soc", text);
        Assert.DoesNotContain("do-not-expose", text);
        Assert.True(detail.GetProperty("diagnosticOnly").GetBoolean());
        Assert.Throws<DomainException>(() => db.Query("SELECT * FROM source_snapshots"));
    }

    [Fact]
    public void SnapshotWindowAndSelectorsAreEnforcedAndCredentialsRedacted()
    {
        using var db = new DataStore(directory);
        var now = DateTimeOffset.UtcNow;
        db.SavePlan(new() { Id = "one", At = now, Source = "Predbat" }, """{"predbat.status":{"state":"fixture-private-value","attributes":{}}}""", "{}");
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Predbat:Token"] = "fixture-private-value" }).Build();
        var detail = Json(db.ReadDiagnosticSnapshots(now.AddMinutes(-1), now.AddMinutes(1), "one", ["predbat.status"], configuration: config));
        Assert.DoesNotContain("fixture-private-value", detail.GetRawText());
        Assert.Throws<DomainException>(() => db.ReadDiagnosticSnapshots(now.AddDays(-2), now.AddDays(-1), "one"));
        Assert.Throws<DomainException>(() => db.ReadDiagnosticSnapshots(now, now.AddDays(32)));
        Assert.Throws<DomainException>(() => db.ReadDiagnosticSnapshots(now.AddMinutes(-1), now, entityIds: Enumerable.Repeat("predbat.status", 11).ToArray()));
        Assert.Throws<DomainException>(() => db.ReadDiagnosticSnapshots(now.AddMinutes(-1), now, offset: -1));
    }

    [Fact]
    public void SnapshotIndexPagesWithoutDiscardingLaterEntries()
    {
        using var db = new DataStore(directory);
        var from = DateTimeOffset.UtcNow.AddMinutes(-1);
        // Distinct states: an identical consecutive snapshot is skipped.
        for (var i = 0; i < 3; i++) db.SaveSource("{\"predbat.status\":{\"state\":\"Demand " + i + "\"}}", "{}");
        var to = DateTimeOffset.UtcNow.AddMinutes(1);
        var first = Json(db.ReadDiagnosticSnapshots(from, to, limit: 2));
        Assert.True(first.GetProperty("truncated").GetBoolean());
        Assert.Equal(2, first.GetProperty("nextOffset").GetInt32());
        var next = Json(db.ReadDiagnosticSnapshots(from, to, offset: 2, limit: 2));
        Assert.Single(next.GetProperty("items").EnumerateArray());
        Assert.False(next.GetProperty("truncated").GetBoolean());
    }

    [Fact]
    public void SnapshotDeclaresSanitizerAndInventoryOmissions()
    {
        using var db = new DataStore(directory);
        var now = DateTimeOffset.UtcNow;
        var states = Enumerable.Range(0, 201).ToDictionary(i => "sensor.predbat_" + i, i => new { state = i });
        var deep = "{\"v\":" + new string('[', 30) + "1" + new string(']', 30) + "}";
        db.SavePlan(new() { Id = "deep", At = now, Source = "Predbat" }, JsonSerializer.Serialize(states), deep);
        var result = Json(db.ReadDiagnosticSnapshots(now.AddMinutes(-1), now.AddMinutes(1), "deep"));
        Assert.True(result.GetProperty("truncated").GetBoolean());
        Assert.True(result.GetProperty("contentTruncated").GetBoolean());
        Assert.True(result.GetProperty("inventoryTruncated").GetBoolean());
        Assert.Equal(200, result.GetProperty("availableEntityIds").GetArrayLength());
    }

    [Fact]
    public void CoverageDescribesAvailableSourcesWithoutInventingHistory()
    {
        using var db = new DataStore(directory);
        var empty = Json(db.ReadEvidenceCoverage());
        Assert.Equal(JsonValueKind.Null, empty.GetProperty("snapshots")[0].GetProperty("first_at").ValueKind);
        db.SaveSource("""{"predbat.status":{"state":"Hold for car","attributes":{}}}""", "{}");
        var populated = Json(db.ReadEvidenceCoverage());
        Assert.Contains("predbat.status", populated.GetProperty("entities").GetRawText());
        Assert.NotEqual(JsonValueKind.Null, populated.GetProperty("snapshots")[0].GetProperty("first_at").ValueKind);
    }

    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
}
