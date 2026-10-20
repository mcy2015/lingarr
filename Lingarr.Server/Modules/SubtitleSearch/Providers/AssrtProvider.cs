using System.Text.Json;
using Lingarr.Contracts.Settings;
using Lingarr.Server.Modules.SubtitleSearch.Abstractions;
using Lingarr.Server.Modules.SubtitleSearch.Scoring;

namespace Lingarr.Server.Modules.SubtitleSearch.Providers;

/// <summary>
/// assrt.net source (tech design ⑩): <c>api.assrt.net/v1</c>, token
/// authentication — the Chinese-subtitle mainstay of the v1 source list.
/// Search is <c>GET /sub/search?q=</c>; the id then resolves a file list via
/// <c>GET /sub/detail?id=</c> whose entries carry the direct file URLs. The
/// API reports errors as <c>status != 0</c> inside an HTTP 200 body — those
/// surface as InvalidResponse failures with the source's message. Candidates
/// are always labelled <c>zh</c>: the search has no language parameter, the
/// catalogue is Chinese, and the per-sub language tags are unreliable.
/// </summary>
[SubtitleProvider("assrt", requiresAuth: true)]
public sealed class AssrtProvider : SubtitleProviderBase, ISubtitleProvider, ISubtitleProviderHealth
{
    private const string ProductionBaseUrl = "https://api.assrt.net/v1";

    /// <summary>
    /// Upper bound of sub ids taken from one search page (QA1 P2-5): the
    /// per-source candidate budget — a hot title page must not translate
    /// into an unbounded id list.
    /// </summary>
    private const int MaxParsedSubIds = 50;

    /// <summary>
    /// Detail-round fan-out cap (QA1 P1-1): one HTTP round per sub id is
    /// serial IO; popular titles blew the 10s orchestrator budget long
    /// before reaching the tail of the list.
    /// </summary>
    private const int MaxDetailFanOut = 20;

    /// <summary>
    /// Wait before the single throttle retry of a file download. assrt
    /// answers the file fetch with non-standard 493/509 as a transient
    /// per-token throttle (Bazarr #802/#1953) — one patient retry rides out
    /// the window; a persistent throttle then surfaces as RateLimited with
    /// a one-minute cooldown. Public static settable for tests (the
    /// "public test seams, no InternalsVisibleTo" ruling).
    /// </summary>
    public static TimeSpan ThrottleRetryDelay = TimeSpan.FromSeconds(8);

    /// <summary>
    /// The fixed language label of every assrt candidate: the search API has
    /// no language parameter and its catalogue is Chinese, so the label is
    /// the honest constant regardless of what the request asked for.
    /// </summary>
    private const string CandidateLanguage = "zh";

    private readonly ISettingsAccess _settings;

    public AssrtProvider(
        ISettingsAccess settings,
        SubtitleCandidateCache candidateCache,
        HttpClient httpClient,
        ILogger<AssrtProvider> logger)
        : base("assrt", httpClient, candidateCache, logger)
    {
        _settings = settings;
    }

    public string Provider => ProviderKey;

