using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Joule;
using Xunit;

namespace Joule.Tests;

/// <summary>
/// Predbat logs Joule's own MCP and API requests ("MCP: Token … failed: Not enough segments", "Authenticated via legacy bearer token").
/// Those lines never start a check, never become a finding, to-do, file edit or suggestion, and the ones already raised are closed
/// once. own-traffic-findings.json holds findings shaped like the live records (with real problems beside them that must stay open);
/// the trimmed live copy from 5 Oct proves the rule closes nothing else.
/// </summary>
public sealed class OwnTrafficTests
{
    static string Fixture(string name, [CallerFilePath] string source = "") => Path.Combine(Path.GetDirectoryName(source)!, "Fixtures", name);
    sealed record Records(List<Investigation> Investigations, List<Proposal> Proposals);
    static Records Load() => JsonSerializer.Deserialize<Records>(File.ReadAllText(Fixture("own-traffic-findings.json")), JsonDefaults.Options)!;
    static Records LoadLive()
    {
        using var file = File.OpenRead(Fixture("live-ai-records-2026-10-05.json.gz")); using var gzip = new GZipStream(file, CompressionMode.Decompress);
        return JsonSerializer.Deserialize<Records>(gzip, JsonDefaults.Options)!;
    }

    [Theory]
    [InlineData("2026-10-06 00:10:01 WARN: MCP: Token eyJhbGciOi failed: Not enough segments", true)]
    [InlineData("Warn: Authenticated via legacy bearer token", true)]
    [InlineData("Predbat rejects MCP tokens overnight", true)]
    [InlineData("Joule's monitoring client login failed", true)]
    [InlineData("Warn: Inverter 0 SoC read failed, retrying", false)]
    [InlineData("Error: Home Assistant token expired (401)", false)]
    [InlineData("The MCP server is disabled in apps.yaml", false)]
    [InlineData("Joule's sensor readings for export are missing", false)]
    public void RecognisesJoulesOwnTraffic(string text, bool own) => Assert.Equal(own, JouleOwnTraffic.IsAbout(text));

    [Fact]
    public void QuietChecksIgnoreLogWarningsCausedByJoule()
    {
        var lines = InvestigationScheduler.WarningLines("""
            2026-10-06 00:10:01 WARN: MCP: Token eyJhbGciOi failed: Not enough segments
            2026-10-06 00:10:01 Warn: Authenticated via legacy bearer token
            2026-10-06 00:12:00 Warn: Inverter 0 failed to set charge rate
            """);
        Assert.Equal(["2026-10-06 00:12:00 Warn: Inverter 0 failed to set charge rate"], lines);
    }

    [Fact]
    public void ANewResultAboutJoulesOwnSignInIsNotRaised()
    {
        var i = new Investigation
        {
            Title = "Predbat rejects MCP tokens: Not enough segments", Verdict = "problem", Evidence = ["e"],
            NextSteps = [new InvestigationNextStep { Id = "a", Title = "Re-issue Joule's MCP token", SuggestedAction = "a" }],
            FileChanges = [new ConfigFileChange { Id = "b", Summary = "Set mcp_secret so the MCP token authenticates", Reason = "r" }]
        };
        var proposals = new List<Proposal>();
        var dropped = JouleOwnTraffic.Filter(i, proposals);
        Assert.Equal(3, dropped.Count);
        Assert.Equal("no_change", i.Verdict); Assert.Empty(i.NextSteps); Assert.Empty(i.FileChanges);

        // A real finding keeps its real to-do; only the part about Joule's sign-in goes.
        var real = new Investigation
        {
            Title = "Battery charged at 1.7 kW against a 5 kW plan", Verdict = "problem", Evidence = ["e"],
            NextSteps = [new InvestigationNextStep { Id = "c", Title = "Ask the installer about the charge rate" }, new InvestigationNextStep { Id = "d", Title = "Re-issue Joule's MCP token" }]
        };
        Assert.Single(JouleOwnTraffic.Filter(real, proposals));
        Assert.Equal("problem", real.Verdict); Assert.Equal("c", Assert.Single(real.NextSteps).Id);
    }

    [Fact]
    public void TheOneOffRepairClosesOpenItemsAboutJoulesOwnSignInAndNothingElse()
    {
        var records = Load();
        var s = new AppState { DataSource = "Live", Investigations = records.Investigations, Proposals = records.Proposals };
        var result = AiDataRepairs.Apply(s, null);

        Investigation I(string id) => s.Investigations.Single(i => i.Id == id);
        // Two findings, the first one's to-do and file edit, the second one's suggestion, one to-do and one suggestion on a real finding.
        Assert.Equal(7, result.OwnTrafficClosed);
        foreach (var id in new[] { "own-1", "own-2" })
        {
            Assert.NotNull(I(id).DismissedAt); Assert.Equal("own_traffic", I(id).ClosedReason);
            Assert.Equal(JouleOwnTraffic.ClosedNotice, I(id).Thread[^1].Text);
        }
        Assert.Equal(("closed", JouleOwnTraffic.ClosedReason), (I("own-1").NextSteps[0].Status, I("own-1").NextSteps[0].ClosedReason));
        Assert.Equal("retired", I("own-1").FileChanges[0].Status);
        // The real charge-rate finding stays open with its real to-do; only the token to-do closed.
        Assert.Null(I("real-1").DismissedAt);
        Assert.Equal("open", I("real-1").NextSteps[0].Status);
        Assert.Equal("closed", I("real-1").NextSteps[1].Status);
        // Predbat's own Home Assistant token is a real problem; the user's own decision and quiet checks are untouched.
        Assert.Null(I("real-2").DismissedAt); Assert.Equal("open", I("real-2").NextSteps[0].Status);
        Assert.Equal(("That's Joule itself.", null), (I("decided-1").DecisionNote, I("decided-1").ClosedReason));
        Assert.Null(I("quiet-1").DismissedAt);
        Proposal P(string id) => s.Proposals.Single(p => p.Id == id);
        Assert.Equal(("Denied", JouleOwnTraffic.ClosedReason), (P("p-own-finding").Status, P("p-own-finding").ClosedReason));
        Assert.Equal(("Denied", JouleOwnTraffic.ClosedReason), (P("p-own-title").Status, P("p-own-title").ClosedReason));
        Assert.Equal("Pending", P("p-real").Status);
        Assert.Contains(s.Activities, a => a.Message.StartsWith("Closed 7 items about Joule's own sign-in to Predbat."));
        Assert.Contains(AiDataRepairs.OwnTraffic, s.AiRepairs);
        Assert.Equal(0, AiDataRepairs.Apply(s, null).Total); // runs once

        // The next check's brief says why, so the AI doesn't raise it again.
        Assert.Contains("Joule's own connection", RecommendationDecisions.FindingClosedText(I("own-1")));
    }

    [Fact]
    public void TheRepairFindsNothingToCloseInTheLiveRecords()
    {
        var live = LoadLive();
        var s = new AppState { DataSource = "Live", Investigations = live.Investigations, Proposals = live.Proposals };
        Assert.Equal(0, AiDataRepairs.Apply(s, null).OwnTrafficClosed);
        Assert.All(s.Investigations, i => Assert.NotEqual("own_traffic", i.ClosedReason));
    }
}
