namespace Joule;

/// <summary>What a check investigates. Label is the plain description shown instead of the scheduled prompt ("Automatic check of
/// 05:16–08:20: …"); Trigger names the event that started an automatic check; ResumeOf continues a check that didn't finish, from its
/// saved transcript and evidence.</summary>
public record AnalysisRequest(string? Question = null, DateTimeOffset? From = null, DateTimeOffset? To = null, bool Scheduled = false, string? Label = null, string? Trigger = null, string? ResumeOf = null)
{
    public void Validate()
    {
        if (Question?.Length > 2000) throw new DomainException("Keep the investigation question within 2,000 characters.", 400);
        if (Label?.Length > 400 || Trigger?.Length > 200 || ResumeOf?.Length > 64) throw new DomainException("The check's label, trigger or resume reference is too long.", 400);
        if ((From == null) != (To == null) || From >= To || To - From > TimeSpan.FromDays(366) || To > DateTimeOffset.UtcNow.AddDays(1))
            throw new DomainException("Choose both dates in order, covering at most 366 days and ending no later than tomorrow.", 400);
    }
}
public record DocumentationReference(string Id, string Version, string Path, string Url, string ContentSha256, int StartLine, int EndLine, string Excerpt, DateTimeOffset CachedAt);
public record SettingDocumentationReference(string SettingKey, string ReferenceId);
public record DocumentationSearchResult(string Version, IReadOnlyList<DocumentationReference> References, IReadOnlyList<string> Gaps);
public record DocumentationCacheEntry(string Version, string Path, string Url, string ContentSha256, DateTimeOffset CachedAt, string Text);
public record ToolEvidence(string Id, string Kind, string Request, DateTimeOffset RetrievedAt, bool Success, string ResultJson, List<DocumentationReference> SourceReferences, string? Error = null)
{
    /// <summary>Plain description of what was read ("Read Predbat's log for “Warn”, 02:05–03:06"); Request keeps the raw call.</summary>
    public string? Label { get; init; }
}
/// <summary>One thing a check did, in words (Label), with the raw step (Detail) kept for the technical view.</summary>
public sealed record InvestigationStep(DateTimeOffset At, string Kind, string Label, string Detail, string? EvidenceId = null);
public partial class Investigation
{
    public AnalysisRequest Request { get; set; } = new();
    /// <summary>Running (a check in progress, saved after every step), Completed, Failed (the AI service or its answer let the check
    /// down) or Interrupted (stopped by you or by a restart; it can resume from its saved steps).</summary>
    public string Status { get; set; } = "Completed";
    /// <summary>problem, opportunity, no_change, or finding (neutral: the AI didn't say and the server couldn't tell). Null for checks
    /// that didn't finish: a failed run is never a household problem.</summary>
    public string? Verdict { get; set; }
    public List<ToolEvidence> ToolEvidence { get; set; } = [];
    public List<string> EvidenceReferences { get; set; } = [];
    /// <summary>At most 80 characters, plain English, for list rows and the Today card.</summary>
    public string? Headline { get; set; }
    /// <summary>At most 280 characters: what happened and what to do, in plain English.</summary>
    public string? Plain { get; set; }
    /// <summary>For checks that didn't finish: provider_busy, timeout, rate_limited, usage_limit, sign_in, rejected, incomplete,
    /// content_filter, invalid_answer, setup, stopped (by you) or restart (Joule restarted). Null otherwise.</summary>
    public string? FailureKind { get; set; }
    /// <summary>The provider's machine-readable error code ("server_is_overloaded"), when it gave one.</summary>
    public string? ProviderCode { get; set; }
    /// <summary>Request and response ids, for provider support.</summary>
    public string? ProviderReference { get; set; }
    /// <summary>How many times this check has been tried: a resume of one that didn't finish reuses the record and counts again, so the
    /// retry backoff keeps growing (5, 15, then 60 minutes) through a long provider outage.</summary>
    public int Attempts { get; set; } = 1;
    /// <summary>When Joule will try again automatically, for failed and interrupted checks.</summary>
    public DateTimeOffset? NextTryAt { get; set; }
    /// <summary>Server-computed money effect of the cited half-hours in pence: measured net grid cost minus what the frozen plan
    /// expected (positive = cost more than planned; negative = saved or earned). Null when it couldn't be computed.</summary>
    public double? ImpactPence { get; set; }
    /// <summary>action (worth your attention) or info (under 5p either way, or intended behaviour). Null for no_change and unfinished checks.</summary>
    public string? Severity { get; set; }
    /// <summary>Category plus the setting keys and entity ids the finding is about, so a repeat can be recognised.</summary>
    public string? Fingerprint { get; set; }
    /// <summary>Set on a later check that found the same thing as an earlier open finding; the earlier record counts it.</summary>
    public string? RepeatOf { get; set; }
    /// <summary>How many checks have found this (1 for a new finding).</summary>
    public int Occurrences { get; set; } = 1;
    public DateTimeOffset? LastSeenAt { get; set; }
    List<string> watching = [];
    /// <summary>Things Joule's next check will measure itself, instead of to-dos asking you to verify them.</summary>
    public List<string> Watching { get => watching; set => watching = value ?? []; }
    List<InvestigationStep> stepDetails = [];
    public List<InvestigationStep> StepDetails { get => stepDetails; set => stepDetails = value ?? []; }
    public DateTimeOffset? UpdatedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
}
public partial class Proposal
{
    public List<SettingDocumentationReference> DocumentationReferences { get; set; } = [];
    public List<string> EvidenceReferences { get; set; } = [];
    /// <summary>A £/month range when one can honestly be estimated; otherwise SavingEstimate says why not.</summary>
    public double? SavingLowGbpPerMonth { get; set; }
    public double? SavingHighGbpPerMonth { get; set; }
    /// <summary>"Not estimated: …", or how the range was worked out. Always set on AI suggestions.</summary>
    public string? SavingEstimate { get; set; }
    /// <summary>For calibration suggestions: the measured quantity over recent comparable windows next to what Predbat assumes.</summary>
    public CalibrationSeries? Calibration { get; set; }
}
public sealed record CalibrationPoint(DateTimeOffset At, double Value);
/// <summary>What a calibration setting would be tuned to, measured over recent comparable windows, next to what Predbat assumes now.</summary>
public sealed record CalibrationSeries(string Quantity, string Unit, double? Assumed, List<CalibrationPoint> Points, string Note);
public class ReportPreferences
{
    /// <summary>On by default: yesterday's report at 08:00.</summary>
    public bool DailyEnabled { get; set; } = true;
    public bool WeeklyEnabled { get; set; }
    public string TimeZone { get; set; } = "Europe/London";
    public int HourLocal { get; set; } = 8;
    /// <summary>0 for preferences saved before daily reports were on by default (ReportService.RepairAsync turns them on once); 1 after that or once the user saves.</summary>
    public int DefaultsVersion { get; set; }
}
public class EnergyReport
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public string Kind { get; set; } = "Daily";
    public string? TimeZone { get; set; }
    public DateTimeOffset From { get; set; }
    public DateTimeOffset To { get; set; }
    public string Title { get; set; } = "";
    public string Summary { get; set; } = "";
    public bool IsDemo { get; set; }
    public DateTimeOffset? ReadAt { get; set; }
    public EnergySummary? EnergySummary { get; set; }
    public List<EnergySummary> Days { get; set; } = [];
    public List<string> InvestigationIds { get; set; } = [];
    /// <summary>2 when the title and summary use the plain wording and only the period is stored (figures are recomputed on view).</summary>
    public int TextVersion { get; set; }
}
public class InAppNotification
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public DateTimeOffset At { get; set; } = DateTimeOffset.UtcNow;
    public string Title { get; set; } = "";
    public string Message { get; set; } = "";
    public string ReportId { get; set; } = "";
    public DateTimeOffset? ReadAt { get; set; }
}
public partial class AppState
{
    public List<EnergyReport> Reports { get; set; } = [];
    public List<InAppNotification> Notifications { get; set; } = [];
    public ReportPreferences ReportPreferences { get; set; } = new();
}

