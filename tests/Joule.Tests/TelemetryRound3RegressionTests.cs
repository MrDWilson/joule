using System.Net;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Joule;
using Xunit;
namespace Joule.Tests;
public class TelemetryRound3RegressionTests : IDisposable
{
    readonly string folder=Path.Combine(Path.GetTempPath(),"predbat-round3-"+Guid.NewGuid().ToString("N"));
    static IConfiguration Config()=>new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> {
        ["App:Demo"]="false",["HomeAssistant:BaseUrl"]="http://fixture.test",["HomeAssistant:AccessToken"]="fixture-secret",
        ["HomeAssistant:Entities:GridImport"]="sensor.import",["HomeAssistant:Entities:GridExport"]="sensor.export",
        ["HomeAssistant:Entities:ImportTariff"]="sensor.import_rate",["HomeAssistant:Entities:ExportTariff"]="sensor.export_rate" }).Build();
    static JsonObject Sensor(string entity,double value,string unit)=>new(){["entity_id"]=entity,["state"]=value.ToString(System.Globalization.CultureInfo.InvariantCulture),["attributes"]=new JsonObject{["unit_of_measurement"]=unit}};
    [Fact] public async Task RealCollectorProducesMatchedNetCostCoverage()
    {
        using var db=new DataStore(folder);var config=Config();var options=new HomeAssistantOptions(config);int calls=0;
        var handler=new Handler(async(req,ct)=> {
            await Task.Delay(15,ct);var bulk=req.RequestUri!.AbsolutePath=="/api/states";int poll=bulk?calls++:calls++/4;
            var states=new JsonArray(Sensor("sensor.import",poll*.1,"kWh"),Sensor("sensor.export",poll*.01,"kWh"),Sensor("sensor.import_rate",25,"p/kWh"),Sensor("sensor.export_rate",15,"p/kWh"));
            JsonNode body=bulk?states:states.First(x=>x!["entity_id"]!.ToString()==req.RequestUri.AbsolutePath.Split('/').Last())!.DeepClone();
            return new(HttpStatusCode.OK){Content=new StringContent(body.ToJsonString())};
        });
        var service=new TelemetryCollectionService(db,new HomeAssistantClient(new HttpClient(handler),options),options,config);
        for(int i=0;i<3;i++)await service.CollectAsync(default);
        var samples=db.ReadTelemetrySamples(DateTimeOffset.UtcNow.AddMinutes(-1),DateTimeOffset.UtcNow.AddMinutes(1),limit:1000);
        var summary=db.ReadEnergySummary(samples.Min(s=>s.Time),samples.Max(s=>s.Time));
        Assert.Equal(1,summary.CostCoverageFraction,8);Assert.Equal(.047,summary.ObservedNetCostGbp!.Value,8);
        Assert.Equal(3,calls);Assert.Null(service.Status().Error);
    }
    [Fact] public async Task CoherentSnapshotPreservesValidMappedEntitiesWhenAnotherIsMalformed()
    {
        var states=new JsonArray(Sensor("sensor.import",10,"kWh"),new JsonObject{["entity_id"]="sensor.export",["state"]="bad"},Sensor("sensor.import_rate",25,"p/kWh"),Sensor("sensor.export_rate",15,"p/kWh"),new JsonObject{["entity_id"]="sensor.unmapped",["state"]="fixture-secret"});
        var client=new HomeAssistantClient(new HttpClient(new Handler((_,_)=>Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(states.ToJsonString())}))),new HomeAssistantOptions(Config()));
        var samples=await client.CollectAsync(default);
        Assert.Equal(4,samples.Count);Assert.Equal(10,samples.Single(s=>s.Metric=="grid_import").Value);Assert.Null(samples.Single(s=>s.Metric=="grid_export").Value);
        Assert.Single(samples.Select(s=>s.Time).Distinct());Assert.DoesNotContain(samples,s=>s.EntityId=="sensor.unmapped");Assert.DoesNotContain("fixture-secret",string.Join("",samples.Select(s=>s.AttributesJson+s.RawState)));
    }
    [Fact] public async Task BatchTimeoutRecordsUnavailableMappedSensorsAndCollectionError()
    {
        using var db=new DataStore(folder);var config=Config();var options=new HomeAssistantOptions(config);
        var client=new HomeAssistantClient(new HttpClient(new Handler((_,_)=>Task.FromException<HttpResponseMessage>(new TaskCanceledException("Timeout",new TimeoutException())))),options);
        var service=new TelemetryCollectionService(db,client,options,config);
        await Assert.ThrowsAsync<DomainException>(()=>service.CollectAsync(default));
        Assert.Equal(4,db.ReadLatestTelemetry().Count);Assert.All(db.ReadLatestTelemetry().Values,s=>{Assert.Null(s.Value);Assert.Equal("unavailable",s.Status);});Assert.NotNull(service.Status().Error);
    }
    [Fact] public void OrdinaryPollResponseDriftRetainsWholeIntervalForecastMeasurements()
    {
        using var db=new DataStore(folder);var start=DateTimeOffset.Parse("2020-01-01T00:00:00Z");
        db.SavePlan(new PlanSnapshot{Source="Predbat",At=start.AddHours(-1),CollectedAt=start.AddHours(-1),Slots=Enumerable.Range(0,144).Select(i=>new PlanSlot(start.AddMinutes(i*30),.6,null,0,null,50,null,25,15,"Demand",0)).ToList()});
        db.SaveTelemetry(Enumerable.Range(0,864).Select(i=>new TelemetrySample("load","sensor.load",start.AddSeconds(i*301),i*.1,"kWh","HomeAssistant","","kWh")));
        var measurement=new ExperimentEvaluator(null!,db).Measure("load",start,start.AddDays(3));
        Assert.NotNull(measurement);Assert.True(measurement.CoveredSeconds/(3*86400d)>.99);
    }
    [Fact] public void DifferentScoringDurationsCannotTriggerForecastOrFinancialRollback()
    {
        var before=new ExperimentEvaluator.Measurement(.1,144,259200,1800,1800);
        var after=new ExperimentEvaluator.Measurement(.3,123,258300,2100,2100);
        Assert.False(ExperimentEvaluator.ComparableTemporalSupport(before,after));
        Assert.False(ExperimentEvaluator.ShouldRollback(before,after,TimeSpan.FromDays(3)));
    }
    [Fact] public async Task ResponseBodyTimeoutCannotHangCollectionAfterHeaders()
    {
        var stream=new WaitingStream();
        var http=new HttpClient(new Handler((_,_)=>Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StreamingContent(stream)}))){Timeout=TimeSpan.FromMilliseconds(30)};
        // A hang would never finish; the 30 ms timeout itself can fire late on a busy CI runner, so allow it a few seconds.
        var readings=await new HomeAssistantClient(http,new HomeAssistantOptions(Config())).CollectAsync(default).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(4,readings.Count);Assert.All(readings,s=>Assert.Equal("unavailable",s.Status));
    }
    [Fact] public async Task UnknownLengthResponseStopsAtBodyLimit()
    {
        var stream=new CountingStream(new byte[9*1024*1024]);
        var http=new HttpClient(new Handler((_,_)=>Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StreamingContent(stream)})));
        var readings=await new HomeAssistantClient(http,new HomeAssistantOptions(Config())).CollectAsync(default);
        Assert.All(readings,s=>Assert.Equal("unavailable",s.Status));Assert.InRange(stream.BytesRead,8*1024*1024,8*1024*1024+16*1024);
    }
    sealed class StreamingContent(Stream source):HttpContent
    {
        protected override bool TryComputeLength(out long length){length=0;return false;}
        protected override Task SerializeToStreamAsync(Stream destination,TransportContext? context)=>source.CopyToAsync(destination);
        protected override Task<Stream> CreateContentReadStreamAsync()=>Task.FromResult(source);
    }
    sealed class WaitingStream:MemoryStream
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer,CancellationToken ct=default){await Task.Delay(Timeout.Infinite,ct);return 0;}
    }
    sealed class CountingStream(byte[] bytes):MemoryStream(bytes)
    {
        public int BytesRead;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer,CancellationToken ct=default){var count=await base.ReadAsync(buffer,ct);BytesRead+=count;return count;}
    }
    public void Dispose(){if(Directory.Exists(folder))Directory.Delete(folder,true);}
    sealed class Handler(Func<HttpRequestMessage,CancellationToken,Task<HttpResponseMessage>> reply):HttpMessageHandler
    {protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)=>reply(request,ct);}
}
