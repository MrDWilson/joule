using System.Net;
using System.Text.Json;
using Joule;
using Xunit;
namespace Joule.Tests;

public class SetupLogicTests
{
    // Live-shaped, made up: no real serials.
    const string PredbatState = """
    {
      "sensor.givtcp_xx0000_load_energy_today_kwh": {"state": "7.41", "attributes": {"unit_of_measurement": "kWh", "device_class": "energy", "state_class": "total_increasing", "friendly_name": "Load Energy Today"}},
      "sensor.givtcp_xx0000_load_energy_total_kwh": {"state": "9123.4", "attributes": {"unit_of_measurement": "kWh", "device_class": "energy", "state_class": "total_increasing"}},
      "sensor.givtcp_xx0000_load_power": {"state": "412", "attributes": {"unit_of_measurement": "W", "device_class": "power"}},
      "sensor.givtcp_xx0000_pv_energy_today_kwh": {"state": "unknown", "attributes": {"unit_of_measurement": "kWh", "device_class": "energy"}},
      "sensor.solcast_pv_forecast_forecast_today": {"state": "12.1", "attributes": {"unit_of_measurement": "kWh"}},
      "sensor.givtcp_xx0000_import_energy_today_kwh": {"state": "3.2", "attributes": {"unit_of_measurement": "kWh", "device_class": "energy"}},
      "sensor.givtcp_xx0000_export_energy_today_kwh": {"state": "1.1", "attributes": {"unit_of_measurement": "kWh", "device_class": "energy"}},
      "sensor.givtcp_xx0000_battery_charge_energy_today_kwh": {"state": "4.0", "attributes": {"unit_of_measurement": "kWh"}},
      "sensor.givtcp_xx0000_battery_discharge_energy_today_kwh": {"state": "3.0", "attributes": {"unit_of_measurement": "kWh"}},
      "sensor.givtcp_xx0000_soc": {"state": "54", "attributes": {"unit_of_measurement": "%", "device_class": "battery"}},
      "sensor.givtcp_xx0000_target_soc": {"state": "100", "attributes": {"unit_of_measurement": "%"}},
      "sensor.myenergi_zappi_charge_added_session": {"state": "0", "attributes": {"unit_of_measurement": "kWh"}},
      "sensor.octopus_energy_electricity_current_rate": {"state": "0.245", "attributes": {"unit_of_measurement": "GBP/kWh"}},
      "sensor.octopus_energy_electricity_export_current_rate": {"state": "0.15", "attributes": {"unit_of_measurement": "GBP/kWh"}},
      "predbat.load_energy": {"state": "20", "attributes": {"unit_of_measurement": "kWh"}},
      "sensor.predbat_load_energy_h0": {"state": "20", "attributes": {"unit_of_measurement": "kWh"}}
    }
    """;

    [Fact]
    public void SuggestsTheDailyMetersPredbatSeesForEachMetric()
    {
        using var json = JsonDocument.Parse(PredbatState);
        var found = SensorCandidates.Find(json.RootElement);
        Assert.Equal("sensor.givtcp_xx0000_load_energy_today_kwh", found["load"][0].Entity);
        Assert.Equal("Load Energy Today", found["load"][0].Name);
        Assert.Equal("7.41", found["load"][0].State);
        Assert.DoesNotContain(found["load"], c => c.Entity.Contains("power") || c.Entity.Contains("predbat"));
        Assert.Equal("sensor.givtcp_xx0000_pv_energy_today_kwh", Assert.Single(found["pv"]).Entity);
        Assert.Equal("sensor.givtcp_xx0000_import_energy_today_kwh", found["grid_import"][0].Entity);
        Assert.Equal("sensor.givtcp_xx0000_export_energy_today_kwh", found["grid_export"][0].Entity);
        Assert.Equal("sensor.givtcp_xx0000_battery_charge_energy_today_kwh", Assert.Single(found["battery_charge"]).Entity);
        Assert.Equal("sensor.givtcp_xx0000_battery_discharge_energy_today_kwh", Assert.Single(found["battery_discharge"]).Entity);
        Assert.Equal("sensor.givtcp_xx0000_soc", Assert.Single(found["soc"]).Entity);
        Assert.Equal("sensor.myenergi_zappi_charge_added_session", Assert.Single(found["ev"]).Entity);
        Assert.Equal("sensor.octopus_energy_electricity_current_rate", Assert.Single(found["import_tariff"]).Entity);
        Assert.Equal("sensor.octopus_energy_electricity_export_current_rate", Assert.Single(found["export_tariff"]).Entity);
    }

    [Fact]
    public void NoStateMeansNoSuggestions()
    {
        using var json = JsonDocument.Parse("[]");
        Assert.All(SensorCandidates.Find(json.RootElement).Values, Assert.Empty);
    }

    [Theory]
    [InlineData("http://192.168.1.20:5052/", "http://192.168.1.20:5052")]
    [InlineData("https://ha.example.com/path?token=abc#x", "https://ha.example.com/path")]
    [InlineData("http://user:pass@host:8123", "http://host:8123")]
    [InlineData("ftp://host", null)]
    [InlineData(null, null)]
    public void AddressesNeverCarryCredentials(string? url, string? expected) => Assert.Equal(expected, SetupEndpoints.SafeAddress(url));

    sealed class FakeMcp(bool configured, McpReadResult result) : IPredbatMcpClient
    {
        public List<string> Calls { get; } = [];
        public bool Configured => configured;
        public McpDiscovery Status => new(configured, true, DateTimeOffset.UtcNow, [], null);
        public Task<McpDiscovery> DiscoverAsync(CancellationToken ct = default) => Task.FromResult(Status);
        public Task<McpReadResult> CallReadOnlyAsync(string name, JsonElement arguments, CancellationToken ct = default) { Calls.Add($"{name} {arguments.GetRawText()}"); return Task.FromResult(result); }
    }
    static string Wrap(string text) => JsonSerializer.Serialize(new { truncated = false, result = new { content = new[] { new { type = "text", text } } } });

