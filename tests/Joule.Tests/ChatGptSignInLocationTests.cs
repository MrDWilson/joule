using System.Net;
using Microsoft.AspNetCore.Http;
using Joule;
using Xunit;

public class ChatGptSignInLocationTests
{
    [Theory]
    [InlineData("127.0.0.1", 5080, "127.0.0.1", false, null, true)]
    [InlineData("localhost", 5080, "::1", false, "http://localhost:5080", true)]
    [InlineData("energy.example.test", 5080, "127.0.0.1", false, null, false)]
    [InlineData("127.0.0.1", 5080, "192.0.2.1", false, null, false)]
    [InlineData("127.0.0.1", 5080, "127.0.0.1", true, null, false)]
    [InlineData("127.0.0.1", 5173, "127.0.0.1", false, null, false)]
    [InlineData("127.0.0.1", 5080, "127.0.0.1", false, "https://energy.example.test", false)]
    [InlineData("127.0.0.1", 5080, "127.0.0.1", false, "null", false)]
    public void OnlyDirectLocalNativeRuntimeOffersLoopbackSignIn(string host, int port, string peer, bool container, string? origin, bool available)
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = "http";
        context.Request.Host = new HostString(host, port);
        context.Connection.LocalPort = port;
        context.Connection.RemoteIpAddress = IPAddress.Parse(peer);
        if (origin != null) context.Request.Headers.Origin = origin;
        Assert.Equal(available, ChatGptSignInLocation.IsAvailable(context, container));
    }
}
