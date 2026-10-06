using Joule;
using Xunit;
namespace Joule.Tests;

public class StateServiceTests : IDisposable
{
    readonly string path = Path.Combine(Path.GetTempPath(), "predbat-state-" + Guid.NewGuid().ToString("N"));
    class Writer : IPredbatClient
    {
        public bool Configured => true;
        public bool WritesEnabled => true;
        public Task<LiveSnapshot> CollectAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task ApplyAsync(List<Change> changes, List<Setting> settings, CancellationToken ct = default) => throw new LiveWriteException("Could not verify", true);
    }
    [Fact] public async Task FailedLiveWriteCannotCommitPermissionOrRevision()
    {
        using var db = new DataStore(path); var seed = DemoData.Create(); seed.DataSource = "Live"; db.Save(seed);
        var service = new StateService(db, new Writer(), false);
        await Assert.ThrowsAsync<LiveWriteException>(() => service.MutateAsync(s => ChangeEngine.Approve(s, s.Proposals[0].Id, true)));
        Assert.Equal(1, service.Read().Revision);
        Assert.All(service.Read().Settings, x => Assert.False(x.AutoAllowed));
        Assert.True(service.Read().WriteUncertain);
        Assert.True(db.HasUncertainWrite());
    }
    [Fact] public async Task ConcurrentStaleEditsOnlyCommitOneRevision()
    {
        using var db = new DataStore(path); var service = new StateService(db, new Writer(), true);
        await service.MutateAsync(s => ChangeEngine.Edit(s, "load_scaling", "1.00", 1, "first"));
        await Assert.ThrowsAsync<DomainException>(() => service.MutateAsync(s => ChangeEngine.Edit(s, "pv_scaling", "0.90", 1, "stale")));
        Assert.Equal(2, service.Read().Revision);
    }
    sealed class SuccessfulWriter : IPredbatClient
    {
        public List<Setting> Settings { get; set; } = [];
        public int Writes { get; private set; }
        public bool Configured => true;
        public bool WritesEnabled => true;
        public Task<LiveSnapshot> CollectAsync(CancellationToken ct = default) => Task.FromResult(new LiveSnapshot(JsonDefaults.Clone(Settings), null, "{}", "{}"));
        public Task ApplyAsync(List<Change> changes, List<Setting> settings, CancellationToken ct = default)
        { Writes++; foreach (var c in changes) Settings.Single(x => x.Key == c.Key).Value = c.After; return Task.CompletedTask; }
    }
    [Fact] public async Task CalculationActivityUpdatesWithoutInvalidatingConfigurationOrExperiments()
    {
        using var db = new DataStore(path);
        var seed = LiveSeed();
        seed.Settings.Add(new Setting { Key = "active", Type = "boolean", Value = "off", Editable = true, AutoAllowed = true });
        seed.Experiments.Add(new Experiment { Title = "Open trial", Status = "Running", RevisionId = seed.Revision });
        db.Save(seed);
        var writer = new SuccessfulWriter { Settings = JsonDefaults.Clone(seed.Settings) };
        writer.Settings.Single(x => x.Key == "active").Value = "on";
        writer.Settings.Single(x => x.Key == "active").Editable = false;
        var service = new StateService(db, writer, false);
        Assert.False(service.Read(false).Settings.Single(x => x.Key == "active").Editable);
        Assert.False(service.Read(false).Settings.Single(x => x.Key == "active").AutoAllowed);
        Assert.Contains("between calculations", service.Read(false).Settings.Single(x => x.Key == "active").Description);
        await service.CollectAsync();
        var refreshed = service.Read();
        Assert.Equal(seed.Revision, refreshed.Revision);
        Assert.Equal("Running", refreshed.Experiments.Single().Status);
        Assert.Equal("on", refreshed.Settings.Single(x => x.Key == "active").Value);
        Assert.False(refreshed.Settings.Single(x => x.Key == "active").AutoAllowed);
        writer.Settings.RemoveAll(x => x.Key == "active");
        await service.CollectAsync();
        Assert.Equal(seed.Revision, service.Read().Revision);
        writer.Settings.Single(x => x.Key == "load_scaling").Value = "1.23";
        // A real setting becoming unavailable/noneditable must still invalidate
        // previous assumptions; diagnostic classification is deliberately narrow.
        writer.Settings.Single(x => x.Key == "load_scaling").Editable = false;
        await service.CollectAsync();
        Assert.Equal(seed.Revision + 1, service.Read().Revision);
        Assert.Equal("Needs review", service.Read().Experiments.First().Status);
    }
    [Fact] public void RestoringLegacyRevisionIgnoresCalculationActivityButRestoresActualSettings()
    {
        var state = DemoData.Create(); state.Mode = "Recommend";
        state.Settings.Add(new Setting { Key = "active", Value = "on", Type = "boolean", Editable = true });
        state.Revisions[0].Values["active"] = "off";
        var original = state.Settings.Single(x => x.Key == "load_scaling").Value;
        state.Settings.Single(x => x.Key == "load_scaling").Value = "1.23";
        ChangeEngine.Restore(state, state.Revisions[0].Id, state.Revision);
        Assert.Equal(original, state.Settings.Single(x => x.Key == "load_scaling").Value);
        Assert.Equal("on", state.Settings.Single(x => x.Key == "active").Value);
        Assert.DoesNotContain("active", state.Revisions.Last().Values.Keys);
        Assert.Throws<DomainException>(() => ChangeEngine.Edit(state, "active", "off", state.Revision, "not a control"));
    }
    sealed class PausedWriter : IPredbatClient
    {
        public bool Configured=>true;
        public bool WritesEnabled=>true;
        public TaskCompletionSource Entered { get; }=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Complete { get; }=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public DateTimeOffset VerifiedAt { get; private set; }
        public Task<LiveSnapshot> CollectAsync(CancellationToken ct=default)=>throw new NotImplementedException();
        public async Task ApplyAsync(List<Change> changes,List<Setting> settings,CancellationToken ct=default)
        {
            Entered.SetResult();await Complete.Task.WaitAsync(ct);VerifiedAt=DateTimeOffset.UtcNow;
        }
    }
    [Fact] public async Task PlanGeneratedWhileLiveWriteIsInFlightCannotRepresentVerifiedConfiguration()
    {
        using var db=new DataStore(path);db.Save(LiveSeed());var writer=new PausedWriter();var state=new StateService(db,writer,false);
        var applying=state.MutateAsync(s=>ChangeEngine.Approve(s,s.Proposals[0].Id,false));
        await writer.Entered.Task;
        var duringWrite=new PlanSnapshot{At=DateTimeOffset.UtcNow,Source="Predbat",Slots=[new(DateTimeOffset.UtcNow.AddHours(1),1,null,0,null,50,null,30,10,"Self-use",.3)]};
        writer.Complete.SetResult();await applying;
        var snapshot=state.Read();
        var nextProposal=new Proposal{BaseRevision=snapshot.Revision,Changes=[new("load_scaling","1.00","1.01")]};
        Assert.False(ImpactPreviewService.Build(snapshot,nextProposal,duringWrite,DateTimeOffset.UtcNow).Available);
        var trial=Assert.Single(snapshot.Experiments);
        Assert.True(trial.StartedAt>=writer.VerifiedAt);
        Assert.Equal(snapshot.Revisions.Last().At,trial.StartedAt);
        Assert.Equal(TimeSpan.FromDays(snapshot.Proposals[0].ReviewDays),trial.ReviewAt-trial.StartedAt);
    }
    sealed class FaultingStore(string path) : DataStore(path)
    {
        public bool FailSave { get; set; }
        public bool FailEnd { get; set; }
        public bool FailReconcile { get; set; }
        public Action? OnReconcile { get; set; }
        public override void Save(AppState state) { if (FailSave) throw new IOException("fixture persistence failure"); base.Save(state); }
        public override void EndWrite(string id, string status) { if (FailEnd) throw new IOException("fixture journal failure"); base.EndWrite(id, status); }
        public override void ReconcileWrites() { OnReconcile?.Invoke(); if (FailReconcile) throw new IOException("fixture reconcile failure"); base.ReconcileWrites(); }
    }
    static AppState LiveSeed() { var s = DemoData.Create(); s.DataSource = "Live"; return s; }
    [Fact] public async Task SuccessfulRemoteWriteThenFailedSaveQuarantinesRunningProcess()
    {
        using var db = new FaultingStore(path); var seed = LiveSeed(); db.Save(seed);
        var writer = new SuccessfulWriter { Settings = JsonDefaults.Clone(seed.Settings) };
        var service = new StateService(db, writer, false); db.FailSave = true;
        await Assert.ThrowsAsync<IOException>(() => service.MutateAsync(s => ChangeEngine.Edit(s, "load_scaling", "1.00", s.Revision, "test")));
        Assert.True(service.Read().WriteUncertain); Assert.True(db.HasUncertainWrite()); Assert.Equal(1, writer.Writes);
        await Assert.ThrowsAsync<DomainException>(() => service.MutateAsync(s => ChangeEngine.Edit(s, "pv_scaling", "0.90", s.Revision, "must block")));
        Assert.Equal(1, writer.Writes);
    }
    [Fact] public async Task SuccessfulRemoteWriteThenFailedJournalQuarantinesRunningProcess()
    {
        using var db = new FaultingStore(path); var seed = LiveSeed(); db.Save(seed);
        var writer = new SuccessfulWriter { Settings = JsonDefaults.Clone(seed.Settings) };
        var service = new StateService(db, writer, false); db.FailEnd = true;
        await Assert.ThrowsAsync<IOException>(() => service.MutateAsync(s => ChangeEngine.Edit(s, "load_scaling", "1.00", s.Revision, "test")));
        Assert.True(service.Read().WriteUncertain); Assert.True(db.HasUncertainWrite());
        await Assert.ThrowsAsync<DomainException>(() => service.MutateAsync(s => ChangeEngine.Edit(s, "pv_scaling", "0.90", s.Revision, "must block")));
        Assert.Equal(1, writer.Writes);
    }
    [Fact] public async Task FailedWriteAndFailedJournalStillQuarantinesMemory()
    {
        using var db = new FaultingStore(path); var seed = LiveSeed(); db.Save(seed);
        var service = new StateService(db, new Writer(), false); db.FailEnd = true;
        await Assert.ThrowsAsync<LiveWriteException>(() => service.MutateAsync(s => ChangeEngine.Edit(s, "load_scaling", "1.00", s.Revision, "test")));
        Assert.True(service.Read().WriteUncertain);
    }
    [Fact] public async Task ReconciliationKeepsNewWritesOutsideJournalAcknowledgement()
    {
        using var db = new FaultingStore(path); var seed = LiveSeed(); seed.WriteUncertain = true; db.Save(seed);
        db.BeginWrite([new("load_scaling", "1.05", "1.00")]);
        var writer = new SuccessfulWriter { Settings = JsonDefaults.Clone(seed.Settings) };
        var service = new StateService(db, writer, false);
        Task? nextWrite = null;
        db.OnReconcile = () =>
        {
            nextWrite = service.MutateAsync(s => ChangeEngine.Edit(s, "load_scaling", "1.00", s.Revision, "after reconciliation"));
            Assert.False(nextWrite.IsCompleted); Assert.Equal(0, writer.Writes);
            db.FailEnd = true;
        };
        await service.ReconcileAsync(default);
        Assert.NotNull(nextWrite); await Assert.ThrowsAsync<IOException>(() => nextWrite!);
        Assert.True(db.HasUncertainWrite()); Assert.True(service.Read().WriteUncertain);
    }
    [Fact] public async Task FailedJournalReconciliationDoesNotClearQuarantine()
    {
        using var db = new FaultingStore(path); var seed = LiveSeed(); seed.WriteUncertain = true; db.Save(seed);
        db.BeginWrite([new("load_scaling", "1.05", "1.00")]);
        var service = new StateService(db, new SuccessfulWriter { Settings = JsonDefaults.Clone(seed.Settings) }, false);
        db.FailReconcile = true;
        await Assert.ThrowsAsync<IOException>(() => service.ReconcileAsync(default));
        Assert.True(service.Read().WriteUncertain); Assert.True(db.HasUncertainWrite());
    }
    public void Dispose() { if (Directory.Exists(path)) Directory.Delete(path, true); }
}
