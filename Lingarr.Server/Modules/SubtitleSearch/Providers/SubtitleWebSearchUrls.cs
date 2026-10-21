using Lingarr.Server.Modules.SubtitleSearch.Abstractions;
using Lingarr.Server.Modules.SubtitleSearch.Scoring;

namespace Lingarr.Server.Modules.SubtitleSearch.Providers;

/// <summary>
/// Manual web-search escape hatch (the assrt download-throttling closure):
/// deep links to the public search pages of the sources that have one, so
/// the user can go download by hand while the API path is rate-limited.
/// Pure function of (provider key, request) — it never reads the per-source
/// BaseUrl override settings (those exist for fake-source tests); website
/// links always point at the production public sites. xunlei and shooter
/// have no usable public search page and answer null. The keyword follows
/// the R3b override rule every provider shares: the locked
/// <c>Query</c> (trailing SxxEyy/NxMM tag stripped) replaces the
/// library-derived title, then each source assembles its own term.
/// </summary>
public static class SubtitleWebSearchUrls
{
    /// <summary>
    /// Site homepages of the sources with a public site, keyed by provider
    /// key (case-insensitive). Doubles as the source list whose deep links
    /// <see cref="BuildSearchUrls"/> produces and as the display fallback
    /// (providers endpoint) for sources without one.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> Homepages =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["assrt"] = "https://assrt.net",
            ["opensubtitles"] = "https://www.opensubtitles.com",
            ["subdl"] = "https://subdl.com",
            ["subsource"] = "https://subsource.net",
            ["gestdown"] = "https://www.addic7ed.com"
        };

    /// <summary>
    /// The source's public site homepage, null for sources without one
    /// (xunlei/shooter) and unknown keys. Used by the providers endpoint so
    /// even a skipped (unconfigured) source's chip can link somewhere
    /// useful — the settings card and the manual-search fallback never
    /// depend on a deep link existing.
    /// </summary>
    public static string? HomepageOf(string? providerKey) =>
        providerKey is not null && Homepages.TryGetValue(providerKey, out var homepage)
            ? homepage
            : null;

    /// <summary>
    /// Deep links of every source with a public search page, keyed by
    /// provider key — the map the search response carries. All links derive
    /// from the one media-level request, so every per-source fan-out
    /// response carries the full set: a source that errors out (or was
    /// skipped) still gets its deep link in the UI once any sibling
    /// response arrives.
    /// </summary>
    public static IReadOnlyDictionary<string, string> BuildSearchUrls(SubtitleSearchRequest request)
    {
        var urls = new Dictionary<string, string>(Homepages.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var key in Homepages.Keys)
        {
            var url = BuildSearchUrl(key, request);
            if (url is not null)
            {
                urls[key] = url;
            }
        }

        return urls;
    }

    /// <summary>
    /// The public search-page deep link of one source for one request:
    /// null for xunlei/shooter (no public search page), unknown keys, and
    /// requests without any usable keyword.
    /// </summary>
    public static string? BuildSearchUrl(string? providerKey, SubtitleSearchRequest request) =>
        providerKey?.Trim().ToLowerInvariant() switch
        {
            "assrt" => AssrtUrl(request),
            "opensubtitles" => OpenSubtitlesUrl(request),
            "subdl" => PathUrl("https://subdl.com/search/", KeywordOf(request)),
            "subsource" => QueryUrl("https://subsource.net/search?q=", KeywordOf(request)),
            "gestdown" => QueryUrl(
                "https://www.addic7ed.com/search.php?search=", KeywordOf(request), "&Submit=Search"),
            _ => null
        };

    /// <summary>
    /// The shared keyword rule (mirror of the providers' own
    /// <c>KeywordOf</c>): the locked <c>Query</c> override with its trailing
    /// SxxEyy/NxMM tag stripped, else the library-derived title. Null when
    /// neither yields a non-blank term.
    /// </summary>
    private static string? KeywordOf(SubtitleSearchRequest request)
    {
        var keyword = string.IsNullOrWhiteSpace(request.Query)
            ? request.Title
            : SubtitleMatchScorer.SplitTrailingSeasonEpisode(request.Query.Trim()).Keyword;
        return string.IsNullOrWhiteSpace(keyword) ? null : keyword;
    }

    /// <summary>
    /// assrt's site search takes the same term its API searches with
    /// (<c>AssrtProvider.SearchAsync</c>): "Title S01E02" for episodes,
    /// "Title Year" for movies — the structured season/episode rides the
    /// term even under a Query override, exactly like the API query.
    /// </summary>
    private static string? AssrtUrl(SubtitleSearchRequest request)
    {
        var keyword = KeywordOf(request);
        if (keyword is null)
        {
            return null;
        }

        var term = request.Season is int season && request.Episode is int episode
            ? $"{keyword} S{season:00}E{episode:00}"
            : $"{keyword}{(request.Year is int year ? $" {year}" : string.Empty)}";
        return $"https://assrt.net/sub/?searchword={Uri.EscapeDataString(term)}";
    }

    /// <summary>
    /// OpenSubtitles' public site narrows by path segments: the term is
    /// hyphenated inside the path (the site's own slug form — spaces become
    /// "-", every other escape stays percent-encoded), and episodes address
    /// the tvshows form with explicit season/episode segments while movies
    /// use the plain q- form. An episode request without structured
    /// season/episode falls back to the movie form — a keyword search is
    /// still more useful than no link.
    /// </summary>
    private static string? OpenSubtitlesUrl(SubtitleSearchRequest request)
    {
        var keyword = KeywordOf(request);
        if (keyword is null)
        {
            return null;
        }

        var slug = Uri.EscapeDataString(keyword).Replace("%20", "-");
        return request.Season is int season && request.Episode is int episode
            ? $"https://www.opensubtitles.com/en/search-tvshows/q-{slug}/season-{season}/episode-{episode}"
            : $"https://www.opensubtitles.com/en/search/q-{slug}";
    }

    /// <summary>Path-segment search URL (subdl): keyword percent-encoded, spaces as %20.</summary>
    private static string? PathUrl(string prefix, string? keyword) =>
        keyword is null ? null : $"{prefix}{Uri.EscapeDataString(keyword)}";

    /// <summary>Query-parameter search URL (subsource/gestdown): keyword percent-encoded.</summary>
    private static string? QueryUrl(string prefix, string? keyword, string suffix = "") =>
        keyword is null ? null : $"{prefix}{Uri.EscapeDataString(keyword)}{suffix}";
}
