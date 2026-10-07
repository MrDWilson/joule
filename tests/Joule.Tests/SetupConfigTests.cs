using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Joule;
using Microsoft.Extensions.Configuration;
using Xunit;
namespace Joule.Tests;

/// <summary>Settings saved from Setup, sensor suggestions from Predbat's apps.yaml, and the Predbat probe. Every value here is made up.</summary>
public class SetupConfigLogicTests : IDisposable
{
    readonly string directory = Path.Combine(Path.GetTempPath(), "joule-setup-" + Guid.NewGuid());
    public void Dispose() { try { Directory.Delete(directory, true); } catch (IOException) { } }

    const string Apps = """
    pred_bat:
      module: predbat
      prefix: predbat
      # One inverter
      load_today:
        - sensor.givtcp_{geserial}_load_energy_today_kwh
      import_today: [sensor.givtcp_{geserial}_import_energy_today_kwh]
      export_today:
        - sensor.givtcp_{geserial}_export_energy_today_kwh   # export
      pv_today: sensor.givtcp_{geserial}_pv_energy_today_kwh
      soc_percent: sensor.givtcp_{geserial}_soc
      car_charging_energy: 're:(sensor.myenergi_zappi_[0-9a-z]+_charge_added_session)'
      metric_octopus_import: 're:(sensor.(octopus_energy_|)electricity_[0-9a-z]+_[0-9a-z]+_current_rate)'
      metric_octopus_export: "re:(sensor.(octopus_energy_|)electricity_[0-9a-z]+_[0-9a-z]+_export_current_rate)"
      battery_power:
        - sensor.givtcp_{geserial}_battery_power
    """;

    const string State = """
    {
      "sensor.givtcp_xx0000_load_energy_today_kwh": {"state": "7.4", "attributes": {"unit_of_measurement": "kWh"}},
      "sensor.givtcp_xx0000_import_energy_today_kwh": {"state": "3.2", "attributes": {"unit_of_measurement": "kWh"}},
      "sensor.givtcp_xx0000_export_energy_today_kwh": {"state": "1.1", "attributes": {"unit_of_measurement": "kWh"}},
      "sensor.givtcp_xx0000_pv_energy_today_kwh": {"state": "9.0", "attributes": {"unit_of_measurement": "kWh"}},
      "sensor.givtcp_xx0000_battery_charge_energy_today_kwh": {"state": "4.0", "attributes": {"unit_of_measurement": "kWh"}},
      "sensor.givtcp_xx0000_battery_discharge_energy_today_kwh": {"state": "3.0", "attributes": {"unit_of_measurement": "kWh"}},
      "sensor.givtcp_xx0000_soc": {"state": "54", "attributes": {"unit_of_measurement": "%"}},
      "sensor.myenergi_zappi_00000000_charge_added_session": {"state": "0", "attributes": {"unit_of_measurement": "kWh"}},
      "sensor.octopus_energy_electricity_00a0000000_0000000000000_current_rate": {"state": "0.245", "attributes": {"unit_of_measurement": "GBP/kWh"}},
      "sensor.octopus_energy_electricity_00a0000000_0000000000001_export_current_rate": {"state": "0.15", "attributes": {"unit_of_measurement": "GBP/kWh"}},
      "predbat.status": {"state": "Idle", "attributes": {}},
      "update.predbat_version": {"state": "on", "attributes": {"installed_version": "v8.30.1", "latest_version": "v8.30.2"}}
    }
    """;

    [Fact]
    public void AppsYamlValuesAreReadInEveryShapePredbatUses()
    {
        var values = SetupConfigEndpoints.AppsValues(Apps);
        Assert.Equal(["sensor.givtcp_{geserial}_load_energy_today_kwh"], values["load_today"]);
        Assert.Equal(["sensor.givtcp_{geserial}_import_energy_today_kwh"], values["import_today"]);
        Assert.Equal(["sensor.givtcp_{geserial}_export_energy_today_kwh"], values["export_today"]);
        Assert.Equal(["sensor.givtcp_{geserial}_pv_energy_today_kwh"], values["pv_today"]);
        Assert.Equal(["re:(sensor.myenergi_zappi_[0-9a-z]+_charge_added_session)"], values["car_charging_energy"]);
        Assert.Equal(["re:(sensor.(octopus_energy_|)electricity_[0-9a-z]+_[0-9a-z]+_export_current_rate)"], values["metric_octopus_export"]);
        Assert.False(values.ContainsKey("battery_power"));

        var json = SetupConfigEndpoints.AppsValues("""{"pred_bat": {"load_today": ["sensor.a_load_today"], "pv_today": "sensor.a_pv_today"}}""");
        Assert.Equal(["sensor.a_load_today"], json["load_today"]);
        Assert.Equal(["sensor.a_pv_today"], json["pv_today"]);
    }

