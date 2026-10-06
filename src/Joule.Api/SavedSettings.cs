using System.Text.Json;
using System.Text.RegularExpressions;

namespace Joule;

/// <summary>A setting the Setup page can save. Kind: bool, url, entity, accessKey or token. Secrets are never sent to the browser.</summary>
public sealed record SetupField(string Key, string Kind, bool Secret)
{
    /// <summary>The environment variable that sets (and overrides) this value, e.g. Predbat__BaseUrl.</summary>
    public string EnvVar => Key.Replace(":", "__");
}

/// <summary>
/// Settings chosen in Setup, kept in settings.json in the data directory so a new install never has to edit its environment.
/// Environment variables (and command-line or appsettings values) always win: a key set there, non-empty, is reported as
/// "environment" and the saved value is ignored. Only the keys in <see cref="Fields"/> are ever read from or written to the file.
/// </summary>
public sealed class SavedSettings
{
    public const string FileName = "settings.json";
    static readonly Regex EntityId = new("^[a-z_]+\\.[a-z0-9_]+$", RegexOptions.CultureInvariant);

    public static readonly IReadOnlyList<SetupField> Fields =
    [
        new("App:Demo", "bool", false),
        new("App:AccessKey", "accessKey", true),
        new("Predbat:BaseUrl", "url", false),
        new("Predbat:McpToken", "token", true),
        new("Predbat:WritesEnabled", "bool", false),
        new("HomeAssistant:BaseUrl", "url", false),
        new("HomeAssistant:AccessToken", "token", true),
        .. SensorCandidates.EnvNames.Values.Select(name => new SetupField("HomeAssistant:Entities:" + name, "entity", false)),
        new("Ai:ApiBaseUrl", "url", false),
        new("Ai:ApiKey", "token", true),
    ];
    static readonly Dictionary<string, SetupField> byKey = Fields.ToDictionary(f => f.Key, StringComparer.OrdinalIgnoreCase);
    public static SetupField? Field(string key) => byKey.GetValueOrDefault(key);

    readonly object gate = new();
    Dictionary<string, string> saved;
    public string Path { get; }
    /// <summary>Keys given a non-empty value outside settings.json when Joule started (usually environment variables).</summary>
    public IReadOnlySet<string> External { get; }
    /// <summary>Values as they were when Joule started (saved or external), so Setup can tell when a restart is still needed.</summary>
    public IReadOnlyDictionary<string, string> Effective { get; }

    SavedSettings(string path, Dictionary<string, string> saved, HashSet<string> external, Dictionary<string, string> effective)
    {
        Path = path; this.saved = saved; External = external; Effective = effective;
    }

    /// <summary>The values saved in settings.json now (including any saved since start), by key.</summary>
    public IReadOnlyDictionary<string, string> Saved { get { lock (gate) return new Dictionary<string, string>(saved, StringComparer.OrdinalIgnoreCase); } }

    /// <summary>"environment" when set outside settings.json, "saved" when it comes from Setup, otherwise null (the default).</summary>
    public string? Source(string key) => External.Contains(key) ? "environment" : Saved.ContainsKey(key) ? "saved" : null;

    /// <summary>
    /// Reads settings.json from <paramref name="dataDirectory"/> and adds its values beneath the existing configuration: a key with a
    /// non-empty value from another source keeps it. Throws <see cref="InvalidDataException"/> with a one-line message for a broken file.
    /// </summary>
    public static SavedSettings Attach(IConfigurationBuilder builder, IConfiguration current, string dataDirectory)
    {
        var path = System.IO.Path.Combine(dataDirectory, FileName);
        var saved = Read(path);
        var external = Fields.Select(f => f.Key).Where(k => !string.IsNullOrWhiteSpace(current[k])).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var applied = saved.Where(x => !external.Contains(x.Key)).ToDictionary(x => x.Key, x => (string?)x.Value);
        // A blank line in .env (Predbat__BaseUrl=) means "not set": hide the empty value so defaults apply and nothing tries to parse "".
        foreach (var f in Fields) if (!applied.ContainsKey(f.Key) && current[f.Key] is { } blank && string.IsNullOrWhiteSpace(blank)) applied[f.Key] = null;
        if (applied.Count > 0) builder.AddInMemoryCollection(applied);
        var effective = Fields.Select(f => (f.Key, Value: external.Contains(f.Key) ? current[f.Key]! : saved.GetValueOrDefault(f.Key)))
            .Where(x => !string.IsNullOrEmpty(x.Value)).ToDictionary(x => x.Key, x => x.Value!, StringComparer.OrdinalIgnoreCase);
        return new(path, saved, external, effective);
    }

