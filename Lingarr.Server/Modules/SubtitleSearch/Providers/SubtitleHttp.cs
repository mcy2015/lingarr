using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text.Json;
using Lingarr.Server.Modules.SubtitleSearch.Abstractions;

namespace Lingarr.Server.Modules.SubtitleSearch.Providers;

/// <summary>
/// Shared request/response plumbing of the subtitle providers: status-code
/// mapping onto <see cref="SubtitleProviderException"/> (cooldown table of
/// tech design ⑧), timeout discrimination (HttpClient timeout vs caller
/// cancellation), the per-source request-rate throttle (design ⑧ polish:
/// OpenSubtitles is capped at one request per second, every other source is
/// currently unlimited — <see cref="SetMinimumRequestInterval"/> extends the
/// table) and body access with a bounded snippet for error messages.
/// Every provider HTTP call goes through here so the seven sources behave
/// identically on 429/5xx/timeout — the acceptance matrix of the M2 batch.
/// <para>
/// Two HTTP budgets (QA2 P2-4/P2-5): search JSON calls ride the source's
/// named client (10s timeout, 2MB response buffer, wired in
/// <c>SubtitleSearchModule</c>), while byte downloads ride a dedicated
/// "<c>subtitle-{source}-download</c>" client (120s) resolved through
/// <see cref="DownloadClientFactory"/>, with a hard 100MB payload cap
/// enforced while streaming (<see cref="MaxDownloadBytes"/>).
/// </para>
/// <para>
/// Public (not internal) because the throttle table is process-wide static
/// state that tests must reset between cases — the same "public hook, no
/// InternalsVisibleTo" ruling as the orchestrator's testing constructor.
/// </para>
/// </summary>
public static class SubtitleHttp
{
    /// <summary>
    /// JSON defaults matching the app-wide policy (camelCase,
    /// case-insensitive, numbers-from-strings tolerant).
    /// </summary>
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Hard cap of one downloaded payload (declared or streamed) — a subtitle is kilobytes; 100MB means the source is broken or hostile.</summary>
    public const long MaxDownloadBytes = 100L * 1024 * 1024;

    /// <summary>DI name suffix of the per-source download clients ("subtitle-opensubtitles-download"…).</summary>
    public const string DownloadClientNameSuffix = "-download";

    /// <summary>
    /// OpenSubtitles' published burst limit: one request per second per API
    /// key (official v1 documentation — the API rejects sustained bursts
    /// with 429). The throttle makes Lingarr a polite client instead of a
    /// banned one; other sources publish no per-second limit and stay
    /// unlimited for now.
    /// </summary>
    public static readonly TimeSpan OpenSubtitlesRequestInterval = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Response header carrying the remaining requests of the current
    /// rate-limit window (OpenSubtitles v1 rate-limit contract).
    /// </summary>
    private const string RemainingRequestsHeader = "X-RateLimit-Remaining-Requests";

    /// <summary>
    /// Response header carrying the epoch second at which the rate-limit
    /// window resets (OpenSubtitles v1 rate-limit contract). Diagnostic
    /// only — the daily-limit cooldown targets UTC midnight regardless.
    /// </summary>
    private const string RateLimitResetHeader = "X-RateLimit-Reset";

    // ── Per-source request throttle (design ⑧) ───────────────────────────
    //
    // A process-wide table keyed on the source key: each entry serializes
    // its source's calls and enforces the source's minimum interval by
    // WAITING (never rejecting) — the caller's cancellation token can
    // interrupt the wait, which matters because the orchestrator's 10s
    // per-source budget penetrates into these HTTP calls.

    private static readonly ConcurrentDictionary<string, SourceThrottle> Throttles =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly ConcurrentDictionary<string, TimeSpan> MinimumIntervals =
        new(StringComparer.OrdinalIgnoreCase);

    static SubtitleHttp()
    {
        // Default throttle table: only OpenSubtitles publishes a per-second
        // limit today. SetMinimumRequestInterval extends/overrides entries;
        // ClearRequestThrottling restores this default table (test hook).
        MinimumIntervals["opensubtitles"] = OpenSubtitlesRequestInterval;
    }

