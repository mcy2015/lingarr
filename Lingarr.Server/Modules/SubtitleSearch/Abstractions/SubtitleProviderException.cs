namespace Lingarr.Server.Modules.SubtitleSearch.Abstractions;

/// <summary>
/// Why a subtitle source call failed. The reason (not the HTTP status alone)
/// drives the cooldown mapping of tech design ⑧ — the M3 orchestrator and the
/// source cards read it to decide whether the source sits out a round.
/// </summary>
public enum SubtitleProviderFailureReason
{
    /// <summary>HTTP 429 (with optional Retry-After) — cooldown 1h or the Retry-After span, whichever is longer.</summary>
    RateLimited,

    /// <summary>
    /// The source's published daily quota is exhausted for today (design ⑧:
    /// OpenSubtitles answers 429/406 with <c>X-RateLimit-Remaining-Requests:
    /// 0</c>). Carries no suggested cooldown span — the cooldown store
    /// resolves "until" as the next UTC midnight, when the daily counter
    /// resets (same-day cooldown, ⑧).
    /// </summary>
    DailyLimit,

    /// <summary>HTTP 401/403 — credentials rejected; cooldown until the credentials change (⑧).</summary>
    Unauthorized,

    /// <summary>HTTP 5xx — cooldown 15min (⑧).</summary>
    ServerError,

    /// <summary>Request exceeded the per-source timeout (10s, design ②) — cooldown 10min (⑧).</summary>
    Timeout,

    /// <summary>DNS/socket level failure (source unreachable) — cooldown 15min (⑧, same bucket as 5xx).</summary>
    NetworkError,

    /// <summary>An auth-declaring source ran without its API key/token configured.</summary>
    MissingCredentials,

    /// <summary>The source answered with a body this adapter cannot parse — no cooldown (code bug or API drift).</summary>
    InvalidResponse,

    /// <summary>The requested subtitle/candidate does not exist any more (expired cache entry, removed upload).</summary>
    NotFound
}

/// <summary>
/// Failure surface of every subtitle provider call (tech design ②/⑧): a
/// structured verdict the orchestrator can bucket into cooldown, skip and
/// telemetry — instead of re-deriving semantics from raw
/// <see cref="HttpRequestException"/> status codes.
/// <para>
/// The suggested <see cref="Cooldown"/> follows the ⑧ mapping
/// (429→1h / 5xx&amp;network→15min / timeout→10min); it is a hint for the
/// caller, persisted by the M3 automation batch. Null means "no cooldown"
/// (reason alone describes the failure).
/// </para>
/// </summary>
public sealed class SubtitleProviderException : Exception
{
    /// <summary>Suggested cooldown per tech design ⑧; null = none.</summary>
    public TimeSpan? Cooldown { get; }

    /// <summary>Bucketed failure reason (drives the ⑧ mapping and the source cards).</summary>
    public SubtitleProviderFailureReason Reason { get; }

    /// <summary>Retry-After header value in seconds when the source sent one (429).</summary>
    public int? RetryAfterSeconds { get; }

    /// <summary>HTTP status of the failing response when the failure came from one; null otherwise.</summary>
    public int? HttpStatus { get; }

    /// <summary>
    /// The quota-window reset moment the source reported alongside its
    /// exhaustion verdict (the OpenSubtitles <c>reset_time_utc</c> response
    /// header, R3) — the exact instant the daily quota refills, unlike
    /// <see cref="RetryAfterSeconds"/> which describes the one-second
    /// rate-limit window. Null on verdicts that carry no such header: the
    /// cooldown store then falls back to the next UTC midnight.
    /// </summary>
    public DateTimeOffset? QuotaResetUtc { get; }

    /// <summary>Source key that failed ("opensubtitles"…).</summary>
    public string Provider { get; }

    private SubtitleProviderException(
        string provider,
        SubtitleProviderFailureReason reason,
        string message,
        TimeSpan? cooldown = null,
        int? retryAfterSeconds = null,
        int? httpStatus = null,
        Exception? inner = null,
        DateTimeOffset? quotaResetUtc = null)
        : base(message, inner)
    {
        Provider = provider;
        Reason = reason;
        Cooldown = cooldown;
        RetryAfterSeconds = retryAfterSeconds;
        HttpStatus = httpStatus;
        QuotaResetUtc = quotaResetUtc;
    }

