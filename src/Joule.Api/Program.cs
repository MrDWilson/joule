using Joule;
using System.Net;
using System.Text.Json;
// Container HEALTHCHECK: the aspnet image has no curl/wget, so the app probes itself.
if (args.Contains("--health")) return await WebSecurity.ProbeHealthAsync(Environment.GetEnvironmentVariable("ASPNETCORE_URLS") ?? Environment.GetEnvironmentVariable("URLS"));
// Setup can save settings and restart: each pass builds the app from the configuration as it is then.
while (true)
{
    var builder = WebApplication.CreateBuilder(args);
    // Request-start logs include the OAuth callback query. Keep codes and state out of logs.
    builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
    // One line per outgoing request (Predbat every five minutes, Setup's probes) is noise in a container log.
    builder.Logging.AddFilter("System.Net.Http.HttpClient", LogLevel.Warning);
    var dataDirectory = Path.GetFullPath(builder.Configuration["App:DataDirectory"] ?? "data");
    // Settings saved by Setup (data/settings.json) fill in whatever the environment leaves unset.
    SavedSettings savedSettings;
    try { savedSettings = SavedSettings.Attach(builder.Configuration, builder.Configuration, dataDirectory); }
    catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException) { Console.Error.WriteLine($"Joule cannot start: {e.Message}"); return 2; }
    var (authOptions, startupError) = AppAuthOptions.From(builder.Configuration);
    if (authOptions is null)
    {
        // A configuration mistake is not a crash: one clear line and a distinct exit code, no stack trace.
        Console.Error.WriteLine($"Joule cannot start: {startupError}{(savedSettings.Saved.Count > 0 ? $" Setup also saved settings in {savedSettings.Path}." : "")}");
        return 2;
    }
    var demo = authOptions.Demo;
    var noAuth = authOptions.NoAuth;
    if (string.IsNullOrWhiteSpace(builder.Configuration["urls"])) builder.WebHost.UseUrls("http://127.0.0.1:5080");
    var fileOptions = new ConfigFileArchiveOptions
    {
        Root = demo ? Path.Combine(dataDirectory, "demo-config") : builder.Configuration["ConfigFiles:Root"],
        DemoRuntimeMirror = demo,
        ArchiveDirectory = Path.Combine(dataDirectory, demo ? "demo-config-archive" : "config-archive"),
        AllowedFiles = demo ? ["runtime-settings.json", ConfigFileArchive.DemoAppsFile] : (builder.Configuration.GetSection("ConfigFiles:AllowedFiles").Get<string[]>() ?? []).Where(x => !string.IsNullOrWhiteSpace(x)).ToArray()
    };
    builder.Services.AddSingleton(new ConfigFileArchive(fileOptions));
    builder.Services.AddHttpClient("predbat", c => c.Timeout = TimeSpan.FromSeconds(25)).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
    builder.Services.AddHttpClient("predbat-probe", c => c.Timeout = TimeSpan.FromSeconds(30)).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
    builder.Services.AddHttpClient("ai", c => c.Timeout = TimeSpan.FromMinutes(15));
    builder.Services.AddHttpClient("auth", c => c.Timeout = TimeSpan.FromSeconds(30));
    builder.Services.AddHttpClient("docs", c => c.Timeout = TimeSpan.FromSeconds(15));
    builder.Services.AddSingleton(TimeProvider.System);
    builder.Services.AddSingleton(sp => new DataStore(Path.Combine(dataDirectory, demo ? "demo" : "live"), sp.GetRequiredService<TimeProvider>()));
    builder.Services.AddSingleton<IPredbatClient>(sp => new PredbatClient(sp.GetRequiredService<IHttpClientFactory>().CreateClient("predbat"), builder.Configuration));
    builder.Services.AddPredbatMcp(builder.Configuration);
    builder.Services.AddSingleton(sp => new StateService(sp.GetRequiredService<DataStore>(), sp.GetRequiredService<IPredbatClient>(), demo, sp.GetRequiredService<ConfigFileArchive>(), builder.Configuration, sampleHistory: demo));
    builder.Services.AddSingleton(sp => new ChatGptAuth(sp.GetRequiredService<IHttpClientFactory>().CreateClient("auth"), Path.Combine(dataDirectory, "auth")));
    builder.Services.AddSingleton(sp => new AiModelClient(sp.GetRequiredService<IHttpClientFactory>().CreateClient("ai"), sp.GetRequiredService<ChatGptAuth>(), builder.Configuration, sp.GetRequiredService<ILogger<AiModelClient>>()));
    builder.Services.AddSingleton<AnalysisService>(); builder.Services.AddSingleton<RecommendationReplyService>(); builder.Services.AddSingleton<ExperimentEvaluator>();
    builder.Services.AddSingleton<InvestigationScheduler>();
    builder.Services.AddSingleton<IPredbatReloadWatcher>(sp => demo ? new DemoReloadWatcher() : new PredbatReloadWatcher(sp.GetRequiredService<IHttpClientFactory>(), builder.Configuration, () => sp.GetRequiredService<AnalysisService>().Zone, sp.GetService<IPredbatMcpClient>()));
    builder.Services.AddSingleton(sp => new ConfigFileEditService(sp.GetRequiredService<ConfigFileArchive>(), sp.GetRequiredService<StateService>(), builder.Configuration, sp.GetRequiredService<IPredbatReloadWatcher>(), sp.GetRequiredService<ILogger<ConfigFileEditService>>()));
    builder.Services.AddSingleton<DocumentationService>();
    builder.Services.AddSingleton<ReportService>();
    builder.Services.AddHostedService<ReportWorker>();
    builder.Services.AddHomeAssistantTelemetry(builder.Configuration);
    builder.Services.AddHostedService<CollectorWorker>(); builder.Services.AddHostedService<AnalysisWorker>();
    builder.Services.AddSingleton(authOptions);
    builder.Services.AddSingleton(savedSettings);
    builder.Services.AddSingleton(sp => new SessionCookies(authOptions));
    builder.Services.AddSingleton<AccessKeyThrottle>();
    builder.Services.AddJouleCompression();
    var app = builder.Build();
    var inContainer = string.Equals(Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER"), "true", StringComparison.OrdinalIgnoreCase);
    if (noAuth && AppAuthOptions.ListensOnAllInterfaces(app.Configuration["urls"] ?? Environment.GetEnvironmentVariable("ASPNETCORE_URLS")))
        app.Logger.LogWarning("NO APP AUTHENTICATION: App__AuthMode=None and Joule listens on every network interface. Anyone who can reach this port can read your data and approve changes. Keep it behind a sign-in proxy, or use App__AuthMode=AccessKey.");
    app.UseResponseCompression();
    var sessions = app.Services.GetRequiredService<SessionCookies>();
    var throttle = app.Services.GetRequiredService<AccessKeyThrottle>();
    app.Use(async (context, next) =>
    {
        WebSecurity.ApplyHeaders(context, authOptions);
        if (context.Request.Path.StartsWithSegments("/api"))
        {
            context.Response.Headers.CacheControl = "no-store";
            if (!await WebSecurity.AuthorizeApiAsync(context, authOptions, sessions, throttle)) return;
        }
        try
        {
            await next();
            if (context.Response.StatusCode == 405 && !context.Response.HasStarted && context.Request.Path.StartsWithSegments("/api"))
                await context.Response.WriteAsJsonAsync(new { error = "That method is not allowed here." });
        }
        catch (DomainException ex) { context.Response.StatusCode = ex.Status; await context.Response.WriteAsJsonAsync(new { error = ex.Message }); }
        catch (BadHttpRequestException) { context.Response.StatusCode = 400; await context.Response.WriteAsJsonAsync(new { error = "Invalid request." }); }
        catch (Exception ex) { app.Logger.LogError("Request failed: {Type}", ex.GetType().Name); context.Response.StatusCode = 500; await context.Response.WriteAsJsonAsync(new { error = "The operation failed. Refresh and review the current state before retrying." }); }
    });
    app.UseDefaultFiles(); app.UseStaticFiles(WebSecurity.StaticFiles());
    // HEAD too: uptime monitors often probe with it.
    // started changes on every (re)start, so Setup can tell when a restart has finished.
    var startedAt = DateTimeOffset.UtcNow;
    app.MapMethods("/api/health", ["GET", "HEAD"], () => new { status = "ok", version = AppVersion.Current, build = AppVersion.Build, started = startedAt });
    app.MapPost("/api/session/logout", (HttpContext context) => { sessions.Clear(context); return Results.Ok(new { ok = true }); });
    app.MapGet("/api/mcp/status", (IPredbatMcpClient mcp) => mcp.Status);
    app.MapPost("/api/mcp/discover", (IPredbatMcpClient mcp, CancellationToken ct) => mcp.DiscoverAsync(ct));
    app.MapStateEndpoints(inContainer);
    app.MapGet("/api/plans/timeline", (int? hours, DataStore db) => { var now = DateTimeOffset.UtcNow; var from = now.AddHours(-Math.Clamp(hours ?? 24, 1, 168)); return new { from, to = now, slots = db.ReadRecentTimeline(from, now) }; });
    app.MapGet("/api/plans/{id}", (string id, DataStore db) => db.GetPlan(id) is { } plan ? Results.Ok(plan) : Results.NotFound(new { error = "Plan not found." }));
    app.MapGet("/api/telemetry/plans/{id}/alternative", (string id,DataStore db) => AlternativeForecastService.Compare(db,id));
    app.MapGet("/api/plans", (DateTimeOffset? from, DateTimeOffset? to, int? offset, int? limit, DataStore db) => db.ListPlans(from, to, offset ?? 0, limit ?? 50));
    app.MapGet("/api/proposals/{id}/preview", (string id, StateService state, DataStore db, AnalysisService analysis) =>
    {
        var snapshot = state.Read(false);
        var proposal = snapshot.Proposals.FirstOrDefault(p => p.Id == id) ?? throw new DomainException("Recommendation not found.", 404);
        return ImpactPreviewService.Build(snapshot, proposal, db.GetPlan(), DateTimeOffset.UtcNow, analysis.Zone);
    });
    app.MapPost("/api/mode", async (ModeRequest request, StateService state) => { await state.MutateAsync(s => ChangeEngine.SetMode(s, request.Mode)); return Results.Ok(new { ok = true }); });
    app.MapPost("/api/settings/{key}", async (string key, EditRequest request, StateService state) => { if (request.Value == null) throw new DomainException("Value is required.", 400); await state.MutateAsync(s => ChangeEngine.Edit(s, key, request.Value, request.Revision, $"Manually updated {key}")); return Results.Ok(new { ok = true }); });
    app.MapPost("/api/permissions/{key}", async (string key, PermissionRequest request, StateService state) => { await state.MutateAsync(s => ChangeEngine.Permission(s, key, request.Allowed)); return Results.Ok(new { ok = true }); });
    app.MapPost("/api/proposals/{id}/approve", async (string id, ApprovalRequest request, StateService state) => { await state.MutateAsync(s => ChangeEngine.Approve(s, id, request.AllowFuture)); return Results.Ok(new { ok = true }); });
    app.MapPost("/api/proposals/{id}/deny", async (string id, DecisionNoteRequest? request, StateService state) => { var note = RecommendationDecisions.Note(request?.Note, PredbatMcpSafety.Secrets(builder.Configuration)); await state.MutateAsync(s => ChangeEngine.Deny(s, id, note)); return Results.Ok(new { ok = true }); });
    app.MapPost("/api/revisions/{id:int}/revert", async (int id, RevisionRequest request, StateService state) => { await state.MutateAsync(s => ChangeEngine.Revert(s, id, request.Revision)); return Results.Ok(new { ok = true }); });
    app.MapPost("/api/revisions/{id:int}/restore", async (int id, RevisionRequest request, StateService state) => { await state.MutateAsync(s => ChangeEngine.Restore(s, id, request.Revision)); return Results.Ok(new { ok = true }); });
    app.MapGet("/api/revisions/{id:int}/export", (int id, StateService state) => { var revision = state.Read(false).Revisions.FirstOrDefault(x => x.Id == id) ?? throw new DomainException("Revision not found.", 404); return Results.File(JsonSerializer.SerializeToUtf8Bytes(revision, new JsonSerializerOptions(JsonDefaults.Options) { WriteIndented = true }), "application/json", $"predbat-config-revision-{id}.json"); });
    app.MapPost("/api/collect", async (StateService state, CancellationToken ct) => { await state.CollectAsync(ct); return Results.Ok(new { ok = true }); });
    app.MapPost("/api/reconcile", async (StateService state, CancellationToken ct) => { await state.ReconcileAsync(ct); return Results.Ok(new { ok = true }); });
    app.MapPost("/api/analysis/run", (AnalysisService analysis, IHostApplicationLifetime lifetime) => analysis.Start(lifetime.ApplicationStopping) ? Results.Accepted(value: new { ok = true }) : Results.Conflict(new { error = "An investigation is already running." }));
    app.MapPost("/api/ai/preferences", async (AiPreferences request, StateService state) =>
    {
        if (request.Provider is not ("Demo" or "Api" or "ChatGpt") || (!demo && request.Provider == "Demo") || request.IntervalMinutes is < 15 or > 1440 || request.MaxRunsPerDay is < 1 or > 96 || request.Model == null || request.Model.Length > 150 || !double.IsFinite(request.InputUsdPerMillion) || !double.IsFinite(request.OutputUsdPerMillion) || request.InputUsdPerMillion is < 0 or > 10000 || request.OutputUsdPerMillion is < 0 or > 10000)
            throw new DomainException("Check provider, schedule (15–1440 minutes), run limit (1–96), model and non-negative token prices.", 400);
        await state.MutateAsync(s => { s.Ai = request; ChangeEngine.Log(s, "provider", $"You selected {request.Provider}. Paid fallback remains disabled."); }); return Results.Ok(new { ok = true });
    });
    app.MapPost("/api/ai/chatgpt/start", async (HttpContext context, ChatGptAuth auth, CancellationToken ct) =>
    {
        if (!ChatGptSignInLocation.IsAvailable(context, inContainer))
            return Results.Conflict(new { code = "local_sign_in_required", error = "ChatGPT plan sign-in requires the native app at http://127.0.0.1:5080 on your browser's computer. For Docker or a remote server, sign in locally and transfer the protected credentials over SSH. See AI & costs for setup instructions." });
        return Results.Ok(new { url = await auth.StartAsync(ct) });
    });
    app.MapGet("/api/ai/chatgpt/models", async (ChatGptAuth auth, CancellationToken ct) => Results.Ok(new { models = await auth.ModelsAsync(ct) }));
    app.MapPost("/api/ai/chatgpt/disconnect", async (ChatGptAuth auth, CancellationToken ct) => { await auth.DisconnectAsync(ct); return Results.Ok(new { ok = true, remoteRevocationConfirmed = auth.RemoteRevocationConfirmed }); });
    app.MapGet("/auth/callback", async (HttpContext context, ChatGptAuth auth, CancellationToken ct) =>
    {
        if (context.Connection.RemoteIpAddress is not { } remote || !IPAddress.IsLoopback(remote)) return Results.StatusCode(403);
        await auth.CompleteAsync(context.Request.Query["code"].ToString(), context.Request.Query["state"].ToString(), context.Request.Query["client_id"].FirstOrDefault(), context.Request.Query["error"].FirstOrDefault(), ct);
        return Results.Content("<!doctype html><html><head><title>Joule connected</title></head><body style='background:#0b1017;color:#e6eef5;font:18px system-ui;padding:4rem'><h1>ChatGPT connected</h1><p>You can close this tab and return to Joule. Choose an available model in AI &amp; costs.</p></body></html>", "text/html");
    });
    app.MapControlEndpoints();
    app.MapAiCompletionEndpoints();
    app.MapRecommendationReplyEndpoints();
    app.MapAiQualityEndpoints();
    app.MapInsightsEndpoints();
    app.MapConfigFileEditEndpoints();
    app.MapHomeAssistantTelemetry();
    app.MapStorageEndpoints();
    app.MapChangeEndpoints();
    app.MapSetupEndpoints();
    app.MapSetupConfigEndpoints(inContainer);
    // Unknown API paths answer with JSON, never the SPA page (which would be a confusing 200 for API clients). Both
    // fallbacks are GET/HEAD only so a wrong method on a real route still gets 405 rather than a fallback.
    var readMethods = new HttpMethodMetadata(["GET", "HEAD"]);
    app.MapFallback("/api/{**rest}", () => Results.NotFound(new { error = "Not found." })).WithMetadata(readMethods);
    app.MapFallbackToFile("index.html", WebSecurity.SpaFallback()).WithMetadata(readMethods);
    var configEdits = app.Services.GetRequiredService<ConfigFileEditService>();
    configEdits.Stopping = app.Lifetime.ApplicationStopping;
    app.Lifetime.ApplicationStarted.Register(configEdits.ResumeChecks);
    app.Run();
    if (!JouleRestart.Consume()) return 0;
    // Setup saved new settings: build the whole app again from the updated configuration, in this same process.
    await app.DisposeAsync();
    Console.WriteLine("Joule is restarting with the settings saved in Setup.");
}
public record ModeRequest(string Mode);
public record EditRequest(string Value, int Revision);
public record PermissionRequest(bool Allowed);
public record ApprovalRequest(bool AllowFuture);
public record RevisionRequest(int Revision);
public partial class Program;
