using System.Net;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Joule;
using Xunit;

namespace Joule.Tests;

/// <summary>
/// An idle EV charger reports Home Assistant's standard "unknown" state between sessions. That is "no value", not corrupt data,
/// so it is recorded as idle (behaviour change: it used to be stored as unavailable) and must not fail every collection for the
/// sensors that did report. "unavailable" (device offline) stays unavailable.
/// </summary>
public sealed class OptionalSensorCollectionTests : IDisposable
{
    readonly string path=Path.Combine(Path.GetTempPath(),"predbat-optional-sensor-"+Guid.NewGuid().ToString("N"));
    static readonly DateTimeOffset At=DateTimeOffset.Parse("2020-01-01T10:00:00Z");
    static IConfiguration Config()=>new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> {
        ["App:Demo"]="false",["HomeAssistant:BaseUrl"]="http://fixture.test",["HomeAssistant:AccessToken"]="fixture",
        ["HomeAssistant:Entities:Load"]="sensor.load",["HomeAssistant:Entities:Pv"]="sensor.pv",["HomeAssistant:Entities:Ev"]="sensor.hypervolt_session_energy_total_increasing" }).Build();
    static JsonObject State(string entity,string state,string unit)=>new(){["entity_id"]=entity,["state"]=state,["last_updated"]="2020-01-01T09:59:00Z",["attributes"]=new JsonObject{["unit_of_measurement"]=unit,["state_class"]="total_increasing"}};
    static TelemetryCollectionService Service(DataStore db,params JsonObject[] states)
    {
        var config=Config();var options=new HomeAssistantOptions(config);
        var client=new HomeAssistantClient(new HttpClient(new Handler(_=>new(HttpStatusCode.OK){Content=new StringContent(new JsonArray(states.Select(x=>x.DeepClone()).ToArray()).ToJsonString())})),options);
        return new TelemetryCollectionService(db,client,options,config);
    }

    [Theory]
    [InlineData("ev","unknown","Wh","idle")]
    [InlineData("ev","Unavailable","Wh","unavailable")]
    [InlineData("load","UNKNOWN","kWh","idle")]
    [InlineData("soc","unavailable","%","unavailable")]
    [InlineData("import_tariff","unknown","p/kWh","idle")]
    public void HomeAssistantUnknownIsIdleAndUnavailableIsOfflineNeitherIsInvalid(string metric,string rawState,string unit,string status)
    {
        var client=new HomeAssistantClient(new HttpClient(),new HomeAssistantOptions(Config()));
        var sample=client.Parse(metric,"sensor.fixture",State("sensor.fixture",rawState,unit),At);
        Assert.Equal(status,sample.Status);Assert.Null(sample.Value);Assert.Equal(rawState,sample.RawState);
    }
    [Fact]
    public void GenuinelyUnparseableStateStaysInvalid()
    {
        var client=new HomeAssistantClient(new HttpClient(),new HomeAssistantOptions(Config()));
        var sample=client.Parse("ev","sensor.fixture",State("sensor.fixture","not-a-number","Wh"),At);
        Assert.Equal("invalid",sample.Status);Assert.Null(sample.Value);
    }
    [Fact]
    public async Task IdleEvChargerReportingUnknownDoesNotFailCollection()
    {
        using var db=new DataStore(path);
        var service=Service(db,State("sensor.load","10","kWh"),State("sensor.pv","5","kWh"),State("sensor.hypervolt_session_energy_total_increasing","unknown","Wh"));
        await service.CollectAsync(default);
        var status=service.Status();
        Assert.Null(status.Error);Assert.NotNull(status.LastCollection);Assert.Equal(HomeAssistantClient.DirectSource,status.LastSource);
        Assert.Equal(10,status.LatestReadings["load"].Value);Assert.Equal(5,status.LatestReadings["pv"].Value);
        Assert.Null(status.LatestReadings["ev"].Value);Assert.Equal("idle",status.LatestReadings["ev"].Status);Assert.Equal("unknown",status.LatestReadings["ev"].RawState);
        Assert.True(status.LatestReadings["ev"].Expected);Assert.Contains("Not charging",status.LatestReadings["ev"].Reason);
    }
    // Behaviour change: a required sensor that has just gone offline no longer fails the poll at once. Home Assistant restarts take a
    // few minutes; only an unexpected outage longer than 30 minutes (15 for battery level and tariffs) is raised, and the optional EV
    // charger never is.
    [Fact]
    public async Task RequiredPvOfflineIsRaisedOnlyAfterTheGracePeriodAndNamesTheMetric()
    {
        using var db=new DataStore(path);
        var service=Service(db,State("sensor.load","10","kWh"),State("sensor.pv","unavailable","kWh"),State("sensor.hypervolt_session_energy_total_increasing","unknown","Wh"));
        await service.CollectAsync(default);
        Assert.Null(service.Status().Error);Assert.False(service.Status().LatestReadings["pv"].Expected);
        // The last usable solar reading was 40 minutes ago.
        var old=DateTimeOffset.UtcNow.AddMinutes(-40);
        db.SaveTelemetry([new("pv","sensor.pv",old,5,"kWh","HomeAssistant","5","kWh",old)]);
        var error=await Assert.ThrowsAsync<DomainException>(()=>service.CollectAsync(default));
        Assert.Contains("pv (offline for 40 min)",error.Message);Assert.DoesNotContain("ev",error.Message.Split("Other sensors")[0]);Assert.DoesNotContain("load",error.Message.Split("Other sensors")[0]);
        var status=service.Status();
        Assert.NotNull(status.LastCollection);Assert.NotNull(status.Error);Assert.Contains("pv",status.Error);
        Assert.Equal("pv",Assert.Single(status.Issues).Metric);
        Assert.Equal(10,status.LatestReadings["load"].Value);Assert.Null(status.LatestReadings["pv"].Value);Assert.Equal(5,status.LatestReadings["pv"].LastObservedValue);
    }

    public void Dispose(){if(Directory.Exists(path))Directory.Delete(path,true);}
    sealed class Handler(Func<HttpRequestMessage,HttpResponseMessage> reply):HttpMessageHandler
    {protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)=>Task.FromResult(reply(request));}
}
