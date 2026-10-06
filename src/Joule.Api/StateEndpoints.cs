using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Joule;

public record InvestigationListItem(string Id, DateTimeOffset At, string Status, string? Verdict, string Title, string Headline, string? Plain, string Summary,
    string Category, string Confidence, string Provider, bool Scheduled, string? Question, DateTimeOffset? DismissedAt, int OpenFollowUps, int OpenFileChanges,
    int PendingProposals, double? ImpactPence, bool Archived);
public record InvestigationPage(List<InvestigationListItem> Items, string? NextCursor, int Total);
public record ActivityPage(List<Activity> Items, DateTimeOffset? Oldest, DateTimeOffset? Newest, bool More);
public record UsageDay(string Date, int Runs, int Failed, long InputTokens, long OutputTokens, double? EstimatedUsd);
public record UsageReport(DateTimeOffset From, DateTimeOffset To, string TimeZone, int Runs, int Failed, long InputTokens, long OutputTokens, double? EstimatedUsd,
    bool CostComplete, List<UsageDay> Daily, List<UsageRecord> Records);
public record SettingsCatalogue(int Revision, List<Setting> Settings);

/// <summary>
/// The state API: /api/state (full or slim header), the full settings catalogue, and paged history. Read endpoints that are
/// polled carry a weak ETag that is a hash of the serialised body, so an unchanged poll costs a 304 and a few hundred bytes.
/// </summary>
public static class StateEndpoints
{
    /// <summary>What GET /api/state returns without a view parameter. Kept "full" until every UI page reads the header; then flip to "header".</summary>
    public const string DefaultView = "full";
    const int MaxPage = 100;
    static readonly TimeSpan telemetryCacheFor = TimeSpan.FromSeconds(30);
    static readonly object telemetryGate = new();
    static (DateTimeOffset At, TelemetryStatus? Status) telemetryCache;

    public static WebApplication MapStateEndpoints(this WebApplication app, bool inContainer)
    {
        app.MapGet("/api/state", (HttpContext context, string? view, string? full, StateService state, IServiceProvider services) =>
        {
            var header = full != "1" && string.Equals(view ?? DefaultView, "header", StringComparison.OrdinalIgnoreCase);
            if (view is not null && view is not ("header" or "full")) throw new DomainException("view must be header or full.", 400);
            var snapshot = state.Read(false);
            var now = DateTimeOffset.UtcNow;
            var ctx = Context(context, snapshot, services, inContainer, now, header);
            var schedules = ReportService.BuildScheduleStatus(snapshot, now);
            return Cached(context, header ? StateProjection.Header(snapshot, ctx, schedules) : StateProjection.Full(snapshot, ctx, schedules));
        });
        app.MapGet("/api/settings", (HttpContext context, StateService state) =>
        {
            var snapshot = state.Read(false);
            return Cached(context, new SettingsCatalogue(snapshot.Revision, snapshot.Settings));
        });
        app.MapGet("/api/investigations", (HttpContext context, string? cursor, int? limit, string? verdict, string? status, StateService state, DataStore db, IConfiguration configuration) =>
            Cached(context, Investigations(state.Read(false), db, new InvestigationReadSanitizer(configuration), cursor, Math.Clamp(limit ?? 20, 1, MaxPage), verdict, status)));
        app.MapGet("/api/activities", (HttpContext context, DateTimeOffset? since, DateTimeOffset? before, int? limit, string? kind, StateService state, DataStore db) =>
            Cached(context, Activities(state.Read(false), db, since, before, Math.Clamp(limit ?? 50, 1, 500), kind)));
        app.MapGet("/api/usage", (HttpContext context, int? days, StateService state, DataStore db, HomeAssistantOptions options) =>
            Cached(context, Usage(state.Read(false), db, Math.Clamp(days ?? 30, 1, 400), options.TimeZone, DateTimeOffset.UtcNow)));
        return app;
    }

    static StateContext Context(HttpContext http, AppState snapshot, IServiceProvider services, bool inContainer, DateTimeOffset now, bool header)
    {
        var client = services.GetRequiredService<IPredbatClient>();
        var analysis = services.GetRequiredService<AnalysisService>();
        var model = services.GetRequiredService<AiModelClient>();
        var auth = services.GetRequiredService<ChatGptAuth>();
        var db = services.GetRequiredService<DataStore>();
        var options = services.GetService<HomeAssistantOptions>();
        return new(services.GetRequiredService<StateService>().Demo, client.WritesEnabled, client.Configured, analysis.Running,
            services.GetRequiredService<InvestigationScheduler>().Status(now), model.ApiConfigured, auth.Connected, auth.Email,
            ChatGptSignInLocation.IsAvailable(http, inContainer), db.GetPlan(), db.ListMemory(), db.ArchivedInvestigationCount,
            header ? Telemetry(services, now) : null, services.GetService<IPredbatMcpClient>()?.Configured ?? false, options?.TimeZone ?? "Europe/London", now,
            services.GetRequiredService<AppAuthOptions>().NoAuth ? "None" : "AccessKey");
    }

