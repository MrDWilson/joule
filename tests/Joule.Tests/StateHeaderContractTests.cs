using System.Text.Json;
using System.Text.RegularExpressions;
using Joule;
using Xunit;

/// <summary>
/// Pins the slim /api/state?view=header contract three ways: the server output validates against
/// docs/api/state-header.schema.json, and the StateHeader interface in web/src/types.ts lists exactly the schema's
/// properties. UI streams build against the TypeScript; this keeps all three in step.
/// </summary>
public class StateHeaderContractTests
{
    static string Repo([System.Runtime.CompilerServices.CallerFilePath] string here = "") => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", ".."));
    static JsonElement Schema() => JsonDocument.Parse(File.ReadAllText(Path.Combine(Repo(), "docs", "api", "state-header.schema.json"))).RootElement;

    /// <summary>Validates the subset of JSON Schema the contract uses: type, required, properties, items, $ref, oneOf, const, enum, maxItems.</summary>
    static void Validate(JsonElement schema, JsonElement value, string at, JsonElement root, List<string> errors)
    {
        if (schema.TryGetProperty("$ref", out var reference))
        {
            var target = root;
            foreach (var part in reference.GetString()!.TrimStart('#', '/').Split('/')) target = target.GetProperty(part);
            Validate(target, value, at, root, errors); return;
        }
        if (schema.TryGetProperty("oneOf", out var options))
        {
            var matches = options.EnumerateArray().Count(o => { var e = new List<string>(); Validate(o, value, at, root, e); return e.Count == 0; });
            if (matches != 1) errors.Add($"{at}: matched {matches} of oneOf");
            return;
        }
        if (schema.TryGetProperty("type", out var type))
        {
            var allowed = type.ValueKind == JsonValueKind.Array ? type.EnumerateArray().Select(t => t.GetString()!).ToArray() : [type.GetString()!];
            var actual = value.ValueKind switch
            {
                JsonValueKind.Object => "object", JsonValueKind.Array => "array", JsonValueKind.String => "string",
                JsonValueKind.True or JsonValueKind.False => "boolean", JsonValueKind.Null => "null",
                JsonValueKind.Number => value.TryGetInt64(out _) ? "integer" : "number", _ => "undefined"
            };
            if (!allowed.Contains(actual) && !(actual == "integer" && allowed.Contains("number"))) { errors.Add($"{at}: {actual} is not {string.Join("|", allowed)}"); return; }
        }
        if (value.ValueKind == JsonValueKind.Null) return;
        if (schema.TryGetProperty("const", out var constant) && constant.ToString() != value.ToString()) errors.Add($"{at}: expected {constant}");
        if (schema.TryGetProperty("enum", out var values) && !values.EnumerateArray().Any(v => v.ToString() == value.ToString())) errors.Add($"{at}: {value} not in enum");
        if (value.ValueKind == JsonValueKind.Object)
        {
            if (schema.TryGetProperty("required", out var required))
                foreach (var name in required.EnumerateArray().Select(r => r.GetString()!))
                    if (!value.TryGetProperty(name, out _)) errors.Add($"{at}.{name}: missing");
            if (schema.TryGetProperty("properties", out var properties))
                foreach (var property in properties.EnumerateObject())
                    if (value.TryGetProperty(property.Name, out var child)) Validate(property.Value, child, $"{at}.{property.Name}", root, errors);
        }
        if (value.ValueKind == JsonValueKind.Array)
        {
            if (schema.TryGetProperty("maxItems", out var max) && value.GetArrayLength() > max.GetInt32()) errors.Add($"{at}: more than {max} items");
            if (schema.TryGetProperty("items", out var items))
            {
                var index = 0;
                foreach (var item in value.EnumerateArray()) Validate(items, item, $"{at}[{index++}]", root, errors);
            }
        }
    }

    public static List<string> Errors(string json)
    {
        var root = Schema(); var errors = new List<string>();
        Validate(root, JsonDocument.Parse(json).RootElement, "$", root, errors);
        return errors;
    }

    static StateContext Context(AppState state, bool demo) => new(demo, false, !demo, false,
        InvestigationScheduler.BuildStatus(state, DateTimeOffset.UtcNow, false, demo, false, false), false, true, "jo.bloggs@example.com", false,
        DemoData.Plan(), [new("m1", "There is a heat pump.", "user", DateTimeOffset.UtcNow, null)], 3,
        new TelemetryStatus(demo, true, "Europe/London", DateTimeOffset.UtcNow, null, [], ["ev"], 15, new() { ["pv"] = new(null, "kWh", DateTimeOffset.UtcNow, "unavailable", "sensor.pv", "Home Assistant", "unavailable", "", null) }),
        false, "Europe/London", DateTimeOffset.UtcNow);

    static AppState Rich()
    {
        var state = DemoData.Create();
        state.Investigations.Add(new Investigation { Title = "Run could not complete", Status = "Failed", Verdict = "problem", At = DateTimeOffset.UtcNow.AddMinutes(-5) });
        state.Notifications.Add(new InAppNotification { Title = "Daily report", Message = "Ready", ReportId = "r1" });
        return state;
    }

