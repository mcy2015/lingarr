using System.Text.Json;
using Lingarr.Contracts.Settings;
using Lingarr.Server.Modules.SubtitleSearch.Abstractions;

namespace Lingarr.Server.Modules.SubtitleSearch.Providers;

/// <summary>
/// SubSource v1 JSON API source (tech design ⑩, R4 hotfix 2026-10): the
/// upstream retired the keyless legacy routes and moved everything under
/// <c>/api/v1/</c> with a mandatory <c>api_key</c> query parameter (no key
/// answers 401). Two-step flow: resolve the title via
/// <c>GET /api/v1/movies/search</c> (IMDb id preferred when known —
/// <c>searchType=imdb</c> with <c>imdb={tt…}</c>, otherwise
/// <c>searchType=text</c> with the lowercased title in <c>q</c>; an IMDb
/// miss falls back to the text search), then list the
/// subtitles via <c>GET /api/v1/subtitles?language=&amp;limit=100&amp;movieId=</c>
/// (episodes append <c>seasonNumber</c>/<c>episodeNumber</c>). The
/// <c>language</c> parameter takes the source's own language names
/// (lowercase, e.g. <c>english</c>/<c>chinese_bg_code</c> — the naming of
/// the upstream language converter, observed via subliminal_patch's
/// subsource converter table; code implemented independently). A response
/// with <c>success=false</c> is a legitimate empty result. Download is
/// <c>GET /api/v1/subtitles/{id}/download</c>, which answers with the raw
/// subtitle file bytes — no JSON wrapper.
/// </summary>
[SubtitleProvider("subsource", requiresAuth: true)]
public sealed class SubSourceProvider : SubtitleProviderBase, ISubtitleProvider, ISubtitleProviderHealth
{
    private const string ProductionBaseUrl = "https://api.subsource.net";

    /// <summary>Per-source candidate budget of one subtitles page (QA1 P2-5).</summary>
    private const int MaxParsedSubtitles = 100;

