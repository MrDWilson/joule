using System.Text.RegularExpressions;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Joule;

// The slim /api/state?view=header contract. Mirrored in web/src/types.ts (StateHeader and friends) and pinned by
// StateHeaderContractTests and web/e2e/api-contract.spec.ts. Add fields at the end; never rename or remove one.

/// <summary>An investigation as a list row: enough to render a findings inbox without steps, evidence or transcripts.</summary>
public record InvestigationSummary(string Id, DateTimeOffset At, string Status, string? Verdict, string Title, string Headline, string? Plain,
    string Category, string Confidence, string Provider, bool Scheduled, DateTimeOffset? DismissedAt, int OpenFollowUps, int OpenFileChanges,
    int PendingProposals, double? ImpactPence);
public record RunStripEntry(string Id, DateTimeOffset At, string Status, string? Verdict);
/// <summary>A setting without its option list or long description. The full catalogue is GET /api/settings.</summary>
public record SettingIndexEntry(string Key, string Name, string Category, string Value, string Type, string Risk, bool Editable, bool AutoAllowed, string EntityId);
public record HeaderCounts(int Investigations, long ArchivedInvestigations, int PendingProposals, int OpenFollowUps, int OpenFileChanges,
    int OpenExperiments, int UnreadNotifications, int UnreadReports, int Activities);
public record HomeAssistantHealth(bool Configured, DateTimeOffset? LastCollection, string? Error, string[] Unexpected, string[] MissingMappings);
/// <summary>Timestamps rather than ages, so an unchanged system keeps an unchanged ETag; the UI derives "5 min ago".</summary>
public record HeaderHealth(DateTimeOffset? PredbatLastAt, string? CollectionError, string? AnalysisError, bool WriteUncertain, bool PendingFileReload,
    string Writes, string AiStatus, HomeAssistantHealth? HomeAssistant);
public record PredbatStatusHeader(string? Version, string? Mode, string? CurrentAction, double? Reserve, DateTimeOffset? PlanAt, DateTimeOffset? PlanCollectedAt);
public record SetupStep(string Key, string Label, bool Done, bool Required);
public record SetupProgress(int Done, int Total, bool RequiredDone, List<SetupStep> Steps);
/// <summary>AuthMode is AccessKey or None, so the UI can warn when the app itself has no sign-in.</summary>
public record HeaderConnection(bool Demo, bool WritesEnabled, bool PredbatConfigured, string AuthMode);
public record HeaderAi(bool Running, InvestigationScheduleStatus Schedule, bool ApiConfigured, bool ChatGptConnected, string? ChatGptEmail,
    bool ChatGptLocalSignInAvailable, AiPreferences Preferences, McpDiscoveryRecord? Mcp = null, string? Objective = null);
public record StateHeader(int SchemaVersion, string Version, string DataSource, string Mode, int Revision, DateTimeOffset? LastCollection, DateTimeOffset? LastAnalysis,
    DateTimeOffset? LastAnalysisAttemptAt, HeaderConnection Connection, HeaderAi Ai, HeaderHealth Health, PredbatStatusHeader PredbatStatus, HeaderCounts Counts,
    PlanSnapshot? Plan, List<InvestigationSummary> LatestInvestigations, InvestigationSummary? TopFinding48h, List<RunStripEntry> TodayRunStrip,
    List<Proposal> Proposals, List<InAppNotification> Notifications, List<SettingIndexEntry> Settings, List<MemoryFact> Memory,
    ReportSchedulesStatus ReportSchedules, ReportPreferences ReportPreferences, SetupProgress SetupProgress, Activity? LastActivity, string TimeZone);

/// <summary>Everything outside AppState that the projections need, gathered by the endpoint.</summary>
public record StateContext(bool Demo, bool WritesEnabled, bool PredbatConfigured, bool AiRunning, InvestigationScheduleStatus Schedule, bool ApiConfigured,
    bool ChatGptConnected, string? ChatGptEmail, bool ChatGptLocalSignInAvailable, PlanSnapshot? Plan, List<MemoryFact> Memory, long ArchivedInvestigations,
    TelemetryStatus? Telemetry, bool McpConfigured, string TimeZone, DateTimeOffset Now, string AuthMode = "AccessKey");

public static class StateProjection
{
    public const int SchemaVersion = 1, LatestInvestigations = 5, MaxNotifications = 20;

