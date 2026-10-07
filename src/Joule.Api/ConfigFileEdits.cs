using System.Text;

namespace Joule;

/// <summary>
/// Whether Joule may edit a configuration file itself. Configured: the file is mounted and listed (ConfigFiles__Root and
/// ConfigFiles__AllowedFiles). Allowed: the owner switched on "Let Joule edit Predbat's config files" (ConfigFiles__AllowEdits, saved
/// from Setup or set in the environment). Writable: Joule's process can write the file. Reason says, in words, what is missing.
/// </summary>
public sealed record ConfigEditStatus(bool Demo, bool Configured, string? File, bool Writable, bool Allowed, string? AllowedSource, bool Quarantined, bool CanApply, string? Reason, string? Root);

/// <summary>The review of one suggested edit: what the file would look like (masked diff), or why Joule won't make it.</summary>
public sealed record ConfigEditReview(string File, bool CanApply, string? Reason, string? Problem, string? Hash, string? Placement, List<ConfigKeyChange> Keys, List<DiffLine> Lines, string Note);

public sealed record ConfigEditApplyRequest(string? Hash);

/// <summary>
/// "Apply for me" for configuration-file edits the AI suggests (apps.yaml). Review shows a masked diff of the real file against the result;
/// Apply snapshots the exact file, writes the edit (see <see cref="YamlFileEdit"/> and <see cref="ConfigFileArchive.ReplaceFile"/>), then
/// watches Predbat reload it and puts the snapshot back by itself if Predbat has a problem. Restore puts the snapshot back on request.
/// Every step is recorded in the Changes timeline. Secret values never leave the server: the diff hides them, and the file text is never
/// sent to the AI.
/// </summary>
public sealed class ConfigFileEditService(ConfigFileArchive archive, StateService state, IConfiguration configuration, IPredbatReloadWatcher watcher, ILogger<ConfigFileEditService>? logger = null)
{
    public const string SettingKey = "ConfigFiles:AllowEdits";
    readonly object gate = new();
    readonly HashSet<string> watching = [];
    /// <summary>Background reload checks still running (tests wait on these).</summary>
    public Task Watching { get { lock (gate) return Task.WhenAll(watches); } }
    readonly List<Task> watches = [];
    public CancellationToken Stopping { get; set; }

    bool Allowed => state.Demo || bool.TryParse(configuration[SettingKey], out var on) && on;

    public ConfigEditStatus Status(string? source = null)
    {
        var file = archive.Allows("apps.yaml") ? "apps.yaml" : archive.Allows("apps.yml") ? "apps.yml" : null;
        var configured = file is not null;
        var writable = configured && archive.CanWrite(file!);
        var quarantined = archive.Status().Quarantined;
        var root = state.Demo ? null : configuration["ConfigFiles:Root"];
        string? reason = !configured
            ? "Joule can't see Predbat's apps.yaml. Mount Predbat's config folder into Joule and list apps.yaml (see Setup)."
            : !Allowed ? "Editing Predbat's config files is switched off in Setup."
            : !writable ? $"Joule can read {file} but can't write it. Give Joule's container write access to it (see Setup)."
            : quarantined ? "An earlier file operation needs reviewing in Files first."
            : null;
        return new(state.Demo, configured, file, writable, Allowed, source, quarantined, reason is null, reason, string.IsNullOrWhiteSpace(root) ? null : root);
    }

    static (Investigation Investigation, ConfigFileChange Change) Find(AppState s, string investigationId, string changeId)
    {
        var investigation = s.Investigations.FirstOrDefault(i => i.Id == investigationId) ?? throw new DomainException("Investigation not found.", 404);
        return (investigation, investigation.FileChanges.FirstOrDefault(x => x.Id == changeId) ?? throw new DomainException("Configuration file change not found.", 404));
    }

    string FileFor(ConfigFileChange change) =>
        archive.Allows(change.File) ? change.File
        : change.File is "apps.yaml" or "apps.yml" && Status().File is { } mounted ? mounted
        : throw new DomainException($"Joule can only edit the files you've mounted for it, and {change.File} isn't one of them.", 409);

    /// <summary>The masked diff of the file as it is now against the file with the edit made, or the plain reason it can't be made.</summary>
    public ConfigEditReview Review(string investigationId, string changeId)
    {
        // The read copy has credentials the AI may have written redacted: that is what you review, and what gets applied.
        var (_, change) = Find(state.Read(false), investigationId, changeId);
        var status = Status();
        if (!status.Configured) return new(change.File, false, status.Reason, null, null, null, [], [], Note);
        var file = FileFor(change);
        var (text, hash) = archive.ReadText(file);
        try
        {
            var plan = YamlFileEdit.Plan(text, change.Location, change.Snippet, change.Before, file);
            var lines = ConfigFileMask.Hunks(ConfigFileMask.Mask(file, text, keepSafeValues: true, keepComments: true), ConfigFileMask.Mask(file, plan.Text, keepSafeValues: true, keepComments: true));
            return new(file, status.CanApply && InvestigationFileChanges.IsOpen(change) && change.Edit is not { Check: "checking" }, status.Reason, null, hash, plan.Placement, plan.Keys, lines, Note);
        }
        catch (YamlEditRefused refused) { return new(file, false, status.Reason, refused.Message, hash, null, [], [], Note); }
    }
    const string Note = "Values that look like passwords or keys are hidden (•••); !secret references are shown as written.";