    /// <summary>
    /// Maps one non-success HTTP status onto the ⑧ cooldown table:
    /// 429→max(1h, Retry-After), 401/403→until credentials change (no span,
    /// see <see cref="SubtitleProviderFailureReason.Unauthorized"/>),
    /// 5xx→15min, other 4xx→no cooldown (request-shape problem).
    /// </summary>
    public static SubtitleProviderException ForHttpStatus(
        string provider,
        System.Net.HttpStatusCode statusCode,
        int? retryAfterSeconds = null,
        string? bodySnippet = null)
    {
        var suffix = string.IsNullOrWhiteSpace(bodySnippet)
            ? string.Empty
            : $" Response: {Truncate(bodySnippet, 300)}";
        var retryNote = retryAfterSeconds is > 0 ? $" (Retry-After: {retryAfterSeconds}s)" : string.Empty;

        return statusCode switch
        {
            System.Net.HttpStatusCode.TooManyRequests => new SubtitleProviderException(
                provider,
                SubtitleProviderFailureReason.RateLimited,
                $"{provider} rate-limited the request{retryNote}.{suffix}",
                cooldown: MaxHourOrRetryAfter(retryAfterSeconds),
                retryAfterSeconds: retryAfterSeconds,
                httpStatus: (int)statusCode),
            System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden =>
                new SubtitleProviderException(
                    provider,
                    SubtitleProviderFailureReason.Unauthorized,
                    $"{provider} rejected the credentials (HTTP {(int)statusCode}).{suffix}",
                    httpStatus: (int)statusCode),
            var code when (int)code >= 500 => new SubtitleProviderException(
                provider,
                SubtitleProviderFailureReason.ServerError,
                $"{provider} server error (HTTP {(int)code}).{suffix}",
                cooldown: TimeSpan.FromMinutes(15),
                httpStatus: (int)code),
            var code => new SubtitleProviderException(
                provider,
                SubtitleProviderFailureReason.InvalidResponse,
                $"{provider} answered HTTP {(int)code}.{suffix}",
                httpStatus: (int)code)
        };
    }

    /// <summary>
    /// Daily quota exhausted (⑧ same-day cooldown). Detected by the shared
    /// subtitle HTTP plumbing from the source's rate-limit response (429/406
    /// with <c>X-RateLimit-Remaining-Requests: 0</c> — the OpenSubtitles v1
    /// shape — or the download-quota header pair <c>remaining: 0</c> +
    /// <c>reset_time_utc</c>, R3). Deliberately carries no
    /// <see cref="Cooldown"/> span: the cooldown store maps this reason onto
    /// the next UTC midnight, not a fixed offset, so the source recovers the
    /// moment its daily counter resets. <paramref name="resetEpochSeconds"/>
    /// is the optional <c>X-RateLimit-Reset</c> epoch hint carried in the
    /// message for diagnostics only — it describes the one-second rate
    /// window, never the daily reset, and therefore never feeds
    /// <see cref="QuotaResetUtc"/>. <paramref name="resetUtc"/> is the
    /// parsed <c>reset_time_utc</c> of the download-quota shape: when
    /// present, the cooldown store targets that exact moment instead of the
    /// midnight fallback and persists the <c>quota_exhausted</c> reason.
    /// </summary>
    public static SubtitleProviderException ForDailyLimit(
        string provider,
        long? resetEpochSeconds = null,
        int? httpStatus = null,
        DateTimeOffset? resetUtc = null) => new(
        provider,
        SubtitleProviderFailureReason.DailyLimit,
        resetUtc is { } reset
            ? $"{provider} reports the daily quota exhausted (resets at {reset:u})."
            : resetEpochSeconds is { } epoch
                ? $"{provider} reports the daily quota exhausted (resets at {DateTimeOffset.FromUnixTimeSeconds(epoch):u})."
                : $"{provider} reports the daily quota exhausted (resets at the next UTC midnight).",
        cooldown: null,
        httpStatus: httpStatus,
        quotaResetUtc: resetUtc);

    /// <summary>HttpClient timeout (TaskCanceledException without caller cancellation) — cooldown 10min per ⑧.</summary>
    public static SubtitleProviderException ForTimeout(string provider, Exception? inner = null) => new(
        provider,
        SubtitleProviderFailureReason.Timeout,
        $"{provider} did not answer within the per-source timeout (10s).",
        cooldown: TimeSpan.FromMinutes(10),
        inner: inner);

    /// <summary>Socket/DNS failure — cooldown 15min, same bucket as 5xx (⑧).</summary>
    public static SubtitleProviderException ForNetworkError(string provider, Exception inner) => new(
        provider,
        SubtitleProviderFailureReason.NetworkError,
        $"{provider} is unreachable: {inner.Message}",
        cooldown: TimeSpan.FromMinutes(15),
        inner: inner);

    /// <summary>An auth-declaring source ran without its key/token.</summary>
    public static SubtitleProviderException ForMissingCredentials(string provider, string credentialName) => new(
        provider,
        SubtitleProviderFailureReason.MissingCredentials,
        $"{provider} requires a {credentialName}; configure it on the subtitle-sources settings card.");

    /// <summary>Body the adapter cannot parse — no cooldown, this is a code/API-drift problem.</summary>
    public static SubtitleProviderException ForInvalidResponse(string provider, string detail, Exception? inner = null) => new(
        provider,
        SubtitleProviderFailureReason.InvalidResponse,
        $"{provider} returned an unexpected response: {detail}",
        inner: inner);

    /// <summary>Expired cache entry / removed upload — the caller retries a search.</summary>
    public static SubtitleProviderException ForNotFound(string provider, string detail) => new(
        provider,
        SubtitleProviderFailureReason.NotFound,
        $"{provider}: {detail}");

    private static TimeSpan? MaxHourOrRetryAfter(int? retryAfterSeconds)
    {
        // ⑧: 429 cools down for an hour — unless the source explicitly asks
        // for longer via Retry-After.
        if (retryAfterSeconds is > 0 && retryAfterSeconds.Value > 3600)
        {
            return TimeSpan.FromSeconds(retryAfterSeconds.Value);
        }

        return TimeSpan.FromHours(1);
    }

    private static string Truncate(string value, int maxLength)
        => value.Length <= maxLength ? value : value[..maxLength] + "…";
}