    /// <inheritdoc />
    public async Task<SubtitleSearchResult> SearchAsync(SubtitleSearchRequest request, CancellationToken cancellationToken)
    {
        var token = await GetRequiredSecretAsync(
            _settings, SubtitleSourceSettingKeys.Assrt.Token, "token", cancellationToken);
        var baseUrl = await ResolveBaseUrlAsync(
            _settings, SubtitleSourceSettingKeys.Assrt.BaseUrl, ProductionBaseUrl, cancellationToken);

        // Episode queries include the SxxEyy pattern — assrt's index matches
        // it in the subtitle title/native name.
        var keywords = request.Season is int season && request.Episode is int episode
            ? $"{KeywordOf(request)} S{season:00}E{episode:00}"
            : $"{KeywordOf(request)}{(request.Year is int year ? $" {year}" : string.Empty)}";

        using var searchRequest = new HttpRequestMessage(
            HttpMethod.Get, $"{baseUrl}/sub/search?q={Uri.EscapeDataString(keywords)}&token={Uri.EscapeDataString(token)}");
        searchRequest.Headers.Add("Accept", "application/json");

        using var searchDocument = await SubtitleHttp.SendForJsonAsync(
            Http, searchRequest, ProviderKey, cancellationToken);
        var subIds = ParseSearchResult(searchDocument);
        if (subIds.Count == 0)
        {
            return SubtitleSearchResult.Empty;
        }

        // The file list needs one detail round per sub id — the detail call
        // is the only way to the file URLs — capped at MaxDetailFanOut so a
        // hot title cannot blow the round budget on serial IO (QA1 P1-1).
        var candidates = new List<SubtitleCandidate>();
        foreach (var subId in subIds.Take(MaxDetailFanOut))
        {
            try
            {
                using var detailRequest = new HttpRequestMessage(
                    HttpMethod.Get, $"{baseUrl}/sub/detail?id={subId}&token={Uri.EscapeDataString(token)}");
                detailRequest.Headers.Add("Accept", "application/json");

                using var detailDocument = await SubtitleHttp.SendForJsonAsync(
                    Http, detailRequest, ProviderKey, cancellationToken);
                candidates.AddRange(ParseDetail(detailDocument, subId));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Budget exhausted mid-fan-out (the orchestrator's 10s token
                // fires): keep the candidates the earlier detail rounds
                // already accumulated instead of discarding them (QA1 P1-1).
                break;
            }
        }

        return new SubtitleSearchResult(candidates);
    }

    /// <inheritdoc />
    public async Task<SubtitleDownload> DownloadAsync(SubtitleCandidateRef candidateRef, CancellationToken cancellationToken)
    {
        var context = CandidateCache.Resolve<CandidateContext>(candidateRef, ProviderKey);

        // The file fetch is where assrt throttles (493/509, transient
        // per-token): one patient retry before the verdict surfaces — a
        // fresh HttpRequestMessage per attempt, they are single-use.
        for (var attempt = 0; ; attempt++)
        {
            using var httpRequest = new HttpRequestMessage(HttpMethod.Get, context.FileUrl);
            try
            {
                var (content, headerFileName) = await SubtitleHttp.SendForBytesAsync(
                    Http, httpRequest, ProviderKey, cancellationToken);
                var fileName = headerFileName ?? context.FileName ?? "assrt.ass";

                // The quota snapshot captured by the detail round that produced this
                // candidate rides along — the download pipeline feeds it into the
                // provider_quota_usage telemetry (the quotaRemaining channel).
                return new SubtitleDownload(ProviderKey, fileName, FormatFromFileName(fileName), content, context.QuotaRemaining);
            }
            catch (SubtitleProviderException ex) when (attempt == 0 && ex.HttpStatus is 493 or 509)
            {
                await Task.Delay(ThrottleRetryDelay, cancellationToken);
            }
        }
    }

    /// <inheritdoc />
    public async Task<SubtitleProviderTestOutcome> TestConnectionAsync(CancellationToken cancellationToken)
    {
        var token = await GetRequiredSecretAsync(
            _settings, SubtitleSourceSettingKeys.Assrt.Token, "token", cancellationToken);
        var baseUrl = await ResolveBaseUrlAsync(
            _settings, SubtitleSourceSettingKeys.Assrt.BaseUrl, ProductionBaseUrl, cancellationToken);

        using var request = new HttpRequestMessage(
            HttpMethod.Get, $"{baseUrl}/sub/search?q=lingarr&token={Uri.EscapeDataString(token)}");
        request.Headers.Add("Accept", "application/json");

        using var _ = await SubtitleHttp.SendCheckedAsync(Http, request, ProviderKey, cancellationToken);
        return new SubtitleProviderTestOutcome(true, "assrt reachable; token accepted.");
    }

    private List<long> ParseSearchResult(JsonDocument document)
    {
        EnsureOk(document, "sub/search");
        var subIds = new List<long>();
        if (document.RootElement.TryGetProperty("sub", out var sub)
            && sub.ValueKind == JsonValueKind.Object
            && sub.TryGetProperty("subs", out var subs)
            && subs.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in subs.EnumerateArray())
            {
                if (subIds.Count >= MaxParsedSubIds)
                {
                    break; // QA1 P2-5: per-source candidate budget
                }

                var id = SubtitleHttp.GetInt64(entry, "id");
                if (id is > 0)
                {
                    subIds.Add(id.Value);
                }
            }
        }

        return subIds;
    }

