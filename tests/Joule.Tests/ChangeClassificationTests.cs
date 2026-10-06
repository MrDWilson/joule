using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Joule;
using Xunit;

namespace Joule.Tests;

/// <summary>Settings catalogue, setting kinds and what they gate: only tunable settings become revisions, trials, proposals,
/// undo and restore; Predbat's own controls become History events.</summary>
public class ChangeClassificationTests : IDisposable
{
    readonly string directory = Path.Combine(Path.GetTempPath(), "predbat-classification-" + Guid.NewGuid().ToString("N"));

    // Every key the live install exposed (GET /api/state, 5 Oct 2026).
    static readonly string[] LiveKeys = "active ai_ha_state_enable auto_update balance_inverters_enable battery_capacity_nominal battery_loss battery_loss_discharge battery_rate_max_scaling battery_rate_max_scaling_discharge best_soc_keep best_soc_keep_weight best_soc_max best_soc_min calculate_export_high_import calculate_export_on_pv calculate_export_oncharge calculate_import_low_export calculate_inday_adjustment calculate_plan_every car_charging_energy_scale car_charging_from_battery car_charging_hold car_charging_loss car_charging_manual_soc car_charging_plan_max_price car_charging_plan_smart car_charging_plan_time car_charging_rate car_charging_threshold car_energy_reported_load carbon_enable charge_scaling10 chat_confirm_writes chat_web_search combine_charge_slots combine_export_slots combine_rate_threshold compare_active debug_enable debug_history_count debug_history_enable debug_history_force_capture debug_history_interval expert_mode export_more_solar export_more_solar_threshold forecast_plan_hours holiday_days_left holiday_load_scaling iboost_enable inverter_hybrid inverter_limit_override inverter_loss inverter_set_charge_before inverter_soc_reset load_filter_modal load_scaling load_scaling10 load_scaling90 load_scaling_free load_scaling_saving low_power_pv_threshold_w manual_api manual_charge manual_demand manual_export manual_export_rates manual_export_value manual_freeze_charge manual_freeze_export manual_import_rates manual_import_value manual_load_adjust manual_load_value manual_soc manual_soc_max manual_soc_max_value manual_soc_value metric_battery_cycle metric_battery_value_export_scaling metric_battery_value_scaling metric_cloud_enable metric_dynamic_load_adjust metric_future_rate_offset_export metric_future_rate_offset_import metric_inday_adjust_damping metric_load_divergence_enable metric_min_improvement metric_min_improvement_export metric_min_improvement_export_freeze metric_min_improvement_plan metric_min_improvement_swap metric_pv_calibration_enable metric_self_sufficiency mode next_volume_temp octopus_intelligent_charging octopus_intelligent_consider_full octopus_intelligent_dynamic octopus_intelligent_ignore_unplugged octopus_intelligent_trust_slots octopus_saving_auto_join octopus_saving_auto_join_lead_hours performance_tweaks plan_debug predheat_enable pv_metric10_weight pv_metric90_weight pv_scaling rate_high_threshold rate_low_threshold saverestore set_charge_freeze set_charge_freeze_only set_charge_low_power set_discharge_during_charge set_event_notify set_export_freeze set_export_freeze_only set_export_low_power set_freeze_export_during_demand set_inverter_notify set_read_only set_reserve_enable set_reserve_min set_status_notify set_system_notify update".Split(' ');

    static JsonDocument DocsIndex() => JsonDocument.Parse(File.ReadAllText(Path.Combine(PlanGlossaryTests.RepositoryRoot(), "src", "Joule.Api", "Knowledge", "predbat-docs-index.json")));

    [Fact]
    public void EveryCitedDocAnchorExistsAtThePinnedRefAndMentionsTheSetting()
    {
        using var index = DocsIndex();
        Assert.Equal(PredbatSettingsCatalogue.DocsRef, index.RootElement.GetProperty("ref").GetString());
        var sections = index.RootElement.GetProperty("sections");
        foreach (var entry in PredbatSettingsCatalogue.Entries.Values)
        {
            if (entry.Doc.Length == 0) { Assert.True(entry.Description.Length == 0, $"{entry.Key} has a description but no cited section; leave it blank rather than invent one."); continue; }
            Assert.True(sections.TryGetProperty(entry.Doc, out var keys), $"{entry.Key}: {entry.Doc} is not a heading in Predbat's docs at {PredbatSettingsCatalogue.DocsRef}.");
            Assert.True(keys.EnumerateArray().Any(k => k.GetString() == entry.Key), $"{entry.Key}: the cited section {entry.Doc} doesn't mention the setting.");
            Assert.False(string.IsNullOrWhiteSpace(entry.Description), $"{entry.Key} cites {entry.Doc} but has no description.");
            Assert.True(entry.Description.Length <= 240, $"{entry.Key}: keep descriptions to one or two plain sentences.");
            Assert.DoesNotContain("Discovered from Predbat", entry.Description);
        }
    }

