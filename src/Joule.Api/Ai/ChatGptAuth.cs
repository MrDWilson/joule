using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Joule;

/// <summary>Public-client Sign in with ChatGPT. Credentials are never part of application state.</summary>
public sealed class ChatGptAuth
{
    public const string CallbackUri = "http://127.0.0.1:5080/auth/callback";
    const string Issuer = "https://auth.openai.com";
    const string Resource = "https://api.openai.com/v1";
    const string DynamicClient = "dynamic_agent_client";
    const string PlanScope = "chatgpt.tokens.use.direct";
    readonly HttpClient http;
    readonly string directory;
    readonly SemaphoreSlim gate = new(1, 1);
    Credential? active;
    Registration? registration;
    public bool? RemoteRevocationConfirmed { get; private set; }
    Pending? pending;
    string? hostId;
    static readonly JsonSerializerOptions StorageJson = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
    public bool Connected => active is not null && active.Scopes.Contains(PlanScope);
    public string? Email => active?.Email;

    public ChatGptAuth(HttpClient http, string dataDirectory)
    {
        this.http = http; directory = Path.GetFullPath(dataDirectory);
        var registrationPath = Path.Combine(directory, "chatgpt-registration.json");
        if (File.Exists(registrationPath)) { SecureFile(registrationPath); registration = JsonSerializer.Deserialize<Registration>(File.ReadAllText(registrationPath), StorageJson); }
        var path = Path.Combine(directory, "chatgpt-credentials.json");
        if (File.Exists(path))
        {
            SecureFile(path);
            active = JsonSerializer.Deserialize<Credential>(File.ReadAllText(path), StorageJson);
            if (active is not null && (active.Issuer != Issuer || string.IsNullOrEmpty(active.Subject) || active.ClientId == DynamicClient || string.IsNullOrEmpty(active.AccessToken)))
                throw new DomainException("Stored ChatGPT credentials are invalid. Disconnect and sign in again.", 400);
        }
        // Create this runtime's separate host ID even on a VM before credentials are imported.
        if (!OperatingSystem.IsWindows()) EnsureHostAsync(CancellationToken.None).GetAwaiter().GetResult();
    }

