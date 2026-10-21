using System.Text.RegularExpressions;

namespace Lingarr.Server.Modules.Infrastructure;

/// <summary>
/// Redaction helpers applied where logs are emitted. With the Serilog file
/// sink, logs became persistent: anything credential-bearing that used to age
/// out of a container stdout now lives on disk. These helpers keep apikeys
/// and oversized payload excerpts out of that file.
/// </summary>
public static class LogSanitizer
{
    /// <summary>
    /// Matches <c>apikey=</c> / <c>api_key=</c> (radarr/sonarr query style and
    /// the LibreTranslate body key) and consumes the value up to the next
    /// separator, whitespace or quote so nothing of it survives.
    /// </summary>
    private static readonly Regex SensitiveParameter = new("(?i)(api_?key)=[^&\\s\"'<>]+", RegexOptions.Compiled);

    /// <summary>Returns the text with sensitive query parameter values masked.</summary>
    public static string Mask(string? text)
        => string.IsNullOrEmpty(text) ? string.Empty : SensitiveParameter.Replace(text, "$1=***");

    /// <summary>True when the text contains an unmasked apikey parameter.</summary>
    public static bool ContainsSensitiveData(string? text)
        => !string.IsNullOrEmpty(text) && SensitiveParameter.IsMatch(text);

    /// <summary>
    /// Truncates the text to <paramref name="maxLength"/> characters and
    /// returns the original length alongside it, so callers can log an honest
    /// "logged/original" count next to the excerpt instead of a bare slice.
    /// </summary>
    public static (string Text, int OriginalLength) Truncate(string? text, int maxLength)
    {
        if (string.IsNullOrEmpty(text))
        {
            return (string.Empty, 0);
        }

        return text.Length <= maxLength ? (text, text.Length) : (text[..maxLength], text.Length);
    }

    /// <summary>
    /// Returns a masked copy of a <see cref="HttpRequestException"/> when any
    /// message in its chain carries an apikey (HttpClient folds the request
    /// URI into connection-failure messages, and the request URL carries
    /// <c>apikey=</c> in the query string), or <c>null</c> when the chain is
    /// clean — the caller then rethrows the original untouched. The clone
    /// preserves the exception type and status code (callers' catch guards
    /// keep working); the inner chain is rebuilt with masked messages so
    /// ToString() cannot re-leak through it.
    /// </summary>
    public static HttpRequestException? MaskHttpRequestException(HttpRequestException exception)
    {
        if (!ChainContainsSensitiveData(exception))
        {
            return null;
        }

        return new HttpRequestException(
            Mask(exception.Message),
            MaskChain(exception.InnerException),
            exception.StatusCode);
    }

    private static bool ChainContainsSensitiveData(Exception? exception)
    {
        for (var current = exception; current != null; current = current.InnerException)
        {
            if (ContainsSensitiveData(current.Message))
            {
                return true;
            }
        }

        return false;
    }

    private static Exception? MaskChain(Exception? exception)
        => exception is null
            ? null
            : new Exception(Mask(exception.Message), MaskChain(exception.InnerException));
}
