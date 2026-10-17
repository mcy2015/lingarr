using Lingarr.Contracts.Settings;
using Lingarr.Server.Modules.SubtitleSearch.Abstractions;

namespace Lingarr.Server.Modules.SubtitleSearch.Providers;

/// <summary>
/// Common plumbing of the v1 subtitle sources (⑩ — seven since the 2026-10
/// xunlei addition backfilled the dead-site removal): provider key, the
/// named HttpClient ("subtitle-{provider}", tech design ②), the candidate
/// cache, credential reads through <see cref="ISettingsAccess"/> (translation
/// plugin precedent — values are re-read per call so credential changes
/// apply without a restart) and base-URL resolution with the production
/// default overridable for the fake-source E2E round (PRD AC25).
/// </summary>
public abstract class SubtitleProviderBase
{
    /// <summary>DI name prefix of the per-source named HttpClients ("subtitle-opensubtitles"…).</summary>
    public const string HttpClientNamePrefix = "subtitle-";

    protected SubtitleProviderBase(
        string providerKey,
        HttpClient httpClient,
        SubtitleCandidateCache candidateCache,
        ILogger logger)
    {
        ProviderKey = providerKey;
        Http = httpClient;
        CandidateCache = candidateCache;
        Logger = logger;
    }

    /// <summary>Stable lowercase source key — matches the attribute name and the settings/telemetry columns.</summary>
    protected string ProviderKey { get; }

    protected HttpClient Http { get; }

    protected SubtitleCandidateCache CandidateCache { get; }

    protected ILogger Logger { get; }

    /// <summary>The DI name of this source's HttpClient.</summary>
    public static string HttpClientName(string providerKey) => HttpClientNamePrefix + providerKey;

    /// <summary>
    /// Reads a required secret (API key/token). Missing credentials raise
    /// <see cref="SubtitleProviderException"/> with reason MissingCredentials —
    /// the source card renders that as "configure credentials" instead of a
    /// search error (PRD AC2).
    /// </summary>
    protected async Task<string> GetRequiredSecretAsync(
        ISettingsAccess settings, string settingKey, string credentialName, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var value = await settings.GetEncryptedSettingAsync(settingKey);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw SubtitleProviderException.ForMissingCredentials(ProviderKey, credentialName);
        }

        return value.Trim();
    }

    /// <summary>
    /// Reads an optional plain setting (base URL override…); null/blank maps
    /// onto the production default.
    /// </summary>
    protected async Task<string> ResolveBaseUrlAsync(
        ISettingsAccess settings, string settingKey, string productionDefault, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var configured = await settings.GetSettingAsync(settingKey);
        return NormalizeBaseUrl(configured) ?? productionDefault;
    }

    /// <summary>Trims a configured base URL and tolerates a trailing slash; null when blank.</summary>
    protected static string? NormalizeBaseUrl(string? configured)
    {
        if (string.IsNullOrWhiteSpace(configured))
        {
            return null;
        }

        var trimmed = configured.Trim().TrimEnd('/');
        return trimmed.Length == 0 ? null : trimmed;
    }

    /// <summary>
    /// Extracts a display format ("srt", "ass", "zip"…) from a remote file
    /// name; defaults to "srt" — the download pipeline re-detects from the
    /// payload anyway (M4).
    /// </summary>
    protected static string FormatFromFileName(string? fileName)
    {
        // Known-video/subtitle/archive extensions only: a scene name like
        // "Movie.2023.WEBDL-1080p" would make GetExtension eat ".2023" —
        // never trust extension inference on media names (known trap).
        var extensions = new[]
        {
            ".srt", ".ass", ".ssa", ".sub", ".idx", ".vtt", ".zip", ".rar", ".7z", ".tar", ".gz"
        };
        if (string.IsNullOrEmpty(fileName))
        {
            return "srt";
        }

        foreach (var extension in extensions)
        {
            if (fileName.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            {
                return extension[1..].ToLowerInvariant();
            }
        }

        return "srt";
    }
}
