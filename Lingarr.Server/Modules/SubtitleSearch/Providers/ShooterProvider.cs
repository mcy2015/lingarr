using System.Text.Json;
using Lingarr.Contracts.Settings;
using Lingarr.Server.Modules.SubtitleSearch.Abstractions;

namespace Lingarr.Server.Modules.SubtitleSearch.Providers;

/// <summary>
/// shooter.cn source (tech design ⑩): <c>POST shooter.cn/api/subapi.php</c>
/// with the segmented-MD5 <c>filehash</c> (<see cref="ShooterFileHasher"/>,
/// the source's publicly documented algorithm), the file name
/// (<c>pathinfo</c>, the parameter name of the reference implementations)
/// and a <c>lang</c> filter (<c>chn</c>/<c>eng</c>) — the Chinese hash-based
/// fallback of the v1 source list. The response is a JSON array whose
/// entries each list <c>Files[]</c> with direct download <c>Link</c>s. The
/// endpoint was renamed from <c>api/subtitle.php</c> to
/// <c>api/subapi.php</c> upstream (R4 hotfix, 2026-10: the old path stopped
/// answering). The
/// hash is computed lazily by the request builder (⑨) and travels in
/// <see cref="SubtitleSearchRequest.ShooterFileHash"/>; without it the
/// source cannot be queried honestly and answers empty rather than
/// pretending to match.
/// </summary>
[SubtitleProvider("shooter")]
public sealed class ShooterProvider : SubtitleProviderBase, ISubtitleProvider, ISubtitleProviderHealth
{
    private const string ProductionBaseUrl = "https://shooter.cn";

    /// <summary>Canonical codes → shooter lang parameter values.</summary>
    private static readonly IReadOnlyDictionary<string, string> LanguageMap =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["zh"] = "chn",
            ["zh-TW"] = "chn",
            ["en"] = "eng"
        };

    private readonly ISettingsAccess _settings;

    public ShooterProvider(
        ISettingsAccess settings,
        SubtitleCandidateCache candidateCache,
        HttpClient httpClient,
        ILogger<ShooterProvider> logger)
        : base("shooter", httpClient, candidateCache, logger)
    {
        _settings = settings;
    }

    public string Provider => ProviderKey;

    /// <inheritdoc />
    public async Task<SubtitleSearchResult> SearchAsync(SubtitleSearchRequest request, CancellationToken cancellationToken)
    {
        var baseUrl = await ResolveBaseUrlAsync(
            _settings, SubtitleSourceSettingKeys.Shooter.BaseUrl, ProductionBaseUrl, cancellationToken);

        // Hash-only API (⑩): no filehash, no honest query. The M3 request
        // builder computes it lazily like the OpenSubtitles hash.
        if (string.IsNullOrEmpty(request.ShooterFileHash))
        {
            Logger.LogDebug(
                "Shooter search for '{Title}' has no filehash; the source is hash-only, returning empty.",
                request.Title);
            return SubtitleSearchResult.Empty;
        }

        var candidates = new List<SubtitleCandidate>();
        foreach (var language in request.Languages)
        {
            if (!LanguageMap.TryGetValue(language, out var mapped))
            {
                // Unmapped code (manual search can ask for any culture-table
                // language): shooter has no lang parameter for it, and a
                // query WITHOUT lang returns the whole library — every hit
                // would then be mislabelled as this language. Skip the
                // language silently instead (SubSource precedent).
                continue;
            }

            try
            {
                var query = $"api/subapi.php?filehash={Uri.EscapeDataString(request.ShooterFileHash!)}"
                    + $"&pathinfo={Uri.EscapeDataString(request.ReleaseName ?? request.Title)}"
                    + "&format=json"
                    + $"&lang={mapped}";

                using var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/{query}");
                httpRequest.Headers.Add("Accept", "application/json");

                using var document = await SubtitleHttp.SendForJsonAsync(
                    Http, httpRequest, ProviderKey, cancellationToken);
                candidates.AddRange(ParseCandidates(document, language));
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

        using var httpRequest = new HttpRequestMessage(HttpMethod.Get, context.Link);
        var (content, headerFileName) = await SubtitleHttp.SendForBytesAsync(
            Http, httpRequest, ProviderKey, cancellationToken);
        var fileName = headerFileName ?? context.FileName ?? $"shooter.{context.Language}.srt";

        return new SubtitleDownload(ProviderKey, fileName, FormatFromFileName(fileName), content);
    }

    /// <inheritdoc />
    public async Task<SubtitleProviderTestOutcome> TestConnectionAsync(CancellationToken cancellationToken)
    {
        var baseUrl = await ResolveBaseUrlAsync(
            _settings, SubtitleSourceSettingKeys.Shooter.BaseUrl, ProductionBaseUrl, cancellationToken);

        // Connectivity probe with a syntactically valid but arbitrary
        // filehash: any HTTP 200 JSON answer proves the endpoint is alive
        // (an empty result list is expected and fine).
        using var request = new HttpRequestMessage(
            HttpMethod.Post, baseUrl + "/api/subapi.php?filehash=00000000000000000000000000000000&format=json");
        request.Headers.Add("Accept", "application/json");

        using var _ = await SubtitleHttp.SendCheckedAsync(Http, request, ProviderKey, cancellationToken);
        return new SubtitleProviderTestOutcome(true, "shooter.cn reachable (no credentials required).");
    }

    private List<SubtitleCandidate> ParseCandidates(JsonDocument document, string language)
    {
        var candidates = new List<SubtitleCandidate>();
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            // An empty result sometimes arrives as an empty string body; a
            // non-array body that parsed is treated as a miss, not an error.
            return candidates;
        }

        foreach (var entry in document.RootElement.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object
                || !entry.TryGetProperty("Files", out var files)
                || files.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            var description = SubtitleHttp.GetString(entry, "Desc");
            foreach (var file in files.EnumerateArray())
            {
                var link = SubtitleHttp.GetString(file, "Link");
                if (string.IsNullOrEmpty(link))
                {
                    continue;
                }

                var ext = SubtitleHttp.GetString(file, "Ext");
                var fileName = $"{Guid.NewGuid():N}.{(string.IsNullOrEmpty(ext) ? "srt" : ext!.ToLowerInvariant())}";
                var externalId = CandidateCache.Store(ProviderKey, new CandidateContext(link!, fileName, language));

                candidates.Add(new SubtitleCandidate(
                    ProviderKey,
                    externalId,
                    language.ToLowerInvariant(),
                    MatchScore: 0,
                    Forced: false,
                    HearingImpaired: false,
                    ReleaseInfo: description,
                    Uploader: null, // the API exposes no uploader
                    Format: string.IsNullOrEmpty(ext) ? "srt" : ext!.ToLowerInvariant(),
                    Size: null,
                    HashMatch: true)); // hash-only API: every hit is a filehash hit
            }
        }

        return candidates;
    }

    /// <summary>Provider-side state a download needs for one shooter file entry.</summary>
    private sealed record CandidateContext(string Link, string? FileName, string Language);
}
