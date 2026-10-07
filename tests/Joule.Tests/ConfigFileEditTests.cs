using System.Text;
using Joule;
using Microsoft.Extensions.Configuration;
using Xunit;
namespace Joule.Tests;

/// <summary>"Apply for me": review, apply with a snapshot, Predbat's reload check, automatic and manual restore, the timeline.</summary>
public class ConfigFileEditTests : IDisposable
{
    readonly string path = Path.Combine(OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath(), "joule-edits-" + Guid.NewGuid().ToString("N"));
    string Config => Path.Combine(path, "config");
    string AppsPath => Path.Combine(Config, "apps.yaml");
    const string LiveApps = "pred_bat:\n  # Sensors\n  import_today:\n    - sensor.import_today\n  octopus_api_key: !secret octopus_api_key\n  ha_key: eyJhbGciOiJIUzI1NiJ9.literal-token-value-1234567890\n  battery_rate_max_scaling: 1.0\n";

    sealed class FakeWatcher(ReloadOutcome outcome) : IPredbatReloadWatcher
    {
        public Action? During;
        public Task<ReloadOutcome> WatchAsync(DateTimeOffset writtenAt, CancellationToken ct) { During?.Invoke(); return Task.FromResult(outcome); }
    }

    static IConfiguration Settings(bool allow) => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["ConfigFiles:AllowEdits"] = allow ? "true" : "false" }).Build();

    (StateService State, ConfigFileArchive Files, ConfigFileEditService Edits, DataStore Db) Live(ReloadOutcome outcome, bool allow = true, FakeWatcher? watcher = null)
    {
        Directory.CreateDirectory(Config);
        if (!File.Exists(AppsPath)) File.WriteAllText(AppsPath, LiveApps);
        var files = new ConfigFileArchive(new() { Root = Config, ArchiveDirectory = Path.Combine(path, "archive"), AllowedFiles = ["apps.yaml"] });
        var db = new DataStore(Path.Combine(path, "db"));
        var state = new StateService(db, null!, false, files, Settings(allow));
        return (state, files, new ConfigFileEditService(files, state, Settings(allow), watcher ?? new FakeWatcher(outcome)), db);
    }

    static async Task<(string Investigation, string Change)> Suggest(StateService state, string location, string snippet, string? before = null)
    {
        var change = new ConfigFileChange { Id = Guid.NewGuid().ToString("N"), File = "apps.yaml", Summary = "Add export_today", Location = location, Snippet = snippet, Before = before, Reason = "Predbat has no export history." };
        var investigation = new Investigation { Title = "No export sensor", Summary = "s", Status = "Completed", FileChanges = [change] };
        await state.MutateAsync(s => s.Investigations.Add(investigation));
        return (investigation.Id, change.Id);
    }

    static ConfigFileChange Change(StateService state, (string Investigation, string Change) ids) =>
        state.Read(false).Investigations.Single(i => i.Id == ids.Investigation).FileChanges.Single(c => c.Id == ids.Change);

    [Fact]
    public async Task ReviewShowsAMaskedDiffWithSecretReferencesButNoSecretValues()
    {
        var (state, _, edits, db) = Live(new(true, true, "ok"));
        using var _ = db;
        var ids = await Suggest(state, "pred_bat, after import_today", "  export_today:\n    - sensor.export_today");
        var review = edits.Review(ids.Investigation, ids.Change);
        Assert.True(review.CanApply, review.Reason);
        Assert.Null(review.Problem);
        Assert.Equal("added", review.Keys.Single().Change);
        Assert.Contains(review.Lines, l => l.Kind == "+" && l.Text == "  export_today:");
        Assert.Contains(review.Lines, l => l.Kind == " " && l.Text == "  octopus_api_key: !secret octopus_api_key");
        Assert.Contains(review.Lines, l => l.Kind == " " && l.Text == "  # Sensors");
        Assert.DoesNotContain(review.Lines, l => l.Text.Contains("eyJ") || l.Text.Contains("literal-token"));
        Assert.Equal(LiveApps, File.ReadAllText(AppsPath)); // Reviewing writes nothing.
    }

    [Fact]
    public async Task ApplySnapshotsWritesConfirmsAndRestoreBringsTheExactFileBack()
    {
        var (state, files, edits, db) = Live(new(true, true, "Predbat reloaded apps.yaml and logged no errors."));
        using var _ = db;
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(AppsPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
        var ids = await Suggest(state, "pred_bat, after import_today", "  export_today:\n    - sensor.export_today");
        var review = edits.Review(ids.Investigation, ids.Change);
        var edit = await edits.ApplyAsync(ids.Investigation, ids.Change, review.Hash);
        await edits.Watching;

        Assert.Equal("pred_bat:\n  # Sensors\n  import_today:\n    - sensor.import_today\n  export_today:\n    - sensor.export_today\n  octopus_api_key: !secret octopus_api_key\n  ha_key: eyJhbGciOiJIUzI1NiJ9.literal-token-value-1234567890\n  battery_rate_max_scaling: 1.0\n", File.ReadAllText(AppsPath));
        if (!OperatingSystem.IsWindows()) Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead, File.GetUnixFileMode(AppsPath));
        Assert.Equal(Encoding.UTF8.GetBytes(LiveApps), files.ReadVersionFile(edit.SnapshotVersion, "apps.yaml"));
        var change = Change(state, ids);
        Assert.Equal("applied", change.Status);
        Assert.Equal("confirmed", change.Edit!.Check);
        Assert.Equal("Predbat reloaded apps.yaml and logged no errors.", change.Edit.CheckNote);
        var s = state.Read(false);
        Assert.Equal(edit.AppliedVersion, s.LastFileVersionId);
        var ev = s.SettingEvents.Single(e => e.Kind == SettingKind.File);
        Assert.Equal("Joule edited apps.yaml: Add export_today", ev.Title);
        Assert.Equal("pred_bat › export_today", ev.After);

        // The next collection sees no "external" change: the archive already holds what Joule wrote.
        await state.FileOperationAsync(_ => { });
        Assert.DoesNotContain(state.Read(false).Experiments, e => e.Source == "External files");

        await edits.RestoreAsync(ids.Investigation, ids.Change);
        Assert.Equal(LiveApps, File.ReadAllText(AppsPath));
        change = Change(state, ids);
        Assert.Equal(("pending", "restored"), (change.Status, change.Edit!.Check));
        s = state.Read(false);
        Assert.NotNull(s.SettingEvents.Single(e => e.Id == edit.EventId).RevertedAt);
        Assert.Contains(s.SettingEvents, e => e.Kind == SettingKind.File && e.Title.StartsWith("You put apps.yaml back", StringComparison.Ordinal));
        await Assert.ThrowsAsync<DomainException>(() => edits.RestoreAsync(ids.Investigation, ids.Change));
    }

    [Fact]
    public async Task APredbatProblemPutsTheFileBackAutomatically()
    {
        var (state, _, edits, db) = Live(new(false, true, "Predbat reported a problem after reloading: “Error: apps.yaml invalid”"));
        using var _ = db;
        var ids = await Suggest(state, "pred_bat", "  battery_rate_max_scaling: 0.7", "  battery_rate_max_scaling: 1.0");
        await edits.ApplyAsync(ids.Investigation, ids.Change, edits.Review(ids.Investigation, ids.Change).Hash);
        await edits.Watching;
        Assert.Equal(LiveApps, File.ReadAllText(AppsPath));
        var change = Change(state, ids);
        Assert.Equal(("pending", "rolled_back"), (change.Status, change.Edit!.Check));
        Assert.StartsWith("Predbat reported a problem", change.Edit.CheckNote);
        Assert.EndsWith("Joule put the previous apps.yaml back.", change.Edit.CheckNote);
        Assert.Contains(state.Read(false).SettingEvents, e => e.Title.StartsWith("Joule put apps.yaml back", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AFileChangedAgainIsNotOverwrittenByTheAutomaticRestore()
    {
        var watcher = new FakeWatcher(new(false, true, "Predbat stopped to reload apps.yaml and hadn't started again 3 minutes later."));
        var (state, _, edits, db) = Live(default!, watcher: watcher);
        using var _ = db;
        watcher.During = () => File.AppendAllText(AppsPath, "  load_today:\n    - sensor.load\n");
        var ids = await Suggest(state, "pred_bat", "  battery_rate_max_scaling: 0.7", "  battery_rate_max_scaling: 1.0");
        await edits.ApplyAsync(ids.Investigation, ids.Change, edits.Review(ids.Investigation, ids.Change).Hash);
        await edits.Watching;
        Assert.Contains("load_today", File.ReadAllText(AppsPath));
        Assert.Contains("battery_rate_max_scaling: 0.7", File.ReadAllText(AppsPath));
        var change = Change(state, ids);
        Assert.Equal(("applied", "attention"), (change.Status, change.Edit!.Check));
        Assert.Contains("changed again", change.Edit.CheckNote);
        // The manual restore refuses too, and points to Files.
        var refused = await Assert.ThrowsAsync<DomainException>(() => edits.RestoreAsync(ids.Investigation, ids.Change));
        Assert.Contains("Files", refused.Message);
    }

    [Fact]
    public async Task NothingIsWrittenWhenTheFileChangedSinceTheReviewOrEditingIsOff()
    {
        var (state, _, edits, db) = Live(new(true, true, "ok"));
        using var _ = db;
        var ids = await Suggest(state, "pred_bat", "  battery_rate_max_scaling: 0.7", "  battery_rate_max_scaling: 1.0");
        var hash = edits.Review(ids.Investigation, ids.Change).Hash;
        File.WriteAllText(AppsPath, LiveApps + "  pv_today: x\n");
        var stale = await Assert.ThrowsAsync<DomainException>(() => edits.ApplyAsync(ids.Investigation, ids.Change, hash));
        Assert.Contains("changed since you reviewed", stale.Message);
        Assert.Equal(LiveApps + "  pv_today: x\n", File.ReadAllText(AppsPath));
        Assert.Equal("pending", Change(state, ids).Status);
    }

    [Fact]
    public async Task EditingNeedsThePermissionSwitchedOn()
    {
        var (state, _, edits, db) = Live(new(true, true, "ok"), allow: false);
        using var _ = db;
        Assert.False(edits.Status().CanApply);
        Assert.Contains("switched off in Setup", edits.Status().Reason);
        var ids = await Suggest(state, "pred_bat", "  battery_rate_max_scaling: 0.7", "  battery_rate_max_scaling: 1.0");
        var review = edits.Review(ids.Investigation, ids.Change);
        Assert.False(review.CanApply);
        Assert.NotEmpty(review.Lines); // The diff is still shown for copying by hand.
        await Assert.ThrowsAsync<DomainException>(() => edits.ApplyAsync(ids.Investigation, ids.Change, review.Hash));
        Assert.Equal(LiveApps, File.ReadAllText(AppsPath));
    }

    [Fact]
    public async Task ARefusedEditExplainsWhyAndCannotBeApplied()
    {
        var (state, _, edits, db) = Live(new(true, true, "ok"));
        using var _ = db;
        var ids = await Suggest(state, "pred_bat", "  battery_rate_max_scaling: 0.7", "  battery_rate_max_scaling: 0.9");
        var review = edits.Review(ids.Investigation, ids.Change);
        Assert.False(review.CanApply);
        Assert.Contains("aren't in apps.yaml exactly", review.Problem);
        await Assert.ThrowsAsync<YamlEditRefused>(() => edits.ApplyAsync(ids.Investigation, ids.Change, review.Hash));
    }

    [Fact]
    public void NotConfiguredExplainsTheMount()
    {
        var files = new ConfigFileArchive(new() { ArchiveDirectory = Path.Combine(path, "archive") });
        using var db = new DataStore(Path.Combine(path, "db"));
        var state = new StateService(db, null!, false, files, Settings(true));
        var status = new ConfigFileEditService(files, state, Settings(true), new FakeWatcher(new(true, true, ""))).Status();
        Assert.False(status.Configured);
        Assert.Contains("Mount Predbat's config folder", status.Reason);
    }

    [Fact]
    public async Task DemoAppliesToItsOwnSampleFileAndResetPutsItBack()
    {
        using var db = new DataStore(Path.Combine(path, "db"));
        var files = new ConfigFileArchive(new() { Root = Config, ArchiveDirectory = Path.Combine(path, "archive"), AllowedFiles = ["runtime-settings.json", "apps.yaml"], DemoRuntimeMirror = true });
        var state = new StateService(db, null!, true, files);
        var edits = new ConfigFileEditService(files, state, new ConfigurationBuilder().Build(), new DemoReloadWatcher());
        Assert.True(edits.Status().CanApply);
        Assert.Equal(DemoData.AppsYaml, File.ReadAllText(AppsPath));
        var investigation = state.Read(false).Investigations.Single(i => i.FileChanges.Count > 0);
        var change = investigation.FileChanges.Single();
        var review = edits.Review(investigation.Id, change.Id);
        Assert.True(review.CanApply, review.Problem ?? review.Reason);
        await edits.ApplyAsync(investigation.Id, change.Id, review.Hash);
        await edits.Watching;
        Assert.Contains("  import_today:\n    - sensor.demo_inverter_import_today\n  export_today:\n    - sensor.demo_inverter_export_today\n  pv_today:", File.ReadAllText(AppsPath));
        Assert.Equal("confirmed", Change(state, (investigation.Id, change.Id)).Edit!.Check);
        await state.ResetDemoAsync();
        Assert.Equal(DemoData.AppsYaml, File.ReadAllText(AppsPath));
    }

    [Fact]
    public void ReplaceFileInPlaceKeepsTheSameFileAndAFailedWriteLeavesTheArchiveUsable()
    {
        Directory.CreateDirectory(Config); File.WriteAllText(AppsPath, LiveApps);
        var files = new ConfigFileArchive(new() { Root = Config, ArchiveDirectory = Path.Combine(path, "archive"), AllowedFiles = ["apps.yaml"] }) { ForceInPlaceWrite = true };
        var (_, hash) = files.ReadText("apps.yaml");
        var result = files.ReplaceFile("apps.yaml", Encoding.UTF8.GetBytes(LiveApps + "  pv_today: x\n"), hash, 1, "test", "before test");
        Assert.Equal(LiveApps + "  pv_today: x\n", File.ReadAllText(AppsPath));
        Assert.Equal(Encoding.UTF8.GetBytes(LiveApps), files.ReadVersionFile(result.Before.Id, "apps.yaml"));
        if (OperatingSystem.IsWindows()) return;
        File.SetUnixFileMode(AppsPath, UnixFileMode.UserRead);
        Assert.False(files.CanWrite("apps.yaml"));
        Assert.ThrowsAny<Exception>(() => files.ReplaceFile("apps.yaml", Encoding.UTF8.GetBytes(LiveApps), result.AfterHash, 1, "test", "before test"));
        Assert.False(files.Status().Quarantined);
        File.SetUnixFileMode(AppsPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        Assert.True(files.CanWrite("apps.yaml"));
    }

    [Fact]
    public void OwnerIsReadOnSupportedPlatforms()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) return;
        Directory.CreateDirectory(Config); File.WriteAllText(AppsPath, "a: 1\n");
        if (UnixOwner.CurrentUser() is not { } me) return; // Platform without a known stat layout: Joule rewrites in place instead.
        Assert.Equal(me, UnixOwner.Get(AppsPath)!.Value.Uid);
    }

    [Fact]
    public void SecretReferencesSurviveRedaction()
    {
        var text = "  api_key: !secret octopus\n  token: \"!secret quoted_ref\"\n  password: hunter2-literal\n";
        var clean = PredbatMcpSafety.CleanText(text, []);
        Assert.Contains("api_key: !secret octopus", clean);
        Assert.Contains("token: \"!secret quoted_ref\"", clean);
        Assert.DoesNotContain("hunter2", clean);
        // Only the literal value is hidden: the key stays, so a suggested snippet is still a readable YAML line.
        Assert.Contains("  password: [redacted]\n", clean);
    }

    [Fact]
    public async Task ALiteralCredentialInASuggestionIsNeverWrittenAndTheFileKeepsItsOwnValue()
    {
        var (state, _, edits, db) = Live(new(true, true, "ok"));
        using var _ = db;
        // The AI copied a line with a literal token; what you review (and what Joule writes) has it redacted, then filled from the file.
        var ids = await Suggest(state, "pred_bat", "  ha_key: sk-made-up-value-abcdef\n  battery_rate_max_scaling: 0.8",
            "  ha_key: eyJhbGciOiJIUzI1NiJ9.literal-token-value-1234567890\n  battery_rate_max_scaling: 1.0");
        Assert.Equal("  ha_key: [redacted]\n  battery_rate_max_scaling: 0.8", Change(state, ids).Snippet);
        var review = edits.Review(ids.Investigation, ids.Change);
        Assert.True(review.CanApply, review.Problem);
        await edits.ApplyAsync(ids.Investigation, ids.Change, review.Hash);
        var text = File.ReadAllText(AppsPath);
        Assert.Contains("  ha_key: eyJhbGciOiJIUzI1NiJ9.literal-token-value-1234567890\n  battery_rate_max_scaling: 0.8\n", text);
        Assert.DoesNotContain("sk-made-up", text);
    }

    public void Dispose() { if (Directory.Exists(path)) Directory.Delete(path, true); }
}
