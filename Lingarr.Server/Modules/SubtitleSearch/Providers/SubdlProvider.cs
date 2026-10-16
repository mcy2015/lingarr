using System.Text.Json;
using Lingarr.Contracts.Settings;
using Lingarr.Server.Modules.SubtitleSearch.Abstractions;

namespace Lingarr.Server.Modules.SubtitleSearch.Providers;

/// <summary>
/// SubDL source (tech design ⑩): <c>api.subdl.com</c>, free — an API key is
/// optional (the public API grants anonymous access at lower limits; a key
/// from the source's getting-started page raises them, docs.subdl.com), so
/// the manifest field is optional and the request omits <c>api_key</c> when
/// unset. Search is <c>GET /auto</c> with a film name plus languages; movie
/// specialist. <c>results[].subtitle</c> carries a site-relative download
/// path that serves the raw zip payload — archive handling stays in the M4
/// pipeline.
/// </summary>
[SubtitleProvider("subdl")]
public sealed class SubdlProvider : SubtitleProviderBase, ISubtitleProvider, ISubtitleProviderHealth
{
    private const string ProductionBaseUrl = "https://api.subdl.com";

    /// <summary>Canonical codes → SubDL uppercase request/response codes; identity fallback (uppercased).</summary>
    private static readonly IReadOnlyDictionary<string, string> LanguageMap =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["en"] = "EN",
            ["zh"] = "ZH",
            ["zh-TW"] = "ZH",
            ["ja"] = "JP",
            ["ko"] = "KR",
            ["fr"] = "FR",
            ["de"] = "GE",
            ["es"] = "ES",
            ["ar"] = "AR"
        };

    /// <summary>
    /// Reverse mapping, written out (not derived) so it stays deterministic:
    /// SubDL does not distinguish Chinese scripts — ZH canonicalises onto zh.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> ResponseLanguageMap =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["EN"] = "en",
            ["ZH"] = "zh",
            ["JP"] = "ja",
            ["KR"] = "ko",
            ["FR"] = "fr",
            ["GE"] = "de",
            ["ES"] = "es",
            ["AR"] = "ar"
        };

    private readonly ISettingsAccess _settings;

    public SubdlProvider(
        ISettingsAccess settings,
        SubtitleCandidateCache candidateCache,
        HttpClient httpClient,
        ILogger<SubdlProvider> logger)
        : base("subdl", httpClient, candidateCache, logger)
    {
        _settings = settings;
    }

    public string Provider => ProviderKey;

    /// <inheritdoc />
    public async Task<SubtitleSearchResult> SearchAsync(SubtitleSearchRequest request, CancellationToken cancellationToken)
    {
        var baseUrl = await ResolveBaseUrlAsync(
            _settings, SubtitleSourceSettingKeys.Subdl.BaseUrl, ProductionBaseUrl, cancellationToken);
        var apiKey = (await _settings.GetEncryptedSettingAsync(SubtitleSourceSettingKeys.Subdl.ApiKey))?.Trim();

        var query = $"auto?film_name={Uri.EscapeDataString(BuildFilmName(request))}"
            + $"&languages={MapLanguages(request.Languages)}"
            + (string.IsNullOrEmpty(apiKey) ? string.Empty : $"&api_key={Uri.EscapeDataString(apiKey)}");

        using var httpRequest = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/{query}");
        httpRequest.Headers.Add("Accept", "application/json");

        using var document = await SubtitleHttp.SendForJsonAsync(Http, httpRequest, ProviderKey, cancellationToken);
        return new SubtitleSearchResult(ParseCandidates(document, request, baseUrl));
    }

    /// <inheritdoc />
    public async Task<SubtitleDownload> DownloadAsync(SubtitleCandidateRef candidateRef, CancellationToken cancellationToken)
    {
        var context = CandidateCache.Resolve<CandidateContext>(candidateRef, ProviderKey);

        // The download URL was resolved (against the possibly overridden
        // base) at search time and cached with the candidate.
        using var httpRequest = new HttpRequestMessage(HttpMethod.Get, context.DownloadUrl);
        var (content, headerFileName) = await SubtitleHttp.SendForBytesAsync(
            Http, httpRequest, ProviderKey, cancellationToken);
        var fileName = headerFileName ?? context.FileName ?? "subtitle.zip";

        return new SubtitleDownload(ProviderKey, fileName, FormatFromFileName(fileName), content);
    }

    /// <inheritdoc />
    public async Task<SubtitleProviderTestOutcome> TestConnectionAsync(CancellationToken cancellationToken)
    {
        var baseUrl = await ResolveBaseUrlAsync(
            _settings, SubtitleSourceSettingKeys.Subdl.BaseUrl, ProductionBaseUrl, cancellationToken);
        var apiKey = (await _settings.GetEncryptedSettingAsync(SubtitleSourceSettingKeys.Subdl.ApiKey))?.Trim();

        var query = "auto?film_name=lingarr&languages=EN"
            + (string.IsNullOrEmpty(apiKey) ? string.Empty : $"&api_key={Uri.EscapeDataString(apiKey)}");
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/{query}");
        request.Headers.Add("Accept", "application/json");

        using var _ = await SubtitleHttp.SendCheckedAsync(Http, request, ProviderKey, cancellationToken);
        return new SubtitleProviderTestOutcome(
            true,
            "SubDL reachable"
            + (string.IsNullOrEmpty(apiKey) ? " (anonymous; optional API key raises rate limits)." : " (API key accepted)."));
    }

    private static string BuildFilmName(SubtitleSearchRequest request)
        => request.Season is int season && request.Episode is int episode
            ? $"{request.Title} S{season:00}E{episode:00}"
            : $"{request.Title}{(request.Year is int year ? $" {year}" : string.Empty)}";

    private static string MapLanguages(string[] languages)
        => string.Join(",", languages.Select(code =>
            LanguageMap.TryGetValue(code, out var mapped) ? mapped : code.ToUpperInvariant()));

    private List<SubtitleCandidate> ParseCandidates(JsonDocument document, SubtitleSearchRequest request, string baseUrl)
    {
        var candidates = new List<SubtitleCandidate>();
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw SubtitleProviderException.ForInvalidResponse(ProviderKey, "expected a JSON object.");
        }

        // status=false is a legitimate miss (unknown film name), not an error.
        if (SubtitleHttp.GetBool(document.RootElement, "status") == false
            || !document.RootElement.TryGetProperty("results", out var results)
            || results.ValueKind != JsonValueKind.Array)
        {
            return candidates;
        }

        foreach (var result in results.EnumerateArray())
        {
            if (result.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var sourceLanguage = SubtitleHttp.GetString(result, "language");
            var language = MapResponseLanguage(sourceLanguage, request);
            var release = SubtitleHttp.GetString(result, "release_name");
            var subtitlePath = SubtitleHttp.GetString(result, "subtitle");
            if (string.IsNullOrEmpty(subtitlePath))
            {
                continue;
            }

            // The path is site-relative ("/subtitle/xxx.zip") — joined onto
            // the (possibly overridden) base URL so the fake-source round
            // downloads from the same origin it searched.
            var separator = subtitlePath!.StartsWith('/') ? string.Empty : "/";
            var downloadUrl = $"{baseUrl}{separator}{subtitlePath}";
            var fileName = Path.GetFileName(subtitlePath);
            var externalId = CandidateCache.Store(ProviderKey, new CandidateContext(downloadUrl, fileName));

            candidates.Add(new SubtitleCandidate(
                ProviderKey,
                externalId,
                language,
                MatchScore: 0,
                Forced: SubtitleHttp.GetBool(result, "forced"),
                HearingImpaired: SubtitleHttp.GetBool(result, "hearing_impaired"),
                ReleaseInfo: release,
                Uploader: SubtitleHttp.GetString(result, "author"),
                Format: FormatFromFileName(fileName),
                Size: null,
                HashMatch: false));
        }

        return candidates;
    }

    private static string MapResponseLanguage(string? sourceLanguage, SubtitleSearchRequest request)
    {
        if (!string.IsNullOrEmpty(sourceLanguage)
            && ResponseLanguageMap.TryGetValue(sourceLanguage, out var canonical))
        {
            return canonical;
        }

        // The languages= parameter already constrained the server side; an
        // unrecognised echo falls back to the primary requested code.
        return request.Languages.FirstOrDefault()?.ToLowerInvariant() ?? "en";
    }

    /// <summary>Provider-side state a download needs for one SubDL result.</summary>
    private sealed record CandidateContext(string DownloadUrl, string? FileName);
}
