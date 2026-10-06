using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Joule;

// Only known primary repository files at the configured ref are downloaded. Cached bytes
// are immutable for that ref; each excerpt carries its source content hash and line range.
public sealed class DocumentationService(DataStore db, IHttpClientFactory clients, IConfiguration configuration, StateService? state = null)
{
    public static readonly string[] PrimaryFiles = ["docs/customisation.md", "docs/apps-yaml.md", "docs/output-data.md", "docs/energy-rates.md", "docs/faq.md", "templates/tesla_powerwall.yaml", "apps/predbat/execute.py"];
    readonly SemaphoreSlim gate = new(1, 1);
    /// <summary>The docs ref matches the Predbat the user runs: the version its update select reports ("v9.3.5 Bug fixes…"),
    /// else Predbat:DocumentationRef, else the catalogue's pinned ref. The cache is keyed by ref, so an update re-caches.</summary>
    public string Version => ReportedVersion ?? ConfiguredVersion ?? PredbatSettingsCatalogue.DocsRef;
    string? ConfiguredVersion => configuration["Predbat:DocumentationRef"] is { Length: > 0 } configured ? configured : null;
    /// <summary>Predbat's own version from its update select, when it names a release.</summary>
    public string? ReportedVersion => ReleaseRef(state?.SettingValue("update"));
    public string VersionSource => ReportedVersion is not null ? "predbat" : ConfiguredVersion is not null ? "configuration" : "default";
    internal static string? ReleaseRef(string? updateValue) => updateValue is not null && Regex.Match(updateValue, @"^\s*(v\d+(?:\.\d+){1,3})\b") is { Success: true } match ? match.Groups[1].Value : null;
    string DirectoryPath => Path.Combine(db.DirectoryPath, "documentation", Hash(Version)[..16]);
    static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    string CachePath(string path) => Path.Combine(DirectoryPath, Path.GetFileNameWithoutExtension(path) + ".json");
    void ValidateVersion()
    {
        if (!Regex.IsMatch(Version, "^[a-zA-Z0-9._-]{1,80}$")) throw new DomainException("Predbat documentation ref is invalid. Configure a release tag or commit.", 400);
    }
    DocumentationCacheEntry? ReadCache(string path)
    {
        try
        {
            if (!File.Exists(CachePath(path))) return null;
            var entry = JsonSerializer.Deserialize<DocumentationCacheEntry>(File.ReadAllText(CachePath(path)), JsonDefaults.Options);
            return entry != null && entry.Version == Version && entry.Path == path && entry.Url == Url(path) && entry.Text is { Length: > 0 and <= 2_000_000 } && Hash(entry.Text) == entry.ContentSha256 ? entry : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }
    string Url(string path) => $"https://raw.githubusercontent.com/springfall2008/batpred/{Version}/{path}";
    public object Status()
    {
        ValidateVersion();
        return new { version = Version, versionSource = VersionSource, predbatVersion = ReportedVersion, matchesPredbat = ReportedVersion is not null && ReportedVersion == Version, primaryFiles = PrimaryFiles.Select(path => { var c = ReadCache(path); return new { path, url = Url(path), availableOffline = c != null, contentSha256 = c?.ContentSha256, cachedAt = c?.CachedAt }; }), policy = "Known primary files only. Immutable cached content per ref with SHA-256. Change the ref to obtain another version; excerpts retain source and line ranges." };
    }
    async Task<DocumentationCacheEntry?> ReadOrFetchAsync(string path, CancellationToken ct)
    {
        var cached = ReadCache(path); if (cached != null) return cached;
        try
        {
            var client = clients.CreateClient("docs");
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            // ResponseHeadersRead ends HttpClient's timeout when headers arrive.
            // Bound the complete body/cache operation independently as well.
            var timeout = client.Timeout == Timeout.InfiniteTimeSpan || client.Timeout > TimeSpan.FromSeconds(15) ? TimeSpan.FromSeconds(15) : client.Timeout;
            deadline.CancelAfter(timeout); var readToken = deadline.Token;
            using var response = await client.GetAsync(Url(path), HttpCompletionOption.ResponseHeadersRead, readToken);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > 2_000_000) return null;
            using var stream = await response.Content.ReadAsStreamAsync(readToken);
            using var buffer = new MemoryStream(); var chunk = new byte[8192];
            while (true) { var count = await stream.ReadAsync(chunk, readToken); if (count == 0) break; if (buffer.Length + count > 2_000_000) return null; buffer.Write(chunk, 0, count); }
            var text = Encoding.UTF8.GetString(buffer.ToArray()); if (string.IsNullOrWhiteSpace(text)) return null;
            var entry = new DocumentationCacheEntry(Version, path, Url(path), Hash(text), DateTimeOffset.UtcNow, text);
            Directory.CreateDirectory(DirectoryPath);
            var temporary = CachePath(path) + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try { await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(entry, JsonDefaults.Options), readToken); File.Move(temporary, CachePath(path), true); }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            return entry;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException || ex is OperationCanceledException && !ct.IsCancellationRequested) { return null; }
    }
    public async Task<DocumentationSearchResult> SearchAsync(string query, CancellationToken ct = default)
    {
        ValidateVersion();
        if (string.IsNullOrWhiteSpace(query) || query.Length is < 2 or > 100) throw new DomainException("Search documentation with a phrase of 2–100 characters.", 400);
        await gate.WaitAsync(ct);
        try
        {
            var tokens = Regex.Matches(query.ToLowerInvariant(), "[a-z0-9_]{2,}").Select(x => x.Value).Distinct().Take(8).ToArray();
            if (tokens.Length == 0) throw new DomainException("Use letters, numbers or a setting name for documentation search.", 400);
            var ranked = new List<(DocumentationReference Reference, int Score)>(); var gaps = new List<string>();
            // One exact setting-like identifier outweighs every generic query
            // word, so broad prose cannot crowd out a later setting reference.
            var exactPatterns = tokens.Select(token => (Pattern: new Regex($"(?<![a-zA-Z0-9_])(?:[a-zA-Z0-9_.]*predbat_)?{Regex.Escape(token)}(?![a-zA-Z0-9_])", RegexOptions.IgnoreCase), Weight: token.Contains('_') ? tokens.Length + 1 : 1)).ToArray();
            foreach (var path in PrimaryFiles)
            {
                var entry = await ReadOrFetchAsync(path, ct);
                if (entry == null) { gaps.Add($"{path} unavailable and no verified cached copy exists at {Version}."); continue; }
                var lines = entry.Text.Replace("\r\n", "\n").Split('\n');
                for (var i = 0; i < lines.Length; i++)
                {
                    if (!tokens.Any(token => lines[i].Contains(token, StringComparison.OrdinalIgnoreCase))) continue;
                    var start = i > 0 && lines[i - 1].Length + lines[i].Length + 2 <= 2200 ? i - 1 : i;
                    var end = start;
                    var excerpt = new StringBuilder();
                    for (; end < Math.Min(lines.Length, start + 10); end++)
                    { if (excerpt.Length + lines[end].Length + Environment.NewLine.Length > 2200) break; excerpt.AppendLine(lines[end]); }
                    if (excerpt.Length == 0) continue;
                    // Score the complete excerpt before skipping its lines. An
                    // exact setting below a substring hit is still useful evidence.
                    var excerptText = excerpt.ToString();
                    var score = exactPatterns.Sum(pattern => pattern.Pattern.IsMatch(excerptText) ? pattern.Weight : 0);
                    if (ranked.Count == 8 && score <= ranked[^1].Score) continue;
                    var referenceId = "doc-" + Hash($"{entry.Version}:{entry.Path}:{entry.ContentSha256}:{start + 1}:{end}")[..24];
                    ranked.Add((new(referenceId, entry.Version, entry.Path, entry.Url, entry.ContentSha256, start + 1, end, excerptText, entry.CachedAt), score));
                    ranked = ranked.OrderByDescending(candidate => candidate.Score).Take(8).ToList();
                    i = Math.Max(i, end - 1);
                }
            }
            if (ranked.Count == 0) gaps.Add("No matching passages were retrieved. Setting recommendations must wait for matching primary documentation.");
            return new(Version, ranked.Select(candidate => candidate.Reference).ToList(), gaps);
        }
        finally { gate.Release(); }
    }
    public static bool CoversSetting(DocumentationReference reference, string key) =>
        // Runtime entity names embed the setting after 'predbat_'. Avoid load_scaling10
        // matching load_scaling, and reject a key merely present in a neighbouring filename.
        Regex.IsMatch(reference.Excerpt, $"(?<![a-zA-Z0-9_])(?:[a-zA-Z0-9_.]*predbat_)?{Regex.Escape(key)}(?![a-zA-Z0-9_])", RegexOptions.IgnoreCase);
}