    /// <summary>
    /// Canonical codes → the source's own language parameter names
    /// (lowercase, as the API expects them). Chinese is a single upload
    /// language on this source (<c>chinese_bg_code</c> — it does not
    /// distinguish simplified/traditional), so both zh forms map there.
    /// Names follow the upstream language-converter table observed via
    /// subliminal_patch's subsource converter.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> RequestLanguageMap =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["en"] = "english",
            ["zh"] = "chinese_bg_code",
            ["zh-TW"] = "chinese_bg_code",
            ["ja"] = "japanese",
            ["ko"] = "korean",
            ["fr"] = "french",
            ["de"] = "german",
            ["es"] = "spanish",
            ["it"] = "italian",
            ["pt"] = "portuguese",
            ["pt-BR"] = "brazilian_portuguese",
            ["ru"] = "russian",
            ["nl"] = "dutch",
            ["pl"] = "polish",
            ["tr"] = "turkish",
            ["th"] = "thai",
            ["vi"] = "vietnamese",
            ["id"] = "indonesian",
            ["ms"] = "malay",
            ["hi"] = "hindi",
            ["fa"] = "farsi_persian",
            ["uk"] = "ukrainian",
            ["cs"] = "czech",
            ["el"] = "greek",
            ["he"] = "hebrew",
            ["hu"] = "hungarian",
            ["no"] = "norwegian",
            ["da"] = "danish",
            ["fi"] = "finnish",
            ["sv"] = "swedish",
            ["ro"] = "romanian",
            ["bg"] = "bulgarian",
            ["hr"] = "croatian",
            ["sk"] = "slovak",
            ["sl"] = "slovenian"
        };

    /// <summary>
    /// Source language names → canonical codes (case-insensitive); used only
    /// to defensively drop items the server returns under a different
    /// language than the one queried. Chinese collapses onto the zh family —
    /// the candidate carries the code that was actually requested.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> ResponseLanguageMap =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["english"] = "en",
            ["chinese_bg_code"] = "zh",
            ["japanese"] = "ja",
            ["korean"] = "ko",
            ["french"] = "fr",
            ["german"] = "de",
            ["spanish"] = "es",
            ["italian"] = "it",
            ["portuguese"] = "pt",
            ["brazilian_portuguese"] = "pt",
            ["russian"] = "ru",
            ["dutch"] = "nl",
            ["polish"] = "pl",
            ["turkish"] = "tr",
            ["thai"] = "th",
            ["vietnamese"] = "vi",
            ["indonesian"] = "id",
            ["malay"] = "ms",
            ["hindi"] = "hi",
            ["farsi_persian"] = "fa",
            ["ukrainian"] = "uk",
            ["czech"] = "cs",
            ["greek"] = "el",
            ["hebrew"] = "he",
            ["hungarian"] = "hu",
            ["norwegian"] = "no",
            ["danish"] = "da",
            ["finnish"] = "fi",
            ["swedish"] = "sv",
            ["romanian"] = "ro",
            ["bulgarian"] = "bg",
            ["croatian"] = "hr",
            ["slovak"] = "sk",
            ["slovenian"] = "sl"
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
        var apiKey = await GetRequiredSecretAsync(
            _settings, SubtitleSourceSettingKeys.SubSource.ApiKey, "API key", cancellationToken);
        var baseUrl = await ResolveBaseUrlAsync(
            _settings, SubtitleSourceSettingKeys.SubSource.BaseUrl, ProductionBaseUrl, cancellationToken);

        // Step 1: resolve the source's movieId for the title (IMDb preferred,
        // text fallback — see FindMovieIdAsync). No id, no honest subtitles.
        var movieId = await FindMovieIdAsync(baseUrl, apiKey, request, cancellationToken);
        if (movieId is null)
        {
            return SubtitleSearchResult.Empty;
        }

        // Step 2: one subtitles page per requested language, using the
        // source's own language names.
        var candidates = new List<SubtitleCandidate>();
        foreach (var language in request.Languages)
        {
            if (!RequestLanguageMap.TryGetValue(language, out var sourceLanguage))
            {
                continue; // unmapped code: this source has no name for it.
            }

            try
            {
                var query = $"api/v1/subtitles?api_key={Uri.EscapeDataString(apiKey)}"
                    + $"&language={Uri.EscapeDataString(sourceLanguage)}"
                    + "&limit=100"
                    + $"&movieId={Uri.EscapeDataString(movieId)}";
                if (request.Season is int season && request.Episode is int episode)
                {
                    query += $"&seasonNumber={season}&episodeNumber={episode}";
                }

                using var httpRequest = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/{query}");
                httpRequest.Headers.Add("Accept", "application/json");

                using var document = await SubtitleHttp.SendForJsonAsync(
                    Http, httpRequest, ProviderKey, cancellationToken);
                candidates.AddRange(ParseCandidates(document, request, language));
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
        var apiKey = await GetRequiredSecretAsync(
            _settings, SubtitleSourceSettingKeys.SubSource.ApiKey, "API key", cancellationToken);
        var baseUrl = await ResolveBaseUrlAsync(
            _settings, SubtitleSourceSettingKeys.SubSource.BaseUrl, ProductionBaseUrl, cancellationToken);
        var context = CandidateCache.Resolve<CandidateContext>(candidateRef, ProviderKey);

        // v1 download answers with the subtitle file bytes themselves (the
        // legacy getSub JSON-string-array wrapper is gone) — the payload
        // passes through untouched; format detection stays in the M4 pipeline.
        using var httpRequest = new HttpRequestMessage(
            HttpMethod.Get,
            $"{baseUrl}/api/v1/subtitles/{Uri.EscapeDataString(context.SubtitleId)}/download"
            + $"?api_key={Uri.EscapeDataString(apiKey)}");
        var (content, headerFileName) = await SubtitleHttp.SendForBytesAsync(
            Http, httpRequest, ProviderKey, cancellationToken);
        var fileName = headerFileName ?? context.FileName ?? $"subsource.{context.Language}.srt";

        return new SubtitleDownload(ProviderKey, fileName, FormatFromFileName(fileName), content);
    }

    /// <inheritdoc />
    public async Task<SubtitleProviderTestOutcome> TestConnectionAsync(CancellationToken cancellationToken)
    {
        // No API key, no request: the v1 API answers 401 without one, so a
        // keyless probe can only ever fail — the missing-credential failure
        // with its settings-card guidance is the honest verdict (same
        // pattern as the OpenSubtitles/assrt probes).
        var apiKey = await GetRequiredSecretAsync(
            _settings, SubtitleSourceSettingKeys.SubSource.ApiKey, "API key", cancellationToken);
        var baseUrl = await ResolveBaseUrlAsync(
            _settings, SubtitleSourceSettingKeys.SubSource.BaseUrl, ProductionBaseUrl, cancellationToken);

        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"{baseUrl}/api/v1/movies/search?searchType=text&q=lingarr"
            + $"&api_key={Uri.EscapeDataString(apiKey)}");
        request.Headers.Add("Accept", "application/json");

        using var _ = await SubtitleHttp.SendCheckedAsync(Http, request, ProviderKey, cancellationToken);
        return new SubtitleProviderTestOutcome(true, "SubSource reachable; API key accepted.");
    }

    /// <summary>
    /// Resolves the source's <c>movieId</c>: an IMDb id is preferred
    /// (exact identity — <c>searchType=imdb</c> with the <c>imdb</c>
    /// parameter, first hit wins); an empty IMDb answer or no id at all
    /// falls back to a text search (<c>searchType=text</c> with the
    /// lowercased title in <c>q</c> — the upstream parameter names) whose
    /// hit must match the title (or its alternate title)
    /// case-insensitively and the year when both are known — a fuzzy hit
    /// would fetch subtitles for the wrong title, so no match answers null.
    /// </summary>
    private async Task<string?> FindMovieIdAsync(
        string baseUrl, string apiKey, SubtitleSearchRequest request, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(request.ImdbId))
        {
            var byImdb = await SearchMoviesAsync(
                baseUrl,
                $"searchType=imdb&imdb={Uri.EscapeDataString(request.ImdbId!.Trim())}",
                apiKey,
                cancellationToken);
            var imdbId = ReadMovieId(byImdb, index: 0);
            if (imdbId is not null)
            {
                return imdbId;
            }
            // IMDb miss: retry by text below (observed upstream behavior).
        }

        var byText = await SearchMoviesAsync(
            baseUrl,
            $"searchType=text&q={Uri.EscapeDataString(request.Title.ToLowerInvariant())}",
            apiKey,
            cancellationToken);
        foreach (var entry in EnumerateData(byText))
        {
            var name = SubtitleHttp.GetString(entry, "title");
            var alternate = SubtitleHttp.GetString(entry, "alternateTitle");
            if (!string.Equals(name, request.Title, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(alternate, request.Title, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (request.Year is int expected
                && SubtitleHttp.GetInt64(entry, "releaseYear") is { } actual
                && actual != expected)
            {
                continue; // right name, wrong year — a different release.
            }

            return ReadId(entry, "movieId");
        }

        return null;
    }

    /// <summary>One <c>GET /api/v1/movies/search</c> round with the given parameter pair.</summary>
    private async Task<JsonDocument> SearchMoviesAsync(
        string baseUrl, string searchParameters, string apiKey, CancellationToken cancellationToken)
    {
        using var httpRequest = new HttpRequestMessage(
            HttpMethod.Get, $"{baseUrl}/api/v1/movies/search?{searchParameters}&api_key={Uri.EscapeDataString(apiKey)}");
        httpRequest.Headers.Add("Accept", "application/json");

        return await SubtitleHttp.SendForJsonAsync(Http, httpRequest, ProviderKey, cancellationToken);
    }

    private List<SubtitleCandidate> ParseCandidates(
        JsonDocument document, SubtitleSearchRequest request, string queriedLanguage)
    {
        var candidates = new List<SubtitleCandidate>();
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw SubtitleProviderException.ForInvalidResponse(ProviderKey, "expected a JSON object.");
        }

        // success=false is the source's legitimate "nothing here" answer;
        // a missing data array is treated the same way.
        if (document.RootElement.TryGetProperty("success", out var success)
            && success.ValueKind == JsonValueKind.False)
        {
            return candidates;
        }

        foreach (var sub in EnumerateData(document))
        {
            if (candidates.Count >= MaxParsedSubtitles)
            {
                break; // QA1 P2-5: per-source candidate budget.
            }

            var subtitleId = ReadId(sub, "subtitleId");
            if (string.IsNullOrEmpty(subtitleId))
            {
                continue;
            }

            // Defensive language check: the page was queried for one
            // language; an item the server filed under a different one is
            // dropped rather than mislabeled. Families (zh-TW→zh,
            // pt-BR→pt) compare as their base code — the source uploads
            // Chinese as one language and the candidate carries the code
            // that was actually requested.
            var itemLanguage = SubtitleHttp.GetString(sub, "language");
            if (!string.IsNullOrEmpty(itemLanguage)
                && ResponseLanguageMap.TryGetValue(itemLanguage, out var canonical)
                && !request.Languages.Any(language =>
                    LanguageFamily(language) == LanguageFamily(canonical)))
            {
                continue;
            }

            // releaseInfo is the release-name list of the subtitle; joined
            // for display, the first name doubles as the file-name material.
            var releases = ReadReleaseNames(sub);
            var releaseInfo = releases.Count == 0 ? null : string.Join(", ", releases);
            var fileName = releases.Count == 0 ? null : $"{releases[0]}.{queriedLanguage.ToLowerInvariant()}.srt";

            var externalId = CandidateCache.Store(
                ProviderKey, new CandidateContext(subtitleId!, fileName, queriedLanguage));
            candidates.Add(new SubtitleCandidate(
                ProviderKey,
                externalId,
                queriedLanguage.ToLowerInvariant(),
                MatchScore: 0,
                Forced: SubtitleHttp.GetBool(sub, "foreignParts"),
                HearingImpaired: SubtitleHttp.GetBool(sub, "hearingImpaired"),
                ReleaseInfo: releaseInfo,
                Uploader: ReadUploader(sub),
                Format: FormatFromFileName(releases.Count == 0 ? null : releases[0]),
                Size: null, // the subtitles page carries no payload size
                HashMatch: false));
        }

        return candidates;
    }

    /// <summary>The base code of a language tag ("zh-TW" → "zh", "pt-BR" → "pt").</summary>
    private static string LanguageFamily(string code)
    {
        var separator = code.IndexOf('-');
        return (separator > 0 ? code[..separator] : code).ToLowerInvariant();
    }

    /// <summary>Uploader display name: the contributor whose id matches uploaderId.</summary>
    private static string? ReadUploader(JsonElement sub)
    {
        var uploaderId = ReadId(sub, "uploaderId");
        if (uploaderId is null
            || !sub.TryGetProperty("contributors", out var contributors)
            || contributors.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var contributor in contributors.EnumerateArray())
        {
            if (ReadId(contributor, "id") == uploaderId)
            {
                return SubtitleHttp.GetString(contributor, "displayname");
            }
        }

        return null;
    }

    /// <summary>
    /// The <c>releaseInfo</c> release names: a JSON array of strings
    /// upstream; a bare string is tolerated. Missing/other shapes answer
    /// empty.
    /// </summary>
    private static List<string> ReadReleaseNames(JsonElement sub)
    {
        var releases = new List<string>();
        if (sub.TryGetProperty("releaseInfo", out var releaseInfo))
        {
            if (releaseInfo.ValueKind == JsonValueKind.Array)
            {
                foreach (var release in releaseInfo.EnumerateArray())
                {
                    if (release.ValueKind == JsonValueKind.String
                        && !string.IsNullOrEmpty(release.GetString()))
                    {
                        releases.Add(release.GetString()!);
                    }
                }
            }
            else if (releaseInfo.ValueKind == JsonValueKind.String
                && !string.IsNullOrEmpty(releaseInfo.GetString()))
            {
                releases.Add(releaseInfo.GetString()!);
            }
        }

        return releases;
    }

    /// <summary>The <c>data</c> array of a v1 response; empty when absent or not an array.</summary>
    private static IEnumerable<JsonElement> EnumerateData(JsonDocument document)
    {
        if (document.RootElement.ValueKind == JsonValueKind.Object
            && document.RootElement.TryGetProperty("data", out var data)
            && data.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in data.EnumerateArray())
            {
                if (entry.ValueKind == JsonValueKind.Object)
                {
                    yield return entry;
                }
            }
        }
    }

    /// <summary>The movieId of the <c>data</c> entry at <paramref name="index"/>, null when absent.</summary>
    private static string? ReadMovieId(JsonDocument document, int index)
    {
        if (document.RootElement.ValueKind != JsonValueKind.Object
            || !document.RootElement.TryGetProperty("data", out var data)
            || data.ValueKind != JsonValueKind.Array
            || data.GetArrayLength() <= index)
        {
            return null;
        }

        return ReadId(data[index], "movieId");
    }

    /// <summary>
    /// String-or-number id access: the v1 wire shape has been observed with
    /// both kinds, and treating either as missing would drop honest hits.
    /// </summary>
    private static string? ReadId(JsonElement parent, string property)
    {
        if (parent.ValueKind != JsonValueKind.Object
            || !parent.TryGetProperty(property, out var element))
        {
            return null;
        }

        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.GetRawText(),
            _ => null
        };
    }

    /// <summary>Provider-side state a download needs for one SubSource subtitle.</summary>
    private sealed record CandidateContext(string SubtitleId, string? FileName, string Language);
}
