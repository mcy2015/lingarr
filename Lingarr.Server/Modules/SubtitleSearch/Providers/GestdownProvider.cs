using System.Text.Json;
using Lingarr.Contracts.Settings;
using Lingarr.Server.Modules.SubtitleSearch.Abstractions;
using Lingarr.Server.Modules.SubtitleSearch.Scoring;

namespace Lingarr.Server.Modules.SubtitleSearch.Providers;

/// <summary>
/// Gestdown source (tech design ⑩): <c>gestdown.info</c> HTTP API, free, no
/// key — an API-fronted mirror of the Addic7ed catalogue and therefore an
/// episode-only EN specialist. The lookup path follows the public API:
/// resolve the series by title search (<c>/shows/search</c>, exact name
/// preferred), then fetch one season/episode/language page
/// (<c>/subtitles/get/{showId}/{season}/{episode}/{language}</c> — HTTP 404
/// means "no subtitles for that episode", a legitimate empty result). Movie
/// requests return empty: the catalogue has none. Download follows the
/// short-lived <c>/subtitles/download/{id}</c> link to the raw srt.
/// </summary>
[SubtitleProvider("gestdown")]
public sealed class GestdownProvider : SubtitleProviderBase, ISubtitleProvider, ISubtitleProviderHealth
{
    private const string ProductionBaseUrl = "https://api.gestdown.info";

    private readonly ISettingsAccess _settings;

    public GestdownProvider(
        ISettingsAccess settings,
        SubtitleCandidateCache candidateCache,
        HttpClient httpClient,
        ILogger<GestdownProvider> logger)
        : base("gestdown", httpClient, candidateCache, logger)
    {
        _settings = settings;
    }

    public string Provider => ProviderKey;

    /// <inheritdoc />
    public async Task<SubtitleSearchResult> SearchAsync(SubtitleSearchRequest request, CancellationToken cancellationToken)
    {
        // Episode-only catalogue by design (⑩) — movies answer empty, the
        // orchestrator's other sources cover them.
        if (request.Season is not int season || request.Episode is not int episode)
        {
            return SubtitleSearchResult.Empty;
        }

        var baseUrl = await ResolveBaseUrlAsync(
            _settings, SubtitleSourceSettingKeys.Gestdown.BaseUrl, ProductionBaseUrl, cancellationToken);

        // R3b: the series lookup keys on the show name — a Query override
        // carries its trailing SxxEyy tag stripped (the exact-name match
        // must not be degraded by an episode suffix; the structured
        // season/episode go into the per-episode page path below).
        var showKeyword = string.IsNullOrWhiteSpace(request.Query)
            ? request.Title
            : SubtitleMatchScorer.SplitTrailingSeasonEpisode(request.Query.Trim()).Keyword;
        var showId = await FindShowIdAsync(baseUrl, showKeyword, cancellationToken);
        if (showId is null)
        {
            return SubtitleSearchResult.Empty;
        }

        var candidates = new List<SubtitleCandidate>();
        foreach (var language in request.Languages)
        {
            try
            {
                var path = $"/subtitles/get/{showId}/{season}/{episode}/{MapRequestLanguage(language)}";
                using var httpRequest = new HttpRequestMessage(HttpMethod.Get, baseUrl + path);
                httpRequest.Headers.Add("Accept", "application/json");

                using var document = await SendForJsonToleratingEpisodeMissAsync(httpRequest, cancellationToken);
                if (document is null)
                {
                    continue;
                }

                using var _ = document;
                candidates.AddRange(ParseCandidates(document, baseUrl, language));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Budget exhausted mid-loop: earlier languages' candidates
                // stay — a cancelled tail must not discard them (QA1 P3).
                break;
            }
        }

        return new SubtitleSearchResult(candidates);
    }

    /// <inheritdoc />
    public async Task<SubtitleDownload> DownloadAsync(SubtitleCandidateRef candidateRef, CancellationToken cancellationToken)
    {
        var context = CandidateCache.Resolve<CandidateContext>(candidateRef, ProviderKey);

        // Short-lived link resolved at search time; srt payload, no archive.
        using var httpRequest = new HttpRequestMessage(HttpMethod.Get, context.DownloadUrl);
        var (content, headerFileName) = await SubtitleHttp.SendForBytesAsync(
            Http, httpRequest, ProviderKey, cancellationToken);
        var fileName = headerFileName ?? $"gestdown.{context.Language}.srt";

        return new SubtitleDownload(ProviderKey, fileName, FormatFromFileName(fileName), content);
    }