    /// <summary>Home Assistant status scans the latest sample per metric; poll-rate callers share one result for 30 seconds.</summary>
    static TelemetryStatus? Telemetry(IServiceProvider services, DateTimeOffset now)
    {
        lock (telemetryGate)
        {
            if (telemetryCache.Status is not null && now - telemetryCache.At < telemetryCacheFor) return telemetryCache.Status;
            try { telemetryCache = (now, services.GetService<TelemetryCollectionService>()?.Status()); }
            catch (Exception e) when (e is not OperationCanceledException) { telemetryCache = (now, null); }
            return telemetryCache.Status;
        }
    }

    /// <summary>Serialises once, tags with a hash of the bytes and answers 304 when the client already holds that body.</summary>
    public static IResult Cached(HttpContext context, object payload)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, payload.GetType(), JsonDefaults.Options);
        var tag = ETagFor(bytes);
        context.Response.Headers.ETag = tag;
        // private: never stored by a shared proxy cache. no-cache: the browser revalidates every time, so it is never stale.
        context.Response.Headers.CacheControl = "private, no-cache";
        return Matches(context.Request.Headers.IfNoneMatch.ToString(), tag) ? Results.StatusCode(StatusCodes.Status304NotModified) : Results.Bytes(bytes, "application/json; charset=utf-8");
    }
    public static string ETagFor(byte[] bytes) => $"W/\"{Convert.ToHexString(SHA256.HashData(bytes), 0, 16).ToLowerInvariant()}\"";
    static bool Matches(string ifNoneMatch, string tag)
    {
        if (string.IsNullOrWhiteSpace(ifNoneMatch)) return false;
        var opaque = tag[2..];
        return ifNoneMatch.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(x => x == "*" || x == tag || x == opaque || (x.StartsWith("W/", StringComparison.Ordinal) && x[2..] == opaque));
    }

    static InvestigationListItem ListItem(Investigation i, int pending, bool archived)
    {
        var s = StateProjection.Summarise(i, pending);
        return new(s.Id, s.At, s.Status, s.Verdict, s.Title, s.Headline, s.Plain, i.Summary, s.Category, s.Confidence, s.Provider, s.Scheduled, i.Request.Question,
            s.DismissedAt, s.OpenFollowUps, s.OpenFileChanges, s.PendingProposals, s.ImpactPence, archived);
    }

    /// <summary>Newest first across AppState and the archive. The cursor is opaque: the (at, id) of the last row returned.</summary>
    internal static InvestigationPage Investigations(AppState state, DataStore db, InvestigationReadSanitizer sanitizer, string? cursor, int limit, string? verdict, string? status)
    {
        (DateTimeOffset At, string Id)? after = cursor is null ? null : DecodeCursor(cursor);
        var pending = state.Proposals.Where(p => p.Status == "Pending").GroupBy(p => p.InvestigationId).ToDictionary(g => g.Key, g => g.Count());
        bool Wanted(Investigation i) => (verdict is null || string.Equals(i.Verdict, verdict, StringComparison.OrdinalIgnoreCase)) && (status is null || string.Equals(i.Status, status, StringComparison.OrdinalIgnoreCase));
        bool Before(Investigation i) => after is null || i.At < after.Value.At || (i.At == after.Value.At && string.CompareOrdinal(i.Id, after.Value.Id) < 0);
        var live = state.Investigations.Select(i => i.Id).ToHashSet(StringComparer.Ordinal);
        var rows = state.Investigations.Where(i => Before(i) && Wanted(i)).Select(i => (Item: i, Archived: false)).ToList();
        // Read archived rows in batches until enough survive the filters (filters are rare; one batch is the usual case).
        var archived = new List<Investigation>();
        (DateTimeOffset At, string Id)? archiveCursor = after;
        while (archived.Count < limit + 1)
        {
            var batch = db.ReadArchivedInvestigations(archiveCursor?.At, archiveCursor?.Id, Math.Max(limit + 1, 50));
            if (batch.Count == 0) break;
            archived.AddRange(batch.Where(i => !live.Contains(i.Id) && Wanted(i)));
            archiveCursor = (batch[^1].At, batch[^1].Id);
            if (batch.Count < Math.Max(limit + 1, 50)) break;
        }
        rows.AddRange(archived.Select(i => (Item: i, Archived: true)));
        var ordered = rows.OrderByDescending(r => r.Item.At).ThenByDescending(r => r.Item.Id, StringComparer.Ordinal).Take(limit + 1).ToList();
        var page = ordered.Take(limit).Select(r => { if (r.Archived) sanitizer.SanitizeCopy(r.Item); return ListItem(r.Item, pending.GetValueOrDefault(r.Item.Id), r.Archived); }).ToList();
        var next = ordered.Count > limit ? EncodeCursor(page[^1].At, page[^1].Id) : null;
        var total = verdict is null && status is null ? state.Investigations.Count + (int)Math.Min(int.MaxValue, db.ArchivedInvestigationCount) : -1;
        return new(page, next, total);
    }

    /// <summary>An investigation moved to the archive, with its stored tool evidence, cleaned for display. Null when unknown.</summary>
    public static Investigation? ReadArchived(DataStore db, IConfiguration configuration, string id)
    {
        if (db.ReadArchivedInvestigation(id) is not { } item) return null;
        item.ToolEvidence = db.ReadInvestigationEvidence(id);
        new InvestigationReadSanitizer(configuration).SanitizeCopy(item);
        return item;
    }

    public static string EncodeCursor(DateTimeOffset at, string id) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes($"{at.UtcTicks}|{id}")).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    static (DateTimeOffset, string) DecodeCursor(string cursor)
    {
        try
        {
            var padded = cursor.Replace('-', '+').Replace('_', '/');
            padded += new string('=', (4 - padded.Length % 4) % 4);
            var parts = Encoding.UTF8.GetString(Convert.FromBase64String(padded)).Split('|', 2);
            return (new DateTimeOffset(long.Parse(parts[0]), TimeSpan.Zero), parts[1]);
        }
        catch (Exception e) when (e is FormatException or OverflowException or IndexOutOfRangeException or ArgumentOutOfRangeException)
        { throw new DomainException("That page link is no longer valid. Reload the list.", 400); }
    }

    /// <summary>
    /// Activities, always oldest first. With <paramref name="since"/>: everything after it (for live progress). Otherwise the
    /// newest <paramref name="limit"/> before <paramref name="before"/> (for paging back through history).
    /// </summary>
    public static ActivityPage Activities(AppState state, DataStore db, DateTimeOffset? since, DateTimeOffset? before, int limit, string? kind)
    {
        bool Wanted(Activity a) => (kind is null || a.Kind == kind) && (since is null || a.At > since) && (before is null || a.At < before);
        var live = state.Activities.Where(Wanted).ToList();
        var newest = since is null;
        var oldestLive = state.Activities.Count > 0 ? state.Activities.Min(a => a.At) : (DateTimeOffset?)null;
        // The archive only holds activities older than the oldest one still in AppState.
        var needArchive = newest ? live.Count < limit + 1 : oldestLive is null || since < oldestLive;
        var archived = needArchive ? db.ReadArchivedActivities(since, Earliest(before, oldestLive), limit + 1, newest, kind) : [];
        // Activities have no id, so (At, Kind, Message) is the identity, as it is the archive's primary key. Two genuinely
        // identical lines in the same tick therefore show once; that is accepted rather than adding an id to every activity.
        var all = archived.Concat(live).DistinctBy(a => (a.At, a.Kind, a.Message)).OrderBy(a => a.At).ToList();
        var more = all.Count > limit;
        var items = newest ? all.TakeLast(limit).ToList() : all.Take(limit).ToList();
        return new(items, items.FirstOrDefault()?.At, items.LastOrDefault()?.At, more);
    }
    static DateTimeOffset? Earliest(DateTimeOffset? a, DateTimeOffset? b) => a is null ? b : b is null ? a : a < b ? a : b;

    /// <summary>AI usage over the last <paramref name="days"/> local days, with per-day totals in the household timezone.</summary>
    public static UsageReport Usage(AppState state, DataStore db, int days, string timeZone, DateTimeOffset now)
    {
        TimeZoneInfo tz;
        try { tz = TimeZoneInfo.FindSystemTimeZoneById(timeZone); } catch (Exception e) when (e is TimeZoneNotFoundException or InvalidTimeZoneException) { tz = TimeZoneInfo.Utc; timeZone = "UTC"; }
        var localToday = TimeZoneInfo.ConvertTime(now, tz).Date;
        var startLocal = localToday.AddDays(1 - days);
        var from = new DateTimeOffset(startLocal, tz.GetUtcOffset(startLocal)).ToUniversalTime();
        // The window ends at the next local midnight rather than "now", so an unchanged history keeps its ETag.
        var endLocal = localToday.AddDays(1);
        var to = new DateTimeOffset(endLocal, tz.GetUtcOffset(endLocal)).ToUniversalTime();
        var records = db.ReadArchivedUsage(from, to).Concat(state.Usage.Where(u => u.At >= from && u.At < to))
            .DistinctBy(u => (u.At, u.Provider, u.Model, u.Status)).OrderBy(u => u.At).ToList();
        static double? Cost(IEnumerable<UsageRecord> rows) { var known = rows.Where(r => r.EstimatedUsd is not null).ToList(); return known.Count == 0 ? null : Math.Round(known.Sum(r => r.EstimatedUsd!.Value), 4); }
        var daily = records.GroupBy(u => TimeZoneInfo.ConvertTime(u.At, tz).Date).OrderBy(g => g.Key)
            .Select(g => new UsageDay(g.Key.ToString("yyyy-MM-dd"), g.Count(), g.Count(x => x.Status == "Failed"), g.Sum(x => x.InputTokens), g.Sum(x => x.OutputTokens), Cost(g))).ToList();
        return new(from, to, timeZone, records.Count, records.Count(x => x.Status == "Failed"), records.Sum(x => x.InputTokens), records.Sum(x => x.OutputTokens),
            Cost(records), records.All(r => r.EstimatedUsd is not null), daily, records);
    }
}
