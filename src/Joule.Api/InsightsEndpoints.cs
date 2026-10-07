namespace Joule;

/// <summary>
/// Small decisions the Insights page needs beyond approve, deny, reply and dismiss: "I changed it in Predbat myself" for an install
/// with live writes off, reopening something closed by mistake, and taking back "Mark as applied" on a file edit.
/// </summary>
public static class InsightsDecisions
{
    /// <summary>Writes are off (or the user prefers it): they made the change in Predbat by hand. The suggestion closes as done; the next
    /// collection sees the new value like any change made in Predbat.</summary>
    public static void MarkProposalDone(AppState s, string id)
    {
        var p = s.Proposals.FirstOrDefault(x => x.Id == id) ?? throw new DomainException("Suggestion not found.", 404);
        if (p.Status != "Pending") throw new DomainException("This suggestion has already been decided.");
        p.Status = "Done"; p.DecidedAt = DateTimeOffset.UtcNow;
        ChangeEngine.Log(s, "decision", $"You made “{p.Title}” in Predbat yourself.");
        if (s.Investigations.FirstOrDefault(i => i.Id == p.InvestigationId) is { } source) RecommendationDecisions.CloseFindingIfDone(s, source, p.DecidedAt.Value);
    }

    /// <summary>A suggestion you declined (or marked done) goes back on your list. It is checked against the current settings again.</summary>
    public static void ReopenProposal(AppState s, string id)
    {
        var p = s.Proposals.FirstOrDefault(x => x.Id == id) ?? throw new DomainException("Suggestion not found.", 404);
        if (p.Status is not ("Denied" or "Done")) throw new DomainException(p.Status == "Pending" ? "This suggestion is already open." : "Only a declined suggestion can be reopened.");
        p.Status = "Pending"; p.DecidedAt = null; p.DecisionNote = null; p.ClosedReason = null;
        RecommendationDecisions.ReopenResolvedFinding(s, p.InvestigationId);
        ChangeEngine.Log(s, "decision", $"You reopened the suggestion “{p.Title}”.");
    }

    /// <summary>The to-do comes back, with any copies from other checks that closed with it (they show as one item).</summary>
    public static void ReopenFollowUp(AppState s, string investigationId, string stepId)
    {
        var step = Investigation(s, investigationId).NextSteps.FirstOrDefault(x => x.Id == stepId) ?? throw new DomainException("To-do not found.", 404);
        if (step.Status == "open") throw new DomainException("This to-do is already open.");
        var (closedAt, reason, key) = (step.ClosedAt, step.ClosedReason, RecommendationDecisions.TitleKey(step.Title));
        foreach (var i in s.Investigations)
            foreach (var twin in i.NextSteps.Where(x => ReferenceEquals(x, step) || (x.Status != "open" && closedAt != null && x.ClosedAt == closedAt && x.ClosedReason == reason && RecommendationDecisions.TitleKey(x.Title) == key)))
            {
                twin.Status = "open"; twin.ClosedAt = null; twin.ClosedReason = null; twin.DecidedAt = null; twin.DecisionNote = null;
                RecommendationDecisions.ReopenResolvedFinding(s, i.Id);
            }
        ChangeEngine.Log(s, "decision", $"You reopened the to-do “{step.Title}”.");
    }

    public static void ReopenFileChange(AppState s, string investigationId, string changeId)
    {
        var change = InvestigationFileChanges.Find(s, investigationId, changeId);
        if (InvestigationFileChanges.IsOpen(change)) throw new DomainException("This file edit is already open.");
        var (closedAt, reason) = (change.ClosedAt, change.ClosedReason);
        foreach (var i in s.Investigations)
            foreach (var twin in i.FileChanges.Where(x => ReferenceEquals(x, change) || (!InvestigationFileChanges.IsOpen(x) && closedAt != null && x.ClosedAt == closedAt && x.ClosedReason == reason && InvestigationFileChanges.SameChange(x, change))))
            {
                // An edit Joule made that is still in the file reopens as applied (so Restore previous version stays the way to undo it).
                var inFile = twin.Edit is { Check: "checking" or "confirmed" or "unconfirmed" };
                twin.Status = inFile ? "applied" : "pending"; twin.AppliedAt = inFile ? twin.AppliedAt ?? twin.Edit!.At : null;
                twin.ClosedAt = null; twin.ClosedReason = null; twin.DecidedAt = null; twin.DecisionNote = null;
                RecommendationDecisions.ReopenResolvedFinding(s, i.Id);
            }
        ChangeEngine.Log(s, "decision", $"You reopened the {change.File} edit “{change.Summary}”.");
    }

    /// <summary>"Undo mark as applied": the edit goes back to waiting for you, and the next check stops looking for it.</summary>
    public static void UnmarkApplied(AppState s, string investigationId, string changeId)
    {
        var change = InvestigationFileChanges.Find(s, investigationId, changeId);
        if (change.Status != "applied") throw new DomainException("This file edit isn't marked as applied.");
        if (change.Edit is { Check: not ("restored" or "rolled_back") })
            throw new DomainException("Joule made this edit in the file. Use Restore previous version to undo it.");
        change.Status = "pending"; change.AppliedAt = null;
        ChangeEngine.Log(s, "decision", $"You took back “applied” on the {change.File} edit “{change.Summary}”.");
    }

