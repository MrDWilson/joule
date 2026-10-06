using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Joule;

/// <summary>
/// The automatic-check schedule as the UI shows it. State: Disabled, Running, InvalidPreferences, ProviderUnavailable, Paused (allowance
/// used up until NextRunAt), DailyLimit, Waiting or Due. Trigger says why the next (or due) check runs; NextCheckAt is the next free
/// quiet check, which reads Joule's own data and Predbat's log and only calls the AI when something is new.
/// </summary>
public record InvestigationScheduleStatus(bool Enabled, string State, string Reason, DateTimeOffset? NextRunAt,
    DateTimeOffset? LastAttemptAt, DateTimeOffset? LastCompletedAt, DateTimeOffset? LastRunRecordedAt,
    int RunsToday, int MaxRunsPerDay, int IntervalMinutes)
{
    public string? Trigger { get; init; }
    public string? TriggerKind { get; init; }
    /// <summary>The check to continue when the due run resumes one that didn't finish.</summary>
    public string? ResumeId { get; init; }
    public DateTimeOffset? NextCheckAt { get; init; }
    public DateTimeOffset? LastQuietCheckAt { get; init; }
    public string? LastQuietCheck { get; init; }
    /// <summary>Checks today that never got an answer from the AI (they don't count against the allowance).</summary>
    public int FailedToday { get; init; }
}

/// <summary>
/// Event-led automatic checks. A full AI check runs about five minutes after each charge or export window ends, on a new Predbat warning
/// or error, when the battery is more than 10 points off plan, when a sensor has been down for 30 minutes, at the 07:30 and 21:30 digests
/// (home time), and at least every three hours. The saved interval is the minimum spacing between AI checks and the cadence of free quiet
/// checks in between. A check that failed before the AI answered retries after 5, 15 and then 60 minutes and doesn't use the allowance,
/// which is counted per local day.
/// </summary>
public sealed class InvestigationScheduler(StateService state, AnalysisService analysis, AiModelClient model, ChatGptAuth auth, DataStore? db = null, IPredbatMcpClient? mcp = null, TelemetryCollectionService? telemetry = null)
{
    public static readonly TimeSpan Heartbeat = TimeSpan.FromHours(3), WindowSettle = TimeSpan.FromMinutes(5), OutageThreshold = TimeSpan.FromMinutes(30), SignalHorizon = TimeSpan.FromHours(6);
    public static readonly TimeSpan[] RetryBackoff = [TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(60)];
    public static readonly TimeSpan[] DigestTimes = [new(7, 30, 0), new(21, 30, 0)];
    public const double SocMissPoints = 10;
    readonly object gate = new();
    DateTimeOffset? lastAttempt;

    public InvestigationScheduleStatus Status(DateTimeOffset now)
    {
        var signals = Signals(now);
        lock (gate) return BuildStatus(state.Read(false), now, analysis.Running, state.Demo, model.ApiConfigured, auth.Connected, lastAttempt, analysis.Zone, signals);
    }

    /// <summary>Starts a full AI check when one is due. Quiet checks are TickAsync's job.</summary>
    public bool TryStart(DateTimeOffset now, CancellationToken ct)
    {
        var signals = Signals(now);
        lock (gate)
        {
            var s = state.Read(false);
            var status = BuildStatus(s, now, analysis.Running, state.Demo, model.ApiConfigured, auth.Connected, lastAttempt, analysis.Zone, signals);
            if (status.State != "Due") return false;
            if (!analysis.StartScheduled(ct, status.Trigger, status.ResumeId)) return false;
            lastAttempt = now;
            var handled = signals.Concat(s.AiSchedule.Pending).Where(x => x.DueAt <= now).Select(x => x.Key).ToList();
            _ = state.MutateAsync(x =>
            {
                foreach (var key in handled) x.AiSchedule.Handled[key] = now;
                x.AiSchedule.Pending.RemoveAll(p => p.DueAt <= now);
                foreach (var old in x.AiSchedule.Handled.Where(h => h.Value < now.AddDays(-2)).Select(h => h.Key).ToList()) x.AiSchedule.Handled.Remove(old);
                x.AiSchedule.LastTrigger = status.Trigger; x.AiSchedule.LastTriggerAt = now;
            }, CancellationToken.None);
            return true;
        }
    }

