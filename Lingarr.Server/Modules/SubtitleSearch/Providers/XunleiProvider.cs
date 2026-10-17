using System.Text.Json;
using Lingarr.Contracts.Settings;
using Lingarr.Server.Modules.SubtitleSearch.Abstractions;

namespace Lingarr.Server.Modules.SubtitleSearch.Providers;

/// <summary>
/// Xunlei subtitle source (batch R2, 2026-10 — the 7th v1 source):
/// <c>GET {BaseUrl}/subxl/{hash}.json</c> with the three-segment sampled
/// SHA1 <see cref="XunleiFileHasher"/> (uppercase hex), computed lazily by
/// the request builder (⑨) and travelling in
/// <see cref="SubtitleSearchRequest.XunleiFileHash"/>. Zero authentication;
/// the endpoint is plain HTTP on port 8000 (protocol reality — no TLS
/// exists upstream, so the client adds no HTTPS-only constraint). The
/// response lists Chinese simplified/traditional/bilingual entries whose
/// <c>language</c> field maps onto the canonical zh codes; bilingual
/// 简体&amp;英语 counts as a zh direct hit (final state without translation).
/// Hash-only semantics like shooter: no filehash, no honest query — empty.
/// </summary>
[SubtitleProvider("xunlei")]
public sealed class XunleiProvider : SubtitleProviderBase, ISubtitleProvider, ISubtitleProviderHealth
{
    private const string ProductionBaseUrl = "http://sub.xmp.xunlei.com:8000";

