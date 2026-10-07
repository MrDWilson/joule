using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;

namespace Joule;

/// <summary>What is sent: a short title, a few lines of text and, when App__PublicUrl is set, a link back into Joule.</summary>
public sealed record PushMessage(string Title, string Body, string? Url, string Event, bool Urgent = false);
/// <summary>How a send went. Retry is true for problems that may pass (no connection, a timeout, rate limiting, a server error).</summary>
public sealed record PushResult(bool Ok, string? Error, bool Retry)
{
    public static readonly PushResult Sent = new(true, null, false);
}
/// <summary>Home Assistant's address and token, from the Sensors settings.</summary>
public sealed record HomeAssistantTarget(Uri BaseUri, string Token);

/// <summary>
/// Builds and sends one message for each kind of channel. Requests are plain HTTP through the "notify" HttpClient; no channel needs
/// a library. Error text never includes a token or a webhook address (Telegram's token is part of its URL).
/// </summary>
public static class PushChannels
{
    public static readonly Uri NtfyDefault = new("https://ntfy.sh/");
    public static readonly Uri PushoverApi = new("https://api.pushover.net/1/messages.json");
    public static readonly Uri TelegramApi = new("https://api.telegram.org/");

    /// <summary>What is missing before this channel can send, in plain words; null when it's ready.</summary>
    public static string? Problem(PushChannelSettings channel, HomeAssistantTarget? homeAssistant)
    {
        var missing = channel.Info.Fields.Where(f => f.Required && channel.Value(f.Key) is null).Select(f => Named(f.Label)).ToList();
        if (missing.Count > 0) return $"Add the {string.Join(" and ", missing)}.";
        if (channel.Info.Id == "HomeAssistant" && homeAssistant is null) return "Joule needs Home Assistant's address and token first (Setup › Sensors).";
        return null;
    }

    /// <summary>The HTTP request for <paramref name="message"/> on this channel. Throws DomainException when the channel isn't set up.</summary>
    public static HttpRequestMessage Build(PushChannelSettings channel, PushMessage message, HomeAssistantTarget? homeAssistant)
    {
        if (Problem(channel, homeAssistant) is { } problem) throw new DomainException($"{channel.Info.Name} isn't set up: {problem}", 400);
        string V(string name) => channel.Value($"{channel.Info.Prefix}:{name}")!;
        string? Optional(string name) => channel.Value($"{channel.Info.Prefix}:{name}");
        var title = Cut(message.Title, 120);
        var body = Cut(message.Body, 900);
        HttpRequestMessage request;
        switch (channel.Info.Id)
        {
            case "Ntfy":
            {
                // JSON publishing (to the server's root) keeps non-ASCII text like £ intact, which headers can't.
                var root = Optional("Url") is { } url ? new Uri(url.TrimEnd('/') + "/") : NtfyDefault;
                request = new(HttpMethod.Post, root)
                {
                    Content = JsonContent.Create(new Dictionary<string, object?>
                    {
                        ["topic"] = V("Topic"), ["title"] = title, ["message"] = body, ["priority"] = message.Urgent ? 4 : 3,
                        ["tags"] = new[] { message.Event == NotificationInbox.Offline || message.Urgent ? "warning" : "zap" },
                        ["click"] = message.Url,
                    }.Where(x => x.Value is not null).ToDictionary(x => x.Key, x => x.Value))
                };
                if (Optional("Token") is { } token) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                break;
            }
            case "Pushover":
            {
                var form = new Dictionary<string, string> { ["token"] = V("AppToken"), ["user"] = V("UserKey"), ["title"] = title, ["message"] = body, ["priority"] = "0" };
                if (message.Url is { } link) { form["url"] = link; form["url_title"] = "Open in Joule"; }
                request = new(HttpMethod.Post, PushoverApi) { Content = new FormUrlEncodedContent(form) };
                break;
            }
            case "HomeAssistant":
            {
                var service = V("Service");
                if (service.StartsWith("notify.", StringComparison.Ordinal)) service = service["notify.".Length..];
                var data = message.Url is { } link ? new { url = link, clickAction = link } : null;
                request = new(HttpMethod.Post, new Uri(homeAssistant!.BaseUri, $"api/services/notify/{service}"))
                {
                    Content = JsonContent.Create(data is null ? (object)new { title, message = body } : new { title, message = body, data })
                };
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", homeAssistant.Token);
                break;
            }
            case "Telegram":
            {
                var text = $"{title}\n{body}" + (message.Url is { } link ? $"\n{link}" : "");
                request = new(HttpMethod.Post, new Uri($"{TelegramApi.AbsoluteUri}bot{V("BotToken")}/sendMessage"))
                {
                    Content = JsonContent.Create(new { chat_id = V("ChatId"), text, disable_web_page_preview = true })
                };
                break;
            }
            case "Chat":
            {
                var url = new Uri(V("WebhookUrl"));
                var discord = url.Host is "discord.com" or "discordapp.com" || url.Host.EndsWith(".discord.com", StringComparison.Ordinal) || url.Host.EndsWith(".discordapp.com", StringComparison.Ordinal);
                var linkLine = message.Url is { } link ? $"\n{link}" : "";
                request = new(HttpMethod.Post, url)
                {
                    Content = discord
                        ? JsonContent.Create(new { username = "Joule", content = Cut($"**{title}**\n{body}{linkLine}", 1900) })
                        : JsonContent.Create(new { text = $"*{title}*\n{body}{linkLine}" })
                };
                break;
            }
            case "Webhook":
            {
                request = new(HttpMethod.Post, new Uri(V("Url")))
                {
                    Content = JsonContent.Create(new { source = "joule", @event = message.Event, title, message = body, url = message.Url, urgent = message.Urgent, at = DateTimeOffset.UtcNow })
                };
                if (Optional("Token") is { } token) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                break;
            }
            default: throw new DomainException("Unknown notification channel.", 404);
        }
        return request;
    }

