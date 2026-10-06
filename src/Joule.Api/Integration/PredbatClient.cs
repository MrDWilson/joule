using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Joule;

public record LiveSnapshot(List<Setting> Settings, PlanSnapshot? Plan, string RawState, string RawPlan);
public class LiveWriteException(string message, bool uncertain) : DomainException(message, 502)
{
    public bool Uncertain { get; } = uncertain;
}

/// <summary>Predbat v9.3.3 web interface. Energy is kWh per plan row, rates p/kWh, cost GBP.</summary>
public interface IPredbatClient
{
    bool Configured { get; }
    bool WritesEnabled { get; }
    Task<LiveSnapshot> CollectAsync(CancellationToken cancellationToken);
    Task ApplyAsync(List<Change> changes, List<Setting> settings, CancellationToken cancellationToken);
}

/// <summary>Reads Home Assistant entity states through Predbat's own mirror of HA state, for telemetry fallback.</summary>
public interface IPredbatEntityReader
{
    bool Configured { get; }
    /// <summary>Returns HA /api/states-shaped objects for the requested entity IDs only, or null when Predbat is unavailable.</summary>
    Task<JsonArray?> ReadEntityStatesAsync(IReadOnlyCollection<string> entityIds, CancellationToken cancellationToken);
}

public sealed class PredbatClient : IPredbatClient, IPredbatEntityReader
{
    // Predbat v9.3.3 web.py labels this switch "On during calculations, off otherwise".
    // It is transient process telemetry, not an enable/disable control.
    public static bool IsDiagnosticSetting(string key) => key == "active";
    public const string ActiveDescription = "Calculation activity: on while Predbat is calculating, off between calculations. Off does not mean Predbat control is disabled; inspect mode, set_read_only, status and execution logs separately.";
    readonly HttpClient http;
    readonly Uri? baseUri;
    readonly string prefix;
    readonly string? token;
    public bool Configured => baseUri is not null;
    public bool WritesEnabled { get; }
    public PredbatClient(HttpClient http, IConfiguration configuration)
    {
        this.http = http;
        // Both collection and the HA mirror fallback buffer JSON responses; keep either path bounded.
        http.MaxResponseContentBufferSize = Math.Min(http.MaxResponseContentBufferSize, 8 * 1024 * 1024);
        var address = configuration["Predbat:BaseUrl"];
        if (!string.IsNullOrWhiteSpace(address))
        {
            if (!Uri.TryCreate(address.TrimEnd('/') + "/", UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(uri.UserInfo))
                throw new DomainException("Predbat base URL must be an HTTP(S) URL without embedded credentials.", 400);
            baseUri = uri;
        }
        prefix = configuration["Predbat:Prefix"] ?? "predbat";
        if (!Regex.IsMatch(prefix, "^[a-z0-9_]+$")) throw new DomainException("Predbat prefix is invalid.", 400);
        token = configuration["Predbat:AccessToken"];
        WritesEnabled = bool.TryParse(configuration["Predbat:WritesEnabled"], out var enabled) && enabled;
    }
    public async Task<LiveSnapshot> CollectAsync(CancellationToken cancellationToken)
    {
        EnsureConfigured();
        try
        {
            var state = await ReadJsonAsync("api/state", cancellationToken);
            var sanitizedState = SanitizeState(state);
            var settings = Discover(sanitizedState);
            var planData = await ReadJsonAsync("api/plan_data", cancellationToken);
            // Native load_energy_actual contains adjusted load and forecast tails.
            // Retain it in RawState, without assigning it meter provenance.
            var plan = ParsePlan(planData);
            if (plan is not null) plan.CollectedAt = DateTimeOffset.UtcNow;
            return new(settings, plan, sanitizedState.ToJsonString(), Sanitize(planData)!.ToJsonString());
        }
        catch (DomainException) { throw; }
        catch (Exception e) when (e is HttpRequestException or JsonException or FormatException or InvalidOperationException or TaskCanceledException)
        { throw new DomainException("Predbat collection failed: the response was unavailable or invalid.", 502); }
    }
    /// <summary>Predbat mirrors every Home Assistant entity in /api/state. Only the explicitly requested entities are returned,
    /// reshaped to HA's /api/states objects so the telemetry parser treats both transports identically.</summary>
    public async Task<JsonArray?> ReadEntityStatesAsync(IReadOnlyCollection<string> entityIds, CancellationToken cancellationToken)
    {
        if (!Configured || entityIds.Count == 0) return null;
        try
        {
            var state = await ReadJsonAsync("api/state", cancellationToken);
            var result = new JsonArray();
            foreach (var id in entityIds.Distinct())
            {
                if (state[id] is not JsonObject entity) continue;
                // This transport knows Predbat's bearer credential; the HA parser only knows its own token.
                // Redact before returning mapped raw evidence so neither transport can expose echoed secrets.
                var item = new JsonObject { ["entity_id"] = id, ["state"] = Sanitize(entity["state"]), ["attributes"] = Sanitize(entity["attributes"]) ?? new JsonObject() };
                foreach (var stamp in new[] { "last_changed", "last_updated", "last_reported" }) if (entity[stamp] is JsonValue value) item[stamp] = value.DeepClone();
                result.Add(item);
            }
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception e) when (e is DomainException or HttpRequestException or JsonException or FormatException or InvalidOperationException or OperationCanceledException) { return null; }
    }
    public async Task ApplyAsync(List<Change> changes, List<Setting> settings, CancellationToken cancellationToken)
    {
        var attempted = false;
        try
        {
            if (!Configured || !WritesEnabled) throw new LiveWriteException("Predbat writes are disabled or unconfigured.", false);
            if (changes.Count == 0 || changes.Select(x => x.Key).Distinct().Count() != changes.Count)
                throw new LiveWriteException("A write requires distinct, supported settings.", false);
            var submitted = new List<(Change Change, Setting Setting)>();
            foreach (var change in changes)
            {
                var setting = settings.SingleOrDefault(x => x.Key == change.Key);
                if (setting is null || IsDiagnosticSetting(setting.Key) || !PredbatSettingsCatalogue.IsTunable(setting.Key, setting.Type, setting.Options) || !SupportedEntity(setting.EntityId) || !setting.Editable)
                    throw new LiveWriteException("This setting does not support verified Predbat writes.", false);
                ValidateValue(setting, change.After);
                submitted.Add((change, setting));
            }
            // Check the complete batch before making the first external change.
            var before = Discover(await ReadJsonAsync("api/state", cancellationToken));
            WholePreflight(settings, before);
            foreach (var item in submitted) Preflight(item.Change, item.Setting, before);
            var expected = JsonDefaults.Clone(settings);
            foreach (var (change, setting) in submitted)
            {
                // The immediate re-read detects edits made after initial batch preflight.
                var current = Discover(await ReadJsonAsync("api/state", cancellationToken));
                WholePreflight(expected, current);
                Preflight(change, setting, current);
                using var request = Request(HttpMethod.Post, "config");
                request.Content = new FormUrlEncodedContent([new(setting.EntityId.Replace(".", "__"), change.After)]);
                attempted = true;
                using var response = await http.SendAsync(request, cancellationToken);
                if (!(response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.Found))
                    throw new LiveWriteException("Predbat did not acknowledge the write; its outcome must be reconciled.", true);
                var after = Discover(await ReadJsonAsync("api/state", cancellationToken));
                expected.Single(x => x.EntityId == setting.EntityId).Value = change.After;
                WholePreflight(expected, after);
                var remote = after.SingleOrDefault(x => x.EntityId == setting.EntityId);
                if (remote is null || !Same(remote.Value, change.After))
                    throw new LiveWriteException("Predbat write could not be verified; its outcome must be reconciled.", true);
            }
        }
        catch (LiveWriteException e) when (attempted && !e.Uncertain)
        { throw new LiveWriteException("A prior write may have succeeded before this batch stopped; reconcile remote configuration.", true); }
        catch (LiveWriteException) { throw; }
        catch (Exception e) when (e is HttpRequestException or JsonException or FormatException or InvalidOperationException or DomainException or OperationCanceledException)
        { throw new LiveWriteException(attempted ? "Predbat write outcome is uncertain; reconcile remote configuration." : "Predbat write preflight failed; no write was attempted.", attempted); }
    }
    static void WholePreflight(List<Setting> expected, List<Setting> actual)
    {
        // A calculation starting/stopping is not an external configuration edit.
        // All genuine controls, including unavailable ones, still participate.
        expected = expected.Where(s => !IsDiagnosticSetting(s.Key)).ToList();
        actual = actual.Where(s => !IsDiagnosticSetting(s.Key)).ToList();
        if (expected.Count != actual.Count || expected.Select(x => x.EntityId).Distinct().Count() != expected.Count ||
            expected.Any(e => actual.SingleOrDefault(a => a.EntityId == e.EntityId) is not { } a ||
                e.Key != a.Key || !Same(e.Value, a.Value) || e.Type != a.Type || e.Editable != a.Editable ||
                e.Min != a.Min || e.Max != a.Max || e.Step != a.Step || !e.Options.SequenceEqual(a.Options)))
            throw new LiveWriteException("Predbat configuration changed; refresh the complete settings before applying.", false);
    }
    void Preflight(Change change, Setting setting, List<Setting> remote)
    {
        var actual = remote.SingleOrDefault(x => x.EntityId == setting.EntityId);
        if (actual is null || !actual.Editable || !Same(actual.Value, change.Before))
            throw new LiveWriteException("Predbat setting changed or became unavailable; refresh before applying.", false);
        ValidateValue(actual, change.After);
    }
    static void ValidateValue(Setting setting, string value)
    {
        if (setting.Type == "number")
        {
            if (!Number(value, out var number) || setting.Min is null || setting.Max is null || setting.Step <= 0 || number < setting.Min || number > setting.Max)
                throw new LiveWriteException("Numeric setting value or metadata is invalid.", false);
            if (!ChangeEngine.NumericStepMatches(number,setting.Min.Value,setting.Step))
                throw new LiveWriteException("Numeric value does not match the supported step.", false);
        }
        else if (setting.Type == "boolean" && value is not ("on" or "off")) throw new LiveWriteException("Switch must be on or off.", false);
        else if (setting.Type == "select" && !setting.Options.Contains(value)) throw new LiveWriteException("Select option is unsupported.", false);
        else if (setting.Type is not ("number" or "boolean" or "select")) throw new LiveWriteException("Setting type is unsupported.", false);
    }
    void EnsureConfigured() { if (!Configured) throw new DomainException("Predbat is not configured.", 503); }
    HttpRequestMessage Request(HttpMethod method, string path)
    {
        EnsureConfigured();
        var request = new HttpRequestMessage(method, new Uri(baseUri!, path));
        if (!string.IsNullOrWhiteSpace(token)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }
    async Task<JsonObject> ReadJsonAsync(string path, CancellationToken cancellationToken)
    {
        using var request = Request(HttpMethod.Get, path);
        using var response = await http.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode) throw new DomainException("Predbat returned an unsuccessful HTTP response.", 502);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        return JsonNode.Parse(raw) as JsonObject ?? throw new DomainException("Predbat returned an invalid JSON object.", 502);
    }
    bool IsPredbatEntity(string id) => id.Contains('.') && (id[(id.IndexOf('.') + 1)..].StartsWith(prefix + "_", StringComparison.Ordinal) || id.StartsWith(prefix + ".", StringComparison.Ordinal));
    bool SupportedEntity(string id) => IsPredbatEntity(id) && !Sensitive(id) && id.Split('.')[0] is "input_number" or "switch" or "select";
    List<Setting> Discover(JsonObject state)
    {
        var result = new List<Setting>();
        foreach (var (id, node) in state)
        {
            if (!SupportedEntity(id) || node is not JsonObject entity) continue;
            var raw = Text(entity["state"]);
            var attributes = entity["attributes"] as JsonObject ?? new();
            var domain = id.Split('.')[0];
            var type = domain == "input_number" ? "number" : domain == "switch" ? "boolean" : "select";
            if (type == "boolean") raw = raw?.ToLowerInvariant() switch { "true" => "on", "false" => "off", var value => value };
            var key = id[(id.IndexOf('.') + 1 + prefix.Length + 1)..];
            var options = (attributes["options"] as JsonArray)?.Select(Text).OfType<string>().ToList() ?? [];
            var min = OptionalNumber(attributes["min"]); var max = OptionalNumber(attributes["max"]); var step = OptionalNumber(attributes["step"]) ?? 0;
            var diagnostic = IsDiagnosticSetting(key);
            var editable = !diagnostic && raw is not null && raw is not ("unknown" or "unavailable") && (type switch
            { "number" => Number(raw, out _) && min.HasValue && max.HasValue && max >= min && step > 0, "boolean" => raw is "on" or "off", "select" => options.Contains(raw), _ => false });
            // The catalogue supplies the plain name, description, section, kind and risk. Predbat's own controls (manual
            // overrides, update/saverestore, mode/read-only) are read-only here: Joule never writes or restores them.
            result.Add(PredbatSettingsCatalogue.Apply(new Setting { Key = key, EntityId = id, Name = Text(attributes["friendly_name"]) ?? key, Description = Text(attributes["description"]) ?? "", Value = raw ?? "unavailable", Type = type, Risk = "High", Min = min, Max = max, Step = step, Options = options, AutoAllowed = false, Editable = editable }));
        }
        return result.OrderBy(x => x.Key, StringComparer.Ordinal).ToList();
    }
    /// <summary>Parses Predbat's /api/plan_data. Every state is normalised through <see cref="PredbatGlossary"/>: Action and
    /// ActionKey hold the canonical key, RawAction Predbat's own code. Split slots (state_text then state2_text), targets,
    /// reasons (rendered with the plan's reason_templates), overrides, rate types and the P10/car/iBoost columns are kept.
    /// Also used to backfill stored plans from their retained source snapshots.</summary>
    internal static PlanSnapshot? ParsePlan(JsonObject data)
    {
        if (data["plan"] is not JsonObject plan || plan["rows"] is not JsonArray rows || rows.Count == 0) return null;
        if (!Date(Text(plan["timestamp"]), out var timestamp)) throw new DomainException("Predbat plan timestamp is invalid.", 502);
        var snapshot = new PlanSnapshot { Source = "Predbat", At = timestamp };
        var templates = (plan["reason_templates"] as JsonObject)?.Where(x => x.Value is JsonValue).ToDictionary(x => x.Key, x => Text(x.Value) ?? "", StringComparer.Ordinal);
        var socMax = OptionalNumber(plan["soc_max"]);
        foreach (var item in rows)
        {
            if (item is not JsonObject row || !Date(Text(row["time"]), out var time)) throw new DomainException("Predbat plan row time is invalid.", 502);
            var raw = Text(row["state"]) ?? throw new DomainException("Predbat plan action is missing.", 502);
            snapshot.Slots.Add(DescribeSlot(new(time, RequiredNumber(row, "load_forecast"), null, RequiredNumber(row, "pv_forecast"), null, RequiredNumber(row, "soc_percent"), null, RequiredNumber(row, "import_rate"), RequiredNumber(row, "export_rate"), raw, RequiredNumber(row, "cost_change")), row, raw, templates));
        }
        for (var i = 0; i < snapshot.Slots.Count; i++)
        {
            var slot = snapshot.Slots[i];
            var minutes = i + 1 < snapshot.Slots.Count ? (snapshot.Slots[i + 1].Time - slot.Time).TotalMinutes : i > 0 ? snapshot.Slots[i - 1].DurationMinutes : 0;
            double? end = i + 1 < snapshot.Slots.Count ? snapshot.Slots[i + 1].SocForecast : slot.SocChangeKwh is { } change && socMax is > 0 ? Math.Clamp(slot.SocForecast + change / socMax.Value * 100, 0, 100) : null;
            slot = slot with { DurationMinutes = minutes is > 0 and <= 1440 ? (int)minutes : 0, SocForecastEnd = end is { } e ? Math.Round(e, 2) : null };
            snapshot.Slots[i] = AtReserve(slot);
        }
        return snapshot;
    }
    /// <summary>A plain forced export whose target is the level the battery already has, and that doesn't fall, exports nothing:
    /// Predbat only switches to HoldExp when the target is above the slot's level. Show it as the held export it is.</summary>
    static PlanSlot AtReserve(PlanSlot slot)
    {
        if (slot.ActionKey != "export" || slot.SecondaryAction is not null || slot.TargetPercent is not { } target || slot.SocForecastEnd is not { } end) return slot;
        if (target < slot.SocForecast - .5 || end < slot.SocForecast - .5 || slot.SocChangeKwh is < -.05) return slot;
        var held = PredbatGlossary.Actions["hold-export"];
        return slot with { Action = held.Key, ActionKey = held.Key, ActionId = held.Key, PrimaryAction = held.Key, ActionLabel = held.Label };
    }
    static PlanSlot DescribeSlot(PlanSlot slot, JsonObject row, string raw, IReadOnlyDictionary<string, string>? templates)
    {
        var reasons = new List<PlanReason>();
        if (row["reasons"] is JsonArray list)
            foreach (var r in list.OfType<JsonObject>())
                if (Text(r["code"]) is { Length: > 0 and <= 100 } code)
                    reasons.Add(new(code, (r["params"] as JsonObject)?.Where(x => x.Value is JsonValue).Take(12).ToDictionary(x => x.Key, x => ParamText(x.Value!), StringComparer.Ordinal) ?? []));
        var whole = PredbatGlossary.Parse(raw);
        var first = Text(row["state_text"]) is { } text ? PredbatGlossary.Parse(text) : null;
        var second = Text(row["state2_text"]) is { Length: > 0 } text2 ? PredbatGlossary.Parse(text2) : null;
        var splitTime = reasons.Select(x => x.Params.GetValueOrDefault("split_time")).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));
        GlossaryEntry? primary; string? secondaryKey = null; string key; string label;
        if (second?.Entry?.Key is { } secondKey)
        {
            // Predbat writes the slot's last-assigned state into "state"; the split cell keeps both halves.
            primary = first?.Entry ?? (first?.HoldForCar == true ? PredbatGlossary.Find("Hold for car") : PredbatGlossary.Find("demand"));
            secondaryKey = secondKey;
            key = PredbatGlossary.Combine(primary?.Key, secondKey);
            var secondLabel = second.Entry.Label;
            label = key == "charge-export" && primary?.Key == "charge" ? PredbatGlossary.Actions[key].Label
                : $"{primary?.Label ?? PredbatGlossary.Actions["demand"].Label}{(splitTime is null ? "" : " until " + splitTime)}, then {char.ToLowerInvariant(secondLabel[0]) + secondLabel[1..]}";
        }
        else
        {
            // Demand rows show a car instead of an arrow while the battery is held for a charging car.
            primary = first?.HoldForCar == true && whole.Entry?.Key == "demand" ? PredbatGlossary.Find("Hold for car") : whole.Entry;
            key = primary?.Key ?? PredbatGlossary.UnknownKey;
            label = primary?.Label ?? PredbatGlossary.UnknownLabel;
        }
        var importType = Text(row["import_rate_adjust_type"]); var exportType = Text(row["export_rate_adjust_type"]);
        var target = OptionalNumber(row["state_target"]) ?? whole.TargetPercent;
        var mixed = (row["state_mixed"] as JsonArray)?.Select(Text).OfType<string>().Take(6).ToList();
        return slot with
        {
            Action = key == PredbatGlossary.UnknownKey ? raw : key,
            RawAction = raw,
            ActionKey = key,
            ActionId = second is null && primary is not null ? primary.Id : key,
            ActionLabel = label,
            PrimaryAction = primary?.Key ?? (key == PredbatGlossary.UnknownKey ? null : key),
            SecondaryAction = secondaryKey,
            State2 = second?.Code is { Length: > 0 } code2 ? code2 : null,
            SplitTime = splitTime,
            TargetPercent = target is >= 0 and <= 100 ? target : null,
            SocChangeKwh = OptionalNumber(row["soc_change"]),
            Reasons = reasons.Count == 0 ? null : reasons,
            ReasonText = PredbatGlossary.RenderReasons(reasons, templates),
            Override = Text(row["state_override"]) is { Length: > 0 and <= 100 } manual ? manual : null,
            MixedStates = mixed is { Count: > 0 } ? mixed : null,
            ImportRateType = importType, ExportRateType = exportType,
            RateEstimated = importType is null && exportType is null ? null : PredbatGlossary.RateEstimated(importType) || PredbatGlossary.RateEstimated(exportType),
            ImportRateAdjusted = OptionalNumber(row["import_rate_adjusted"]), ExportRateAdjusted = OptionalNumber(row["export_rate_adjusted"]),
            CarKwh = OptionalNumber(row["car_charging"]), IBoostKwh = OptionalNumber(row["iboost"]),
            Pv10 = OptionalNumber(row["pv_forecast10"]), Load10 = OptionalNumber(row["load_forecast10"]),
            ClippedKwh = OptionalNumber(row["clipped"]), TotalCost = OptionalNumber(row["total_cost"]),
        };
    }
    static string ParamText(JsonNode value) => value is JsonValue v && v.TryGetValue<double>(out var number) ? number.ToString("0.##", CultureInfo.InvariantCulture) : Text(value) ?? "";
    static bool Date(string? text, out DateTimeOffset date)
    {
        // Predbat TIME_FORMAT uses %z (+0000); normalize it for .NET ISO parsing.
        if (text is not null) text = Regex.Replace(text, @"([+-]\d{2})(\d{2})$", "$1:$2");
        return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out date);
    }
    static double RequiredNumber(JsonObject row, string key) => OptionalNumber(row[key]) ?? throw new DomainException("Predbat plan is missing valid numeric data.", 502);
    static string? Text(JsonNode? node) => node is JsonValue value ? value.ToString() : null;
    static double? OptionalNumber(JsonNode? node) => Number(Text(node), out var value) ? value : null;
    static bool Number(string? value, out double number) => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out number) && double.IsFinite(number);
    static bool Same(string left, string right) => left == right || Number(left, out var a) && Number(right, out var b) && Math.Abs(a - b) < 1e-9;
    static bool Sensitive(string key) => Regex.IsMatch(key, "password|passwd|secret|token|credential|authorization|api.?key|access.?key|private.?key|connection.?string|user.?name|auth.?header", RegexOptions.IgnoreCase);
    JsonObject SanitizeState(JsonObject state)
    {
        var safe = new JsonObject();
        // The state endpoint may contain unrelated HA entities with arbitrary private strings.
        // Store only this Predbat instance's entities and recursively remove credential fields.
        foreach (var (key, value) in state) if (IsPredbatEntity(key) && !Sensitive(key)) safe[key] = Sanitize(value);
        return safe;
    }
    JsonNode? Sanitize(JsonNode? node)
    {
        if (node is JsonObject obj)
        {
            var safe = new JsonObject();
            foreach (var (key, child) in obj) if (!Sensitive(key)) safe[key] = Sanitize(child);
            return safe;
        }
        if (node is JsonArray array) return new JsonArray(array.Select(Sanitize).ToArray());
        if (node is JsonValue value && value.TryGetValue<string>(out var text) && (Regex.IsMatch(text, @"https?://[^\s/]*@", RegexOptions.IgnoreCase) || Regex.IsMatch(text, "Bearer [A-Za-z0-9]", RegexOptions.IgnoreCase) || Regex.IsMatch(text, @"(?:password|access_token|api_key)\s*[:=]", RegexOptions.IgnoreCase) || !string.IsNullOrEmpty(token) && text.Contains(token, StringComparison.Ordinal))) return JsonValue.Create("[redacted]");
        return node?.DeepClone();
    }
}
