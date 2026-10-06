using System.Net;
using System.Text.Json;
using Joule;
using Xunit;

namespace Joule.Tests;

public sealed class ChatGptDisconnectTests : IDisposable
{
    readonly string directory = Path.Combine(Path.GetTempPath(), "predbat-disconnect-" + Guid.NewGuid().ToString("N"));
    sealed class RevocationDiscovery(string endpoint) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.Equal("https://auth.openai.com/.well-known/openid-configuration", request.RequestUri!.AbsoluteUri);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { revocation_endpoint = endpoint })) });
        }
    }

    [Theory]
    [InlineData("not-an-absolute-url")]
    [InlineData("https://[invalid-host/")]
    public async Task UnusableRevocationDiscoveryStillRemovesLocalCredentials(string endpoint)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "chatgpt-credentials.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new
        {
            issuer = "https://auth.openai.com", client_id = "oaiapp_fixture", subject = "fixture-account",
            access_token = "fixture-access", refresh_token = "fixture-refresh", id_token = "fixture-id",
            scopes = new[] { "resource.invoke", "chatgpt.tokens.use.direct" }, expires_at = DateTimeOffset.UtcNow.AddHours(1)
        }));
        using var http = new HttpClient(new RevocationDiscovery(endpoint));
        var auth = new ChatGptAuth(http, directory);
        Assert.True(auth.Connected);

        await auth.DisconnectAsync(default);

        Assert.False(auth.Connected);
        Assert.False(auth.RemoteRevocationConfirmed);
        Assert.False(File.Exists(path));
        Assert.False(new ChatGptAuth(http, directory).Connected);
        Assert.True(File.Exists(Path.Combine(directory, "chatgpt-registration.json")));
    }

    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
}