    [Fact]
    public async Task AppsViewAsksForMaskedAppsAndHidesAnythingThatStillLooksSecret()
    {
        var mcp = new FakeMcp(true, new(true, Wrap("pred_bat:\n  inverter_type: GE\n  api_key: abc123\n  load_today:\n    - sensor.givtcp_xx0000_load_energy_today_kwh\n"), false, null));
        var view = await SetupEndpoints.PredbatApps(mcp, DateTimeOffset.UtcNow, default);
        Assert.True(view.Available);
        Assert.Equal("get_apps {\"masked\":true}", Assert.Single(mcp.Calls));
        Assert.Contains("inverter_type: GE", view.Text);
        Assert.Contains("- sensor.givtcp_xx0000_load_energy_today_kwh", view.Text);
        Assert.Contains("api_key: •••", view.Text);
        Assert.DoesNotContain("abc123", view.Text);
    }

    [Fact]
    public async Task AppsViewUnwrapsAJsonEnvelope()
    {
        var mcp = new FakeMcp(true, new(true, Wrap(JsonSerializer.Serialize(new { apps = "pred_bat:\n  token: zzz\n  pv_scaling: 1.0\n" })), false, null));
        var view = await SetupEndpoints.PredbatApps(mcp, DateTimeOffset.UtcNow, default);
        Assert.True(view.Available);
        Assert.Contains("pv_scaling: 1.0", view.Text);
        Assert.Contains("token: •••", view.Text);
    }

    [Fact]
    public async Task AppsViewExplainsWhyItIsUnavailable()
    {
        Assert.False((await SetupEndpoints.PredbatApps(null, DateTimeOffset.UtcNow, default)).Available);
        var off = await SetupEndpoints.PredbatApps(new FakeMcp(false, new(true, "{}", false, null)), DateTimeOffset.UtcNow, default);
        Assert.False(off.Available);
        Assert.Contains("isn't set up", off.Reason);
        var failed = await SetupEndpoints.PredbatApps(new FakeMcp(true, new(false, "{}", false, "MCP read timed out.")), DateTimeOffset.UtcNow, default);
        Assert.Equal("MCP read timed out.", failed.Reason);
    }
}

[Collection(ChildProcessCollection.Name)]
public class SetupEndpointTests
{
    [Fact]
    public async Task DemoSetupIsCompleteAndMatchesTheHeaderProgress()
    {
        await using var app = new JouleProcess(new() { ["App__AuthMode"] = "None" });
        Assert.True(await app.Start(), app.Log);
        using var setup = JsonDocument.Parse(await app.Http.GetStringAsync("api/setup"));
        using var header = JsonDocument.Parse(await app.Http.GetStringAsync("api/state?view=header"));
        Assert.True(setup.RootElement.GetProperty("demo").GetBoolean());
        Assert.True(setup.RootElement.GetProperty("progress").GetProperty("requiredDone").GetBoolean());
        static string[] Steps(JsonElement p) => [.. p.GetProperty("steps").EnumerateArray().Select(s => $"{s.GetProperty("key").GetString()}:{s.GetProperty("label").GetString()}:{s.GetProperty("done").GetBoolean()}:{s.GetProperty("required").GetBoolean()}"),
            $"{p.GetProperty("done").GetInt32()}/{p.GetProperty("total").GetInt32()}"];
        Assert.Equal(Steps(header.RootElement.GetProperty("setupProgress")), Steps(setup.RootElement.GetProperty("progress")));
        Assert.Equal(10, setup.RootElement.GetProperty("sensors").GetProperty("meters").GetArrayLength());
    }

    [Fact]
    public async Task AFreshLiveInstallIsNotSetUpAndNamesWhatToSet()
    {
        await using var app = new JouleProcess(new() { ["App__AuthMode"] = "None", ["App__Demo"] = "false", ["Predbat__BaseUrl"] = null });
        Assert.True(await app.Start(), app.Log);
        var response = await app.Http.GetAsync("api/setup");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var setup = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = setup.RootElement;
        Assert.False(root.GetProperty("demo").GetBoolean());
        Assert.False(root.GetProperty("progress").GetProperty("requiredDone").GetBoolean());
        Assert.False(root.GetProperty("predbat").GetProperty("configured").GetBoolean());
        var load = root.GetProperty("sensors").GetProperty("meters").EnumerateArray().First(m => m.GetProperty("metric").GetString() == "load");
        Assert.Equal("HomeAssistant__Entities__Load", load.GetProperty("envVar").GetString());
        Assert.True(load.GetProperty("required").GetBoolean());
        Assert.False(root.GetProperty("mcp").GetProperty("configured").GetBoolean());
        using var apps = JsonDocument.Parse(await app.Http.GetStringAsync("api/setup/predbat-apps"));
        Assert.False(apps.RootElement.GetProperty("available").GetBoolean());
    }

    [Fact]
    public async Task ThePredbatAddressIsShownWithoutAnythingSecret()
    {
        await using var app = new JouleProcess(new() { ["App__AuthMode"] = "None", ["App__Demo"] = "false", ["Predbat__BaseUrl"] = "http://192.0.2.10:5052/" });
        Assert.True(await app.Start(), app.Log);
        using var setup = JsonDocument.Parse(await app.Http.GetStringAsync("api/setup"));
        Assert.Equal("http://192.0.2.10:5052", setup.RootElement.GetProperty("predbat").GetProperty("address").GetString());
        Assert.True(setup.RootElement.GetProperty("predbat").GetProperty("configured").GetBoolean());
    }
}