    /// <summary>
    /// Observed <c>language</c> values → canonical codes. The repo
    /// vocabulary distinguishes 简繁 ("zh" / "zh-TW", see SubSource's
    /// CHS/CHT mapping); the bilingual form simplifies onto zh (R2 decision:
    /// direct-hit terminal state, no translation hop).
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> LanguageMap =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["简体"] = "zh",
            ["繁体"] = "zh-TW",
            ["简体&英语"] = "zh",
            ["繁体&英语"] = "zh-TW"
        };

    private readonly ISettingsAccess _settings;

    public XunleiProvider(
        ISettingsAccess settings,
        SubtitleCandidateCache candidateCache,
        HttpClient httpClient,
        ILogger<XunleiProvider> logger)
        : base("xunlei", httpClient, candidateCache, logger)
    {
        _settings = settings;
    }

    public string Provider => ProviderKey;

    /// <inheritdoc />
    public async Task<SubtitleSearchResult> SearchAsync(SubtitleSearchRequest request, CancellationToken cancellationToken)
    {
        var baseUrl = await ResolveBaseUrlAsync(
            _settings, SubtitleSourceSettingKeys.Xunlei.BaseUrl, ProductionBaseUrl, cancellationToken);

        // Hash-only API (same verdict as shooter): no filehash — either the
        // round did not query xunlei-lazily, or the file is under the
        // algorithm's 0xF000 floor and the hasher answered null. Empty, not
        // a fake keyword search.
        if (string.IsNullOrWhiteSpace(request.XunleiFileHash))
        {
            Logger.LogDebug(
                "Xunlei search for '{Title}' has no filehash; the source is hash-only, returning empty.",
                request.Title);
            return SubtitleSearchResult.Empty;
        }

        // One GET per search: the endpoint has no language parameter, the
        // language filter is applied client-side while parsing (SubSource
        // precedent — the endpoint returns every language it hosts).
        using var httpRequest = new HttpRequestMessage(
            HttpMethod.Get, $"{baseUrl}/subxl/{Uri.EscapeDataString(request.XunleiFileHash!)}.json");
        httpRequest.Headers.Add("Accept", "application/json");

        using var document = await SubtitleHttp.SendForJsonAsync(
            Http, httpRequest, ProviderKey, cancellationToken);
        return new SubtitleSearchResult(ParseCandidates(document, request));
    }

    /// <inheritdoc />
    public async Task<SubtitleDownload> DownloadAsync(SubtitleCandidateRef candidateRef, CancellationToken cancellationToken)
    {
        var context = CandidateCache.Resolve<CandidateContext>(candidateRef, ProviderKey);

        // surl is a plain direct-file link (.srt/.ass/.ssa) — no archive.
        using var httpRequest = new HttpRequestMessage(HttpMethod.Get, context.Link);
        var (content, headerFileName) = await SubtitleHttp.SendForBytesAsync(
            Http, httpRequest, ProviderKey, cancellationToken);
        var fileName = headerFileName ?? context.FileName ?? $"xunlei.{context.Language}.srt";

        return new SubtitleDownload(ProviderKey, fileName, FormatFromFileName(fileName), content);
    }

    /// <inheritdoc />
    public async Task<SubtitleProviderTestOutcome> TestConnectionAsync(CancellationToken cancellationToken)
    {
        var baseUrl = await ResolveBaseUrlAsync(
            _settings, SubtitleSourceSettingKeys.Xunlei.BaseUrl, ProductionBaseUrl, cancellationToken);

        // Connectivity probe with a syntactically valid dummy hash (forty
        // zeros): the endpoint answers unknown hashes with a hot-list
        // fallback, so any HTTP 200 JSON body proves it is alive.
        using var request = new HttpRequestMessage(
            HttpMethod.Get, baseUrl + "/subxl/" + new string('0', 40) + ".json");
        request.Headers.Add("Accept", "application/json");

        using var _ = await SubtitleHttp.SendForJsonAsync(Http, request, ProviderKey, cancellationToken);
        return new SubtitleProviderTestOutcome(true, "Xunlei endpoint reachable (no credentials required).");
    }

    private List<SubtitleCandidate> ParseCandidates(JsonDocument document, SubtitleSearchRequest request)
    {
        var candidates = new List<SubtitleCandidate>();
        if (document.RootElement.ValueKind != JsonValueKind.Object
            || !document.RootElement.TryGetProperty("sublist", out var sublist)
            || sublist.ValueKind != JsonValueKind.Array)
        {
            // An empty result is a missing/empty sublist — a miss, not an error.
            return candidates;
        }

        foreach (var entry in sublist.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            // Observed garbage values ("未知语言", GBK-mangled "\uFFFD体")
            // must not pollute the candidate pool: unmapped entries are
            // dropped with a debug trace, never thrown.
            var sourceLanguage = SubtitleHttp.GetString(entry, "language");
            if (string.IsNullOrEmpty(sourceLanguage) || !LanguageMap.TryGetValue(sourceLanguage, out var language))
            {
                Logger.LogDebug(
                    "Xunlei entry has an unrecognized language '{Language}'; dropping the entry.",
                    sourceLanguage);
                continue;
            }

            // Client-side language filter: the hash lookup returns every
            // language the file has; only the requested set is worth a
            // candidate (SubSource precedent).
            if (!request.Languages.Contains(language, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            var link = SubtitleHttp.GetString(entry, "surl");
            if (string.IsNullOrEmpty(link))
            {
                Logger.LogDebug("Xunlei entry has no surl; dropping the entry.");
                continue;
            }

            // sname is the display/release name and may arrive wholly
            // percent-encoded ("%3A%5C…" shape).
            var fileName = DecodeDisplayName(SubtitleHttp.GetString(entry, "sname"));
            var externalId = CandidateCache.Store(ProviderKey, new CandidateContext(link!, fileName, language));

            candidates.Add(new SubtitleCandidate(
                ProviderKey,
                externalId,
                language,
                MatchScore: 0,
                Forced: false,
                HearingImpaired: false,
                ReleaseInfo: fileName,
                Uploader: null, // the API exposes no uploader
                Format: FormatFromFileName(fileName ?? link),
                Size: null, // the search response carries no payload size
                HashMatch: true)); // hash-only API: every hit is a hash hit
        }

        return candidates;
    }

    /// <summary>
    /// Unescapes a wholly percent-encoded <c>sname</c>. A real subtitle name
    /// almost never contains '%'; one that carries valid %XX escapes decodes
    /// into the honest display name. Invalid escapes and '%' without a hex
    /// pair pass through untouched (UnescapeDataString would throw on them).
    /// </summary>
    private static string? DecodeDisplayName(string? sname)
    {
        if (string.IsNullOrEmpty(sname) || !sname.Contains('%') || !HasPercentEncoding(sname))
        {
            return sname;
        }

        try
        {
            return Uri.UnescapeDataString(sname);
        }
        catch (UriFormatException)
        {
            return sname; // malformed escapes: the raw name is the best we have
        }
    }

    /// <summary>Whether the value contains at least one valid %XX hex escape.</summary>
    private static bool HasPercentEncoding(string value)
    {
        for (var i = 0; i + 2 < value.Length; i++)
        {
            if (value[i] == '%'
                && Uri.IsHexDigit(value[i + 1])
                && Uri.IsHexDigit(value[i + 2]))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Provider-side state a download needs for one xunlei sublist entry.</summary>
    private sealed record CandidateContext(string Link, string? FileName, string Language);
}