    /// <summary>Set JOULE_DOCS_ONLINE=1 to check the committed index still matches Predbat's docs at the pinned ref.</summary>
    [Fact]
    public async Task DocsIndexMatchesTheFetchedDocsAtThePinnedRef()
    {
        if (Environment.GetEnvironmentVariable("JOULE_DOCS_ONLINE") != "1") return;
        using var index = DocsIndex();
        using var http = new HttpClient();
        foreach (var source in index.RootElement.GetProperty("sources").EnumerateObject())
        {
            var text = await http.GetStringAsync($"https://raw.githubusercontent.com/springfall2008/batpred/{PredbatSettingsCatalogue.DocsRef}/docs/{source.Name}");
            Assert.Equal(source.Value.GetString(), Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant());
        }
    }

    [Fact]
    public void CatalogueIsWellFormedAndCoversTheLiveInstall()
    {
        Assert.All(PredbatSettingsCatalogue.Entries.Values, e =>
        {
            Assert.Contains(e.Kind, SettingKind.All);
            Assert.Contains(e.Section, PredbatSettingsCatalogue.Sections);
            Assert.Contains(e.Risk, new[] { "Low", "Medium", "High" });
            Assert.False(string.IsNullOrWhiteSpace(e.FriendlyName));
            Assert.NotEqual(e.Key, e.FriendlyName);
        });
        var described = LiveKeys.Count(k => PredbatSettingsCatalogue.Find(k) is { Entry: { Description.Length: > 0, Section: not PredbatSettingsCatalogue.OtherSection } } || PredbatClient.IsDiagnosticSetting(k));
        Assert.True(described >= LiveKeys.Length * .9, $"Only {described} of {LiveKeys.Length} live settings have a section and description.");
        Assert.All(LiveKeys, k => Assert.NotNull(PredbatSettingsCatalogue.Find(k)));
        Assert.InRange(PredbatSettingsCatalogue.Entries.Values.Count(e => e.CommonlyTuned), 8, 20);
        Assert.All(PredbatSettingsCatalogue.Entries.Values.Where(e => e.CommonlyTuned), e => Assert.Equal(SettingKind.Tunable, e.Kind));
    }

    [Theory]
    [InlineData("load_scaling")] [InlineData("pv_scaling")] [InlineData("battery_rate_max_scaling")] [InlineData("combine_charge_slots")]
    [InlineData("best_soc_keep")] [InlineData("set_reserve_min")] [InlineData("car_charging_from_battery")]
    public void SettingsChangedThroughJouleStayTunable(string key) => Assert.Equal(SettingKind.Tunable, PredbatSettingsCatalogue.Kind(key));

    [Fact]
    public void PredbatsOwnControlsAreNeverTunable()
    {
        foreach (var key in LiveKeys.Where(k => k.StartsWith("manual_", StringComparison.Ordinal)).Concat(["update", "auto_update", "saverestore", "mode", "active", "set_read_only", "debug_enable", "debug_history_force_capture", "plan_debug", "holiday_days_left", "expert_mode"]))
            Assert.False(PredbatSettingsCatalogue.IsTunable(key), $"{key} must not be tunable.");
        Assert.Equal(SettingKind.Software, PredbatSettingsCatalogue.Kind("update"));
        Assert.Equal(SettingKind.Override, PredbatSettingsCatalogue.Kind("manual_soc_max_value"));
        Assert.Equal(SettingKind.Control, PredbatSettingsCatalogue.Kind("mode"));
        // Keys the catalogue doesn't know yet fall back to Predbat's naming patterns and the select's options.
        Assert.Equal(SettingKind.Override, PredbatSettingsCatalogue.Kind("manual_brand_new"));
        Assert.Equal(SettingKind.Debug, PredbatSettingsCatalogue.Kind("debug_brand_new"));
        Assert.Equal(SettingKind.Debug, PredbatSettingsCatalogue.Kind("set_brand_new_notify"));
        Assert.Equal(SettingKind.Software, PredbatSettingsCatalogue.Kind("installer", "select", ["main", "v9.3.5 Fixes", "v9.3.4 Other"]));
        Assert.Equal(SettingKind.Tunable, PredbatSettingsCatalogue.Kind("brand_new_knob"));
        // Extra cars and inverters reuse their base entry.
        var car2 = PredbatSettingsCatalogue.Apply(new Setting { Key = "car_charging_rate_1", Name = "Car charging rate (Car 1)", Value = "7.4", Min = 0, Max = 24, Step = .1 });
        Assert.Equal("Car charging rate (car 2)", car2.Name); Assert.Equal("Car & Octopus", car2.Section);
    }

