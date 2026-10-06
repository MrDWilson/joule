using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.Configuration;
using Joule;
using Xunit;

sealed class ManualClock(DateTimeOffset start) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = start;
    public override DateTimeOffset GetUtcNow() => Now;
}

/// <summary>Remembered sign-in cookie, wrong-key backoff and access-key validation.</summary>
[Collection(ChildProcessCollection.Name)]
public class SessionAuthTests
{
    const string Key = "session-test-key-0123456789";
    static readonly DateTimeOffset T0 = new(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);
    static AppAuthOptions Options(string? key = Key, double days = 30) => new(false, false, key, TimeSpan.FromDays(days), "'none'");

    [Fact]
    public void SessionCookieRoundTripsAndRejectsTamperingExpiryAndKeyRotation()
    {
        var clock = new ManualClock(T0);
        var sessions = new SessionCookies(Options(), clock);
        var value = sessions.Issue();
        Assert.Equal(T0.AddDays(30).ToUnixTimeSeconds(), sessions.Validate(value)!.Value.ToUnixTimeSeconds());
        Assert.Null(sessions.Validate(value[..^1] + (value[^1] == 'A' ? 'B' : 'A')));
        Assert.Null(sessions.Validate((T0.AddDays(300).ToUnixTimeSeconds()) + value[value.IndexOf('.')..]));
        Assert.Null(sessions.Validate("not-a-cookie"));
        Assert.Null(sessions.Validate(null));
        clock.Now = T0.AddDays(31);
        Assert.Null(sessions.Validate(value));
        // A new access key invalidates every remembered sign-in.
        clock.Now = T0;
        Assert.Null(new SessionCookies(Options("another-key-0123456789"), clock).Validate(value));
        // A shortened lifetime applies to cookies already issued.
        Assert.Null(new SessionCookies(Options(days: 1), clock).Validate(value));
    }

    [Fact]
    public void SessionCookiesAreOffWithoutAKeyOrWithZeroDays()
    {
        Assert.False(new SessionCookies(Options(key: null)).Enabled);
        Assert.False(new SessionCookies(Options(days: 0)).Enabled);
        Assert.Null(new SessionCookies(Options(days: 0)).Validate(new SessionCookies(Options()).Issue()));
    }

    [Fact]
    public void ThrottleAllowsTyposThenBacksOffAndForgetsOnSuccess()
    {
        var clock = new ManualClock(T0);
        var throttle = new AccessKeyThrottle(clock);
        for (var i = 0; i < AccessKeyThrottle.FreeAttempts - 1; i++) { throttle.Failure("a"); Assert.Equal(0, throttle.RetryAfterSeconds("a")); }
        throttle.Failure("a");
        var first = throttle.RetryAfterSeconds("a");
        Assert.True(first > 0);
        throttle.Failure("a");
        Assert.True(throttle.RetryAfterSeconds("a") > first);
        Assert.Equal(0, throttle.RetryAfterSeconds("b"));
        for (var i = 0; i < 30; i++) throttle.Failure("a");
        Assert.InRange(throttle.RetryAfterSeconds("a"), 1, 300);
        clock.Now = clock.Now.AddMinutes(11);
        Assert.Equal(0, throttle.RetryAfterSeconds("a"));
        for (var i = 0; i < 6; i++) throttle.Failure("c");
        throttle.Success("c");
        Assert.Equal(0, throttle.RetryAfterSeconds("c"));
    }