    [Fact]
    public void TemplatesAndRegexesResolveAgainstTheEntitiesPredbatSees()
    {
        using var state = JsonDocument.Parse(State);
        var root = state.RootElement;
        Assert.Equal("sensor.givtcp_xx0000_load_energy_today_kwh", SetupConfigEndpoints.Resolve("sensor.givtcp_{geserial}_load_energy_today_kwh", root));
        Assert.Equal("sensor.octopus_energy_electricity_00a0000000_0000000000000_current_rate",
            SetupConfigEndpoints.Resolve("re:(sensor.(octopus_energy_|)electricity_[0-9a-z]+_[0-9a-z]+_current_rate)", root));
        Assert.Equal("sensor.anything_literal", SetupConfigEndpoints.Resolve("sensor.anything_literal", root));
        Assert.Null(SetupConfigEndpoints.Resolve("sensor.missing_{geserial}_thing", root));
        Assert.Null(SetupConfigEndpoints.Resolve("re:([unclosed", root));
        Assert.Null(SetupConfigEndpoints.Resolve("50", root));
    }

    sealed class AppsMcp(string text) : IPredbatMcpClient
    {
        public bool Configured => true;
        public McpDiscovery Status => new(true, true, DateTimeOffset.UtcNow, [], null);
        public Task<McpDiscovery> DiscoverAsync(CancellationToken ct = default) => Task.FromResult(Status);
        public Task<McpReadResult> CallReadOnlyAsync(string name, JsonElement arguments, CancellationToken ct = default) =>
            Task.FromResult(new McpReadResult(true, JsonSerializer.Serialize(new { result = new { content = new[] { new { type = "text", text } } } }), false, null));
    }

    [Fact]
    public async Task DetectionPrefersAppsYamlAndFallsBackToNameMatches()
    {
        using var db = new DataStore(Path.Combine(directory, "live"));
        db.SaveSource(State, "{}");
        var config = new ConfigurationBuilder().Build();
        var found = await SetupConfigEndpoints.Detect(db, new AppsMcp(Apps), config, null, default);
        Assert.Equal("Predbat's MCP", found.AppsSource);
        Assert.True(found.HaveEntities);
        var by = found.Meters.ToDictionary(m => m.Metric);
        Assert.Equal("sensor.givtcp_xx0000_load_energy_today_kwh", by["load"].Entity);
        Assert.Equal("load_today in apps.yaml", by["load"].From);
        Assert.Equal("7.4", by["load"].State);
        Assert.Equal("kWh", by["load"].Unit);
        Assert.Equal("HomeAssistant__Entities__Load", by["load"].EnvVar);
        Assert.Equal("sensor.givtcp_xx0000_soc", by["soc"].Entity);
        Assert.Equal("sensor.myenergi_zappi_00000000_charge_added_session", by["ev"].Entity);
        Assert.Equal("metric_octopus_export in apps.yaml", by["export_tariff"].From);
        // Predbat's apps.yaml has no battery charge/discharge energy: those come from the name and unit.
        Assert.Equal("sensor.givtcp_xx0000_battery_charge_energy_today_kwh", by["battery_charge"].Entity);
        Assert.Equal("name and unit match", by["battery_charge"].From);

        // Without MCP or a mounted apps.yaml, every suggestion is a name match.
        var guessed = await SetupConfigEndpoints.Detect(db, null, config, null, default);
        Assert.Null(guessed.AppsSource);
        Assert.Equal("sensor.givtcp_xx0000_load_energy_today_kwh", guessed.Meters.First(m => m.Metric == "load").Entity);
        Assert.All(guessed.Meters.Where(m => m.Entity is not null), m => Assert.Equal("name and unit match", m.From));
    }

