using DuckDB.NET.Data;
using Microsoft.Extensions.Configuration;
using Joule;
using System.Text.Json;
using Xunit;

namespace Joule.Tests;
public sealed class HistoryStorageTests : IDisposable
{
    readonly string directory = Path.Combine(Path.GetTempPath(), "predbat-history-" + Guid.NewGuid().ToString("N"));
    [Fact]
    public void ModelQueriesCannotReadInternalArchivedInvestigationPayloads()
    {
        using var db = new DataStore(directory);
        var state = DemoData.Create();
        state.Investigations.Add(new Investigation { ToolEvidence = [new("private", "configuration", "old settings", DateTimeOffset.UtcNow, true, "private archived payload", [])] });
        db.Save(state);
        Assert.Throws<DomainException>(() => db.Query("SELECT payload FROM investigation_evidence"));
        Assert.Single(db.ReadInvestigationEvidence(state.Investigations.Last().Id));
    }
    [Fact]
    public void FrozenEvidenceIsStoredSeparatelyFromFrequentlySavedStateAndSurvivesRestart()
    {
        var state = DemoData.Create();
        for (var i = 0; i < 100; i++)
            state.Investigations.Add(new Investigation { ToolEvidence = [new($"evidence-{i}", "query", "SELECT fixture", DateTimeOffset.UtcNow, true, new string('x', 14000), [])] });
        using (var db = new DataStore(directory))
        {
            db.Save(state);
            using var connection = new DuckDBConnection($"Data Source={Path.Combine(directory, DataFiles.Database)}"); connection.Open();
            using var cmd = connection.CreateCommand(); cmd.CommandText = "SELECT length(payload) FROM application_state WHERE id=1";
            Assert.True(Convert.ToInt64(cmd.ExecuteScalar()) < 200_000, "Frequently saved state must omit archived result bodies.");
        }
        using var reopened = new DataStore(directory);
        Assert.Equal(100, reopened.Load()!.Investigations.Count(i => i.ToolEvidence.Count > 0));
        Assert.Equal(14000, reopened.Load()!.Investigations.Last().ToolEvidence[0].ResultJson.Length);
    }
    [Fact]
    public async Task LegacyEmbeddedEvidenceMigratesAtStartupAndLazyReadsRemainFrozen()
    {
        Directory.CreateDirectory(directory);
        var seed = DemoData.Create();
        var investigation = new Investigation { ToolEvidence = [new("legacy", "query", "SELECT fixture", DateTimeOffset.UtcNow, true, "historical result", [])] };
        seed.Investigations.Add(investigation);
        using (var old = new DuckDBConnection($"Data Source={Path.Combine(directory, DataFiles.LegacyDatabase)}"))
        {
            old.Open(); using var command = old.CreateCommand();
            command.CommandText = "CREATE TABLE application_state (id INTEGER PRIMARY KEY, payload VARCHAR NOT NULL)"; command.ExecuteNonQuery();
            command.CommandText = "INSERT INTO application_state VALUES (1, ?)";
            command.Parameters.Add(new DuckDBParameter { Value = JsonSerializer.Serialize(seed, JsonDefaults.Options) }); command.ExecuteNonQuery();
        }
        using var http = new HttpClient();
        using (var db = new DataStore(directory))
        {
            var service = new StateService(db, new PredbatClient(http, new ConfigurationBuilder().Build()), true);
            Assert.Empty(service.Read(false).Investigations.Single(i => i.Id == investigation.Id).ToolEvidence);
            var detail = service.ReadInvestigation(investigation.Id)!;
            Assert.Equal("historical result", Assert.Single(detail.ToolEvidence).ResultJson);
            detail.ToolEvidence[0] = detail.ToolEvidence[0] with { ResultJson = "mutated caller copy" };
            await service.MutateAsync(s => s.AnalysisError = "fixture state update");
            Assert.Equal("historical result", Assert.Single(service.ReadInvestigation(investigation.Id)!.ToolEvidence).ResultJson);
        }
        using var reopened = new DataStore(directory);
        Assert.Empty(reopened.Load(false)!.Investigations.Single(i => i.Id == investigation.Id).ToolEvidence);
        Assert.Equal("historical result", Assert.Single(reopened.ReadInvestigationEvidence(investigation.Id)).ResultJson);
    }
    [Fact]
    public void FailedMetadataPublicationLeavesPreviousStateAndEvidenceUsable()
    {
        string originalId;
        string orphanId;
        using (var db = new DataStore(directory))
        {
            var state = DemoData.Create();
            var original = new Investigation { ToolEvidence = [new("original", "query", "SELECT fixture", DateTimeOffset.UtcNow, true, "original result", [])] }; originalId = original.Id;
            state.Investigations.Add(original); db.Save(state);
            var next = JsonDefaults.Clone(state);
            var orphan = new Investigation { ToolEvidence = [new("unpublished", "query", "SELECT fixture", DateTimeOffset.UtcNow, true, "unpublished result", [])] }; orphanId = orphan.Id;
            next.Investigations.Add(orphan); next.Ai.InputUsdPerMillion = double.NaN;
            Assert.Throws<ArgumentException>(() => db.Save(next));
            Assert.DoesNotContain(db.Load(false)!.Investigations, i => i.Id == orphanId);
            Assert.Equal("original result", Assert.Single(db.ReadInvestigationEvidence(originalId)).ResultJson);
            db.Save(state);
        }
        using var reopened = new DataStore(directory);
        Assert.DoesNotContain(reopened.Load()!.Investigations, i => i.Id == orphanId);
        Assert.Equal("original result", Assert.Single(reopened.ReadInvestigationEvidence(originalId)).ResultJson);
    }
    [Fact]
    public async Task LegacyRejectedRepliesAreMaskedForSummaryAndDetailReadsWithoutRewritingTheArchive()
    {
        using var db = new DataStore(directory); using var http = new HttpClient();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Ai:ApiKey"] = "legacy-private-config" }).Build();
        var seed = DemoData.Create();
        var legacy = new Investigation { Status = "Failed", Title = "Old rejected reply", Summary = "legacy-private-config", Steps = ["api_key=legacy-step-secret"], ToolEvidence = [new("old-model", "model", "Rejected finish", DateTimeOffset.UtcNow, false, JsonSerializer.Serialize(new { excerpt = "legacy-private-config api_key=legacy-api-secret" }), [], "legacy-private-config")] };
        seed.Investigations.Add(legacy); seed.AnalysisError = "legacy-private-config"; db.Save(seed);
        var raw = JsonSerializer.Serialize(db.ReadInvestigationEvidence(legacy.Id));
        var service = new StateService(db, new PredbatClient(http, config), true, configuration: config);
        var summary = JsonSerializer.Serialize(service.Read(false)); var detail = JsonSerializer.Serialize(service.ReadInvestigation(legacy.Id)); var hydrated = JsonSerializer.Serialize(service.Read());
        foreach (var exposed in new[] { summary, detail, hydrated }) { Assert.DoesNotContain("legacy-private-config", exposed); Assert.DoesNotContain("legacy-step-secret", exposed); Assert.DoesNotContain("legacy-api-secret", exposed); }
        Assert.Contains("[redacted]", detail); Assert.Empty(service.Read(false).Investigations.Last().ToolEvidence);
        await service.MutateAsync(s => s.LastAnalysis = DateTimeOffset.UtcNow);
        Assert.Equal(raw, JsonSerializer.Serialize(db.ReadInvestigationEvidence(legacy.Id)));
    }
    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
}