    [Fact]
    public void ThrottleCountsOnlyDistinctWrongKeys()
    {
        var clock = new ManualClock(T0);
        var throttle = new AccessKeyThrottle(clock);
        var typo = throttle.Fingerprint("session-test-key-012345678");
        Assert.Equal(typo, throttle.Fingerprint("session-test-key-012345678"));
        Assert.NotEqual(typo, throttle.Fingerprint("session-test-key-0123456780"));
        Assert.DoesNotContain("session-test-key", typo);
        // A tab polling with the same typo is one mistake, however often it asks.
        Assert.False(throttle.AlreadyCounted("a", typo));
        for (var i = 0; i < 50; i++) throttle.Failure("a", typo);
        Assert.True(throttle.AlreadyCounted("a", typo));
        Assert.Equal(0, throttle.RetryAfterSeconds("a"));
        Assert.False(throttle.AlreadyCounted("b", typo));
        // New keys are new guesses.
        for (var i = 1; i < AccessKeyThrottle.FreeAttempts; i++) throttle.Failure("a", throttle.Fingerprint($"guess-{i}"));
        Assert.True(throttle.RetryAfterSeconds("a") > 0);
        // The window forgets everything, including which keys were seen.
        clock.Now = T0.AddMinutes(11);
        Assert.False(throttle.AlreadyCounted("a", typo));
        Assert.Equal(0, throttle.RetryAfterSeconds("a"));
        throttle.Failure("a", typo);
        Assert.Equal(0, throttle.RetryAfterSeconds("a"));
        // Past the remembered-key limit, new keys still count.
        for (var i = 0; i < AccessKeyThrottle.RememberedKeys + 10; i++) throttle.Failure("c", throttle.Fingerprint($"spray-{i}"));
        Assert.Equal(300, throttle.RetryAfterSeconds("c"));
    }

    [Fact]
    public void SecureCookieSettingIsValidated()
    {
        Assert.Null(AppAuthOptions.From(Config()).Options!.SecureCookie);
        Assert.Null(AppAuthOptions.From(Config(("App:SecureCookie", "auto"))).Options!.SecureCookie);
        Assert.True(AppAuthOptions.From(Config(("App:SecureCookie", "true"))).Options!.SecureCookie);
        Assert.False(AppAuthOptions.From(Config(("App:SecureCookie", "False"))).Options!.SecureCookie);
        Assert.Contains("App__SecureCookie", AppAuthOptions.From(Config(("App:SecureCookie", "yes"))).Error);
    }