    /// <summary>One scheduler tick: start a due AI check, or run a free quiet check when its time has come.</summary>
    public async Task<string> TickAsync(DateTimeOffset now, CancellationToken ct)
    {
        if (TryStart(now, ct)) return "started";
        var status = Status(now);
        if (status.NextCheckAt is not { } next || next > now) return "waiting";
        await QuietCheckAsync(now, ct);
        return TryStart(now, ct) ? "started" : "checked";
    }

    /// <summary>
    /// Reads Predbat's warnings since the last check and compares the battery with the plan, without the AI. A new warning or error is
    /// queued as a trigger; otherwise a visible "checked, nothing new" activity is recorded so the timeline is never silently empty.
    /// </summary>
    public async Task QuietCheckAsync(DateTimeOffset now, CancellationToken ct)
    {
        var s = state.Read(false); var zone = analysis.Zone;
        var since = s.AiSchedule.LastQuietCheckAt ?? s.LastAnalysisAttemptAt ?? now.AddHours(-1);
        if (now - since > TimeSpan.FromHours(6)) since = now.AddHours(-6);
        var log = await ReadWarningsAsync(since, now, zone, ct);
        var fresh = log.Messages.Where(m => !s.AiSchedule.SeenLogMessages.Contains(Normalise(m))).Distinct().ToList();
        var battery = BatteryLine(now, zone);
        var line = fresh.Count > 0
            ? $"Checked {InvestigationQuality.Span(since, now, zone)}: Predbat logged {fresh.Count} new warning{(fresh.Count == 1 ? "" : "s")} (“{InvestigationQuality.Shorten(fresh[0], 90)}”), so an AI check follows."
            : $"Checked {InvestigationQuality.Span(since, now, zone)}: nothing new. {battery}{(log.Available ? "No new Predbat warnings." : "Predbat's log wasn't available.")} The AI wasn't needed.";
        await state.MutateAsync(x =>
        {
            x.AiSchedule.LastQuietCheckAt = now; x.AiSchedule.LastQuietCheck = line;
            foreach (var m in fresh) x.AiSchedule.SeenLogMessages.Add(Normalise(m));
            if (x.AiSchedule.SeenLogMessages.Count > 300) x.AiSchedule.SeenLogMessages = x.AiSchedule.SeenLogMessages.TakeLast(300).ToList();
            if (fresh.Count > 0) x.AiSchedule.Pending.Add(new($"warn:{Normalise(fresh[0]).GetHashCode():x8}:{now:O}", "predbat_warning", now, $"new Predbat warning: {InvestigationQuality.Shorten(fresh[0], 80)}"));
            ChangeEngine.Log(x, "check", line);
        }, ct);
    }

    string BatteryLine(DateTimeOffset now, TimeZoneInfo zone)
    {
        if (db is null) return "";
        try
        {
            var last = db.ReadPlanVsActual(now.AddHours(-1), now).LastOrDefault(x => x.SocPlannedPercent is not null && x.SocActualStartPercent is not null);
            return last is null ? "" : $"Battery {Math.Round(last.SocActualStartPercent!.Value)}% at {InvestigationBrief.Clock(last.Time, zone)} against {Math.Round(last.SocPlannedPercent!.Value)}% planned. ";
        }
        catch (Exception e) when (e is DomainException or InvalidOperationException) { return ""; }
    }

