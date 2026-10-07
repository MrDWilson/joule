using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Joule;
using Xunit;

namespace Joule.Tests;

/// <summary>
/// The one-off repairs, run on a trimmed copy of the live 5 Oct records (67 checks, 2 suggestions, their reply threads and the two
/// memory facts). The live copy has no item closed by the old "AI review unavailable" path, so items shaped exactly as that path
/// wrote them are added to prove the reopening.
/// </summary>
public sealed class AiDataRepairTests : IDisposable
{
    readonly string directory = Path.Combine(Path.GetTempPath(), "predbat-repairs-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(directory, true); } catch { } }
    static string FixturePath([CallerFilePath] string source = "") => Path.Combine(Path.GetDirectoryName(source)!, "Fixtures", "live-ai-records-2026-10-05.json.gz");

    sealed record Fixture(List<Investigation> Investigations, List<Proposal> Proposals, List<MemoryFact> Memory);
    static Fixture Load()
    {
        using var file = File.OpenRead(FixturePath()); using var gzip = new GZipStream(file, CompressionMode.Decompress);
        return JsonSerializer.Deserialize<Fixture>(gzip, JsonDefaults.Options)!;
    }

    /// <summary>What the old ReplyAsync wrote when the evaluation failed: the note, a system message, a 30-day decline.</summary>
    static void OldUnavailablePath(List<ReplyMessage> thread, string note, DateTimeOffset at)
    {
        thread.Add(new ReplyMessage { At = at, Role = "user", Text = note });
        thread.Add(new ReplyMessage { At = at, Role = "system", Verdict = "unavailable", Provider = "ChatGpt", Text = "AI review unavailable: ChatGPT response failed (error). Dismissed with your note.", SuggestedMemory = note });
    }

    [Fact]
    public void LiveRecordsHaveNoWronglyDismissedItemsButTheirFailedChecksAreRelabelled()
    {
        var fixture = Load();
        var state = new AppState { DataSource = "Live", Investigations = fixture.Investigations, Proposals = fixture.Proposals };
        Assert.Equal(67, state.Investigations.Count);
        Assert.Equal(7, state.Investigations.Count(i => i.Status == "Failed" && i.Verdict == "problem"));
        var before = JsonSerializer.Serialize(state.Proposals, JsonDefaults.Options);
        var result = AiDataRepairs.Apply(state, null);
        // Reported to the user: none of the live items were dismissed by the failure path.
        Assert.Equal(0, result.Reopened); Assert.Equal(0, result.MemoryRemoved); Assert.Equal(7, result.FailedRelabelled);
        Assert.All(state.Investigations.Where(i => i.Status == "Failed"), i => { Assert.Null(i.Verdict); Assert.NotNull(i.FailureKind); Assert.Equal("Check didn't finish", i.Title); });
        Assert.Equal(before, JsonSerializer.Serialize(state.Proposals, JsonDefaults.Options));
        Assert.Equal("Denied", state.Proposals.Single(p => p.Id.StartsWith("2a1898ee")).Status); // the user's own denial is untouched
        Assert.Equal([AiDataRepairs.ReopenUnavailableReplies, AiDataRepairs.FailedVerdicts, AiDataRepairs.OwnTraffic], state.AiRepairs);
        Assert.Equal(0, AiDataRepairs.Apply(state, null).Total); // runs once
    }

    [Fact]
    public void ItemsDismissedOnlyBecauseTheAiWasUnavailableAreReopenedWithTheirSuppressionAndMemoryRemoved()
    {
        var fixture = Load();
        using var db = new DataStore(directory);
        foreach (var fact in fixture.Memory) db.AddMemory(fact.Text, fact.Source, fact.InvestigationId);
        var at = DateTimeOffset.Parse("2026-10-05T07:25:00Z");
        var state = new AppState { DataSource = "Live", Investigations = fixture.Investigations, Proposals = fixture.Proposals };

        // A suggestion denied by the old path; the user then clicked "Remember this note".
        var proposal = new Proposal { Id = "old-proposal", Title = "Combine charge slots", Status = "Pending", Changes = [new("combine_charge_slots", "off", "on")] };
        OldUnavailablePath(proposal.Thread, "Why would this help?", at); state.Proposals.Add(proposal); ChangeEngine.Deny(state, proposal.Id, "Why would this help?");
        db.AddMemory("Why would this help?", "user", null);
        // A follow-up, a file change and a finding closed the same way.
        var host = state.Investigations.First(i => i.Status == "Completed");
        var step = new InvestigationNextStep { Id = "old-step", Title = "Restore the export sensor", Rationale = "r", SuggestedAction = "a", Verification = "v", Uncertainty = "u" };
        OldUnavailablePath(step.Thread, "I can't fix the source.", at); host.NextSteps.Add(step);
        var change = new ConfigFileChange { Id = "old-file", File = "apps.yaml", Summary = "Add export_today", Location = "pred_bat", Snippet = "  export_today: sensor.x", Reason = "r" };
        OldUnavailablePath(change.Thread, "Where does this go?", at); host.FileChanges.Add(change);
        RecommendationDecisions.DismissFollowUp(state, host.Id, step.Id, "I can't fix the source.");
        step.ClosedReason = "Dismissed by user"; // the old path's wording
        RecommendationDecisions.DismissFileChange(state, host.Id, change.Id, "Where does this go?");
        var finding = state.Investigations.First(i => i.Status == "Completed" && i.DismissedAt is null && i.Id != host.Id);
        var findingStep = new InvestigationNextStep { Id = "finding-step", Title = "Ask the installer about the CT clamp", Rationale = "r", SuggestedAction = "a", Verification = "v", Uncertainty = "u" };
        finding.NextSteps.Add(findingStep);
        OldUnavailablePath(finding.Thread, "This is wrong.", at); RecommendationDecisions.DismissFinding(state, finding.Id, "This is wrong.");
        Assert.Equal("closed", findingStep.Status);

        var result = AiDataRepairs.Apply(state, db);

        Assert.Equal(4, result.Reopened); Assert.Equal(1, result.MemoryRemoved);
        var p = state.Proposals.Single(x => x.Id == "old-proposal");
        Assert.Equal("Pending", p.Status); Assert.Null(p.DecidedAt); Assert.Null(p.DecisionNote); Assert.Equal(AiDataRepairs.ReopenedNotice, p.Thread[^1].Text);
        Assert.Equal("open", step.Status); Assert.Null(step.DecidedAt); Assert.Null(step.ClosedReason);
        Assert.Equal("pending", change.Status); Assert.Null(change.DecidedAt);
        Assert.Null(finding.DismissedAt); Assert.Equal("open", findingStep.Status);
        // The suggested "fact" (a question) is gone; the two real facts stay.
        Assert.Equal(fixture.Memory.Select(m => m.Text).Order(), db.ListMemory().Select(m => m.Text).Order());
        // The live threads that ended normally are untouched.
        Assert.Equal("Denied", state.Proposals.Single(x => x.Id.StartsWith("2a1898ee")).Status);
        Assert.Contains(state.Activities, a => a.Message.StartsWith("Reopened 4 items"));
        // Nothing the user declined themselves is suppressed any more by these, and the next brief won't list them as declined.
        Assert.DoesNotContain(state.Investigations.SelectMany(i => i.NextSteps), n => n.Id == "old-step" && n.DecidedAt != null);
    }

}