/// <summary>A measurable hypothesis a check made ("the battery charges at no more than 3.3 kW"). Later checks confirm or refute it, so a
/// contradicted finding is retired explicitly instead of being silently repeated or forgotten.</summary>
public sealed class AiClaim
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];
    public string Text { get; set; } = "";
    /// <summary>How the next check can test it ("max battery charge kW in the next cheap window").</summary>
    public string? Test { get; set; }
    /// <summary>open, confirmed or refuted.</summary>
    public string Status { get; set; } = "open";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public string? InvestigationId { get; set; }
    public DateTimeOffset? ResolvedAt { get; set; }
    public string? ResolvedBy { get; set; }
    public string? Reason { get; set; }
}

/// <summary>The last Predbat MCP discovery, from a manual connection check or the start of a check, so the status card reflects real use.</summary>
public sealed record McpDiscoveryRecord(DateTimeOffset At, bool Configured, bool Connected, int ToolCount, List<string> Tools, string? Error, string Source);

/// <summary>What the scheduler remembers between ticks: quiet checks, Predbat log lines already seen and events already handled.</summary>
public sealed class AiScheduleMemory
{
    public DateTimeOffset? LastQuietCheckAt { get; set; }
    public string? LastQuietCheck { get; set; }
    /// <summary>Normalised Predbat warning and error lines already reported (newest last, bounded).</summary>
    public List<string> SeenLogMessages { get; set; } = [];
    /// <summary>Keys of events that already started (or were folded into) a check, with when they were handled.</summary>
    public Dictionary<string, DateTimeOffset> Handled { get; set; } = [];
    public string? LastTrigger { get; set; }
    public DateTimeOffset? LastTriggerAt { get; set; }
    /// <summary>Events found by a quiet check (new Predbat warnings) waiting for the minimum spacing before a full check.</summary>
    public List<ScheduleSignal> Pending { get; set; } = [];
}
/// <summary>Something that should start a full AI check: a charge or export window ending, a new Predbat warning, the battery off plan,
/// a sensor down, a digest time or the heartbeat. Key identifies the event so it starts at most one check.</summary>
public sealed record ScheduleSignal(string Key, string Kind, DateTimeOffset DueAt, string Reason);

public partial class AppState
{
    /// <summary>max_savings (default), limit_cycling or self_sufficiency; see HouseholdObjective.</summary>
    public string HouseholdObjective { get; set; } = Joule.HouseholdObjective.MaxSavings;
    public List<AiClaim> Claims { get; set; } = [];
    public McpDiscoveryRecord? McpDiscovery { get; set; }
    public AiScheduleMemory AiSchedule { get; set; } = new();
    /// <summary>One-off data repairs already applied (by name).</summary>
    public List<string> AiRepairs { get; set; } = [];
}
