namespace Joule;

public record ObjectiveRequest(string? Objective);

/// <summary>AI check controls added by the AI-quality work: resume a check that didn't finish, the household objective and claims.</summary>
public static class AiQualityEndpoints
{
    public static void MapAiQualityEndpoints(this WebApplication app)
    {
        // Continue a failed or interrupted check from its saved steps instead of paying for them again.
        app.MapPost("/api/investigations/{id}/resume", (string id, StateService state, AnalysisService analysis, IHostApplicationLifetime lifetime) =>
        {
            var item = state.Read(false).Investigations.FirstOrDefault(i => i.Id == id) ?? throw new DomainException("Investigation not found.", 404);
            if (item.Status is not ("Failed" or "Interrupted")) throw new DomainException("Only a check that didn't finish can be resumed.", 409);
            return analysis.Start(new AnalysisRequest(ResumeOf: id), lifetime.ApplicationStopping)
                ? Results.Accepted(value: new { ok = true })
                : Results.Conflict(new { error = "A check is already running." });
        });
        app.MapGet("/api/ai/objective", (StateService state) => Results.Ok(Objective(state.Read(false).HouseholdObjective)));
        app.MapPost("/api/ai/objective", async (ObjectiveRequest request, StateService state, CancellationToken ct) =>
        {
            if (request.Objective is not { } objective || !HouseholdObjective.All.Contains(objective))
                throw new DomainException($"Choose one of: {string.Join(", ", HouseholdObjective.All)}.", 400);
            await state.MutateAsync(s => { s.HouseholdObjective = objective; ChangeEngine.Log(s, "provider", $"AI checks now aim to: {HouseholdObjective.Label(objective)}."); }, ct);
            return Results.Ok(Objective(objective));
        });
        app.MapGet("/api/ai/claims", (StateService state) => Results.Ok(state.Read(false).Claims.OrderByDescending(c => c.CreatedAt).Take(50)));
    }

    static object Objective(string objective) => new
    {
        objective,
        label = HouseholdObjective.Label(objective),
        description = HouseholdObjective.Describe(objective),
        options = HouseholdObjective.All.Select(o => new { value = o, label = HouseholdObjective.Label(o), description = HouseholdObjective.Describe(o) })
    };
}