    [Fact]
    public void ApplyGivesPlainNamesSectionsAndOnlyLowRiskNumbersCanBeAutomated()
    {
        var update = PredbatSettingsCatalogue.Apply(new Setting { Key = "update", Name = "Predbat update", Type = "select", Value = DemoData.Version, Options = ["main", DemoData.Version], Editable = true, AutoAllowed = true });
        Assert.False(update.Editable); Assert.False(update.AutoAllowed); Assert.False(update.AutoEligible);
        Assert.Equal("Predbat version", update.Name); Assert.Equal("Predbat update", update.PredbatName); Assert.Equal("Predbat software", update.Section);
        Assert.Equal("https://springfall2008.github.io/batpred/customisation/#updating-predbat", update.Documentation);
        var load = PredbatSettingsCatalogue.Apply(new Setting { Key = "load_scaling", Name = "Load Scaling", Value = "1.05", Min = 0, Max = 2, Step = .01 });
        Assert.True(load.AutoEligible); Assert.True(load.CommonlyTuned); Assert.Equal("1.05", load.Default); Assert.Equal("Forecast", load.Section); Assert.Equal("Forecast", load.Category);
        var rate = PredbatSettingsCatalogue.Apply(new Setting { Key = "battery_rate_max_scaling", Value = "1", Min = 0, Max = 2, Step = .01 });
        Assert.False(rate.AutoEligible); Assert.Equal("Medium", rate.Risk);
        var toggle = PredbatSettingsCatalogue.Apply(new Setting { Key = "calculate_import_low_export", Type = "boolean", Value = "on" });
        Assert.Equal("Low", toggle.Risk); Assert.False(toggle.AutoEligible);
        var unknown = PredbatSettingsCatalogue.Apply(new Setting { Key = "brand_new_knob", Description = "Discovered from Predbat. Review its documented effect before changing.", Value = "1", Min = 0, Max = 2, Step = .1 });
        Assert.Equal("", unknown.Description); Assert.Equal("High", unknown.Risk); Assert.Equal("Other", unknown.Section);
        Assert.Equal(.2, PredbatSettingsCatalogue.MaxAutoStep(load), 6);
        Assert.Equal(10, PredbatSettingsCatalogue.MaxAutoStep(new Setting { Min = 0, Max = 100, Step = 1 }));
    }

    [Fact]
    public void WholeNumberSettingsStartWithAnAutomaticStepOfAtLeastOneStep()
    {
        Assert.Equal(5, PredbatSettingsCatalogue.DefaultAutoStep(new Setting { Key = "calculate_plan_every", Min = 5, Max = 60, Step = 5 }));
        Assert.Equal(.1, PredbatSettingsCatalogue.DefaultAutoStep(new Setting { Key = "load_scaling", Min = 0, Max = 2, Step = .01 }), 6);
        // Never above the 10%-of-range ceiling.
        Assert.Equal(.05, PredbatSettingsCatalogue.DefaultAutoStep(new Setting { Key = "x", Min = 0, Max = .5, Step = .01 }), 6);
    }

    [Fact]
    public void ReapplyingTheCatalogueKeepsHomeAssistantsNameForNumberedVariants()
    {
        var car2 = PredbatSettingsCatalogue.Apply(new Setting { Key = "car_charging_rate_1", Name = "Car charging rate (Car 1)", Value = "7.4", Min = 0, Max = 24, Step = .1 });
        Assert.Equal("Car charging rate (Car 1)", car2.PredbatName);
        car2.PredbatName = null;
        PredbatSettingsCatalogue.Apply(car2);
        Assert.Null(car2.PredbatName);
    }

