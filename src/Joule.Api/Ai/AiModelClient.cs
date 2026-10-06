using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Joule;

/// <summary>One model answer plus the diagnostics callers may record (cached tokens, request ids, retries, downgrades).</summary>
public record ModelReply(string Text, long InputTokens, long OutputTokens)
{
    /// <summary>Input tokens the provider served from its prompt cache (a subset of <see cref="InputTokens"/>).</summary>
    public long CachedInputTokens { get; init; }
    /// <summary>Output tokens spent on hidden reasoning (a subset of <see cref="OutputTokens"/>).</summary>
    public long ReasoningTokens { get; init; }
    /// <summary>The model that actually answered; differs from the requested model only when the fallback model was used.</summary>
    public string? Model { get; init; }
    /// <summary>The reasoning effort actually sent, or null when none was sent.</summary>
    public string? ReasoningEffort { get; init; }
    public string? RequestId { get; init; }
    public string? ResponseId { get; init; }
    /// <summary>How many provider calls this answer took, including retries.</summary>
    public int Attempts { get; init; } = 1;
    /// <summary>True when the model rejected the reasoning parameter and the call was sent without it.</summary>
    public bool ReasoningDropped { get; init; }
    public bool FallbackModelUsed { get; init; }
}

/// <summary>What kind of provider failure ended (or interrupted) a model call. Callers map this to plain copy and scheduling.</summary>
public enum ModelFailureKind
{
    /// <summary>Server error, overload, dropped connection or garbled stream. Retried with backoff.</summary>
    Transient,
    /// <summary>One provider call ran past <see cref="AiModelClient.CallTimeout"/>.</summary>
    Timeout,
    /// <summary>Short-term rate limit. Retried when the provider's wait is short; otherwise <see cref="ModelFailureInfo.ResetsAt"/> says when to try again.</summary>
    RateLimited,
    /// <summary>The ChatGPT plan (or this app's share of it) is used up. Never retried; pause until the reset.</summary>
    UsageLimit,
    /// <summary>The sign-in or API key was not accepted, even after a forced refresh. The user must reconnect.</summary>
    SignIn,
    /// <summary>The provider rejected this request (bad parameter, model unavailable, too long). Retrying the same body will not help.</summary>
    Rejected,
    /// <summary>The answer stopped early, usually at the output-token cap (<see cref="ModelFailureInfo.IncompleteReason"/>).</summary>
    Incomplete,
    /// <summary>The provider declined to answer (content filter).</summary>
    ContentFilter
}

/// <summary>Sanitised, credential-free facts about a provider failure. Safe to store on a failed investigation.</summary>
public sealed record ModelFailureInfo
{
    public ModelFailureKind Kind { get; init; }
    /// <summary>The provider's machine-readable code (always a plain identifier) or null.</summary>
    public string? Code { get; init; }
    public string? Param { get; init; }
    public int? HttpStatus { get; init; }
    /// <summary>The x-request-id response header, for OpenAI support.</summary>
    public string? RequestId { get; init; }
    /// <summary>The Responses API response id from response.created.</summary>
    public string? ResponseId { get; init; }
    /// <summary>The provider's own wait hint (Retry-After, x-ratelimit-reset-*, "try again in …").</summary>
    public TimeSpan? RetryAfter { get; init; }
    /// <summary>When the provider says the limit lifts, when it said so explicitly.</summary>
    public DateTimeOffset? ResetsAt { get; init; }
    /// <summary>response.incomplete_details.reason, e.g. max_output_tokens or content_filter.</summary>
    public string? IncompleteReason { get; init; }
    public int Attempts { get; init; } = 1;
    /// <summary>Wall time from the first attempt to giving up.</summary>
    public TimeSpan Elapsed { get; init; }
    public string? Model { get; init; }
    public int? Turn { get; init; }
    /// <summary>A short reference for support: request and response ids, when known.</summary>
    public string? Reference => RequestId is null && ResponseId is null ? null : string.Join(" · ", new[] { RequestId, ResponseId }.Where(x => x is not null));
}

/// <summary>A provider failure that carries a plain-English message for the homeowner, the sanitised provider code and a server-log detail line.</summary>
public abstract class ModelProviderException(string message, string? detail, ModelFailureInfo info, int status) : DomainException(message, status)
{
    /// <summary>Server-log detail: event type, code, param, bounded provider message, rate-limit headers. Never contains credentials.</summary>
    public string? Detail { get; } = detail;
    public ModelFailureInfo Info { get; } = info;
    public ModelFailureKind Kind => Info.Kind;
    public string? Code => Info.Code;
    public bool Retryable => this is TransientModelException;
}
/// <summary>A provider failure worth retrying: server error, overload, dropped stream, short rate limit or timeout. Usage limits are never transient.</summary>
public sealed class TransientModelException(string message, string? detail = null, ModelFailureInfo? info = null)
    : ModelProviderException(message, detail, info ?? new() { Kind = ModelFailureKind.Transient }, 502);
/// <summary>A non-retryable provider failure (usage limit, sign-in, rejected request, incomplete or filtered answer).</summary>
public sealed class ProviderModelException(string message, string? detail, ModelFailureInfo? info = null, int status = 502)
    : ModelProviderException(message, detail, info ?? new() { Kind = ModelFailureKind.Rejected }, status);

/// <summary>Raised before each retry so the UI can say "ChatGPT hiccup, trying again in 20 s (2 of 6)".</summary>
public sealed record ModelRetryNotice(int NextAttempt, int MaxAttempts, TimeSpan Delay, DateTimeOffset RetryAt, ModelFailureKind Kind, string? Code, string Message, string? RequestId, string Model);

