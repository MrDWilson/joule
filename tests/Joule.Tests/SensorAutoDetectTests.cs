using System.Net;
using System.Text;
using System.Text.Json;
using Joule;
using Microsoft.Extensions.Configuration;
using Xunit;
namespace Joule.Tests;

/// <summary>
/// Finding the Home Assistant sensors from Predbat without any configuration. The apps.yaml fixtures follow Predbat's own templates
/// (templates/givenergy_givtcp.yaml, tesla_powerwall.yaml, solax_sx4.yaml) and the Octopus Energy integration's sensor names; every
/// serial, MPAN and reading is made up.
/// </summary>
public class SensorAutoDetectTests : IDisposable
{
    readonly string directory = Path.Combine(Path.GetTempPath(), "joule-autodetect-" + Guid.NewGuid());
    public void Dispose() { try { Directory.Delete(directory, true); } catch (IOException) { } }

    static string Entity(string id, string state, string unit, string? deviceClass = null, string? stateClass = null) =>
        $"\"{id}\": {{\"state\": \"{state}\", \"attributes\": {{\"unit_of_measurement\": \"{unit}\"{(deviceClass is null ? "" : $", \"device_class\": \"{deviceClass}\"")}{(stateClass is null ? "" : $", \"state_class\": \"{stateClass}\"")}}}}}";
    static string State(params string[] entities) => "{" + string.Join(",\n", [.. entities, "\"predbat.status\": {\"state\": \"Idle\", \"attributes\": {}}"]) + "}";

    // ---- Octopus Energy integration, the same in every install ----
    const string OctopusApps = """
      metric_octopus_import: 're:(sensor.(octopus_energy_|)electricity_[0-9a-z]+_[0-9a-z]+_current_rate)'
      metric_octopus_export: 're:(sensor.(octopus_energy_|)electricity_[0-9a-z]+_[0-9a-z]+_export_current_rate)'
      metric_standing_charge: 're:(sensor.(octopus_energy_|)electricity_[0-9a-z]+_[0-9a-z]+_current_standing_charge)'
    """;
    static readonly string[] Octopus =
    [
        Entity("sensor.octopus_energy_electricity_22l0000000_1900000000000_current_rate", "0.2451", "GBP/kWh", "monetary"),
        Entity("sensor.octopus_energy_electricity_22l0000000_1900000000001_export_current_rate", "0.15", "GBP/kWh", "monetary"),
        Entity("sensor.octopus_energy_electricity_22l0000000_1900000000000_current_standing_charge", "0.4891", "GBP", "monetary"),
        Entity("sensor.octopus_energy_gas_g4a00000_7000000000_current_standing_charge", "0.3115", "GBP", "monetary"),
        Entity("sensor.octopus_energy_electricity_22l0000000_1900000000000_current_accumulative_cost", "1.92", "GBP", "monetary", "total"),
    ];

    // ---- GivEnergy through GivTCP (templates/givenergy_givtcp.yaml) ----
    const string GivTcpApps = """
    pred_bat:
      module: predbat
      class: PredBat
      prefix: predbat
      timezone: Europe/London
      # Sets the prefix for all created entities in HA - only change if you want to run more than once instance
      geserial: 're:sensor.givtcp_(.+)_soc_kwh'
      # geserial2: 're:sensor.givtcp2_(.+)_soc_kwh'
      load_today:
        - sensor.givtcp_{geserial}_load_energy_today_kwh
      import_today:
        - sensor.givtcp_{geserial}_import_energy_today_kwh
      export_today:
        - sensor.givtcp_{geserial}_export_energy_today_kwh
      pv_today:
        - sensor.givtcp_{geserial}_pv_energy_today_kwh
      num_inverters: 1
      battery_power:
        - sensor.givtcp_{geserial}_battery_power
      soc_kw:
        - sensor.givtcp_{geserial}_soc_kwh
      car_charging_energy: 're:(sensor.myenergi_zappi_[0-9a-z]+_charge_added_session|sensor.wallbox_portal_added_energy)'
    """ + "\n" + OctopusApps;
    static readonly string GivTcpState = State(GivTcpEntities);
    static string[] GivTcpEntities => [
        Entity("sensor.givtcp_ce2000a000_soc_kwh", "4.6", "kWh"),
        Entity("sensor.givtcp_ce2000a000_soc", "54", "%", "battery"),
        Entity("sensor.givtcp_ce2000a000_load_energy_today_kwh", "7.4", "kWh", "energy", "total_increasing"),
        Entity("sensor.givtcp_ce2000a000_load_energy_total_kwh", "8210.1", "kWh", "energy", "total_increasing"),
        Entity("sensor.givtcp_ce2000a000_import_energy_today_kwh", "3.2", "kWh", "energy", "total_increasing"),
        Entity("sensor.givtcp_ce2000a000_export_energy_today_kwh", "1.1", "kWh", "energy", "total_increasing"),
        Entity("sensor.givtcp_ce2000a000_pv_energy_today_kwh", "9.0", "kWh", "energy", "total_increasing"),
        Entity("sensor.givtcp_ce2000a000_battery_charge_energy_today_kwh", "4.0", "kWh", "energy", "total_increasing"),
        Entity("sensor.givtcp_ce2000a000_battery_charge_energy_total_kwh", "2210.0", "kWh", "energy", "total_increasing"),
        Entity("sensor.givtcp_ce2000a000_battery_discharge_energy_today_kwh", "3.0", "kWh", "energy", "total_increasing"),
        Entity("sensor.givtcp_ce2000a000_battery_discharge_energy_total_kwh", "2100.0", "kWh", "energy", "total_increasing"),
        Entity("sensor.givtcp_ce2000a000_battery_power", "-1200", "W", "power"),
        Entity("sensor.myenergi_zappi_21000000_charge_added_session", "0", "kWh", "energy"),
        .. Octopus];

    static async Task<MeterDetection> Detect(string apps, string state, HomeAssistantOptions? options = null)
    {
        var dir = Path.Combine(Path.GetTempPath(), "joule-autodetect-db-" + Guid.NewGuid());
        try { using var db = new DataStore(dir); return await SetupConfigEndpoints.Detect(db, new AppsMcp(apps), new ConfigurationBuilder().Build(), options, default, state); }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }
    static Dictionary<string, MeterSuggestion> By(MeterDetection d) => d.Meters.ToDictionary(m => m.Metric);
    static void Sure(MeterSuggestion m, string entity, string from) { Assert.Equal(entity, m.Entity); Assert.Equal(from, m.From); Assert.True(m.Confident, $"{m.Metric} should be confident"); }

    sealed class AppsMcp(string text) : IPredbatMcpClient
    {
        public bool Configured => true;
        public McpDiscovery Status => new(true, true, DateTimeOffset.UtcNow, [], null);
        public Task<McpDiscovery> DiscoverAsync(CancellationToken ct = default) => Task.FromResult(Status);
        public Task<McpReadResult> CallReadOnlyAsync(string name, JsonElement arguments, CancellationToken ct = default) =>
            Task.FromResult(new McpReadResult(true, JsonSerializer.Serialize(new { result = new { content = new[] { new { type = "text", text } } } }), false, null));
    }

    [Fact]
    public async Task GivTcpWithOctopusIsFoundWithoutAskingAnything()
    {
        var d = await Detect(GivTcpApps, GivTcpState);
        var by = By(d);
        // {geserial} is Predbat's own regex setting; its group is the inverter serial.
        Sure(by["load"], "sensor.givtcp_ce2000a000_load_energy_today_kwh", "load_today in apps.yaml");
        Sure(by["grid_import"], "sensor.givtcp_ce2000a000_import_energy_today_kwh", "import_today in apps.yaml");
        Sure(by["grid_export"], "sensor.givtcp_ce2000a000_export_energy_today_kwh", "export_today in apps.yaml");
        Sure(by["pv"], "sensor.givtcp_ce2000a000_pv_energy_today_kwh", "pv_today in apps.yaml");
        Sure(by["ev"], "sensor.myenergi_zappi_21000000_charge_added_session", "car_charging_energy in apps.yaml");
        Sure(by["import_tariff"], "sensor.octopus_energy_electricity_22l0000000_1900000000000_current_rate", "metric_octopus_import in apps.yaml");
        Sure(by["export_tariff"], "sensor.octopus_energy_electricity_22l0000000_1900000000001_export_current_rate", "metric_octopus_export in apps.yaml");
        Sure(by["standing_charge"], "sensor.octopus_energy_electricity_22l0000000_1900000000000_current_standing_charge", "metric_standing_charge in apps.yaml");
        // GivTCP's template has no soc_percent or battery energy keys: names and units fill those in, the daily counter ahead of the total.
        Sure(by["soc"], "sensor.givtcp_ce2000a000_soc", "name and unit match");
        Sure(by["battery_charge"], "sensor.givtcp_ce2000a000_battery_charge_energy_today_kwh", "name and unit match");
        Sure(by["battery_discharge"], "sensor.givtcp_ce2000a000_battery_discharge_energy_today_kwh", "name and unit match");
        Assert.Null(d.StandingChargePence);

        var (use, ask) = SensorAutoDetect.Choose(d, new HashSet<string>());
        Assert.Equal(11, use.Count);
        Assert.Empty(ask);
    }

    [Fact]
    public async Task TwoGivTcpInvertersAreAChoiceBecausePredbatAddsThemUp()
    {
        var apps = GivTcpApps.Replace("""
              load_today:
                - sensor.givtcp_{geserial}_load_energy_today_kwh
            """, """
              geserial2: 're:sensor.givtcp2_(.+)_soc_kwh'
              load_today:
                - sensor.givtcp_{geserial}_load_energy_today_kwh
                - sensor.givtcp2_{geserial2}_load_energy_today_kwh
            """);
        Assert.Contains("givtcp2_{geserial2}_load", apps);
        var state = State([.. GivTcpEntities, Entity("sensor.givtcp2_ce2000b000_soc_kwh", "4.1", "kWh"), Entity("sensor.givtcp2_ce2000b000_load_energy_today_kwh", "2.2", "kWh")]);
        var d = await Detect(apps, state);
        var load = By(d)["load"];
        Assert.False(load.Confident);
        Assert.Equal("sensor.givtcp_ce2000a000_load_energy_today_kwh", load.Entity);
        Assert.Contains(load.Alternatives, a => a.Entity == "sensor.givtcp2_ce2000b000_load_energy_today_kwh");
        var (use, ask) = SensorAutoDetect.Choose(d, new HashSet<string>());
        Assert.Equal(["load"], ask);
        Assert.False(use.ContainsKey("load"));
        Assert.True(use.ContainsKey("pv"));
    }

    // ---- Tesla Powerwall through Home Assistant's Tesla Fleet integration (templates/tesla_powerwall.yaml) ----
    const string TeslaApps = """
    pred_bat:
      module: predbat
      class: PredBat
      prefix: predbat
      inverter_type: TESLA
      battery_power:
        - sensor.my_home_battery_power
      load_today:
        - sensor.my_home_home_usage
      import_today:
        - sensor.my_home_grid_imported
      export_today:
        - sensor.my_home_grid_exported

      # Replace this with the total PV energy generated today from your inverter
      pv_today: sensor.my_home_solar_generated

      # ---- State of charge ----
      soc_percent:
        - sensor.my_home_percentage_charged
      soc_max:
        - "13.5"  # ensure this matches your usable kWh
      rates_import:
        - start: "00:30:00"
          end: "05:30:00"
          rate: 7.5
        - rate: 27.5
      metric_standing_charge: 0.48
    """;
    static readonly string TeslaState = State(
        Entity("sensor.my_home_home_usage", "11.3", "kWh", "energy", "total_increasing"),
        Entity("sensor.my_home_grid_imported", "6.1", "kWh", "energy", "total_increasing"),
        Entity("sensor.my_home_grid_exported", "0.4", "kWh", "energy", "total_increasing"),
        Entity("sensor.my_home_solar_generated", "5.6", "kWh", "energy", "total_increasing"),
        Entity("sensor.my_home_battery_charged", "7.0", "kWh", "energy", "total_increasing"),
        Entity("sensor.my_home_battery_discharged", "6.2", "kWh", "energy", "total_increasing"),
        Entity("sensor.my_home_percentage_charged", "61", "%", "battery"),
        Entity("sensor.my_home_battery_power", "1.2", "kW", "power"),
        Entity("sensor.my_car_battery_level", "70", "%", "battery"));

    [Fact]
    public async Task ATeslaPowerwallThroughHomeAssistantIsFoundAndItsFixedStandingChargeKept()
    {
        var d = await Detect(TeslaApps, TeslaState);
        var by = By(d);
        Sure(by["load"], "sensor.my_home_home_usage", "load_today in apps.yaml");
        Sure(by["grid_import"], "sensor.my_home_grid_imported", "import_today in apps.yaml");
        Sure(by["grid_export"], "sensor.my_home_grid_exported", "export_today in apps.yaml");
        Sure(by["pv"], "sensor.my_home_solar_generated", "pv_today in apps.yaml");
        Sure(by["soc"], "sensor.my_home_percentage_charged", "soc_percent in apps.yaml");
        Sure(by["battery_charge"], "sensor.my_home_battery_charged", "name and unit match");
        Sure(by["battery_discharge"], "sensor.my_home_battery_discharged", "name and unit match");
        // Fixed rates in apps.yaml: there is no price sensor to read, and none is invented.
        Assert.Null(by["import_tariff"].Entity);
        Assert.Null(by["standing_charge"].Entity);
        Assert.Equal(48, d.StandingChargePence);
    }

    // ---- SolaX through the SolaX Modbus integration (templates/solax_sx4.yaml) ----
    const string SolaxApps = """
    pred_bat:
      module: predbat
      class: PredBat
      prefix: predbat
      num_inverters: 1
      inverter_type: SOLAX
      load_today:
        - sensor.todays_house_load
      import_today:
        - re:(sensor.solax([a-z_]+|)_today_s_import_energy)
      export_today:
        - re:(sensor.solax([a-z_]+|)_today_s_export_energy)
      pv_today:
        - re:(sensor.solax([a-z_]+|)_today_s_solar_energy)
      soc_percent:
        - re:(sensor.solax([a-z_]+|)(?<!chargeable)(?<!remaining)_battery_capacity)
      battery_power:
        - re:(sensor.solax([a-z_]+|)_battery_power_charge)
    """ + "\n" + OctopusApps;
    static readonly string SolaxState = State([
        Entity("sensor.todays_house_load", "9.8", "kWh", "energy", "total_increasing"),
        Entity("sensor.solax_today_s_import_energy", "4.4", "kWh", "energy", "total_increasing"),
        Entity("sensor.solax_today_s_export_energy", "0.9", "kWh", "energy", "total_increasing"),
        Entity("sensor.solax_today_s_solar_energy", "6.6", "kWh", "energy", "total_increasing"),
        Entity("sensor.solax_remaining_battery_capacity", "5.1", "kWh"),
        Entity("sensor.solax_battery_capacity", "47", "%", "battery"),
        Entity("sensor.solax_battery_power_charge", "800", "W", "power"),
        Entity("sensor.solax_battery_input_energy_today", "3.1", "kWh", "energy", "total_increasing"),
        Entity("sensor.solax_battery_output_energy_today", "2.7", "kWh", "energy", "total_increasing"),
        .. Octopus]);

    [Fact]
    public async Task SolaxRegexSettingsResolveTheWayPredbatResolvesThem()
    {
        var d = await Detect(SolaxApps, SolaxState);
        var by = By(d);
        Sure(by["load"], "sensor.todays_house_load", "load_today in apps.yaml");
        Sure(by["grid_import"], "sensor.solax_today_s_import_energy", "import_today in apps.yaml");
        Sure(by["grid_export"], "sensor.solax_today_s_export_energy", "export_today in apps.yaml");
        Sure(by["pv"], "sensor.solax_today_s_solar_energy", "pv_today in apps.yaml");
        // The look-behinds keep "remaining_battery_capacity" (kWh) out, as in Predbat.
        Sure(by["soc"], "sensor.solax_battery_capacity", "soc_percent in apps.yaml");
        Sure(by["import_tariff"], "sensor.octopus_energy_electricity_22l0000000_1900000000000_current_rate", "metric_octopus_import in apps.yaml");
        Sure(by["standing_charge"], "sensor.octopus_energy_electricity_22l0000000_1900000000000_current_standing_charge", "metric_standing_charge in apps.yaml");
        // SolaX calls battery energy "input" and "output": nothing is guessed for charge and discharge.
        Assert.Null(by["battery_charge"].Entity);
        Assert.Null(by["battery_discharge"].Entity);
    }

    [Fact]
    public async Task PredbatsLiveSettingsFromMcpAreUsedAsResolved()
    {
        // MCP's get_apps returns Predbat's settings after it resolved each pattern: geserial is now the serial itself.
        var live = JsonSerializer.Serialize(new
        {
            success = true, error = (string?)null, masked = true,
            data = new Dictionary<string, object>
            {
                ["geserial"] = "ce2000a000", ["load_today"] = new[] { "sensor.givtcp_{geserial}_load_energy_today_kwh" }, ["pv_today"] = new[] { "sensor.givtcp_{geserial}_pv_energy_today_kwh" },
                ["metric_octopus_import"] = "sensor.octopus_energy_electricity_22l0000000_1900000000000_current_rate", ["metric_standing_charge"] = 0.5, ["ha_key"] = "xxx",
            },
        });
        var d = await Detect(live, GivTcpState);
        var by = By(d);
        Sure(by["load"], "sensor.givtcp_ce2000a000_load_energy_today_kwh", "load_today in apps.yaml");
        Sure(by["import_tariff"], "sensor.octopus_energy_electricity_22l0000000_1900000000000_current_rate", "metric_octopus_import in apps.yaml");
        Assert.Equal(50, d.StandingChargePence);
        Assert.Equal("Predbat's MCP", d.AppsSource);
    }

    [Fact]
    public async Task WithoutMcpPredbatsWebInterfaceGivesItsLiveSettings()
    {
        // ruamel.yaml's dump of Predbat's live settings (/debug_apps_live), list items level with their key.
        const string dump = """
        pred_bat:
          module: predbat
          geserial: ce2000a000
          load_today:
          - sensor.givtcp_{geserial}_load_energy_today_kwh
          import_today:
          - sensor.givtcp_{geserial}_import_energy_today_kwh
          metric_octopus_import: sensor.octopus_energy_electricity_22l0000000_1900000000000_current_rate
          ha_key: xxx
        """;
        var handler = new Answer(request => request.RequestUri!.AbsolutePath switch
        {
            "/debug_apps_live" => new(HttpStatusCode.OK) { Content = new StringContent(dump, Encoding.UTF8, "text/plain") },
            _ => new(HttpStatusCode.NotFound),
        });
        using var db = new DataStore(Path.Combine(directory, "live"));
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Predbat:BaseUrl"] = "http://predbat.test:5052" }).Build();
        var d = await SetupConfigEndpoints.Detect(db, null, config, null, default, GivTcpState, new HttpClient(handler));
        Assert.Equal("Predbat's web interface", d.AppsSource);
        Sure(By(d)["load"], "sensor.givtcp_ce2000a000_load_energy_today_kwh", "load_today in apps.yaml");
        Sure(By(d)["grid_import"], "sensor.givtcp_ce2000a000_import_energy_today_kwh", "import_today in apps.yaml");
        Assert.Contains(handler.Paths, p => p == "/debug_apps_live");

        // A Predbat without that page: names and units only.
        var old = new Answer(_ => new(HttpStatusCode.NotFound));
        var guessed = await SetupConfigEndpoints.Detect(db, null, config, null, default, GivTcpState, new HttpClient(old));
        Assert.Null(guessed.AppsSource);
        Assert.Equal("name and unit match", By(guessed)["load"].From);
    }

    sealed class Answer(Func<HttpRequestMessage, HttpResponseMessage> reply) : HttpMessageHandler
    {
        public List<string> Paths { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) { Paths.Add(request.RequestUri!.AbsolutePath); return Task.FromResult(reply(request)); }
    }

    [Fact]
    public async Task ASensorWithTheWrongUnitOrMissingFromPredbatIsNotUsedWithoutAsking()
    {
        var apps = GivTcpApps.Replace("sensor.givtcp_{geserial}_pv_energy_today_kwh", "sensor.givtcp_{geserial}_pv_power").Replace("sensor.givtcp_{geserial}_export_energy_today_kwh", "sensor.renamed_export_today");
        var state = State([.. GivTcpEntities, Entity("sensor.givtcp_ce2000a000_pv_power", "2300", "W", "power")]);
        var by = By(await Detect(apps, state));
        Assert.Equal("sensor.givtcp_ce2000a000_pv_power", by["pv"].Entity);
        Assert.False(by["pv"].Confident);
        Assert.Equal("sensor.renamed_export_today", by["grid_export"].Entity);
        Assert.False(by["grid_export"].Confident);
    }

    [Fact]
    public async Task NameMatchesAreUsedOnlyWhenClearAndNeverForTwoMeters()
    {
        // Two house meters from different integrations: a choice.
        var state = State(
            Entity("sensor.house_consumption_today", "7.4", "kWh", "energy", "total_increasing"),
            Entity("sensor.home_energy_load_today", "7.3", "kWh", "energy", "total_increasing"),
            Entity("sensor.inverter_soc", "54", "%", "battery"));
        var by = By(await Detect("pred_bat:\n  module: predbat\n", state));
        Assert.False(by["load"].Confident);
        Assert.True(by["soc"].Confident);

        // The same sensor named by two keys: neither is sure.
        var clash = By(await Detect("pred_bat:\n  import_today: sensor.grid_today\n  export_today: sensor.grid_today\n", State(Entity("sensor.grid_today", "1", "kWh"))));
        Assert.False(clash["grid_import"].Confident);
        Assert.False(clash["grid_export"].Confident);
    }

    [Fact]
    public void PatternsMatchTheWholeNameAndKeepTheFirstGroupAsPredbatDoes()
    {
        using var state = JsonDocument.Parse(State(Entity("sensor.givtcp_ce2000a000_soc_kwh", "4", "kWh"), Entity("sensor.solax_today_s_import_energy", "1", "kWh")));
        var root = state.RootElement;
        Assert.Equal(["sensor.solax_today_s_import_energy"], SetupConfigEndpoints.ResolveAll("re:(sensor.solax([a-z_]+|)_today_s_import_energy)", root));
        // The whole name has to match, not just its start.
        Assert.Empty(SetupConfigEndpoints.ResolveAll("re:sensor.solax_today_s_import", root));
        // geserial's group is the inverter serial, which is no entity on its own; templates use it.
        Assert.Empty(SetupConfigEndpoints.ResolveAll("re:sensor.givtcp_(.+)_soc_kwh", root));
        Assert.Equal("sensor.givtcp_ce2000a000_soc_kwh", SetupConfigEndpoints.Resolve("sensor.givtcp_{geserial}_soc_kwh", root,
            new Dictionary<string, List<string>> { ["geserial"] = ["re:sensor.givtcp_(.+)_soc_kwh"] }));
    }

    // ---- Using what was found ----

    static HomeAssistantOptions Options(Dictionary<string, string?>? values = null) => new(new ConfigurationBuilder().AddInMemoryCollection(values ?? []).Build());

    [Fact]
    public async Task FoundSensorsAreUsedButNeverReplaceOnesChosenInTheEnvironmentOrSetup()
    {
        var options = Options(new() { ["HomeAssistant:Entities:Load"] = "sensor.my_own_load", ["HomeAssistant:Entities:Ev"] = "none" });
        Assert.Equal(["ev", "load"], options.Chosen.Order());
        Assert.Equal(["ev"], options.Declined);
        Assert.False(options.Entities.ContainsKey("ev"));
        var d = await Detect(GivTcpApps, GivTcpState, options);
        var auto = new SensorAutoDetect(options, Path.Combine(directory, SensorAutoDetect.FileName), _ => Task.FromResult(d), new ManualClock(DateTimeOffset.UnixEpoch));
        await auto.RunIfDueAsync(default);
        Assert.Equal("sensor.my_own_load", options.Entities["load"]);
        Assert.False(options.Entities.ContainsKey("ev"));
        Assert.False(options.Detected.ContainsKey("load"));
        Assert.Equal("sensor.givtcp_ce2000a000_pv_energy_today_kwh", options.Entities["pv"]);
        Assert.Equal(new DetectedSensor("sensor.givtcp_ce2000a000_pv_energy_today_kwh", "pv_today in apps.yaml"), options.Detected["pv"]);
        Assert.Equal("sensor.octopus_energy_electricity_22l0000000_1900000000000_current_standing_charge", options.Entities["standing_charge"]);
        Assert.Empty(options.NeedsChoice);

        // A restart uses what was found straight away, before Predbat is read again.
        var restarted = Options(new() { ["HomeAssistant:Entities:Load"] = "sensor.my_own_load" });
        _ = new SensorAutoDetect(restarted, Path.Combine(directory, SensorAutoDetect.FileName), _ => throw new InvalidOperationException("not yet"));
        Assert.Equal("sensor.givtcp_ce2000a000_pv_energy_today_kwh", restarted.Entities["pv"]);
        Assert.Equal("sensor.my_own_load", restarted.Entities["load"]);
        // A detection file can't put a sensor where the person has since chosen one.
        Assert.False(restarted.Detected.ContainsKey("load"));
    }

    [Fact]
    public async Task DetectionRunsOnTheFirstPollThenEverySixHoursAndKeepsWhatItHadWhenPredbatIsAway()
    {
        var clock = new ManualClock(new DateTimeOffset(2026, 10, 7, 9, 0, 0, TimeSpan.Zero));
        var options = Options();
        var calls = 0; MeterDetection? next = await Detect(GivTcpApps, GivTcpState);
        var auto = new SensorAutoDetect(options, null, _ => { calls++; return Task.FromResult(next!); }, clock);
        Assert.True(auto.Due);
        await auto.RunIfDueAsync(default);
        Assert.Equal(1, calls);
        Assert.Equal(11, options.Detected.Count);
        Assert.Equal(clock.Now, auto.LastDetected);

        clock.Now += TimeSpan.FromHours(5);
        await auto.RunIfDueAsync(default);
        Assert.Equal(1, calls);

        // Predbat can't be read: nothing is dropped, and Joule tries again sooner.
        clock.Now += TimeSpan.FromHours(1);
        next = new MeterDetection(null, null, false, []);
        await auto.RunIfDueAsync(default);
        Assert.Equal(2, calls);
        Assert.Equal(11, options.Detected.Count);
        clock.Now += SensorAutoDetect.Retry;
        next = await Detect(TeslaApps, TeslaState);
        await auto.RunIfDueAsync(default);
        Assert.Equal(3, calls);
        // Predbat changed: what it found before is replaced, not added to.
        Assert.Equal("sensor.my_home_home_usage", options.Entities["load"]);
        Assert.False(options.Entities.ContainsKey("import_tariff"));
        Assert.Equal(48, options.FixedStandingChargePence);
    }

    [Fact]
    public async Task AnAutomaticSensorMakesCollectionPossibleWithNothingConfigured()
    {
        var options = Options(new() { ["Predbat:BaseUrl"] = "http://predbat.test:5052" });
        var client = new HomeAssistantClient(new HttpClient(new Answer(_ => new(HttpStatusCode.NotFound))), options, new MirrorReady());
        Assert.False(client.Configured);
        var auto = new SensorAutoDetect(options, null, async _ => await Detect(GivTcpApps, GivTcpState));
        await auto.RunIfDueAsync(default);
        Assert.True(client.Configured);
    }

    sealed class MirrorReady : IPredbatEntityReader
    {
        public bool Configured => true;
        public Task<System.Text.Json.Nodes.JsonArray?> ReadEntityStatesAsync(IReadOnlyCollection<string> entities, CancellationToken ct) => Task.FromResult<System.Text.Json.Nodes.JsonArray?>(null);
    }

    [Theory]
    [InlineData("0.4891", "GBP", 48.91, "observed")]
    [InlineData("48.91", "p", 48.91, "observed")]
    [InlineData("0.5", "£/day", 50.0, "observed")]
    [InlineData("0.5", "GBP/kWh", null, "unsupported_unit")]
    [InlineData("-1", "GBP", null, "unsupported_unit")]
    public void TheStandingChargeIsReadInPenceADay(string state, string unit, double? pence, string status)
    {
        var (value, normalisedUnit, result) = HomeAssistantClient.Normalize("standing_charge", state, unit);
        Assert.Equal(pence, value is null ? null : Math.Round(value.Value, 4));
        Assert.Equal("p/day", normalisedUnit);
        Assert.Equal(status, result);
    }

    [Fact]
    public void NoneIsAValidChoiceForAMeter()
    {
        Assert.Null(SavedSettings.Validate(SavedSettings.Field("HomeAssistant:Entities:Ev")!, "none"));
        Assert.Null(SavedSettings.Validate(SavedSettings.Field("HomeAssistant:Entities:StandingCharge")!, "sensor.octopus_energy_electricity_x_y_current_standing_charge"));
        Assert.NotNull(SavedSettings.Validate(SavedSettings.Field("HomeAssistant:Entities:Ev")!, "nothing"));
    }
}
