using System.Text.Json;
using Lingarr.Contracts.Settings;
using Lingarr.Server.Modules.SubtitleSearch.Abstractions;

namespace Lingarr.Server.Modules.SubtitleSearch.Providers;

/// <summary>
/// assrt.net source (tech design ⑩): <c>api.assrt.net/v1</c>, token
/// authentication — the Chinese-subtitle mainstay of the v1 source list.
/// Search is <c>GET /sub/search?q=</c>; the id then resolves a file list via
/// <c>GET /sub/detail?id=</c> whose entries carry the direct file URLs. The
/// API reports errors as <c>status != 0</c> inside an HTTP 200 body — those
/// surface as InvalidResponse failures with the source's message. Candidate
/// language defaults to the primary requested code: assrt's search is
/// Chinese-focused and its per-sub language tags are unreliable.
/// </summary>
[SubtitleProvider("assrt", requiresAuth: true)]
public sealed class AssrtProvider : SubtitleProviderBase, ISubtitleProvider, ISubtitleProviderHealth
{
    private const string ProductionBaseUrl = "https://api.assrt.net/v1";

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
            ? $"{request.Title} S{season:00}E{episode:00}"
            : $"{request.Title}{(request.Year is int year ? $" {year}" : string.Empty)}";

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

        // The file list needs one detail round per sub id; the search result
        // set is naturally small and the detail call is the only way to the
        // file URLs.
        var candidates = new List<SubtitleCandidate>();
        foreach (var subId in subIds)
        {
            using var detailRequest = new HttpRequestMessage(
                HttpMethod.Get, $"{baseUrl}/sub/detail?id={subId}&token={Uri.EscapeDataString(token)}");
            detailRequest.Headers.Add("Accept", "application/json");

            using var detailDocument = await SubtitleHttp.SendForJsonAsync(
                Http, detailRequest, ProviderKey, cancellationToken);
            candidates.AddRange(ParseDetail(detailDocument, subId, request));
        }

        return new SubtitleSearchResult(candidates);
    }

    /// <inheritdoc />
    public async Task<SubtitleDownload> DownloadAsync(SubtitleCandidateRef candidateRef, CancellationToken cancellationToken)
    {
        var context = CandidateCache.Resolve<CandidateContext>(candidateRef, ProviderKey);

        using var httpRequest = new HttpRequestMessage(HttpMethod.Get, context.FileUrl);
        var (content, headerFileName) = await SubtitleHttp.SendForBytesAsync(
            Http, httpRequest, ProviderKey, cancellationToken);
        var fileName = headerFileName ?? context.FileName ?? "assrt.ass";

        return new SubtitleDownload(ProviderKey, fileName, FormatFromFileName(fileName), content);
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
                var id = SubtitleHttp.GetInt64(entry, "id");
                if (id is > 0)
                {
                    subIds.Add(id.Value);
                }
            }
        }

        return subIds;
    }

    private List<SubtitleCandidate> ParseDetail(JsonDocument document, long subId, SubtitleSearchRequest request)
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
        var language = request.Languages.FirstOrDefault()?.ToLowerInvariant() ?? "zh";

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
                    ProviderKey, new CandidateContext(fileUrl!, fileName, subId));
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
                    ProviderKey, new CandidateContext(fileUrl!, null, subId));
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

    /// <summary>Provider-side state a download needs for one assrt file entry.</summary>
    private sealed record CandidateContext(string FileUrl, string? FileName, long SubId);
}