    [Fact]
    public async Task AMountedAppsYamlIsUsedWhenThereIsNoMcp()
    {
        Directory.CreateDirectory(Path.Combine(directory, "config"));
        File.WriteAllText(Path.Combine(directory, "config", "apps.yaml"), Apps);
        using var db = new DataStore(Path.Combine(directory, "live"));
        db.SaveSource(State, "{}");
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["ConfigFiles:Root"] = Path.Combine(directory, "config") }).Build();
        var found = await SetupConfigEndpoints.Detect(db, null, config, null, default);
        Assert.Equal("apps.yaml in ConfigFiles:Root", found.AppsSource);
        Assert.Equal("pv_today in apps.yaml", found.Meters.First(m => m.Metric == "pv").From);
    }

    [Fact]
    public async Task BeforePredbatHasBeenReadThereIsNothingToSuggest()
    {
        using var db = new DataStore(Path.Combine(directory, "live"));
        var found = await SetupConfigEndpoints.Detect(db, null, new ConfigurationBuilder().Build(), null, default);
        Assert.False(found.HaveEntities);
        Assert.All(found.Meters, m => Assert.Null(m.Entity));
    }

    [Theory]
    [InlineData("Predbat:BaseUrl", "http://192.168.1.20:5052", true)]
    [InlineData("Predbat:BaseUrl", "https://predbat.example.com/", true)]
    [InlineData("Predbat:BaseUrl", "http://user:pass@host:5052", false)]
    [InlineData("Predbat:BaseUrl", "http://host:5052/?token=1", false)]
    [InlineData("Predbat:BaseUrl", "ftp://host", false)]
    [InlineData("Predbat:BaseUrl", "predbat:5052", false)]
    [InlineData("HomeAssistant:Entities:Load", "sensor.house_load_today", true)]
    [InlineData("HomeAssistant:Entities:Load", "sensor.House Load", false)]
    [InlineData("App:AccessKey", "short", false)]
    [InlineData("App:AccessKey", "a-long-enough-access-key", true)]
    [InlineData("App:AccessKey", "a long key with spaces in", false)]
    [InlineData("App:Demo", "false", true)]
    [InlineData("App:Demo", "maybe", false)]
    [InlineData("Predbat:McpToken", "abc123", true)]
    [InlineData("Predbat:McpToken", "abc 123", false)]
    public void ValuesAreCheckedBeforeTheyAreSaved(string key, string value, bool ok) =>
        Assert.Equal(ok, SavedSettings.Validate(SavedSettings.Field(key)!, value) is null);

    static (SavedSettings Saved, IConfiguration Config) Attach(string dir, Dictionary<string, string?> environment)
    {
        var manager = new ConfigurationManager();
        manager.AddInMemoryCollection(environment);
        var saved = SavedSettings.Attach(manager, manager, dir);
        return (saved, manager);
    }

    [Fact]
    public void SavedSettingsFillInWhatTheEnvironmentLeavesUnsetAndNeverOverrideIt()
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, SavedSettings.FileName), """
            {"App:Demo": "false", "Predbat:BaseUrl": "http://saved:5052", "HomeAssistant:Entities:Load": "sensor.saved_load", "Something:Else": "ignored", "Ai:ApiKey": ""}
            """);
        var (saved, config) = Attach(directory, new() { ["Predbat:BaseUrl"] = "http://from-env:5052", ["HomeAssistant:Entities:Load"] = "" });
        Assert.Equal("http://from-env:5052", config["Predbat:BaseUrl"]);
        Assert.Equal("environment", saved.Source("Predbat:BaseUrl"));
        // An empty environment value (an unfilled line in .env) doesn't count as set.
        Assert.Equal("sensor.saved_load", config["HomeAssistant:Entities:Load"]);
        Assert.Equal("saved", saved.Source("HomeAssistant:Entities:Load"));
        Assert.Equal("false", config["App:Demo"]);
        Assert.Null(config["Something:Else"]);
        Assert.Null(saved.Source("Ai:ApiKey"));
        var refused = Assert.Throws<DomainException>(() => saved.Save(new Dictionary<string, string?> { ["Predbat:BaseUrl"] = "http://other:5052" }));
        Assert.Contains("Predbat__BaseUrl is set in Joule's environment", refused.Message);
    }

    [Fact]
    public void SavingValidatesEverythingFirstAndWritesAPrivateFile()
    {
        var (saved, _) = Attach(directory, []);
        var bad = Assert.Throws<DomainException>(() => saved.Save(new Dictionary<string, string?> { ["Predbat:BaseUrl"] = "http://ok:5052", ["HomeAssistant:Entities:Pv"] = "not an entity" }));
        Assert.Equal(400, bad.Status);
        Assert.False(File.Exists(saved.Path));
        Assert.Throws<DomainException>(() => saved.Save(new Dictionary<string, string?> { ["App:DataDirectory"] = "/tmp" }));

        saved.Save(new Dictionary<string, string?> { ["Predbat:BaseUrl"] = " http://predbat:5052 ", ["App:AccessKey"] = "fixture-access-key-0123", ["App:Demo"] = "false" });
        Assert.Equal("http://predbat:5052", saved.Saved["Predbat:BaseUrl"]);
        if (!OperatingSystem.IsWindows()) Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(saved.Path));
        saved.Save(new Dictionary<string, string?> { ["App:AccessKey"] = null });
        var (again, config) = Attach(directory, []);
        Assert.Equal("http://predbat:5052", config["Predbat:BaseUrl"]);
        Assert.Null(config["App:AccessKey"]);
        Assert.False(again.Saved.ContainsKey("App:AccessKey"));
    }

    [Fact]
    public void BlankEnvironmentLinesCountAsUnset()
    {
        // `App__Demo=` and `Predbat__BaseUrl=` straight from a copied .env.example.
        var (saved, config) = Attach(directory, new() { ["App:Demo"] = "", ["Predbat:BaseUrl"] = " " });
        Assert.True(config.GetValue("App:Demo", true));
        Assert.Null(config["Predbat:BaseUrl"]);
        Assert.Empty(saved.External);
        var (auth, error) = AppAuthOptions.From(config);
        Assert.Null(error);
        Assert.True(auth!.Demo);
        Assert.Contains("App__Demo must be true or false", AppAuthOptions.From(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["App:Demo"] = "yes" }).Build()).Error);
    }

    [Fact]
    public void ABrokenSettingsFileStopsStartupWithAClearMessage()
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, SavedSettings.FileName), "{ not json");
        var e = Assert.Throws<InvalidDataException>(() => Attach(directory, []));
        Assert.Contains("isn't valid JSON", e.Message);
    }

    [Fact]
    public void TheViewNeverCarriesSecretsAndSaysWhereEachValueComesFrom()
    {
        var (saved, _) = Attach(directory, new() { ["Ai:ApiKey"] = "sk-fixture-env-secret" });
        saved.Save(new Dictionary<string, string?> { ["Predbat:McpToken"] = "fixture-mcp-secret", ["Predbat:BaseUrl"] = "http://predbat:5052" });
        var auth = new AppAuthOptions(true, false, null, TimeSpan.FromDays(30), "'none'");
        var view = SetupConfigEndpoints.View(saved, auth, inContainer: true);
        var text = JsonSerializer.Serialize(view, JsonDefaults.Options);
        Assert.DoesNotContain("fixture-mcp-secret", text);
        Assert.DoesNotContain("sk-fixture-env-secret", text);
        var by = view.Fields.ToDictionary(f => f.Key);
        Assert.True(by["Predbat:McpToken"].Set);
        Assert.Equal("saved", by["Predbat:McpToken"].Source);
        Assert.True(by["Predbat:McpToken"].Pending);
        Assert.Equal("environment", by["Ai:ApiKey"].Source);
        Assert.False(by["Ai:ApiKey"].Pending);
        Assert.Equal("http://predbat:5052", by["Predbat:BaseUrl"].Value);
        Assert.Equal("/data/settings.json", view.SettingsFile);
        Assert.True(view.CanSave);
        Assert.True(view.AccessKeyNeeded);
        Assert.True(view.RestartPending);
    }

    [Fact]
    public async Task TheProbeExplainsWhatAnsweredInPlainWords()
    {
        await using var predbat = await FakePredbat.Start(State);
        using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
        var ok = await SetupConfigEndpoints.Probe(http, predbat.Url, null, TimeSpan.FromSeconds(5), default);
        Assert.True(ok.Ok, ok.Error);
        Assert.Equal("8.30.1", ok.Version);
        Assert.Equal(12, ok.Entities);

        await using var other = await FakePredbat.Start("""{"sensor.not_it": {"state": "1"}}""");
        var notPredbat = await SetupConfigEndpoints.Probe(http, other.Url, null, TimeSpan.FromSeconds(5), default);
        Assert.False(notPredbat.Ok);
        Assert.Contains("no Predbat entities", notPredbat.Error);

        await using var html = await FakePredbat.Start("<html>Home Assistant</html>");
        Assert.Contains("not with Predbat's JSON", (await SetupConfigEndpoints.Probe(http, html.Url, null, TimeSpan.FromSeconds(5), default)).Error);

        var closed = FakePredbat.FreePort();
        var refused = await SetupConfigEndpoints.Probe(http, $"http://127.0.0.1:{closed}", null, TimeSpan.FromSeconds(5), default);
        Assert.False(refused.Ok);
        Assert.Contains("Nothing is listening", refused.Error);
    }
}