/// <summary>Per-call options. All optional; existing callers need none of them.</summary>
public sealed record ModelCallOptions
{
    /// <summary>Told about every retry, downgrade and fallback before the wait starts. Exceptions thrown by the handler are ignored.</summary>
    public IProgress<ModelRetryNotice>? Progress { get; init; }
    /// <summary>Overrides <see cref="AiModelClient.RetryDelays"/>, e.g. a short ladder for a reply the user is waiting on.</summary>
    public IReadOnlyList<TimeSpan>? RetryDelays { get; init; }
    /// <summary>Stable system text, sent as the Responses API "instructions" field (or a system message for the API provider) so the prefix stays cacheable.</summary>
    public string? Instructions { get; init; }
    /// <summary>Another model on the same provider to try once after transient retries run out. Defaults to configuration Ai:FallbackModel.</summary>
    public string? FallbackModel { get; init; }
    /// <summary>The investigation turn, for logs and failure diagnostics.</summary>
    public int? Turn { get; init; }
}

public class AiModelClient(HttpClient http, ChatGptAuth auth, IConfiguration configuration, ILogger<AiModelClient>? logger = null)
{
    public const int MaxResponseBytes = 4_000_000, MaxReplyCharacters = 60000;
    const int MaxErrorBodyBytes = 16_384;
    public bool ApiConfigured => !string.IsNullOrWhiteSpace(configuration["Ai:ApiKey"]);
    /// <summary>
    /// Waits before each retry of a transient provider failure. Live outages last minutes, so the ladder climbs from
    /// seconds to minutes: one hiccup or a short outage must not discard a long investigation.
    /// </summary>
    public TimeSpan[] RetryDelays { get; set; } = [TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(20), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(3), TimeSpan.FromMinutes(8)];
    /// <summary>A rate limit whose wait is longer than this ends the call (with a reset time) instead of waiting inside it.</summary>
    public TimeSpan MaxRateLimitWait { get; set; } = TimeSpan.FromMinutes(2);
    /// <summary>Bounds one provider call. Reasoning models spend minutes on a long transcript.</summary>
    public TimeSpan CallTimeout { get; set; } = TimeSpan.FromMinutes(12);
    /// <summary>± fraction applied to ladder waits so retries from several calls do not line up.</summary>
    public double RetryJitter { get; set; } = 0.2;
    /// <summary>The wait used between attempts; replaceable in tests.</summary>
    public Func<TimeSpan, CancellationToken, Task> Delay { get; set; } = (wait, ct) => wait <= TimeSpan.Zero ? Task.CompletedTask : Task.Delay(wait, ct);
    // Models that rejected the reasoning parameter once are not sent it again for the life of the process.
    readonly ConcurrentDictionary<string, bool> rejectsReasoning = new(StringComparer.OrdinalIgnoreCase);
    public bool ModelRejectsReasoning(string model) => rejectsReasoning.ContainsKey(model);