    /// <summary>Makes the edit. <paramref name="expectedHash"/> is the file as reviewed: if it has changed since, nothing is written.</summary>
    public async Task<ConfigFileEdit> ApplyAsync(string investigationId, string changeId, string? expectedHash, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(expectedHash)) throw new DomainException("Review the edit first.", 400);
        var status = Status();
        if (!status.CanApply) throw new DomainException(status.Reason ?? "Joule can't edit this file.");
        ConfigFileEdit? made = null;
        await state.FileOperationAsync(next =>
        {
            var (_, change) = Find(next, investigationId, changeId);
            if (!InvestigationFileChanges.IsOpen(change)) throw new DomainException("This edit is closed. Reopen it first.");
            if (change.Status == "applied") throw new DomainException(change.Edit is null ? "You've marked this edit as applied. Undo that first if you'd like Joule to make it." : "Joule has already made this edit.");
            var file = FileFor(change);
            // Plan from the reviewed (read) copy, never from text the browser sent.
            var reviewed = Find(state.Read(false), investigationId, changeId).Change;
            var (text, hash) = archive.ReadText(file);
            if (hash != expectedHash) throw new DomainException($"{file} has changed since you reviewed this edit. Review it again.");
            var plan = YamlFileEdit.Plan(text, reviewed.Location, reviewed.Snippet, reviewed.Before, file);
            var summary = Cut(change.Summary, 200);
            var result = archive.ReplaceFile(file, Encoding.UTF8.GetBytes(plan.Text), hash, next.Revision, $"Joule's edit: {summary}", $"Before Joule's edit: {summary}");
            next.LastFileVersionId = result.After.Id;
            var at = DateTimeOffset.UtcNow;
            var ev = new SettingEvent { At = at, Kind = SettingKind.File, Key = "file:" + file, Name = file, Title = $"Joule edited {file}: {summary}", Before = "", After = string.Join(", ", plan.Keys.Select(k => k.Name)) };
            next.SettingEvents.Add(ev);
            made = new ConfigFileEdit
            {
                At = at, SnapshotVersion = result.Before.Id, AppliedVersion = result.After.Id, BeforeHash = result.BeforeHash, AfterHash = result.AfterHash,
                Placement = plan.Placement, Keys = plan.Keys.Select(k => k.Name).ToList(), EventId = ev.Id,
                CheckNote = state.Demo ? null : "Watching Predbat reload apps.yaml…",
            };
            change.Status = "applied"; change.AppliedAt = at; change.Edit = made;
            foreach (var trial in next.Experiments.Where(ChangeEngine.IsOpen)) { trial.Status = "Needs review"; trial.Result = $"An edit to {file} confounds this trial."; }
            ChangeEngine.Log(next, "configuration", $"Joule edited {file} for “{summary}” ({plan.Placement.ToLowerInvariant()}). A copy of the previous file is kept.");
        }, ct);
        var watch = Watch(investigationId, changeId, made!.At);
        // The demo has no Predbat to wait for, so its check finishes before the reply and the card shows the outcome at once.
        if (state.Demo) await watch;
        return made;
    }

    Task Watch(string investigationId, string changeId, DateTimeOffset writtenAt)
    {
        lock (gate)
        {
            if (!watching.Add(changeId)) return Task.CompletedTask;
            watches.RemoveAll(t => t.IsCompleted);
            var task = Task.Run(() => WatchAsync(investigationId, changeId, writtenAt));
            watches.Add(task);
            return task;
        }
    }

    async Task WatchAsync(string investigationId, string changeId, DateTimeOffset writtenAt)
    {
        try
        {
            ReloadOutcome outcome;
            try { outcome = await watcher.WatchAsync(writtenAt, Stopping); }
            catch (OperationCanceledException) { return; }
            catch (Exception e) { logger?.LogWarning("Reload check failed: {Type}", e.GetType().Name); outcome = new(true, false, "Joule couldn't finish checking that Predbat reloaded apps.yaml. Check Predbat if the change doesn't take effect."); }
            if (outcome.Ok)
            {
                await state.FileOperationAsync(next =>
                {
                    var (_, change) = Find(next, investigationId, changeId);
                    if (change.Edit is not { Check: "checking" } edit) return;
                    edit.Check = outcome.Reloaded ? "confirmed" : "unconfirmed"; edit.CheckNote = outcome.Note; edit.CheckedAt = DateTimeOffset.UtcNow;
                    ChangeEngine.Log(next, "configuration", $"{change.File} edit “{Cut(change.Summary, 120)}”: {outcome.Note}");
                }, CancellationToken.None, captureExternal: false);
                return;
            }
            await PutBackAsync(investigationId, changeId, automatic: true, outcome.Note, CancellationToken.None);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger?.LogWarning("Reload follow-up failed: {Type}", e.GetType().Name);
        }
        finally { lock (gate) watching.Remove(changeId); }
    }

    /// <summary>Puts the file back exactly as it was before Joule's edit ("Restore previous version").</summary>
    public Task RestoreAsync(string investigationId, string changeId, CancellationToken ct = default) => PutBackAsync(investigationId, changeId, automatic: false, null, ct);

    async Task PutBackAsync(string investigationId, string changeId, bool automatic, string? why, CancellationToken ct)
    {
        if (!automatic && !Status().Configured) throw new DomainException(Status().Reason ?? "Joule can't edit this file.");
        await state.FileOperationAsync(next =>
        {
            var (_, change) = Find(next, investigationId, changeId);
            var edit = change.Edit ?? throw new DomainException("Joule didn't make this edit, so there is no copy of its own to put back. Use Files to restore an earlier copy.");
            if (edit.Check is "restored" or "rolled_back") throw new DomainException("The previous version is already back.");
            if (automatic && edit.Check != "checking") return;
            var file = FileFor(change);
            var (_, hash) = archive.ReadText(file);
            var now = DateTimeOffset.UtcNow;
            var summary = Cut(change.Summary, 200);
            if (hash != edit.AfterHash)
            {
                if (!automatic) throw new DomainException($"{file} has changed since Joule's edit, so putting the old copy back would undo those changes too. Use Files to choose a copy to restore.");
                edit.Check = "attention"; edit.CheckedAt = now;
                edit.CheckNote = $"{why} Joule didn't put the old {file} back because the file changed again after its edit. Check it in Files.";
                ChangeEngine.Log(next, "error", $"{file} edit “{summary}”: {edit.CheckNote}");
                return;
            }
            var bytes = archive.ReadVersionFile(edit.SnapshotVersion, file);
            var reason = automatic ? $"Joule put {file} back after its edit: {summary}" : $"You restored {file} to before Joule's edit: {summary}";
            var result = archive.ReplaceFile(file, bytes, hash, next.Revision, reason, $"Joule's edit, before putting it back: {summary}");
            next.LastFileVersionId = result.After.Id;
            edit.Check = automatic ? "rolled_back" : "restored"; edit.RestoredAt = now; edit.RestoredVersion = result.After.Id; edit.CheckedAt ??= now;
            edit.CheckNote = automatic ? $"{why} Joule put the previous {file} back." : edit.CheckNote;
            if (next.SettingEvents.FirstOrDefault(e => e.Id == edit.EventId) is { } original) original.RevertedAt = now;
            next.SettingEvents.Add(new SettingEvent
            {
                At = now, Kind = SettingKind.File, Key = "file:" + file, Name = file, Before = "", After = "",
                Title = automatic ? $"Joule put {file} back: Predbat had a problem with “{summary}”" : $"You put {file} back as it was before “{summary}”",
            });
            change.Status = "pending"; change.AppliedAt = null;
            ChangeEngine.Log(next, automatic ? "error" : "configuration", automatic ? $"{why} Joule put the previous {file} back." : $"You restored {file} to the copy taken before Joule's edit.");
        }, ct);
    }

    /// <summary>On start: edits whose reload check was cut short by a restart are checked again from now.</summary>
    public void ResumeChecks()
    {
        foreach (var investigation in state.Read(false).Investigations)
            foreach (var change in investigation.FileChanges.Where(c => c.Edit is { Check: "checking" }))
                Watch(investigation.Id, change.Id, DateTimeOffset.UtcNow);
    }

    static string Cut(string text, int max) => text.Length <= max ? text : text[..max] + "…";
}

public static class ConfigFileEditEndpoints
{
    public static WebApplication MapConfigFileEditEndpoints(this WebApplication app)
    {
        app.MapGet("/api/config-edits/status", (ConfigFileEditService edits, SavedSettings saved) => edits.Status(saved.Source(ConfigFileEditService.SettingKey)));
        app.MapGet("/api/investigations/{id}/filechanges/{changeId}/review", (string id, string changeId, ConfigFileEditService edits) => edits.Review(id, changeId));
        app.MapPost("/api/investigations/{id}/filechanges/{changeId}/apply", async (string id, string changeId, ConfigEditApplyRequest request, ConfigFileEditService edits, CancellationToken ct) =>
            Results.Ok(await edits.ApplyAsync(id, changeId, request.Hash, ct)));
        app.MapPost("/api/investigations/{id}/filechanges/{changeId}/restore", async (string id, string changeId, ConfigFileEditService edits, CancellationToken ct) =>
        {
            await edits.RestoreAsync(id, changeId, ct);
            return Results.Ok(new { ok = true });
        });
        return app;
    }
}
