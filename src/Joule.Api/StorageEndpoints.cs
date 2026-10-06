namespace Joule;

public static class StorageEndpoints
{
    /// <summary>GET /api/storage: database size (dbSizeBytes), snapshot and observation counts, plan-detail backfill progress and
    /// the last retention pass (dry run by default). Also routes storage log lines to the app's logger.</summary>
    public static void MapStorageEndpoints(this WebApplication app)
    {
        var db = app.Services.GetRequiredService<DataStore>();
        db.Logger ??= app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Joule.Storage");
        app.MapGet("/api/storage", (DataStore store, StateService state) => store.ReadStorageReport(state.RetentionMode, state.RetentionKeepDays));
    }
}