    public async Task<ModelReply> CompleteAsync(string provider, string model, string prompt, CancellationToken cancellationToken, string? reasoningEffort = null, ModelCallOptions? options = null)
    {
        var ladder = options?.RetryDelays ?? RetryDelays;
        var fallback = options?.FallbackModel ?? configuration["Ai:FallbackModel"];
        var started = DateTimeOffset.UtcNow;
        var effort = reasoningEffort; var currentModel = model;
        int attempt = 0, ladderStep = 0;
        bool timeoutRetried = false, incompleteRetried = false, fallbackUsed = false;
        while (true)
        {
            attempt++;
            var attemptStarted = DateTimeOffset.UtcNow;
            try
            {
                var reply = await CompleteOnceAsync(provider, currentModel, prompt, effort, options, cancellationToken);
                reply = reply with { Attempts = attempt, FallbackModelUsed = fallbackUsed, Model = currentModel };
                logger?.LogInformation("AI provider call completed (turn {Turn}, attempt {Attempt}, model {Model}, effort {Effort}, {ElapsedMs} ms): input {InputTokens} (cached {CachedTokens}), output {OutputTokens} (reasoning {ReasoningTokens}), request {RequestId}, response {ResponseId}",
                    options?.Turn, attempt, currentModel, reply.ReasoningEffort ?? "none", (long)(DateTimeOffset.UtcNow - attemptStarted).TotalMilliseconds, reply.InputTokens, reply.CachedInputTokens, reply.OutputTokens, reply.ReasoningTokens, reply.RequestId ?? "none", reply.ResponseId ?? "none");
                return reply;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (ModelProviderException failure)
            {
                var info = failure.Info;
                var failedModel = currentModel; var failedEffort = effort;
                TimeSpan? wait = null; string? notice = null; var label = Label(provider); var onLadder = false;
                switch (info.Kind)
                {
                    case ModelFailureKind.Transient when failure.Retryable && ladderStep < ladder.Count:
                        wait = Jittered(ladder[ladderStep++]); onLadder = true;
                        notice = $"{label} hiccup{CodeSuffix(info.Code)}, trying again in {Duration(wait.Value)}";
                        break;
                    case ModelFailureKind.RateLimited when failure.Retryable && ladderStep < ladder.Count:
                        // Honour the provider's own wait when it is short; a long wait ends the call with its reset time.
                        var hinted = info.RetryAfter ?? ladder[ladderStep];
                        if (hinted <= MaxRateLimitWait)
                        {
                            ladderStep++; onLadder = true;
                            wait = info.RetryAfter is { } exact ? exact + TimeSpan.FromMilliseconds(250) : Jittered(hinted);
                            notice = $"{label} asked Joule to slow down, trying again in {Duration(wait.Value)}";
                        }
                        break;
                    case ModelFailureKind.Timeout when !timeoutRetried:
                        timeoutRetried = true; effort = Lower(effort); wait = TimeSpan.Zero;
                        notice = $"{label} took too long on one step, trying once more{(effort is null ? "" : " with less deliberation")}";
                        break;
                    case ModelFailureKind.Incomplete when info.IncompleteReason == "max_output_tokens" && !incompleteRetried && Lower(effort) != effort:
                        // Reasoning tokens count against the output cap, so a lighter think leaves room for the answer.
                        incompleteRetried = true; effort = Lower(effort); wait = TimeSpan.Zero;
                        notice = $"{label}'s answer was cut off, trying again with less deliberation";
                        break;
                }
                if (wait is null && info.Kind is ModelFailureKind.Transient or ModelFailureKind.Timeout && !fallbackUsed
                    && !string.IsNullOrWhiteSpace(fallback) && !fallback.Equals(currentModel, StringComparison.OrdinalIgnoreCase))
                {
                    fallbackUsed = true; currentModel = fallback.Trim(); wait = TimeSpan.Zero;
                    notice = $"{label} model {model} kept failing, trying {currentModel} once";
                }
                // Ladder retries count against the whole ladder; one-off retries (timeout, cut-off answer, fallback) are "once more".
                var maxAttempts = onLadder ? Math.Max(attempt + 1, ladder.Count + 1) : attempt + (wait is null ? 0 : 1);
                LogFailure(failure, attempt, maxAttempts, failedModel, failedEffort, prompt.Length, options?.Turn, DateTimeOffset.UtcNow - attemptStarted, wait);
                if (wait is null) throw Final(failure, provider, attempt, DateTimeOffset.UtcNow - started, model, options?.Turn);
                Notify(options, new(attempt + 1, maxAttempts, wait.Value, DateTimeOffset.UtcNow + wait.Value, info.Kind, info.Code, $"{notice} ({attempt + 1} of {maxAttempts}).", info.RequestId, currentModel));
                await Delay(wait.Value, cancellationToken);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                logger?.LogWarning("AI provider call failed (turn {Turn}, attempt {Attempt}, model {Model}): {ExceptionType}: {Message}", options?.Turn, attempt, currentModel, e.GetType().Name, e.Message);
                throw;
            }
        }
    }

    void Notify(ModelCallOptions? options, ModelRetryNotice notice)
    {
        try { options?.Progress?.Report(notice); }
        catch (Exception e) { logger?.LogDebug(e, "AI retry progress handler failed"); }
    }

    // Logged server-side only: operators see the provider's own code, message, request id and rate-limit headers.
    void LogFailure(ModelProviderException e, int attempt, int maxAttempts, string model, string? effort, int promptChars, int? turn, TimeSpan elapsed, TimeSpan? wait)
    {
        var next = wait is null ? "giving up" : wait == TimeSpan.Zero ? "retrying now" : $"retrying in {(long)wait.Value.TotalSeconds} s";
        logger?.LogWarning("AI provider call failed (turn {Turn}, attempt {Attempt}/{MaxAttempts}, model {Model}, effort {Effort}, prompt {PromptChars} chars, after {ElapsedMs} ms; {Next}): kind={Kind} code={Code} http={Http} request={RequestId} response={ResponseId}. {Message} Provider detail: {Detail}",
            turn, attempt, maxAttempts, model, effort ?? "none", promptChars, (long)elapsed.TotalMilliseconds, next, e.Kind, e.Code ?? "none", e.Info.HttpStatus?.ToString(CultureInfo.InvariantCulture) ?? "none", e.Info.RequestId ?? "none", e.Info.ResponseId ?? "none", e.Message, e.Detail ?? "none");
    }

    static ModelProviderException Final(ModelProviderException failure, string provider, int attempts, TimeSpan elapsed, string model, int? turn)
    {
        var info = failure.Info with { Attempts = attempts, Elapsed = elapsed, Model = failure.Info.Model ?? model, Turn = turn };
        var message = attempts > 1 ? $"{failure.Message} Joule tried {attempts} times over {Duration(elapsed)}." : failure.Message;
        return Rebuild(failure, message, failure.Detail, info);
    }
    static ModelProviderException Rebuild(ModelProviderException failure, string message, string? detail, ModelFailureInfo info) => failure is TransientModelException
        ? new TransientModelException(message, detail, info)
        : new ProviderModelException(message, detail, info, failure.Status);

    TimeSpan Jittered(TimeSpan wait)
    {
        if (wait <= TimeSpan.Zero || RetryJitter <= 0) return wait;
        return wait * (1 + (Random.Shared.NextDouble() * 2 - 1) * RetryJitter);
    }
    /// <summary>One step lighter: high → medium → low; low stays low.</summary>
    public static string? Lower(string? effort) => effort switch { "xhigh" => "high", "high" => "medium", "medium" => "low", "minimal" => "minimal", null => null, _ => "low" };
    static string Label(string provider) => provider.Equals("ChatGpt", StringComparison.OrdinalIgnoreCase) ? "ChatGPT" : "The AI service";
    static string CodeSuffix(string? code) => code is null ? "" : $" ({code})";
    static string Duration(TimeSpan span)
    {
        if (span < TimeSpan.FromSeconds(1)) return "under a second";
        if (span < TimeSpan.FromSeconds(90)) return $"{Math.Round(span.TotalSeconds):0} s";
        if (span < TimeSpan.FromMinutes(90)) return $"{Math.Round(span.TotalMinutes):0} min";
        return $"{span.TotalHours:0.#} h";
    }

    async Task<ModelReply> CompleteOnceAsync(string provider, string model, string prompt, string? reasoningEffort, ModelCallOptions? options, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(model)) throw new DomainException("Select a model before running AI.", 400);
        var subscription = provider.Equals("ChatGpt", StringComparison.OrdinalIgnoreCase);
        if (!subscription && !new[] { "Api", "OpenAI" }.Contains(provider, StringComparer.OrdinalIgnoreCase))
            throw new DomainException("Unknown AI provider.", 400);
        var label = Label(provider);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        // The provider call, not the investigation, is bounded here.
        deadline.CancelAfter(CallTimeout);
        var ct = deadline.Token;
        string? requestId = null;
        var owned = new List<IDisposable>();
        try
        {
            var key = subscription ? await auth.AccessTokenAsync(ct) : configuration["Ai:ApiKey"];
            if (string.IsNullOrWhiteSpace(key)) throw new DomainException("Configure an API key on the server before running API analysis.", 400);
            var url = subscription ? "https://api.openai.com/v1/responses" : (configuration["Ai:ApiBaseUrl"] ?? "https://api.openai.com/v1").TrimEnd('/') + "/chat/completions";
            var effort = subscription && reasoningEffort != null && !rejectsReasoning.ContainsKey(model) ? reasoningEffort : null;
            var dropped = subscription && reasoningEffort != null && effort == null;
            async Task<HttpResponseMessage> Send(string bearer, string? sentEffort)
            {
                var request = new HttpRequestMessage(HttpMethod.Post, url);
                owned.Add(request);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
                request.Content = subscription
                    ? JsonContent.Create(ResponsesRequestBody(model, prompt, sentEffort, options?.Instructions))
                    : JsonContent.Create(ChatCompletionsRequestBody(model, prompt, options?.Instructions));
                var sent = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                owned.Add(sent);
                requestId = RequestIdOf(sent) ?? requestId;
                return sent;
            }
            var response = await Send(key, effort);
            if (response.StatusCode == HttpStatusCode.Unauthorized && subscription)
            {
                // A revoked or rotated access token: renew it once and try again before telling anyone to reconnect.
                var body = await ReadBodyAsync(response, ct);
                logger?.LogWarning("AI provider rejected the ChatGPT access token (request {RequestId}); forcing one token refresh. Provider detail: {Detail}", requestId ?? "none", HttpDetail(response, body));
                key = await auth.ForceRefreshAsync(key, ct);
                response = await Send(key, effort);
            }
            if (response.StatusCode == HttpStatusCode.BadRequest && effort != null)
            {
                var body = await ReadBodyAsync(response, ct);
                var error = ParseErrorBody(body);
                if (!RejectsReasoningParameter(error)) throw HttpFailure(response, body, error, label, requestId, model);
                // Only a model that says it does not accept reasoning is retried without it; every other 400 is reported.
                rejectsReasoning[model] = true; dropped = true;
                logger?.LogWarning("Model {Model} rejected the reasoning parameter (request {RequestId}); sending without it from now on. Provider detail: {Detail}", model, requestId ?? "none", HttpDetail(response, body));
                effort = null;
                response = await Send(key, null);
            }
            if (!response.IsSuccessStatusCode)
            {
                var body = await ReadBodyAsync(response, ct);
                throw HttpFailure(response, body, ParseErrorBody(body), label, requestId, model);
            }
            if (response.Content.Headers.ContentLength > MaxResponseBytes) throw new DomainException("AI response exceeded the allowed byte limit.", 502);
            ModelReply reply;
            if (subscription)
            {
                try { reply = await ReadResponsesStreamAsync(await response.Content.ReadAsStreamAsync(ct), ct, requestId); }
                catch (ModelProviderException e)
                {
                    // A stream error arrives on an HTTP 200; its rate-limit headers are what tell throttling from an outage.
                    var info = e.Info with { Model = model, RetryAfter = e.Info.RetryAfter ?? (e.Kind is ModelFailureKind.RateLimited ? RetryAfterHeader(response) : null) };
                    throw Rebuild(e, e.Message, $"{e.Detail} {HttpDetail(response, null)}", info);
                }
            }
            else
            {
                await using var responseStream = await response.Content.ReadAsStreamAsync(ct);
                using var boundedStream = new BoundedResponseStream(responseStream);
                reply = await ReadChatCompletionAsync(boundedStream, requestId, ct);
            }
            return reply with { ReasoningEffort = effort, ReasoningDropped = dropped, RequestId = reply.RequestId ?? requestId };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Our per-call deadline or the HttpClient timeout fired; the caller did not cancel.
            throw new TransientModelException($"{label} took more than {Duration(CallTimeout)} on one step, so Joule stopped waiting.", $"timeout after {CallTimeout}",
                new() { Kind = ModelFailureKind.Timeout, Code = "timeout", RequestId = requestId, Model = model });
        }
        catch (HttpRequestException e)
        {
            throw new TransientModelException($"{label} couldn't be reached.", $"network {e.HttpRequestError}: {Redact(e.Message)}",
                new() { Kind = ModelFailureKind.Transient, Code = "network_error", RequestId = requestId, Model = model });
        }
        catch (IOException e)
        {
            throw new TransientModelException($"The connection to {(subscription ? "ChatGPT" : "the AI service")} dropped before the answer finished.", $"stream {e.GetType().Name}: {Redact(e.Message)}",
                new() { Kind = ModelFailureKind.Transient, Code = "stream_interrupted", RequestId = requestId, Model = model });
        }
        finally { for (var i = owned.Count - 1; i >= 0; i--) owned[i].Dispose(); }
    }