    static Dictionary<string, string> Read(string path)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(path)) return result;
        try
        {
            using var json = JsonDocument.Parse(File.ReadAllText(path));
            if (json.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException($"{path} must be a JSON object.");
            foreach (var p in json.RootElement.EnumerateObject())
            {
                // Unknown keys are ignored: this file can only ever hold what Setup itself writes.
                if (Field(p.Name) is not { } field) continue;
                var value = p.Value.ValueKind switch { JsonValueKind.String => p.Value.GetString(), JsonValueKind.True => "true", JsonValueKind.False => "false", _ => null };
                if (string.IsNullOrWhiteSpace(value)) continue;
                result[field.Key] = value.Trim();
            }
        }
        catch (JsonException) { throw new InvalidDataException($"{path} isn't valid JSON. Fix it or move it aside to start with the settings from your environment only."); }
        return result;
    }

    /// <summary>A one-line problem with <paramref name="value"/> for this field, or null when it can be saved.</summary>
    public static string? Validate(SetupField field, string value)
    {
        var v = value.Trim();
        switch (field.Kind)
        {
            case "bool":
                return v is "true" or "false" ? null : $"{field.EnvVar} must be true or false.";
            case "url":
                return Uri.TryCreate(v, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" && string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment) && v.Length <= 500
                    ? null : "Enter an address like http://192.168.1.20:5052 (http or https, no user name, password or ?query).";
            case "entity":
                return EntityId.IsMatch(v) && v.Length <= 255 ? null : $"\"{Short(v)}\" isn't a Home Assistant entity id (like sensor.house_load_today).";
            case "accessKey":
                if (v.Length < AppAuthOptions.MinimumKeyLength) return $"The access key needs at least {AppAuthOptions.MinimumKeyLength} characters.";
                return v.Length <= 256 && v.All(c => c is >= '!' and <= '~') ? null : "The access key can only use letters, digits and punctuation (no spaces), up to 256 characters.";
            case "token":
                return v.Length is > 0 and <= 4096 && v.All(c => c is >= '!' and <= '~') ? null : "That doesn't look like a token: no spaces or special characters, up to 4096 characters.";
            default:
                return "This setting can't be saved from Setup.";
        }
    }
    static string Short(string v) => v.Length > 60 ? v[..60] + "…" : v;

    /// <summary>
    /// Saves <paramref name="changes"/> (a null or empty value removes the saved value) after validating every one. Keys set in the
    /// environment are refused, because the environment would override them anyway. The file is replaced atomically, readable only by Joule.
    /// </summary>
    public void Save(IReadOnlyDictionary<string, string?> changes)
    {
        var problems = new List<string>();
        var normalised = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in changes)
        {
            if (Field(key) is not { } field) { problems.Add($"{key} can't be saved from Setup."); continue; }
            if (External.Contains(field.Key)) { problems.Add($"{field.EnvVar} is set in Joule's environment, which always wins. Change it there."); continue; }
            if (string.IsNullOrWhiteSpace(value)) { normalised[field.Key] = null; continue; }
            if (Validate(field, value) is { } problem) problems.Add(problem);
            else normalised[field.Key] = value.Trim();
        }
        if (problems.Count > 0) throw new DomainException(string.Join(" ", problems.Distinct()), 400);
        lock (gate)
        {
            var next = new Dictionary<string, string>(saved, StringComparer.OrdinalIgnoreCase);
            foreach (var (key, value) in normalised) { if (value is null) next.Remove(key); else next[key] = value; }
            Write(next);
            saved = next;
        }
    }

    void Write(Dictionary<string, string> values)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        var ordered = Fields.Where(f => values.ContainsKey(f.Key)).ToDictionary(f => f.Key, f => values[f.Key]);
        var text = JsonSerializer.Serialize(ordered, new JsonSerializerOptions { WriteIndented = true }) + "\n";
        var temp = Path + ".tmp";
        // Secrets may be saved here (like ChatGPT's credentials in auth/): owner read/write only.
        using (var stream = OperatingSystem.IsWindows() ? new FileStream(temp, FileMode.Create, FileAccess.Write) : new FileStream(temp, new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write, UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite }))
        using (var writer = new StreamWriter(stream)) writer.Write(text);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.Move(temp, Path, overwrite: true);
    }
}

/// <summary>Restarts the web host in-process after Setup saves settings, so new settings apply without touching Docker.</summary>
public static class JouleRestart
{
    static int requested;
    public static void Request(IHostApplicationLifetime lifetime) { Interlocked.Exchange(ref requested, 1); lifetime.StopApplication(); }
    /// <summary>True once after a restart was requested.</summary>
    public static bool Consume() => Interlocked.Exchange(ref requested, 0) == 1;
}