    sealed record LogDigest(bool Available, List<string> Messages);
    async Task<LogDigest> ReadWarningsAsync(DateTimeOffset since, DateTimeOffset now, TimeZoneInfo zone, CancellationToken ct)
    {
        if (mcp is null || !mcp.Configured) return new(false, []);
        try
        {
            string Local(DateTimeOffset t) => TimeZoneInfo.ConvertTime(t, zone).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
            using var args = JsonDocument.Parse(JsonSerializer.Serialize(new { filter = "warnings", start = Local(since), end = Local(now), max_lines = 200 }));
            var result = await mcp.CallReadOnlyAsync("get_log", args.RootElement, ct);
            if (!result.Success) return new(false, []);
            return new(true, WarningLines(InvestigationContext.Text(result.ResultJson).Content));
        }
        catch (Exception e) when (e is not OperationCanceledException) { return new(false, []); }
    }

    /// <summary>Warning and error lines from Predbat's log text.</summary>
    public static List<string> WarningLines(string text) =>
        text.Split('\n').Select(l => Regex.Replace(l.Trim(), @"^[a-z_]{1,24}:\s+(?=\S)", "")).Where(l => Regex.IsMatch(l, @"\b(warn(ing)?|error)\b", RegexOptions.IgnoreCase) && l.Length > 8).Take(200).ToList();

    /// <summary>A log line without its timestamp and numbers, so the same warning repeated with new values counts once.</summary>
    public static string Normalise(string line) => Regex.Replace(Regex.Replace(line, @"^\S*\d{4}-\d{2}-\d{2}[ T][\d:.,]+\S*\s*", ""), @"\d+(\.\d+)?", "#").Trim().ToLowerInvariant();

    /// <summary>How long one reading of the events is shared. Status runs on every /api/state poll (each open tab, every 10 s) and the
    /// worker asks two or three times a minute; the plan-versus-actual queries and the telemetry scan run at most once in this window.</summary>
    public static readonly TimeSpan SignalCacheFor = TimeSpan.FromSeconds(60);
    readonly object signalGate = new();
    (DateTimeOffset At, IReadOnlyList<ScheduleSignal> Signals)? signalCache;
    /// <summary>How many times the events were read from the database (for tests).</summary>
    public int SignalReads { get; private set; }

    /// <summary>The events in Joule's own data: window ends, battery off plan and sensor outages (warnings come from quiet checks).
    /// Shared for SignalCacheFor; one caller reads while concurrent callers wait for that result.</summary>
    public IReadOnlyList<ScheduleSignal> Signals(DateTimeOffset now)
    {
        lock (signalGate)
        {
            if (signalCache is { } c && now >= c.At && now - c.At < SignalCacheFor) return c.Signals;
            var fresh = ReadSignals(now);
            signalCache = (now, fresh);
            return fresh;
        }
    }

    IReadOnlyList<ScheduleSignal> ReadSignals(DateTimeOffset now)
    {
        if (db is null) return [];
        SignalReads++;
        try
        {
            var recent = db.ReadPlanVsActual(now - SignalHorizon, now.AddMinutes(30));
            return ComputeSignals(recent, db.GetPlan(), Try(() => telemetry?.Status()), now, analysis.Zone);
        }
        catch (Exception e) when (e is DomainException or InvalidOperationException or ArgumentException) { return []; }
    }
    static T? Try<T>(Func<T?> f) where T : class { try { return f(); } catch (Exception e) when (e is not OperationCanceledException) { return null; } }

