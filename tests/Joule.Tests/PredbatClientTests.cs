using System.Net;
using Microsoft.Extensions.Configuration;
using Joule;
using Xunit;

namespace Joule.Tests;

public class PredbatClientTests
{
    const string State = """
    {"input_number.predbat_load_scaling":{"state":"1.0","attributes":{"friendly_name":"Load scaling","min":0.5,"max":2,"step":0.05}},
    "switch.predbat_read_only":{"state":"on","attributes":{"friendly_name":"Read only"}},
    "select.predbat_mode":{"state":"Monitor","attributes":{"options":["Monitor","Control"]}},
    "input_number.predbat_new_knob":{"state":"3","attributes":{"min":1,"max":5,"step":1}},
    "input_number.predbat_without_metadata":{"state":"3","attributes":{}},
    "sensor.predbat_api_token":{"state":"SUPERSECRET","attributes":{}},
    "sensor.predbat_status":{"state":"ok","attributes":{"password":"SUPERSECRET","nested":{"access_token":"SUPERSECRET"}}},
    "sensor.unrelated":{"state":"SUPERSECRET","attributes":{}}}
    """;
    const string Plan = """
    {"unchanged":false,"plan":{"timestamp":"2026-10-02T10:00:00+00:00","rows":[{"time":"2026-10-02T10:30:00+0000","load_forecast":0.6,"pv_forecast":0.3,"soc_percent":72,"import_rate":25,"export_rate":15,"state":"Charge","cost_change":0.12}]}}
    """;
    static (PredbatClient Client, FixtureHandler Handler) Client(bool writes = false, Func<HttpRequestMessage, int, HttpResponseMessage>? reply = null, string? token = null)
    {
        var handler = new FixtureHandler(reply ?? ((r, _) => Json(r.RequestUri!.AbsolutePath == "/api/state" ? State : Plan)));
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Predbat:BaseUrl"] = "http://predbat.test", ["Predbat:WritesEnabled"] = writes.ToString(), ["Predbat:AccessToken"] = token }).Build();
        return (new PredbatClient(new HttpClient(handler), config), handler);
    }
    static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK) { Content = new StringContent(value) };
    static Setting Load() => new() { Key = "load_scaling", EntityId = "input_number.predbat_load_scaling", Value = "1", Min = .5, Max = 2, Step = .05 };
    [Fact] public async Task CollectDiscoversAllControlsAndRealPlanWithoutInventingActuals()
    {
        var (client, _) = Client(); var result = await client.CollectAsync(default);
        Assert.Equal(5, result.Settings.Count);
        // Risk comes from the curated catalogue; keys it doesn't know stay High.
        Assert.All(result.Settings, s => { Assert.False(s.AutoAllowed); Assert.Equal(s.Key == "load_scaling" ? "Low" : "High", s.Risk); });
        Assert.Equal("House load scaling", result.Settings.Single(x => x.Key == "load_scaling").Name);
        Assert.Equal("Load scaling", result.Settings.Single(x => x.Key == "load_scaling").PredbatName);
        var mode = result.Settings.Single(x => x.Key == "mode"); Assert.Equal(SettingKind.Control, mode.Kind); Assert.False(mode.Editable);
        Assert.False(result.Settings.Single(x => x.Key == "without_metadata").Editable);
        var load = result.Settings.Single(x => x.Key == "load_scaling"); Assert.Equal(.5, load.Min); Assert.Equal(.05, load.Step);
        var slot = Assert.Single(result.Plan!.Slots);
        Assert.Equal(.6, slot.LoadForecast); Assert.Equal(.3, slot.PvForecast); Assert.Equal(72, slot.SocForecast); Assert.Equal(.12, slot.Cost);
        Assert.Equal(25, slot.ImportRate); Assert.Equal("charge", slot.Action); Assert.Equal("Charge", slot.RawAction); Assert.Null(slot.LoadActual); Assert.Null(slot.SocActual);
        Assert.DoesNotContain("SUPERSECRET", result.RawState);
    }
    [Theory]
    // Every code is normalised through the glossary; Predbat's own code is kept in RawAction.
    [InlineData("Chrg", "charge")]
    [InlineData("Exp", "export")]
    [InlineData("Demand", "demand")]
    [InlineData("FrzExp", "freeze-export")]
    [InlineData("HoldChrg", "hold-charge")]
    [InlineData("Unexpected action", "Unexpected action")]
    public async Task RealPredbatActionAliasesIdentifyChargeAndExportWindows(string raw, string expected)
    {
        var (client, _) = Client(reply: (r, _) => Json(r.RequestUri!.AbsolutePath == "/api/state" ? State : Plan.Replace("\"state\":\"Charge\"", "\"state\":\"" + raw + "\"")));
        var slot = Assert.Single((await client.CollectAsync(default)).Plan!.Slots);
        Assert.Equal(expected, slot.Action);
        Assert.Equal(raw, slot.RawAction);
    }
    [Fact] public async Task MissingPlanIsUnavailableInsteadOfDemo()
    {
        var (client, _) = Client(reply: (r, _) => Json(r.RequestUri!.AbsolutePath == "/api/state" ? State : "{\"unchanged\":true}"));
        Assert.Null((await client.CollectAsync(default)).Plan);
    }
    [Theory]
    [InlineData("on")]
    [InlineData("off")]
    public async Task CalculationActivityIsReadOnlyDiagnosticAndNotControlEnablement(string active)
    {
        var data=State.TrimEnd()[..^1]+",\"switch.predbat_active\":{\"state\":\""+active+"\",\"attributes\":{\"friendly_name\":\"Predbat active\"}}}";
        var (client,_)=Client(reply:(r,_)=>Json(r.RequestUri!.AbsolutePath=="/api/state"?data:Plan));
        var settings=(await client.CollectAsync(default)).Settings;
        var diagnostic=settings.Single(s=>s.Key=="active");
        // Calculation activity is one of Predbat's own controls (status), shown under "Predbat control".
        Assert.False(diagnostic.Editable);Assert.Equal(SettingKind.Control,diagnostic.Kind);Assert.Equal("Predbat control",diagnostic.Category);Assert.Equal(active,diagnostic.Value);
        Assert.Contains("calculation",diagnostic.Description,StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not",diagnostic.Description,StringComparison.OrdinalIgnoreCase);
        Assert.True(settings.Single(s=>s.Key=="read_only").Editable);
        Assert.Throws<DomainException>(()=>ChangeEngine.Validate(diagnostic,active=="on"?"off":"on"));
    }
    [Fact] public async Task DiagnosticActivityDoesNotInvalidateAnOtherwiseVerifiedControlWrite()
    {
        var changed=false;var reads=0;
        var (client,handler)=Client(true,reply:(r,_)=>
        {
            if(r.Method==HttpMethod.Post){changed=true;return new(HttpStatusCode.Found);}
            if(r.RequestUri!.AbsolutePath!="/api/state")return Json(Plan);
            var data=changed?State.Replace("\"1.0\"","\"1.1\""):State;
            return Json(data.TrimEnd()[..^1]+",\"switch.predbat_active\":{\"state\":\""+(++reads%2==0?"on":"off")+"\",\"attributes\":{}}}");
        });
        var settings=(await client.CollectAsync(default)).Settings;
        await client.ApplyAsync([new("load_scaling","1","1.1")],settings,default);
        Assert.Single(handler.Requests,r=>r.Method=="POST");
    }
    [Fact] public async Task CachedEditableActivityStillCannotBeSubmittedAsAControlWrite()
    {
        var (client,handler)=Client(true);
        var stale=new Setting { Key="active",EntityId="switch.predbat_active",Type="boolean",Value="off",Editable=true };
        var failure=await Assert.ThrowsAsync<LiveWriteException>(()=>client.ApplyAsync([new("active","off","on")],[stale],default));
        Assert.False(failure.Uncertain);Assert.Empty(handler.Requests);
    }
    [Fact] public async Task HttpFailureCannotReturnDemo()
    {
        var (client, _) = Client(reply: (_, _) => new(HttpStatusCode.ServiceUnavailable));
        await Assert.ThrowsAsync<DomainException>(() => client.CollectAsync(default));
    }
    [Fact] public async Task WritesOffRejectBeforeHttp()
    {
        var (client, handler) = Client();
        var error = await Assert.ThrowsAsync<LiveWriteException>(() => client.ApplyAsync([new("load_scaling", "1", "1.1")], [Load()], default));
        Assert.False(error.Uncertain); Assert.Empty(handler.Requests);
    }
    [Fact] public async Task ChangedRemoteValueBlocksWrite()
    {
        var (client, handler) = Client(true, (_, _) => Json(State.Replace("\"1.0\"", "\"1.2\"")));
        var error = await Assert.ThrowsAsync<LiveWriteException>(() => client.ApplyAsync([new("load_scaling", "1", "1.1")], [Load()], default));
        Assert.False(error.Uncertain); Assert.All(handler.Requests, r => Assert.Equal("GET", r.Method));
    }
    [Fact] public async Task SuccessfulWriteUsesConfigFormAndVerifiesState()
    {
        var changed = false; string? body = null;
        var (client, handler) = Client(true, (r, _) =>
        {
            if (r.Method == HttpMethod.Post) { body = r.Content!.ReadAsStringAsync().GetAwaiter().GetResult(); changed = true; return new(HttpStatusCode.Found); }
            return Json(changed ? State.Replace("\"1.0\"", "\"1.10\"") : State);
        });
        await client.ApplyAsync([new("load_scaling", "1", "1.1")], (await client.CollectAsync(default)).Settings, default);
        Assert.Equal("input_number__predbat_load_scaling=1.1", body);
        Assert.Single(handler.Requests, x => x.Method == "POST"); Assert.Equal("/config", handler.Requests.Single(x => x.Method == "POST").Path);
        Assert.Equal("GET", handler.Requests.Last().Method);
    }
    [Fact] public async Task AcceptedButUnchangedStateIsUncertain()
    {
        var (client, _) = Client(true, (r, _) => r.Method == HttpMethod.Post ? new(HttpStatusCode.Found) : Json(State));
        var settings = (await client.CollectAsync(default)).Settings;
        var error = await Assert.ThrowsAsync<LiveWriteException>(() => client.ApplyAsync([new("load_scaling", "1", "1.1")], settings, default));
        Assert.True(error.Uncertain);
    }
    [Fact] public async Task HttpFailureAfterPostIsUncertain()
    {
        var (client, _) = Client(true, (r, _) => r.Method == HttpMethod.Post ? new(HttpStatusCode.ServiceUnavailable) : Json(State));
        var settings = (await client.CollectAsync(default)).Settings;
        Assert.True((await Assert.ThrowsAsync<LiveWriteException>(() => client.ApplyAsync([new("load_scaling", "1", "1.1")], settings, default))).Uncertain);
    }
    [Fact] public async Task UnsupportedEntityCannotBeWritten()
    {
        var setting = Load(); setting.EntityId = "number.predbat_load_scaling";
        var (client, handler) = Client(true);
        Assert.False((await Assert.ThrowsAsync<LiveWriteException>(() => client.ApplyAsync([new("load_scaling", "1", "1.1")], [setting], default))).Uncertain);
        Assert.Empty(handler.Requests);
    }
    [Fact] public async Task MissingForecastFieldRejectsPlanInsteadOfInventingZero()
    {
        var (client, _) = Client(reply: (r, _) => Json(r.RequestUri!.AbsolutePath == "/api/state" ? State : Plan.Replace("\"load_forecast\":0.6,", "")));
        await Assert.ThrowsAsync<DomainException>(() => client.CollectAsync(default));
    }
    [Fact] public async Task InvalidNumericStepBlocksWriteBeforeHttp()
    {
        var (client, handler) = Client(true);
        Assert.False((await Assert.ThrowsAsync<LiveWriteException>(() => client.ApplyAsync([new("load_scaling", "1", "1.11")], [Load()], default))).Uncertain);
        Assert.Empty(handler.Requests);
    }
    [Fact] public async Task LargeSpanHalfStepRejectsBeforeAnyRemoteRequest()
    {
        var (client,handler)=Client(true);
        var setting=Load();setting.Min=0;setting.Max=200000;setting.Step=.01;
        var error=await Assert.ThrowsAsync<LiveWriteException>(()=>client.ApplyAsync([new("load_scaling","1","100000.005")],[setting],default));
        Assert.False(error.Uncertain);Assert.Empty(handler.Requests);
    }
    [Fact] public async Task PreflightHttpFailureHasNoUncertainWrite()
    {
        var (client, handler) = Client(true, (_, _) => new(HttpStatusCode.BadGateway));
        Assert.False((await Assert.ThrowsAsync<LiveWriteException>(() => client.ApplyAsync([new("load_scaling", "1", "1.1")], [Load()], default))).Uncertain);
        Assert.All(handler.Requests, r => Assert.Equal("GET", r.Method));
    }
    [Fact] public async Task MidBatchFailureReportsPriorWriteAsUncertain()
    {
        var changed = false;
        var (client, handler) = Client(true, (r, _) =>
        {
            if (r.Method == HttpMethod.Post) { changed = true; return new(HttpStatusCode.Found); }
            return Json(changed ? State.Replace("\"1.0\"", "\"1.1\"") : State);
        });
        // The second (tunable) write is never reflected by Predbat. Predbat's mode is a control and can't be written at all.
        var settings = (await client.CollectAsync(default)).Settings;
        Assert.True((await Assert.ThrowsAsync<LiveWriteException>(() => client.ApplyAsync([new("load_scaling", "1", "1.1"), new("new_knob", "3", "4")], settings, default))).Uncertain);
        Assert.Equal(2, handler.Requests.Count(x => x.Method == "POST"));
        Assert.False((await Assert.ThrowsAsync<LiveWriteException>(() => client.ApplyAsync([new("mode", "Monitor", "Control")], settings, default))).Uncertain);
    }
    [Fact] public async Task UnrelatedExternalTuningRejectsWholeConfigurationBeforeAnyPost()
    {
        var tuned = false;
        var (client, handler) = Client(true, (r, _) => Json(r.RequestUri!.AbsolutePath == "/api/state" ? tuned ? State.Replace("\"3\"", "\"4\"") : State : Plan));
        var settings = (await client.CollectAsync(default)).Settings;
        tuned = true;
        var error = await Assert.ThrowsAsync<LiveWriteException>(() => client.ApplyAsync([new("load_scaling", "1", "1.1")], settings, default));
        Assert.False(error.Uncertain);
        Assert.DoesNotContain(handler.Requests, x => x.Method == "POST");
    }
    [Fact] public async Task ExactHistoricalNativeCurveEndpointsRemainUnverifiedDiagnostics()
    {
        const string historicalState = """
        {"predbat.load_energy_actual":{"state":1.45,"attributes":{"results":{"2020-01-01T10:00:00+0000":1.0,"2020-01-01T10:30:00+0000":1.45}}}}
        """;
        var row = "\"time\":\"2020-01-01T10:00:00+0000\",\"load_forecast\":0.6,\"pv_forecast\":0.3,\"soc_percent\":72,\"import_rate\":25,\"export_rate\":15,\"state\":\"Demand\",\"cost_change\":0.12";
        var historicalPlan = "{\"plan\":{\"timestamp\":\"2020-01-01T10:00:00Z\",\"rows\":[{" + row + "},{" + row.Replace("10:00:00", "10:30:00") + "}]}}";
        var (client, _) = Client(reply: (r, _) => Json(r.RequestUri!.AbsolutePath == "/api/state" ? historicalState : historicalPlan));
        var snapshot=await client.CollectAsync(default);var slots=snapshot.Plan!.Slots;
        Assert.Null(slots[0].LoadActual); Assert.Null(slots[1].LoadActual); Assert.Null(slots[0].PvActual);
        Assert.Contains("load_energy_actual",snapshot.RawState);
    }
    [Fact] public async Task BearerCredentialIsSentOnlyAsHeaderAndRedactedWhenEchoed()
    {
        const string credential = "fixture-credential-123";
        var (client, _) = Client(reply: (r, _) =>
        {
            Assert.Equal("Bearer", r.Headers.Authorization!.Scheme); Assert.Equal(credential, r.Headers.Authorization.Parameter);
            Assert.DoesNotContain(credential, r.RequestUri!.ToString());
            return Json(r.RequestUri.AbsolutePath == "/api/state" ? State.Replace("\"ok\"", "\"" + credential + "\"") : Plan);
        }, token: credential);
        Assert.DoesNotContain(credential, (await client.CollectAsync(default)).RawState);
    }
    [Fact] public async Task UnconfiguredClientDoesNotInventLiveSettings()
    {
        var handler = new FixtureHandler((_, _) => Json(State));
        var client = new PredbatClient(new HttpClient(handler), new ConfigurationBuilder().Build());
        Assert.False(client.Configured); Assert.False(client.WritesEnabled);
        await Assert.ThrowsAsync<DomainException>(() => client.CollectAsync(default)); Assert.Empty(handler.Requests);
    }
    [Fact] public async Task EntityReaderReturnsOnlyRequestedMirroredStatesInHomeAssistantShape()
    {
        var (client, handler) = Client();
        IPredbatEntityReader reader = client; Assert.True(reader.Configured);
        var states = await reader.ReadEntityStatesAsync(["sensor.unrelated", "sensor.missing"], default);
        var entry = Assert.IsType<System.Text.Json.Nodes.JsonObject>(Assert.Single(states!));
        Assert.Equal("sensor.unrelated", entry["entity_id"]!.ToString()); Assert.Equal("SUPERSECRET", entry["state"]!.ToString()); Assert.NotNull(entry["attributes"]);
        var request = Assert.Single(handler.Requests); Assert.Equal("/api/state", request.Path);
    }
    [Fact] public async Task EntityReaderReturnsNullWhenPredbatIsUnavailable()
    {
        var (client, _) = Client(reply: (_, _) => new(HttpStatusCode.BadGateway));
        Assert.Null(await ((IPredbatEntityReader)client).ReadEntityStatesAsync(["sensor.unrelated"], default));
    }
    [Fact] public async Task MirroredMappedSensorCannotExposeThePredbatBearerTokenAsRawEvidence()
    {
        const string secret="owned-predbat-mirror-secret";
        var state="{\"sensor.shadow\":{\"state\":\""+secret+"\",\"attributes\":{\"note\":\""+secret+"\",\"forecast_kind\":\"interval_energy\"}}}";
        var (client, _) = Client(reply: (_, _) => Json(state), token: secret);
        var values=new Dictionary<string,string?> { ["HomeAssistant:Entities:AlternativeForecast"]="sensor.shadow" };
        var options=new HomeAssistantOptions(new ConfigurationBuilder().AddInMemoryCollection(values).Build());
        var reading=Assert.Single(await new HomeAssistantClient(new HttpClient(),options,client).CollectAsync(default));
        Assert.DoesNotContain(secret,reading.RawState);
        Assert.DoesNotContain(secret,reading.AttributesJson);
        Assert.Equal("Predbat mirror",reading.Source);
        Assert.Contains("interval_energy",reading.AttributesJson);
    }
    [Fact] public async Task OversizedPredbatStateCannotBeBufferedForTelemetryFallback()
    {
        var oversized = "{\"sensor.unrelated\":{\"state\":\"" + new string('x', 9 * 1024 * 1024) + "\"}}";
        var (client, _) = Client(reply: (_, _) => Json(oversized));
        Assert.Null(await ((IPredbatEntityReader)client).ReadEntityStatesAsync(["sensor.unrelated"], default));
        await Assert.ThrowsAsync<DomainException>(() => client.CollectAsync(default));
    }
    sealed class FixtureHandler(Func<HttpRequestMessage, int, HttpResponseMessage> reply) : HttpMessageHandler
    {
        public List<(string Method, string Path)> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { Requests.Add((request.Method.Method, request.RequestUri!.AbsolutePath)); return Task.FromResult(reply(request, Requests.Count)); }
    }
}
