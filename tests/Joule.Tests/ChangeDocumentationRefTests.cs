using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Joule;
using Xunit;

namespace Joule.Tests;

/// <summary>Documentation citations follow the Predbat version the user actually runs.</summary>
public class ChangeDocumentationRefTests : IDisposable
{
    readonly string directory = Path.Combine(Path.GetTempPath(), "predbat-docs-ref-" + Guid.NewGuid().ToString("N"));
    sealed class Factory : IHttpClientFactory { public HttpClient CreateClient(string name) => new(); }
    sealed class NoClient : IPredbatClient
    {
        public bool Configured => true; public bool WritesEnabled => false;
        public Task<LiveSnapshot> CollectAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task ApplyAsync(List<Change> changes, List<Setting> settings, CancellationToken ct) => throw new NotSupportedException();
    }
    static IConfiguration Config(string? docsRef) => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Predbat:DocumentationRef"] = docsRef }).Build();

    [Theory]
    [InlineData("v9.3.5 Bug fixes cloud inverters & Misc", "v9.3.5")]
    [InlineData("v9.3.4 IOG started-dispatch fix", "v9.3.4")]
    [InlineData("v10.0 New", "v10.0")]
    [InlineData("main", null)]
    [InlineData("Loading...", null)]
    [InlineData(null, null)]
    public void ReleaseRefComesFromTheUpdateSelect(string? value, string? expected) => Assert.Equal(expected, DocumentationService.ReleaseRef(value));

    [Fact]
    public async Task VersionFollowsPredbatThenConfigurationThenThePinnedDefault()
    {
        using var db = new DataStore(directory);
        var state = new StateService(db, new NoClient(), true);
        var docs = new DocumentationService(db, new Factory(), Config("v9.3.3"), state);
        Assert.Equal("v9.3.5", docs.Version);
        Assert.Equal("predbat", docs.VersionSource);
        using (var status = JsonDocument.Parse(JsonSerializer.Serialize(docs.Status(), JsonDefaults.Options)))
        {
            Assert.True(status.RootElement.GetProperty("matchesPredbat").GetBoolean());
            Assert.All(status.RootElement.GetProperty("primaryFiles").EnumerateArray(), f => Assert.Contains("/batpred/v9.3.5/", f.GetProperty("url").GetString()));
        }
        // Predbat updates: the ref (and so the cache directory) follows.
        await state.MutateAsync(s => s.Settings.Single(x => x.Key == "update").Value = "v9.3.6 Next release");
        Assert.Equal("v9.3.6", docs.Version);
        await state.MutateAsync(s => s.Settings.Single(x => x.Key == "update").Value = "main");
        Assert.Equal("v9.3.3", docs.Version); Assert.Equal("configuration", docs.VersionSource);
        Assert.Equal(PredbatSettingsCatalogue.DocsRef, new DocumentationService(db, new Factory(), Config(null)).Version);
        Assert.Equal("default", new DocumentationService(db, new Factory(), Config(null)).VersionSource);
    }

    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
}
