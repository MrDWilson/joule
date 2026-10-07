namespace Joule;

public sealed class StateService
{
    readonly SemaphoreSlim mutation = new(1, 1);
    readonly DataStore db;
    readonly IPredbatClient client;
    readonly ConfigFileArchive? archive;
    readonly InvestigationReadSanitizer investigationReadSanitizer;
    AppState current;
    public bool Demo { get; }
    /// <summary>Storage__SnapshotRetention: off | dry-run (default) | enforce. Dry run only reports what would be pruned.</summary>
    public string RetentionMode { get; }
    public int RetentionKeepDays { get; }
    DateTimeOffset lastRetention = DateTimeOffset.MinValue;
    public static readonly TimeSpan RetentionInterval = TimeSpan.FromHours(6);
    /// <summary>The running demo starts with a month of sample history (trials, claims, today's checks); test fixtures start plain.</summary>
    readonly bool sampleHistory;
    public StateService(DataStore db, IPredbatClient client, bool demo, ConfigFileArchive? archive = null, IConfiguration? configuration = null, bool sampleHistory = false)
    {
        this.db = db; this.client = client; Demo = demo; this.archive = archive; this.sampleHistory = sampleHistory; investigationReadSanitizer = new(configuration);
        RetentionMode = configuration?["Storage:SnapshotRetention"]?.Trim().ToLowerInvariant() is DataStore.RetentionOff or DataStore.RetentionEnforce ? configuration["Storage:SnapshotRetention"]!.Trim().ToLowerInvariant() : DataStore.RetentionDryRun;
        RetentionKeepDays = int.TryParse(configuration?["Storage:SnapshotKeepDays"], out var keep) && keep is >= 1 and <= 3650 ? keep : 14;
        current = db.Load(false) ?? (demo ? DemoData.Create(history: sampleHistory) : new AppState { DataSource = "Live", Mode = "Monitor", Ai = new AiPreferences { Provider = "Api" } });
        if (current.DataSource != (demo ? "Demo" : "Live")) throw new InvalidOperationException("Use separate data directories for demo and live data.");
        if (db.HasUncertainWrite()) { current.WriteUncertain = true; ChangeEngine.Log(current, "error", "An unfinished write was found on restart. Review live configuration and reconcile before further changes."); }
        if (archive?.DemoRuntimeMirror == true && !demo) throw new InvalidOperationException("Demo file mirror is prohibited in live mode.");
        if (archive?.Status().Quarantined == true) current.WriteUncertain = true;
        else if (archive?.DemoRuntimeMirror == true && archive.List().Count==0) { archive.MirrorDemoRuntime(current); current.LastFileVersionId=archive.Capture(current.Revision,"Demo settings file").Id; }
        // Revisions and trials recorded for Predbat's own controls before settings were classified become timeline events.
        if (ChangeEngine.MigrateSettingKinds(current)) ChangeEngine.Log(current, "configuration", "Predbat's own controls (version updates, manual overrides, mode) are now shown as History events rather than settings trials.");
        db.Save(current); current=DataStore.StateSummary(current);
        if (demo && db.GetPlan() == null) for (var i = 9; i >= 0; i--) db.SavePlan(DemoData.Plan(i));
        if (demo && sampleHistory && db.ListMemory().Count == 0) foreach (var fact in DemoData.MemoryFacts) db.AddMemory(fact, "model");
    }
    public AppState Read(bool includeEvidence = true)
    {
        var result=DataStore.StateSummary(current);
        ChangeEngine.PresentForRead(result);
        if(includeEvidence) db.HydrateInvestigationEvidence(result);
        // The inbox's keys come from the stored text, so decide what is still open before that text is masked for the browser.
        NotificationInbox.Present(result, DateTimeOffset.UtcNow);
        foreach(var investigation in result.Investigations) investigationReadSanitizer.SanitizeCopy(investigation);
        foreach(var proposal in result.Proposals) investigationReadSanitizer.SanitizeCopy(proposal);
        foreach(var revision in result.Revisions) investigationReadSanitizer.SanitizeCopy(revision);
        result.AnalysisError = investigationReadSanitizer.Clean(result.AnalysisError);
        result.Activities = result.Activities.Select(a => a with { Message = investigationReadSanitizer.Clean(a.Message)! }).ToList();
        foreach(var item in result.Inbox){item.Title=investigationReadSanitizer.Clean(item.Title)!;item.Detail=investigationReadSanitizer.Clean(item.Detail);}
        foreach(var e in result.Experiments)
        {
            // Old persisted scores may have used native/embedded Predbat values.
            // Withhold them on the read copy without rewriting the audit record.
            if(!Demo && e.ForecastEvidenceVersion<Experiment.CurrentForecastEvidenceVersion &&
                (e.BaselineError is not null || e.CurrentError is not null || e.BaselineForecastCoverage>0 || e.CurrentForecastCoverage>0))
            {
                e.BaselineError=null;e.CurrentError=null;
                e.BaselineForecastCoverage=0;e.CurrentForecastCoverage=0;
                e.ForecastMethod="Legacy forecast scores are withheld because their meter provenance was not verified and may include Predbat-derived values. Any recorded outcome that relied on that accuracy remains historical and unverified; original metrics and decisions are preserved in stored history.";
            }
            var eligibility=ChangeEngine.RollbackEligibility(result,e);e.RevertEligible=eligibility.Eligible;e.AutomaticRevertEligible=eligibility.AutomaticEligible;e.RevertReason=eligibility.Reason;
            investigationReadSanitizer.SanitizeCopy(e);
        }
        return result;
    }
    /// <summary>A copy of the stored state with nothing masked or presented, for server-side work that must see the text as saved
    /// (the notifications inbox keys items by it). Never send this to the browser.</summary>
    public AppState ReadRaw() => DataStore.StateSummary(current);
    /// <summary>Cheap read of one current setting value (no state copy), e.g. Predbat's reported version.</summary>
    public string? SettingValue(string key) => current.Settings.FirstOrDefault(x => x.Key == key)?.Value;
    public Investigation? ReadInvestigation(string id)
    {
        var item = Read(false).Investigations.FirstOrDefault(i => i.Id == id);
        if (item != null) { item.ToolEvidence = db.ReadInvestigationEvidence(id); investigationReadSanitizer.SanitizeCopy(item); }
        return item;
    }
    public async Task MutateAsync(Action<AppState> edit, CancellationToken ct = default)
    {
        await mutation.WaitAsync(ct);
        try
        {
            var next = JsonDefaults.Clone(current); edit(next);
            ConfigFileVersion? beforeFiles=null;
            if(next.Revision>current.Revision && archive?.Enabled==true)
            {
                if(archive.Status().Quarantined)throw new DomainException("A file restore requires reconciliation before configuration changes.");
                var previousFiles=PublishedFiles(archive,current);
                beforeFiles=archive.Capture(current.Revision,"Before runtime change",true);
                if(previousFiles!=null && !SameFiles(previousFiles,beforeFiles))
                {
                    var external=JsonDefaults.Clone(current); TrackFileChange(external,beforeFiles,"External files","External mounted configuration file change");
                    db.Save(external);current=DataStore.StateSummary(external);
                    if(next.Revisions[^1].Source is "Auto" or "Automatic rollback")throw new DomainException("An external file change requires review before automatic tuning.");
                    TrackFileChange(next,beforeFiles,"External files","External mounted configuration file change");
                }
                next.Revisions[^1].FileVersionBefore=beforeFiles.Id;
            }
            if (!Demo && next.Revision > current.Revision)
            {
                var changes = next.Revisions[^1].Changes;
                if (!client.WritesEnabled) throw new DomainException("Live writes are disabled. Enable Predbat:WritesEnabled in your deployment before applying changes.");
                var journal = db.BeginWrite(changes);
                var stateSaved = false;
                try
                {
                    await client.ApplyAsync(changes, current.Settings, ct);
                    // A plan produced while remote writes were still in flight does not
                    // establish a forecast for the verified configuration.
                    var verifiedAt=DateTimeOffset.UtcNow;next.Revisions[^1].At=verifiedAt;
                    foreach(var trial in next.Experiments.Where(e=>e.RevisionId==next.Revision && e.FileVersionId==null))
                    {
                        var reviewDuration=trial.ReviewAt-trial.StartedAt;
                        trial.StartedAt=verifiedAt;trial.ReviewAt=verifiedAt+reviewDuration;
                    }
                    if(archive?.Enabled==true)
                    {
                        var afterFiles=archive.Capture(next.Revision,"After runtime change");
                        next.Revisions[^1].FileVersionAfter=afterFiles.Id;next.LastFileVersionId=afterFiles.Id;
                        // Runtime verification cannot establish the cause or runtime effect
                        // of a concurrent mounted-file change, including Predbat-owned edits.
                        if(beforeFiles!=null && !SameFiles(beforeFiles,afterFiles))
                            TrackFileChange(next,afterFiles,"Observed files","Mounted configuration files changed during runtime update");
                    }
                    db.Save(next); stateSaved = true;
                    db.EndWrite(journal, "verified");
                    current = DataStore.StateSummary(next);
                }
                catch (Exception ex)
                {
                    var uncertain = ex is not LiveWriteException writeError || writeError.Uncertain;
                    var failed = JsonDefaults.Clone(stateSaved ? next : current); failed.WriteUncertain = uncertain;
                    ChangeEngine.Log(failed, "error", uncertain ? "A live write or its local persistence could not be completed. Further writes are blocked pending reconciliation." : "Predbat rejected the change before it was applied.");
                    // Quarantine the running process before touching storage: either recovery write
                    // can fail too (for example disk-full), while the durable pending journal remains.
                    current = DataStore.StateSummary(failed);
                    try { db.EndWrite(journal, uncertain ? "uncertain" : "rejected"); } catch { }
                    try { db.Save(failed); } catch { }
                    throw;
                }
            }
            else
            {
                var mirrored=false;
                try
                {
                    if(next.Revision>current.Revision && archive?.Enabled==true)
                    {
                        archive.MirrorDemoRuntime(next); mirrored=archive.DemoRuntimeMirror;
                        next.Revisions[^1].FileVersionAfter=archive.Capture(next.Revision,"After demo runtime change").Id;next.LastFileVersionId=next.Revisions[^1].FileVersionAfter;
                    }
                    db.Save(next); current = DataStore.StateSummary(next);
                }
                catch
                {
                    if(mirrored){current.WriteUncertain=true;try{archive!.Quarantine("Demo runtime mirror or state persistence failed.");}catch{}try{db.Save(current);}catch{}}
                    throw;
                }
            }
        }
        finally { mutation.Release(); }
    }
    public async Task CollectAsync(CancellationToken ct = default)
    {
        await mutation.WaitAsync(ct);
        try { await CollectLockedAsync(ct); }
        finally { mutation.Release(); }
    }
    // Caller owns mutation throughout the read and publication. Reconciliation uses this
    // helper so no write can start between refreshing remote values and closing journals.
    async Task CollectLockedAsync(CancellationToken ct)
    {
        if (Demo)
        {
            var p = DemoData.Plan(); p.Id = Guid.NewGuid().ToString("N"); p.At = DateTimeOffset.UtcNow; db.SavePlan(p);
            var next = JsonDefaults.Clone(current); next.LastCollection = DateTimeOffset.UtcNow; next.CollectionError = null;
            CaptureExternalFiles(next); db.Save(next); current = DataStore.StateSummary(next); return;
        }
        try
        {
            var snapshot = await client.CollectAsync(ct);
            var next = JsonDefaults.Clone(current);
            foreach (var setting in snapshot.Settings)
            {
                PredbatSettingsCatalogue.Apply(setting);
                var previous=next.Settings.FirstOrDefault(x=>x.Key==setting.Key);
                setting.AutoAllowed = setting.AutoEligible && (previous?.AutoAllowed ?? false);
                setting.AutoMinimum=previous?.AutoMinimum; setting.AutoMaximum=previous?.AutoMaximum;
                setting.AutoMaxStep=previous?.AutoMaxStep ?? PredbatSettingsCatalogue.DefaultAutoStep(setting); setting.AutoCooldownHours=previous?.AutoCooldownHours ?? 72;
            }
            // Only tunable settings become revisions and trials. Predbat's own controls (version updates, manual overrides,
            // mode, read-only) become History events; calculation activity and debug values are ignored, so none of them
            // invalidate proposals or confound trials.
            ChangeEngine.RecordObserved(next, snapshot.Settings, DateTimeOffset.UtcNow);
            next.Settings = snapshot.Settings;
            CaptureExternalFiles(next);
            next.LastCollection = DateTimeOffset.UtcNow; next.CollectionError = null;
            if (snapshot.Plan != null) db.SavePlan(snapshot.Plan, snapshot.RawState, snapshot.RawPlan);
            else db.SaveSource(snapshot.RawState, snapshot.RawPlan);
            db.Save(next); current = DataStore.StateSummary(next);
            MaintainStorage(next, DateTimeOffset.UtcNow);
        }
        catch (Exception)
        {
            var failed = JsonDefaults.Clone(current); failed.CollectionError = "Predbat collection failed. Check the endpoint, credentials and container network. The last good snapshot is retained.";
            current = DataStore.StateSummary(failed);
            try { db.Save(failed); } catch { }
            throw;
        }
    }
    /// <summary>After each live collection: re-derive a batch of stored plans from their raw snapshots (split states, targets,
    /// reasons), and every six hours run the snapshot retention pass. Retention never runs before that backfill completes and
    /// protects the comparison window of every open experiment. Storage upkeep never fails a collection.</summary>
    void MaintainStorage(AppState state, DateTimeOffset now)
    {
        try
        {
            // Small batches keep each poll's hold on the database short (about 900 live plans take ~4 hours).
            db.BackfillPlanDetails(20);
            if (now - lastRetention < RetentionInterval) return;
            lastRetention = now;
            var open = state.Experiments.Where(ChangeEngine.IsOpen).Select(e => e.StartedAt - (e.ReviewAt - e.StartedAt > TimeSpan.FromDays(7) ? e.ReviewAt - e.StartedAt : TimeSpan.FromDays(7))).ToList();
            db.RunSnapshotRetention(RetentionMode, now, RetentionKeepDays, open.Count == 0 ? null : open.Min());
        }
        catch (Exception e) when (e is not OperationCanceledException) { db.Logger?.LogWarning("Storage upkeep failed: {Type}", e.GetType().Name); }
    }
    /// <summary>Demo only: puts the sample household back to its starting point (settings, suggestion, follow-up, history and
    /// plans) so every flow can be tried again. Telemetry is kept.</summary>
    public async Task ResetDemoAsync(CancellationToken ct = default)
    {
        if (!Demo) throw new DomainException("Only the demo can be reset.", 404);
        await mutation.WaitAsync(ct);
        try
        {
            var fresh = DemoData.Create(history: sampleHistory);
            db.ResetDemoData();
            if (archive?.Enabled == true && archive.DemoRuntimeMirror && !archive.Status().Quarantined)
            {
                archive.MirrorDemoRuntime(fresh);
                fresh.LastFileVersionId = archive.Capture(fresh.Revision, "Demo reset").Id;
            }
            ChangeEngine.Log(fresh, "configuration", "You reset the demo to its starting point.");
            db.Save(fresh); current = DataStore.StateSummary(fresh);
            for (var i = 9; i >= 0; i--) db.SavePlan(DemoData.Plan(i));
        }
        finally { mutation.Release(); }
    }
    public async Task ReconcileAsync(CancellationToken ct)
    {
        await mutation.WaitAsync(ct);
        try
        {
            await CollectLockedAsync(ct);
            if(archive?.Status().Quarantined==true)throw new DomainException("Review and reconcile the partial file restore first.");
            var next = JsonDefaults.Clone(current);
            next.WriteUncertain = false;
            ChangeEngine.Log(next, "configuration", "You acknowledged the refreshed live configuration after an uncertain write.");
            // Close the existing journals while writes are excluded. Publish permission to
            // write only after both reconciliation and state persistence have succeeded.
            db.ReconcileWrites();
            db.Save(next); current = DataStore.StateSummary(next);
        }
        finally { mutation.Release(); }
    }
    void CaptureExternalFiles(AppState next)
    {
        if(archive?.Enabled!=true)return;
        var before=PublishedFiles(archive,next);
        var captured=archive.Capture(next.Revision,"Observed mounted configuration files",true);
        if(before!=null && !SameFiles(before,captured)) TrackFileChange(next,captured,"External files","External mounted configuration file change");
        next.LastFileVersionId=captured.Id;
    }
    static bool SameFiles(ConfigFileVersion before,ConfigFileVersion after)=>before.Files.OrderBy(x=>x.Path).SequenceEqual(after.Files.OrderBy(x=>x.Path));
    static ConfigFileVersion? PublishedFiles(ConfigFileArchive files,AppState state)
    {
        var versions=files.List();
        return state.LastFileVersionId==null?versions.FirstOrDefault():versions.FirstOrDefault(x=>x.Id==state.LastFileVersionId)??throw new DomainException("The published file version is missing. Review and reconcile file history before further changes.");
    }
    static void TrackFileChange(AppState next,ConfigFileVersion captured,string source,string title)
    {
        foreach(var e in next.Experiments.Where(ChangeEngine.IsOpen)){e.Status="Needs review";e.Result="Configuration file changes confound this trial.";}
        next.Experiments.Add(new Experiment{Title=title,Source=source,RevisionId=next.Revision,FileVersionId=captured.Id,StartedAt=captured.At,ReviewAt=captured.At.AddDays(7),Status="Needs review",Result="File changes were captured; review their effect and use explicit file restore if needed."});
        next.LastFileVersionId=captured.Id;
        ChangeEngine.Log(next,"configuration",$"Captured file version {captured.Id}.");
    }
    public async Task<ConfigFileVersion> CaptureFilesAsync(int expected,string reason,CancellationToken ct=default)
    {
        await mutation.WaitAsync(ct);
        try
        {
            ChangeEngine.CheckRevision(current,expected);var files=archive??throw new DomainException("File archive is unavailable.");var before=PublishedFiles(files,current);var captured=files.Capture(current.Revision,reason);
            var next=JsonDefaults.Clone(current);
            if(before!=null && !SameFiles(before,captured))TrackFileChange(next,captured,"You","Manual capture of changed configuration files");
            next.LastFileVersionId=captured.Id;db.Save(next);current=DataStore.StateSummary(next);
            return captured;
        }
        finally{mutation.Release();}
    }
    public async Task<ConfigFileVersion> RestoreFilesAsync(string id,int expected,string expectedVersion,Dictionary<string,string> expectedHashes,string notes,CancellationToken ct=default)
    {
        await mutation.WaitAsync(ct);
        try
        {
            ChangeEngine.CheckRevision(current,expected); if(current.Mode=="Monitor")throw new DomainException("Joule is set to Watch only, so it won't restore files. Choose Suggest changes or Automatic under How should AI help? first.");
            var files=archive??throw new DomainException("File archive is unavailable.");
            var attempted=false;
            try
            {
                attempted=true; var captured=files.Restore(id,expectedVersion,expectedHashes,current.Revision,notes,true);
                var next=JsonDefaults.Clone(current);next.LastFileVersionId=captured.Id;
                next.PendingFileReload=true;next.PendingFileReloadAt=DateTimeOffset.UtcNow;
                next.FileReloadNotes="Files were restored. Reload Predbat, collect refreshed settings and explicitly acknowledge the runtime check before tuning resumes.";
                foreach(var e in next.Experiments.Where(ChangeEngine.IsOpen)){e.Status="Needs review";e.Result="A manual file restore confounds this trial.";}
                next.Experiments.Add(new Experiment{Title="Manual configuration file restore",Source="You",RevisionId=next.Revision,FileVersionId=captured.Id,StartedAt=captured.At,ReviewAt=captured.At.AddDays(7),Result="Files restored. Predbat reload and runtime effects must be checked separately."});
                ChangeEngine.Log(next,"configuration",$"You restored file version {id} as new version {captured.Id}.");
                db.Save(next);files.CompleteRestore(captured.Id);current=DataStore.StateSummary(next);return captured;
            }
            catch(Exception ex)
            {
                if(attempted && (ex is not DomainException || files.Status().Quarantined))
                {
                    current.WriteUncertain=true;
                    try{files.Quarantine("File restore or state persistence failed.");}catch{}
                    try{db.Save(current);}catch{}
                }
                throw;
            }
        }
        finally{mutation.Release();}
    }
    public async Task<ConfigFileVersion> ReconcileFilesAsync(int expected,string notes,CancellationToken ct=default)
    {
        await mutation.WaitAsync(ct);
        try
        {
            if(current.Revision!=expected)throw new DomainException("Configuration changed. Refresh before reconciling.");
            var files=archive??throw new DomainException("File archive is unavailable.");
            try
            {
                var previousFiles=PublishedFiles(files,current);
                var captured=files.Reconcile(current.Revision,notes);
                var next=JsonDefaults.Clone(current);
                if(previousFiles!=null && !SameFiles(previousFiles,captured))TrackFileChange(next,captured,"You","Reconciled changed mounted configuration files");
                next.LastFileVersionId=captured.Id;
                next.PendingFileReload=true;next.PendingFileReloadAt=DateTimeOffset.UtcNow;
                next.FileReloadNotes="Mounted files were reconciled. Check runtime reload separately before tuning resumes.";
                ChangeEngine.Log(next,"configuration","You acknowledged mounted files after manual file reconciliation. Runtime reload and write reconciliation remain separate.");
                db.Save(next);current=DataStore.StateSummary(next);return captured;
            }
            catch(Exception ex)
            {
                if(ex is not DomainException){current.WriteUncertain=true;try{files.Quarantine("File reconciliation state persistence failed.");}catch{}try{db.Save(current);}catch{}}
                throw;
            }
        }
        finally{mutation.Release();}
    }
    public async Task AcknowledgeFileReloadAsync(int expected,string notes,CancellationToken ct=default)
    {
        await mutation.WaitAsync(ct);
        try
        {
            ChangeEngine.CheckRevision(current,expected);
            if(!current.PendingFileReload)throw new DomainException("No file reload is awaiting acknowledgement.");
            if(string.IsNullOrWhiteSpace(notes)||notes.Length>4000)throw new DomainException("Record your review of the refreshed runtime settings.",400);
            if(current.LastCollection==null || current.LastCollection<current.PendingFileReloadAt || current.CollectionError!=null || current.Settings.Count==0)throw new DomainException("Collect refreshed runtime settings after the file operation, then review them before acknowledging reload.");
            (archive??throw new DomainException("File archive is unavailable.")).VerifyCurrent(current.LastFileVersionId);
            var next=JsonDefaults.Clone(current);next.PendingFileReload=false;next.FileReloadNotes=notes;
            ChangeEngine.Log(next,"configuration","You acknowledged the refreshed runtime settings after a file operation. "+notes);
            db.Save(next);current=DataStore.StateSummary(next);
        }
        finally{mutation.Release();}
    }
    public async Task AutoApplyAsync(CancellationToken ct = default)
    {
        var snapshot = Read(false);
        var p = snapshot.Proposals.FirstOrDefault(x => ChangeEngine.CanAutoApply(snapshot, x));
        if (p == null) return;
        try { await MutateAsync(s => ChangeEngine.Approve(s, p.Id, false, true), ct); }
        catch (DomainException) { /* A concurrent user action changed eligibility. Next cycle rechecks it. */ }
    }
}