    /// <summary>Findings you closed come back with the to-dos, file edits and suggestions that closed with them (an undo); items you
    /// closed one by one before that stay closed.</summary>
    public static void ReopenFinding(AppState s, string investigationId)
    {
        var investigation = Investigation(s, investigationId);
        if (investigation.DismissedAt is not { } closed) throw new DomainException("These findings aren't dismissed.");
        const string with = RecommendationDecisions.WithFindings;
        foreach (var step in investigation.NextSteps.Where(x => x.Status == "closed" && x.ClosedReason == with && x.ClosedAt == closed))
        { step.Status = "open"; step.ClosedAt = null; step.ClosedReason = null; }
        foreach (var change in investigation.FileChanges.Where(x => x.Status == "retired" && x.ClosedReason == with && x.ClosedAt == closed))
        { change.Status = change.AppliedAt is null ? "pending" : "applied"; change.ClosedAt = null; change.ClosedReason = null; }
        foreach (var p in s.Proposals.Where(p => p.InvestigationId == investigation.Id && p.Status == "Denied" && p.ClosedReason == with && p.DecidedAt == closed))
        { p.Status = "Pending"; p.DecidedAt = null; p.ClosedReason = null; p.DecisionNote = null; }
        investigation.DismissedAt = null; investigation.DecisionNote = null; investigation.ClosedReason = null;
        ChangeEngine.Log(s, "decision", $"You reopened the findings “{investigation.Title}”.");
    }

    static Investigation Investigation(AppState s, string id) => s.Investigations.FirstOrDefault(i => i.Id == id) ?? throw new DomainException("Investigation not found.", 404);
}

public record AiTestRequest(string? Provider, string? Model);

/// <summary>The outcome of one tiny call to the chosen AI: it answered (with how long it took) or the plain reason it didn't.</summary>
public record AiTestResult(bool Ok, string Message, double Seconds, long InputTokens, long OutputTokens);

public static class InsightsEndpoints
{
    /// <summary>One small call, no retries, so "Test connection" answers within a minute with the provider's own reason.</summary>
    public static async Task<AiTestResult> TestAiAsync(AiTestRequest request, AiModelClient model, bool demo, CancellationToken ct)
    {
        var provider = request.Provider ?? "";
        if (provider is not ("Demo" or "Api" or "ChatGpt") || (!demo && provider == "Demo")) throw new DomainException("Choose ChatGPT or an AI API first.", 400);
        if (provider == "Demo") return new(true, "The demo answers from built-in examples, so there is nothing to connect.", 0, 0, 0);
        var name = (request.Model ?? "").Trim();
        if (name.Length is 0 or > 150) throw new DomainException("Choose a model first.", 400);
        var started = DateTimeOffset.UtcNow;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        try
        {
            var reply = await model.CompleteAsync(provider, name, "Connection test from Joule. Reply with this JSON object only: {\"ok\":true}", timeout.Token,
                options: new ModelCallOptions { RetryDelays = [], FallbackModel = "" });
            var seconds = Math.Round((DateTimeOffset.UtcNow - started).TotalSeconds, 1);
            return new(true, $"{(provider == "ChatGpt" ? "ChatGPT" : "The AI API")} answered with {name} in {seconds:0.#} s.", seconds, reply.InputTokens, reply.OutputTokens);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new(false, "No answer within a minute. The service may be busy; try again shortly.", 60, 0, 0);
        }
        catch (Exception e) when (e is ModelProviderException or DomainException or HttpRequestException)
        {
            return new(false, e.Message, Math.Round((DateTimeOffset.UtcNow - started).TotalSeconds, 1), 0, 0);
        }
    }

    public static void MapInsightsEndpoints(this WebApplication app)
    {
        static async Task<IResult> Decide(StateService state, Action<AppState> decide, CancellationToken ct) { await state.MutateAsync(decide, ct); return Results.Ok(new { ok = true }); }

        app.MapPost("/api/ai/test", (AiTestRequest request, AiModelClient model, StateService state, CancellationToken ct) => TestAiAsync(request, model, state.Demo, ct));
        app.MapPost("/api/proposals/{id}/done", (string id, StateService state, CancellationToken ct) => Decide(state, s => InsightsDecisions.MarkProposalDone(s, id), ct));
        app.MapPost("/api/proposals/{id}/reopen", (string id, StateService state, CancellationToken ct) => Decide(state, s => InsightsDecisions.ReopenProposal(s, id), ct));
        app.MapPost("/api/investigations/{id}/reopen", (string id, StateService state, CancellationToken ct) => Decide(state, s => InsightsDecisions.ReopenFinding(s, id), ct));
        app.MapPost("/api/investigations/{id}/followups/{stepId}/reopen", (string id, string stepId, StateService state, CancellationToken ct) =>
            Decide(state, s => InsightsDecisions.ReopenFollowUp(s, id, stepId), ct));
        app.MapPost("/api/investigations/{id}/filechanges/{changeId}/reopen", (string id, string changeId, StateService state, CancellationToken ct) =>
            Decide(state, s => InsightsDecisions.ReopenFileChange(s, id, changeId), ct));
        app.MapPost("/api/investigations/{id}/filechanges/{changeId}/unapply", (string id, string changeId, StateService state, CancellationToken ct) =>
            Decide(state, s => InsightsDecisions.UnmarkApplied(s, id, changeId), ct));
    }
}
