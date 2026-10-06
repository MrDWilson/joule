namespace Joule;

public static class AiCompletionEndpoints
{
    public static void MapAiCompletionEndpoints(this WebApplication app)
    {
        app.MapPost("/api/investigations/run", (AnalysisRequest request, AnalysisService analysis, IHostApplicationLifetime lifetime) => analysis.Start(request, lifetime.ApplicationStopping) ? Results.Accepted(value: new { ok = true }) : Results.Conflict(new { error = "An investigation is already running." }));
        app.MapPost("/api/investigations/cancel", (AnalysisService analysis) => analysis.Cancel() ? Results.Accepted(value: new { ok = true }) : Results.Conflict(new { error = "No investigation is running." }));
        app.MapGet("/api/investigations/{id}", (string id, StateService state, DataStore db, IConfiguration configuration) => (state.ReadInvestigation(id) ?? StateEndpoints.ReadArchived(db, configuration, id)) is { } i ? Results.Ok(i) : Results.NotFound(new { error = "Investigation not found." }));
        app.MapGet("/api/memory", (DataStore db) => Results.Ok(db.ListMemory()));
        app.MapPost("/api/memory", (MemoryRequest request, DataStore db) => Results.Ok(new { ok = true, fact = db.AddMemory(request.Text, "user") }));
        app.MapPost("/api/memory/{id}/delete", (string id, DataStore db) => db.DeleteMemory(id) ? Results.Ok(new { ok = true }) : Results.NotFound(new { error = "Fact not found." }));
        app.MapGet("/api/documentation/status", (DocumentationService docs) => Results.Ok(docs.Status()));
        app.MapPost("/api/documentation/search", async (DocumentationSearchRequest request, DocumentationService docs, CancellationToken ct) => Results.Ok(await docs.SearchAsync(request.Query, ct)));
        app.MapGet("/api/reports", (StateService state) => Results.Ok(state.Read(false).Reports.OrderByDescending(r => r.CreatedAt)));
        app.MapPost("/api/reports/generate", async (ReportRequest request, ReportService reports, CancellationToken ct) => Results.Ok(await reports.GenerateAsync(request, ct)));
        app.MapPost("/api/reports/{id}/read", async (string id, ReportService reports, CancellationToken ct) => { await reports.MarkReadAsync(id, ct); return Results.Ok(new { ok = true }); });
        app.MapPost("/api/reports/preferences", async (ReportPreferences request, ReportService reports, CancellationToken ct) => { await reports.SavePreferencesAsync(request, ct); return Results.Ok(new { ok = true }); });
        app.MapGet("/api/reports/{id}", (string id, ReportService reports) => Results.Ok(reports.View(id)));
        app.MapGet("/api/notifications", (StateService state) => Results.Ok(state.Read(false).Notifications.OrderByDescending(n => n.At)));
        app.MapPost("/api/notifications/{id}/read", async (string id, StateService state, CancellationToken ct) => { await state.MutateAsync(s => { var n = s.Notifications.FirstOrDefault(n => n.Id == id) ?? throw new DomainException("Notification not found.", 404); n.ReadAt ??= DateTimeOffset.UtcNow; }, ct); return Results.Ok(new { ok = true }); });
    }
}
public record DocumentationSearchRequest(string Query);
public record MemoryRequest(string Text);