    /// <summary>
    /// Pure event detection. Past half-hours use the plan as it was just before each started; future ones use the current plan, so the
    /// next window end is known in advance. Charge windows are any charge-side plan (charge, hold, freeze charge); export windows are
    /// planned battery exports.
    /// </summary>
    public static List<ScheduleSignal> ComputeSignals(IReadOnlyList<PlanVsActualSlot> recent, PlanSnapshot? plan, TelemetryStatus? telemetry, DateTimeOffset now, TimeZoneInfo zone)
    {
        var signals = new List<ScheduleSignal>();
        var timeline = new SortedDictionary<DateTimeOffset, (string? Key, int Minutes)>();
        foreach (var x in recent) if (x.Time < now) timeline[x.Time] = (x.ActionKey ?? x.PlannedAction, x.DurationMinutes);
        foreach (var x in plan?.Slots ?? []) if (x.Time.AddMinutes(x.DurationMinutes) > now && !timeline.ContainsKey(x.Time) && x.Time < now.AddHours(24)) timeline[x.Time] = (x.ActionKey ?? PredbatGlossary.Key(x.Action), x.DurationMinutes);
        string? Side(string? key) => key is null ? null : key == "charge-export" ? "charge" : PredbatGlossary.ChargeSide.Contains(key) ? "charge" : key == "export" ? "export" : null;
        string? previous = null; DateTimeOffset previousEnd = DateTimeOffset.MinValue;
        foreach (var (time, slot) in timeline)
        {
            var side = Side(slot.Key);
            if (previous != null && (side != previous || time != previousEnd))
                signals.Add(new($"window:{previous}:{previousEnd:O}", "window_end", previousEnd + WindowSettle, $"the {InvestigationBrief.Clock(previousEnd, zone)} end of {(previous == "charge" ? "a charge" : "an export")} window"));
            previous = side; previousEnd = time.AddMinutes(slot.Minutes);
        }
        if (previous != null) signals.Add(new($"window:{previous}:{previousEnd:O}", "window_end", previousEnd + WindowSettle, $"the {InvestigationBrief.Clock(previousEnd, zone)} end of {(previous == "charge" ? "a charge" : "an export")} window"));
        // A continuing miss (back-to-back half-hours off plan in the same direction) is one event, keyed at its first half-hour, so a battery
        // that stays off plan doesn't start a new AI check every half-hour. A run already under way at the edge of the horizon began
        // earlier and was flagged then.
        var horizonEdge = recent.Count == 0 ? now : recent.Min(x => x.Time);
        int? runSign = null; DateTimeOffset runEnd = DateTimeOffset.MinValue;
        foreach (var x in recent.Where(x => x.Time <= now && x.SocPlannedPercent is not null && x.SocActualStartPercent is not null).OrderBy(x => x.Time))
        {
            var miss = x.SocActualStartPercent!.Value - x.SocPlannedPercent!.Value;
            var sign = Math.Abs(miss) > SocMissPoints ? Math.Sign(miss) : 0;
            var continues = sign != 0 && runSign == sign && x.Time <= runEnd;
            if (sign != 0 && !continues && !(x.Time == horizonEdge && x.Time < now - SignalHorizon + TimeSpan.FromMinutes(30)))
                signals.Add(new($"soc:{x.Time:O}", "soc_miss", x.Time, $"the battery {Math.Abs(Math.Round(miss))} points {(miss < 0 ? "below" : "above")} plan at {InvestigationBrief.Clock(x.Time, zone)}"));
            runSign = sign == 0 ? null : sign; runEnd = x.Time.AddMinutes(Math.Max(1, x.DurationMinutes));
        }
        foreach (var issue in telemetry?.Issues ?? [])
            if (issue.Since is { } since && now - since >= OutageThreshold)
                signals.Add(new($"sensor:{issue.Metric}:{since:O}", "sensor_outage", since + OutageThreshold, $"the {InvestigationBrief.MeterName(issue.Metric).ToLowerInvariant()} down since {InvestigationBrief.Clock(since, zone)}"));
        return signals.Where(x => x.DueAt >= now - SignalHorizon).ToList();
    }

