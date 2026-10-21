using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json;
using Lingarr.Server.Modules.Infrastructure;
using Lingarr.Server.Modules.SubtitleSearch.Abstractions;
using Microsoft.Extensions.Logging;

namespace Lingarr.Server.Modules.SubtitleSearch.Providers;

/// <summary>
/// Shared request/response plumbing of the subtitle providers: status-code
/// mapping onto <see cref="SubtitleProviderException"/> (cooldown table of
/// tech design ⑧), timeout discrimination (HttpClient timeout vs caller
/// cancellation), the per-source request-rate throttle (design ⑧ polish:
/// OpenSubtitles is capped at one request per second and assrt at its
/// published 20 requests per minute; every other source is currently
/// unlimited — <see cref="SetMinimumRequestInterval"/> extends the
/// table) and body access with a bounded snippet for error messages.
/// Every provider HTTP call goes through here so every source behaves
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
/// <para>
/// L4a: every verdict the gate maps is logged through a process-wide logger
/// (<see cref="Initialize"/>) — quota/rate-limit verdicts, timeouts and
/// network failures as Warning (the "assrt 493 shipped unlogged" closure),
/// other non-success statuses and long throttle waits as Debug, success
/// never.
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
    /// banned one; sources without a throttle-table entry stay unlimited.
    /// </summary>
    public static readonly TimeSpan OpenSubtitlesRequestInterval = TimeSpan.FromSeconds(1);

    /// <summary>
    /// assrt's published request limit: 20 requests per minute with the
    /// quota shared between the token and the calling IP (official v1
    /// documentation — exceeding the window answers 429 and risks the
    /// account). 60s / 20 = one request every three seconds, enforced as
    /// the source's minimum interval by the throttle table below.
    /// </summary>
    public static readonly TimeSpan AssrtRequestInterval = TimeSpan.FromSeconds(3);

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

    /// <summary>
    /// Response header carrying the remaining downloads of the current
    /// DAILY quota window (OpenSubtitles /download contract, R3 — a plain,
    /// unprefixed header name distinct from the rate-limit family above).
    /// Reading <c>0</c> on a 429/406 is the quota-exhaustion verdict.
    /// </summary>
    private const string DailyRemainingHeader = "remaining";

    /// <summary>
    /// Response header carrying the UTC moment the daily quota window
    /// refills (OpenSubtitles /download contract, R3) — an ISO-8601
    /// timestamp, not an epoch. Rides into the daily-limit exception as
    /// <see cref="SubtitleProviderException.QuotaResetUtc"/> so the
    /// cooldown can target the source's own reset instead of the midnight
    /// fallback.
    /// </summary>
    private const string DailyResetHeader = "reset_time_utc";

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
        // Default throttle table: OpenSubtitles publishes one request per
        // second, assrt 20 per minute (token/IP shared).
        // SetMinimumRequestInterval extends/overrides entries;
        // ClearRequestThrottling restores this default table (test hook).
        MinimumIntervals["opensubtitles"] = OpenSubtitlesRequestInterval;
        MinimumIntervals["assrt"] = AssrtRequestInterval;
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
    /// request and the documented OpenSubtitles/assrt limits.
    /// </summary>
    public static void ClearRequestThrottling()
    {
        Throttles.Clear();
        MinimumIntervals.Clear();
        MinimumIntervals["opensubtitles"] = OpenSubtitlesRequestInterval;
        MinimumIntervals["assrt"] = AssrtRequestInterval;
    }

    /// <summary>
    /// Waits until the source's rate limit allows the next request. Must be
    /// awaited before every <c>SendAsync</c> of a rate-limited source; the
    /// wait is interruptible through <paramref name="cancellationToken"/>
    /// (the orchestrator's per-source budget token).
    /// </summary>
    public static async Task ThrottleAsync(string provider, CancellationToken cancellationToken)
    {
        var interval = GetMinimumRequestInterval(provider);
        if (interval <= TimeSpan.Zero)
        {
            return;
        }

        var throttle = Throttles.GetOrAdd(provider, _ => new SourceThrottle());
        var stopwatch = Stopwatch.StartNew();
        await throttle.WaitForTurnAsync(interval, cancellationToken);
        stopwatch.Stop();

        // L4a: a wait of a second or more is the visible face of the assrt
        // 3s / OpenSubtitles 1s pacing — Debug, because a busy automated
        // round would otherwise flood the log with routine waits.
        if (stopwatch.Elapsed >= ThrottleWaitLogThreshold)
        {
            _logger?.LogDebug(
                "Subtitle source {Provider} waited {WaitMs:0} ms for its rate-limit slot before sending.",
                provider,
                stopwatch.Elapsed.TotalMilliseconds);
        }
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

    // ── Gate logging (L4a) ────────────────────────────────────────────────
    //
    // Every verdict this gate maps used to be silent — the direct root cause
    // of the "assrt 493 download throttle shipped unlogged" incident. The
    // logger is static like the rest of the gate's process-wide state,
    // assigned once at host start by SubtitleSearchModule's hosted wiring
    // (same pattern as DownloadClientFactory above); tests hand in a
    // capturing logger, null (uninitialized) keeps the gate silent.

    private static volatile ILogger? _logger;

    /// <summary>
    /// Installs (or, with null, removes) the gate's process-wide logger.
    /// Public for the module wiring and for tests — the same "public hook,
    /// no InternalsVisibleTo" ruling as <see cref="ClearRequestThrottling"/>.
    /// The wiring passes the logger of this static class as the category, so
    /// every entry filters as the shared gate regardless of which source
    /// produced it (the provider rides in the {Provider} placeholder).
    /// </summary>
    public static void Initialize(ILogger? logger) => _logger = logger;

    /// <summary>
    /// Single throttle waits at or above this span earn a Debug entry (L4a):
    /// below it the pacing is routine and would only flood the log — the
    /// waits that matter (assrt's 3s, OpenSubtitles' 1s slots) all clear it.
    /// </summary>
    private static readonly TimeSpan ThrottleWaitLogThreshold = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Log-safe request identifier of one gate call: scheme://host/path of
    /// the request URI — never the query string (the subtitle sources carry
    /// their keys and tokens there) and never the headers. Masked with
    /// <see cref="LogSanitizer.Mask"/> as defense in depth; "(no request
    /// URI)" for hand-built responses that did not carry their request.
    /// </summary>
    private static string DescribeRequest(Uri? requestUri)
        => requestUri is null
            ? "(no request URI)"
            : LogSanitizer.Mask(requestUri.GetComponents(
                UriComponents.SchemeAndServer | UriComponents.Path,
                UriFormat.UriEscaped));

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
        // L2 correlation: every verdict this gate logs (the L4a entries
        // below) carries the source key — pushed once at the common entry of
        // every provider HTTP call, before the throttle wait so pacing logs
        // are stamped too. Idempotent over an ambient push of the same key
        // (the orchestrator already stamps its per-source iterations).
        using var correlation = CorrelationContext.Push(provider: provider);

        // Design ⑧ per-source pacing: rate-limited sources (OpenSubtitles
        // 1 req/s, assrt 20/min) WAIT here for their slot before the
        // request leaves. The
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
            _logger?.LogWarning(
                ex,
                "Subtitle source {Provider} timed out while sending the request ({Request}) — Timeout verdict.",
                provider,
                DescribeRequest(request.RequestUri));
            throw SubtitleProviderException.ForTimeout(provider, ex);
        }
        catch (HttpRequestException ex)
        {
            _logger?.LogWarning(
                ex,
                "Subtitle source {Provider} failed while sending the request ({Request}) — NetworkError verdict.",
                provider,
                DescribeRequest(request.RequestUri));
            throw SubtitleProviderException.ForNetworkError(provider, ex);
        }

        EnsureSuccess(response, provider, request.RequestUri);
        return response;
    }

    /// <summary>
    /// Applies the ⑧ mapping to a finished response (throws on non-success).
    /// A rate-limit response that reports zero remaining requests maps onto
    /// the <see cref="SubtitleProviderFailureReason.DailyLimit"/> bucket
    /// (same-day cooldown) instead of the generic 429 handling — see
    /// <see cref="TryReadDailyLimit"/>.
    /// </summary>
    public static void EnsureSuccess(HttpResponseMessage response, string provider, Uri? requestUri = null)
    {
        if (response.IsSuccessStatusCode)
        {
            return; // success path stays log-free — aggregation is L4b's business
        }

        var retryAfterSeconds = ParseRetryAfter(response.Headers.RetryAfter);
        var statusCode = response.StatusCode;
        // Prefer the response's own request stamp; HttpClient does not
        // populate it for handler-built responses, so the caller's URI is
        // the reliable fallback.
        var request = DescribeRequest(response.RequestMessage?.RequestUri ?? requestUri);

        if (statusCode is System.Net.HttpStatusCode.TooManyRequests
            or System.Net.HttpStatusCode.NotAcceptable
            && TryReadDailyLimit(response, provider, out var dailyLimit))
        {
            // L4a: the quota verdict is the historic silent killer (the
            // rate-limit family once shipped entirely unlogged) — Warning,
            // low-frequency by nature and always visible in the ring/file.
            _logger?.LogWarning(
                "Subtitle source {Provider} answered {StatusCode} on {Request} with zero remaining quota — DailyLimit verdict.",
                provider,
                (int)statusCode,
                request);
            response.Dispose(); // nobody else will read the failed response
            throw dailyLimit;
        }

        response.Dispose(); // nobody else will read the failed response
        var failure = SubtitleProviderException.ForHttpStatus(provider, statusCode, retryAfterSeconds);
        if ((int)statusCode is 429 or 406 or 493 or 509)
        {
            // The rest of the rate-limit family: a bare 429/406 without
            // quota headers and assrt's non-standard 493/509 download
            // throttle — same Warning treatment, verdict stated honestly as
            // the cooldown mapping it actually is.
            _logger?.LogWarning(
                "Subtitle source {Provider} answered {StatusCode} on {Request} — rate-limit cooldown verdict{RetryAfterNote}.",
                provider,
                (int)statusCode,
                request,
                retryAfterSeconds is > 0 ? $" (Retry-After: {retryAfterSeconds}s)" : string.Empty);
        }
        else
        {
            // Every other non-success status can repeat heavily (5xx during
            // a source outage, per-candidate 404s) — Debug keeps the ring
            // and the default file clean while staying greppable.
            _logger?.LogDebug(
                "Subtitle source {Provider} answered {StatusCode} on {Request} — mapped to {Reason}.",
                provider,
                (int)statusCode,
                request,
                failure.Reason);
        }

        throw failure;
    }

    /// <summary>
    /// Detects the "daily quota exhausted" verdict of a rate-limit response.
    /// Two recognized header shapes: a 429 (or 406, the documented
    /// daily-download-limit status) whose <c>X-RateLimit-Remaining-Requests</c>
    /// header reads 0 (rate-limit family), and — R3 — a 406/429 whose plain
    /// <c>remaining</c> download-quota header reads 0 (the OpenSubtitles
    /// /download contract). The optional <c>X-RateLimit-Reset</c> epoch rides
    /// along into the message for diagnostics only; the daily
    /// <c>reset_time_utc</c> timestamp (when parseable) becomes
    /// <see cref="SubtitleProviderException.QuotaResetUtc"/> so the cooldown
    /// targets the source's own reset moment, with the next UTC midnight as
    /// the fallback (SubtitleSourceCooldownStore's DailyLimit branch).
    /// </summary>
    private static bool TryReadDailyLimit(
        HttpResponseMessage response,
        string provider,
        out SubtitleProviderException exception)
    {
        if (TryGetHeaderLong(response, RemainingRequestsHeader) is not 0
            && TryGetHeaderLong(response, DailyRemainingHeader) is not 0)
        {
            exception = null!;
            return false;
        }

        exception = SubtitleProviderException.ForDailyLimit(
            provider,
            resetEpochSeconds: TryGetHeaderLong(response, RateLimitResetHeader),
            httpStatus: (int)response.StatusCode,
            resetUtc: TryGetHeaderDateTimeOffset(response, DailyResetHeader));
        return true;
    }

    /// <summary>First parseable long of a response header, null when absent/unparsable.</summary>
    private static long? TryGetHeaderLong(HttpResponseMessage response, string name)
        => response.Headers.TryGetValues(name, out var values)
           && long.TryParse(values.FirstOrDefault(), out var parsed)
            ? parsed
            : null;

    /// <summary>
    /// First parseable timestamp of a response header, null when
    /// absent/unparsable. Header-name lookup is case-insensitive
    /// (<see cref="HttpHeaders"/> compares ordinally ignoring case), so
    /// <c>Reset_Time_Utc</c> and <c>reset_time_utc</c> resolve alike; the
    /// value accepts ISO-8601 with or without an explicit offset (naive
    /// values read as UTC per the header's contract).
    /// </summary>
    private static DateTimeOffset? TryGetHeaderDateTimeOffset(HttpResponseMessage response, string name)
        => response.Headers.TryGetValues(name, out var values)
           && DateTimeOffset.TryParse(
               values.FirstOrDefault(),
               System.Globalization.CultureInfo.InvariantCulture,
               System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
               out var parsed)
            ? parsed
            : null;

    /// <summary>
    /// GET/POST returning a parsed JSON document (disposes the response).
    /// <paramref name="onResponse"/> — when set — observes the live response
    /// right after the status check and before the body is consumed and the
    /// response disposed: the one window in which response headers are
    /// readable (R3 quota capture; the callback must not read the body).
    /// </summary>
    public static async Task<JsonDocument> SendForJsonAsync(
        HttpClient httpClient,
        HttpRequestMessage request,
        string provider,
        CancellationToken cancellationToken,
        Action<HttpResponseMessage>? onResponse = null)
    {
        using var response = await SendCheckedAsync(httpClient, request, provider, cancellationToken);
        onResponse?.Invoke(response);
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
        // L2 correlation: the byte download's body-read phase streams AFTER
        // SendCheckedAsync returned and disposed its own push — this outer
        // scope keeps the source key on the read-failure warnings below (and
        // covers download flows that enter here without an ambient stamp,
        // e.g. the download pipeline, which only stamps mediaKey/session).
        using var correlation = CorrelationContext.Push(provider: provider);

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

        try
        {
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
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw; // caller gave up — not a source failure
        }
        catch (Exception ex) when (ex is TaskCanceledException or HttpRequestException or IOException)
        {
            // L4a: with ResponseHeadersRead the body streams AFTER SendAsync
            // returned, so the send-side catches above never see these —
            // log with the full exception and rethrow unchanged (the
            // mapping of read-phase failures stays the caller's business).
            _logger?.LogWarning(
                ex,
                "Subtitle source {Provider} failed while reading the download body ({Request}).",
                provider,
                DescribeRequest(request.RequestUri));
            throw;
        }
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
