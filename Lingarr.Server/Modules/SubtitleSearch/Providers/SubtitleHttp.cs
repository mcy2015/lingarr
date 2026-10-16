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
/// </summary>
internal static class SubtitleHttp
{
    /// <summary>
    /// JSON defaults matching the app-wide policy (camelCase,
    /// case-insensitive, numbers-from-strings tolerant).
    /// </summary>
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Sends one request and turns every failure into a
    /// <see cref="SubtitleProviderException"/>: 429/401/403/5xx per the ⑧
    /// table, HttpClient timeout as Timeout (10min cooldown), socket errors
    /// as NetworkError (15min). Caller cancellation propagates untouched.
    /// </summary>
    public static async Task<HttpResponseMessage> SendCheckedAsync(
        HttpClient httpClient,
        HttpRequestMessage request,
        string provider,
        CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        try
        {
            response = await httpClient.SendAsync(request, cancellationToken);
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

    /// <summary>GET/POST returning the raw payload bytes plus a file name hint from headers.</summary>
    public static async Task<(byte[] Content, string? FileName)> SendForBytesAsync(
        HttpClient httpClient,
        HttpRequestMessage request,
        string provider,
        CancellationToken cancellationToken)
    {
        using var response = await SendCheckedAsync(httpClient, request, provider, cancellationToken);
        var content = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        var fileName = response.Content.Headers.ContentDisposition?.FileNameStar
            ?? response.Content.Headers.ContentDisposition?.FileName;
        return (content, fileName?.Trim('"'));
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
