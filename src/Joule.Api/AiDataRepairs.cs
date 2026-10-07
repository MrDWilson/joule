namespace Joule;

/// <summary>
/// One-off repairs of AI records written by earlier versions. Each runs once (recorded in AppState.AiRepairs) at startup:
/// <list type="bullet">
/// <item>reply-unavailable-reopen-v1: a reply the AI couldn't read used to dismiss the item ("AI review unavailable: … Dismissed with
/// your note") and hide it for 30 days. Those items are reopened, the suppression is lifted, and any memory fact saved from that
/// dismissal (the note offered as a fact) is removed.</item>
/// <item>failed-verdict-null-v1: checks that didn't finish were stored with the verdict "problem". They get no verdict, a failure kind
/// and a plain headline, so they never show as a household problem.</item>
/// <item>own-traffic-close-v1: checks reported Predbat log lines caused by Joule's own MCP sign-in ("Not enough segments", "legacy
/// bearer token") as household problems. Those open findings, to-dos and file edits are closed with a plain note.</item>
/// </list>
/// </summary>
public static class AiDataRepairs
{
    public const string ReopenUnavailableReplies = "reply-unavailable-reopen-v1", FailedVerdicts = "failed-verdict-null-v1", OwnTraffic = "own-traffic-close-v1";
    public const string UnavailablePrefix = "AI review unavailable:";
    public const string ReopenedNotice = "Reopened: this was dismissed only because the AI couldn't read your reply at the time. Nothing else changed.";

    public sealed record RepairResult(int Reopened, int MemoryRemoved, int FailedRelabelled, int OwnTrafficClosed = 0)
    {
        public int Total => Reopened + MemoryRemoved + FailedRelabelled + OwnTrafficClosed;
        public string Summary => $"{Reopened} item(s) reopened, {MemoryRemoved} memory fact(s) removed, {FailedRelabelled} unfinished check(s) relabelled, {OwnTrafficClosed} item(s) about Joule's own connection closed";
    }

    public static RepairResult Apply(AppState s, DataStore? db)
    {
        int reopened = 0, removed = 0, relabelled = 0, ownTraffic = 0;
        if (!s.AiRepairs.Contains(ReopenUnavailableReplies))
        {
            (reopened, removed) = ReopenWronglyDismissed(s, db);
            s.AiRepairs.Add(ReopenUnavailableReplies);
            if (reopened > 0) ChangeEngine.Log(s, "decision", $"Reopened {reopened} item{(reopened == 1 ? "" : "s")} that {(reopened == 1 ? "was" : "were")} dismissed only because the AI couldn't read your reply.");
        }
        if (!s.AiRepairs.Contains(FailedVerdicts))
        {
            relabelled = RelabelFailedChecks(s);
            s.AiRepairs.Add(FailedVerdicts);
        }
        if (!s.AiRepairs.Contains(OwnTraffic))
        {
            ownTraffic = JouleOwnTraffic.CloseExisting(s, DateTimeOffset.UtcNow);
            s.AiRepairs.Add(OwnTraffic);
            if (ownTraffic > 0) ChangeEngine.Log(s, "decision", $"Closed {ownTraffic} item{(ownTraffic == 1 ? "" : "s")} about Joule's own sign-in to Predbat. {(ownTraffic == 1 ? "It wasn't" : "They weren't")} about your system.");
        }
        return new(reopened, removed, relabelled, ownTraffic);
    }

    /// <summary>The system message that closed a thread through the old "AI unavailable means dismiss" path, if that is how it ended.</summary>
    static ReplyMessage? ClosedByUnavailable(List<ReplyMessage> thread) =>
        thread.LastOrDefault() is { Role: "system" } last && last.Text.StartsWith(UnavailablePrefix, StringComparison.Ordinal) && last.Text.Contains("Dismissed with your note", StringComparison.Ordinal) ? last : null;

