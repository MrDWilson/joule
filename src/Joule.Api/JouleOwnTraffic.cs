using System.Text.RegularExpressions;

namespace Joule;

/// <summary>
/// Joule reads Predbat through its MCP server and web API, and Predbat logs those requests: "MCP: Token … failed: Not enough
/// segments", "Authenticated via legacy bearer token". Those lines are about Joule's own connection, not the household's system,
/// so they never start a check, never become a finding, to-do or file edit, and old ones are closed once (see AiDataRepairs).
/// </summary>
public static class JouleOwnTraffic
{
    const string Auth = @"(tokens?|auth\w*|bearer|log ?ins?|logins?|sign[- ]?ins?|credentials?|secrets?|jwt|401|403|unauthori[sz]ed|forbidden|segments)";
    static readonly TimeSpan Limit = TimeSpan.FromMilliseconds(200);

    /// <summary>Predbat's own words for Joule's requests, or MCP/Joule named next to a sign-in word in the same sentence.</summary>
    static readonly Regex About = new(
        @"not enough segments|legacy bearer token|\bMCP\b[^.\n]{0,80}?\b" + Auth + @"\b|\b" + Auth + @"\b[^.\n]{0,80}?\bMCP\b|\b(Joule|monitoring client)('s)?\b[^.\n]{0,60}?\b" + Auth + @"\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, Limit);

    /// <summary>The rule the analyst follows (and the server enforces with <see cref="IsAbout"/>).</summary>
    public const string Rule = "Joule's own traffic: Joule reads Predbat through its MCP server and web API, and Predbat logs those requests (for example \"MCP: Token … failed: Not enough segments\" or \"Authenticated via legacy bearer token\"). Those lines are about Joule's own connection, not the household's system: never report them as a finding, to-do, file edit or proposal; the server drops any that are.";

    public static bool IsAbout(params string?[] texts) => texts.Any(t => !string.IsNullOrEmpty(t) && About.IsMatch(t));

    /// <summary>A Predbat log line caused by Joule's own MCP or API sign-in.</summary>
    public static bool IsOwnLogLine(string line) => About.IsMatch(line);

    public static bool IsAbout(InvestigationNextStep step) => IsAbout(step.Title, step.SuggestedAction, step.Rationale);
    public static bool IsAbout(ConfigFileChange change) => IsAbout(change.Summary, change.Reason);
    public static bool IsAbout(Proposal proposal) => IsAbout(proposal.Title, proposal.Summary);
    /// <summary>The finding as a whole is about Joule's own traffic when its title or headline is; one sentence in the summary isn't enough.</summary>
    public static bool IsAboutFinding(Investigation i) => IsAbout(i.Title, i.Headline);

    public const string ClosedReason = "About Joule's own connection to Predbat, not your system";
    public const string ClosedNotice = "Closed automatically: this was about Joule's own sign-in to Predbat, not your system. Joule won't raise it again.";

    /// <summary>
    /// Drops the parts of a new result that are about Joule's own traffic: to-dos, file edits and proposals that match, and the finding
    /// itself (recorded as nothing new) when its title says so and nothing else is left. Returns what was dropped, for the steps.
    /// </summary>
    public static List<string> Filter(Investigation i, List<Proposal> proposals)
    {
        var dropped = new List<string>();
        foreach (var step in i.NextSteps.Where(IsAbout).ToList()) { i.NextSteps.Remove(step); dropped.Add($"to-do “{Cut(step.Title)}”"); }
        foreach (var change in i.FileChanges.Where(IsAbout).ToList()) { i.FileChanges.Remove(change); dropped.Add($"file edit “{Cut(change.Summary)}”"); }
        foreach (var proposal in proposals.Where(IsAbout).ToList()) { proposals.Remove(proposal); dropped.Add($"suggestion “{Cut(proposal.Title)}”"); }
        if (i.Verdict != "no_change" && IsAboutFinding(i) && i.NextSteps.Count == 0 && i.FileChanges.Count == 0 && proposals.Count == 0)
        { i.Verdict = "no_change"; dropped.Add($"finding “{Cut(i.Title)}”"); }
        return dropped;
    }

    /// <summary>
    /// One-off repair: open findings, to-dos and file edits about Joule's own MCP or API sign-in are closed with a plain note. Nothing
    /// the user decided is touched. Returns how many were closed.
    /// </summary>
    public static int CloseExisting(AppState s, DateTimeOffset now)
    {
        var count = 0;
        foreach (var i in s.Investigations)
        {
            if (i.Status == "Completed" && i.DismissedAt is null && i.Verdict is not "no_change" && IsAboutFinding(i))
            {
                i.DismissedAt = now; i.ClosedReason = RecommendationDecisions.OwnTraffic;
                i.Thread.Add(new ReplyMessage { At = now, Role = "system", Text = ClosedNotice });
                count++;
                foreach (var step in i.NextSteps.Where(x => x.Status == "open")) { step.Status = "closed"; step.ClosedAt = now; step.ClosedReason = ClosedReason; count++; }
                foreach (var change in i.FileChanges.Where(InvestigationFileChanges.IsOpen)) { change.Status = "retired"; change.ClosedAt = now; change.ClosedReason = ClosedReason; count++; }
            }
            foreach (var step in i.NextSteps.Where(x => x.Status == "open" && IsAbout(x)))
            { step.Status = "closed"; step.ClosedAt = now; step.ClosedReason = ClosedReason; step.Thread.Add(new ReplyMessage { At = now, Role = "system", Text = ClosedNotice }); count++; }
            foreach (var change in i.FileChanges.Where(x => x.Status == "pending" && IsAbout(x)))
            { change.Status = "retired"; change.ClosedAt = now; change.ClosedReason = ClosedReason; change.Thread.Add(new ReplyMessage { At = now, Role = "system", Text = ClosedNotice }); count++; }
        }
        return count;
    }

    static string Cut(string text) => text.Length <= 100 ? text : text[..100] + "…";
}
