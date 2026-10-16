using System.Net.Http.Headers;
using System.Text.Json;
using Lingarr.Contracts.Settings;
using Lingarr.Core;
using Lingarr.Server.Modules.SubtitleSearch.Abstractions;

namespace Lingarr.Server.Modules.SubtitleSearch.Providers;

/// <summary>
/// OpenSubtitles.com official v1 REST source (tech design ⑩):
/// <c>https://api.opensubtitles.com/api/v1</c>, header
/// <c>Api-Key</c> plus a self-describing User-Agent (the API's terms require
/// it). Search is <c>GET /subtitles</c> — by movie hash when the request
/// carries one (hash results are exact-file matches and score 100, ③),
/// otherwise by IMDb id or title with season/episode. Download is two-step:
/// <c>POST /download</c> returns a short-lived <c>link</c>, fetched as the
/// raw payload (often zip) — archive handling stays in the M4 pipeline.
/// </summary>
[SubtitleProvider("opensubtitles", requiresAuth: true)]
public sealed class OpenSubtitlesProvider : SubtitleProviderBase, ISubtitleProvider, ISubtitleProviderHealth
{
    /// <summary>
    /// Published free-API-key limit. Per the official documentation
    /// (https://opensubtitles.stoplight.io/docs/opensubtitles-api —
    /// "40 downloads per day" for free API keys). Display-only constant:
    /// it feeds the quota_limit waterline column, never a circuit breaker
    /// (PM decision, ⑩/PRD AC22).
    /// </summary>
    public const int DailyDownloadQuota = 40;

    private const string ProductionBaseUrl = "https://api.opensubtitles.com/api/v1";