    public static (int Reopened, int MemoryRemoved) ReopenWronglyDismissed(AppState s, DataStore? db)
    {
        var suggested = new List<string>(); var count = 0;
        void Reopened(List<ReplyMessage> thread, ReplyMessage closing)
        {
            if (closing.SuggestedMemory is { Length: > 0 } fact) suggested.Add(fact);
            thread.Add(new ReplyMessage { At = DateTimeOffset.UtcNow, Role = "system", Verdict = "reopened", Text = ReopenedNotice });
            count++;
        }
        foreach (var p in s.Proposals)
            if (p.Status == "Denied" && ClosedByUnavailable(p.Thread) is { } closing)
            { p.Status = "Pending"; p.DecidedAt = null; p.DecisionNote = null; Reopened(p.Thread, closing); }
        foreach (var i in s.Investigations)
        {
            foreach (var step in i.NextSteps)
                if (step.Status == "closed" && step.ClosedReason == "Dismissed by user" && ClosedByUnavailable(step.Thread) is { } closing)
                { step.Status = "open"; step.ClosedAt = null; step.ClosedReason = null; step.DecidedAt = null; step.DecisionNote = null; Reopened(step.Thread, closing); }
            foreach (var change in i.FileChanges)
                if (change.Status == "dismissed" && ClosedByUnavailable(change.Thread) is { } closing)
                { change.Status = change.AppliedAt is null ? "pending" : "applied"; change.ClosedAt = null; change.ClosedReason = null; change.DecidedAt = null; change.DecisionNote = null; Reopened(change.Thread, closing); }
            if (i.DismissedAt is { } dismissed && ClosedByUnavailable(i.Thread) is { } findingClosing)
            {
                // Disputing a finding also closed its to-dos and file edits at the same moment; reopen exactly those.
                foreach (var step in i.NextSteps.Where(x => x.Status == "closed" && x.ClosedReason == "Findings dismissed by user" && x.ClosedAt == dismissed))
                { step.Status = "open"; step.ClosedAt = null; step.ClosedReason = null; }
                foreach (var change in i.FileChanges.Where(x => x.Status == "retired" && x.ClosedReason == "Findings dismissed by user" && x.ClosedAt == dismissed))
                { change.Status = change.AppliedAt is null ? "pending" : "applied"; change.ClosedAt = null; change.ClosedReason = null; }
                i.DismissedAt = null; i.DecisionNote = null; Reopened(i.Thread, findingClosing);
            }
        }
        var removed = 0;
        if (db != null && suggested.Count > 0)
            foreach (var fact in db.ListMemory().Where(f => f.Source is "user" or "user-reply" && suggested.Any(x => string.Equals(x.Trim(), f.Text.Trim(), StringComparison.OrdinalIgnoreCase))))
                if (db.DeleteMemory(fact.Id)) removed++;
        return (count, removed);
    }

    public static int RelabelFailedChecks(AppState s)
    {
        var count = 0;
        foreach (var i in s.Investigations.Where(i => i.Status is "Failed" or "Interrupted" && (i.Verdict is not null || i.FailureKind is null)))
        {
            i.Verdict = null;
            i.FailureKind ??= i.Summary.Contains("cancel", StringComparison.OrdinalIgnoreCase) || i.Summary.Contains("timed out", StringComparison.OrdinalIgnoreCase) ? "timeout"
                : i.Summary.Contains("ChatGPT", StringComparison.Ordinal) || i.Summary.Contains("provider", StringComparison.OrdinalIgnoreCase) || i.Summary.Contains("HTTP", StringComparison.Ordinal) ? "provider_busy"
                : "invalid_answer";
            if (i.Title == "Investigation could not complete") i.Title = "Check didn't finish";
            i.Headline ??= i.FailureKind switch { "provider_busy" => "The AI service didn't answer", "timeout" => "The check ran out of time", _ => "The AI's answer couldn't be used" };
            i.Plain ??= InvestigationQuality.Shorten(i.Summary, InvestigationQuality.PlainLimit);
            if (i.Category == "Provider or validation failure") i.Category = "Didn't finish";
            count++;
        }
        return count;
    }
}
