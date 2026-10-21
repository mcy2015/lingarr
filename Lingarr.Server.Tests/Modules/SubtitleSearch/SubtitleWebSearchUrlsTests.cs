using System;
using System.Collections.Generic;
using System.Linq;
using Lingarr.Server.Modules.SubtitleSearch.Abstractions;
using Lingarr.Server.Modules.SubtitleSearch.Providers;
using Xunit;

namespace Lingarr.Server.Tests.Modules.SubtitleSearch;

/// <summary>
/// Web-search escape hatch (the assrt throttling closure): the deep links
/// of the five sources with a public search page. Covers the five
/// templates in movie/episode form, the R3b Query override rule (Query
/// wins, trailing SxxEyy stripped, then assembled per source), the
/// no-public-site pair (xunlei/shooter → null), and the BaseUrl-independence
/// of the links — the helper is a pure function of (key, request) with no
/// settings input at all, so the produced URLs are asserted against the
/// production hosts verbatim.
/// </summary>
public class SubtitleWebSearchUrlsTests
{
    private static SubtitleSearchRequest MovieRequest(
        string title = "Dune: Part Two",
        int? year = 2024,
        string? query = null) => new(
        title, year, Season: null, Episode: null,
        ReleaseName: null, VideoHash: null, Languages: ["en"], ImdbId: null,
        Query: query);

    private static SubtitleSearchRequest EpisodeRequest(
        string title = "Severance",
        int? season = 1,
        int? episode = 2,
        string? query = null) => new(
        title, Year: null, Season: season, Episode: episode,
        ReleaseName: null, VideoHash: null, Languages: ["en"], ImdbId: null,
        Query: query);

    // ── assrt ─────────────────────────────────────────────────────
    [Fact]
    public void Assrt_EpisodeCarriesTitleAndSxxEyy()
    {
        Assert.Equal(
            "https://assrt.net/sub/?searchword=Severance%20S01E02",
            SubtitleWebSearchUrls.BuildSearchUrl("assrt", EpisodeRequest()));
    }

    [Fact]
    public void Assrt_MovieAppendsYearLikeTheApiQuery()
    {
        Assert.Equal(
            "https://assrt.net/sub/?searchword=Dune%3A%20Part%20Two%202024",
            SubtitleWebSearchUrls.BuildSearchUrl("assrt", MovieRequest()));
    }

    [Fact]
    public void Assrt_MovieWithoutYearKeepsTheBareTitle()
    {
        Assert.Equal(
            "https://assrt.net/sub/?searchword=Dune%3A%20Part%20Two",
            SubtitleWebSearchUrls.BuildSearchUrl("assrt", MovieRequest(year: null)));
    }

    [Fact]
    public void Assrt_QueryOverrideReplacesTitleButStructuredTagWins()
    {
        // R3b: the override's own trailing tag is stripped; the structured
        // season/episode of the request ride the term (S02E05 here), exactly
        // like AssrtProvider's API keyword.
        Assert.Equal(
            "https://assrt.net/sub/?searchword=My%20Show%20S02E05",
            SubtitleWebSearchUrls.BuildSearchUrl(
                "assrt", EpisodeRequest(season: 2, episode: 5, query: "My Show S01E01")));
    }

    // ── opensubtitles ─────────────────────────────────────────────
    [Fact]
    public void OpenSubtitles_MovieUsesHyphenatedQPath()
    {
        Assert.Equal(
            "https://www.opensubtitles.com/en/search/q-Dune%3A-Part-Two",
            SubtitleWebSearchUrls.BuildSearchUrl("opensubtitles", MovieRequest()));
    }

    [Fact]
    public void OpenSubtitles_EpisodeUsesTvshowsFormWithSeasonEpisode()
    {
        Assert.Equal(
            "https://www.opensubtitles.com/en/search-tvshows/q-Severance/season-1/episode-2",
            SubtitleWebSearchUrls.BuildSearchUrl("opensubtitles", EpisodeRequest()));
    }

    [Fact]
    public void OpenSubtitles_EpisodeWithoutStructuredTagFallsBackToMovieForm()
    {
        Assert.Equal(
            "https://www.opensubtitles.com/en/search/q-Severance",
            SubtitleWebSearchUrls.BuildSearchUrl(
                "opensubtitles", EpisodeRequest(season: null, episode: null)));
    }

    [Fact]
    public void OpenSubtitles_QueryOverrideIsStrippedBeforeSlugging()
    {
        Assert.Equal(
            "https://www.opensubtitles.com/en/search/q-Loki",
            SubtitleWebSearchUrls.BuildSearchUrl(
                "opensubtitles", MovieRequest(query: "Loki S01E01")));
    }

    // ── subdl / subsource / gestdown ──────────────────────────────
    [Fact]
    public void Subdl_SearchesTheBareKeywordInThePath()
    {
        Assert.Equal(
            "https://subdl.com/search/Dune%3A%20Part%20Two",
            SubtitleWebSearchUrls.BuildSearchUrl("subdl", MovieRequest()));
        // episodes search the show name — the site's own flow narrows the
        // season/episode from there, and the template keeps {标题} bare
        Assert.Equal(
            "https://subdl.com/search/Severance",
            SubtitleWebSearchUrls.BuildSearchUrl("subdl", EpisodeRequest()));
    }

    [Fact]
    public void SubSource_SearchesTheKeywordAsQueryParameter()
    {
        Assert.Equal(
            "https://subsource.net/search?q=Dune%3A%20Part%20Two",
            SubtitleWebSearchUrls.BuildSearchUrl("subsource", MovieRequest()));
        Assert.Equal(
            "https://subsource.net/search?q=Severance",
            SubtitleWebSearchUrls.BuildSearchUrl("subsource", EpisodeRequest()));
    }

