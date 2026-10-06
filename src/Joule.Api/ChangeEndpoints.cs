namespace Joule;

/// <summary>Read-only previews of edits, undos and restores (what changes, what Predbat controls are left alone, which trials
/// end or are confounded), the settings catalogue's sections and kinds, and the demo reset.</summary>
public static class ChangeEndpoints
{
    public static void MapChangeEndpoints(this WebApplication app)
    {
        app.MapGet("/api/settings/catalogue", () => new
        {
            docsRef = PredbatSettingsCatalogue.DocsRef,
            sections = PredbatSettingsCatalogue.Sections,
            kinds = SettingKind.All,
            glossaryDocsRef = PredbatGlossary.DocsRef,
        });
        app.MapGet("/api/settings/{key}/preview", (string key, string? value, StateService state) =>
        {
            if (value is null) throw new DomainException("Value is required.", 400);
            return ChangeEngine.PreviewEdit(state.Read(false), key, value);
        });
        app.MapGet("/api/revisions/{id:int}/revert/preview", (int id, StateService state) => ChangeEngine.PreviewRevert(state.Read(false), id));
        app.MapGet("/api/revisions/{id:int}/restore/preview", (int id, StateService state) => ChangeEngine.PreviewRestore(state.Read(false), id));
        app.MapPost("/api/demo/reset", async (StateService state, CancellationToken ct) => { await state.ResetDemoAsync(ct); return Results.Ok(new { ok = true }); });
    }
}
