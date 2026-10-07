using System.Collections.Concurrent;
using System.Globalization;

namespace Joule;

/// <summary>Kind is Daily, Weekly or Custom (Custom needs both dates). Dates left out mean yesterday (Daily) or last week (Weekly).</summary>
public record ReportRequest(string Kind = "Daily", DateTimeOffset? From = null, DateTimeOffset? To = null);
public record ReportScheduleStatus(string Kind, bool Enabled, string State, string Reason, DateTimeOffset? NextRunAt,
    DateTimeOffset? LastGeneratedAt, DateTimeOffset? From, DateTimeOffset? To);
public record ReportSchedulesStatus(string TimeZone, int HourLocal, ReportScheduleStatus Daily, ReportScheduleStatus Weekly);
/// <summary>An AI check made during a report's period, by title.</summary>
public record ReportRelatedCheck(string Id, DateTimeOffset At, string Title);
/// <summary>
/// A saved report as it reads now: the stored period with its figures, days and written summary recomputed from the meters
/// (so later fixes to the accounting reach old reports too), plus the AI checks made in that period by title.
/// </summary>
public record ReportView(string Id, DateTimeOffset CreatedAt, string Kind, string TimeZone, DateTimeOffset From, DateTimeOffset To, string Title,
    string Summary, bool IsDemo, DateTimeOffset? ReadAt, bool Partial, bool HasReadings, EnergySummary EnergySummary, List<EnergySummary> Days,
    List<ReportRelatedCheck> RelatedChecks);

/// <summary>
/// Energy reports straight from the meters (no AI). A report stores only its period; figures are recomputed when it is opened.
/// Daily reports are on by default, delivered at 08:00 local time for yesterday. Scheduled reports raise one in-app notification;
/// reports made by hand do not. Periods with no meter readings get no report and no notification.
/// </summary>
public sealed class ReportService(StateService state, DataStore db)
{
    /// <summary>3: part-day titles name the date ("Report · Mon 5 Oct, to 09:53") and the summary leads with Home use.</summary>
    public const int CurrentTextVersion = 3;
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture, UK = CultureInfo.GetCultureInfo("en-GB");
    readonly SemaphoreSlim creation = new(1, 1);
    /// <summary>Scheduled periods found empty, so the worker doesn't re-read them every minute.</summary>
    readonly ConcurrentDictionary<string, DateTimeOffset> emptyPeriods = new();

