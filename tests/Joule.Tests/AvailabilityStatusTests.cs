using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Joule;
using Xunit;

namespace Joule.Tests;

public sealed class AvailabilityStatusTests
{
    [Fact] public void StatusReportsPersistedFirstUsableMeterObservationAcrossRestart()
    {
        var path=Path.Combine(Path.GetTempPath(),"availability-status-"+Guid.NewGuid().ToString("N"));
        var at=DateTimeOffset.Parse("2026-10-02T10:00:00Z");
        try
        {
            using(var db=new DataStore(path))db.SaveTelemetry([
                new("load","sensor.load",at.AddDays(-1),null,"kWh","HomeAssistant","unknown","kWh",Status:"invalid"),
                new("soc","sensor.soc",at.AddHours(-1),50,"%","HomeAssistant","50","%"),
                new("load","sensor.load",at,10,"kWh","HomeAssistant","10","kWh")]);
            using var restarted=new DataStore(path);
            var config=new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{["App:Demo"]="false"}).Build();
            var options=new HomeAssistantOptions(config);
            var service=new TelemetryCollectionService(restarted,new HomeAssistantClient(new HttpClient(),options),options,config);
            using var json=JsonDocument.Parse(JsonSerializer.Serialize(service.Status(),JsonDefaults.Options));
            Assert.True(json.RootElement.TryGetProperty("firstObservationAt",out var first));
            Assert.Equal(at,first.GetDateTimeOffset());
        }
        finally{Directory.Delete(path,true);}
    }
}