    /// <summary>
    /// The configured minimum interval between two requests of one source
    /// (zero = unlimited). Public for tests and for future sources whose
    /// limit becomes known.
    /// </summary>
    public static TimeSpan GetMinimumRequestInterval(string provider)
        => MinimumIntervals.TryGetValue(provider, out var interval) ? interval : TimeSpan.Zero;

    /// <summary>
    /// Overrides the minimum request interval of one source (process-wide,
    /// applies to every client of that source). Setting
    /// <see cref="TimeSpan.Zero"/> removes the limit.
    /// </summary>
    public static void SetMinimumRequestInterval(string provider, TimeSpan interval)
    {
        if (interval <= TimeSpan.Zero)
        {
            MinimumIntervals.TryRemove(provider, out _);
        }
        else
        {
            MinimumIntervals[provider] = interval;
        }
    }

    /// <summary>
    /// Test hook: forgets every reservation and restores the default
    /// interval table, so each test starts with a clean, immediate first
    /// request and the documented OpenSubtitles limit.
    /// </summary>
    public static void ClearRequestThrottling()
    {
        Throttles.Clear();
        MinimumIntervals.Clear();
        MinimumIntervals["opensubtitles"] = OpenSubtitlesRequestInterval;
    }

    /// <summary>
    /// Waits until the source's rate limit allows the next request. Must be
    /// awaited before every <c>SendAsync</c> of a rate-limited source; the
    /// wait is interruptible through <paramref name="cancellationToken"/>
    /// (the orchestrator's per-source budget token).
    /// </summary>
    public static Task ThrottleAsync(string provider, CancellationToken cancellationToken)
    {
        var interval = GetMinimumRequestInterval(provider);
        if (interval <= TimeSpan.Zero)
        {
            return Task.CompletedTask;
        }

        var throttle = Throttles.GetOrAdd(provider, _ => new SourceThrottle());
        return throttle.WaitForTurnAsync(interval, cancellationToken);
    }

    /// <summary>
    /// One source's request pacing: a gate serializing the check-and-wait
    /// cycle plus the earliest instant the next request may leave. Waiting
    /// (not rejecting) is deliberate — a burst costs latency, never a
    /// dropped round.
    /// </summary>
    private sealed class SourceThrottle
    {
        private readonly SemaphoreSlim _gate = new(1, 1);
        private DateTimeOffset _nextAllowedUtc = DateTimeOffset.MinValue;

        public async Task WaitForTurnAsync(TimeSpan minInterval, CancellationToken cancellationToken)
        {
            await _gate.WaitAsync(cancellationToken);
            try
            {
                var now = DateTimeOffset.UtcNow;
                if (_nextAllowedUtc > now)
                {
                    // Reserve the slot before sleeping: a caller cancelled
                    // mid-wait must not free the window for the next request
                    // (the reservation is conservative by one interval).
                    var reservedAt = _nextAllowedUtc;
                    _nextAllowedUtc = reservedAt + minInterval;
                    await Task.Delay(reservedAt - now, cancellationToken);
                }
                else
                {
                    _nextAllowedUtc = now + minInterval;
                }
            }
            finally
            {
                _gate.Release();
            }
        }
    }

    /// <summary>DI name of a source's dedicated download client (long timeout budget).</summary>
    public static string DownloadHttpClientName(string providerKey)
        => SubtitleProviderBase.HttpClientName(providerKey) + DownloadClientNameSuffix;

    /// <summary>
    /// Module wiring hook: resolves the dedicated download client of a
    /// source. Assigned once at host start (<c>SubtitleSearchModule</c>'s
    /// hosted wiring) because providers only receive the search client
    /// through their typed-client constructor — the static surface is the
    /// one place both sides meet. Null (tests, unwired hosts) falls back to
    /// the passed-in client.
    /// </summary>
    public static Func<string, HttpClient>? DownloadClientFactory { get; set; }

