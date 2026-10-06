using System.Net;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Joule;
using Xunit;

namespace Joule.Tests;

public class TelemetryRound2RegressionTests : IDisposable
{
    readonly string path=Path.Combine(Path.GetTempPath(),"predbat-round2-"+Guid.NewGuid().ToString("N"));
    static IConfiguration Config()=>new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> {
        ["App:Demo"]="false",["HomeAssistant:BaseUrl"]="http://fixture.test",["HomeAssistant:AccessToken"]="fixture",["HomeAssistant:Entities:Load"]="sensor.load",["HomeAssistant:Entities:Pv"]="sensor.pv" }).Build();
    static HttpResponseMessage Sensor(string entity,string value,DateTimeOffset? reported=null)=>new(HttpStatusCode.OK){Content=new StringContent(new JsonObject {
        ["entity_id"]=entity,["state"]=value,["last_reported"]=reported?.ToString("O"),["attributes"]=new JsonObject{["unit_of_measurement"]="kWh"} }.ToJsonString())};
    // Behaviour change: the fixture's pv was "unavailable", which on a first poll is no longer an error (see the grace period in
    // OptionalSensorCollectionTests). A state that cannot be read as a number is a fault at once, so the fixture now uses one.
    [Fact] public async Task MalformedEntityPreservesValidSnapshotSensorsAndRecordsCollectionError()
    {
        using var db=new DataStore(path);var config=Config();var options=new HomeAssistantOptions(config);
        var client=new HomeAssistantClient(new HttpClient(new Handler((_,_)=>Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent("""[{"entity_id":"sensor.load","state":"10","attributes":{"unit_of_measurement":"kWh"}},{"entity_id":"sensor.pv","state":"garbled","attributes":{"unit_of_measurement":"kWh"}}]""")}))),options);
        var service=new TelemetryCollectionService(db,client,options,config);
        await Assert.ThrowsAsync<DomainException>(()=>service.CollectAsync(default));
        Assert.Equal(10,db.ReadLatestTelemetry()["load"].Value);Assert.Null(db.ReadLatestTelemetry()["pv"].Value);Assert.NotNull(service.Status().Error);
        Assert.NotNull(service.Status().LastCollection);
    }
    [Fact] public async Task RequestedCancellationIsPropagated()
    {
        using var source=new CancellationTokenSource();source.Cancel();var options=new HomeAssistantOptions(Config());
        var client=new HomeAssistantClient(new HttpClient(new Handler((_,ct)=>Task.FromCanceled<HttpResponseMessage>(ct))),options);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>client.CollectAsync(source.Token));
    }
    [Fact] public async Task AllMappedSensorsUseCoherentResponseObservationTime()
    {
        DateTimeOffset observed=default;var options=new HomeAssistantOptions(Config());
        var client=new HomeAssistantClient(new HttpClient(new Handler(async(req,ct)=> {
            Assert.Equal("/api/states",req.RequestUri!.AbsolutePath);
            await Task.Delay(50,ct);observed=DateTimeOffset.UtcNow;
            var body=new JsonArray(JsonNode.Parse(await Sensor("sensor.load","10",observed).Content.ReadAsStringAsync(ct)),JsonNode.Parse(await Sensor("sensor.pv","10",observed).Content.ReadAsStringAsync(ct)));
            return new(HttpStatusCode.OK){Content=new StringContent(body.ToJsonString())};
        })),options);
        var readings=await client.CollectAsync(default);
        Assert.All(readings,x=>Assert.True(x.Time>=observed));Assert.Single(readings.Select(x=>x.Time).Distinct());Assert.All(readings,x=>Assert.Equal("observed",x.Status));
    }
    static EnergySummary Context(double exportRate,double? importCost=.25)
    {
        var from=DateTimeOffset.Parse("2020-01-01T00:00:00Z");var metrics=new Dictionary<string,EnergyMetricSummary>();
        foreach(var key in new[]{"load","pv","ev","grid_import","grid_export"})metrics[key]=new(1,259200,1,0);
        return new(from,from.AddDays(3),metrics,importCost,exportRate,importCost-exportRate,1,259200,["HomeAssistant"],[]);
    }
    [Fact] public void ExportTariffChangeBlocksAutomaticFinancialAttribution()
    {
        var before=Context(.15);var after=Context(0);
        Assert.Contains(ExperimentEvaluator.CompareContext(before,after),x=>x.Contains("export tariff"));
    }
    [Theory][InlineData("grid_import")][InlineData("grid_export")]
    public void MissingTariffContextCannotSilentlyPass(string metric)
    {
        var before=Context(.15);var after=metric=="grid_import"?Context(.15,null):Context(.15) with{ExportCreditGbp=null};
        Assert.Contains(ExperimentEvaluator.CompareContext(before,after),x=>x.Contains(metric=="grid_import"?"import tariff":"export tariff"));
    }
    [Fact] public void IdenticalConsumptionHasSameForecastErrorAcrossFiveAndTenMinuteMeterCadences()
    {
        using var db=new DataStore(path);var at=DateTimeOffset.Parse("2020-01-01T00:00:00Z");var change=at.AddDays(3);
        var slots=Enumerable.Range(0,288).Select(i=>new PlanSlot(at.AddMinutes(i*30),1.3,null,0,null,50,null,25,15,"Demand",0)).ToList();
        db.SavePlan(new PlanSnapshot{Source="Predbat",At=at.AddHours(-1),CollectedAt=at.AddHours(-1),Slots=slots});db.SavePlan(new PlanSnapshot{Source="Predbat",At=change,CollectedAt=change,Slots=slots.Where(s=>s.Time>=change).ToList()});
        var samples=new List<TelemetrySample>();double cumulative=0;
        for(int i=0;i<=1728;i++) {
            if(i>0)cumulative+=i%2==1?.1:.3;if(i<864 && i%2==1)continue;
            samples.Add(new("load","sensor.load",at.AddMinutes(i*5),cumulative,"kWh","HomeAssistant",cumulative.ToString(),"kWh"));
        }
        db.SaveTelemetry(samples);var evaluator=new ExperimentEvaluator(null!,db);var b=evaluator.Measure("load",at,change);var a=evaluator.Measure("load",change,change.AddDays(3),change);
        Assert.NotNull(b);Assert.NotNull(a);Assert.Equal(.1,b.Error,6);Assert.Equal(b.Error,a.Error,6);Assert.Equal(144,b.Slots);Assert.Equal(144,a.Slots);
        Assert.False(ExperimentEvaluator.ShouldRollback(b,a,TimeSpan.FromDays(3)));
    }
    [Fact] public void IncompatibleMeterCadenceRequiresExplicitlyComparableWholeIntervalScoring()
    {
        using var db=new DataStore(path);var at=DateTimeOffset.Parse("2020-01-01T00:00:00Z");
        db.SavePlan(new PlanSnapshot{Source="Predbat",At=at.AddHours(-1),CollectedAt=at.AddHours(-1),Slots=Enumerable.Range(0,4).Select(i=>new PlanSlot(at.AddMinutes(i*30),1,null,0,null,50,null,25,15,"Demand",0)).ToList()});
        db.SaveTelemetry(Enumerable.Range(0,16).Select(i=>new TelemetrySample("load","sensor.load",at.AddMinutes(i*7),i*.1,"kWh","HomeAssistant","","kWh")));
        var measurement=new ExperimentEvaluator(null!,db).Measure("load",at,at.AddHours(2));
        Assert.NotNull(measurement);Assert.Equal(2100,measurement.MedianScoredSeconds);Assert.Equal(3,measurement.Slots);
        Assert.False(ExperimentEvaluator.ComparableTemporalSupport(new(.1,48,86400,1800,1800),measurement));
    }
    public void Dispose(){if(Directory.Exists(path))Directory.Delete(path,true);}
    sealed class Handler(Func<HttpRequestMessage,CancellationToken,Task<HttpResponseMessage>> reply):HttpMessageHandler
    {protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)=>reply(request,ct);}
}
