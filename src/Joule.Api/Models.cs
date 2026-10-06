using System.Text.Json;

namespace Joule;

public static class JsonDefaults
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = false };
    public static T Clone<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, Options), Options)!;
}
public class DomainException(string message, int status = 409) : Exception(message) { public int Status { get; } = status; }
public record Change(string Key, string Before, string After);
public class Setting
{
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Category { get; set; } = "Forecasting";
    public string Value { get; set; } = "";
    public string Type { get; set; } = "number";
    public string Risk { get; set; } = "High";
    public string EntityId { get; set; } = "";
    public double? Min { get; set; }
    public double? Max { get; set; }
    public double Step { get; set; } = .01;
    public List<string> Options { get; set; } = [];
    public bool AutoAllowed { get; set; }
    public double? AutoMinimum { get; set; }
    public double? AutoMaximum { get; set; }
    public double AutoMaxStep { get; set; } = .1;
    public int AutoCooldownHours { get; set; } = 72;
    public bool Editable { get; set; } = true;
    public string Documentation { get; set; } = "https://springfall2008.github.io/batpred/customisation/";
    /// <summary>tunable | override | software | control | debug (see SettingKind). Only tunable settings can be edited, suggested,
    /// trialled, undone or restored through Joule; the others are Predbat's own controls and appear as timeline events.</summary>
    public string Kind { get; set; } = SettingKind.Tunable;
    /// <summary>Catalogue section (Battery, Charging, Export, Forecast, Planning, Car &amp; Octopus, Freeze &amp; inverter, iBoost,
    /// Manual overrides, Notifications &amp; debug, Predbat software, Predbat control, Other). Category mirrors it.</summary>
    public string Section { get; set; } = PredbatSettingsCatalogue.OtherSection;
    /// <summary>Predbat's default value at the catalogue's docs ref, when known.</summary>
    public string? Default { get; set; }
    public string Unit { get; set; } = "";
    /// <summary>One of the settings people most often tune; shown first.</summary>
    public bool CommonlyTuned { get; set; }
    /// <summary>Automatic changes can be allowed: a low-risk numeric tunable that Joule can edit.</summary>
    public bool AutoEligible { get; set; }
    /// <summary>Predbat's own friendly name for the entity, when it differs from Joule's plain name.</summary>
    public string? PredbatName { get; set; }
    /// <summary>Only visible in Predbat's expert mode.</summary>
    public bool ExpertOnly { get; set; }
    /// <summary>Cited documentation section, e.g. "customisation.md#battery-loss-options" (null when the docs don't describe it).</summary>
    public string? DocumentationAnchor { get; set; }
}
/// <summary>A change to one of Predbat's own controls (manual override, software, mode/read-only) shown on the History timeline.
/// These never become configuration revisions, experiments or restorable values.</summary>
public class SettingEvent
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public DateTimeOffset At { get; set; } = DateTimeOffset.UtcNow;
    public string Kind { get; set; } = SettingKind.Override;
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    public string Before { get; set; } = "";
    public string After { get; set; } = "";
    /// <summary>Plain sentence, e.g. "Predbat updated to v9.3.5" or "Manual charge set for Sun 15:00".</summary>
    public string Title { get; set; } = "";
    /// <summary>Set when the change was undone within a day (for example a manual charge cleared 20 minutes later).</summary>
    public DateTimeOffset? RevertedAt { get; set; }
}
public class ConfigRevision
{
    public int Id { get; set; }
    public DateTimeOffset At { get; set; } = DateTimeOffset.UtcNow;
    public string Source { get; set; } = "You";
    public string Reason { get; set; } = "";
    public List<Change> Changes { get; set; } = [];
    public Dictionary<string, string> Values { get; set; } = [];
    public int? Reverts { get; set; }
    public string? FileVersionBefore { get; set; }
    public string? FileVersionAfter { get; set; }
}
public partial class Proposal
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = "";
    public string Summary { get; set; } = "";
    public string ExpectedEffect { get; set; } = "";
    public string Tradeoff { get; set; } = "";
    public string Confidence { get; set; } = "Medium";
    public string Status { get; set; } = "Pending";
    public string Source { get; set; } = "AI";
    public string InvestigationId { get; set; } = "";
    public double? EstimatedMonthlySavingGbp { get; set; }
    public int BaseRevision { get; set; }
    public int ReviewDays { get; set; } = 7;
    public List<Change> Changes { get; set; } = [];
    public List<string> Evidence { get; set; } = [];
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    /// <summary>The configuration revision that applied this suggestion; undoing that revision marks it "Reverted".</summary>
    public int? AppliedRevision { get; set; }
    /// <summary>Computed on read: keys of this proposal whose current value no longer matches the change's Before. A proposal is
    /// stale only when one of its own settings moved; unrelated revisions don't block it.</summary>
    public List<string> StaleKeys { get; set; } = [];
    /// <summary>Computed on read: open experiments that applying this proposal would confound.</summary>
    public List<string> ConfoundsExperimentIds { get; set; } = [];
}
public class Experiment
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = "";
    public string Hypothesis { get; set; } = "";
    public string Status { get; set; } = "Running";
    public int RevisionId { get; set; }
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset ReviewAt { get; set; }
    public string Result { get; set; } = "Collecting evidence. No savings have been verified.";
    public double? BaselineError { get; set; }
    public double? CurrentError { get; set; }
    public bool AutomaticRollback { get; set; }
    public string Source { get; set; } = "Approved by you";
    public string? FileVersionId { get; set; }
    public List<ExperimentDecision> Decisions { get; set; } = [];
    public double? BaselineCostGbpPerDay { get; set; }
    public double? CurrentCostGbpPerDay { get; set; }
    public double BaselineCostCoverage { get; set; }
    public double CurrentCostCoverage { get; set; }
    public double BaselineForecastCoverage { get; set; }
    public double CurrentForecastCoverage { get; set; }
    public const int CurrentForecastEvidenceVersion = 1;
    public int ForecastEvidenceVersion { get; set; }
    public List<string> Confounders { get; set; } = [];
    /// <summary>A demo sample trial: its figures are illustrative, so the evaluator never re-measures it against the demo meters.</summary>
    public bool Seeded { get; set; }
    public bool RevertEligible { get; set; }
    public bool AutomaticRevertEligible { get; set; }
    public string RevertReason { get; set; } = "Eligibility has not been evaluated.";
    public const string CurrentForecastMethod = "Scoring combines consecutive whole meter intervals toward thirty minutes, allowing up to thirty seconds of receipt-time undershoot and overshoot by at most one valid interval. Measured energy is unchanged; MAE is normalized to kWh per half-hour over observed duration. Windows require median and maximum scored durations within 10%; incompatible cadence prevents automatic decisions. Forecast energy is allocated uniformly from eligible pre-slot forecasts. At least 95% window coverage for automatic decisions; forecast timing allocation is an estimate.";
    public string ForecastMethod { get; set; } = CurrentForecastMethod;
    public string FinancialMethod { get; set; } = "Observed net cost per covered day over matched duration windows; >=99% coverage, <=30 missing minutes, no invalid grid intervals, coverage difference <=0.5 percentage points. Observational comparison; no causal savings claim.";
}
public record Activity(DateTimeOffset At, string Kind, string Message);
public partial class Investigation
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public DateTimeOffset At { get; set; } = DateTimeOffset.UtcNow;
    public string Title { get; set; } = "";
    public string Summary { get; set; } = "";
    public string Category { get; set; } = "Exploration";
    public string Confidence { get; set; } = "Medium";
    public List<string> Evidence { get; set; } = [];
    public List<string> Steps { get; set; } = [];
    public string Provider { get; set; } = "Demo";
}
/// <summary>LoadActualMethod/PvActualMethod label how an elapsed slot's actual was obtained: "measured" when meter intervals exactly cover the slot, "boundary" when contiguous observed intervals straddling the slot edges were prorated, "estimated" when part of the slot is a spread (timing-estimated) interval, null when no actual is available (future slot or gap). SocActual is the battery level at the slot END; SocActualStart at the slot START, which is what Predbat's SocForecast describes. HomeActual is load minus EV when the load meter includes EV charging; EvActual is the EV meter's energy.</summary>
/// <para>Action is the canonical glossary key ("demand", "charge", "freeze-export", "charge-export"…; see PredbatGlossary) for
/// recognised states and Predbat's own code otherwise. RawAction keeps Predbat's code untouched. The remaining Predbat detail
/// fields are null for plans captured before they were recorded (and for demo plans that don't model them).</para>
public record PlanSlot(DateTimeOffset Time, double LoadForecast, double? LoadActual, double PvForecast, double? PvActual, double SocForecast, double? SocActual, double ImportRate, double ExportRate, string Action, double Cost, int DurationMinutes = 30, string? LoadActualMethod = null, string? PvActualMethod = null, double? SocActualStart = null, double? HomeActual = null, double? EvActual = null)
{
    /// <summary>Predbat's raw state code ("Exp", "Chrg", "FrzExp", "Demand"), or the legacy stored value for older plans.</summary>
    public string? RawAction { get; init; }
    /// <summary>Canonical action for the slot as a whole: demand | charge | freeze-charge | hold-charge | no-charge | export | freeze-export | hold-export | charge-export | unknown.</summary>
    public string? ActionKey { get; init; }
    /// <summary>Glossary entry id: usually the key, or a variant that shares it ("hold-for-car", "hold-for-iboost").</summary>
    public string? ActionId { get; init; }
    /// <summary>Plain-English label for the slot, including split timing ("Power your home until 18:10, then export battery to the grid").</summary>
    public string? ActionLabel { get; init; }
    /// <summary>Canonical action of the first (or only) part of the slot.</summary>
    public string? PrimaryAction { get; init; }
    /// <summary>Canonical action of the second part of a split slot (Predbat's state2), otherwise null.</summary>
    public string? SecondaryAction { get; init; }
    /// <summary>Predbat's decoded state2 text for split slots ("Exp"), otherwise null.</summary>
    public string? State2 { get; init; }
    /// <summary>Local HH:mm at which a split slot switches to its second action.</summary>
    public string? SplitTime { get; init; }
    /// <summary>Planned battery level (%) at the end of a charge or export slot (Predbat's state_target / "Limit %").</summary>
    public double? TargetPercent { get; init; }
    /// <summary>Planned battery level (%) at the end of the slot: the next slot's start level. SocForecast is the level at the start.</summary>
    public double? SocForecastEnd { get; init; }
    /// <summary>Planned battery change over the slot in kWh (Predbat's soc_change).</summary>
    public double? SocChangeKwh { get; init; }
    public List<PlanReason>? Reasons { get; init; }
    /// <summary>Predbat's reasons rendered as sentences with its own reason_templates (Joule's wording when the plan has none).</summary>
    public string? ReasonText { get; init; }
    /// <summary>Manual override on the slot ("Manual charge", "Manual export freeze"…), otherwise null.</summary>
    public string? Override { get; init; }
    /// <summary>States a past slot held part-way through (Predbat's state_mixed), otherwise null.</summary>
    public List<string>? MixedStates { get; init; }
    /// <summary>How Predbat derived the rate when it isn't the published tariff: offset | future | copy | user | increment | manual | saving.</summary>
    public string? ImportRateType { get; init; }
    public string? ExportRateType { get; init; }
    /// <summary>True when either price is Predbat's estimate (future, offset or copied from the previous day).</summary>
    public bool? RateEstimated { get; init; }
    public double? ImportRateAdjusted { get; init; }
    public double? ExportRateAdjusted { get; init; }
    public double? CarKwh { get; init; }
    public double? IBoostKwh { get; init; }
    /// <summary>Pessimistic (10%) solar and load forecasts for the slot, kWh.</summary>
    public double? Pv10 { get; init; }
    public double? Load10 { get; init; }
    public double? ClippedKwh { get; init; }
    /// <summary>Predbat's running total cost for today at the start of the slot, GBP.</summary>
    public double? TotalCost { get; init; }
}
public record PlanReason(string Code, Dictionary<string, string> Params);
public class PlanSnapshot
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public DateTimeOffset At { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CollectedAt { get; set; }
    public string Source { get; set; } = "Demo";
    public List<PlanSlot> Slots { get; set; } = [];
}
public class AiPreferences
{
    public string Provider { get; set; } = "Demo";
    public string Model { get; set; } = "";
    public bool Scheduled { get; set; }
    public int IntervalMinutes { get; set; } = 60;
    public int MaxRunsPerDay { get; set; } = 12;
    public double InputUsdPerMillion { get; set; }
    public double OutputUsdPerMillion { get; set; }
}
public record UsageRecord(DateTimeOffset At, string Provider, string Model, long InputTokens, long OutputTokens, double? EstimatedUsd, string Status)
{
    /// <summary>Input tokens the provider served from its prompt cache (a subset of InputTokens).</summary>
    public long CachedInputTokens { get; init; }
}
public partial class AppState
{
    public string DataSource { get; set; } = "Demo";
    public string Mode { get; set; } = "Recommend";
    public int Revision => Revisions.LastOrDefault()?.Id ?? 0;
    public List<Setting> Settings { get; set; } = [];
    public List<ConfigRevision> Revisions { get; set; } = [];
    /// <summary>Changes to Predbat's own controls (overrides, software, mode), newest last, capped at 500.</summary>
    public List<SettingEvent> SettingEvents { get; set; } = [];
    /// <summary>Set once legacy revisions and experiments created from non-tunable changes have been converted.</summary>
    public int SettingKindsVersion { get; set; }
    public List<Proposal> Proposals { get; set; } = [];
    public List<Experiment> Experiments { get; set; } = [];
    public List<Investigation> Investigations { get; set; } = [];
    public List<Activity> Activities { get; set; } = [];
    public List<UsageRecord> Usage { get; set; } = [];
    public AiPreferences Ai { get; set; } = new();
    public DateTimeOffset? LastCollection { get; set; }
    public DateTimeOffset? LastAnalysis { get; set; }
    public DateTimeOffset? LastAnalysisAttemptAt { get; set; }
    public string? CollectionError { get; set; }
    public string? AnalysisError { get; set; }
    public bool PendingFileReload { get; set; }
    public DateTimeOffset? PendingFileReloadAt { get; set; }
    public string? FileReloadNotes { get; set; }
    public string? LastFileVersionId { get; set; }
    public bool WriteUncertain { get; set; }
}

public record ExperimentDecision(DateTimeOffset At, string Decision, string Notes, int? ExtendedDays = null);
public record RollbackCheck(bool Eligible, bool AutomaticEligible, string Reason);