    /// <summary>Whether a usage record counts against the daily allowance: any finished check, or a failed one that got an AI answer.</summary>
    public static bool Counts(UsageRecord u) => u.Status == "Completed" || u.InputTokens + u.OutputTokens > 0;
    /// <summary>Checks counted against today's allowance, where today is the home's local day.</summary>
    public static int RunsToday(IEnumerable<UsageRecord> usage, DateTimeOffset now, TimeZoneInfo zone)
    {
        var today = TimeZoneInfo.ConvertTime(now, zone).Date;
        return usage.Count(u => Counts(u) && TimeZoneInfo.ConvertTime(u.At, zone).Date == today);
    }
    static DateTimeOffset NextLocalMidnight(DateTimeOffset now, TimeZoneInfo zone) => CivilTime.FirstValidInstant(TimeZoneInfo.ConvertTime(now, zone).Date.AddDays(1), zone);

    /// <summary>The digest instants (07:30 and 21:30 home time) from yesterday to tomorrow, correct across clock changes.</summary>
    public static IEnumerable<DateTimeOffset> DigestInstants(DateTimeOffset now, TimeZoneInfo zone)
    {
        var today = TimeZoneInfo.ConvertTime(now, zone).Date;
        for (var d = -1; d <= 1; d++)
            foreach (var at in DigestTimes) yield return CivilTime.FirstValidInstant(today.AddDays(d) + at, zone);
    }

    /// <summary>
    /// When a check that didn't finish should be retried automatically: 5, 15, then 60 minutes after consecutive failures, counting every
    /// try of a resumed check (currentId and attempts describe the check that just failed); a restart
    /// resumes at once; a used-up allowance waits for its reset (an hour when unknown); a sign-in problem or a setup error is not retried.
    /// </summary>
    public static DateTimeOffset? NextRetryAt(AppState s, string kind, bool retryable, DateTimeOffset? resetsAt, DateTimeOffset now, string? currentId = null, int attempts = 1)
    {
        if (kind is "restart") return now;
        if (kind is "usage_limit" or "rate_limited") return resetsAt is { } r && r > now ? r : now + RetryBackoff[^1];
        if (!retryable || kind is "sign_in" or "setup" or "stopped") return null;
        // This check's own tries (a resume reuses its record) plus the other checks that failed in a row just before it.
        var consecutive = Math.Max(1, attempts) + s.Investigations.Where(i => i.Id != currentId).OrderByDescending(i => i.At)
            .TakeWhile(i => i.Status is "Failed" or "Interrupted" or "Running").Where(i => i.Status != "Running").Sum(i => Math.Max(1, i.Attempts));
        return now + RetryBackoff[Math.Min(consecutive, RetryBackoff.Length) - 1];
    }