    [Fact]
    public void Gestdown_SearchesTheShowNameWithoutSxxEyy()
    {
        // Addic7ed (the site gestdown fronts) narrows season/episode in its
        // own browse flow — the link searches the bare show name.
        Assert.Equal(
            "https://www.addic7ed.com/search.php?search=Severance&Submit=Search",
            SubtitleWebSearchUrls.BuildSearchUrl("gestdown", EpisodeRequest()));
    }

    [Fact]
    public void QueryOverrideRidesEveryTemplate()
    {
        var request = EpisodeRequest(query: "  Silo S02E01  ");
        Assert.Equal(
            "https://subdl.com/search/Silo",
            SubtitleWebSearchUrls.BuildSearchUrl("subdl", request));
        Assert.Equal(
            "https://subsource.net/search?q=Silo",
            SubtitleWebSearchUrls.BuildSearchUrl("subsource", request));
        Assert.Equal(
            "https://www.addic7ed.com/search.php?search=Silo&Submit=Search",
            SubtitleWebSearchUrls.BuildSearchUrl("gestdown", request));
        // assrt re-appends the STRUCTURED tag (S01E02), never the override's
        // own (S02E01) — same precedence as the API query
        Assert.Equal(
            "https://assrt.net/sub/?searchword=Silo%20S01E02",
            SubtitleWebSearchUrls.BuildSearchUrl("assrt", request));
    }

    // ── no-site sources / degenerate inputs ───────────────────────
    [Fact]
    public void XunleiShooterAndUnknownKeysHaveNoLink()
    {
        var movie = MovieRequest();
        Assert.Null(SubtitleWebSearchUrls.BuildSearchUrl("xunlei", movie));
        Assert.Null(SubtitleWebSearchUrls.BuildSearchUrl("shooter", movie));
        Assert.Null(SubtitleWebSearchUrls.BuildSearchUrl("does-not-exist", movie));
        Assert.Null(SubtitleWebSearchUrls.BuildSearchUrl(null, movie));

        Assert.Null(SubtitleWebSearchUrls.HomepageOf("xunlei"));
        Assert.Null(SubtitleWebSearchUrls.HomepageOf("shooter"));
        Assert.Null(SubtitleWebSearchUrls.HomepageOf(null));
        Assert.Null(SubtitleWebSearchUrls.HomepageOf("does-not-exist"));
    }

    [Fact]
    public void BlankKeywordYieldsNoLink()
    {
        var blank = MovieRequest(title: " ");
        foreach (var key in new[] { "assrt", "opensubtitles", "subdl", "subsource", "gestdown" })
        {
            Assert.Null(SubtitleWebSearchUrls.BuildSearchUrl(key, blank));
        }
    }

    // ── the map the search response carries ───────────────────────
    [Fact]
    public void BuildSearchUrls_CoversExactlyTheFiveWebSearchableSources()
    {
        var urls = SubtitleWebSearchUrls.BuildSearchUrls(EpisodeRequest());
        Assert.Equal(
            new[] { "assrt", "gestdown", "opensubtitles", "subdl", "subsource" },
            urls.Keys.OrderBy(key => key, StringComparer.Ordinal).ToArray());
        Assert.DoesNotContain("xunlei", urls.Keys);
        Assert.DoesNotContain("shooter", urls.Keys);
        Assert.All(urls.Values, url => Assert.StartsWith("https://", url));
    }

    [Fact]
    public void BuildSearchUrls_MatchesPerKeyBuilds()
    {
        var request = EpisodeRequest(title: "The Bear", season: 3, episode: 7);
        var urls = SubtitleWebSearchUrls.BuildSearchUrls(request);
        foreach (var (key, url) in urls)
        {
            Assert.Equal(url, SubtitleWebSearchUrls.BuildSearchUrl(key, request));
        }
    }

    // ── BaseUrl-independence ──────────────────────────────────────
    [Fact]
    public void LinksAlwaysTargetTheProductionPublicSites()
    {
        // The helper takes no settings input, so the fake-source BaseUrl
        // overrides cannot leak in — pinned by asserting every produced URL
        // against the production hosts (and the same hosts as the
        // homepages).
        var request = EpisodeRequest();
        var urls = SubtitleWebSearchUrls.BuildSearchUrls(request);
        foreach (var (key, url) in urls)
        {
            var homepage = SubtitleWebSearchUrls.HomepageOf(key);
            Assert.NotNull(homepage);
            Assert.StartsWith(homepage!, url);
        }

        Assert.Equal("https://assrt.net", SubtitleWebSearchUrls.HomepageOf("assrt"));
        Assert.Equal("https://www.opensubtitles.com", SubtitleWebSearchUrls.HomepageOf("opensubtitles"));
        Assert.Equal("https://subdl.com", SubtitleWebSearchUrls.HomepageOf("subdl"));
        Assert.Equal("https://subsource.net", SubtitleWebSearchUrls.HomepageOf("subsource"));
        Assert.Equal("https://www.addic7ed.com", SubtitleWebSearchUrls.HomepageOf("gestdown"));
    }

    [Fact]
    public void ProviderKeyLookupIsCaseInsensitive()
    {
        // Front-end source keys arrive lower-case; tolerating case keeps
        // the helper honest if a caller passes display casing.
        Assert.Equal(
            SubtitleWebSearchUrls.BuildSearchUrl("assrt", EpisodeRequest()),
            SubtitleWebSearchUrls.BuildSearchUrl("ASSRT", EpisodeRequest()));
        Assert.Equal(
            SubtitleWebSearchUrls.HomepageOf("subdl"),
            SubtitleWebSearchUrls.HomepageOf("SubDL"));
    }
}
