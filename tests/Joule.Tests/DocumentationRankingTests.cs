using System.Net;
using Microsoft.Extensions.Configuration;
using Joule;
using Xunit;

namespace Joule.Tests;

public sealed class DocumentationRankingTests : IDisposable
{
    readonly string directory = Path.Combine(Path.GetTempPath(), "predbat-doc-ranking-" + Guid.NewGuid().ToString("N"));
    sealed class Factory(HttpClient http) : IHttpClientFactory { public HttpClient CreateClient(string name) => http; }
    sealed class Corpus(bool exactFirst = true, bool belowSubstring = true) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var exact = (belowSubstring ? "load_scaling10 adjusts the pessimistic estimate.\n" : "")
                + "input_number.predbat_load_scaling adjusts the base estimate.\n" + new string('\n', 12);
            var generic = string.Join("\n", Enumerable.Range(1, 8).Select(i => $"forecast passage {i}\n" + new string('\n', 12)));
            var text = request.RequestUri!.AbsolutePath.EndsWith("customisation.md")
                ? exactFirst ? exact + generic : generic + exact
                : "No relevant settings here.";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(text) });
        }
    }

    [Fact]
    public async Task ExactSettingInsideAnExcerptSurvivesLaterGenericQueryMatches()
    {
        using var db = new DataStore(directory); using var http = new HttpClient(new Corpus());
        var docs = new DocumentationService(db, new Factory(http), new ConfigurationBuilder().Build());
        var result = await docs.SearchAsync("load_scaling forecast");
        Assert.Contains(result.References, reference => DocumentationService.CoversSetting(reference, "load_scaling"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LaterExactSettingOutranksEarlierGenericQueryMatches(bool belowSubstring)
    {
        using var db = new DataStore(directory); using var http = new HttpClient(new Corpus(exactFirst: false, belowSubstring));
        var docs = new DocumentationService(db, new Factory(http), new ConfigurationBuilder().Build());
        var result = await docs.SearchAsync("load_scaling forecast");
        Assert.NotEmpty(result.References);
        Assert.True(DocumentationService.CoversSetting(result.References[0], "load_scaling"));
    }

    [Fact]
    public async Task GenericQueryWordsRemainSearchableWithoutASettingIdentifier()
    {
        using var db = new DataStore(directory); using var http = new HttpClient(new Corpus(exactFirst: false));
        var docs = new DocumentationService(db, new Factory(http), new ConfigurationBuilder().Build());
        var result = await docs.SearchAsync("forecast");
        Assert.NotEmpty(result.References);
        Assert.All(result.References, reference => Assert.Contains("forecast", reference.Excerpt));
    }

    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
}