    static async Task<ModelReply> ReadChatCompletionAsync(Stream stream, string? requestId, CancellationToken ct)
    {
        JsonDocument doc;
        try { doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct); }
        catch (JsonException e) { throw new TransientModelException("The AI service sent an answer Joule couldn't read.", $"malformed json: {Redact(e.Message)}", new() { Kind = ModelFailureKind.Transient, Code = "malformed_response", RequestId = requestId }); }
        using (doc)
        {
            var choice = doc.RootElement.GetProperty("choices")[0];
            var finish = choice.TryGetProperty("finish_reason", out var f) && f.ValueKind == JsonValueKind.String ? f.GetString() : null;
            if (finish == "length") throw new ProviderModelException("The AI service's answer was cut off before it finished.", "finish_reason=length", new() { Kind = ModelFailureKind.Incomplete, Code = "max_output_tokens", IncompleteReason = "max_output_tokens", RequestId = requestId });
            if (finish == "content_filter") throw new ProviderModelException("The AI service declined to answer (content filter).", "finish_reason=content_filter", new() { Kind = ModelFailureKind.ContentFilter, Code = "content_filter", IncompleteReason = "content_filter", RequestId = requestId });
            if (finish != "stop") throw new DomainException("API response did not finish successfully; analysis was discarded.", 502);
            var result = choice.GetProperty("message").GetProperty("content").GetString();
            if (string.IsNullOrWhiteSpace(result)) throw new DomainException("API returned no analysis.", 502);
            if (result.Length > MaxReplyCharacters) throw new DomainException("AI model reply exceeded the allowed character limit.", 502);
            var usage = doc.RootElement.TryGetProperty("usage", out var u) ? u : default;
            return new(result, Tokens(usage, "prompt_tokens"), Tokens(usage, "completion_tokens"))
            {
                CachedInputTokens = Tokens(Nested(usage, "prompt_tokens_details"), "cached_tokens"),
                ReasoningTokens = Tokens(Nested(usage, "completion_tokens_details"), "reasoning_tokens"),
                RequestId = requestId, ResponseId = Text(doc.RootElement, "id")
            };
        }
    }

    // The prompt asks for one JSON object; json_object mode stops reasoning models wrapping it in prose.
    // The ChatGPT plan route requires store=false and stream=true and rejects max_output_tokens, previous_response_id,
    // prompt_cache_key, temperature and system-role items, so none of those are ever sent. Each turn carries the whole
    // transcript as one user message, so no reasoning items (and no encrypted reasoning content) need to be replayed.
    public static object ResponsesRequestBody(string model, string prompt, string? reasoningEffort = null, string? instructions = null)
    {
        var body = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["input"] = new[] { new { role = "user", content = prompt } },
            ["store"] = false,
            ["stream"] = true,
            ["text"] = new { format = new { type = "json_object" } }
        };
        if (!string.IsNullOrWhiteSpace(instructions)) body["instructions"] = instructions;
        if (reasoningEffort is not null) body["reasoning"] = new { effort = reasoningEffort };
        return body;
    }
    public static object ChatCompletionsRequestBody(string model, string prompt, string? instructions = null)
    {
        var messages = new List<object>();
        if (!string.IsNullOrWhiteSpace(instructions)) messages.Add(new { role = "system", content = instructions });
        messages.Add(new { role = "user", content = prompt });
        return new { model, messages, response_format = new { type = "json_object" }, max_completion_tokens = 12000 };
    }

    public static async Task<ModelReply> ReadResponsesStreamAsync(Stream stream, CancellationToken ct, string? requestId = null)
    {
        string? responseId = null; string? lastType = null;
        try
        {
            using var boundedStream = new BoundedResponseStream(stream);
            using var reader = new StreamReader(boundedStream, Encoding.UTF8, leaveOpen: true);
            var data = new StringBuilder();
            var deltas = new StringBuilder();
            while (true)
            {
                var line = await reader.ReadLineAsync(ct);
                if (line is null || line.Length == 0)
                {
                    if (data.Length > 0)
                    {
                        var payload = data.ToString(); data.Clear();
                        if (payload != "[DONE]")
                        {
                            using var doc = JsonDocument.Parse(payload);
                            var root = doc.RootElement;
                            var type = root.GetProperty("type").GetString() ?? throw new KeyNotFoundException("type");
                            lastType = type;
                            if (Nested(root, "response") is { ValueKind: JsonValueKind.Object } created && Text(created, "id") is { } id && SafeId(id) is { } safe) responseId ??= safe;
                            if (type is "error" or "response.failed" or "response.incomplete")
                                throw StreamFailure(root, type, requestId, responseId);
                            if (type == "response.output_text.delta")
                            {
                                var delta = root.GetProperty("delta").GetString() ?? "";
                                if (deltas.Length + delta.Length > MaxReplyCharacters) throw new DomainException("ChatGPT output exceeded the allowed character limit; partial analysis was discarded.", 502);
                                deltas.Append(delta);
                            }
                            if (type == "response.completed")
                            {
                                var response = root.GetProperty("response");
                                if (response.TryGetProperty("status", out var status) && status.GetString() is { } state && state != "completed")
                                    throw StreamFailure(root, state == "incomplete" ? "response.incomplete" : "response.failed", requestId, responseId);
                                var text = new StringBuilder();
                                if (response.TryGetProperty("output", out var output)) foreach (var item in output.EnumerateArray())
                                    if (item.TryGetProperty("content", out var contents)) foreach (var content in contents.EnumerateArray())
                                        if (content.TryGetProperty("type", out var kind) && kind.GetString() == "output_text")
                                        {
                                            var part = content.GetProperty("text").GetString() ?? "";
                                            if (text.Length + part.Length > MaxReplyCharacters) throw new DomainException("ChatGPT output exceeded the allowed character limit; partial analysis was discarded.", 502);
                                            text.Append(part);
                                        }
                                var completedText = text.Length > 0 ? text.ToString() : deltas.ToString();
                                if (string.IsNullOrWhiteSpace(completedText)) throw new DomainException("ChatGPT returned no analysis.", 502);
                                var usage = response.TryGetProperty("usage", out var u) ? u : default;
                                return new(completedText, Tokens(usage, "input_tokens"), Tokens(usage, "output_tokens"))
                                {
                                    CachedInputTokens = Tokens(Nested(usage, "input_tokens_details"), "cached_tokens"),
                                    ReasoningTokens = Tokens(Nested(usage, "output_tokens_details"), "reasoning_tokens"),
                                    RequestId = requestId, ResponseId = responseId
                                };
                            }
                        }
                    }
                    if (line is null) break;
                }
                else if (line.StartsWith("data:", StringComparison.Ordinal))
                {
                    if (data.Length > 0) data.Append('\n');
                    data.Append(line.AsSpan(5).TrimStart());
                    if (data.Length > 4_000_000) throw new DomainException("ChatGPT event exceeded the allowed size.", 502);
                }
            }
        }
        catch (IOException e)
        {
            // HttpIOException and socket resets surface here while reading the event stream, not as HttpRequestException.
            throw new TransientModelException("The connection to ChatGPT dropped before the answer finished.", $"stream {e.GetType().Name} after {lastType ?? "no events"}: {Redact(e.Message)}",
                new() { Kind = ModelFailureKind.Transient, Code = "stream_interrupted", RequestId = requestId, ResponseId = responseId });
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or IndexOutOfRangeException)
        {
            throw new TransientModelException("ChatGPT sent a garbled answer.", $"malformed event after {lastType ?? "no events"}: {e.GetType().Name}: {Redact(e.Message)}",
                new() { Kind = ModelFailureKind.Transient, Code = "malformed_event", RequestId = requestId, ResponseId = responseId });
        }
        throw new TransientModelException("ChatGPT stopped answering part-way through.", $"stream ended without response.completed after {lastType ?? "no events"}",
            new() { Kind = ModelFailureKind.Transient, Code = "stream_ended_early", RequestId = requestId, ResponseId = responseId });
    }

    // ---- Failure classification -------------------------------------------------------------------------

    static readonly HashSet<string> UsageLimitCodes = new(StringComparer.OrdinalIgnoreCase) { "subscription_sharing_usage_limit_exceeded", "usage_limit_reached", "usage_not_included", "insufficient_quota", "billing_hard_limit_reached", "billing_not_active" };
    // subscription_sharing_usage_unavailable and _user_unavailable are 503s: "retry later with bounded backoff".
    static readonly HashSet<string> TransientCodes = new(StringComparer.OrdinalIgnoreCase) { "subscription_sharing_usage_unavailable", "subscription_sharing_user_unavailable", "server_error", "internal_error", "internal_server_error", "api_error", "overloaded", "overloaded_error", "server_is_overloaded", "server_overloaded", "service_unavailable", "slow_down", "timeout", "request_timeout", "stream_error" };
    static readonly HashSet<string> RateLimitCodes = new(StringComparer.OrdinalIgnoreCase) { "rate_limit_exceeded", "rate_limit_error", "rate_limited", "requests", "tokens", "too_many_requests" };
    static readonly HashSet<string> SignInCodes = new(StringComparer.OrdinalIgnoreCase) { "subscription_sharing_invalid_user", "invalid_api_key", "invalid_authentication", "authentication_error", "token_expired", "token_invalidated", "account_deactivated" };
    static readonly HashSet<string> ContentCodes = new(StringComparer.OrdinalIgnoreCase) { "content_filter", "content_policy_violation", "safety_violation", "cyber_policy_violation" };
    static readonly HashSet<string> RejectedCodes = new(StringComparer.OrdinalIgnoreCase) { "context_length_exceeded", "invalid_prompt", "invalid_request_error", "invalid_value", "invalid_type", "unsupported_parameter", "unsupported_value", "unknown_parameter", "model_not_found", "not_found_error", "permission_error", "subscription_sharing_unsupported_capability", "subscription_sharing_route_not_supported", "subscription_sharing_user_not_eligible", "chatpass_v2_scope_not_authorized", "chatpass_v2_invalid_authorization_context", "unsupported_country_region_territory" };

    static ModelFailureKind? KindOf(string? code)
    {
        if (string.IsNullOrEmpty(code)) return null;
        if (UsageLimitCodes.Contains(code)) return ModelFailureKind.UsageLimit;
        if (TransientCodes.Contains(code)) return ModelFailureKind.Transient;
        if (RateLimitCodes.Contains(code)) return ModelFailureKind.RateLimited;
        if (SignInCodes.Contains(code)) return ModelFailureKind.SignIn;
        if (ContentCodes.Contains(code)) return ModelFailureKind.ContentFilter;
        if (RejectedCodes.Contains(code)) return ModelFailureKind.Rejected;
        return null;
    }

    /// <summary>The parsed provider error: the standard {"error":{…}} object, a top-level stream error, or a pre-stream {"detail":"…"} admission body.</summary>
    sealed record ProviderError(string? Code, string? Type, string? Param, string? Message, TimeSpan? ResetsIn, DateTimeOffset? ResetsAt)
    {
        public static readonly ProviderError None = new(null, null, null, null, null, null);
        /// <summary>The most specific plain identifier: the code, else the error type.</summary>
        public string? Shown => Plain(Code) ?? Plain(Type);
    }

    static ProviderError ParseError(JsonElement root)
    {
        JsonElement error = default;
        if (root.TryGetProperty("error", out var top) && top.ValueKind == JsonValueKind.Object) error = top;
        else if (Nested(root, "response") is { ValueKind: JsonValueKind.Object } response && Nested(response, "error") is { ValueKind: JsonValueKind.Object } nested) error = nested;
        var code = Text(error, "code") ?? Text(root, "code");
        // An error object's "type" is its class (e.g. invalid_request_error); the stream event's own "type" is just "error".
        var type = Text(error, "type");
        var message = Text(error, "message") ?? Text(root, "message") ?? Text(root, "detail") ?? (root.TryGetProperty("error", out var plain) && plain.ValueKind == JsonValueKind.String ? plain.GetString() : null);
        TimeSpan? resetsIn = Number(error, "resets_in_seconds") is { } s and > 0 ? TimeSpan.FromSeconds(s) : null;
        DateTimeOffset? resetsAt = Number(error, "resets_at") is { } at and > 1_000_000_000 and < 10_000_000_000 ? DateTimeOffset.FromUnixTimeSeconds((long)at) : null;
        return new(code, type, Text(error, "param") ?? Text(root, "param"), message, resetsIn, resetsAt);
    }

    static ProviderError ParseErrorBody(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return ProviderError.None;
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.ValueKind == JsonValueKind.Object ? ParseError(doc.RootElement) : ProviderError.None;
        }
        catch (JsonException) { return ProviderError.None with { Message = body }; }
    }

    static bool RejectsReasoningParameter(ProviderError error)
    {
        if (error.Param is { } param && param.StartsWith("reasoning", StringComparison.OrdinalIgnoreCase)) return true;
        var unsupported = error.Code is "unsupported_parameter" or "unsupported_value" or "unknown_parameter" or "subscription_sharing_unsupported_capability";
        return unsupported && error.Message is { } m && m.Contains("reasoning", StringComparison.OrdinalIgnoreCase);
    }

    static ModelProviderException StreamFailure(JsonElement root, string type, string? requestId, string? responseId)
    {
        var error = ParseError(root);
        var response = Nested(root, "response");
        var reason = Text(Nested(response, "incomplete_details"), "reason");
        var detail = $"type={Text(root, "type")} code={Plain(error.Code) ?? "none"} errorType={Plain(error.Type) ?? "none"} param={Sanitize(error.Param, 80) ?? "none"} reason={Plain(reason) ?? "none"} status={Plain(Text(response, "status")) ?? "none"} message={Sanitize(error.Message, 400) ?? "none"}";
        if (type == "response.incomplete")
        {
            var filtered = reason == "content_filter";
            var info = new ModelFailureInfo { Kind = filtered ? ModelFailureKind.ContentFilter : ModelFailureKind.Incomplete, Code = Plain(reason) ?? error.Shown, IncompleteReason = Plain(reason), RequestId = requestId, ResponseId = responseId };
            var message = filtered ? "ChatGPT declined to answer this review (content filter)."
                : reason == "max_output_tokens" ? "ChatGPT's answer was cut off before it finished (max_output_tokens)."
                : $"ChatGPT stopped before finishing its answer{CodeSuffix(Plain(reason))}.";
            return new ProviderModelException(message, detail, info);
        }
        var shown = error.Shown;
        var kind = KindOf(error.Code) ?? KindOf(error.Type) ?? ModelFailureKind.Transient;
        var wait = RetryHint(error.Message) ?? error.ResetsIn;
        var failure = new ModelFailureInfo
        {
            Kind = kind, Code = shown, Param = Sanitize(error.Param, 80), RequestId = requestId, ResponseId = responseId, RetryAfter = wait,
            ResetsAt = error.ResetsAt ?? (wait is { } w ? DateTimeOffset.UtcNow + w : null)
        };
        return Create(kind, "ChatGPT", shown, null, failure, detail);
    }

    static ModelProviderException HttpFailure(HttpResponseMessage response, string? body, ProviderError error, string label, string? requestId, string model)
    {
        var status = (int)response.StatusCode;
        var shown = error.Shown;
        var kind = KindOf(error.Code) ?? KindOf(error.Type) ?? status switch
        {
            401 => ModelFailureKind.SignIn,
            429 => ModelFailureKind.RateLimited,
            408 or 409 or 425 or >= 500 => ModelFailureKind.Transient,
            _ => ModelFailureKind.Rejected
        };
        var wait = RetryAfterHeader(response) ?? RetryHint(error.Message) ?? error.ResetsIn;
        var info = new ModelFailureInfo
        {
            Kind = kind, Code = shown, Param = Sanitize(error.Param, 80), HttpStatus = status, RequestId = requestId, Model = model, RetryAfter = wait,
            ResetsAt = error.ResetsAt ?? (wait is { } w ? DateTimeOffset.UtcNow + w : null)
        };
        return Create(kind, label, shown, status, info, HttpDetail(response, body));
    }

    static ModelProviderException Create(ModelFailureKind kind, string label, string? code, int? http, ModelFailureInfo info, string detail)
    {
        var technical = string.Join(", ", new[] { code, http is { } h ? $"HTTP {h}" : null }.Where(x => x is not null));
        var suffix = technical.Length == 0 ? "" : $" ({technical})";
        var until = info.RetryAfter is { } wait ? $" It asked Joule to wait about {Duration(wait)}." : "";
        return kind switch
        {
            ModelFailureKind.Transient => new TransientModelException($"{label} had a problem and didn't answer{suffix}.", detail, info),
            // Retried only with a rate-limit signal (a known code or a wait hint). A bare 429 may be a plan limit, and
            // hammering a plan limit wastes the allowance, so it ends the call like before.
            ModelFailureKind.RateLimited when info.RetryAfter is not null || (code is not null && RateLimitCodes.Contains(code))
                => new TransientModelException($"{label} is busy and asked Joule to slow down{suffix}.{until}", detail, info),
            ModelFailureKind.RateLimited => new ProviderModelException($"{label} is busy and asked Joule to slow down{suffix}.", detail, info),
            ModelFailureKind.UsageLimit => new ProviderModelException($"{(label == "ChatGPT" ? "Your ChatGPT plan's usage limit for Joule has been reached" : "The AI account's usage limit has been reached")}{suffix}. Reviews pause until it resets{(info.ResetsAt is null ? "; check ChatGPT settings → Usage" : "")}.", detail, info),
            ModelFailureKind.SignIn => new ProviderModelException(label == "ChatGPT"
                ? $"ChatGPT didn't accept Joule's sign-in, even after renewing it{suffix}. Reconnect ChatGPT in Setup."
                : $"The AI service didn't accept the server's API key{suffix}. Check the key in the server configuration.", detail, info),
            ModelFailureKind.ContentFilter => new ProviderModelException($"{label} declined to answer this review{suffix}.", detail, info),
            ModelFailureKind.Incomplete => new ProviderModelException($"{label} stopped before finishing its answer{suffix}.", detail, info),
            _ => new ProviderModelException($"{label} rejected the request{suffix}.", detail, info)
        };
    }

    static async Task<string?> ReadBodyAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            var buffer = new byte[MaxErrorBodyBytes]; var total = 0;
            while (total < buffer.Length) { var read = await stream.ReadAsync(buffer.AsMemory(total), ct); if (read == 0) break; total += read; }
            return Encoding.UTF8.GetString(buffer, 0, total);
        }
        catch (Exception e) when (e is IOException or HttpRequestException) { return null; }
    }

    static string HttpDetail(HttpResponseMessage response, string? body)
    {
        var headers = new List<string>();
        foreach (var (name, values) in response.Headers.Concat(response.Content.Headers))
        {
            if (!(name.StartsWith("x-ratelimit-", StringComparison.OrdinalIgnoreCase) || name.StartsWith("retry-after", StringComparison.OrdinalIgnoreCase)
                || name.Equals("x-request-id", StringComparison.OrdinalIgnoreCase) || name.Equals("openai-processing-ms", StringComparison.OrdinalIgnoreCase) || name.Equals("cf-ray", StringComparison.OrdinalIgnoreCase))) continue;
            headers.Add($"{name.ToLowerInvariant()}={Sanitize(string.Join(",", values), 60)}");
        }
        return $"http={(int)response.StatusCode} headers=[{string.Join(" ", headers.Take(16))}] body={Sanitize(body, 600) ?? "none"}";
    }

    static string? RequestIdOf(HttpResponseMessage response) =>
        response.Headers.TryGetValues("x-request-id", out var values) ? SafeId(values.FirstOrDefault()) : null;

    /// <summary>Retry-After (seconds or HTTP date), retry-after-ms, or the longest x-ratelimit-reset-* duration.</summary>
    static TimeSpan? RetryAfterHeader(HttpResponseMessage response)
    {
        if (response.Headers.TryGetValues("retry-after-ms", out var ms) && double.TryParse(ms.FirstOrDefault(), NumberStyles.Float, CultureInfo.InvariantCulture, out var milliseconds) && milliseconds >= 0)
            return TimeSpan.FromMilliseconds(milliseconds);
        if (response.Headers.RetryAfter is { } retry)
        {
            if (retry.Delta is { } delta) return delta;
            if (retry.Date is { } date) return date - DateTimeOffset.UtcNow is { } left && left > TimeSpan.Zero ? left : TimeSpan.Zero;
        }
        TimeSpan? longest = null;
        foreach (var name in new[] { "x-ratelimit-reset-requests", "x-ratelimit-reset-tokens" })
            if (response.Headers.TryGetValues(name, out var values) && ParseDuration(values.FirstOrDefault()) is { } reset && (longest is null || reset > longest)) longest = reset;
        return longest;
    }

    /// <summary>"Please try again in 11.054s" / "in 20ms" / "in 1m30s" → a wait.</summary>
    static TimeSpan? RetryHint(string? message)
    {
        if (message is null) return null;
        var match = Regex.Match(message, @"(?i)try again in\s+~?\s*((?:\d+(?:\.\d+)?\s*(?:ms|h|m|s|seconds?|minutes?|hours?)\s*)+)");
        return match.Success ? ParseDuration(match.Groups[1].Value) : null;
    }
    static TimeSpan? ParseDuration(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var total = TimeSpan.Zero; var any = false;
        foreach (Match m in Regex.Matches(text, @"(?i)(\d+(?:\.\d+)?)\s*(ms|h|hours?|m|minutes?|s|seconds?)\b?"))
        {
            var value = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture); any = true;
            total += m.Groups[2].Value.ToLowerInvariant() switch
            {
                "ms" => TimeSpan.FromMilliseconds(value),
                "h" or "hour" or "hours" => TimeSpan.FromHours(value),
                "m" or "minute" or "minutes" => TimeSpan.FromMinutes(value),
                _ => TimeSpan.FromSeconds(value)
            };
        }
        return any ? total : null;
    }

    static readonly Regex Secret = new(@"(?i)(bearer\s+|sk-|eyJ)[A-Za-z0-9._\-]+", RegexOptions.Compiled);
    static string Redact(string text) => Secret.Replace(text, "[redacted]");
    static string? Sanitize(string? text, int limit)
    {
        if (text is null) return null;
        text = Redact(text.ReplaceLineEndings(" "));
        return text.Length > limit ? text[..limit] + "…" : text;
    }
    /// <summary>Only plain identifiers are ever shown to the user; never arbitrary provider text.</summary>
    static string? Plain(string? code) => code is { Length: > 0 and <= 64 } && Regex.IsMatch(code, "^[a-z0-9_.]+$") ? code : null;
    static string? SafeId(string? id) => id is { Length: > 0 and <= 128 } && Regex.IsMatch(id, "^[A-Za-z0-9_.:-]+$") ? id : null;
    static JsonElement Nested(JsonElement element, string name) => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) ? value : default;
    static string? Text(JsonElement element, string name) => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    static double? Number(JsonElement element, string name) => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var d) ? d : null;

    // The wrapper bounds total bytes across many small SSE events and before API
    // JSON allocation. It leaves the caller-owned stream open.
    sealed class BoundedResponseStream(Stream inner) : Stream
    {
        int read;
        void Account(int count) { read += count; if (read > MaxResponseBytes) throw new DomainException("AI response exceeded the allowed byte limit; partial analysis was discarded.", 502); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            var count = await inner.ReadAsync(buffer[..Math.Min(buffer.Length, Math.Max(1, MaxResponseBytes - read + 1))], ct); Account(count); return count;
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();
        public override int Read(byte[] buffer, int offset, int count) { var amount = inner.Read(buffer, offset, Math.Min(count, Math.Max(1, MaxResponseBytes - read + 1))); Account(amount); return amount; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
    static long Tokens(JsonElement usage, string key) => usage.ValueKind == JsonValueKind.Object && usage.TryGetProperty(key, out var value) && value.TryGetInt64(out var count) ? count : 0;
}