    private List<SubtitleCandidate> ParseDetail(JsonDocument document, long subId)
    {
        EnsureOk(document, "sub/detail");
        var candidates = new List<SubtitleCandidate>();
        if (!document.RootElement.TryGetProperty("sub", out var sub)
            || sub.ValueKind != JsonValueKind.Object
            || !sub.TryGetProperty("subs", out var subs)
            || subs.ValueKind != JsonValueKind.Array
            || subs.GetArrayLength() == 0)
        {
            return candidates;
        }

        // The detail round answers an array with the requested sub first.
        var entry = subs[0];
        if (entry.ValueKind != JsonValueKind.Object)
        {
            return candidates;
        }

        var title = SubtitleHttp.GetString(entry, "title");
        var nativeName = SubtitleHttp.GetString(entry, "native_name");
        var release = !string.IsNullOrWhiteSpace(nativeName) ? nativeName : title;
        // The search has no language parameter and assrt's catalogue is
        // Chinese-focused: the label must stay "zh" instead of echoing the
        // requested code — any-language manual searches used to mark their
        // results with whatever code was asked for.
        var language = CandidateLanguage;

        // Official remaining-quota field of the detail response (assrt v1
        // reports the account's remaining downloads as sub.quota_remain;
        // tolerated at the root too because deployments differ — absent
        // simply keeps null, the telemetry then preserves its last value).
        var quotaRemaining = ReadQuotaRemaining(document, sub);

        if (entry.TryGetProperty("filelist", out var filelist) && filelist.ValueKind == JsonValueKind.Array)
        {
            foreach (var file in filelist.EnumerateArray())
            {
                var fileUrl = SubtitleHttp.GetString(file, "url");
                var fileName = SubtitleHttp.GetString(file, "f");
                if (string.IsNullOrEmpty(fileUrl))
                {
                    continue;
                }

                var externalId = CandidateCache.Store(
                    ProviderKey, new CandidateContext(fileUrl!, fileName, subId, quotaRemaining));
                candidates.Add(new SubtitleCandidate(
                    ProviderKey,
                    externalId,
                    language,
                    MatchScore: 0,
                    Forced: false,
                    HearingImpaired: false,
                    ReleaseInfo: release,
                    Uploader: null, // assrt exposes no uploader on the file entry
                    Format: FormatFromFileName(fileName),
                    Size: null,
                    HashMatch: false));
            }
        }
        else
        {
            // No filelist: the sub itself carries a direct URL.
            var fileUrl = SubtitleHttp.GetString(entry, "url");
            if (!string.IsNullOrEmpty(fileUrl))
            {
                var externalId = CandidateCache.Store(
                    ProviderKey, new CandidateContext(fileUrl!, null, subId, quotaRemaining));
                candidates.Add(new SubtitleCandidate(
                    ProviderKey,
                    externalId,
                    language,
                    MatchScore: 0,
                    Forced: false,
                    HearingImpaired: false,
                    ReleaseInfo: release,
                    Uploader: null,
                    Format: "ass",
                    Size: null,
                    HashMatch: false));
            }
        }

        return candidates;
    }

    /// <summary>
    /// The remaining-quota value of one detail response, null when the
    /// response carries none. Assumption (documented in the batch report):
    /// assrt's v1 API exposes the account's remaining download quota as
    /// <c>sub.quota_remain</c> on the detail round; the root-object spelling
    /// is read as a tolerated fallback.
    /// </summary>
    private static int? ReadQuotaRemaining(JsonDocument document, JsonElement sub)
    {
        var remain = SubtitleHttp.GetInt64(sub, "quota_remain")
                     ?? SubtitleHttp.GetInt64(document.RootElement, "quota_remain");
        return remain is >= 0 and <= int.MaxValue ? (int)remain : null;
    }

    /// <summary>assrt reports failures as status != 0 inside HTTP 200 bodies — turn those into exceptions.</summary>
    private void EnsureOk(JsonDocument document, string endpoint)
    {
        if (document.RootElement.ValueKind == JsonValueKind.Object
            && document.RootElement.TryGetProperty("status", out var status)
            && status.ValueKind == JsonValueKind.Number
            && status.GetInt32() != 0)
        {
            var message = SubtitleHttp.GetString(document.RootElement, "message") ?? "unknown error";
            throw SubtitleProviderException.ForInvalidResponse(ProviderKey, $"{endpoint} failed: {message}.");
        }
    }

    /// <summary>
    /// The keyword this source searches for: the locked <c>Query</c> override
    /// (R3b — a trailing SxxEyy/NxMM tag is stripped from it, the structured
    /// season/episode is appended above) or the library-derived title.
    /// </summary>
    private static string KeywordOf(SubtitleSearchRequest request)
        => string.IsNullOrWhiteSpace(request.Query)
            ? request.Title
            : SubtitleMatchScorer.SplitTrailingSeasonEpisode(request.Query.Trim()).Keyword;

    /// <summary>Provider-side state a download needs for one assrt file entry.</summary>
    private sealed record CandidateContext(string FileUrl, string? FileName, long SubId, int? QuotaRemaining = null);
}