/// <summary>A stand-in for Predbat's web interface: answers /api/state (and /api/plan_data) with fixed text.</summary>
sealed class FakePredbat : IAsyncDisposable
{
    readonly HttpListener listener = new();
    readonly Task loop;
    public string Url { get; }
    FakePredbat(string state, int port, IReadOnlyDictionary<string, string>? pages = null)
    {
        Url = $"http://127.0.0.1:{port}";
        listener.Prefixes.Add(Url + "/");
        listener.Start();
        loop = Task.Run(async () =>
        {
            while (listener.IsListening)
            {
                HttpListenerContext context;
                try { context = await listener.GetContextAsync(); } catch (Exception e) when (e is HttpListenerException or ObjectDisposedException or InvalidOperationException) { return; }
                var path = context.Request.Url!.AbsolutePath;
                var body = path == "/api/state" ? state : path == "/api/plan_data" ? "{}" : pages?.GetValueOrDefault(path) ?? "";
                context.Response.StatusCode = body.Length > 0 ? 200 : 404;
                context.Response.ContentType = "application/json";
                var bytes = Encoding.UTF8.GetBytes(body);
                try { await context.Response.OutputStream.WriteAsync(bytes); context.Response.Close(); } catch (Exception e) when (e is HttpListenerException or ObjectDisposedException) { }
            }
        });
    }
    public static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0); probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop();
        return port;
    }
    /// <param name="pages">Other paths to answer, such as /debug_apps_live.</param>
    public static Task<FakePredbat> Start(string state, IReadOnlyDictionary<string, string>? pages = null) => Task.FromResult(new FakePredbat(state, FreePort(), pages));
    public async ValueTask DisposeAsync()
    {
        try { listener.Stop(); listener.Close(); } catch (ObjectDisposedException) { }
        try { await loop; } catch (Exception e) when (e is HttpListenerException or ObjectDisposedException) { }
    }
}

