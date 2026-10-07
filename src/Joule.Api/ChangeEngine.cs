using System.Globalization;
using System.Text.RegularExpressions;

namespace Joule;

/// <summary>One setting change in a preview, with its plain name.</summary>
public sealed record PreviewChange(string Key, string Name, string Before, string After);
/// <summary>A value a restore leaves alone because it belongs to one of Predbat's own controls.</summary>
public sealed record PreviewSkip(string Key, string Name, string Kind, string StoredValue, string CurrentValue);
/// <summary>An open trial that a change would end early or confound.</summary>
public sealed record AffectedTrial(string Id, string Title, DateTimeOffset StartedAt, string Effect);
/// <summary>What an edit, undo or restore would do, computed on a copy before anything is saved.</summary>
public sealed record ChangePreview(bool Allowed, string? Reason, List<PreviewChange> Changes, List<PreviewSkip> NotRestored, List<AffectedTrial> AffectedExperiments)
{
    /// <summary>Open experiments this change would confound (they move to "Needs review").</summary>
    public List<string> ConfoundedExperimentIds => AffectedExperiments.Where(x => x.Effect == "confounded").Select(x => x.Id).ToList();
}

public static class ChangeEngine
{
    public static void CheckRevision(AppState s, int expected)
    {
        if (s.Revision != expected) throw new DomainException("Configuration changed since this was prepared. Refresh and review the current values.");
        if (s.WriteUncertain) throw new DomainException("A previous write has an uncertain outcome. Reconcile with Predbat before changing configuration.");
    }
    public static bool Equal(string a, string b) => a == b ||
        (double.TryParse(a, NumberStyles.Float, CultureInfo.InvariantCulture, out var x) && double.TryParse(b, NumberStyles.Float, CultureInfo.InvariantCulture, out var y) && double.IsFinite(x) && double.IsFinite(y) && Math.Abs(x - y) < 1e-8);
    public static void Validate(Setting setting, string value)
    {
        if (!PredbatClient.IsDiagnosticSetting(setting.Key) && !PredbatSettingsCatalogue.IsTunable(setting))
            throw new DomainException($"{setting.Name} is one of Predbat's own controls, so Joule leaves it alone. Change it in Predbat if you need to.", 400);
        if (!setting.Editable || PredbatClient.IsDiagnosticSetting(setting.Key)) throw new DomainException("This setting is read-only.", 400);
        if (value.Length > 200) throw new DomainException("Value is too long.", 400);
        if (setting.Type == "number")
        {
            if(setting.Min is not {} minimum || setting.Max is not {} maximum || !double.IsFinite(minimum) || !double.IsFinite(maximum) || minimum>maximum || !double.IsFinite(setting.Step) || setting.Step<=0)
                throw new DomainException("Numeric setting metadata is invalid.",400);
            if(!double.TryParse(value,NumberStyles.Float,CultureInfo.InvariantCulture,out var n) || !double.IsFinite(n) || n<minimum || n>maximum)
                throw new DomainException($"{setting.Name} must be a finite number between {setting.Min} and {setting.Max}.",400);
            if(!NumericStepMatches(n,minimum,setting.Step))
                throw new DomainException("Numeric value does not match the supported step.",400);
        }
        if (setting.Type == "boolean" && value is not ("on" or "off")) throw new DomainException("Choose on or off.", 400);
        if (setting.Type == "select" && !setting.Options.Contains(value)) throw new DomainException("Choose an available option.", 400);
        if(setting.Type is not ("number" or "boolean" or "select"))throw new DomainException("This setting type is unsupported.",400);
    }
    public static bool IsOpen(Experiment e) => e.Status is "Running" or "Needs review" or "Inconclusive";
    internal static bool NumericStepMatches(double value,double minimum,double step)
    {
        var steps=(value-minimum)/step;
        // Floating-point tolerance must never grow to a meaningful fraction of a step.
        var tolerance=Math.Min(1e-4,1e-7*Math.Max(1,Math.Abs(steps)));
        return double.IsFinite(steps) && Math.Abs(steps-Math.Round(steps))<=tolerance;
    }
    /// <summary>Automatic changes are possible only for low-risk numeric tunables Joule can edit.</summary>
    public static bool IsAutoEligible(Setting setting) => PredbatSettingsCatalogue.IsTunable(setting) && setting.Editable && !PredbatClient.IsDiagnosticSetting(setting.Key) && setting.Risk == "Low" && setting.Type == "number";
    /// <summary>Keys of a proposal whose setting has moved away from the value the proposal was based on (or disappeared).</summary>
    public static List<string> StaleKeys(AppState s, Proposal p) =>
        p.Changes.Where(c => s.Settings.FirstOrDefault(x => x.Key == c.Key) is not { } setting || !Equal(setting.Value, c.Before)).Select(c => c.Key).ToList();
    public static bool CanAutoApply(AppState s, Proposal p) => s.Mode == "Auto" && !s.WriteUncertain && !s.PendingFileReload && p.Status == "Pending" && p.BaseRevision == s.Revision && p.Changes.Count > 0 &&
        StaleKeys(s, p).Count == 0 && !s.Experiments.Any(IsOpen) && p.Changes.All(c => AutoChangeAllowed(s, c));
    static bool AutoChangeAllowed(AppState s, Change c)
    {
        var setting = s.Settings.FirstOrDefault(x => x.Key == c.Key);
        if (setting is not { AutoAllowed: true } || !IsAutoEligible(setting) || !Equal(setting.Value, c.Before)) return false;
        if (!double.TryParse(c.Before, NumberStyles.Float, CultureInfo.InvariantCulture, out var before) ||
            !double.TryParse(c.After, NumberStyles.Float, CultureInfo.InvariantCulture, out var after) || !double.IsFinite(after) ||
            Math.Abs(after-before) > setting.AutoMaxStep + 1e-8 || after < (setting.AutoMinimum ?? setting.Min ?? double.MinValue) || after > (setting.AutoMaximum ?? setting.Max ?? double.MaxValue)) return false;
        if (s.Revisions.Any(r => r.Source != "Demo" && r.Changes.Any(x => x.Key == c.Key) && r.At > DateTimeOffset.UtcNow.AddHours(-setting.AutoCooldownHours))) return false;
        try { Validate(setting,c.After); return true; } catch (DomainException) { return false; }
    }
    static void CheckMode(AppState s)
    {
        if(s.PendingFileReload)throw new DomainException("Files were restored but runtime reload has not been acknowledged. Refresh and check Predbat before further tuning.");
        if (s.Mode == "Monitor") throw new DomainException("Joule is set to Watch only, so it won't change Predbat. Choose Suggest changes or Automatic under How should AI help? first.");
    }