    public static InvestigationScheduleStatus BuildStatus(AppState snapshot, DateTimeOffset now, bool running, bool demo, bool apiConfigured, bool chatGptConnected, DateTimeOffset? inProcessAttempt = null, TimeZoneInfo? zone = null, IReadOnlyList<ScheduleSignal>? signals = null)
    {
        zone ??= London();
        var prefs = snapshot.Ai;
        var latestUsage = snapshot.Usage.OrderByDescending(x => x.At).FirstOrDefault();
        var lastAttemptAt = Latest(snapshot.LastAnalysisAttemptAt, inProcessAttempt);
        var runsToday = RunsToday(snapshot.Usage, now, zone);
        var localToday = TimeZoneInfo.ConvertTime(now, zone).Date;
        var failedToday = snapshot.Usage.Count(u => !Counts(u) && TimeZoneInfo.ConvertTime(u.At, zone).Date == localToday);
        var interval = TimeSpan.FromMinutes(Math.Clamp(prefs.IntervalMinutes, 15, 1440));
        var nextCheck = (snapshot.AiSchedule.LastQuietCheckAt is { } q ? Latest(q, lastAttemptAt) : lastAttemptAt) is { } basis ? basis + interval : now;
        InvestigationScheduleStatus Result(string status, string reason, DateTimeOffset? next = null, string? trigger = null, string? kind = null, string? resume = null) =>
            new(prefs.Scheduled, status, reason, next, lastAttemptAt, snapshot.LastAnalysis, latestUsage?.At, runsToday, prefs.MaxRunsPerDay, prefs.IntervalMinutes)
            {
                Trigger = trigger, TriggerKind = kind, ResumeId = resume, NextCheckAt = status is "Waiting" or "DailyLimit" or "Paused" ? nextCheck : null,
                LastQuietCheckAt = snapshot.AiSchedule.LastQuietCheckAt, LastQuietCheck = snapshot.AiSchedule.LastQuietCheck, FailedToday = failedToday
            };
        string Clock(DateTimeOffset t) => InvestigationBrief.Clock(t, zone);

        if (!prefs.Scheduled) return Result("Disabled", "Automatic AI checks are off. Turn on automatic checks in AI settings to opt in.");
        if (running) return Result("Running", "A check is running. The next one is worked out when it finishes.");
        if (prefs.IntervalMinutes is < 15 or > 1440 || prefs.MaxRunsPerDay is < 1 or > 96)
            return Result("InvalidPreferences", "Save a valid check spacing and daily allowance in AI settings.");
        if (prefs.Provider == "Demo" && !demo) return Result("ProviderUnavailable", "Scripted demo checks cannot run against live data. Choose a live AI provider in AI settings.");
        if (prefs.Provider is not ("Demo" or "Api" or "ChatGpt")) return Result("ProviderUnavailable", "Choose a supported AI provider in AI settings.");
        if (prefs.Provider != "Demo" && string.IsNullOrWhiteSpace(prefs.Model)) return Result("ProviderUnavailable", "Choose a model in AI settings before automatic checks can run.");
        if (prefs.Provider == "Api" && !apiConfigured) return Result("ProviderUnavailable", "The AI service has no API key configured. Add one before automatic checks can run.");
        if (prefs.Provider == "ChatGpt" && !chatGptConnected) return Result("ProviderUnavailable", "Connect ChatGPT in AI settings before automatic checks can run.");

        // A check that didn't finish decides what happens next: resume after a restart, back off after a provider failure, pause for an allowance.
        var latest = snapshot.Investigations.Where(i => i.Status != "Running").OrderByDescending(i => i.At).FirstOrDefault();
        if (latest is { Status: "Failed" or "Interrupted" } failed && failed.FailureKind is not ("stopped" or "setup" or null))
        {
            if (failed.FailureKind == "sign_in" && (failed.FinishedAt ?? failed.At) >= (lastAttemptAt ?? DateTimeOffset.MinValue).AddSeconds(-5))
                return Result("ProviderUnavailable", "ChatGPT didn't accept Joule's sign-in. Reconnect it in AI settings; automatic checks wait until then.");
            if (failed.NextTryAt is { } retryAt && retryAt > now)
                return Result(failed.FailureKind is "usage_limit" ? "Paused" : "Waiting",
                    failed.FailureKind is "usage_limit" ? $"The AI allowance is used up. Checks resume at {Clock(retryAt)}." : $"The last check didn't finish ({failed.Headline ?? "AI error"}). Trying again at {Clock(retryAt)}; it doesn't use today's allowance.", retryAt, "retry", "retry", Resumable(failed));
            if (failed.NextTryAt is not null && runsToday < prefs.MaxRunsPerDay)
            {
                var yours = !failed.Request.Scheduled && failed.Request.Question is { Length: > 0 };
                return Result("Due", failed.FailureKind == "restart" ? $"Resuming the {(yours ? "question" : "check")} a restart interrupted." : yours ? "Retrying your question that didn't finish." : "Retrying the check that didn't finish.", failed.NextTryAt,
                    (failed.FailureKind == "restart" ? "resuming after a restart" : "retry") + (yours ? " of your question" : ""), failed.FailureKind == "restart" ? "resume" : "retry", Resumable(failed));
            }
        }
        if (runsToday >= prefs.MaxRunsPerDay)
            return Result("DailyLimit", $"Today's {prefs.MaxRunsPerDay} AI checks are used. The allowance resets at midnight; quiet checks continue without the AI.", NextLocalMidnight(now, zone));

        // Events. A check that started after an event's due time covers it.
        var covered = lastAttemptAt ?? DateTimeOffset.MinValue;
        var lastRun = Latest(snapshot.LastAnalysis, snapshot.Usage.Where(Counts).Select(u => (DateTimeOffset?)u.At).Max(), lastAttemptAt);
        var spacingUntil = lastRun is { } lr ? lr + interval : now;
        var all = new List<ScheduleSignal>(signals ?? []);
        all.AddRange(snapshot.AiSchedule.Pending);
        foreach (var digest in DigestInstants(now, zone))
            all.Add(new($"digest:{digest:O}", "digest", digest, TimeZoneInfo.ConvertTime(digest, zone).Hour < 12 ? "the morning digest" : "the evening digest"));
        all.Add(new($"heartbeat:{lastRun:O}", "heartbeat", lastRun is { } hb ? hb + Heartbeat : now, "the 3-hour heartbeat"));
        bool Pending(ScheduleSignal x) => x.DueAt <= now && x.DueAt > covered && !snapshot.AiSchedule.Handled.ContainsKey(x.Key) && (x.Kind != "digest" || now - x.DueAt < Heartbeat);
        int Priority(string kind) => kind switch { "predbat_warning" => 0, "soc_miss" => 1, "sensor_outage" => 2, "window_end" => 3, "digest" => 4, _ => 5 };
        var due = all.Where(Pending).OrderBy(x => Priority(x.Kind)).ThenBy(x => x.DueAt).ToList();
        // A heartbeat that would only run shortly before a known event (a digest, a window end) waits for that event instead.
        var soon = all.Where(x => x.Kind != "heartbeat" && x.DueAt > now && x.DueAt <= now + interval && !snapshot.AiSchedule.Handled.ContainsKey(x.Key)).OrderBy(x => x.DueAt).FirstOrDefault();
        if (due.Count > 0 && due.All(x => x.Kind == "heartbeat") && soon != null)
            return Result("Waiting", $"Next AI check about {Clock(soon.DueAt)}, for {soon.Reason}.", soon.DueAt, soon.Reason, soon.Kind);
        if (due.Count > 0)
        {
            var first = due[0];
            if (spacingUntil > now)
                return Result("Waiting", $"A check is due for {first.Reason}; it runs at {Clock(spacingUntil)}, after your minimum spacing of {prefs.IntervalMinutes} minutes.", spacingUntil, first.Reason, first.Kind);
            return Result("Due", $"An automatic check is due for {first.Reason}.", first.DueAt, first.Reason, first.Kind);
        }
        var upcoming = all.Where(x => x.DueAt > now && !snapshot.AiSchedule.Handled.ContainsKey(x.Key)).OrderBy(x => x.DueAt).FirstOrDefault();
        var nextAt = upcoming is null ? (DateTimeOffset?)null : upcoming.DueAt < spacingUntil ? spacingUntil : upcoming.DueAt;
        return Result("Waiting", upcoming is null ? "Waiting for the next event." : $"Next AI check about {Clock(nextAt!.Value)}, for {upcoming.Reason}. Quiet checks every {prefs.IntervalMinutes} minutes in between don't use the AI unless something is new.", nextAt, upcoming?.Reason, upcoming?.Kind);
    }
    /// <summary>A check worth resuming gathered something before it stopped (state copies carry steps, not evidence bodies).</summary>
    static string? Resumable(Investigation failed) => failed.Steps.Count > 1 ? failed.Id : null;
    static TimeZoneInfo London() { try { return TimeZoneInfo.FindSystemTimeZoneById("Europe/London"); } catch (Exception e) when (e is TimeZoneNotFoundException or InvalidTimeZoneException) { return TimeZoneInfo.Utc; } }
    static DateTimeOffset? Latest(params DateTimeOffset?[] values) => values.Where(x => x.HasValue).OrderByDescending(x => x).FirstOrDefault();
}
