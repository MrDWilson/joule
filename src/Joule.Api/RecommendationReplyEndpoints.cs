namespace Joule;

/// <summary>Replies to recommendations, dismissals with a note, and the configuration file change workflow.</summary>
public static class RecommendationReplyEndpoints
{
    public static void MapRecommendationReplyEndpoints(this WebApplication app)
    {
        static string? Note(DecisionNoteRequest? request, IConfiguration configuration) => RecommendationDecisions.Note(request?.Note, PredbatMcpSafety.Secrets(configuration));
        static async Task<IResult> Decide(StateService state, Action<AppState> decide, CancellationToken ct) { await state.MutateAsync(decide, ct); return Results.Ok(new { ok = true }); }

        app.MapPost("/api/proposals/{id}/reply", (string id, DecisionNoteRequest request, RecommendationReplyService replies, CancellationToken ct) =>
            replies.ReplyAsync(new("proposal", id), request.Note, ct));
        app.MapPost("/api/investigations/{id}/reply", (string id, DecisionNoteRequest request, RecommendationReplyService replies, CancellationToken ct) =>
            replies.ReplyAsync(new("finding", id), request.Note, ct));
        app.MapPost("/api/investigations/{id}/followups/{stepId}/reply", (string id, string stepId, DecisionNoteRequest request, RecommendationReplyService replies, CancellationToken ct) =>
            replies.ReplyAsync(new("followup", id, stepId), request.Note, ct));
        app.MapPost("/api/investigations/{id}/filechanges/{changeId}/reply", (string id, string changeId, DecisionNoteRequest request, RecommendationReplyService replies, CancellationToken ct) =>
            replies.ReplyAsync(new("filechange", id, changeId), request.Note, ct));

        // Each close takes an optional note and an outcome: dismissed (the default), not_needed, or done.
        static string Outcome(DecisionNoteRequest? request) => RecommendationDecisions.Outcome(request?.Outcome);
        app.MapPost("/api/investigations/{id}/dismiss", (string id, DecisionNoteRequest? request, StateService state, IConfiguration configuration, CancellationToken ct) =>
        { var note = Note(request, configuration); var outcome = Outcome(request); return Decide(state, s => RecommendationDecisions.DismissFinding(s, id, note, outcome), ct); });
        app.MapPost("/api/investigations/{id}/followups/{stepId}/dismiss", (string id, string stepId, DecisionNoteRequest? request, StateService state, IConfiguration configuration, CancellationToken ct) =>
        { var note = Note(request, configuration); var outcome = Outcome(request); return Decide(state, s => RecommendationDecisions.DismissFollowUp(s, id, stepId, note, outcome), ct); });
        app.MapPost("/api/investigations/{id}/filechanges/{changeId}/dismiss", (string id, string changeId, DecisionNoteRequest? request, StateService state, IConfiguration configuration, CancellationToken ct) =>
        { var note = Note(request, configuration); var outcome = Outcome(request); return Decide(state, s => RecommendationDecisions.DismissFileChange(s, id, changeId, note, outcome), ct); });
        app.MapPost("/api/investigations/{id}/filechanges/{changeId}/applied", (string id, string changeId, StateService state, CancellationToken ct) =>
            Decide(state, s => InvestigationFileChanges.MarkApplied(s, id, changeId), ct));
    }
}
