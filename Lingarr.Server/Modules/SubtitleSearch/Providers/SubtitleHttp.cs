using System.Net.Http.Headers;
using System.Text.Json;
using Lingarr.Server.Modules.SubtitleSearch.Abstractions;

namespace Lingarr.Server.Modules.SubtitleSearch.Providers;

/// <summary>
/// Shared request/response plumbing of the subtitle providers: status-code
/// mapping onto <see cref="SubtitleProviderException"/> (cooldown table of
/// tech design ⑧), timeout discrimination (HttpClient timeout vs caller
/// cancellation) and body access with a bounded snippet for error messages.
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
/// </summary>
internal static class SubtitleHttp
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

    /// <summary>Applies the ⑧ mapping to a finished response (throws on non-success).</summary>
    public static void EnsureSuccess(HttpResponseMessage response, string provider)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var retryAfterSeconds = ParseRetryAfter(response.Headers.RetryAfter);
        var statusCode = response.StatusCode;
        response.Dispose(); // nobody else will read the failed response
        throw SubtitleProviderException.ForHttpStatus(provider, statusCode, retryAfterSeconds);
    }

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
