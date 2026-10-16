using System.Xml.Linq;
using Lingarr.Contracts.Settings;
using Lingarr.Server.Modules.SubtitleSearch.Abstractions;

namespace Lingarr.Server.Modules.SubtitleSearch.Providers;

/// <summary>
/// Podnapisi source (tech design ⑩): the long-standing XML search interface
/// at <c>podnapisi.net</c>, free, no key. The wire protocol (search URL
/// shape, XML result nodes, download link semantics) follows the reference
/// implementation in subliminal (MIT — the cleanest-licensed upstream, ⑩/⑬;
/// protocol behaviour reference, no code copied): GET
/// <c>/subtitles/search/</c> with <c>keywords</c>/<c>language</c>/
/// <c>season</c>/<c>episode</c> and an XML Accept header; results are
/// <c>&lt;subtitle&gt;</c> nodes; the download link serves the raw payload
/// (commonly zip — the M4 pipeline extracts).
/// </summary>
[SubtitleProvider("podnapisi")]
public sealed class PodnapisiProvider : SubtitleProviderBase, ISubtitleProvider, ISubtitleProviderHealth
{
    private const string ProductionBaseUrl = "https://www.podnapisi.net";

    private readonly ISettingsAccess _settings;

    public PodnapisiProvider(
        ISettingsAccess settings,
        SubtitleCandidateCache candidateCache,
        HttpClient httpClient,
        ILogger<PodnapisiProvider> logger)
        : base("podnapisi", httpClient, candidateCache, logger)
    {
        _settings = settings;
    }

    public string Provider => ProviderKey;

    /// <inheritdoc />
    public async Task<SubtitleSearchResult> SearchAsync(SubtitleSearchRequest request, CancellationToken cancellationToken)
    {
        var baseUrl = await ResolveBaseUrlAsync(
            _settings, SubtitleSourceSettingKeys.Podnapisi.BaseUrl, ProductionBaseUrl, cancellationToken);

        var candidates = new List<SubtitleCandidate>();
        foreach (var language in request.Languages)
        {
            // One request per language — the XML interface keys its language
            // filter on a single code. zh/zh-TW collapse: the interface does
            // not distinguish Chinese scripts.
            var queryLanguage = language.Equals("zh-TW", StringComparison.OrdinalIgnoreCase) ? "zh" : language.ToLowerInvariant();
            var query = $"subtitles/search/?keywords={Uri.EscapeDataString(request.Title)}"
                + $"&language={queryLanguage}";
            if (request.Season is int season && request.Episode is int episode)
            {
                query += $"&season={season}&episode={episode}";
            }
            else if (request.Year is int year)
            {
                query += $"&year={year}";
            }

            using var httpRequest = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/{query}");
            httpRequest.Headers.Add("Accept", "application/xml");

            using var response = await SubtitleHttp.SendCheckedAsync(
                Http, httpRequest, ProviderKey, cancellationToken);
            var xml = await response.Content.ReadAsStringAsync(cancellationToken);
            XDocument document;
            try
            {
                document = XDocument.Parse(xml);
            }
            catch (System.Xml.XmlException ex)
            {
                throw SubtitleProviderException.ForInvalidResponse(ProviderKey, "malformed XML.", ex);
            }

            candidates.AddRange(ParseCandidates(document, baseUrl, language));
        }

        return new SubtitleSearchResult(candidates);
    }

    /// <inheritdoc />
    public async Task<SubtitleDownload> DownloadAsync(SubtitleCandidateRef candidateRef, CancellationToken cancellationToken)
    {
        var context = CandidateCache.Resolve<CandidateContext>(candidateRef, ProviderKey);

        using var httpRequest = new HttpRequestMessage(HttpMethod.Get, context.DownloadUrl);
        var (content, headerFileName) = await SubtitleHttp.SendForBytesAsync(
            Http, httpRequest, ProviderKey, cancellationToken);
        var fileName = headerFileName ?? context.FileName ?? "podnapisi.zip";

        return new SubtitleDownload(ProviderKey, fileName, FormatFromFileName(fileName), content);
    }

    /// <inheritdoc />
    public async Task<SubtitleProviderTestOutcome> TestConnectionAsync(CancellationToken cancellationToken)
    {
        var baseUrl = await ResolveBaseUrlAsync(
            _settings, SubtitleSourceSettingKeys.Podnapisi.BaseUrl, ProductionBaseUrl, cancellationToken);

        using var request = new HttpRequestMessage(
            HttpMethod.Get, baseUrl + "/subtitles/search/?keywords=lingarr&language=en");
        request.Headers.Add("Accept", "application/xml");

        using var _ = await SubtitleHttp.SendCheckedAsync(Http, request, ProviderKey, cancellationToken);
        return new SubtitleProviderTestOutcome(true, "Podnapisi reachable (no credentials required).");
    }

    /// <summary>
    /// Reads <c>&lt;subtitle&gt;</c> nodes. Attribute names are the
    /// fixture-pinned contract (the XML interface has no schema document);
    /// missing optional attributes degrade to null, a missing id skips the
    /// node.
    /// </summary>
    private List<SubtitleCandidate> ParseCandidates(XDocument document, string baseUrl, string language)
    {
        var candidates = new List<SubtitleCandidate>();
        var root = document.Root ?? throw SubtitleProviderException.ForInvalidResponse(
            ProviderKey, "the XML response has no root element.");
        var nodes = root.Elements("subtitle").ToList();
        if (nodes.Count == 0)
        {
            // The feed nests results one level down on some responses —
            // accept both shapes rather than treating a nested feed as empty.
            nodes = root.Elements().SelectMany(child => child.Elements("subtitle")).ToList();
        }

        foreach (var node in nodes)
        {
            var id = (string?)node.Attribute("id");
            if (string.IsNullOrEmpty(id))
            {
                continue;
            }

            var link = (string?)node.Attribute("link");
            var title = (string?)node.Attribute("title");
            var release = (string?)node.Attribute("release");
            var uploader = (string?)node.Attribute("uploader");
            var format = (string?)node.Attribute("format");
            var hearingImpairedRaw = (string?)node.Attribute("hearing-impaired");

            // Relative link → absolute against the (possibly overridden)
            // base, so the fake-source round downloads from its own origin.
            Uri.TryCreate(link, UriKind.RelativeOrAbsolute, out var linkUri);
            var downloadUrl = linkUri?.IsAbsoluteUri == true
                ? linkUri.ToString()
                : $"{baseUrl}{(string.IsNullOrEmpty(link) ? $"/subtitles/download/{id}" : (link!.StartsWith('/') ? link : "/" + link))}";

            var fileName = $"{id}.zip";
            var externalId = CandidateCache.Store(ProviderKey, new CandidateContext(downloadUrl, fileName));

            candidates.Add(new SubtitleCandidate(
                ProviderKey,
                externalId,
                language.ToLowerInvariant(),
                MatchScore: 0,
                Forced: false,
                HearingImpaired: hearingImpairedRaw == "1" || string.Equals(hearingImpairedRaw, "true", StringComparison.OrdinalIgnoreCase),
                ReleaseInfo: release ?? title,
                Uploader: uploader,
                Format: string.IsNullOrEmpty(format) ? "zip" : format!.ToLowerInvariant(),
                Size: null,
                HashMatch: false));
        }

        return candidates;
    }

    /// <summary>Provider-side state a download needs for one Podnapisi entry.</summary>
    private sealed record CandidateContext(string DownloadUrl, string? FileName);
}