    public async Task<string> StartAsync(CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            await EnsureHostAsync(ct);
            var state = Random(); var nonce = Random(); var verifier = Random(64);
            var client = active?.ClientId ?? registration?.ClientId ?? DynamicClient;
            pending = new(state, nonce, verifier, client, active?.Subject ?? registration?.Subject, DateTimeOffset.UtcNow.AddMinutes(10));
            var values = new Dictionary<string, string> {
                ["client_id"] = client, ["ext_agent_host_id"] = hostId!, ["response_type"] = "code", ["redirect_uri"] = CallbackUri,
                ["scope"] = "openid profile email offline_access resource.invoke chatgpt.tokens.use.direct", ["resource"] = Resource,
                ["state"] = state, ["nonce"] = nonce, ["code_challenge_method"] = "S256", ["code_challenge"] = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)))
            };
            if (client == DynamicClient) values["agent_name_hint"] = "Joule";
            else
            {
                if (!string.IsNullOrEmpty(active?.IdToken)) values["id_token_hint"] = active!.IdToken;
                var email = active?.Email ?? registration?.Email;
                if (!string.IsNullOrEmpty(email)) values["login_hint"] = email;
            }
            return Issuer + "/api/accounts/authorize?" + string.Join("&", values.Select(p => Uri.EscapeDataString(p.Key) + "=" + Uri.EscapeDataString(p.Value)));
        }
        finally { gate.Release(); }
    }

    public async Task CompleteAsync(string code, string state, string? clientId = null, string? error = null, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            var attempt = pending;
            if (attempt is null || attempt.ExpiresAt <= DateTimeOffset.UtcNow || !ConstantEquals(attempt.State, state))
                throw new DomainException("Invalid or expired ChatGPT sign-in state. Start sign-in again.", 400);
            pending = null; // Consume valid attempts once, including denials.
            if (error is not null) throw new DomainException("ChatGPT authorization was denied or unavailable. Existing credentials were retained.", 400);
            if (string.IsNullOrWhiteSpace(code)) throw new DomainException("ChatGPT authorization code is missing.", 400);
            var issued = attempt.ClientId == DynamicClient ? clientId : attempt.ClientId;
            if (string.IsNullOrWhiteSpace(issued) || issued == DynamicClient || (attempt.ClientId != DynamicClient && clientId is not null && clientId != issued))
                throw new DomainException("ChatGPT registration client ID is missing or mismatched.", 400);
            using var tokens = await ExchangeAsync(new() { ["grant_type"] = "authorization_code", ["client_id"] = issued, ["code"] = code, ["code_verifier"] = attempt.Verifier, ["redirect_uri"] = CallbackUri, ["resource"] = Resource }, cancellationToken);
            var idToken = Required(tokens.RootElement, "id_token");
            var identity = await ValidateIdentityAsync(idToken, issued, attempt.Nonce, cancellationToken);
            if (attempt.Subject is not null && identity.Subject != attempt.Subject) throw new DomainException("ChatGPT sign-in returned a different account. Existing credentials were retained.", 400);
            var credential = ReadTokens(tokens.RootElement, issued, identity.Subject, identity.Email, idToken, null);
            await SaveAsync(credential, cancellationToken);
            active = credential;
            registration = new(credential.ClientId, credential.Subject, credential.Email);
            RemoteRevocationConfirmed = null;
        }
        finally { gate.Release(); }
    }

    public async Task<string> AccessTokenAsync(CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            var old = active ?? throw NotConnected();
            if (!old.Scopes.Contains(PlanScope)) throw SignIn("ChatGPT plan usage was not authorized. Continue with ChatGPT again.", "plan_scope_missing");
            if (old.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(1)) return old.AccessToken;
            return await RefreshLockedAsync(old, ct);
        }
        finally { gate.Release(); }
    }

    /// <summary>
    /// Renews the access token after the provider rejected it (HTTP 401) even though it had not expired, e.g. when it was
    /// rotated or revoked server-side. If another caller already renewed it, the newer token is returned without a second refresh.
    /// </summary>
    public async Task<string> ForceRefreshAsync(string rejectedAccessToken, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            var old = active ?? throw NotConnected();
            if (!ConstantEquals(old.AccessToken, rejectedAccessToken)) return old.AccessToken;
            return await RefreshLockedAsync(old, ct);
        }
        finally { gate.Release(); }
    }

    // The caller holds the gate, so refreshes of the rotating refresh token are serialised.
    async Task<string> RefreshLockedAsync(Credential old, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(old.RefreshToken)) throw SignIn("ChatGPT sign-in has expired. Reconnect ChatGPT in Setup.", "no_refresh_token");
        using var tokens = await ExchangeAsync(new() { ["grant_type"] = "refresh_token", ["client_id"] = old.ClientId, ["refresh_token"] = old.RefreshToken, ["resource"] = Resource }, ct, refresh: true);
        var root = tokens.RootElement;
        var idToken = root.TryGetProperty("id_token", out var t) ? t.GetString() : null;
        if (!string.IsNullOrEmpty(idToken))
        {
            var identity = await ValidateIdentityAsync(idToken, old.ClientId, null, ct);
            if (identity.Subject != old.Subject) throw new DomainException("Refreshed ChatGPT identity did not match. Sign in again.", 400);
        }
        var fresh = ReadTokens(root, old.ClientId, old.Subject, old.Email, idToken ?? old.IdToken, old);
        await SaveAsync(fresh, ct); active = fresh;
        return fresh.AccessToken;
    }

    public async Task<List<ChatGptModel>> ModelsAsync(CancellationToken ct)
    {
        var token = await AccessTokenAsync(ct);
        async Task<HttpResponseMessage> Get(string bearer)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, Resource + "/models");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
            return await http.SendAsync(request, ct);
        }
        var first = await Get(token);
        if (first.StatusCode == System.Net.HttpStatusCode.Unauthorized) { first.Dispose(); first = await Get(await ForceRefreshAsync(token, ct)); }
        using var response = first;
        if (!response.IsSuccessStatusCode) throw new DomainException($"ChatGPT model catalog unavailable (HTTP {(int)response.StatusCode}).", 502);
        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        return doc.RootElement.GetProperty("models").EnumerateArray().Where(m => m.TryGetProperty("visibility", out var v) && v.GetString() == "list")
            .Select(m => new ChatGptModel(Required(m,"slug"), Required(m,"display_name"))).ToList();
    }

    public async Task DisconnectAsync(CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            var old = active;
            RemoteRevocationConfirmed = old is null;
            if (old is not null)
            {
                registration = new(old.ClientId, old.Subject, old.Email);
                await AtomicWriteAsync(Path.Combine(directory,"chatgpt-registration.json"), registration, ct);
                if (!string.IsNullOrEmpty(old.RefreshToken))
                {
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    deadline.CancelAfter(TimeSpan.FromSeconds(15));
                    try
                    {
                        using var discovery = await http.GetAsync(Issuer + "/.well-known/openid-configuration",deadline.Token);
                        discovery.EnsureSuccessStatusCode();
                        using var doc = await JsonDocument.ParseAsync(await discovery.Content.ReadAsStreamAsync(deadline.Token),cancellationToken:deadline.Token);
                        if (!Uri.TryCreate(Required(doc.RootElement,"revocation_endpoint"), UriKind.Absolute, out var endpoint) || endpoint.Scheme != "https" || endpoint.Host != "auth.openai.com") throw new CryptographicException();
                        using var request = new HttpRequestMessage(HttpMethod.Post,endpoint) { Content = new FormUrlEncodedContent(new Dictionary<string,string> { ["token"] = old.RefreshToken, ["token_type_hint"] = "refresh_token", ["client_id"] = old.ClientId }) };
                        using var response = await http.SendAsync(request,deadline.Token);
                        RemoteRevocationConfirmed = response.StatusCode == System.Net.HttpStatusCode.OK;
                    }
                    catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException or CryptographicException or KeyNotFoundException or InvalidOperationException) { RemoteRevocationConfirmed = false; }
                }
            }
            File.Delete(Path.Combine(directory,"chatgpt-credentials.json")); active = null; pending = null;
        }
        finally { gate.Release(); }
    }

    // Refresh failures that mean the token set can never work again; OpenAI's guidance is to clear it and sign in again.
    static readonly HashSet<string> TerminalRefreshCodes = new(StringComparer.Ordinal) { "invalid_grant", "invalid_refresh_token", "token_expired", "refresh_token_expired", "refresh_token_invalidated", "refresh_token_reused" };

    async Task<JsonDocument> ExchangeAsync(Dictionary<string,string> values, CancellationToken ct, bool refresh = false)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Issuer + "/api/accounts/oauth/token") { Content = new FormUrlEncodedContent(values) };
        HttpResponseMessage sent;
        try { sent = await http.SendAsync(request, ct); }
        catch (HttpRequestException e) when (refresh) { throw Unavailable($"network {e.HttpRequestError}"); }
        catch (OperationCanceledException) when (refresh && !ct.IsCancellationRequested) { throw Unavailable("timeout"); }
        using var response = sent;
        var status = (int)response.StatusCode;
        if (!response.IsSuccessStatusCode)
        {
            var code = await OAuthErrorCodeAsync(response, ct);
            var temporary = status is 408 or 429 or >= 500;
            if (!refresh)
                throw new DomainException(temporary
                    ? $"OpenAI's sign-in service is temporarily unavailable (HTTP {status}). Try signing in again in a minute; existing credentials were retained."
                    : $"ChatGPT token exchange failed (HTTP {status}{(code is null ? "" : ", " + code)}). Start sign-in again; existing credentials were retained.", 502);
            // A temporary OpenAI auth problem must not send the homeowner to reconnect, and must not erase credentials.
            if (temporary) throw Unavailable($"http={status} code={code ?? "none"}");
            if (code is not null && TerminalRefreshCodes.Contains(code))
            {
                await ClearUnusableCredentialsAsync(ct);
                throw SignIn($"ChatGPT sign-in has expired or was revoked ({code}). Reconnect ChatGPT in Setup.", code, status);
            }
            throw SignIn($"ChatGPT didn't accept Joule's sign-in renewal ({code ?? $"HTTP {status}"}). Reconnect ChatGPT in Setup.", code ?? "refresh_rejected", status);
        }
        return await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
    }

    /// <summary>OAuth errors are {"error":"invalid_grant"}; some OpenAI errors are {"error":{"code":"…"}}. Only plain identifiers are kept.</summary>
    static async Task<string?> OAuthErrorCodeAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            if (body.Length > 16_384) body = body[..16_384];
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("error", out var error)) return null;
            var code = error.ValueKind == JsonValueKind.String ? error.GetString()
                : error.ValueKind == JsonValueKind.Object && error.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
            return code is { Length: > 0 and <= 64 } && code.All(ch => char.IsAsciiLetterLower(ch) || char.IsAsciiDigit(ch) || ch is '_' or '.') ? code : null;
        }
        catch (Exception e) when (e is JsonException or IOException or HttpRequestException or InvalidOperationException) { return null; }
    }

    // Keeps the registration (issued client ID) so reconnecting reuses it; only the unusable tokens go.
    async Task ClearUnusableCredentialsAsync(CancellationToken ct)
    {
        var old = active;
        if (old is null) return;
        registration = new(old.ClientId, old.Subject, old.Email);
        await AtomicWriteAsync(Path.Combine(directory, "chatgpt-registration.json"), registration, ct);
        File.Delete(Path.Combine(directory, "chatgpt-credentials.json")); active = null;
    }

    static ProviderModelException NotConnected() => SignIn("Continue with ChatGPT before running subscription analysis.", "not_connected");
    static ProviderModelException SignIn(string message, string code, int? http = null) =>
        new(message, $"sign-in code={code} http={http?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "none"}", new() { Kind = ModelFailureKind.SignIn, Code = code, HttpStatus = http }, 400);
    static TransientModelException Unavailable(string detail, bool renewing = true) =>
        new(renewing ? "OpenAI's sign-in service is temporarily unavailable, so Joule couldn't renew the ChatGPT sign-in. It will try again."
                : "OpenAI's sign-in service is temporarily unavailable. Try signing in again in a minute; existing credentials were retained.", $"sign-in {(renewing ? "renewal" : "check")} {detail}",
            new() { Kind = ModelFailureKind.Transient, Code = "sign_in_unavailable" });

    async Task<(string Subject, string? Email)> ValidateIdentityAsync(string jwt, string clientId, string? nonce, CancellationToken ct)
    {
        try
        {
            var parts = jwt.Split('.');
            if (parts.Length != 3) throw new CryptographicException();
            using var headerDoc = JsonDocument.Parse(Decode(parts[0]));
            using var claimsDoc = JsonDocument.Parse(Decode(parts[1]));
            var header = headerDoc.RootElement; var claims = claimsDoc.RootElement;
            var algorithm = Required(header, "alg"); var kid = Required(header, "kid");
            // Endpoints are pinned to OpenAI. Never follow a token-supplied URL.
            HttpResponseMessage fetched;
            try { fetched = await http.GetAsync(Issuer + "/.well-known/jwks.json", ct); }
            catch (HttpRequestException e) { throw Unavailable($"signing keys network {e.HttpRequestError}", nonce is null); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw Unavailable("signing keys timeout", nonce is null); }
            using var response = fetched;
            if (!response.IsSuccessStatusCode) throw Unavailable($"signing keys http={(int)response.StatusCode}", nonce is null);
            using var keys = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            var key = keys.RootElement.GetProperty("keys").EnumerateArray().First(k => Required(k,"kid") == kid);
            if (key.TryGetProperty("use", out var use) && use.GetString() != "sig") throw new CryptographicException();
            if (key.TryGetProperty("alg", out var keyAlg) && keyAlg.GetString() != algorithm) throw new CryptographicException();
            var signed = Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]); var signature = Decode(parts[2]);
            bool valid;
            if (algorithm == "RS256" && Required(key,"kty") == "RSA")
            {
                using var rsa = RSA.Create(); rsa.ImportParameters(new RSAParameters { Modulus = Decode(Required(key,"n")), Exponent = Decode(Required(key,"e")) });
                valid = rsa.VerifyData(signed, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            }
            else if (algorithm == "ES256" && Required(key,"kty") == "EC" && Required(key,"crv") == "P-256")
            {
                using var ec = ECDsa.Create(new ECParameters { Curve = ECCurve.NamedCurves.nistP256, Q = new ECPoint { X = Decode(Required(key,"x")), Y = Decode(Required(key,"y")) } });
                valid = ec.VerifyData(signed, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
            }
            else throw new CryptographicException();
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var audience = claims.GetProperty("aud");
            var audienceValid = audience.ValueKind == JsonValueKind.String ? audience.GetString() == clientId : audience.ValueKind == JsonValueKind.Array && audience.EnumerateArray().Any(a => a.GetString() == clientId);
            if (!valid || Required(claims,"iss") != Issuer || !audienceValid || claims.GetProperty("exp").GetInt64() <= now - 5 || claims.GetProperty("iat").GetInt64() > now + 5 || (claims.TryGetProperty("nbf", out var nbf) && nbf.GetInt64() > now + 5)) throw new CryptographicException();
            if (audience.ValueKind == JsonValueKind.Array && audience.GetArrayLength() > 1 && Required(claims,"azp") != clientId) throw new CryptographicException();
            if (nonce is not null && !ConstantEquals(Required(claims,"nonce"), nonce)) throw new CryptographicException();
            return (Required(claims,"sub"), claims.TryGetProperty("email", out var email) ? email.GetString() : null);
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        { throw new DomainException("ChatGPT identity token validation failed. Existing credentials were retained.", 400); }
    }

    static Credential ReadTokens(JsonElement root, string client, string subject, string? email, string idToken, Credential? previous)
    {
        var scopes = root.TryGetProperty("scope", out var scope) ? (scope.GetString() ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries) : previous?.Scopes ?? [];
        if (!scopes.Contains(PlanScope) || !scopes.Contains("resource.invoke")) throw new DomainException("ChatGPT plan usage was not granted. Existing credentials were retained.", 400);
        if (!Required(root,"token_type").Equals("Bearer", StringComparison.OrdinalIgnoreCase)) throw new DomainException("Unsupported ChatGPT token type.", 400);
        var expires = root.GetProperty("expires_in").GetInt64();
        if (expires <= 0) throw new DomainException("ChatGPT access token was already expired.", 400);
        return new() { Issuer = Issuer, ClientId = client, Subject = subject, Email = email, IdToken = idToken, AccessToken = Required(root,"access_token"), RefreshToken = root.TryGetProperty("refresh_token", out var refresh) ? refresh.GetString() ?? "" : previous?.RefreshToken ?? "", Scopes = scopes, ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(expires) };
    }

    async Task EnsureHostAsync(CancellationToken ct)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory,"chatgpt-host.json");
        if (hostId is not null) return;
        if (File.Exists(path)) { SecureFile(path); hostId = JsonSerializer.Deserialize<Host>(await File.ReadAllTextAsync(path,ct), StorageJson)?.ExtAgentHostId; }
        if (string.IsNullOrWhiteSpace(hostId)) { hostId = "urn:uuid:" + Guid.NewGuid(); await AtomicWriteAsync(path, new Host(hostId), ct); }
    }
    Task SaveAsync(Credential credential, CancellationToken ct) => AtomicWriteAsync(Path.Combine(directory,"chatgpt-credentials.json"), credential, ct);
    async Task AtomicWriteAsync<T>(string path, T value, CancellationToken ct)
    {
        Directory.CreateDirectory(directory);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            else throw new PlatformNotSupportedException("ChatGPT credential storage currently requires Unix owner-only file permissions.");
            await using (var file = new FileStream(temporary, options))
            { await JsonSerializer.SerializeAsync(file, value, StorageJson, ct); await file.FlushAsync(ct); file.Flush(true); }
            File.Move(temporary,path,true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    static void SecureFile(string path)
    {
        if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("ChatGPT credential storage requires Unix owner-only file permissions.");
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new DomainException("ChatGPT credential files must not be symbolic links.", 400);
        File.SetUnixFileMode(path,UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
    static string Required(JsonElement element, string name) => element.GetProperty(name).GetString() is { Length: > 0 } value ? value : throw new JsonException();
    static string Random(int bytes = 32) => Base64Url(RandomNumberGenerator.GetBytes(bytes));
    static string Base64Url(byte[] value) => Convert.ToBase64String(value).TrimEnd('=').Replace('+','-').Replace('/','_');
    static byte[] Decode(string value) { var s = value.Replace('-','+').Replace('_','/'); return Convert.FromBase64String(s.PadRight((s.Length + 3) / 4 * 4,'=')); }
    static bool ConstantEquals(string a, string b) => CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));
    sealed record Pending(string State, string Nonce, string Verifier, string ClientId, string? Subject, DateTimeOffset ExpiresAt);
    sealed record Host(string ExtAgentHostId);
    sealed record Registration(string ClientId, string Subject, string? Email);
    sealed class Credential
    {
        public string Issuer { get; set; } = "";
        public string ClientId { get; set; } = "";
        public string Subject { get; set; } = "";
        public string? Email { get; set; }
        public string IdToken { get; set; } = "";
        public string AccessToken { get; set; } = "";
        public string RefreshToken { get; set; } = "";
        public string[] Scopes { get; set; } = [];
        public DateTimeOffset ExpiresAt { get; set; }
    }
}
public record ChatGptModel(string Id, string Name);
