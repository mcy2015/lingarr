using System.Text;
using System.Text.Json;
using Lingarr.Contracts.Settings;
using Lingarr.Server.Modules.SubtitleSearch.Abstractions;

namespace Lingarr.Server.Modules.SubtitleSearch.Providers;

/// <summary>
/// SubSource JSON API source (tech design ⑩): free, no key, movie + episode
/// coverage in English plus multi-language supplements. Search is a POST
/// (<c>/api/searchMovie</c> for movies, <c>/api/searchSeason</c> for
/// episodes); the language filter is applied client-side because the search
/// endpoints return every language they host. Download posts to
/// <c>/api/getSub</c>, which answers with the subtitle text inside a JSON
/// string array. Implemented per the source's public API conventions; the
/// exact wire shapes are pinned by the test fixtures and the fake-source
/// server.
/// </summary>
[SubtitleProvider("subsource")]
public sealed class SubSourceProvider : SubtitleProviderBase, ISubtitleProvider, ISubtitleProviderHealth
{
    private const string ProductionBaseUrl = "https://api.subsource.net";

    /// <summary>Response language labels → canonical codes; case-insensitive.</summary>
    private static readonly IReadOnlyDictionary<string, string> ResponseLanguageMap =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["ENG"] = "en",
            ["English"] = "en",
            ["CHS"] = "zh",
            ["CHT"] = "zh-TW",
            ["Chinese"] = "zh",
            ["JPN"] = "ja",
            ["Japanese"] = "ja",
            ["KOR"] = "ko",
            ["Korean"] = "ko"
        };

    private readonly ISettingsAccess _settings;

    public SubSourceProvider(
        ISettingsAccess settings,
        SubtitleCandidateCache candidateCache,
        HttpClient httpClient,
        ILogger<SubSourceProvider> logger)
        : base("subsource", httpClient, candidateCache, logger)
    {
        _settings = settings;
    }

    public string Provider => ProviderKey;

    /// <inheritdoc />
    public async Task<SubtitleSearchResult> SearchAsync(SubtitleSearchRequest request, CancellationToken cancellationToken)
    {
        var baseUrl = await ResolveBaseUrlAsync(
            _settings, SubtitleSourceSettingKeys.SubSource.BaseUrl, ProductionBaseUrl, cancellationToken);

        var isEpisode = request.Season is not null && request.Episode is not null;
        var path = isEpisode ? "/api/searchSeason" : "/api/searchMovie";
        object payload = isEpisode
            ? new { showName = request.Title, season = request.Season, episode = request.Episode }
            : new { queryName = $"{request.Title}{(request.Year is int year ? $" {year}" : string.Empty)}" };

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, baseUrl + path)
        {
            Content = SubtitleHttp.JsonBody(payload)
        };
        httpRequest.Headers.Add("Accept", "application/json");

        using var document = await SubtitleHttp.SendForJsonAsync(Http, httpRequest, ProviderKey, cancellationToken);
        return new SubtitleSearchResult(ParseCandidates(document, request));
    }

    /// <inheritdoc />
    public async Task<SubtitleDownload> DownloadAsync(SubtitleCandidateRef candidateRef, CancellationToken cancellationToken)
    {
        var baseUrl = await ResolveBaseUrlAsync(
            _settings, SubtitleSourceSettingKeys.SubSource.BaseUrl, ProductionBaseUrl, cancellationToken);
        var context = CandidateCache.Resolve<CandidateContext>(candidateRef, ProviderKey);

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, baseUrl + "/api/getSub")
        {
            Content = SubtitleHttp.JsonBody(new { linkName = context.LinkName, language = context.SourceLanguage })
        };
        httpRequest.Headers.Add("Accept", "application/json");

        using var document = await SubtitleHttp.SendForJsonAsync(Http, httpRequest, ProviderKey, cancellationToken);
        // getSub answers with the subtitle text inside a JSON string array —
        // joined and re-encoded into the byte payload the ABI expects.
        if (!document.RootElement.TryGetProperty("sub", out var segments)
            || segments.ValueKind != JsonValueKind.Array
            || segments.GetArrayLength() == 0)
        {
            throw SubtitleProviderException.ForInvalidResponse(ProviderKey, "getSub returned no subtitle body.");
        }

        var text = string.Join("\n", segments.EnumerateArray()
            .Where(element => element.ValueKind == JsonValueKind.String)
            .Select(element => element.GetString()));
        var fileName = $"{context.LinkName}.{context.Language}.srt";

        return new SubtitleDownload(ProviderKey, fileName, "srt", Encoding.UTF8.GetBytes(text));
    }

    /// <inheritdoc />
    public async Task<SubtitleProviderTestOutcome> TestConnectionAsync(CancellationToken cancellationToken)
    {
        var baseUrl = await ResolveBaseUrlAsync(
            _settings, SubtitleSourceSettingKeys.SubSource.BaseUrl, ProductionBaseUrl, cancellationToken);

        using var request = new HttpRequestMessage(HttpMethod.Post, baseUrl + "/api/searchMovie")
        {
            Content = SubtitleHttp.JsonBody(new { queryName = "lingarr" })
        };
        request.Headers.Add("Accept", "application/json");

        using var _ = await SubtitleHttp.SendCheckedAsync(Http, request, ProviderKey, cancellationToken);
        return new SubtitleProviderTestOutcome(true, "SubSource reachable (no credentials required).");
    }

    private List<SubtitleCandidate> ParseCandidates(JsonDocument document, SubtitleSearchRequest request)
    {
        var candidates = new List<SubtitleCandidate>();
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw SubtitleProviderException.ForInvalidResponse(ProviderKey, "expected a JSON object.");
        }

        // found=false / missing subs array is a legitimate empty result.
        if (SubtitleHttp.GetBool(document.RootElement, "found") == false
            || !document.RootElement.TryGetProperty("subs", out var subs)
            || subs.ValueKind != JsonValueKind.Array)
        {
            return candidates;
        }

        foreach (var sub in subs.EnumerateArray())
        {
            if (sub.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var sourceLanguage = SubtitleHttp.GetString(sub, "lang") ?? SubtitleHttp.GetString(sub, "language");
            if (!MapLanguage(sourceLanguage, out var language)
                || !request.Languages.Contains(language, StringComparer.OrdinalIgnoreCase))
            {
                continue; // client-side language filter (⑩: English focus + supplements)
            }

            var release = SubtitleHttp.GetString(sub, "releaseName");
            var linkName = SubtitleHttp.GetString(sub, "linkName");
            if (string.IsNullOrEmpty(linkName))
            {
                continue;
            }

            var externalId = CandidateCache.Store(ProviderKey, new CandidateContext(linkName, sourceLanguage!, language));
            candidates.Add(new SubtitleCandidate(
                ProviderKey,
                externalId,
                language,
                MatchScore: 0,
                Forced: false,
                HearingImpaired: SubtitleHttp.GetBool(sub, "hearingImpaired"),
                ReleaseInfo: release,
                Uploader: SubtitleHttp.GetString(sub, "author"),
                Format: FormatFromFileName(release),
                Size: null, // the search response carries no payload size
                HashMatch: false));
        }

        return candidates;
    }

    private static bool MapLanguage(string? sourceLanguage, out string canonical)
    {
        if (!string.IsNullOrEmpty(sourceLanguage)
            && ResponseLanguageMap.TryGetValue(sourceLanguage, out var mapped))
        {
            canonical = mapped;
            return true;
        }

        canonical = string.Empty;
        return false;
    }

    /// <summary>Provider-side state a download needs for one SubSource entry.</summary>
    private sealed record CandidateContext(string LinkName, string SourceLanguage, string Language);
}