    [Fact]
    public void DemoAndLiveHeadersValidateAgainstThePublishedSchema()
    {
        foreach (var demo in new[] { true, false })
        {
            var state = Rich(); if (!demo) state.DataSource = "Live";
            var header = StateProjection.Header(state, Context(state, demo), ReportService.BuildScheduleStatus(state, DateTimeOffset.UtcNow));
            var json = JsonSerializer.Serialize(header, JsonDefaults.Options);
            Assert.Empty(Errors(json));
            Assert.DoesNotContain("bloggs", json);
            Assert.Equal("jo.…@example.com", header.Ai.ChatGptEmail);
        }
    }

    [Fact]
    public void AnEmptyFirstRunHeaderValidatesToo()
    {
        var state = new AppState { DataSource = "Live", Mode = "Monitor" };
        var context = Context(state, false) with { Plan = null, Memory = [], Telemetry = null, ArchivedInvestigations = 0 };
        var header = StateProjection.Header(state, context, ReportService.BuildScheduleStatus(state, DateTimeOffset.UtcNow));
        Assert.Empty(Errors(JsonSerializer.Serialize(header, JsonDefaults.Options)));
        Assert.Null(header.TopFinding48h);
        Assert.False(header.SetupProgress.RequiredDone);
        Assert.Equal("disabled", header.Health.Writes);
        Assert.Equal("AccessKey", header.Connection.AuthMode);
        Assert.Equal("None", StateProjection.Header(state, context with { AuthMode = "None" }, ReportService.BuildScheduleStatus(state, DateTimeOffset.UtcNow)).Connection.AuthMode);
    }

    [Theory]
    [InlineData("v9.3.5 Bug fixes cloud inverters & Misc", "9.3.5")]
    [InlineData("v8.20", "8.20")]
    [InlineData("9.3.5", "9.3.5")]
    [InlineData("unknown", null)]
    [InlineData(null, null)]
    public void PredbatVersionIsReadFromTheUpdateSelect(string? text, string? expected) => Assert.Equal(expected, StateProjection.PredbatVersion(text));

    [Fact]
    public void PredbatStatusCarriesVersionAndReserveFromTheSettingsThatExist()
    {
        var state = Rich();
        state.Settings.RemoveAll(x => x.Key is "update" or "version" or "set_reserve_min");
        state.Settings.Add(new Setting { Key = "update", Value = "v9.3.5 Bug fixes cloud inverters & Misc" });
        state.Settings.Add(new Setting { Key = "set_reserve_min", Value = "4" });
        var status = StateProjection.Header(state, Context(state, false), ReportService.BuildScheduleStatus(state, DateTimeOffset.UtcNow)).PredbatStatus;
        Assert.Equal("9.3.5", status.Version);
        Assert.Equal(4, status.Reserve);
        state.Settings.First(x => x.Key == "set_reserve_min").Value = "unavailable";
        Assert.Null(StateProjection.Header(state, Context(state, false), ReportService.BuildScheduleStatus(state, DateTimeOffset.UtcNow)).PredbatStatus.Reserve);
    }

    [Fact]
    public void TheSchemaRejectsAMissingField()
    {
        var state = Rich();
        var json = JsonSerializer.Serialize(StateProjection.Header(state, Context(state, true), ReportService.BuildScheduleStatus(state, DateTimeOffset.UtcNow)), JsonDefaults.Options);
        var broken = Regex.Replace(json, "\"timeZone\":\"Europe/London\"}$", "\"zone\":\"x\"}");
        Assert.Contains(Errors(broken), e => e.Contains("timeZone"));
    }

    static string[] InterfaceProperties(string source, string name)
    {
        var start = source.IndexOf($"export interface {name} {{", StringComparison.Ordinal);
        Assert.True(start >= 0, $"{name} not found in types.ts");
        var depth = 0; var end = start;
        for (var i = source.IndexOf('{', start); i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}' && --depth == 0) { end = i; break; }
        }
        var body = source[(source.IndexOf('{', start) + 1)..end];
        // Top-level members only: drop nested object literals and comments.
        body = Regex.Replace(body, @"/\*.*?\*/", "", RegexOptions.Singleline);
        var flat = new System.Text.StringBuilder(); depth = 0;
        foreach (var ch in body) { if (ch == '{') depth++; if (depth == 0) flat.Append(ch); if (ch == '}') depth--; }
        return Regex.Matches(flat.ToString(), @"^\s*(\w+)\??:", RegexOptions.Multiline).Select(m => m.Groups[1].Value).ToArray();
    }

    [Fact]
    public void TypeScriptStateHeaderMatchesTheSchemaAndTheServer()
    {
        var types = File.ReadAllText(Path.Combine(Repo(), "web", "src", "types.ts"));
        var schemaProperties = Schema().GetProperty("properties").EnumerateObject().Select(p => p.Name).Order().ToArray();
        Assert.Equal(schemaProperties, InterfaceProperties(types, "StateHeader").Order().ToArray());
        var serverProperties = typeof(StateHeader).GetProperties().Select(p => JsonNamingPolicy.CamelCase.ConvertName(p.Name)).Order().ToArray();
        Assert.Equal(schemaProperties, serverProperties);
        var summary = Schema().GetProperty("$defs").GetProperty("investigationSummary").GetProperty("properties").EnumerateObject().Select(p => p.Name).Order().ToArray();
        Assert.Equal(summary, InterfaceProperties(types, "InvestigationSummary").Order().ToArray());
        Assert.Equal(summary, typeof(InvestigationSummary).GetProperties().Select(p => JsonNamingPolicy.CamelCase.ConvertName(p.Name)).Order().ToArray());
    }
}