    /// <inheritdoc />
    public async Task<SubtitleProviderTestOutcome> TestConnectionAsync(CancellationToken cancellationToken)
    {
        var baseUrl = await ResolveBaseUrlAsync(
            _settings, SubtitleSourceSettingKeys.Gestdown.BaseUrl, ProductionBaseUrl, cancellationToken);

        using var request = new HttpRequestMessage(HttpMethod.Get, baseUrl + "/shows/search?search=lingarr");
        request.Headers.Add("Accept", "application/json");

        using var _ = await SubtitleHttp.SendCheckedAsync(Http, request, ProviderKey, cancellationToken);
        return new SubtitleProviderTestOutcome(true, "Gestdown reachable (no credentials required).");
    }

    /// <summary>
    /// Sends one subtitles/get request; a documented HTTP 404 ("no subtitles
    /// for this episode", a normal miss on this source) yields null instead
    /// of an exception.
    /// </summary>
    private async Task<JsonDocument?> SendForJsonToleratingEpisodeMissAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        try
        {
            return await SubtitleHttp.SendForJsonAsync(Http, request, ProviderKey, cancellationToken);
        }
        catch (SubtitleProviderException ex) when (ex.HttpStatus == 404)
        {
            return null;
        }
    }

    /// <summary>
    /// Resolves the series id: exact (case-insensitive) name match first,
    /// otherwise the first hit. Null when the search yields nothing.
    /// </summary>
    private async Task<string?> FindShowIdAsync(string baseUrl, string title, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get, $"{baseUrl}/shows/search?search={Uri.EscapeDataString(title)}");
        request.Headers.Add("Accept", "application/json");

        using var document = await SubtitleHttp.SendForJsonAsync(Http, request, ProviderKey, cancellationToken);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw SubtitleProviderException.ForInvalidResponse(ProviderKey, "show search did not return an array.");
        }

        JsonElement best = default;
        foreach (var show in document.RootElement.EnumerateArray())
        {
            if (show.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var name = SubtitleHttp.GetString(show, "name");
            if (string.Equals(name, title, StringComparison.OrdinalIgnoreCase))
            {
                return SubtitleHttp.GetString(show, "uuid") ?? SubtitleHttp.GetString(show, "id");
            }

            best = best.ValueKind == JsonValueKind.Undefined ? show : best;
        }

        return best.ValueKind == JsonValueKind.Undefined
            ? null
            : SubtitleHttp.GetString(best, "uuid") ?? SubtitleHttp.GetString(best, "id");
    }

    private List<SubtitleCandidate> ParseCandidates(JsonDocument document, string baseUrl, string language)
    {
        var candidates = new List<SubtitleCandidate>();
        if (!document.RootElement.TryGetProperty("subtitles", out var subtitles)
            || subtitles.ValueKind != JsonValueKind.Array)
        {
            throw SubtitleProviderException.ForInvalidResponse(ProviderKey, "missing subtitles array.");
        }

        foreach (var subtitle in subtitles.EnumerateArray())
        {
            if (subtitle.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var subtitleId = SubtitleHttp.GetString(subtitle, "uuid") ?? SubtitleHttp.GetString(subtitle, "id");
            if (string.IsNullOrEmpty(subtitleId))
            {
                continue;
            }

            // "version" is the Addic7ed-style release descriptor
            // ("720p.WEB-DL"), the strongest match fact this source exposes.
            var version = SubtitleHttp.GetString(subtitle, "version");
            var downloadUrl = $"{baseUrl}/subtitles/download/{subtitleId}";
            var externalId = CandidateCache.Store(
                ProviderKey, new CandidateContext(downloadUrl, language, subtitleId));

            candidates.Add(new SubtitleCandidate(
                ProviderKey,
                externalId,
                language.ToLowerInvariant(),
                MatchScore: 0,
                Forced: SubtitleHttp.GetBool(subtitle, "forced"),
                HearingImpaired: SubtitleHttp.GetBool(subtitle, "hearingImpaired"),
                ReleaseInfo: version,
                Uploader: null, // the API exposes no uploader
                Format: "srt",
                Size: null,
                HashMatch: false));
        }

        return candidates;
    }

    private static string MapRequestLanguage(string canonical)
        => canonical.ToLowerInvariant() switch
        {
            "zh" or "zh-tw" => "zh",
            var code => code
        };

    /// <summary>Provider-side state a download needs for one Gestdown subtitle.</summary>
    private sealed record CandidateContext(string DownloadUrl, string Language, string SubtitleId);
}
