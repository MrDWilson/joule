using Joule;
using Xunit;
namespace Joule.Tests;
public class ControlFileIntegrationTests:IDisposable
{
    readonly string path=Path.Combine(OperatingSystem.IsMacOS()?"/private/tmp":Path.GetTempPath(),"predbat-file-state-"+Guid.NewGuid().ToString("N"));
    sealed class FaultStore(string path):DataStore(path){public bool FailSave;public override void Save(AppState state){if(FailSave)throw new IOException("fixture disk failure");base.Save(state);}}
    sealed class ConcurrentFileWriter(List<Setting> settings,string file):IPredbatClient
    {
        public bool Configured=>true;
        public bool WritesEnabled=>true;
        public Task<LiveSnapshot> CollectAsync(CancellationToken ct=default)=>Task.FromResult(new LiveSnapshot(JsonDefaults.Clone(settings),null,"{}","{}"));
        public Task ApplyAsync(List<Change> changes,List<Setting> expected,CancellationToken ct=default)
        {
            foreach(var change in changes)settings.Single(x=>x.Key==change.Key).Value=change.After;
            File.WriteAllText(file,"{\"unrelated_external_configuration\":2}");
            return Task.CompletedTask;
        }
    }
    ConfigFileArchive Files(bool mirror=false)=>new(new(){Root=Path.Combine(path,"config"),ArchiveDirectory=Path.Combine(path,"archive"),AllowedFiles=["runtime-settings.json"],DemoRuntimeMirror=mirror});
    [Fact] public async Task OwnedDemoRuntimeCreatesLinkedBeforeAndAfterFileVersions()
    {
        using var db=new DataStore(Path.Combine(path,"db"));var files=Files(true);var state=new StateService(db,null!,true,files);
        await state.MutateAsync(s=>ChangeEngine.Edit(s,"load_scaling","1.00",s.Revision,"demo trial"));
        var revision=state.Read().Revisions.Last();Assert.NotNull(revision.FileVersionBefore);Assert.NotNull(revision.FileVersionAfter);
        Assert.NotEqual(files.List().First().Files[0].Hash,files.List().Last().Files[0].Hash);
        Assert.DoesNotContain("1.00",files.View(revision.FileVersionAfter!,"runtime-settings.json").Text);
    }
    [Fact] public async Task RestoreStatePersistenceFailureQuarantinesAcrossRestart()
    {
        using var db=new FaultStore(Path.Combine(path,"db"));var files=Files(true);var state=new StateService(db,null!,true,files);var first=files.List().First();
        await state.MutateAsync(s=>ChangeEngine.Edit(s,"load_scaling","1.00",s.Revision,"demo trial"));var latest=files.List().Last();db.FailSave=true;
        await Assert.ThrowsAsync<IOException>(()=>state.RestoreFilesAsync(first.Id,state.Read().Revision,latest.Id,latest.Files.ToDictionary(x=>x.Path,x=>x.Hash),"restore"));
        Assert.True(state.Read().WriteUncertain);Assert.True(Files(true).Status().Quarantined);
        await Assert.ThrowsAsync<DomainException>(()=>state.MutateAsync(s=>ChangeEngine.Edit(s,"load_scaling","1.02",s.Revision,"must block")));
    }
    [Fact] public async Task ExternalFileChangesAreCapturedAndTrackedOnCollection()
    {
        using var db=new DataStore(Path.Combine(path,"db"));var files=Files(true);var state=new StateService(db,null!,true,files);
        File.WriteAllText(Path.Combine(path,"config/runtime-settings.json"),"{\"load_scaling\":\"2\"}");
        await state.CollectAsync();var e=Assert.Single(state.Read().Experiments);Assert.Equal("External files",e.Source);Assert.NotNull(e.FileVersionId);
        await Assert.ThrowsAsync<DomainException>(()=>state.MutateAsync(s=>ChangeEngine.Decide(s,e.Id,"revert","",0,s.Revision)));
    }
    [Fact] public void DeferredArchiveCompletionBlocksRestartUntilStateIsDurable()
    {
        var files=Files(true);var s=DemoData.Create();files.MirrorDemoRuntime(s);var first=files.Capture(1,"first");ChangeEngine.Edit(s,"load_scaling","1",1,"next");files.MirrorDemoRuntime(s);var second=files.Capture(2,"next");
        var restored=files.Restore(first.Id,second.Id,second.Files.ToDictionary(x=>x.Path,x=>x.Hash),2,"restore",true);
        Assert.True(Files(true).Status().Quarantined);files.CompleteRestore(restored.Id);Assert.False(files.Status().Quarantined);
    }
    [Fact] public async Task FileReloadAcknowledgementRequiresRefreshedRuntimeAndCurrentHashes()
    {
        using var db=new DataStore(Path.Combine(path,"db"));var files=Files(true);var state=new StateService(db,null!,true,files);var first=files.List().First();
        await state.MutateAsync(s=>ChangeEngine.Edit(s,"load_scaling","1",s.Revision,"trial"));var latest=files.List().Last();
        await state.RestoreFilesAsync(first.Id,state.Read().Revision,latest.Id,latest.Files.ToDictionary(x=>x.Path,x=>x.Hash),"file-only restoration");
        Assert.True(state.Read().PendingFileReload);
        await Assert.ThrowsAsync<DomainException>(()=>state.MutateAsync(s=>ChangeEngine.Edit(s,"load_scaling","1.02",s.Revision,"blocked until review")));
        await Assert.ThrowsAsync<DomainException>(()=>state.AcknowledgeFileReloadAsync(state.Read().Revision,"reviewed"));
        await state.CollectAsync();Assert.True(state.Read().PendingFileReload);
        File.WriteAllText(Path.Combine(path,"config/runtime-settings.json"),"{\"load_scaling\":\"1.03\"}");
        await Assert.ThrowsAsync<DomainException>(()=>state.AcknowledgeFileReloadAsync(state.Read().Revision,"reviewed"));
        await state.CollectAsync();await state.AcknowledgeFileReloadAsync(state.Read().Revision,"Checked refreshed values and file-only state");
        Assert.False(state.Read().PendingFileReload);Assert.Contains("Checked refreshed",state.Read().FileReloadNotes);
    }
    [Fact] public async Task FailedExternalCollectionPublicationDoesNotLoseFileChangeOnRetry()
    {
        using var db=new FaultStore(Path.Combine(path,"db"));var files=Files(true);var state=new StateService(db,null!,true,files);
        File.WriteAllText(Path.Combine(path,"config/runtime-settings.json"),"{\"load_scaling\":\"1.03\"}");db.FailSave=true;
        await Assert.ThrowsAsync<IOException>(()=>state.CollectAsync());Assert.Empty(state.Read().Experiments);
        db.FailSave=false;await state.CollectAsync();Assert.Single(state.Read().Experiments);
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task FailedManualCapturePublicationDoesNotLoseFileTrialOnRetry(bool restart)
    {
        using var db=new FaultStore(Path.Combine(path,"db"));var files=Files(true);var state=new StateService(db,null!,true,files);
        File.WriteAllText(Path.Combine(path,"config/runtime-settings.json"),"{\"load_scaling\":\"1.03\"}");db.FailSave=true;
        await Assert.ThrowsAsync<IOException>(()=>state.CaptureFilesAsync(state.Read().Revision,"capture change"));
        Assert.Empty(state.Read().Experiments);db.FailSave=false;
        if(restart)state=new StateService(db,null!,true,Files(true));
        await state.CaptureFilesAsync(state.Read().Revision,"retry capture");await state.CollectAsync();
        var trial=Assert.Single(state.Read().Experiments);Assert.NotNull(trial.FileVersionId);
        await state.MutateAsync(s=>{ChangeEngine.SetMode(s,"Auto");ChangeEngine.Permission(s,"load_scaling",true);});
        Assert.False(ChangeEngine.CanAutoApply(state.Read(),state.Read().Proposals[0]));
    }
    [Fact] public async Task FailedRedundantCaptureDoesNotInventFileChangeBeforeRuntimeEdit()
    {
        using var db=new FaultStore(Path.Combine(path,"db"));var files=Files(true);var state=new StateService(db,null!,true,files);
        db.FailSave=true;await Assert.ThrowsAsync<IOException>(()=>state.CaptureFilesAsync(state.Read().Revision,"redundant snapshot"));db.FailSave=false;
        await state.MutateAsync(s=>ChangeEngine.Edit(s,"load_scaling","1.00",s.Revision,"runtime trial"));
        var trial=Assert.Single(state.Read().Experiments);Assert.Null(trial.FileVersionId);
    }
    [Fact] public async Task FailedChangedCaptureStillBlocksAutomaticRuntimeApplication()
    {
        using var db=new FaultStore(Path.Combine(path,"db"));var files=Files(true);var state=new StateService(db,null!,true,files);
        await state.MutateAsync(s=>{ChangeEngine.SetMode(s,"Auto");ChangeEngine.Permission(s,"load_scaling",true);});
        File.WriteAllText(Path.Combine(path,"config/runtime-settings.json"),"{\"load_scaling\":\"1.03\"}");
        db.FailSave=true;await Assert.ThrowsAsync<IOException>(()=>state.CaptureFilesAsync(state.Read().Revision,"changed capture"));db.FailSave=false;
        await state.AutoApplyAsync();
        Assert.Equal(1,state.Read().Revision);Assert.NotNull(Assert.Single(state.Read().Experiments).FileVersionId);
        Assert.Equal("Pending",state.Read().Proposals[0].Status);
    }
    [Fact] public async Task MountedFileChangeDuringRuntimeWriteRemainsARecordedConfounder()
    {
        using var db=new DataStore(Path.Combine(path,"db"));
        Directory.CreateDirectory(Path.Combine(path,"config"));
        var file=Path.Combine(path,"config/runtime-settings.json");File.WriteAllText(file,"{\"unrelated_external_configuration\":1}");
        var seed=DemoData.Create();seed.DataSource="Live";db.Save(seed);
        var files=Files();var writer=new ConcurrentFileWriter(JsonDefaults.Clone(seed.Settings),file);
        var state=new StateService(db,writer,false,files);await state.CollectAsync();
        await state.MutateAsync(s=>{ChangeEngine.SetMode(s,"Auto");ChangeEngine.Permission(s,"load_scaling",true);});
        await state.AutoApplyAsync();
        var snapshot=state.Read();
        Assert.Equal("Applied",snapshot.Proposals[0].Status);
        var fileTrial=Assert.Single(snapshot.Experiments,e=>e.FileVersionId!=null);
        var runtimeTrial=Assert.Single(snapshot.Experiments,e=>e.FileVersionId==null);
        Assert.Equal("Needs review",runtimeTrial.Status);
        Assert.Equal(snapshot.Revisions.Last().FileVersionAfter,fileTrial.FileVersionId);
        Assert.False(runtimeTrial.AutomaticRevertEligible);
        await state.MutateAsync(s=>ChangeEngine.Decide(s,fileTrial.Id,"close","Reviewed external file change",0,s.Revision));
        await state.CollectAsync();
        snapshot=state.Read();Assert.Single(snapshot.Experiments,e=>e.FileVersionId!=null);
        Assert.False(snapshot.Experiments.Single(e=>e.Id==runtimeTrial.Id).AutomaticRevertEligible);
    }
    [Theory][InlineData(false)][InlineData(true)]
    public async Task ChangedFileReconciliationTracksConfounderAfterPublicationFailure(bool failPublication)
    {
        using var db=new FaultStore(Path.Combine(path,"db"));var files=Files(true);var state=new StateService(db,null!,true,files);
        await state.MutateAsync(s=>{ChangeEngine.SetMode(s,"Auto");ChangeEngine.Permission(s,"load_scaling",true);ChangeEngine.Approve(s,s.Proposals[0].Id,false,true);});
        File.WriteAllText(Path.Combine(path,"config/runtime-settings.json"),"{\"load_scaling\":\"1.50\"}");
        if(failPublication)
        {
            db.FailSave=true;await Assert.ThrowsAsync<IOException>(()=>state.ReconcileFilesAsync(state.Read().Revision,"Reviewed changed mounted files"));db.FailSave=false;
            state=new StateService(db,null!,true,Files(true));files=Files(true);
        }
        await state.ReconcileFilesAsync(state.Read().Revision,"Reviewed changed mounted files");
        if(state.Read().WriteUncertain)await state.ReconcileAsync(default);
        else await state.CollectAsync();
        await state.AcknowledgeFileReloadAsync(state.Read().Revision,"Reviewed refreshed runtime after file reconciliation");
        var snapshot=state.Read();var fileTrial=Assert.Single(snapshot.Experiments,e=>e.FileVersionId!=null);
        Assert.Equal("Needs review",fileTrial.Status);Assert.False(snapshot.Experiments.First(e=>e.FileVersionId==null).AutomaticRevertEligible);
        await state.CollectAsync();Assert.Single(state.Read().Experiments,e=>e.FileVersionId!=null);
    }
    public void Dispose(){if(Directory.Exists(path))Directory.Delete(path,true);}
}