[Collection(ChildProcessCollection.Name)]
public class SetupConfigProcessTests
{
    const string State = """{"predbat.status": {"state": "Idle"}, "update.predbat_version": {"state": "on", "attributes": {"installed_version": "v8.30.1"}}, "sensor.house_load_today": {"state": "1.0", "attributes": {"unit_of_measurement": "kWh"}}}""";

    static async Task<string> Started(HttpClient http) =>
        JsonDocument.Parse(await http.GetStringAsync("api/health")).RootElement.GetProperty("started").GetString()!;

    static async Task WaitForRestart(HttpClient http, string before)
    {
        for (var i = 0; i < 150; i++)
        {
            try { if (await Started(http) != before) return; } catch (HttpRequestException) { }
            await Task.Delay(200);
        }
        throw new TimeoutException("Joule did not restart.");
    }

    [Fact]
    public async Task AFreshInstallSwitchesFromTheDemoToItsOwnPredbatFromSetup()
    {
        await using var predbat = await FakePredbat.Start(State);
        // No App__Demo in the environment: the out-of-the-box container. A fixed port, because the restart binds it again.
        await using var app = new JouleProcess(new() { ["App__Demo"] = null, ["ASPNETCORE_URLS"] = $"http://127.0.0.1:{FakePredbat.FreePort()}" });
        Assert.True(await app.Start(), app.Log);
        // Setup's writes need the dashboard's request header even before there is a sign-in.
        Assert.Equal(HttpStatusCode.Forbidden, (await app.Http.PostAsJsonAsync("api/setup/predbat/find", new { })).StatusCode);
        app.Http.DefaultRequestHeaders.Add("X-Joule-Request", "1");
        using (var config = JsonDocument.Parse(await app.Http.GetStringAsync("api/setup/config")))
        {
            Assert.True(config.RootElement.GetProperty("demo").GetBoolean());
            Assert.True(config.RootElement.GetProperty("canSave").GetBoolean());
            Assert.True(config.RootElement.GetProperty("accessKeyNeeded").GetBoolean());
        }
        var test = await app.Http.PostAsJsonAsync("api/setup/predbat/test", new { url = predbat.Url });
        using (var probe = JsonDocument.Parse(await test.Content.ReadAsStringAsync()))
        {
            Assert.True(probe.RootElement.GetProperty("ok").GetBoolean(), probe.RootElement.ToString());
            Assert.Equal("8.30.1", probe.RootElement.GetProperty("version").GetString());
        }
        var find = await app.Http.PostAsJsonAsync("api/setup/predbat/find", new { });
        Assert.Equal(HttpStatusCode.OK, find.StatusCode);
        Assert.Contains("http://predbat:5052", await find.Content.ReadAsStringAsync());

        // Going live without an access key is refused: the dashboard would be open.
        var noKey = await app.Http.PostAsJsonAsync("api/setup/config", new { values = new Dictionary<string, string?> { ["App:Demo"] = "false", ["Predbat:BaseUrl"] = predbat.Url }, restart = true });
        Assert.Equal(HttpStatusCode.BadRequest, noKey.StatusCode);
        Assert.Contains("access key", await noKey.Content.ReadAsStringAsync());

        var before = await Started(app.Http);
        const string key = "fixture-setup-key-0123456789";
        var save = await app.Http.PostAsJsonAsync("api/setup/config", new { values = new Dictionary<string, string?> { ["App:Demo"] = "false", ["Predbat:BaseUrl"] = predbat.Url, ["App:AccessKey"] = key }, restart = true });
        Assert.Equal(HttpStatusCode.OK, save.StatusCode);
        await WaitForRestart(app.Http, before);

        Assert.Equal(HttpStatusCode.Unauthorized, (await app.Http.GetAsync("api/state?view=header")).StatusCode);
        using var request = new HttpRequestMessage(HttpMethod.Get, "api/setup/config");
        request.Headers.Add("X-Access-Key", key);
        using var view = JsonDocument.Parse(await (await app.Http.SendAsync(request)).Content.ReadAsStringAsync());
        Assert.False(view.RootElement.GetProperty("demo").GetBoolean());
        Assert.False(view.RootElement.GetProperty("restartPending").GetBoolean());
        var fields = view.RootElement.GetProperty("fields").EnumerateArray().ToDictionary(f => f.GetProperty("key").GetString()!);
        Assert.Equal("saved", fields["Predbat:BaseUrl"].GetProperty("source").GetString());
        Assert.Equal(predbat.Url, fields["Predbat:BaseUrl"].GetProperty("value").GetString());
        Assert.Equal(JsonValueKind.Null, fields["App:AccessKey"].GetProperty("value").ValueKind);
        Assert.True(fields["App:AccessKey"].GetProperty("set").GetBoolean());
        Assert.DoesNotContain(key, view.RootElement.ToString());
        Assert.Contains(key, File.ReadAllText(Path.Combine(app.Directory, SavedSettings.FileName)));
    }