    /// <summary>
    /// Sends one request and turns every failure into a
    /// <see cref="SubtitleProviderException"/>: 429/401/403/5xx per the ⑧
    /// table, HttpClient timeout as Timeout (10min cooldown), socket errors
    /// as NetworkError (15min). Caller cancellation propagates untouched.
    /// </summary>
    /// <param name="completionOption">
    /// Byte downloads pass <see cref="HttpCompletionOption.ResponseHeadersRead"/>
    /// so the body streams through the size cap instead of being buffered
    /// wholesale by the client (the default option buffers up to
    /// <c>HttpClient.MaxResponseContentBufferSize</c>, which the 2MB search
    /// budget would trip).
    /// </param>
    public static async Task<HttpResponseMessage> SendCheckedAsync(
        HttpClient httpClient,
        HttpRequestMessage request,
        string provider,
        CancellationToken cancellationToken,
        HttpCompletionOption completionOption = HttpCompletionOption.ResponseContentRead)
    {
        // Design ⑧ per-source pacing: rate-limited sources (OpenSubtitles,
        // 1 req/s) WAIT here for their slot before the request leaves. The
        // caller's token interrupts the wait — the orchestrator's 10s budget
        // stays the outer bound, never the rate limiter.
        await ThrottleAsync(provider, cancellationToken);

        HttpResponseMessage response;
        try
        {
            response = await httpClient.SendAsync(request, completionOption, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw; // caller gave up — not a source failure
        }
        catch (TaskCanceledException ex)
        {
            // No caller cancellation pending: the HttpClient timeout fired.
            throw SubtitleProviderException.ForTimeout(provider, ex);
        }
        catch (HttpRequestException ex)
        {
            throw SubtitleProviderException.ForNetworkError(provider, ex);
        }

        EnsureSuccess(response, provider);
        return response;
    }

    /// <summary>
    /// Applies the ⑧ mapping to a finished response (throws on non-success).
    /// A rate-limit response that reports zero remaining requests maps onto
    /// the <see cref="SubtitleProviderFailureReason.DailyLimit"/> bucket
    /// (same-day cooldown) instead of the generic 429 handling — see
    /// <see cref="TryReadDailyLimit"/>.
    /// </summary>
    public static void EnsureSuccess(HttpResponseMessage response, string provider)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var retryAfterSeconds = ParseRetryAfter(response.Headers.RetryAfter);
        var statusCode = response.StatusCode;

        if (statusCode is System.Net.HttpStatusCode.TooManyRequests
            or System.Net.HttpStatusCode.NotAcceptable
            && TryReadDailyLimit(response, provider, out var dailyLimit))
        {
            response.Dispose(); // nobody else will read the failed response
            throw dailyLimit;
        }

        response.Dispose(); // nobody else will read the failed response
        throw SubtitleProviderException.ForHttpStatus(provider, statusCode, retryAfterSeconds);
    }

    /// <summary>
    /// Detects the "daily quota exhausted" verdict of a rate-limit response.
    /// Implemented shape (assumption, official OpenSubtitles v1 contract):
    /// a 429 (or 406, the documented daily-download-limit status) whose
    /// <c>X-RateLimit-Remaining-Requests</c> header reads 0. The optional
    /// <c>X-RateLimit-Reset</c> epoch rides along into the message for
    /// diagnostics; the cooldown itself targets the next UTC midnight
    /// (SubtitleSourceCooldownStore's DailyLimit branch).
    /// </summary>
    private static bool TryReadDailyLimit(
        HttpResponseMessage response,
        string provider,
        out SubtitleProviderException exception)
    {
        if (TryGetHeaderLong(response, RemainingRequestsHeader) is not 0)
        {
            exception = null!;
            return false;
        }

        exception = SubtitleProviderException.ForDailyLimit(
            provider,
            resetEpochSeconds: TryGetHeaderLong(response, RateLimitResetHeader),
            httpStatus: (int)response.StatusCode);
        return true;
    }

    /// <summary>First parseable long of a response header, null when absent/unparsable.</summary>
    private static long? TryGetHeaderLong(HttpResponseMessage response, string name)
        => response.Headers.TryGetValues(name, out var values)
           && long.TryParse(values.FirstOrDefault(), out var parsed)
            ? parsed
            : null;

    /// <summary>GET/POST returning a parsed JSON document (disposes the response).</summary>
    public static async Task<JsonDocument> SendForJsonAsync(
        HttpClient httpClient,
        HttpRequestMessage request,
        string provider,
        CancellationToken cancellationToken)
    {
        using var response = await SendCheckedAsync(httpClient, request, provider, cancellationToken);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        try
        {
            return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        }
        catch (JsonException ex)
        {
            throw SubtitleProviderException.ForInvalidResponse(provider, "malformed JSON.", ex);
        }
    }