    /// <summary>Sends one request and says how it went, in words safe to show and store.</summary>
    public static async Task<PushResult> SendAsync(HttpClient http, HttpRequestMessage request, string channelName, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            using var response = await http.SendAsync(request, deadline.Token);
            if (response.IsSuccessStatusCode)
            {
                // Telegram answers 200 with ok:false for some problems.
                if (channelName == "Telegram" && await ReadError(response, deadline.Token, okField: true) is { } telegram) return new(false, $"Telegram said: {telegram}", false);
                return PushResult.Sent;
            }
            var code = (int)response.StatusCode;
            var said = await ReadError(response, deadline.Token);
            var retry = code is 408 or 429 || code >= 500;
            var reason = response.StatusCode switch
            {
                HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "it refused the token or key",
                HttpStatusCode.NotFound => "the address, topic or service wasn't found",
                HttpStatusCode.TooManyRequests => "it asked Joule to slow down",
                _ when (int)response.StatusCode is >= 300 and < 400 => "it redirected somewhere else",
                _ => $"it answered HTTP {code}",
            };
            return new(false, $"{channelName} didn't accept it: {reason}" + (said is null ? "." : $" ({said})."), retry);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return new(false, $"{channelName} didn't answer within 20 seconds.", true); }
        catch (HttpRequestException e)
        {
            var socket = e.InnerException as SocketException ?? e.InnerException?.InnerException as SocketException;
            var why = socket?.SocketErrorCode is SocketError.HostNotFound or SocketError.NoData || e.HttpRequestError == HttpRequestError.NameResolutionError ? "Joule can't find that server name"
                : e.HttpRequestError == HttpRequestError.SecureConnectionError ? "the secure (HTTPS) connection failed"
                : "Joule couldn't connect";
            return new(false, $"{channelName}: {why}.", true);
        }
        catch (IOException) { return new(false, $"{channelName}: the connection dropped.", true); }
    }

    /// <summary>The service's own short error text, when its body says one (Telegram description, Pushover errors, ntfy/HA message).</summary>
    static async Task<string?> ReadError(HttpResponseMessage response, CancellationToken ct, bool okField = false)
    {
        try
        {
            if (response.Content.Headers.ContentLength > 64 * 1024) return null;
            var text = await response.Content.ReadAsStringAsync(ct);
            if (string.IsNullOrWhiteSpace(text) || text.Length > 64 * 1024) return null;
            using var json = JsonDocument.Parse(text);
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (okField) return root.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.False ? Field(root, "description") ?? "it refused the message" : null;
            if (root.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array && errors.GetArrayLength() > 0 && errors[0].ValueKind == JsonValueKind.String)
                return Cut(errors[0].GetString()!, 160);
            return Field(root, "description") ?? Field(root, "error") ?? Field(root, "message");
        }
        catch (Exception e) when (e is JsonException or IOException or HttpRequestException or InvalidOperationException) { return null; }
    }
    static string? Field(JsonElement root, string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString()) ? Cut(v.GetString()!, 160) : null;

    /// <summary>Home Assistant's notify services ("notify.mobile_app_pixel_8"), sorted, for Setup's picker.</summary>
    public static async Task<List<string>> HomeAssistantServicesAsync(HttpClient http, HomeAssistantTarget target, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(target.BaseUri, "api/services"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", target.Token);
        using var response = await http.SendAsync(request, deadline.Token);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) throw new DomainException("Home Assistant refused Joule's token.", 502);
        if (!response.IsSuccessStatusCode) throw new DomainException($"Home Assistant answered HTTP {(int)response.StatusCode}.", 502);
        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(deadline.Token), cancellationToken: deadline.Token);
        var result = new List<string>();
        if (json.RootElement.ValueKind != JsonValueKind.Array) return result;
        foreach (var domain in json.RootElement.EnumerateArray())
            if (domain.ValueKind == JsonValueKind.Object && domain.TryGetProperty("domain", out var name) && name.GetString() == "notify"
                && domain.TryGetProperty("services", out var services) && services.ValueKind == JsonValueKind.Object)
                result.AddRange(services.EnumerateObject().Select(s => "notify." + s.Name).Where(s => s.Length <= 107));
        // The phone apps first: they're what most people want.
        return result.OrderBy(s => s.StartsWith("notify.mobile_app_", StringComparison.Ordinal) ? 0 : 1).ThenBy(s => s, StringComparer.Ordinal).ToList();
    }

    /// <summary>"Your user key" → "user key", "Application API token" → "application API token", for "Add the …".</summary>
    static string Named(string label)
    {
        var name = label.StartsWith("Your ", StringComparison.Ordinal) ? label[5..] : label;
        return name.Length > 1 && char.IsUpper(name[0]) && !char.IsUpper(name[1]) ? char.ToLowerInvariant(name[0]) + name[1..] : name;
    }

    static string Cut(string text, int max) => text.Length <= max ? text : text[..(max - 1)].TrimEnd() + "…";
}
