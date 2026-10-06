namespace Joule;
public record ExperimentDecisionRequest(string Decision,string Notes,int ExtendDays,int Revision);
public record PermissionBoundsRequest(bool Allowed,double? Minimum,double? Maximum,double MaxStep=.1,int CooldownHours=72);
public record FileCaptureRequest(int Revision,string Reason);
public record FileRestoreRequest(int Revision,string ExpectedVersion,Dictionary<string,string> ExpectedHashes,string Notes);
public record FileReconcileRequest(int Revision,string Notes);
public static class ControlEndpoints
{
    public static WebApplication MapControlEndpoints(this WebApplication app)
    {
        app.MapPost("/api/experiments/{id}/decision",async(string id,ExperimentDecisionRequest r,StateService state,CancellationToken ct)=>{await state.MutateAsync(s=>ChangeEngine.Decide(s,id,r.Decision,r.Notes,r.ExtendDays,r.Revision),ct);return Results.Ok(new{ok=true});});
        app.MapPost("/api/permissions/{key}/bounds",async(string key,PermissionBoundsRequest r,StateService state,CancellationToken ct)=>{await state.MutateAsync(s=>ChangeEngine.PermissionBounds(s,key,r.Allowed,r.Minimum,r.Maximum,r.MaxStep,r.CooldownHours),ct);return Results.Ok(new{ok=true});});
        app.MapGet("/api/files",(ConfigFileArchive files)=>Results.Ok(new{status=files.Status(),versions=files.List()}));
        app.MapGet("/api/files/{id}/view",(string id,string file,ConfigFileArchive files)=>Results.Ok(files.View(id,file)));
        app.MapGet("/api/files/{id}/diff",(string id,string other,string file,ConfigFileArchive files)=>Results.Ok(files.Diff(other,id,file)));
        app.MapPost("/api/files/capture",async(FileCaptureRequest r,StateService state,CancellationToken ct)=>Results.Ok(await state.CaptureFilesAsync(r.Revision,r.Reason,ct)));
        app.MapPost("/api/files/{id}/restore",async(string id,FileRestoreRequest r,StateService state,CancellationToken ct)=>Results.Ok(await state.RestoreFilesAsync(id,r.Revision,r.ExpectedVersion,r.ExpectedHashes,r.Notes,ct)));
        app.MapPost("/api/files/reload-acknowledge",async(FileReconcileRequest r,StateService state,CancellationToken ct)=>{await state.AcknowledgeFileReloadAsync(r.Revision,r.Notes,ct);return Results.Ok(new{ok=true});});
        app.MapPost("/api/files/reconcile",async(FileReconcileRequest r,StateService state,CancellationToken ct)=>Results.Ok(await state.ReconcileFilesAsync(r.Revision,r.Notes,ct)));
        return app;
    }
}