    [Fact]
    public void AJouleChangePutBackInPredbatWithinADayRollsBackItsTrial()
    {
        var s = DemoData.Create();
        ChangeEngine.Approve(s, s.Proposals[0].Id, false);
        var trial = Assert.Single(s.Experiments, e => ChangeEngine.IsOpen(e));
        var observed = JsonDefaults.Clone(s.Settings);
        observed.Single(x => x.Key == "load_scaling").Value = "1.08";
        Assert.True(ChangeEngine.RecordObserved(s, observed, DateTimeOffset.UtcNow.AddHours(3)));
        Assert.Equal("Rolled back", trial.Status);
        Assert.Contains("Changed back in Predbat", trial.Result);
        Assert.DoesNotContain(s.Experiments, e => ChangeEngine.IsOpen(e));
    }

    static AppState LiveLike()
    {
        var s = new AppState { DataSource = "Live", Mode = "Recommend" };
        s.Settings = [
            new Setting { Key = "load_scaling", EntityId = "input_number.predbat_load_scaling", Name = "Load Scaling", Value = "1.00", Min = 0, Max = 2, Step = .01 },
            new Setting { Key = "update", EntityId = "select.predbat_update", Type = "select", Value = DemoData.Version, Options = ["main", DemoData.Version, "v9.3.4 IOG started-dispatch fix"] },
            new Setting { Key = "manual_charge", EntityId = "select.predbat_manual_charge", Type = "select", Value = "off", Options = ["off", "+Sun 15:00", "Sun 15:00"] },
            new Setting { Key = "mode", EntityId = "select.predbat_mode", Type = "select", Value = "Control charge & discharge", Options = ["Monitor", "Control charge & discharge"] },
        ];
        foreach (var x in s.Settings) PredbatSettingsCatalogue.Apply(x);
        // Revision 6 as stored by the previous version: every entity's value, including the version select and a manual override.
        s.Revisions.Add(new ConfigRevision { Id = 6, Source = "Predbat", Reason = "Configuration change observed outside this app", Values = new() { ["load_scaling"] = "1.08", ["update"] = "v9.3.4 IOG started-dispatch fix", ["manual_charge"] = "+Sun 15:00", ["mode"] = "Monitor" } });
        return s;
    }

    [Fact]
    public void RestoringRevisionSixNeverWritesPredbatsVersionOrOverrides()
    {
        var s = LiveLike();
        var preview = ChangeEngine.PreviewRestore(s, 6);
        Assert.True(preview.Allowed);
        Assert.Equal(["load_scaling"], preview.Changes.Select(x => x.Key));
        Assert.Equal(["update", "manual_charge", "mode"], preview.NotRestored.Select(x => x.Key));
        ChangeEngine.Restore(s, 6, s.Revision);
        var changes = s.Revisions[^1].Changes;
        Assert.Equal(["load_scaling"], changes.Select(x => x.Key));
        Assert.Equal(DemoData.Version, ChangeEngine.Find(s, "update").Value);
        Assert.DoesNotContain("update", s.Revisions[^1].Values.Keys);
        Assert.Empty(s.Experiments);
        // Nothing tunable left to restore.
        Assert.Throws<DomainException>(() => ChangeEngine.Restore(s, 6, s.Revision));
    }

    sealed class RecordingWriter : IPredbatClient
    {
        public List<List<Change>> Writes { get; } = [];
        public bool Configured => true;
        public bool WritesEnabled => true;
        public Task<LiveSnapshot> CollectAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task ApplyAsync(List<Change> changes, List<Setting> settings, CancellationToken ct) { Writes.Add(changes); return Task.CompletedTask; }
    }

    [Fact]
    public async Task ALiveRestoreSendsOnlyTunableValuesToPredbat()
    {
        using var db = new DataStore(directory);
        var seed = LiveLike(); seed.SettingKindsVersion = 1; db.Save(seed);
        var writer = new RecordingWriter();
        var service = new StateService(db, writer, false);
        await service.MutateAsync(s => ChangeEngine.Restore(s, 6, s.Revision));
        var written = Assert.Single(writer.Writes);
        Assert.Equal(["load_scaling"], written.Select(x => x.Key));
    }