    public static void ValidatePreferences(ReportPreferences preferences)
    {
        if (preferences.HourLocal is < 0 or > 23 || preferences.TimeZone is null || preferences.TimeZone.Length > 100) throw new DomainException("Choose a valid reporting hour (0–23) and timezone.", 400);
        try { _ = TimeZoneInfo.FindSystemTimeZoneById(preferences.TimeZone); }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException) { throw new DomainException("Reporting timezone is unavailable on this host.", 400); }
    }
    /// <summary>Saves the user's choice; it is never overridden by a later change of defaults.</summary>
    public Task SavePreferencesAsync(ReportPreferences preferences, CancellationToken ct = default)
    {
        ValidatePreferences(preferences);
        preferences.DefaultsVersion = 1;
        return state.MutateAsync(s => s.ReportPreferences = preferences, ct);
    }
    public static (DateTimeOffset From, DateTimeOffset To) CompletedPeriod(string kind, DateTimeOffset now, string timeZone)
    {
        if (kind is not ("Daily" or "Weekly")) throw new DomainException("Choose Daily or Weekly report.", 400);
        var zone = TimeZoneInfo.FindSystemTimeZoneById(timeZone);
        var today = TimeZoneInfo.ConvertTime(now, zone).Date;
        var end = kind == "Daily" ? today : today.AddDays(-(((int)today.DayOfWeek + 6) % 7));
        var start = end.AddDays(kind == "Daily" ? -1 : -7);
        var to = CivilTime.FirstValidInstant(end, zone);
        var from = CivilTime.FirstValidInstant(start, zone);
        // A date-line transition can remove yesterday entirely. Report the
        // latest actual day instead of creating an empty, fictitious period.
        while (from >= to) { start = start.AddDays(-1); from = CivilTime.FirstValidInstant(start, zone); }
        return (from, to);
    }

    /// <summary>A report made by hand: no notification, and an empty period is refused with a plain reason.</summary>
    public async Task<EnergyReport> GenerateAsync(ReportRequest request, CancellationToken ct = default) =>
        (await CreateAsync(request, scheduled: false, DateTimeOffset.UtcNow, ct))!;

    async Task<EnergyReport?> CreateAsync(ReportRequest request, bool scheduled, DateTimeOffset now, CancellationToken ct)
    {
        if (request.Kind is not ("Daily" or "Weekly" or "Custom")) throw new DomainException("Choose a daily, weekly or custom report.", 400);
        if (request.Kind == "Custom" && (request.From == null || request.To == null)) throw new DomainException("Choose the dates for the report.", 400);
        var prefs = state.Read(false).ReportPreferences; ValidatePreferences(prefs);
        var zone = TimeZoneInfo.FindSystemTimeZoneById(prefs.TimeZone);
        var period = request.Kind == "Custom" ? (request.From!.Value, request.To!.Value) : CompletedPeriod(request.Kind, now, prefs.TimeZone);
        var from = request.From ?? period.Item1; var to = request.To ?? period.Item2;
        new AnalysisRequest(null, from, to).Validate();
        if ((request.From == null) != (request.To == null)) throw new DomainException("Choose both dates for the report, or neither.", 400);
        if (to > now.AddMinutes(1)) throw new DomainException("A report can't run into the future. Choose a period that has ended, or today so far.", 400);
        if (to - from > TimeSpan.FromDays(32)) throw new DomainException("A report can cover at most 32 days.", 400);
        await creation.WaitAsync(ct);
        try
        {
            var snapshot = state.Read(false);
            var existing = snapshot.Reports.FirstOrDefault(r => r.Kind == request.Kind && r.From == from && r.To == to);
            if (existing != null) return existing;
            var summary = db.ReadEnergySummary(from, to);
            CheckSources(summary);
            if (!HasReadings(summary))
            {
                if (scheduled) return null;
                throw new DomainException("There are no meter readings for this period, so there's nothing to report.", 400);
            }
            var report = new EnergyReport
            {
                Kind = request.Kind, TimeZone = prefs.TimeZone, From = from, To = to, IsDemo = state.Demo, CreatedAt = now, TextVersion = CurrentTextVersion,
                Title = Title(request.Kind, from, to, now, zone),
                Summary = Describe(summary, from, to, zone, state.Demo),
                InvestigationIds = snapshot.Investigations.Where(i => i.At >= from && i.At < to && i.Status == "Completed").Select(i => i.Id).ToList(),
            };
            await state.MutateAsync(s =>
            {
                s.Reports.Add(report);
                if (scheduled) s.Notifications.Add(new InAppNotification { Title = NotificationTitle(report.Kind, from, to, zone), Message = Headline(summary), ReportId = report.Id, At = now });
                ChangeEngine.Log(s, "report", report.Title);
            }, ct);
            return report;
        }
        finally { creation.Release(); }
    }

    void CheckSources(EnergySummary summary)
    {
        if (state.Demo && summary.Sources.Any(source => !source.StartsWith("Demo", StringComparison.OrdinalIgnoreCase)))
            throw new DomainException("Demo reporting found non-demo telemetry. Use a separate demo data directory.");
        if (!state.Demo && summary.Sources.Any(source => source.StartsWith("Demo", StringComparison.OrdinalIgnoreCase)))
            throw new DomainException("Live reporting found scripted telemetry. Use a separate live data directory.");
    }

    public static bool HasReadings(EnergySummary summary) => summary.Metrics.Values.Any(m => m.EnergyKwh != null && m.CoverageFraction > 0);

    /// <summary>A saved report recomputed from the meters now.</summary>
    public ReportView View(string id)
    {
        var snapshot = state.Read(false);
        var report = snapshot.Reports.FirstOrDefault(r => r.Id == id) ?? throw new DomainException("Report not found.", 404);
        var tz = report.TimeZone ?? snapshot.ReportPreferences.TimeZone;
        var zone = FindZone(tz);
        var summary = db.ReadEnergySummary(report.From, report.To);
        var days = report.To - report.From > TimeSpan.FromHours(26) ? db.GetDailySummaries(report.From, report.To, zone.Id) : [summary];
        var byId = snapshot.Investigations.ToDictionary(i => i.Id);
        var related = report.InvestigationIds.Where(byId.ContainsKey).Select(i => byId[i])
            .Select(i => new ReportRelatedCheck(i.Id, i.At, string.IsNullOrWhiteSpace(i.Headline) ? i.Title : i.Headline!)).OrderBy(c => c.At).ToList();
        return new(report.Id, report.CreatedAt, report.Kind, tz, report.From, report.To, Title(report.Kind, report.From, report.To, report.CreatedAt, zone),
            Describe(summary, report.From, report.To, zone, report.IsDemo), report.IsDemo, report.ReadAt, IsPartialDay(report.From, report.To, zone),
            HasReadings(summary), summary, days, related);
    }

    static TimeZoneInfo FindZone(string? id)
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById(id ?? "Europe/London"); }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException) { return TimeZoneInfo.Utc; }
    }

    // ------------------------------------------------------------------ wording

    static DateTime Local(DateTimeOffset t, TimeZoneInfo zone) => TimeZoneInfo.ConvertTime(t, zone).DateTime;
    static DateTimeOffset LocalMidnightAfter(DateTimeOffset t, TimeZoneInfo zone) => CivilTime.FirstValidInstant(Local(t, zone).Date.AddDays(1), zone);
    static bool StartsAtMidnight(DateTimeOffset t, TimeZoneInfo zone) => CivilTime.FirstValidInstant(Local(t, zone).Date, zone) == t;
    /// <summary>A single local day that stops before its midnight ("today so far").</summary>
    public static bool IsPartialDay(DateTimeOffset from, DateTimeOffset to, TimeZoneInfo zone) =>
        StartsAtMidnight(from, zone) && to < LocalMidnightAfter(from, zone) && Local(to, zone).Date == Local(from, zone).Date;
    static bool WholeDays(DateTimeOffset from, DateTimeOffset to, TimeZoneInfo zone) => StartsAtMidnight(from, zone) && StartsAtMidnight(to, zone);
    static string Day(DateTime d) => d.ToString("ddd d MMM", UK).Replace("Sept", "Sep");
    static string ShortDay(DateTime d) => d.ToString("d MMM", UK).Replace("Sept", "Sep");
    static string Clock(DateTimeOffset t, TimeZoneInfo zone) => Local(t, zone).ToString("HH:mm", Inv);

    /// <summary>
    /// "Daily report · Sun 4 Oct", "Weekly report · 28 Sep – 4 Oct", "Report · 28 Sep – 4 Oct", and a snapshot of part of a
    /// day by its date and end time ("Report · Mon 5 Oct, to 09:53"), which stays true on later days (never "So far today").
    /// Always in the household's time zone.
    /// </summary>
    public static string Title(string kind, DateTimeOffset from, DateTimeOffset to, DateTimeOffset? createdAt, TimeZoneInfo zone)
    {
        var first = Local(from, zone).Date;
        if (IsPartialDay(from, to, zone)) return $"Report · {Day(first)}, to {Clock(to, zone)}";
        var prefix = kind switch { "Daily" => "Daily report", "Weekly" => "Weekly report", _ => "Report" };
        if (WholeDays(from, to, zone))
        {
            var last = Local(to, zone).Date.AddDays(-1);
            return last <= first ? $"{prefix} · {Day(first)}" : $"{prefix} · {ShortDay(first)} – {ShortDay(last)}";
        }
        return Local(to, zone).Date == first
            ? $"{prefix} · {Day(first)}, {Clock(from, zone)}–{Clock(to, zone)}"
            : $"{prefix} · {ShortDay(first)} {Clock(from, zone)} – {ShortDay(Local(to, zone).Date)} {Clock(to, zone)}";
    }

    /// <summary>A notification's title: the report's own, which names a part day by its date so it stays true tomorrow.</summary>
    public static string NotificationTitle(string kind, DateTimeOffset from, DateTimeOffset to, TimeZoneInfo zone) =>
        Title(kind, from, to, null, zone);

    static string Kwh(double v) => $"{Math.Round(v, 1, MidpointRounding.AwayFromZero).ToString("#,0.0", Inv)} kWh";
    static string Gbp(double v) => $"£{Math.Round(v, 2, MidpointRounding.AwayFromZero).ToString("#,0.00", Inv)}";
    static double? Energy(EnergySummary s, string key) => s.Metrics.TryGetValue(key, out var m) ? m.EnergyKwh : null;
    /// <summary>
    /// "at least " when the meter missed a long stretch (the figure leaves that time out, so it's a lower bound), "about " when
    /// more than 5% of it was spread across a short outage, otherwise nothing. The page uses the same rule.
    /// </summary>
    static string Hedge(EnergySummary s, string key)
    {
        if (!s.Metrics.TryGetValue(key, out var m) || m.EnergyKwh is not { } v) return "";
        if (m.State == "partial") return "at least ";
        return m.State == "estimated" && m.EstimatedKwh is { } e && Math.Abs(e) > 0.05 * Math.Max(0.1, Math.Abs(v)) ? "about " : "";
    }
    static string Amount(EnergySummary s, string key) => Hedge(s, key) + Kwh(Energy(s, key)!.Value);
    /// <summary>Home use without the car, hedged like the house meter it is worked out from.</summary>
    static string HomeAmount(EnergySummary s, EnergyMetricSummary home)
    {
        var hedge = home.State == "partial" ? "at least " : Hedge(s, "load");
        return hedge + Kwh(home.EnergyKwh!.Value);
    }
    static readonly Dictionary<string, string> MeterNames = new()
    {
        ["load"] = "home-use meter", ["pv"] = "solar meter", ["grid_import"] = "grid import meter", ["grid_export"] = "export meter",
        ["battery_charge"] = "battery charge meter", ["battery_discharge"] = "battery discharge meter", ["ev"] = "car charger meter",
    };
    static string GapVerb(string reason) => reason switch
    {
        "offline" => "was offline", "idle" => "reported nothing", "not_found" => "couldn't be found", "invalid" => "gave unreadable readings",
        "reset" => "reset unexpectedly", "source_changed" => "changed to a different sensor", _ => "wasn't read",
    };
    /// <summary>Gaps long enough to mention (five minutes or more).</summary>
    static IEnumerable<(string Metric, EnergyGap Gap)> NotableGaps(EnergySummary s) =>
        s.Metrics.Where(m => MeterNames.ContainsKey(m.Key)).SelectMany(m => m.Value.Gaps.Select(g => (m.Key, g))).Where(x => x.g.To - x.g.From >= TimeSpan.FromMinutes(5));
    static string Range(DateTimeOffset a, DateTimeOffset b, TimeZoneInfo zone, bool withDay) =>
        withDay ? $"{ShortDay(Local(a, zone).Date)} {Clock(a, zone)}–{(Local(b, zone).Date == Local(a, zone).Date ? Clock(b, zone) : $"{ShortDay(Local(b, zone).Date)} {Clock(b, zone)}")}"
                : $"{Clock(a, zone)}–{(b == LocalMidnightAfter(a, zone) ? "24:00" : Clock(b, zone))}";

    /// <summary>
    /// The written summary: short sentences using the same rounding as the page (kWh to 1 dp, pounds to 2 dp), the meters by
    /// name, and only gaps that are long enough to matter. The standing charge, when Joule knows it, is named with its daily rate and is
    /// in the net cost when the owner includes it (the default).
    /// </summary>
    public static string Describe(EnergySummary s, DateTimeOffset from, DateTimeOffset to, TimeZoneInfo zone, bool demo)
    {
        var parts = new List<string>();
        if (demo) parts.Add("Demo figures from made-up readings.");
        if (!HasReadings(s)) { parts.Add("No meter readings for this period."); return string.Join(" ", parts); }
        var lead = IsPartialDay(from, to, zone) ? $"From midnight to {Clock(to, zone)}, " : "";
        string Sentence(string text) => lead.Length > 0 ? lead + text : char.ToUpperInvariant(text[0]) + text[1..];

        double? load = Energy(s, "load"), ev = Energy(s, "ev"), pv = Energy(s, "pv"), import = Energy(s, "grid_import"), export = Energy(s, "grid_export"),
            charge = Energy(s, "battery_charge"), discharge = Energy(s, "battery_discharge");
        var car = ev is > 0.05;
        // The first figure is the page's Home use tile: the house without the car when the house meter includes its charging.
        var home = s.LoadIncludesEv == true && s.Home is { EnergyKwh: not null } ? s.Home : null;
        string? use = load switch
        {
            not null when car && home != null => $"your home used {HomeAmount(s, home)} and the car {Amount(s, "ev")}.",
            not null when car && s.LoadIncludesEv == true => $"you used {Amount(s, "load")}, {Amount(s, "ev")} of it charging the car.",
            not null when car => $"your home used {Amount(s, "load")} and the car took {Amount(s, "ev")}.",
            not null => $"your home used {Amount(s, "load")}.",
            null when car => $"the car took {Amount(s, "ev")}.",
            _ => null,
        };
        if (use != null) { parts.Add(Sentence(use)); lead = ""; }
        if (pv != null) { parts.Add(Sentence(pv < 0.05 ? "solar made nothing." : $"solar made {Amount(s, "pv")}.")); lead = ""; }
        if (import != null)
        {
            var bought = $"you bought {Amount(s, "grid_import")} from the grid{(s.ImportCostGbp is { } paid ? $" for {Gbp(paid)}" : "")}";
            var sold = export switch
            {
                null => "",
                < 0.05 => " and exported nothing",
                _ => $" and exported {Amount(s, "grid_export")}{(s.ExportCreditGbp is { } earned ? $", earning {Gbp(earned)}" : "")}",
            };
            parts.Add(Sentence(bought + sold + ".")); lead = "";
        }
        else if (export != null) { parts.Add(Sentence($"you exported {Amount(s, "grid_export")}{(s.ExportCreditGbp is { } earned ? $", earning {Gbp(earned)}" : "")}.")); lead = ""; }
        if (charge != null && discharge != null) { parts.Add(Sentence($"the battery took in {Amount(s, "battery_charge")} and gave back {Amount(s, "battery_discharge")}.")); lead = ""; }

        var net = s.NetCostGbp ?? (s.ImportCostGbp is { } i && s.ExportCreditGbp is { } e ? i - e : null);
        if (net is { } n)
        {
            var coverage = Math.Min(s.ImportCostCoverage, s.ExportCostCoverage);
            var about = coverage < 0.95 || s.EstimatedCostGbp > 0.05 * Math.Max(0.01, Math.Abs((s.ImportCostGbp ?? 0) + (s.ExportCreditGbp ?? 0))) ? "about " : "";
            if (s.StandingChargeGbp is { } standing && s.StandingChargePencePerDay is { } rate)
            {
                var standingText = $"the {Gbp(standing)} standing charge ({StandingRate(rate)})";
                var total = n + standing;
                parts.Add(s.StandingChargeIncluded
                    ? total >= 0 ? $"Net cost {about}{Gbp(total)}, including {standingText}." : $"Net earnings {about}{Gbp(-total)}, after {standingText}."
                    : (n >= 0 ? $"Net cost {about}{Gbp(n)}" : $"Net earnings {about}{Gbp(-n)}") + $"; {standingText} isn't included.");
            }
            else parts.Add(n >= 0 ? $"Net cost {about}{Gbp(n)}." : $"Net earnings {about}{Gbp(-n)}.");
        }
        else if (import != null) parts.Add("The net cost isn't known because a grid meter or price is missing.");

        var started = s.Metrics.Values.Where(m => m.EnergyKwh != null && m.CoverageFrom is { } c && c > from.AddMinutes(5)).Select(m => m.CoverageFrom!.Value).DefaultIfEmpty().Min();
        if (started != default && started < to)
            parts.Add($"Readings began {(Local(started, zone).Date == Local(from, zone).Date && to - from <= TimeSpan.FromHours(25) ? "at" : "on")} {(to - from > TimeSpan.FromHours(25) ? $"{Day(Local(started, zone).Date)}, " : "")}{Clock(started, zone)}, so earlier energy isn't counted.");
        var multiDay = Local(to.AddTicks(-1), zone).Date != Local(from, zone).Date;
        foreach (var group in NotableGaps(s).GroupBy(x => (x.Gap.From, x.Gap.To, x.Gap.Reason)).OrderBy(g => g.Key.From).Take(2))
        {
            var names = group.Select(x => MeterNames[x.Metric]).Distinct().ToList();
            var bare = names.Select(n => n.Replace(" meter", "")).ToList();
            var who = names.Count == 1 ? $"The {names[0]}" : $"The {string.Join(", ", bare.Take(bare.Count - 1))} and {bare[^1]} meters";
            var known = group.Sum(x => x.Gap.KnownKwh ?? 0);
            parts.Add($"{who} {(names.Count > 1 ? GapVerb(group.Key.Reason).Replace("was ", "were ") : GapVerb(group.Key.Reason))} {Range(group.Key.From, group.Key.To, zone, multiDay)}{(known > 0.05 && names.Count == 1 ? $" ({Kwh(known)} in that time isn't counted)" : "")}.");
        }
        if (s.StandingChargeGbp is null) parts.Add("Standing charges aren't included.");
        return string.Join(" ", parts);
    }

    /// <summary>"53.68p a day".</summary>
    static string StandingRate(double pence) => $"{pence.ToString(pence % 1 == 0 ? "0" : "0.##", System.Globalization.CultureInfo.InvariantCulture)}p a day";

    /// <summary>One line for a notification: "You used 61.1 kWh · net cost £5.20".</summary>
    public static string Headline(EnergySummary s)
    {
        var bits = new List<string>();
        if (Energy(s, "load") is not null) bits.Add($"You used {Amount(s, "load")}");
        var net = s.NetCostGbp ?? (s.ImportCostGbp is { } i && s.ExportCreditGbp is { } e ? i - e : null);
        if (net is { } energy && s.StandingChargeIncluded && s.StandingChargeGbp is { } standing) net = energy + standing;
        if (net is { } n) bits.Add(n >= 0 ? $"net cost {Gbp(n)}" : $"net earnings {Gbp(-n)}");
        if (bits.Count == 0) return "Your energy report is ready.";
        var text = string.Join(" · ", bits);
        return char.ToUpperInvariant(text[0]) + text[1..];
    }

    // ------------------------------------------------------------------ schedule

    public ReportSchedulesStatus GetScheduleStatus(DateTimeOffset now) => BuildScheduleStatus(state.Read(false), now);
    public static ReportSchedulesStatus BuildScheduleStatus(AppState snapshot, DateTimeOffset now)
    {
        var prefs = snapshot.ReportPreferences;
        ReportScheduleStatus Inactive(string kind, bool enabled, string status, string reason) => new(kind, enabled, enabled ? status : "Disabled",
            enabled ? reason : $"{kind} reports are off.", null,
            snapshot.Reports.Where(r => r.Kind == kind).Select(r => (DateTimeOffset?)r.CreatedAt).OrderByDescending(x => x).FirstOrDefault(), null, null);
        try { ValidatePreferences(prefs); }
        catch (DomainException ex) { return new(prefs.TimeZone, prefs.HourLocal, Inactive("Daily", prefs.DailyEnabled, "InvalidPreferences", ex.Message), Inactive("Weekly", prefs.WeeklyEnabled, "InvalidPreferences", ex.Message)); }
        var zone = TimeZoneInfo.FindSystemTimeZoneById(prefs.TimeZone);
        var today = TimeZoneInfo.ConvertTime(now, zone).Date;
        var delivery = LocalDelivery(today, prefs.HourLocal, zone);
        ReportScheduleStatus For(string kind, bool enabled)
        {
            if (!enabled) return Inactive(kind, false, "Disabled", "");
            var period = CompletedPeriod(kind, now, prefs.TimeZone);
            var generated = snapshot.Reports.Any(r => r.Kind == kind && r.From == period.From && r.To == period.To);
            var last = snapshot.Reports.Where(r => r.Kind == kind).Select(r => (DateTimeOffset?)r.CreatedAt).OrderByDescending(x => x).FirstOrDefault();
            var covers = kind == "Daily" ? "yesterday" : "last week";
            if (!generated && now >= delivery) return new(kind, true, "Due", $"The report for {covers} is due and will appear within a minute, if the meters recorded anything.", delivery, last, period.From, period.To);
            var nextDate = !generated ? today : kind == "Daily" ? today.AddDays(1) : today.AddDays(7 - (((int)today.DayOfWeek + 6) % 7));
            return new(kind, true, "Waiting", generated ? $"The report for {covers} is ready. The next one arrives at {prefs.HourLocal:00}:00." : $"The report for {covers} arrives at {prefs.HourLocal:00}:00.", LocalDelivery(nextDate, prefs.HourLocal, zone), last, period.From, period.To);
        }
        return new(prefs.TimeZone, prefs.HourLocal, For("Daily", prefs.DailyEnabled), For("Weekly", prefs.WeeklyEnabled));
    }
    static DateTimeOffset LocalDelivery(DateTime day, int hour, TimeZoneInfo zone)
    {
        // Deliver at the first valid instant, using the first occurrence of a
        // repeated hour. Period deduplication prevents a second delivery.
        return CivilTime.FirstValidInstant(day.AddHours(hour), zone);
    }
    public async Task GenerateDueAsync(DateTimeOffset now, CancellationToken ct = default)
    {
        var schedules = GetScheduleStatus(now);
        foreach (var schedule in new[] { schedules.Daily, schedules.Weekly })
        {
            if (schedule.State != "Due") continue;
            var key = $"{schedule.Kind}|{schedule.From:O}|{schedule.To:O}";
            if (emptyPeriods.TryGetValue(key, out var checkedAt) && now - checkedAt < TimeSpan.FromHours(1)) continue;
            if (await CreateAsync(new(schedule.Kind, schedule.From, schedule.To), scheduled: true, now, ct) == null) emptyPeriods[key] = now;
        }
    }
    public Task MarkReadAsync(string id, CancellationToken ct = default) => state.MutateAsync(s =>
    {
        var report = s.Reports.FirstOrDefault(r => r.Id == id) ?? throw new DomainException("Report not found.", 404); report.ReadAt ??= DateTimeOffset.UtcNow;
        foreach (var notification in s.Notifications.Where(n => n.ReportId == id)) notification.ReadAt ??= DateTimeOffset.UtcNow;
    }, ct);

    /// <summary>
    /// One-off upgrades, run when the worker starts:
    /// - daily reports become on by default (once; an off that was clearly chosen, or saved later, is kept);
    /// - when that happens, the last week's complete days that have readings get a report (no notifications);
    /// - older reports get the plain title and summary, stop carrying a frozen copy of their figures (they are recomputed when
    ///   opened), and a report for a period with no readings is marked read along with its notification.
    /// </summary>
    public async Task RepairAsync(DateTimeOffset now, CancellationToken ct = default)
    {
        var snapshot = state.Read(false);
        var prefs = snapshot.ReportPreferences;
        var zone = FindZone(prefs.TimeZone);
        var upgrade = prefs.DefaultsVersion < 1;
        // Daily reports were off by default before. Off is kept when it was clearly someone's choice: they changed the hour,
        // zone or weekly report (so they saved these preferences), or scheduled reports were delivered before (so they had
        // daily reports on and turned them off). Only untouched old defaults are switched on.
        var chosenOff = upgrade && !prefs.DailyEnabled &&
            (prefs.WeeklyEnabled || prefs.HourLocal != 8 || prefs.TimeZone != "Europe/London" || snapshot.Notifications.Any(n => n.ReportId != null));
        var turnOn = upgrade && !chosenOff;
        var legacy = snapshot.Reports.Where(r => r.TextVersion < CurrentTextVersion).ToList();
        var rewritten = new Dictionary<string, (string Title, string Summary, bool Empty)>();
        foreach (var r in legacy)
        {
            var reportZone = FindZone(r.TimeZone ?? prefs.TimeZone);
            var summary = db.ReadEnergySummary(r.From, r.To);
            rewritten[r.Id] = (Title(r.Kind, r.From, r.To, r.CreatedAt, reportZone), Describe(summary, r.From, r.To, reportZone, r.IsDemo), !HasReadings(summary));
        }
        if (upgrade || rewritten.Count > 0)
            await state.MutateAsync(s =>
            {
                if (s.ReportPreferences.DefaultsVersion < 1) { if (turnOn) s.ReportPreferences.DailyEnabled = true; s.ReportPreferences.DefaultsVersion = 1; }
                foreach (var r in s.Reports.Where(r => rewritten.ContainsKey(r.Id)))
                {
                    var (title, text, empty) = rewritten[r.Id];
                    r.Title = title; r.Summary = text; r.EnergySummary = null; r.Days = []; r.TextVersion = CurrentTextVersion;
                    foreach (var n in s.Notifications.Where(n => n.ReportId == r.Id)) { n.Title = NotificationTitle(r.Kind, r.From, r.To, FindZone(r.TimeZone ?? s.ReportPreferences.TimeZone)); if (empty) n.ReadAt ??= now; }
                    if (empty) r.ReadAt ??= now;
                }
            }, ct);
        if (!turnOn) return;
        // Catch up on the last week's complete days, quietly.
        var yesterday = CompletedPeriod("Daily", now, prefs.TimeZone);
        for (var day = 7; day >= 1; day--)
        {
            var from = CivilTime.FirstValidInstant(Local(yesterday.From, zone).Date.AddDays(1 - day), zone);
            var to = CivilTime.FirstValidInstant(Local(from, zone).Date.AddDays(1), zone);
            if (to > yesterday.To || to <= from || state.Read(false).Reports.Any(r => r.From == from)) continue;
            try
            {
                var report = await CreateAsync(new("Daily", from, to), scheduled: false, now, ct);
                // Caught up quietly: these don't count as new.
                if (report != null) await state.MutateAsync(s => { if (s.Reports.FirstOrDefault(r => r.Id == report.Id) is { } saved) saved.ReadAt ??= now; }, ct);
            }
            catch (DomainException) { /* No readings that day. */ }
        }
    }
}
public sealed class ReportWorker(ReportService reports, StateService state, TelemetryCollectionService telemetry, ILogger<ReportWorker> log) : BackgroundService
{
    /// <summary>
    /// Runs before Joule starts answering requests, so the first page sees settled reports: older reports are upgraded, missed
    /// days are filled in and a due report is made. The demo collects its scripted readings first so its reports are the same
    /// on every start.
    /// </summary>
    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (state.Demo) await telemetry.CollectAsync(cancellationToken);
            await reports.RepairAsync(DateTimeOffset.UtcNow, cancellationToken);
            await reports.GenerateDueAsync(DateTimeOffset.UtcNow, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex) { log.LogWarning(ex, "Updating reports at startup failed. Collection is unaffected; reports are retried every minute."); }
        await base.StartAsync(cancellationToken);
    }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken); }
        catch (OperationCanceledException) { return; }
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await reports.GenerateDueAsync(DateTimeOffset.UtcNow, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { log.LogWarning(ex, "Report generation failed. Measurements and collection remain independent; the report will be retried."); }
            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
        }
    }
}