    [Fact]
    public async Task ALiveJouleFindsItsSensorsFromPredbatWithoutConfigurationAndKeepsTheOnesSetInTheEnvironment()
    {
        const string state = """
            {"predbat.status": {"state": "Idle"}, "update.predbat_version": {"state": "on", "attributes": {"installed_version": "v8.30.1"}},
             "sensor.givtcp_ce2000a000_load_energy_today_kwh": {"state": "7.4", "attributes": {"unit_of_measurement": "kWh"}},
             "sensor.givtcp_ce2000a000_import_energy_today_kwh": {"state": "3.2", "attributes": {"unit_of_measurement": "kWh"}},
             "sensor.givtcp_ce2000a000_pv_energy_today_kwh": {"state": "9.0", "attributes": {"unit_of_measurement": "kWh"}},
             "sensor.my_own_pv_meter": {"state": "8.9", "attributes": {"unit_of_measurement": "kWh"}},
             "sensor.house_meter_today": {"state": "7.1", "attributes": {"unit_of_measurement": "kWh"}},
             "sensor.octopus_energy_electricity_22l0000000_1900000000000_current_standing_charge": {"state": "0.4891", "attributes": {"unit_of_measurement": "GBP"}}}
            """;
        const string apps = """
            pred_bat:
              geserial: ce2000a000
              load_today:
              - sensor.givtcp_{geserial}_load_energy_today_kwh
              import_today:
              - sensor.givtcp_{geserial}_import_energy_today_kwh
              pv_today:
              - sensor.givtcp_{geserial}_pv_energy_today_kwh
              metric_standing_charge: sensor.octopus_energy_electricity_22l0000000_1900000000000_current_standing_charge
            """;
        await using var predbat = await FakePredbat.Start(state, new Dictionary<string, string> { ["/debug_apps_live"] = apps });
        await using var app = new JouleProcess(new() { ["App__Demo"] = "false", ["App__AuthMode"] = "None", ["Predbat__BaseUrl"] = predbat.Url, ["HomeAssistant__Entities__Pv"] = "sensor.my_own_pv_meter" });
        Assert.True(await app.Start(), app.Log);
        Dictionary<string, JsonElement> meters = [];
        for (var i = 0; i < 100 && !meters.ContainsKey("load"); i++)
        {
            using var setup = JsonDocument.Parse(await app.Http.GetStringAsync("api/setup"));
            meters = setup.RootElement.GetProperty("sensors").GetProperty("meters").EnumerateArray().Where(m => m.GetProperty("entity").ValueKind == JsonValueKind.String)
                .ToDictionary(m => m.GetProperty("metric").GetString()!, m => m.Clone());
            if (!meters.ContainsKey("load")) await Task.Delay(200);
        }
        Assert.True(meters.ContainsKey("load"), app.Log);
        Assert.Equal("sensor.givtcp_ce2000a000_load_energy_today_kwh", meters["load"].GetProperty("entity").GetString());
        Assert.Equal("load_today in apps.yaml", meters["load"].GetProperty("foundFrom").GetString());
        Assert.Equal("sensor.octopus_energy_electricity_22l0000000_1900000000000_current_standing_charge", meters["standing_charge"].GetProperty("entity").GetString());
        // The environment's choice stands, and isn't labelled as found.
        Assert.Equal("sensor.my_own_pv_meter", meters["pv"].GetProperty("entity").GetString());
        Assert.Equal(JsonValueKind.Null, meters["pv"].GetProperty("foundFrom").ValueKind);
        var saved = File.ReadAllText(Path.Combine(app.Directory, SensorAutoDetect.FileName));
        Assert.Contains("web interface", saved);
        Assert.DoesNotContain("sensor.my_own_pv_meter", saved);
    }

