using System.Net;
using Microsoft.Extensions.Configuration;
using Joule;
using Xunit;

namespace Joule.Tests;

public sealed class DiagnosticDocumentationTests : IDisposable
{
    readonly string directory=Path.Combine(Path.GetTempPath(),"predbat-diagnostic-docs-"+Guid.NewGuid().ToString("N"));
    sealed class Factory(HttpClient http):IHttpClientFactory { public HttpClient CreateClient(string name)=>http; }
    sealed class Sources:HttpMessageHandler
    {
        public List<Uri> Requests=[];
        public bool Offline;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {
            Requests.Add(request.RequestUri!);
            if(Offline)throw new HttpRequestException("offline fixture");
            var content=request.RequestUri!.AbsolutePath switch {
                "/springfall2008/batpred/v9.3.5/templates/tesla_powerwall.yaml"=>"discharge_stop_service:\n  - service: select.select_option\n    entity_id: select.fixture_allow_export\n    option: never\n",
                "/springfall2008/batpred/v9.3.5/apps/predbat/execute.py"=>"# Hold for car prevents discharging while EV charging\nstatus = 'Hold for car'\n",
                _=>"Generic documentation with no fixture matches."
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(content)});
        }
    }
    [Theory]
    [InlineData("discharge_stop_service","templates/tesla_powerwall.yaml","option: never")]
    [InlineData("Hold for car","apps/predbat/execute.py","Hold for car")]
    public async Task PinnedDiagnosticPrimarySourcesAreSearchableAndAvailableOffline(string query,string path,string expected)
    {
        using var db=new DataStore(directory);using var source=new Sources();using var http=new HttpClient(source);
        var docs=new DocumentationService(db,new Factory(http),new ConfigurationBuilder().Build());
        var result=await docs.SearchAsync(query);
        var reference=Assert.Single(result.References,r=>r.Path==path);
        Assert.Contains(expected,reference.Excerpt);Assert.Equal("v9.3.5",reference.Version);Assert.Equal(64,reference.ContentSha256.Length);Assert.True(reference.StartLine>0);Assert.True(reference.EndLine>=reference.StartLine);
        Assert.Equal("https://raw.githubusercontent.com/springfall2008/batpred/v9.3.5/"+path,reference.Url);
        Assert.All(source.Requests,uri=>Assert.Equal("raw.githubusercontent.com",uri.Host));
        source.Offline=true;var count=source.Requests.Count;
        var cached=await docs.SearchAsync(query);Assert.Equal(reference.Id,Assert.Single(cached.References,r=>r.Path==path).Id);Assert.Equal(count,source.Requests.Count);
    }
    public void Dispose(){if(Directory.Exists(directory))Directory.Delete(directory,true);}
}