    /// <summary>The legacy full payload (?full=1, and the default until the UI has moved to the header). The unused "plans" list is gone and the ChatGPT email is masked.</summary>
    public static object Full(AppState state, StateContext c, ReportSchedulesStatus reportSchedules) => new
    {
        state,
        memory = c.Memory,
        plan = c.Plan,
        reportSchedules,
        connection = new HeaderConnection(c.Demo, c.Demo || c.WritesEnabled, c.PredbatConfigured, c.AuthMode),
        ai = new { running = c.AiRunning, schedule = c.Schedule, apiConfigured = c.ApiConfigured, chatGptConnected = c.ChatGptConnected, chatGptEmail = WebSecurity.MaskEmail(c.ChatGptEmail), chatGptLocalSignInAvailable = c.ChatGptLocalSignInAvailable, mcp = state.McpDiscovery, objective = state.HouseholdObjective }
    };

    public static StateHeader Header(AppState state, StateContext c, ReportSchedulesStatus reportSchedules)
    {
        var ordered = state.Investigations.OrderByDescending(i => i.At).ThenByDescending(i => i.Id, StringComparer.Ordinal).ToList();
        var pendingByInvestigation = state.Proposals.Where(p => p.Status == "Pending").GroupBy(p => p.InvestigationId).ToDictionary(g => g.Key, g => g.Count());
        InvestigationSummary Summary(Investigation i) => Summarise(i, pendingByInvestigation.GetValueOrDefault(i.Id));

        var tz = FindZone(c.TimeZone);
        var today = TimeZoneInfo.ConvertTime(c.Now, tz).Date;
        var strip = ordered.Where(i => TimeZoneInfo.ConvertTime(i.At, tz).Date == today).OrderBy(i => i.At).Select(i => new RunStripEntry(i.Id, i.At, i.Status, i.Verdict)).ToList();
        var top = ordered.Where(i => i.At >= c.Now.AddHours(-48) && i.Status == "Completed" && i.DismissedAt is null && i.RepeatOf is null && i.Verdict is "problem" or "opportunity")
            .Select(Summary).OrderByDescending(s => s.ImpactPence ?? double.MinValue).ThenByDescending(s => s.Verdict == "problem").ThenByDescending(s => s.At).FirstOrDefault();

        var openFollowUps = state.Investigations.Sum(i => i.NextSteps.Count(s => (s.Status ?? "open") == "open"));
        var openFileChanges = state.Investigations.Sum(i => i.FileChanges.Count(InvestigationFileChanges.IsOpen));
        var counts = new HeaderCounts(state.Investigations.Count + (int)Math.Min(int.MaxValue, c.ArchivedInvestigations), c.ArchivedInvestigations,
            state.Proposals.Count(p => p.Status == "Pending"), openFollowUps, openFileChanges, state.Experiments.Count(ChangeEngine.IsOpen),
            state.Notifications.Count(n => n.ReadAt is null), state.Reports.Count(r => r.ReadAt is null), state.Activities.Count);

        var writes = c.Demo ? "demo" : state.WriteUncertain ? "blocked" : c.WritesEnabled ? "enabled" : "disabled";
        var health = new HeaderHealth(state.LastCollection, state.CollectionError, state.AnalysisError, state.WriteUncertain, state.PendingFileReload, writes,
            AiStatus(c, state), c.Telemetry is { } t ? new HomeAssistantHealth(t.Configured, t.LastCollection, t.Error,
                t.LatestReadings.Where(x => x.Value.Status is not ("observed" or "idle")).Select(x => x.Key).Order(StringComparer.Ordinal).ToArray(), t.MissingMappings) : null);

        string? Setting(string key) => state.Settings.FirstOrDefault(s => s.Key == key)?.Value;
        var current = c.Plan?.Slots.LastOrDefault(s => s.Time <= c.Now && s.Time.AddMinutes(Math.Max(1, s.DurationMinutes)) > c.Now);
        // Predbat publishes its running version as the current option of select.predbat_update ("v9.3.5 Bug fixes ...")
        // and the floor it keeps in the battery as input_number.predbat_set_reserve_min (percent).
        var predbat = new PredbatStatusHeader(PredbatVersion(Setting("version") ?? Setting("update")), Setting("mode"), current?.Action,
            double.TryParse(Setting("set_reserve_min"), NumberStyles.Float, CultureInfo.InvariantCulture, out var reserve) && double.IsFinite(reserve) && reserve is >= 0 and <= 100 ? reserve : null,
            c.Plan?.At, c.Plan?.CollectedAt);

        return new StateHeader(SchemaVersion, AppVersion.Current, state.DataSource, state.Mode, state.Revision, state.LastCollection, state.LastAnalysis, state.LastAnalysisAttemptAt,
            new HeaderConnection(c.Demo, c.Demo || c.WritesEnabled, c.PredbatConfigured, c.AuthMode),
            new HeaderAi(c.AiRunning, c.Schedule, c.ApiConfigured, c.ChatGptConnected, WebSecurity.MaskEmail(c.ChatGptEmail), c.ChatGptLocalSignInAvailable, state.Ai, state.McpDiscovery, state.HouseholdObjective),
            health, predbat, counts, c.Plan, ordered.Take(LatestInvestigations).Select(Summary).ToList(), top, strip,
            state.Proposals.Where(p => p.Status == "Pending").OrderByDescending(p => p.CreatedAt).ToList(),
            state.Notifications.Where(n => n.ReadAt is null).OrderByDescending(n => n.At).Take(MaxNotifications).ToList(),
            state.Settings.Select(s => new SettingIndexEntry(s.Key, s.Name, s.Category, s.Value, s.Type, s.Risk, s.Editable, s.AutoAllowed, s.EntityId)).ToList(),
            c.Memory, reportSchedules, state.ReportPreferences, Setup(state, c), state.Activities.LastOrDefault(), c.TimeZone);
    }