    [Fact]
    public async Task ADemoPinnedByTheEnvironmentCannotBeSwitchedOrUsedToProbe()
    {
        await using var app = new JouleProcess([]);
        Assert.True(await app.Start(), app.Log);
        app.Http.DefaultRequestHeaders.Add("X-Joule-Request", "1");
        using (var config = JsonDocument.Parse(await app.Http.GetStringAsync("api/setup/config")))
        {
            Assert.False(config.RootElement.GetProperty("canSave").GetBoolean());
            Assert.Contains("App__Demo", config.RootElement.GetProperty("locked").GetString());
        }
        var save = await app.Http.PostAsJsonAsync("api/setup/config", new { values = new Dictionary<string, string?> { ["App:Demo"] = "false" }, restart = true });
        Assert.Equal(HttpStatusCode.Forbidden, save.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await app.Http.PostAsJsonAsync("api/setup/predbat/test", new { url = "http://127.0.0.1:9" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await app.Http.PostAsJsonAsync("api/setup/predbat/find", new { })).StatusCode);
        Assert.False(File.Exists(Path.Combine(app.Directory, SavedSettings.FileName)));
    }

    [Fact]
    public async Task EnvironmentVariablesStillWinOverSavedSettings()
    {
        var directory = Path.Combine(OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath(), "joule-saved-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(Path.Combine(directory, SavedSettings.FileName), """{"App:Demo": "false", "Predbat:BaseUrl": "http://192.0.2.1:5052"}""");
            await using var app = new JouleProcess(new() { ["App__Demo"] = null, ["App__AuthMode"] = "None", ["App__DataDirectory"] = directory, ["Predbat__BaseUrl"] = "http://192.0.2.2:5052" });
            Assert.True(await app.Start(), app.Log);
            using var setup = JsonDocument.Parse(await app.Http.GetStringAsync("api/setup"));
            Assert.False(setup.RootElement.GetProperty("demo").GetBoolean());
            Assert.Equal("http://192.0.2.2:5052", setup.RootElement.GetProperty("predbat").GetProperty("address").GetString());
        }
        finally { try { Directory.Delete(directory, true); } catch (IOException) { } }
    }
}