    [Fact]
    public async Task ThePredbatClientRefusesToWriteTheUpdateSelect()
    {
        var handler = new Handler();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Predbat:BaseUrl"] = "http://predbat.test", ["Predbat:WritesEnabled"] = "true" }).Build();
        var client = new PredbatClient(new HttpClient(handler), config);
        var update = new Setting { Key = "update", EntityId = "select.predbat_update", Type = "select", Value = DemoData.Version, Options = ["v9.3.4 IOG started-dispatch fix", DemoData.Version], Editable = true, Kind = SettingKind.Tunable };
        var failure = await Assert.ThrowsAsync<LiveWriteException>(() => client.ApplyAsync([new("update", DemoData.Version, "v9.3.4 IOG started-dispatch fix")], [update], default));
        Assert.False(failure.Uncertain);
        Assert.Empty(handler.Requests);
    }
    sealed class Handler : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) { Requests.Add(request); return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") }); }
    }

    sealed class SequenceClient : IPredbatClient
    {
        public List<Setting> Settings { get; set; } = [];
        public bool Configured => true;
        public bool WritesEnabled => false;
        public Task<LiveSnapshot> CollectAsync(CancellationToken ct) => Task.FromResult(new LiveSnapshot(JsonDefaults.Clone(Settings).Select(PredbatSettingsCatalogue.Apply).ToList(), null, "{}", "{}"));
        public Task ApplyAsync(List<Change> changes, List<Setting> settings, CancellationToken ct) => throw new NotSupportedException();
        public void Set(string key, string value) => Settings.Single(x => x.Key == key).Value = value;
    }

    [Fact]
    public async Task ChangesMadeInPredbatBecomeEventsOrNamedTrials()
    {
        using var db = new DataStore(directory);
        var client = new SequenceClient { Settings = LiveLike().Settings };
        var service = new StateService(db, client, false);
        await service.CollectAsync();
        Assert.Equal(1, service.Read().Revision);
        Assert.Equal("First copy of your Predbat settings", service.Read().Revisions[0].Reason);

        // A Predbat update and a manual override: events, no revision, no trial.
        client.Set("update", "main");
        await service.CollectAsync();
        client.Set("update", DemoData.Version); client.Set("manual_charge", "+Sun 15:00");
        await service.CollectAsync();
        client.Set("manual_charge", "off");
        await service.CollectAsync();
        var state = service.Read();
        Assert.Equal(1, state.Revision);
        Assert.Empty(state.Experiments);
        Assert.Contains(state.SettingEvents, e => e.Kind == SettingKind.Software && e.Title == "Predbat updated to v9.3.5");
        var manual = Assert.Single(state.SettingEvents, e => e.Key == "manual_charge");
        Assert.NotNull(manual.RevertedAt);
        Assert.StartsWith("Manual charge slots set for Sun 15:00 (cleared after", manual.Title);

        // A tunable change made in Predbat: a revision and a trial named after the change.
        client.Set("load_scaling", "1.05");
        await service.CollectAsync();
        state = service.Read();
        Assert.Equal(2, state.Revision);
        Assert.Equal("Changed in Predbat: House load scaling 1.00 → 1.05", state.Revisions[^1].Reason);
        var trial = Assert.Single(state.Experiments);
        Assert.Equal("Changed in Predbat: House load scaling 1.00 → 1.05", trial.Title);
        Assert.DoesNotContain("update", state.Revisions[^1].Values.Keys);

        // Changed straight back: recorded, but the trial closes and no new one opens.
        client.Set("load_scaling", "1.00");
        await service.CollectAsync();
        state = service.Read();
        Assert.Equal(3, state.Revision);
        Assert.Equal("Closed", Assert.Single(state.Experiments).Status);
        Assert.Contains("Changed back", state.Experiments[0].Result);
    }

    [Fact]
    public void LegacyNonTunableRevisionsBecomeEventsAndTheirTrialsClose()
    {
        // The live history: active flips, a manual SoC target set and cleared, a manual charge set and cleared, two Predbat updates.
        var s = LiveLike(); s.Revisions.Clear();
        s.Settings.Add(PredbatSettingsCatalogue.Apply(new Setting { Key = "active", Type = "boolean", Value = "off" }));
        s.Settings.Add(PredbatSettingsCatalogue.Apply(new Setting { Key = "manual_soc", Type = "select", Value = "off", Options = ["off"] }));
        var at = DateTimeOffset.Parse("2026-10-03T08:02:00Z");
        void Revision(int id, DateTimeOffset when, params Change[] changes) { s.Revisions.Add(new ConfigRevision { Id = id, At = when, Source = "Predbat", Reason = id == 1 ? "Initial live configuration snapshot" : "Configuration change observed outside this app", Changes = [.. changes] }); if (id > 1) s.Experiments.Add(new Experiment { Title = "Configuration change observed outside this app", RevisionId = id, Status = "Needs review", Source = "Predbat", StartedAt = when }); }
        Revision(1, at.AddDays(-1));
        Revision(2, at, new Change("active", "off", "on"));
        Revision(3, at.AddMinutes(5), new Change("active", "on", "off"), new Change("manual_soc", "off", "+Sat 09:00=100.0"));
        Revision(4, at.AddMinutes(10), new Change("manual_soc", "+Sat 09:00=100.0", "off"));
        Revision(5, at.AddDays(1), new Change("manual_charge", "off", "+Sun 15:00"));
        Revision(6, at.AddDays(1).AddMinutes(20), new Change("manual_charge", "+Sun 15:00", "off"));
        Revision(7, at.AddDays(1).AddHours(5), new Change("update", "v9.3.3 Cloud inverter fixes", "v9.3.4 IOG started-dispatch fix"));
        Revision(8, at.AddDays(1).AddHours(6), new Change("update", "v9.3.4 IOG started-dispatch fix", DemoData.Version));
        Assert.Equal(7, s.Experiments.Count(ChangeEngine.IsOpen));
        Assert.True(ChangeEngine.MigrateSettingKinds(s));
        Assert.False(ChangeEngine.MigrateSettingKinds(s));
        Assert.DoesNotContain(s.Experiments, ChangeEngine.IsOpen);
        Assert.All(s.Experiments, e => { Assert.Equal("Closed", e.Status); Assert.StartsWith("Not a tunable change", e.Result); });
        Assert.Equal(["Manual battery targets set for Sat 09:00=100.0 (cleared after 5 min)", "Manual charge slots set for Sun 15:00 (cleared after 20 min)", "Predbat updated to v9.3.4", "Predbat updated to v9.3.5"], s.SettingEvents.Select(e => e.Title));
    }

    [Fact]
    public void AutomaticPermissionIsOnlyForLowRiskNumbers()
    {
        var s = DemoData.Create(); s.Mode = "Recommend";
        ChangeEngine.Permission(s, "load_scaling", true);
        Assert.Throws<DomainException>(() => ChangeEngine.Permission(s, "battery_rate_max_scaling", true));
        Assert.Throws<DomainException>(() => ChangeEngine.Permission(s, "car_charging_from_battery", true));
        Assert.Throws<DomainException>(() => ChangeEngine.Permission(s, "update", true));
        ChangeEngine.Permission(s, "battery_rate_max_scaling", false);
        // The largest step is 10% of the setting's range: load_scaling here runs 0.5–1.5.
        ChangeEngine.PermissionBounds(s, "load_scaling", true, null, null, .1, 72);
        Assert.Throws<DomainException>(() => ChangeEngine.PermissionBounds(s, "load_scaling", true, null, null, .11, 72));
        var reserve = ChangeEngine.Find(s, "pv_metric10_weight"); reserve.Min = 0; reserve.Max = 100; reserve.Step = 1;
        ChangeEngine.PermissionBounds(s, "pv_metric10_weight", true, null, null, 10, 72);
        // "Approve and allow future" can't grant automation to a setting that always needs approval.
        s.Proposals[0].Changes = [new Change("battery_rate_max_scaling", "1.00", "0.90")];
        Assert.Throws<DomainException>(() => ChangeEngine.Approve(s, s.Proposals[0].Id, true));
        Assert.Equal(1, s.Revision);
    }

    [Fact]
    public void TrialsAreNamedAfterTheChangeAndUndoOrRestoreOpenNone()
    {
        var s = DemoData.Create(); s.Mode = "Recommend";
        ChangeEngine.Edit(s, "pv_scaling", "0.95", s.Revision, "ignored");
        Assert.Equal("You changed Solar forecast scaling 1.00 → 0.95", s.Experiments.Single().Title);
        Assert.Equal("You changed Solar forecast scaling 1.00 → 0.95", s.Revisions[^1].Reason);
        ChangeEngine.Approve(s, s.Proposals[0].Id, false);
        // Behaviour change: an approved suggestion's trial carries the suggestion's own title.
        Assert.Equal("Bring the evening load forecast closer to reality", s.Experiments[^1].Title);
        Assert.Equal("Needs review", s.Experiments[0].Status);
        var approved = s.Experiments[^1];
        ChangeEngine.Revert(s, approved.RevisionId, s.Revision);
        Assert.Equal(2, s.Experiments.Count);
        Assert.Equal("Rolled back", approved.Status);
        Assert.StartsWith("Undid version 3", s.Revisions[^1].Reason);
    }

    [Fact]
    public void PreviewsShowTheChangeAndTheTrialsItWouldEndOrConfound()
    {
        var s = DemoData.Create(); s.Mode = "Recommend";
        ChangeEngine.Approve(s, s.Proposals[0].Id, false);
        var trial = s.Experiments.Single();
        var edit = ChangeEngine.PreviewEdit(s, "pv_scaling", "0.90");
        Assert.True(edit.Allowed);
        Assert.Equal("Solar forecast scaling", Assert.Single(edit.Changes).Name);
        Assert.Equal([trial.Id], edit.ConfoundedExperimentIds);
        Assert.Equal("Running", trial.Status);
        var undo = ChangeEngine.PreviewRevert(s, trial.RevisionId);
        Assert.True(undo.Allowed);
        Assert.Equal("rolled back", Assert.Single(undo.AffectedExperiments).Effect);
        Assert.Empty(undo.ConfoundedExperimentIds);
        var invalid = ChangeEngine.PreviewEdit(s, "pv_scaling", "9");
        Assert.False(invalid.Allowed); Assert.Contains("between", invalid.Reason);
        var control = ChangeEngine.PreviewEdit(s, "update", "main");
        Assert.False(control.Allowed); Assert.Contains("Predbat's own controls", control.Reason);
        Assert.Single(s.Experiments);
    }

    [Fact]
    public async Task ReadMarksProposalsStaleOnlyForTheirOwnKeys()
    {
        using var db = new DataStore(directory);
        var service = new StateService(db, new SequenceClient(), true);
        await service.MutateAsync(s => ChangeEngine.Edit(s, "pv_scaling", "0.95", s.Revision, "x"));
        var proposal = service.Read().Proposals.Single();
        Assert.Empty(proposal.StaleKeys);
        Assert.Single(proposal.ConfoundsExperimentIds);
        await service.MutateAsync(s => ChangeEngine.Edit(s, "load_scaling", "1.04", s.Revision, "x"));
        Assert.Equal(["load_scaling"], service.Read().Proposals.Single().StaleKeys);
    }

    [Fact]
    public void DemoUsesTheCatalogueAndShowsEveryPlanState()
    {
        var s = DemoData.Create();
        Assert.All(s.Settings, x => { Assert.NotEqual(PredbatSettingsCatalogue.OtherSection, x.Section); Assert.False(string.IsNullOrWhiteSpace(x.Description)); });
        Assert.Contains(s.Settings, x => x.Kind == SettingKind.Software);
        Assert.Contains(s.Settings, x => x.Kind == SettingKind.Override);
        Assert.Contains(s.Settings, x => x.Kind == SettingKind.Control);
        Assert.Contains(s.SettingEvents, e => e.Title == "Predbat updated to v9.3.5");
        Assert.Contains(s.Investigations, i => i.Status == "Failed");
        Assert.Contains(s.Investigations, i => i.NextSteps.Any(n => n.Status == "open"));
        Assert.Equal(["opportunity", "no_change", "problem", null], s.Investigations.Select(i => i.Verdict));
        var plan = DemoData.Plan(1);
        var raw = plan.Slots.Select(x => x.RawAction).ToHashSet();
        foreach (var code in new[] { "Chrg", "FrzChrg", "HoldChrg", "FrzExp", "Exp", "HoldExp", "Demand" }) Assert.Contains(code, raw);
        var keys = plan.Slots.Select(x => x.ActionKey).ToHashSet();
        foreach (var key in new[] { "charge", "freeze-charge", "hold-charge", "freeze-export", "export", "hold-export", "charge-export", "demand" }) Assert.Contains(key, keys);
        Assert.Contains(plan.Slots, x => x.ActionId == "hold-for-car" && x.ActionLabel == "Hold battery for the car");
        Assert.Contains(plan.Slots, x => x.SplitTime == "16:10" && x.SecondaryAction == "export" && x.PrimaryAction == "demand");
        Assert.All(plan.Slots, x => Assert.Equal(x.ActionKey, x.Action));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void DemoBatteryLevelsMoveTheWayEachPlanStateSays(int daysAgo)
    {
        var now = DateTimeOffset.UtcNow;
        var plan = DemoData.Plan(daysAgo, now);
        // Two whole local days (more for today, which looks at least 48 hours ahead).
        Assert.True(plan.Slots.Count >= 92, $"{plan.Slots.Count} slots");
        for (var i = 0; i < plan.Slots.Count; i++)
        {
            var slot = plan.Slots[i];
            var entry = PredbatGlossary.Entries.FirstOrDefault(x => x.Id == slot.ActionId);
            var action = PredbatGlossary.Actions[slot.ActionKey!];
            // The glossary entry (e.g. hold-for-car) narrows its canonical action; split slots use the combined action.
            var mayCharge = slot.SplitTime is null && slot.State2 is null && entry is not null ? entry.MayCharge : action.MayCharge;
            var mayDischarge = slot.SplitTime is null && slot.State2 is null && entry is not null ? entry.MayDischarge : action.MayDischarge;
            var label = $"{slot.Time:HH:mm} {slot.RawAction} ({slot.ActionLabel})";
            var start = slot.SocForecast; var end = slot.SocForecastEnd!.Value;
            // Today's plan starts again from the measured level in the slot holding "now", as Predbat re-plans.
            var replanned = i + 1 < plan.Slots.Count && plan.Slots[i + 1].Time <= now && plan.Slots[i + 1].Time.AddMinutes(30) > now;
            Assert.True(i + 1 == plan.Slots.Count || replanned || plan.Slots[i + 1].SocForecast == end, $"{label}: the slot must end where the next one starts");
            Assert.True(mayCharge || end <= start, $"{label}: planned {start}% -> {end}% rises, but the state does not charge");
            Assert.True(mayDischarge || end >= start, $"{label}: planned {start}% -> {end}% falls, but the state does not discharge");
            if (slot.ActionKey == "charge") Assert.True(end > start && end <= slot.TargetPercent, $"{label}: a charge rises towards its target");
            if (slot.ActionKey == "hold-charge") Assert.True(start >= slot.TargetPercent && end >= start, $"{label}: hold at target starts at or above it");
            if (slot.ActionKey == "export") Assert.True(end < start && end >= slot.TargetPercent, $"{label}: an export falls to its target, not below");
            if (slot.ActionKey == "hold-export") Assert.True(Math.Abs(start - slot.TargetPercent!.Value) < 1 && end >= start, $"{label}: a paused export stays at its minimum");
            if (i > 0 && plan.Slots[i - 1].SocActual is { } a && slot.SocActual is { } b)
            {
                Assert.True(mayCharge || b <= a, $"{label}: measured {a}% -> {b}% rises, but the state does not charge");
                Assert.True(mayDischarge || b >= a, $"{label}: measured {a}% -> {b}% falls, but the state does not discharge");
            }
        }
    }

    [Fact]
    public async Task DemoResetPutsTheSampleHouseholdBack()
    {
        using var db = new DataStore(directory);
        var service = new StateService(db, new SequenceClient(), true);
        await service.MutateAsync(s => ChangeEngine.Approve(s, s.Proposals[0].Id, false));
        Assert.Equal("Applied", service.Read().Proposals[0].Status);
        await service.ResetDemoAsync();
        var state = service.Read();
        Assert.Equal("Pending", state.Proposals.Single().Status);
        Assert.Equal(1, state.Revision);
        Assert.Empty(state.Experiments);
        Assert.Equal("1.08", state.Settings.Single(x => x.Key == "load_scaling").Value);
        Assert.NotNull(db.GetPlan());
        await service.MutateAsync(s => ChangeEngine.Approve(s, s.Proposals[0].Id, false));
        using var live = new DataStore(Path.Combine(directory, "live"));
        var seed = LiveLike(); live.Save(seed);
        await Assert.ThrowsAsync<DomainException>(() => new StateService(live, new SequenceClient(), false).ResetDemoAsync());
    }

    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
}