    public static InvestigationSummary Summarise(Investigation i, int pendingProposals)
    {
        // headline, plain and impactPence are added to Investigation by the AI-quality work; read them by name so this
        // projection keeps compiling before and after that lands, and falls back to the title and summary.
        var node = JsonSerializer.SerializeToNode(i, JsonDefaults.Options) as JsonObject;
        string? Text(string name) => node?[name] is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s : null;
        double? Number(string name) => node?[name] is JsonValue v && v.TryGetValue<double>(out var d) && double.IsFinite(d) ? d : null;
        return new(i.Id, i.At, i.Status, i.Verdict, i.Title, Text("headline") ?? i.Title, Text("plain") ?? Shorten(i.Summary, 280), i.Category, i.Confidence, i.Provider,
            i.Request.Scheduled, i.DismissedAt, i.NextSteps.Count(s => (s.Status ?? "open") == "open"), i.FileChanges.Count(InvestigationFileChanges.IsOpen), pendingProposals,
            Number("impactPence"));
    }

    /// <summary>Cuts at a word boundary and adds an ellipsis; null for empty text.</summary>
    public static string? Shorten(string? text, int max)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        text = text.Trim();
        if (text.Length <= max) return text;
        var cut = text.LastIndexOf(' ', max - 1);
        return text[..(cut > max / 2 ? cut : max - 1)].TrimEnd(' ', ',', ';', ':', '.') + "…";
    }

    static string AiStatus(StateContext c, AppState state) =>
        c.AiRunning ? "running"
        : c.Schedule.State is "ProviderUnavailable" or "InvalidPreferences" ? "needs_setup"
        : state.Usage.OrderByDescending(u => u.At).FirstOrDefault()?.Status == "Failed" ? "last_run_failed"
        : c.Schedule.State == "Disabled" ? "off"
        : "ready";

    internal static SetupProgress Setup(AppState state, StateContext c)
    {
        var prefs = state.Ai;
        var aiReady = prefs.Provider switch
        {
            "Demo" => c.Demo,
            "Api" => c.ApiConfigured && !string.IsNullOrWhiteSpace(prefs.Model),
            "ChatGpt" => c.ChatGptConnected && !string.IsNullOrWhiteSpace(prefs.Model),
            _ => false
        };
        var energyMapped = c.Demo || c.Telemetry is { Configured: true } t && !t.MissingMappings.Contains("load");
        List<SetupStep> steps =
        [
            new("predbat", "Connect to Predbat", c.Demo || c.PredbatConfigured, true),
            new("collecting", "Read Predbat's plan", state.LastCollection is not null && state.CollectionError is null, true),
            new("meters", "Map your Home Assistant meters", energyMapped, true),
            new("ai", "Choose an AI provider", aiReady, true),
            new("mcp", "Let the AI read Predbat's logs (MCP)", c.Demo || c.McpConfigured, false),
            new("reviews", "Turn on automatic reviews", prefs.Scheduled, false),
            new("writes", "Allow Joule to change Predbat", c.Demo || c.WritesEnabled, false)
        ];
        return new(steps.Count(s => s.Done), steps.Count, steps.Where(s => s.Required).All(s => s.Done), steps);
    }

    /// <summary>"v9.3.5 Bug fixes cloud inverters &amp; Misc" → "9.3.5"; null when the text holds no version.</summary>
    public static string? PredbatVersion(string? text) =>
        text is null ? null : Regex.Match(text, @"\bv?(\d+(?:\.\d+){1,3})\b") is { Success: true } m ? m.Groups[1].Value : null;

    static TimeZoneInfo FindZone(string id)
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById(id); } catch (Exception e) when (e is TimeZoneNotFoundException or InvalidTimeZoneException) { return TimeZoneInfo.Utc; }
    }
}
