using System.Net;
using Microsoft.Extensions.Configuration;
using System.Text.Json.Nodes;
using Joule;
using Xunit;
namespace Joule.Tests;
public class HomeAssistantTests
{
    static HomeAssistantOptions Options(Dictionary<string,string?>? extra=null)
    {
        var values=new Dictionary<string,string?> { ["HomeAssistant:BaseUrl"]="http://ha.test",["HomeAssistant:AccessToken"]="test-secret",["HomeAssistant:Entities:Load"]="sensor.house_energy" };
        foreach(var (key,value) in extra??[])values[key]=value;
        return new(new ConfigurationBuilder().AddInMemoryCollection(values).Build());
    }
    static readonly DateTimeOffset At=DateTimeOffset.Parse("2026-01-01T10:00:00Z");
    [Theory][InlineData("load","1000","Wh",1,"kWh")][InlineData("import_tariff","0.25","GBP/kWh",25,"p/kWh")][InlineData("export_tariff","150","GBP/MWh",15,"p/kWh")][InlineData("soc","72","%",72,"%")]
    public void NormalizesAuthoritativeUnits(string metric,string state,string unit,double expected,string normalized)
    {
        var client=new HomeAssistantClient(new HttpClient(),Options());
        var value=client.Parse(metric,"sensor.fixture",new JsonObject { ["state"]=state,["last_updated"]="2026-01-01T09:59:00Z",["attributes"]=new JsonObject { ["unit_of_measurement"]=unit } },At);
        Assert.Equal(expected,value.Value);Assert.Equal(normalized,value.Unit);Assert.Equal(unit,value.RawUnit);
    }
    [Fact] public void PowerUnitsCannotFabricateActualEnergy()
    {
        var client=new HomeAssistantClient(new HttpClient(),Options());
        var state=JsonNode.Parse("""{"state":"100","last_updated":"2026-01-01T09:59:00Z","last_reported":"2026-01-01T09:59:00Z","attributes":{"unit_of_measurement":"W"}}""")!.AsObject();
        var sample=client.Parse("load","sensor.fixture",state,At);
        Assert.Null(sample.Value);Assert.Equal("unsupported_unit",sample.Status);
    }
    [Fact] public void UnchangedCounterWithOldLastReportedStaysObservedAndRecordsAge()
    {
        // Push-based integrations (Teslemetry, Octopus) only write state on change. HA's unavailable state is the offline signal.
        var client=new HomeAssistantClient(new HttpClient(),Options());
        var state=JsonNode.Parse("""{"state":"100","last_updated":"2026-01-01T09:00:00Z","last_reported":"2026-01-01T09:00:00Z","attributes":{"unit_of_measurement":"kWh"}}""")!.AsObject();
        var sample=client.Parse("load","sensor.fixture",state,At);
        Assert.Equal(100,sample.Value);Assert.Equal("observed",sample.Status);
        Assert.Contains("\"reported_age_seconds\":3600",sample.AttributesJson);Assert.Contains("2026-01-01T09:00:00",sample.AttributesJson);
    }
    [Fact] public async Task ReadsOnlyMappedSensorsAndKeepsTokenOutOfEvidence()
    {
        var handler=new Handler(request=>
        {
            Assert.Equal("/api/states",request.RequestUri!.AbsolutePath);Assert.Equal("test-secret",request.Headers.Authorization!.Parameter);
            return new(HttpStatusCode.OK){Content=new StringContent("""[{"entity_id":"sensor.house_energy","state":"12","last_updated":"2026-01-01T09:59:00Z","last_reported":"2026-01-01T09:59:00Z","attributes":{"unit_of_measurement":"kWh","password":"test-secret","nested":{"token":"test-secret"}}},{"entity_id":"sensor.unmapped","state":"private"}]""")};
        });
        var result=Assert.Single(await new HomeAssistantClient(new HttpClient(handler),Options()).CollectAsync(default));
        Assert.DoesNotContain("test-secret",result.AttributesJson);Assert.DoesNotContain("password",result.AttributesJson);
        Assert.Equal(12,result.Value); // an old last_reported on an unchanged counter is recorded, not treated as stale
    }
    [Fact] public void OptionalIntelligentAndAlternativeForecastAttributesPreserveProvenance()
    {
        var client=new HomeAssistantClient(new HttpClient(),Options());
        var state=JsonNode.Parse("""{"state":"on","attributes":{"planned_dispatches":[{"start":"2026-01-01T10:00:00Z","end":"2026-01-01T10:30:00Z"}],"results":{"2026-01-01T10:00:00Z":1.2}}}""")!.AsObject();
        var value=client.Parse("intelligent_slots","sensor.dispatches",state,At);
        Assert.Contains("planned_dispatches",value.AttributesJson);Assert.Equal("sensor.dispatches",value.EntityId);Assert.Null(value.Value);
    }
    [Fact] public void UnchangedNightCounterUsesSuccessfulPollTimeWithExplicitFreshnessLimit()
    {
        var client=new HomeAssistantClient(new HttpClient(),Options());
        var state=JsonNode.Parse("""{"state":"10","last_updated":"2025-12-31T16:00:00Z","attributes":{"unit_of_measurement":"kWh"}}""")!.AsObject();
        var first=client.Parse("pv","sensor.pv",state,At);var second=client.Parse("pv","sensor.pv",state,At.AddMinutes(5));
        Assert.Equal(10,first.Value);Assert.Equal(At,first.Time);Assert.Equal(At.AddMinutes(5),second.Time);Assert.Contains("unknown_device_freshness",first.AttributesJson);
        var path=Path.Combine(Path.GetTempPath(),"night-test-"+Guid.NewGuid().ToString("N"));
        try{using var db=new DataStore(path);db.SaveTelemetry([first,second]);Assert.Equal(0,db.ReadEnergySummary(At,At.AddMinutes(5)).Metrics["pv"].EnergyKwh);Assert.Equal(1,db.ReadEnergySummary(At,At.AddMinutes(5)).Metrics["pv"].CoverageFraction);}finally{if(Directory.Exists(path))Directory.Delete(path,true);}
    }
    [Fact] public void MappedSensorCannotExposeCredentialUrlsOrPrivateMetadata()
    {
        var client=new HomeAssistantClient(new HttpClient(),Options());
        var state=JsonNode.Parse("""{"state":"on","attributes":{"owner_email":"person@example.test","callback":"https://user:pass@ha.test","headers":{"auth_header":"Bearer fixture-secret"},"dispatches":[{"start":"2026-01-01T10:00:00Z"}]}}""")!.AsObject();
        var sample=client.Parse("intelligent_slots","sensor.slots",state,At);
        Assert.DoesNotContain("user:pass",sample.AttributesJson);Assert.DoesNotContain("person@example.test",sample.AttributesJson);Assert.DoesNotContain("fixture-secret",sample.AttributesJson);Assert.Contains("dispatches",sample.AttributesJson);
    }
    static JsonArray Mirror(string state="12")=>JsonNode.Parse($$$"""[{"entity_id":"sensor.house_energy","state":"{{{state}}}","last_changed":"2026-01-01T09:59:00Z","attributes":{"unit_of_measurement":"kWh"}}]""")!.AsArray();
    static HomeAssistantOptions MirrorOnlyOptions()=>new(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{["HomeAssistant:Entities:Load"]="sensor.house_energy"}).Build());
    [Fact] public async Task FallsBackToPredbatMirrorWhenHomeAssistantIsUnreachable()
    {
        var mirror=new MirrorReader(Mirror());
        var client=new HomeAssistantClient(new HttpClient(new Handler(_=>new(HttpStatusCode.ServiceUnavailable))),Options(),mirror);
        var sample=Assert.Single(await client.CollectAsync(default));
        Assert.Equal(12,sample.Value);Assert.Equal("Predbat mirror",sample.Source);Assert.Equal("observed",sample.Status);
        Assert.Contains("predbat_mirror",sample.AttributesJson);Assert.Equal(new[]{"sensor.house_energy"},mirror.Requested);
    }
    [Fact] public async Task UsesPredbatMirrorWhenHomeAssistantCredentialsAreAbsent()
    {
        var client=new HomeAssistantClient(new HttpClient(new Handler(_=>throw new InvalidOperationException("Home Assistant must not be called without credentials."))),MirrorOnlyOptions(),new MirrorReader(Mirror()));
        Assert.True(client.Configured);
        var sample=Assert.Single(await client.CollectAsync(default));
        Assert.Equal(12,sample.Value);Assert.Equal("Predbat mirror",sample.Source);
    }
    [Fact] public async Task PrefersHomeAssistantWhenItResponds()
    {
        var mirror=new MirrorReader(Mirror("99"));
        var handler=new Handler(_=>new(HttpStatusCode.OK){Content=new StringContent("""[{"entity_id":"sensor.house_energy","state":"12","last_updated":"2026-01-01T09:59:00Z","attributes":{"unit_of_measurement":"kWh"}}]""")});
        var sample=Assert.Single(await new HomeAssistantClient(new HttpClient(handler),Options(),mirror).CollectAsync(default));
        Assert.Equal(12,sample.Value);Assert.Equal("HomeAssistant",sample.Source);Assert.Equal(0,mirror.Calls);
    }
    [Fact] public async Task StaysUnavailableWhenBothSourcesFail()
    {
        var client=new HomeAssistantClient(new HttpClient(new Handler(_=>new(HttpStatusCode.ServiceUnavailable))),Options(),new MirrorReader(null));
        var sample=Assert.Single(await client.CollectAsync(default));
        Assert.Null(sample.Value);Assert.Equal("unavailable",sample.Status);
    }
    [Fact] public async Task MirrorOnlyCollectionReportsConfiguredStatusAndSource()
    {
        var path=Path.Combine(Path.GetTempPath(),"mirror-status-"+Guid.NewGuid().ToString("N"));
        try
        {
            using var db=new DataStore(path);var options=MirrorOnlyOptions();
            var client=new HomeAssistantClient(new HttpClient(new Handler(_=>throw new InvalidOperationException())),options,new MirrorReader(Mirror()));
            var service=new TelemetryCollectionService(db,client,options,new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{["App:Demo"]="false"}).Build());
            Assert.True(service.Status().Configured);Assert.False(service.Status().HomeAssistantDirect);Assert.Null(service.Status().LastSource);
            await service.CollectAsync(default);
            Assert.Equal("Predbat mirror",service.Status().LastSource);Assert.Equal(12,service.Status().LatestReadings["load"].Value);
        }
        finally{if(Directory.Exists(path))Directory.Delete(path,true);}
    }
    sealed class MirrorReader(JsonArray? states,bool configured=true):IPredbatEntityReader
    {
        public int Calls;public List<string> Requested=[];
        public bool Configured=>configured;
        public Task<JsonArray?> ReadEntityStatesAsync(IReadOnlyCollection<string> entityIds,CancellationToken ct){Calls++;Requested.AddRange(entityIds);return Task.FromResult(states);}
    }
    sealed class Handler(Func<HttpRequestMessage,HttpResponseMessage> reply):HttpMessageHandler
    {protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)=>Task.FromResult(reply(request));}
}