    static IConfiguration Config(params (string Key, string? Value)[] values) => new ConfigurationBuilder().AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value))).Build();

    [Fact]
    public void AccessKeyOptionsAreValidatedWithReadableMessages()
    {
        Assert.Null(AppAuthOptions.From(Config(("App:Demo", "true"))).Error);
        Assert.Contains("App__AccessKey", AppAuthOptions.From(Config(("App:Demo", "false"))).Error);
        Assert.Contains("too short", AppAuthOptions.From(Config(("App:Demo", "false"), ("App:AccessKey", "fifteen-chars!!"))).Error);
        Assert.Null(AppAuthOptions.From(Config(("App:Demo", "false"), ("App:AccessKey", "sixteen-chars!!!"))).Error);
        Assert.Null(AppAuthOptions.From(Config(("App:Demo", "false"), ("App:AuthMode", "None"), ("App:AccessKey", "x"))).Error);
        Assert.Contains("App__AuthMode", AppAuthOptions.From(Config(("App:AuthMode", "Oauth"))).Error);
        Assert.Contains("App__SessionDays", AppAuthOptions.From(Config(("App:SessionDays", "-1"))).Error);
        Assert.Contains("App__FrameAncestors", AppAuthOptions.From(Config(("App:FrameAncestors", "javascript:alert(1)"))).Error);
        Assert.Equal("'self' https://ha.example.test", AppAuthOptions.From(Config(("App:FrameAncestors", "'self' https://ha.example.test"))).Options!.FrameAncestors);
    }

    static Dictionary<string, string?> Live => new() { ["App__Demo"] = "true", ["App__AuthMode"] = "AccessKey", ["App__AccessKey"] = Key };

    static string SessionCookie(HttpResponseMessage response)
    {
        var header = Assert.Single(response.Headers.GetValues("Set-Cookie"), c => c.StartsWith(SessionCookies.Name + "=", StringComparison.Ordinal));
        Assert.Contains("httponly", header, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", header, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("path=/", header, StringComparison.OrdinalIgnoreCase);
        return header.Split(';')[0];
    }

    [Fact]
    public async Task ACorrectKeyIsRememberedByAnHttpOnlyCookie()
    {
        await using var app = new JouleProcess(Live);
        Assert.True(await app.Start(), app.Log);
        using var signIn = new HttpRequestMessage(HttpMethod.Get, "api/state?view=header");
        signIn.Headers.Add("X-Access-Key", Key);
        using var signedIn = await app.Http.SendAsync(signIn);
        Assert.Equal(HttpStatusCode.OK, signedIn.StatusCode);
        var cookie = SessionCookie(signedIn);

        // A new tab has no key in sessionStorage, only the cookie.
        using var newTab = new HttpRequestMessage(HttpMethod.Get, "api/state");
        newTab.Headers.Add("Cookie", cookie);
        Assert.Equal(HttpStatusCode.OK, (await app.Http.SendAsync(newTab)).StatusCode);

        // A cookie alone cannot change anything without the dashboard header (a form post cannot add it).
        using var formPost = new HttpRequestMessage(HttpMethod.Post, "api/mode") { Content = JsonContent.Create(new { mode = "Monitor" }) };
        formPost.Headers.Add("Cookie", cookie);
        Assert.Equal(HttpStatusCode.Forbidden, (await app.Http.SendAsync(formPost)).StatusCode);
        using var dashboardPost = new HttpRequestMessage(HttpMethod.Post, "api/mode") { Content = JsonContent.Create(new { mode = "Monitor" }) };
        dashboardPost.Headers.Add("Cookie", cookie); dashboardPost.Headers.Add("X-Joule-Request", "1");
        Assert.Equal(HttpStatusCode.OK, (await app.Http.SendAsync(dashboardPost)).StatusCode);

        // Even with the header, a cross-site request carrying the cookie is rejected.
        using var crossSite = new HttpRequestMessage(HttpMethod.Post, "api/mode") { Content = JsonContent.Create(new { mode = "Recommend" }) };
        crossSite.Headers.Add("Cookie", cookie); crossSite.Headers.Add("X-Joule-Request", "1"); crossSite.Headers.Add("Sec-Fetch-Site", "cross-site");
        Assert.Equal(HttpStatusCode.Forbidden, (await app.Http.SendAsync(crossSite)).StatusCode);
        using var foreignOrigin = new HttpRequestMessage(HttpMethod.Post, "api/mode") { Content = JsonContent.Create(new { mode = "Recommend" }) };
        foreignOrigin.Headers.Add("Cookie", cookie); foreignOrigin.Headers.Add("X-Joule-Request", "1"); foreignOrigin.Headers.Add("Origin", "https://evil.example.test");
        Assert.Equal(HttpStatusCode.Forbidden, (await app.Http.SendAsync(foreignOrigin)).StatusCode);
        Assert.Contains("\"mode\":\"Monitor\"", await (await Get(app, "api/state?view=header", cookie)).Content.ReadAsStringAsync());

        // Signing out clears the cookie.
        using var logout = new HttpRequestMessage(HttpMethod.Post, "api/session/logout");
        logout.Headers.Add("Cookie", cookie); logout.Headers.Add("X-Joule-Request", "1");
        using var loggedOut = await app.Http.SendAsync(logout);
        Assert.Equal(HttpStatusCode.OK, loggedOut.StatusCode);
        Assert.Contains(loggedOut.Headers.GetValues("Set-Cookie"), c => c.StartsWith(SessionCookies.Name + "=;", StringComparison.Ordinal) && c.Contains("expires=Thu, 01 Jan 1970", StringComparison.OrdinalIgnoreCase));
    }

    static async Task<HttpResponseMessage> Get(JouleProcess app, string path, string cookie)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add("Cookie", cookie);
        return await app.Http.SendAsync(request);
    }

    [Fact]
    public async Task ForgedOrStaleCookiesAreRefusedAndCleared()
    {
        await using var app = new JouleProcess(Live);
        Assert.True(await app.Start(), app.Log);
        using var forged = await Get(app, "api/state", $"{SessionCookies.Name}={DateTimeOffset.UtcNow.AddDays(1).ToUnixTimeSeconds()}.forged");
        Assert.Equal(HttpStatusCode.Unauthorized, forged.StatusCode);
        Assert.Contains("key_required", await forged.Content.ReadAsStringAsync());
        Assert.Contains(forged.Headers.GetValues("Set-Cookie"), c => c.StartsWith(SessionCookies.Name + "=;", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RepeatedWrongKeysAreSlowedDownWithRetryAfter()
    {
        await using var app = new JouleProcess(Live);
        Assert.True(await app.Start(), app.Log);
        async Task<HttpResponseMessage> Try(string key)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "api/state");
            request.Headers.Add("X-Access-Key", key);
            return await app.Http.SendAsync(request);
        }
        for (var i = 0; i < AccessKeyThrottle.FreeAttempts; i++)
        {
            using var wrong = await Try($"wrong-key-0123456789-{i}");
            Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
            var body = await wrong.Content.ReadAsStringAsync();
            Assert.Contains("didn't work", body);
            Assert.Contains("\"authMode\":\"AccessKey\"", body);
        }
        using var throttled = await Try("wrong-key-0123456789-new");
        Assert.Equal((HttpStatusCode)429, throttled.StatusCode);
        Assert.True(throttled.Headers.RetryAfter?.Delta > TimeSpan.Zero);
        // The correct key waits too, or guessing would only be slowed for wrong answers.
        Assert.Equal((HttpStatusCode)429, (await Try(Key)).StatusCode);
        // A key already counted is not a new guess: it gets the same 401 and does not extend the wait.
        using var repeat = await Try("wrong-key-0123456789-0");
        Assert.Equal(HttpStatusCode.Unauthorized, repeat.StatusCode);
        Assert.Contains("wrong_key", await repeat.Content.ReadAsStringAsync());
        // Requests without a key are not attempts: the sign-in screen still loads normally.
        Assert.Equal(HttpStatusCode.Unauthorized, (await app.Http.GetAsync("api/state")).StatusCode);
        await Task.Delay(throttled.Headers.RetryAfter!.Delta!.Value + TimeSpan.FromMilliseconds(300));
        using var right = await Try(Key);
        Assert.Equal(HttpStatusCode.OK, right.StatusCode);
    }

    [Fact]
    public async Task OneTypoPolledRepeatedlyNeverLocksOutTheCorrectKey()
    {
        // The sign-in screen keeps polling with whatever was typed; a single typo must not turn into a lockout.
        await using var app = new JouleProcess(Live);
        Assert.True(await app.Start(), app.Log);
        async Task<HttpResponseMessage> Try(string key)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "api/state?view=header");
            request.Headers.Add("X-Access-Key", key);
            return await app.Http.SendAsync(request);
        }
        for (var i = 0; i < 50; i++)
        {
            using var wrong = await Try("session-test-key-012345678");
            Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        }
        using var right = await Try(Key);
        Assert.Equal(HttpStatusCode.OK, right.StatusCode);
    }

    [Fact]
    public async Task SecureCookieCanBeForcedBehindATlsProxy()
    {
        await using var app = new JouleProcess(new(Live) { ["App__SecureCookie"] = "true" });
        Assert.True(await app.Start(), app.Log);
        using var request = new HttpRequestMessage(HttpMethod.Get, "api/state?view=header");
        request.Headers.Add("X-Access-Key", Key);
        using var response = await app.Http.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(response.Headers.GetValues("Set-Cookie"), c => c.StartsWith(SessionCookies.Name + "=", StringComparison.Ordinal) && c.Contains("; secure", StringComparison.OrdinalIgnoreCase));
    }
}