    public static void Approve(AppState s, string id, bool allowFuture, bool automatic = false)
    {
        CheckMode(s);
        var p = Pending(s, id);
        if (s.WriteUncertain) throw new DomainException("A previous write has an uncertain outcome. Reconcile with Predbat before changing configuration.");
        // Only this proposal's own settings matter: an unrelated change since it was made doesn't make it stale.
        if (StaleKeys(s, p) is { Count: > 0 } stale)
            throw new DomainException($"{string.Join(", ", stale.Select(k => Name(s, k)))} changed since this was suggested. Check it again before approving.");
        if (automatic && !CanAutoApply(s, p)) throw new DomainException("This proposal requires your approval.");
        if (allowFuture && p.Changes.FirstOrDefault(c => !IsAutoEligible(Find(s, c.Key))) is { } notEligible)
            throw new DomainException($"{Name(s, notEligible.Key)} always needs your approval, so future automatic changes can't be allowed for it.", 400);
        var verb = automatic ? "Automatic change" : "Suggestion applied";
        Commit(s, p.Changes, automatic ? "Auto" : "Approved by you", p.Title, trialTitle: $"{verb}: {DescribeChanges(s, p.Changes, before: true)}");
        if (allowFuture) foreach (var c in p.Changes) Permission(s, c.Key, true);
        p.Status = "Applied"; p.DecidedAt = DateTimeOffset.UtcNow; p.AppliedRevision = s.Revision;
        // The trial is named after the suggestion the homeowner approved, so Insights and Changes call it the same thing.
        var trial = s.Experiments.Last(); trial.Title = p.Title; trial.Hypothesis = p.ExpectedEffect; trial.ReviewAt = trial.StartedAt.AddDays(Math.Clamp(p.ReviewDays, 1, 30)); trial.AutomaticRollback = automatic;
        // Like a to-do or file edit, approving the last open suggestion from a check closes its findings as resolved.
        if (s.Investigations.FirstOrDefault(i => i.Id == p.InvestigationId) is { } source) RecommendationDecisions.CloseFindingIfDone(s, source, p.DecidedAt.Value);
    }
    public static void Deny(AppState s, string id, string? note = null)
    {
        var p = Pending(s, id); p.Status = "Denied"; p.DecidedAt = DateTimeOffset.UtcNow; p.DecisionNote = note;
        Log(s, "decision", $"You denied “{p.Title}”." + (note is null ? "" : $" Note: {note}"));
    }
    private static Proposal Pending(AppState s, string id)
    {
        var p = s.Proposals.FirstOrDefault(x => x.Id == id) ?? throw new DomainException("Recommendation not found.", 404);
        if (p.Status != "Pending") throw new DomainException("This recommendation has already been decided.");
        return p;
    }
    public static void Permission(AppState s, string key, bool allowed)
    {
        var setting = Find(s, key);
        if (allowed && !IsAutoEligible(setting))
            throw new DomainException($"{setting.Name} always needs your approval: Joule only changes low-risk number settings by itself.", 400);
        setting.AutoAllowed = allowed;
        Log(s, "permission", $"You {(allowed ? "allowed" : "disabled")} automatic changes to {setting.Name}.");
    }
    public static void SetMode(AppState s, string mode)
    {
        if (mode is not ("Monitor" or "Recommend" or "Auto")) throw new DomainException("Unknown running mode.", 400);
        s.Mode = mode; Log(s, "mode", $"You selected {mode} mode.");
    }
    /// <summary>A manual edit. The ledger reason and the trial title both name the change.</summary>
    public static void Edit(AppState s, string key, string value, int revision, string reason)
    {
        CheckMode(s); CheckRevision(s, revision); var setting = Find(s, key);
        List<Change> changes = [new Change(key, setting.Value, value)];
        var described = $"You changed {DescribeChanges(s, changes, before: true)}";
        Commit(s, changes, "You", described, trialTitle: described);
    }
    /// <summary>Values a restore would write: tunable settings only. Predbat's own controls (version, manual overrides, mode)
    /// are never restored: restoring "update" would install an older Predbat.</summary>
    static (List<Change> Changes, List<PreviewSkip> Skipped) RestorePlan(AppState s, ConfigRevision r)
    {
        var changes = new List<Change>(); var skipped = new List<PreviewSkip>();
        foreach (var (key, stored) in r.Values)
        {
            if (PredbatClient.IsDiagnosticSetting(key)) continue;
            var setting = s.Settings.FirstOrDefault(x => x.Key == key);
            var tunable = setting is not null ? PredbatSettingsCatalogue.IsTunable(setting) : PredbatSettingsCatalogue.IsTunable(key);
            if (!tunable)
            {
                if (setting is not null && !Equal(setting.Value, stored)) skipped.Add(new(key, setting.Name, setting.Kind, stored, setting.Value));
                continue;
            }
            if (setting is null) throw new DomainException($"{key} from this version no longer exists in Predbat.");
            if (!Equal(setting.Value, stored)) changes.Add(new Change(key, setting.Value, stored));
        }
        return (changes, skipped);
    }
    public static void Restore(AppState s, int id, int expected)
    {
        CheckMode(s); CheckRevision(s, expected);
        var r = s.Revisions.FirstOrDefault(x => x.Id == id) ?? throw new DomainException("Revision not found.", 404);
        var (changes, _) = RestorePlan(s, r);
        if (changes.Count == 0) throw new DomainException($"Nothing to restore: the settings Joule can change already match version {id}.", 400);
        // Restoring ends nothing by itself and starts no new trial; open trials are confounded by the change.
        Commit(s, changes, "You", $"Restored settings from version {id}", trackExperiment: false);
    }
    public static void Revert(AppState s, int id, int expected, bool automatic = false)
    {
        CheckMode(s); CheckRevision(s, expected);
        if (automatic && s.Mode != "Auto") throw new DomainException("Automatic rollback is paused outside Auto mode.");
        var automaticTrial=automatic?s.Experiments.FirstOrDefault(e=>e.RevisionId==id && e.FileVersionId==null && IsOpen(e) && e.AutomaticRollback):null;
        if (automatic && automaticTrial==null) throw new DomainException("This trial does not currently permit automatic rollback.");
        var r = s.Revisions.FirstOrDefault(x => x.Id == id) ?? throw new DomainException("Revision not found.", 404);
        if (r.Changes.Count == 0 || s.Revisions.Any(x => x.Reverts == id)) throw new DomainException("This revision has no remaining change to revert.");
        if(automatic && RollbackEligibility(s,automaticTrial!) is {AutomaticEligible:false} eligibility)throw new DomainException(eligibility.Reason);
        foreach (var c in r.Changes)
        {
            if (!Equal(Find(s, c.Key).Value, c.After) || s.Revisions.Any(x => x.Id > id && x.Changes.Any(y => y.Key == c.Key)))
                throw new DomainException($"{Name(s, c.Key)} was edited later. Review it manually; rollback will not overwrite it.");
        }
        // The undo closes the original trial (rolled back) instead of opening a new one.
        foreach (var experiment in s.Experiments.Where(x => x.RevisionId == id && x.FileVersionId == null && IsOpen(x))) { experiment.Status = "Rolled back"; experiment.Result = "Previous values restored as a new configuration revision."; experiment.Decisions.Add(new(DateTimeOffset.UtcNow, automatic ? "automatic revert" : "revert", experiment.Result)); }
        Commit(s, r.Changes.Select(c => new Change(c.Key, c.After, c.Before)).ToList(), automatic ? "Automatic rollback" : "You", $"Undid version {id}: {DescribeChanges(s, r.Changes.Select(c => new Change(c.Key, c.After, c.Before)).ToList(), before: true)}", trackExperiment: false);
        s.Revisions[^1].Reverts = id;
        // A suggestion whose change was undone reads "Applied, then undone" in its history.
        foreach (var p in s.Proposals.Where(p => p.Status == "Applied" && (p.AppliedRevision == id || p.AppliedRevision is null && r.Source is "Approved by you" or "Auto" && r.Reason == p.Title)))
            p.Status = "Reverted";
    }
    public static void Commit(AppState s, List<Change> changes, string source, string reason, bool trackExperiment = true, string? trialTitle = null)
    {
        if (changes.Count == 0 || changes.All(c => Equal(c.Before, c.After))) throw new DomainException("There are no configuration changes to apply.", 400);
        if (changes.Select(c => c.Key).Distinct().Count() != changes.Count) throw new DomainException("A setting occurs more than once.", 400);
        foreach (var c in changes) { var setting = Find(s, c.Key); Validate(setting, c.After); if (!Equal(setting.Value, c.Before)) throw new DomainException($"{Name(s, c.Key)} no longer has the expected value."); }
        foreach (var c in changes) Find(s, c.Key).Value = c.After;
        s.Revisions.Add(new ConfigRevision { Id = s.Revision + 1, Source = source, Reason = reason, Changes = changes, Values = TunableValues(s.Settings) });
        TrackRevision(s, s.Revisions[^1], trackExperiment, trialTitle);
        Log(s, "configuration", $"{source}: {reason}. Revision {s.Revision} saved.");
    }
    /// <summary>The values a revision records: tunable settings only, so a later restore can never write Predbat's own controls.</summary>
    public static Dictionary<string, string> TunableValues(IEnumerable<Setting> settings) =>
        settings.Where(x => !PredbatClient.IsDiagnosticSetting(x.Key) && PredbatSettingsCatalogue.IsTunable(x)).ToDictionary(x => x.Key, x => x.Value);
    static bool WithinBounds(Setting x, string value) => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) && double.IsFinite(n) && n >= (x.AutoMinimum ?? x.Min ?? double.MinValue) && n <= (x.AutoMaximum ?? x.Max ?? double.MaxValue);
    static bool AutoRollbackChangeAllowed(Setting setting,Change change) => setting.AutoAllowed && IsAutoEligible(setting) && WithinBounds(setting,change.Before) &&
        double.TryParse(change.Before,NumberStyles.Float,CultureInfo.InvariantCulture,out var before) && double.TryParse(change.After,NumberStyles.Float,CultureInfo.InvariantCulture,out var after) &&
        double.IsFinite(after) && double.IsFinite(setting.AutoMaxStep) && setting.AutoMaxStep>0 && Math.Abs(before-after)<=setting.AutoMaxStep+1e-8;
    public static void PermissionBounds(AppState s, string key, bool allowed, double? minimum, double? maximum, double maxStep, int cooldownHours)
    {
        var x=Find(s,key);
        if (allowed && !IsAutoEligible(x)) throw new DomainException($"{x.Name} always needs your approval: Joule only changes low-risk number settings by itself.", 400);
        // The largest step scales with the setting's own range (10%), not a fixed 0.1.
        var stepLimit=PredbatSettingsCatalogue.MaxAutoStep(x);
        if (minimum is {} min && (!double.IsFinite(min) || min < (x.Min ?? double.MinValue) || min > (x.Max ?? double.MaxValue)) || maximum is {} max && (!double.IsFinite(max) || max > (x.Max ?? double.MaxValue) || max < (x.Min ?? double.MinValue)) || minimum > maximum || !double.IsFinite(maxStep) || maxStep <= 0 || maxStep > stepLimit + 1e-9 || cooldownHours is < 24 or > 720)
            throw new DomainException($"Choose limits within {x.Min?.ToString(CultureInfo.InvariantCulture) ?? "any"}–{x.Max?.ToString(CultureInfo.InvariantCulture) ?? "any"}, a largest change of at most {stepLimit.ToString("0.###", CultureInfo.InvariantCulture)} and a wait of 24–720 hours.",400);
        x.AutoMinimum=minimum; x.AutoMaximum=maximum; x.AutoMaxStep=maxStep; x.AutoCooldownHours=cooldownHours; Permission(s,key,allowed);
    }
    /// <summary>Records a revision against the open trials: they are confounded, and (unless this is an undo, restore or
    /// change made in Predbat and changed straight back) a new trial named after the change begins.</summary>
    public static void TrackRevision(AppState s, ConfigRevision r, bool openTrial = true, string? title = null)
    {
        if (r.Changes.Count == 0) return;
        foreach (var e in s.Experiments.Where(IsOpen)) { e.Status="Needs review"; e.Result=$"A later change ({DescribeChanges(s, r.Changes, before: false)}) confounds this trial. Review or close it before automatic tuning resumes."; }
        if (openTrial) s.Experiments.Add(new Experiment { Title=title ?? DescribeChanges(s, r.Changes, before: true), Source=r.Source, RevisionId=r.Id, StartedAt=r.At, ReviewAt=r.At.AddDays(7), AutomaticRollback=false });
    }
    public static RollbackCheck RollbackEligibility(AppState s, Experiment e)
    {
        if (e.FileVersionId != null) return new(false,false,"File changes require the explicit file restore workflow and current file hashes.");
        var r=s.Revisions.FirstOrDefault(r=>r.Id==e.RevisionId);
        if (r==null || r.Changes.Count==0) return new(false,false,"No tracked runtime changes are available to revert.");
        if (s.WriteUncertain) return new(false,false,"Write outcome is uncertain; reconcile first.");
        if (s.PendingFileReload) return new(false,false,"File restore is awaiting runtime reload acknowledgement.");
        if (s.Mode=="Monitor") return new(false,false,"Monitor mode blocks configuration writes.");
        if (s.Revisions.Any(x=>x.Reverts==r.Id)) return new(false,false,"This revision has already been reverted.");
        foreach(var c in r.Changes)
        {
            var setting=s.Settings.FirstOrDefault(x=>x.Key==c.Key);
            if (setting==null || !setting.Editable || !PredbatSettingsCatalogue.IsTunable(setting)) return new(false,false,$"{Name(s, c.Key)} is unavailable, read-only or one of Predbat's own controls.");
            if (!Equal(setting.Value,c.After) || s.Revisions.Any(x=>x.Id>r.Id && x.Changes.Any(y=>y.Key==c.Key))) return new(false,false,$"{Name(s, c.Key)} has a later edit; reverting would overwrite it.");
            try { Validate(setting,c.Before); } catch (DomainException) { return new(false,false,$"The previous value for {Name(s, c.Key)} is no longer valid."); }
        }
        if (s.Mode!="Auto" || !e.AutomaticRollback || !IsOpen(e)) return new(true,false,"Manual revert is available; automatic rollback is not enabled for this trial in the current mode.");
        if (s.Experiments.Any(x=>x.Id!=e.Id && IsOpen(x)) || s.Revisions.Any(x=>x.Id>r.Id)) return new(true,false,"Later changes or an unresolved trial prevent automatic rollback.");
        if(s.Experiments.Any(x=>x.FileVersionId!=null && x.StartedAt>=e.StartedAt))return new(true,false,"Mounted configuration files changed during this trial; review rollback manually even if the file trial was closed.");
        if (r.Changes.Any(c=>!AutoRollbackChangeAllowed(Find(s,c.Key),c))) return new(true,false,"Current permissions, risk, bounds or maximum step require a manual decision.");
        return new(true,true,"Rollback permission is current; covered financial and forecast evidence is still required.");
    }
    public static void Decide(AppState s, string id, string decision, string notes, int extendDays, int expected)
    {
        CheckRevision(s,expected);
        var e=s.Experiments.FirstOrDefault(x=>x.Id==id) ?? throw new DomainException("Experiment not found.",404);
        if (notes==null || notes.Length>4000) throw new DomainException("Notes must be at most 4000 characters.",400);
        if (decision=="notes") { e.Decisions.Add(new(DateTimeOffset.UtcNow,decision,notes)); return; }
        if (!IsOpen(e)) throw new DomainException("This experiment has already been decided.");
        switch (decision)
        {
            case "keep": e.Status="Kept"; e.Result="You chose to keep this change. This decision does not establish causal savings."; break;
            case "close": e.Status="Closed"; e.Result="You closed this trial without a savings claim."; break;
            case "extend": if(extendDays is <1 or >30) throw new DomainException("Extend by 1–30 days.",400); e.ReviewAt=(e.ReviewAt>DateTimeOffset.UtcNow?e.ReviewAt:DateTimeOffset.UtcNow).AddDays(extendDays); e.Status="Running"; e.Result="You extended the review period; confounders remain visible."; break;
            case "revert": if(e.FileVersionId!=null)throw new DomainException("Use the explicit file restore workflow with current hashes for this file change."); Revert(s,e.RevisionId,expected); break;
            default: throw new DomainException("Choose keep, close, extend, revert or notes.",400);
        }
        if(decision!="revert") e.Decisions.Add(new(DateTimeOffset.UtcNow,decision,notes,decision=="extend"?extendDays:null));
        else e.Decisions[^1]=e.Decisions[^1] with { Notes=notes };
        Log(s,"decision",$"Experiment {e.Title}: {decision}.");
    }
    public static Setting Find(AppState s, string key) => s.Settings.FirstOrDefault(x => x.Key == key) ?? throw new DomainException($"Unknown setting: {key}", 400);
    public static void Log(AppState s, string kind, string message) => s.Activities.Add(new Activity(DateTimeOffset.UtcNow, kind, message));

    static string Name(AppState s, string key) => s.Settings.FirstOrDefault(x => x.Key == key)?.Name is { Length: > 0 } name ? name : PredbatSettingsCatalogue.Find(key)?.Entry.FriendlyName ?? key;
    static string Value(string value) => value.Length > 40 ? value[..37] + "…" : value;
    /// <summary>"House load scaling 1.08 → 1.00" (with "and 2 more" for longer lists).</summary>
    public static string DescribeChanges(AppState s, IReadOnlyList<Change> changes, bool before)
    {
        if (changes.Count == 0) return "no setting changes";
        string One(Change c) => before ? $"{Name(s, c.Key)} {Value(c.Before)} → {Value(c.After)}" : $"{Name(s, c.Key)} → {Value(c.After)}";
        return changes.Count == 1 ? One(changes[0]) : changes.Count == 2 ? $"{One(changes[0])} and {One(changes[1])}" : $"{One(changes[0])} and {changes.Count - 1} more";
    }

    static List<AffectedTrial> Affected(AppState before, AppState after) => before.Experiments.Where(IsOpen).Select(e =>
    {
        var now = after.Experiments.FirstOrDefault(x => x.Id == e.Id);
        var effect = now is null ? "unchanged" : now.Status == "Rolled back" ? "rolled back" : now.Status != e.Status || now.Result != e.Result ? "confounded" : "unchanged";
        return new AffectedTrial(e.Id, e.Title, e.StartedAt, effect);
    }).Where(x => x.Effect != "unchanged").ToList();
    static ChangePreview Preview(AppState s, Action<AppState> apply, List<PreviewSkip>? skipped = null)
    {
        var copy = JsonDefaults.Clone(s);
        try
        {
            apply(copy);
            var revision = copy.Revisions[^1];
            return new(true, null, revision.Changes.Select(c => new PreviewChange(c.Key, Name(s, c.Key), c.Before, c.After)).ToList(), skipped ?? [], Affected(s, copy));
        }
        catch (DomainException e) { return new(false, e.Message, [], skipped ?? [], []); }
    }
    /// <summary>What saving a manual edit would do, including the trials it would confound.</summary>
    public static ChangePreview PreviewEdit(AppState s, string key, string value) => Preview(s, copy => Edit(copy, key, value, copy.Revision, "preview"));
    /// <summary>What undoing a version would do: the values put back and the trials rolled back or confounded.</summary>
    public static ChangePreview PreviewRevert(AppState s, int id) => Preview(s, copy => Revert(copy, id, copy.Revision));
    /// <summary>What restoring a version would do, and which of Predbat's own controls it deliberately leaves alone.</summary>
    public static ChangePreview PreviewRestore(AppState s, int id)
    {
        var r = s.Revisions.FirstOrDefault(x => x.Id == id) ?? throw new DomainException("Revision not found.", 404);
        List<PreviewSkip> skipped;
        try { skipped = RestorePlan(s, r).Skipped; } catch (DomainException) { skipped = []; }
        return Preview(s, copy => Restore(copy, id, copy.Revision), skipped);
    }

    /// <summary>Prepares a read copy of the state: classifies settings persisted before the catalogue existed (including before
    /// the first live collection), rewords legacy ledger text, and marks pending suggestions stale only for their own keys.
    /// The stored revision history is not changed.</summary>
    public static void PresentForRead(AppState result)
    {
        foreach (var setting in result.Settings) PredbatSettingsCatalogue.Apply(setting);
        foreach (var revision in result.Revisions)
        {
            if (revision.Reason == "Initial live configuration snapshot") revision.Reason = "First copy of your Predbat settings";
            else if (revision.Reason == "Configuration change observed outside this app")
                revision.Reason = revision.Changes.Count == 0 ? "Predbat's list of settings changed" : $"Changed in Predbat: {DescribeChanges(result, revision.Changes, before: true)}";
        }
        foreach (var e in result.Experiments.Where(e => e.Title == "Configuration change observed outside this app"))
            if (result.Revisions.FirstOrDefault(r => r.Id == e.RevisionId) is { } origin) e.Title = origin.Reason;
        foreach (var proposal in result.Proposals.Where(p => p.Status == "Pending"))
        {
            proposal.StaleKeys = StaleKeys(result, proposal);
            proposal.ConfoundsExperimentIds = result.Experiments.Where(IsOpen).Select(x => x.Id).ToList();
        }
    }

    // ---- Changes observed in Predbat ----

    /// <summary>Applies one collection's observed settings: tunable changes become a revision (and a trial unless they undo a
    /// change made in Predbat within the last day); changes to Predbat's own controls become timeline events; debug values are
    /// ignored. Returns true when a revision was recorded.</summary>
    public static bool RecordObserved(AppState next, List<Setting> observed, DateTimeOffset now)
    {
        var previous = next.Settings;
        var changes = observed.Where(x => !PredbatClient.IsDiagnosticSetting(x.Key) && previous.Any(y => y.Key == x.Key && !Equal(x.Value, y.Value)))
            .Select(x => (Setting: x, Change: new Change(x.Key, previous.First(y => y.Key == x.Key).Value, x.Value))).ToList();
        var tunable = changes.Where(x => PredbatSettingsCatalogue.IsTunable(x.Setting)).Select(x => x.Change).ToList();
        foreach (var (setting, change) in changes.Where(x => x.Setting.Kind is SettingKind.Override or SettingKind.Software or SettingKind.Control))
            AddEvent(next, setting, change, now);
        // Classify previous keys afresh: settings persisted before classification all default to tunable.
        static IEnumerable<string> TunableKeys(IEnumerable<Setting> list) => list.Where(x => !PredbatClient.IsDiagnosticSetting(x.Key) && PredbatSettingsCatalogue.IsTunable(x.Key, x.Type, x.Options)).Select(x => x.Key).Order(StringComparer.Ordinal);
        var catalogChanged = !TunableKeys(observed).SequenceEqual(TunableKeys(previous));
        if (tunable.Count == 0 && !catalogChanged && next.Revision != 0) return false;
        var described = DescribeChanges(next, tunable, before: true);
        var reason = next.Revision == 0 ? "First copy of your Predbat settings" : tunable.Count > 0 ? $"Changed in Predbat: {described}" : "Predbat's list of settings changed";
        next.Revisions.Add(new ConfigRevision { Id = next.Revision + 1, At = now, Source = "Predbat", Reason = reason, Changes = tunable, Values = TunableValues(observed) });
        var revision = next.Revisions[^1];
        if (tunable.Count > 0)
        {
            var undone = UndoneWithinADay(next, revision, now);
            if (undone is not null)
                foreach (var e in next.Experiments.Where(x => x.RevisionId == undone.Id && x.FileVersionId == null && IsOpen(x)))
                {
                    // A change made in Predbat and changed straight back isn't a trial at all; a Joule change that was put
                    // back in Predbat is a rollback of that trial.
                    var madeInPredbat = undone.Source == "Predbat";
                    e.Status = madeInPredbat ? "Closed" : "Rolled back";
                    e.Result = madeInPredbat ? "Changed back in Predbat within a day, so it isn't tracked as a trial." : "Changed back in Predbat within a day, so the trial stopped.";
                    e.Decisions.Add(new(now, madeInPredbat ? "close" : "revert", e.Result));
                }
            TrackRevision(next, revision, openTrial: undone is null, title: reason);
        }
        else if (next.Revision > 1)
            foreach (var e in next.Experiments.Where(IsOpen)) { e.Status = "Needs review"; e.Result = "Predbat's list of settings changed, so this change's effect can't be isolated."; }
        Log(next, "configuration", $"Captured live configuration revision {next.Revision}.");
        return true;
    }
    /// <summary>The earlier revision this one exactly undoes, if it was made within the last 24 hours: one observed in Predbat,
    /// or a Joule change whose trial is still open (put back by hand in Predbat).</summary>
    static ConfigRevision? UndoneWithinADay(AppState s, ConfigRevision r, DateTimeOffset now) =>
        s.Revisions.Where(x => x.Id < r.Id && (x.Source == "Predbat" || s.Experiments.Any(e => e.RevisionId == x.Id && e.FileVersionId == null && IsOpen(e))) && x.Changes.Count == r.Changes.Count && x.Changes.Count > 0 && now - x.At <= TimeSpan.FromHours(24))
            .OrderByDescending(x => x.Id)
            .FirstOrDefault(x => x.Changes.All(c => r.Changes.Any(y => y.Key == c.Key && Equal(y.Before, c.After) && Equal(y.After, c.Before)))
                && !s.Revisions.Any(z => z.Id > x.Id && z.Id < r.Id && z.Changes.Any(y => x.Changes.Any(c => c.Key == y.Key))));

    const int MaxEvents = 500;
    static void AddEvent(AppState s, Setting setting, Change change, DateTimeOffset at)
    {
        // A manual override set and then cleared within a day is one event, not two.
        var earlier = s.SettingEvents.LastOrDefault(e => e.Key == change.Key && e.RevertedAt is null && Equal(e.Before, change.After) && at - e.At <= TimeSpan.FromHours(24));
        if (earlier is not null && setting.Kind == SettingKind.Override)
        {
            earlier.RevertedAt = at;
            earlier.Title = $"{EventTitle(setting, earlier.Before, earlier.After)} (cleared after {Duration(at - earlier.At)})";
            return;
        }
        s.SettingEvents.Add(new SettingEvent { At = at, Kind = setting.Kind, Key = change.Key, Name = setting.Name, Before = change.Before, After = change.After, Title = EventTitle(setting, change.Before, change.After) });
        if (s.SettingEvents.Count > MaxEvents) s.SettingEvents.RemoveRange(0, s.SettingEvents.Count - MaxEvents);
    }
    static string Duration(TimeSpan span) => span.TotalMinutes < 90 ? $"{Math.Max(1, (int)Math.Round(span.TotalMinutes))} min" : $"{span.TotalHours:0.#} h";
    static string ManualSlots(string value) => string.Join(", ", value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Select(x => x.TrimStart('+').Trim('[', ']')).Take(4));
    static readonly Regex Version = new(@"v?\d+(?:\.\d+)+", RegexOptions.Compiled);
    /// <summary>Plain sentence for a change to one of Predbat's own controls.</summary>
    public static string EventTitle(Setting setting, string before, string after)
    {
        var name = setting.Name;
        switch (setting.Key)
        {
            case "update":
                var to = Version.Match(after).Value; var from = Version.Match(before).Value;
                if (to.Length == 0) return $"Predbat update selection changed to {Value(after)}";
                return Newer(to, from) ? $"Predbat updated to {to}" : $"Predbat changed to {to}";
            case "mode": return $"Predbat mode changed to {after}";
            case "set_read_only": return after == "on" ? "Predbat switched to read-only (no inverter control)" : "Predbat read-only turned off";
            case "auto_update": return after == "on" ? "Predbat automatic updates turned on" : "Predbat automatic updates turned off";
            case "holiday_days_left": return after is "0" or "0.0" ? "Holiday mode ended" : $"Holiday mode: {after} days left";
        }
        if (setting.Key.StartsWith("manual_", StringComparison.Ordinal) && setting.Type == "select")
            return after == "off" ? $"{name} cleared" : before == "off" ? $"{name} set for {ManualSlots(after)}" : $"{name} changed to {ManualSlots(after)}";
        if (setting.Type == "boolean") return $"{name} turned {after}";
        return $"{name} changed from {Value(before)} to {Value(after)}";
    }
    static bool Newer(string to, string from)
    {
        static int[] Parts(string v) => v.TrimStart('v', 'V').Split('.').Select(x => int.TryParse(x, out var n) ? n : 0).ToArray();
        if (from.Length == 0) return true;
        var a = Parts(to); var b = Parts(from);
        for (var i = 0; i < Math.Max(a.Length, b.Length); i++) { var x = i < a.Length ? a[i] : 0; var y = i < b.Length ? b[i] : 0; if (x != y) return x > y; }
        return false;
    }

    /// <summary>One-time upgrade: revisions recorded for Predbat's own controls (version updates, manual overrides, the
    /// calculating flag) become timeline events, and the trials they opened close as "Not a tunable change".</summary>
    public static bool MigrateSettingKinds(AppState s)
    {
        if (s.SettingKindsVersion >= 1) return false;
        foreach (var setting in s.Settings) PredbatSettingsCatalogue.Apply(setting);
        foreach (var r in s.Revisions)
        {
            var own = r.Changes.Where(c => !PredbatClient.IsDiagnosticSetting(c.Key) && PredbatSettingsCatalogue.Kind(c.Key, s.Settings.FirstOrDefault(x => x.Key == c.Key)?.Type ?? "select") is SettingKind.Override or SettingKind.Software or SettingKind.Control).ToList();
            foreach (var c in own)
            {
                var setting = s.Settings.FirstOrDefault(x => x.Key == c.Key) ?? PredbatSettingsCatalogue.Apply(new Setting { Key = c.Key, Name = c.Key, Type = "select" });
                if (!s.SettingEvents.Any(e => e.Key == c.Key && e.At == r.At)) AddEvent(s, setting, c, r.At);
            }
            if (r.Changes.Count > 0 && r.Changes.All(c => PredbatClient.IsDiagnosticSetting(c.Key) || !PredbatSettingsCatalogue.IsTunable(c.Key, s.Settings.FirstOrDefault(x => x.Key == c.Key)?.Type ?? "select")))
                foreach (var e in s.Experiments.Where(x => x.RevisionId == r.Id && x.FileVersionId == null && IsOpen(x)))
                {
                    e.Status = "Closed";
                    e.Result = $"Not a tunable change: {string.Join(", ", r.Changes.Select(c => Name(s, c.Key)))} is one of Predbat's own controls, now shown as an event in History.";
                    e.Decisions.Add(new(DateTimeOffset.UtcNow, "close", "Not a tunable change"));
                }
        }
        s.SettingEvents = s.SettingEvents.OrderBy(e => e.At).ToList();
        s.SettingKindsVersion = 1;
        return true;
    }
}
