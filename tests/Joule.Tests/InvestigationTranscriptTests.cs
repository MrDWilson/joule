using Joule;
using Xunit;

namespace Joule.Tests;

public class InvestigationTranscriptTests
{
    static ToolEvidence Tool(string id, string body, bool success = true, string? error = null) =>
        new(id, "mcp", "get_entity_history", DateTimeOffset.UtcNow, success, System.Text.Json.JsonSerializer.Serialize(new { text = body }), [], error);

    [Fact]
    public void KeepsEveryTurnInOrderWithResultsAndNotice()
    {
        var transcript = new InvestigationTranscript();
        transcript.Add("{\"action\":\"mcp\",\"tool\":\"get_log\"}", Tool("tool-a", "first log line"));
        transcript.Add("{\"action\":\"query\",\"sql\":\"SELECT 1\"}", Tool("tool-b", "second result", false, "Query rejected"));
        var prompt = transcript.Compose("BRIEF", "CATALOG", "NOTICE");
        Assert.StartsWith("BRIEF\nCATALOG", prompt);
        Assert.True(prompt.IndexOf("Turn 1 — your action") < prompt.IndexOf("tool-a") && prompt.IndexOf("tool-a") < prompt.IndexOf("Turn 2 — your action"));
        Assert.Contains("first log line", prompt); Assert.Contains("second result", prompt); Assert.Contains("gap: Query rejected", prompt);
        Assert.Contains("unavailable", prompt); Assert.EndsWith("NOTICE\nRespond with the next single JSON action object.", prompt);
    }

    [Fact]
    public void LongResultsAreExcerptedInlineButStayFullyArchived()
    {
        var transcript = new InvestigationTranscript();
        var body = string.Concat(Enumerable.Range(0, 2000).Select(i => $"line {i} needle-{i}\n"));
        transcript.Add("{\"action\":\"mcp\"}", Tool("tool-long", body));
        var prompt = transcript.Compose("B", "C", "");
        Assert.Contains("needle-5", prompt); Assert.DoesNotContain("needle-1999", prompt); Assert.Contains("promptTruncated", prompt);
    }

    [Fact]
    public void OldestResultBodiesAreStubbedWhenEvenHeadExcerptsWouldExceedTheHardLimit()
    {
        var transcript = new InvestigationTranscript();
        var body = new string('x', 2_000);
        const int turns = 400; // 400 head excerpts of 1,200 characters plus replies exceed the hard limit
        for (var i = 0; i < turns; i++) transcript.Add("{\"action\":\"mcp\",\"turn\":" + i + "}", Tool("tool-" + i, "marker-" + i + " " + body));
        var prompt = transcript.Compose("B", "C", "N");
        Assert.True(prompt.Length <= InvestigationTranscript.PromptLimit, $"prompt {prompt.Length}");
        Assert.Contains("compacted out of the prompt", prompt);
        Assert.Contains("tool-0", prompt); // the ID survives compaction so the model can page it
        Assert.DoesNotContain("marker-0 ", prompt);
        Assert.Contains("marker-" + (turns - 1) + " ", prompt); // the newest result is intact
        for (var i = 0; i < turns; i++) Assert.Contains("\"turn\":" + i + "}", prompt); // every action stays visible
    }

    [Fact]
    public void UnparseableReplyIsRecordedWithTheServerExplanation()
    {
        var transcript = new InvestigationTranscript();
        transcript.Add("I will look at the plan now.", null, "[reply was not a single JSON action object]");
        Assert.Contains("[reply was not a single JSON action object]", transcript.Compose("B", "C", ""));
    }

    [Fact]
    public void PastTheSoftLimitOlderResultsKeepOnlyTheirHeadWhileTheNewestStayFullAndThePrefixStaysStable()
    {
        var transcript = new InvestigationTranscript();
        var body = new string('y', InvestigationTranscript.ResultExcerptLimit - 100);
        var turns = InvestigationTranscript.SoftLimit / InvestigationTranscript.ResultExcerptLimit + 2;
        for (var i = 0; i < turns; i++) transcript.Add("{\"action\":\"mcp\",\"turn\":" + i + "}", Tool("tool-" + i, "head-" + i + " " + body + " tail-" + i));
        var prompt = transcript.Compose("B", "C", "N");
        Assert.True(prompt.Length < InvestigationTranscript.SoftLimit + 2 * InvestigationTranscript.ResultExcerptLimit, $"prompt {prompt.Length}");
        Assert.Contains("head-0 ", prompt); Assert.DoesNotContain("tail-0", prompt); Assert.Contains("compacted to its head", prompt);
        Assert.Contains("tail-" + (turns - 1), prompt); Assert.Contains("tail-" + (turns - InvestigationTranscript.KeepFullResults), prompt);
        // Adding a small turn afterwards must not rewrite earlier entries: the provider's prefix cache depends on it.
        var before = prompt;
        transcript.Add("{\"action\":\"evidence\"}", Tool("tool-small", "small"));
        var after = transcript.Compose("B", "C", "N");
        Assert.StartsWith(before[..(before.Length - "\nN\nRespond with the next single JSON action object.".Length)], after);
    }
}
