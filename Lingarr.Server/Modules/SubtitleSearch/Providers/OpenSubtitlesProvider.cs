using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using Lingarr.Contracts.Settings;
using Lingarr.Core;
using Lingarr.Server.Modules.SubtitleSearch.Abstractions;
using Lingarr.Server.Modules.SubtitleSearch.Orchestration;
using Lingarr.Server.Modules.SubtitleSearch.Scoring;

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

    /// <summary>
    /// Explicit page size of the /subtitles search (QA1 P2-5): without it
    /// the API decides the page size itself and a hot title can flood the
    /// candidate list; 50 aligns with the per-source candidate budget.
    /// </summary>
    private const int SearchPageSize = 50;

    /// <summary>
    /// Search-response language (ISO 639-3, e.g. "ita") → canonical two-letter
    /// code, so candidates match the codes the frontend offers from the
    /// culture table. Covers the ISO 639-3 codes (terminologic and
    /// bibliographic spellings) of every language that has a two-letter code;
    /// unknown codes (including three-letter ones outside the table) pass
    /// through lowercased unchanged rather than being dropped.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> ResponseLanguageMap =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["afr"] = "af",
            ["amh"] = "am",
            ["ara"] = "ar",
            ["aze"] = "az",
            ["bel"] = "be",
            ["ben"] = "bn",
            ["bos"] = "bs",
            ["bul"] = "bg",
            ["cat"] = "ca",
            ["ces"] = "cs",
            ["chi"] = "zh",
            ["cmn"] = "zh",
            ["cze"] = "cs",
            ["dan"] = "da",
            ["ell"] = "el",
            ["eng"] = "en",
            ["epo"] = "eo",
            ["est"] = "et",
            ["eus"] = "eu",
            ["baq"] = "eu",
            ["fas"] = "fa",
            ["per"] = "fa",
            ["fin"] = "fi",
            ["fra"] = "fr",
            ["fre"] = "fr",
            ["fry"] = "fy",
            ["ful"] = "ff",
            ["gla"] = "gd",
            ["gle"] = "ga",
            ["glg"] = "gl",
            ["glv"] = "gv",
            ["gre"] = "el",
            ["hau"] = "ha",
            ["heb"] = "he",
            ["hin"] = "hi",
            ["hrv"] = "hr",
            ["hun"] = "hu",
            ["hye"] = "hy",
            ["arm"] = "hy",
            ["ibo"] = "ig",
            ["ind"] = "id",
            ["isl"] = "is",
            ["ice"] = "is",
            ["ita"] = "it",
            ["jpn"] = "ja",
            ["kat"] = "ka",
            ["geo"] = "ka",
            ["kaz"] = "kk",
            ["khm"] = "km",
            ["kin"] = "rw",
            ["kir"] = "ky",
            ["kor"] = "ko",
            ["kur"] = "ku",
            ["lao"] = "lo",
            ["lav"] = "lv",
            ["lin"] = "ln",
            ["lit"] = "lt",
            ["lug"] = "lg",
            ["mal"] = "ml",
            ["mar"] = "mr",
            ["mkd"] = "mk",
            ["mac"] = "mk",
            ["mlt"] = "mt",
            ["mon"] = "mn",
            ["mri"] = "mi",
            ["msa"] = "ms",
            ["may"] = "ms",
            ["mya"] = "my",
            ["bur"] = "my",
            ["nep"] = "ne",
            ["nld"] = "nl",
            ["dut"] = "nl",
            ["nno"] = "nn",
            ["nob"] = "nb",
            ["nor"] = "no",
            ["nya"] = "ny",
            ["orm"] = "om",
            ["oss"] = "os",
            ["pan"] = "pa",
            ["pol"] = "pl",
            ["por"] = "pt",
            ["ron"] = "ro",
            ["rum"] = "ro",
            ["rus"] = "ru",
            ["sin"] = "si",
            ["slk"] = "sk",
            ["slo"] = "sk",
            ["slv"] = "sl",
            ["sme"] = "se",
            ["sna"] = "sn",
            ["som"] = "so",
            ["sot"] = "st",
            ["sqi"] = "sq",
            ["alb"] = "sq",
            ["srp"] = "sr",
            ["sun"] = "su",
            ["swa"] = "sw",
            ["swe"] = "sv",
            ["tam"] = "ta",
            ["tat"] = "tt",
            ["tel"] = "te",
            ["tgk"] = "tg",
            ["tgl"] = "tl",
            ["tha"] = "th",
            ["tir"] = "ti",
            ["tuk"] = "tk",
            ["tur"] = "tr",
            ["uig"] = "ug",
            ["ukr"] = "uk",
            ["urd"] = "ur",
            ["uzb"] = "uz",
            ["vie"] = "vi",
            ["yid"] = "yi",
            ["yor"] = "yo",
            ["zha"] = "za",
            ["zho"] = "zh"
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

    // ── Daily download quota observability (R3) ─────────────────────────
    //
    // The /download responses carry the quota state as three plain response
    // headers (official contract): `requests` (counter), `remaining`
    // (downloads left) and `reset_time_utc` (the UTC refill moment; the
    // window rolls over at UTC midnight). Header-name lookup is
    // case-insensitive by HttpHeaders contract, so real-world casings like
    // `Remaining` resolve alike. The provider is transient (typed client,
    // scoped settings) — the snapshot MUST live in the singleton
    // SubtitleQuotaStore to be observable from the providers endpoint.

    private const string QuotaRemainingHeader = "remaining";
    private const string QuotaRequestsHeader = "requests";
    private const string QuotaResetHeader = "reset_time_utc";

    private readonly ISettingsAccess _settings;
    private readonly SubtitleQuotaStore _quotaStore;

    public OpenSubtitlesProvider(
        ISettingsAccess settings,
        SubtitleCandidateCache candidateCache,
        SubtitleQuotaStore quotaStore,
        HttpClient httpClient,
        ILogger<OpenSubtitlesProvider> logger)
        : base("opensubtitles", httpClient, candidateCache, logger)
    {
        _settings = settings;
        _quotaStore = quotaStore;
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
        // Trust assumption (QA1 P2 note): a movie_hashes query is scoped
        // server-side to that exact file hash, so marking every returned
        // candidate HashMatch=true is safe by construction — the local hash
        // came from the media file itself (LazyMediaHasher), no title/year
        // inference involved.
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
            query = $"subtitles?query={Uri.EscapeDataString(KeywordOf(request))}&languages={languages}";
        }

        if (request.Season is int season && request.Episode is int episode && !hashMatch)
        {
            query += $"&season={season}&episode={episode}";
        }

        query += $"&per_page={SearchPageSize}";

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

        // Step 1: exchange the file id for a short-lived download link. The
        // quota headers ride on THIS response (official /download contract);
        // they are captured before the plumbing disposes the response, and a
        // quota-exhaustion verdict (406 with remaining:0) is mirrored into the
        // snapshot as remaining=0 before rethrowing — the providers endpoint
        // then shows the stall alongside the cooldown chip.
        using var downloadRequest = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/download")
        {
            Content = SubtitleHttp.JsonBody(new { file_id = context.FileId })
        };
        ApplyHeaders(downloadRequest, apiKey);

        using var downloadDocument = await SendDownloadDocumentAsync(downloadRequest, cancellationToken);
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
    /// Sends the <c>POST /download</c> exchange and captures the quota state
    /// of its response. Success path: the three quota headers are read
    /// (case-insensitively) into the singleton snapshot — a response without
    /// any parseable quota header leaves the last snapshot untouched (partial
    /// captures are fine: only the parseable parts update). Failure path: the
    /// shared plumbing already maps a quota exhaustion (429/406 with a zero
    /// remaining header) onto the DailyLimit exception carrying the parsed
    /// <c>reset_time_utc</c>; the verdict is mirrored into the snapshot as
    /// remaining=0 before the exception propagates to the caller.
    /// </summary>
    private async Task<JsonDocument> SendDownloadDocumentAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        try
        {
            return await SubtitleHttp.SendForJsonAsync(
                Http, request, ProviderKey, cancellationToken, onResponse: CaptureQuotaSnapshot);
        }
        catch (SubtitleProviderException ex) when (ex.Reason == SubtitleProviderFailureReason.DailyLimit)
        {
            _quotaStore.Record(ProviderKey, new SubtitleQuotaSnapshot(
                Remaining: 0,
                Total: null,
                Requests: null,
                ResetUtc: ex.QuotaResetUtc,
                CapturedAtUtc: DateTimeOffset.UtcNow));
            throw;
        }
    }

    /// <summary>
    /// Reads the /download quota headers into the singleton snapshot.
    /// All-parse-failed (or absent) headers keep the previous snapshot —
    /// garbage never wipes a good observation.
    /// </summary>
    private void CaptureQuotaSnapshot(HttpResponseMessage response)
    {
        var remaining = ReadHeaderInt(response, QuotaRemainingHeader);
        var requests = ReadHeaderInt(response, QuotaRequestsHeader);
        var resetUtc = ReadHeaderDateTimeOffset(response, QuotaResetHeader);
        if (remaining is null && requests is null && resetUtc is null)
        {
            return;
        }

        _quotaStore.Record(ProviderKey, new SubtitleQuotaSnapshot(
            remaining, Total: null, requests, resetUtc, DateTimeOffset.UtcNow));
        if (Logger.IsEnabled(LogLevel.Debug))
        {
            Logger.LogDebug(
                "OpenSubtitles quota snapshot: remaining={Remaining}, requests={Requests}, resets at {Reset}.",
                remaining, requests, resetUtc);
        }
    }

    /// <summary>First parseable integer of a response header, null when absent/unparsable.</summary>
    private static int? ReadHeaderInt(HttpResponseMessage response, string name)
        => response.Headers.TryGetValues(name, out var values)
           && int.TryParse(values.FirstOrDefault(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;

    /// <summary>First parseable timestamp of a response header (naive values read as UTC), null when absent/unparsable.</summary>
    private static DateTimeOffset? ReadHeaderDateTimeOffset(HttpResponseMessage response, string name)
        => response.Headers.TryGetValues(name, out var values)
           && DateTimeOffset.TryParse(
               values.FirstOrDefault(),
               CultureInfo.InvariantCulture,
               DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
               out var parsed)
            ? parsed
            : null;

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
            // QA1 P2-3: `release` is the documented attribute, but some
            // deployments/fixtures only populate `release_notes` — read both.
            var releaseAttr = SubtitleHttp.GetString(attributes, "release");
            var releaseInfo = string.IsNullOrWhiteSpace(releaseAttr)
                ? SubtitleHttp.GetString(attributes, "release_notes")
                : releaseAttr;

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

    /// <summary>
    /// The keyword this source searches for: the locked <c>Query</c> override
    /// (R3b — a trailing SxxEyy/NxMM tag is stripped from it; the structured
    /// season/episode travels as its own parameters) or the library title.
    /// </summary>
    private static string KeywordOf(SubtitleSearchRequest request)
        => string.IsNullOrWhiteSpace(request.Query)
            ? request.Title
            : SubtitleMatchScorer.SplitTrailingSeasonEpisode(request.Query.Trim()).Keyword;

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