    /// <summary>
    /// GET/POST returning the raw payload bytes plus a file name hint from
    /// headers. Streams through <see cref="MaxDownloadBytes"/>: an
    /// over-large Content-Length is rejected up front and an undeclared
    /// body is capped mid-read — both as <see cref="SubtitleProviderFailureReason.InvalidResponse"/>
    /// (no cooldown: the source answered, it answered garbage). Sends via
    /// the source's dedicated download client when the module wired
    /// <see cref="DownloadClientFactory"/> (120s budget, QA2 P2-4).
    /// </summary>
    public static async Task<(byte[] Content, string? FileName)> SendForBytesAsync(
        HttpClient httpClient,
        HttpRequestMessage request,
        string provider,
        CancellationToken cancellationToken)
    {
        var downloadClient = DownloadClientFactory?.Invoke(provider) ?? httpClient;
        using var response = await SendCheckedAsync(
            downloadClient, request, provider, cancellationToken,
            HttpCompletionOption.ResponseHeadersRead);

        var declaredBytes = response.Content.Headers.ContentLength;
        if (declaredBytes is > MaxDownloadBytes)
        {
            throw SubtitleProviderException.ForInvalidResponse(
                provider,
                $"the download declares {declaredBytes} bytes, over the {MaxDownloadBytes / (1024 * 1024)}MB payload budget.");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var buffered = new MemoryStream((int)Math.Min(declaredBytes ?? 0L, MaxDownloadBytes));
        var chunk = new byte[81920];
        long total = 0;
        int read;
        while ((read = await stream.ReadAsync(chunk.AsMemory(), cancellationToken)) > 0)
        {
            total += read;
            if (total > MaxDownloadBytes)
            {
                throw SubtitleProviderException.ForInvalidResponse(
                    provider,
                    $"the download streamed past the {MaxDownloadBytes / (1024 * 1024)}MB payload budget.");
            }

            buffered.Write(chunk, 0, read);
        }

        var fileName = response.Content.Headers.ContentDisposition?.FileNameStar
            ?? response.Content.Headers.ContentDisposition?.FileName;
        return (buffered.ToArray(), fileName?.Trim('"'));
    }

    /// <summary>JSON body helper with the app-wide serializer options.</summary>
    public static ByteArrayContent JsonBody<T>(T payload)
    {
        var content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(payload, Json));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        return content;
    }

    /// <summary>Null-tolerant string access into a JSON object.</summary>
    public static string? GetString(JsonElement parent, string property)
        => parent.ValueKind == JsonValueKind.Object
            && parent.TryGetProperty(property, out var element)
            && element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;

    /// <summary>Null-tolerant boolean access into a JSON object (true literal or non-zero number).</summary>
    public static bool GetBool(JsonElement parent, string property)
        => parent.ValueKind == JsonValueKind.Object
            && parent.TryGetProperty(property, out var element)
            && (element.ValueKind == JsonValueKind.True
                || (element.ValueKind == JsonValueKind.Number && element.GetDouble() != 0));

    /// <summary>Null-tolerant integer access into a JSON object (accepts numbers-as-strings).</summary>
    public static long? GetInt64(JsonElement parent, string property)
    {
        if (parent.ValueKind != JsonValueKind.Object || !parent.TryGetProperty(property, out var element))
        {
            return null;
        }

        return element.ValueKind switch
        {
            JsonValueKind.Number when element.TryGetInt64(out var number) => number,
            JsonValueKind.String when long.TryParse(element.GetString(), out var number) => number,
            _ => null
        };
    }

    private static int? ParseRetryAfter(RetryConditionHeaderValue? header)
    {
        if (header is null)
        {
            return null;
        }

        if (header.Delta is { } delta)
        {
            return (int)Math.Max(1, delta.TotalSeconds);
        }

        if (header.Date is { } date)
        {
            return (int)Math.Max(1, (date - DateTimeOffset.UtcNow).TotalSeconds);
        }

        return null;
    }
}
