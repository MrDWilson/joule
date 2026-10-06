using System.Net;

namespace Joule;

/// <summary>The OSS authorization flow returns to the browser computer, not a remote server.</summary>
public static class ChatGptSignInLocation
{
    static readonly Uri Callback = new(ChatGptAuth.CallbackUri);

    public static bool IsAvailable(HttpContext context, bool inContainer)
    {
        if (inContainer || context.Connection.RemoteIpAddress is not { } peer || !IPAddress.IsLoopback(peer)
            || context.Connection.LocalPort != Callback.Port || context.Request.Scheme != Callback.Scheme
            || !LocalHost(context.Request.Host.Host) || context.Request.Host.Port != Callback.Port)
            return false;

        // A native reverse proxy may also connect over loopback; a remote browser's
        // Origin must not start a transaction whose callback it cannot deliver.
        var origin = context.Request.Headers.Origin.ToString();
        return origin.Length == 0 || (Uri.TryCreate(origin, UriKind.Absolute, out var uri)
            && uri.Scheme == Callback.Scheme && LocalHost(uri.Host) && uri.Port == Callback.Port);
    }

    static bool LocalHost(string host) => host is "127.0.0.1" or "localhost" or "[::1]" or "::1";
}