    /// <summary>Search-response language (ISO 639-3) → canonical code; identity fallback.</summary>
    private static readonly IReadOnlyDictionary<string, string> ResponseLanguageMap =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["eng"] = "en",
            ["zho"] = "zh",
            ["chi"] = "zh",
            ["jpn"] = "ja",
            ["kor"] = "ko",
            ["fra"] = "fr",
            ["ger"] = "de",
            ["spa"] = "es"
        };

    /// <summary>Canonical code → ISO 639-3 for the languages request parameter.</summary>
    private static readonly IReadOnlyDictionary<string, string> RequestLanguageMap =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["en"] = "eng",
            ["zh"] = "zho",
            ["zh-TW"] = "zho",
            ["ja"] = "jpn",
            ["ko"] = "kor",
            ["fr"] = "fra",
            ["de"] = "ger",
            ["es"] = "spa"
        };

    private readonly ISettingsAccess _settings;

    public OpenSubtitlesProvider(
        ISettingsAccess settings,
        SubtitleCandidateCache candidateCache,
        HttpClient httpClient,
        ILogger<OpenSubtitlesProvider> logger)
        : base("opensubtitles", httpClient, candidateCache, logger)
    {
        _settings = settings;
    }

    public string Provider => ProviderKey;

    /// <inheritdoc />
    public async Task<SubtitleSearchResult> SearchAsync(SubtitleSearchRequest request, CancellationToken cancellationToken)
    {
        var apiKey = await GetRequiredSecretAsync(
            _settings, SubtitleSourceSettingKeys.OpenSubtitles.ApiKey, "API key", cancellationToken);
        var baseUrl = await ResolveBaseUrlAsync(
            _settings, SubtitleSourceSettingKeys.OpenSubtitles.BaseUrl, ProductionBaseUrl, cancellationToken);

        var languages = string.Join(",",
            request.Languages.Select(code => RequestLanguageMap.TryGetValue(code, out var mapped) ? mapped : code));

        // Hash query (exact file): the whole result set counts as hash match.
        // Otherwise IMDb id outranks the title; episodes add season/episode.
        string query;
        var hashMatch = !string.IsNullOrEmpty(request.VideoHash);
        if (hashMatch)
        {
            query = $"subtitles?movie_hashes={Uri.EscapeDataString(request.VideoHash!)}&languages={languages}";
        }
        else if (!string.IsNullOrEmpty(request.ImdbId))
        {
            var imdbNumber = request.ImdbId!.StartsWith("tt", StringComparison.OrdinalIgnoreCase)
                ? request.ImdbId[2..]
                : request.ImdbId;
            query = $"subtitles?imdb_id={Uri.EscapeDataString(imdbNumber)}&languages={languages}";
        }
        else
        {
            query = $"subtitles?query={Uri.EscapeDataString(request.Title)}&languages={languages}";
        }

        if (request.Season is int season && request.Episode is int episode && !hashMatch)
        {
            query += $"&season={season}&episode={episode}";
        }

        using var httpRequest = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/{query}");
        ApplyHeaders(httpRequest, apiKey);

        using var document = await SubtitleHttp.SendForJsonAsync(Http, httpRequest, ProviderKey, cancellationToken);
        var candidates = ParseCandidates(document, hashMatch);
        if (Logger.IsEnabled(LogLevel.Debug))
        {
            Logger.LogDebug("OpenSubtitles returned {Count} candidates for '{Title}'.", candidates.Count, request.Title);
        }

        return new SubtitleSearchResult(candidates);
    }

    /// <inheritdoc />
    public async Task<SubtitleDownload> DownloadAsync(SubtitleCandidateRef candidateRef, CancellationToken cancellationToken)
    {
        var apiKey = await GetRequiredSecretAsync(
            _settings, SubtitleSourceSettingKeys.OpenSubtitles.ApiKey, "API key", cancellationToken);
        var baseUrl = await ResolveBaseUrlAsync(
            _settings, SubtitleSourceSettingKeys.OpenSubtitles.BaseUrl, ProductionBaseUrl, cancellationToken);
        var context = CandidateCache.Resolve<CandidateContext>(candidateRef, ProviderKey);

        // Step 1: exchange the file id for a short-lived download link.
        using var downloadRequest = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/download")
        {
            Content = SubtitleHttp.JsonBody(new { file_id = context.FileId })
        };
        ApplyHeaders(downloadRequest, apiKey);

        using var downloadDocument = await SubtitleHttp.SendForJsonAsync(
            Http, downloadRequest, ProviderKey, cancellationToken);
        var link = downloadDocument.RootElement.TryGetProperty("link", out var linkElement)
            && linkElement.ValueKind == JsonValueKind.String
                ? linkElement.GetString()
                : null;
        var fileName = downloadDocument.RootElement.TryGetProperty("file_name", out var nameElement)
            && nameElement.ValueKind == JsonValueKind.String
                ? nameElement.GetString()
                : null;
        if (string.IsNullOrEmpty(link))
        {
            throw SubtitleProviderException.ForInvalidResponse(ProviderKey, "POST /download returned no link.");
        }

        // Step 2: fetch the payload exactly as served (zip or plain text) —
        // the M4 pipeline owns magic-number detection and extraction.
        using var payloadRequest = new HttpRequestMessage(HttpMethod.Get, link);
        var (content, headerFileName) = await SubtitleHttp.SendForBytesAsync(
            Http, payloadRequest, ProviderKey, cancellationToken);
        var resolvedName = headerFileName ?? fileName ?? context.FileName ?? "subtitle.zip";

        return new SubtitleDownload(ProviderKey, resolvedName, FormatFromFileName(resolvedName), content);
    }

    /// <inheritdoc />
    public async Task<SubtitleProviderTestOutcome> TestConnectionAsync(CancellationToken cancellationToken)
    {
        var apiKey = await GetRequiredSecretAsync(
            _settings, SubtitleSourceSettingKeys.OpenSubtitles.ApiKey, "API key", cancellationToken);
        var baseUrl = await ResolveBaseUrlAsync(
            _settings, SubtitleSourceSettingKeys.OpenSubtitles.BaseUrl, ProductionBaseUrl, cancellationToken);

        // Lightest legal probe (tech design ⑥): a one-page search with a
        // throwaway keyword — 200 proves both connectivity and the API key.
        using var request = new HttpRequestMessage(
            HttpMethod.Get, $"{baseUrl}/subtitles?query=lingarr&languages=eng");
        ApplyHeaders(request, apiKey);

        using var _ = await SubtitleHttp.SendCheckedAsync(Http, request, ProviderKey, cancellationToken);
        return new SubtitleProviderTestOutcome(
            true,
            $"OpenSubtitles reachable; daily download quota {DailyDownloadQuota}.");
    }

    /// <summary>
    /// Flattens the <c>data[].attributes</c> entries into candidates, one
    /// per <c>files[]</c> entry (the file is what gets downloaded).
    /// <paramref name="hashMatch"/> marks every candidate of a movie-hash
    /// query — hash results are exact-file matches (③ short-circuit).
    /// </summary>
    private List<SubtitleCandidate> ParseCandidates(JsonDocument document, bool hashMatch)
    {
        var candidates = new List<SubtitleCandidate>();
        if (document.RootElement.ValueKind != JsonValueKind.Object
            || !document.RootElement.TryGetProperty("data", out var data)
            || data.ValueKind != JsonValueKind.Array)
        {
            throw SubtitleProviderException.ForInvalidResponse(ProviderKey, "missing data array.");
        }

        foreach (var entry in data.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object
                || !entry.TryGetProperty("attributes", out var attributes)
                || attributes.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var language = MapLanguage(SubtitleHttp.GetString(attributes, "language"));
            var forced = SubtitleHttp.GetBool(attributes, "forced");
            var hearingImpaired = SubtitleHttp.GetBool(attributes, "hearing_impaired");
            var uploader = attributes.TryGetProperty("uploader", out var uploaderElement)
                && uploaderElement.ValueKind == JsonValueKind.Object
                ? SubtitleHttp.GetString(uploaderElement, "name")
                : null;
            var releaseInfo = SubtitleHttp.GetString(attributes, "release_notes");

            if (!attributes.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var file in files.EnumerateArray())
            {
                if (file.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var fileId = file.TryGetProperty("file_id", out var fileIdElement)
                    && fileIdElement.ValueKind == JsonValueKind.Number
                    ? fileIdElement.GetInt64()
                    : 0;
                if (fileId == 0)
                {
                    continue;
                }

                var fileName = SubtitleHttp.GetString(file, "file_name");
                // The served file name is the strongest release fact OS
                // exposes — the scorer's title/token rules read it.
                var release = !string.IsNullOrWhiteSpace(releaseInfo) ? releaseInfo : fileName;
                var externalId = CandidateCache.Store(ProviderKey, new CandidateContext(
                    fileId, fileName, language, release));

                candidates.Add(new SubtitleCandidate(
                    ProviderKey,
                    externalId,
                    language,
                    MatchScore: 0,
                    Forced: forced,
                    HearingImpaired: hearingImpaired,
                    ReleaseInfo: release,
                    Uploader: uploader,
                    Format: FormatFromFileName(fileName),
                    Size: null, // the search response carries no payload size
                    HashMatch: hashMatch));
            }
        }

        return candidates;
    }

    private void ApplyHeaders(HttpRequestMessage request, string apiKey)
    {
        request.Headers.Add("Api-Key", apiKey);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        // Self-describing agent per the OpenSubtitles API terms.
        request.Headers.UserAgent.ParseAdd($"{LingarrVersion.Name}/{LingarrVersion.Number}");
    }

    private string MapLanguage(string? sourceLanguage)
    {
        if (string.IsNullOrEmpty(sourceLanguage))
        {
            return "en";
        }

        return ResponseLanguageMap.TryGetValue(sourceLanguage, out var mapped) ? mapped : sourceLanguage.ToLowerInvariant();
    }

    /// <summary>Provider-side state a download needs for one OS file entry.</summary>
    private sealed record CandidateContext(long FileId, string? FileName, string Language, string? ReleaseInfo);
}
