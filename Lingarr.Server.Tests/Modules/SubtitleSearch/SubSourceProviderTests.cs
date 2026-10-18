using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Lingarr.Server.Modules.SubtitleSearch;
using Lingarr.Server.Modules.SubtitleSearch.Abstractions;
using Lingarr.Server.Modules.SubtitleSearch.Providers;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Lingarr.Server.Tests.Modules.SubtitleSearch;

/// <summary>
/// SubSource adapter against the recorded v1 fixtures (R4 hotfix
/// acceptance): the API key is mandatory (fast-fail without any HTTP when
/// missing), the two-step flow resolves the title through
/// <c>/api/v1/movies/search</c> (IMDb preferred, text fallback) and lists
/// subtitles through <c>/api/v1/subtitles</c> with the source's own
/// language names, <c>success=false</c> is an empty result, episodes append
/// seasonNumber/episodeNumber, and the download fetches the raw file bytes
/// from <c>/api/v1/subtitles/{id}/download</c>.
/// </summary>
public class SubSourceProviderTests
{
    private const string ApiKey = "sub-key";

    private static SubtitleCandidateCache NewCache() =>
        new(new MemoryCache(new MemoryCacheOptions()), NullLogger<SubtitleCandidateCache>.Instance);

    private static SubSourceProvider Create(StubHttpMessageHandler handler, string? baseUrl = null, string? apiKey = ApiKey)
    {
        var settings = baseUrl is null
            ? null
            : new System.Collections.Generic.Dictionary<string, string>
            {
                [SubtitleSourceSettingKeys.SubSource.BaseUrl] = baseUrl
            };
        var secrets = apiKey is null
            ? null
            : new System.Collections.Generic.Dictionary<string, string>
            {
                [SubtitleSourceSettingKeys.SubSource.ApiKey] = apiKey
            };
        return new SubSourceProvider(
            SubtitleSettingsDouble.Create(settings, secrets).Object,
            NewCache(), handler.ToClient(), NullLogger<SubSourceProvider>.Instance);
    }

    private static SubtitleSearchRequest MovieRequest(string[] languages, string? imdbId = "tt4321") => new(
        "Movie", 2019, Season: null, Episode: null,
        ReleaseName: null, VideoHash: null, Languages: languages, ImdbId: imdbId);

    [Fact]
    public async Task Search_WithoutApiKey_FailsFastWithoutHttpRequest()
    {
        var handler = new StubHttpMessageHandler();

        var provider = Create(handler, apiKey: null);
        var exception = await Assert.ThrowsAsync<SubtitleProviderException>(
            () => provider.SearchAsync(MovieRequest(["en"]), CancellationToken.None));

        // The v1 API answers 401 without a key — the honest verdict is the
        // missing-credential failure, before any request leaves.
        Assert.Equal(SubtitleProviderFailureReason.MissingCredentials, exception.Reason);
        Assert.Contains("API key", exception.Message);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Search_WithImdbId_PrefersImdbSearchAndParsesCandidates()
    {
        var handler = new StubHttpMessageHandler()
            .EnqueueJson(SubtitleFixtures.Read("subsource", "movies-search.json"))
            .EnqueueJson(SubtitleFixtures.Read("subsource", "subtitles.json"));

        var provider = Create(handler);
        var result = await provider.SearchAsync(MovieRequest(["en"]), CancellationToken.None);

        var searchUrl = handler.Requests[0].RequestUri!.ToString();
        Assert.Contains("/api/v1/movies/search", searchUrl);
        Assert.Contains("searchType=imdb", searchUrl);
        Assert.Contains("imdb=tt4321", searchUrl); // upstream parameter name
        Assert.Contains("api_key=sub-key", searchUrl);

        var subtitlesUrl = handler.Requests[1].RequestUri!.ToString();
        Assert.Contains("/api/v1/subtitles", subtitlesUrl);
        Assert.Contains("language=english", subtitlesUrl);
        Assert.Contains("limit=100", subtitlesUrl);
        Assert.Contains("movieId=sub-movie-1", subtitlesUrl); // imdb identity: the first hit wins
        Assert.Equal(HttpMethod.Get, handler.Requests[0].Method);

        // The fixture's English pair survives; the French entry is dropped
        // by the defensive cross-language check.
        Assert.Equal(2, result.Candidates.Count);
        var candidate = result.Candidates[0];
        Assert.Equal("subsource", candidate.Provider);
        Assert.Equal("en", candidate.Language);
        Assert.Equal("Movie.2019.WEBDL-1080p", candidate.ReleaseInfo);
        Assert.Equal("fixture-author", candidate.Uploader);
        Assert.False(candidate.HearingImpaired);
        Assert.False(candidate.Forced);
        Assert.False(candidate.HashMatch);
        Assert.StartsWith("subtitle-candidate:subsource:", candidate.ExternalId);
        Assert.True(result.Candidates[1].HearingImpaired);
        Assert.True(result.Candidates[1].Forced);
        Assert.Equal("Movie.2019.WEBDL-1080p, Movie.2019.WEBDL-1080p.HI", result.Candidates[1].ReleaseInfo);
    }

    [Fact]
    public async Task Search_WithoutImdbId_UsesLowercaseTitleTextSearch()
    {
        var handler = new StubHttpMessageHandler()
            .EnqueueJson(SubtitleFixtures.Read("subsource", "movies-search.json"))
            .EnqueueJson(SubtitleFixtures.Read("subsource", "subtitles.json"));

        var provider = Create(handler);
        await provider.SearchAsync(MovieRequest(["en"], imdbId: null), CancellationToken.None);

        // Upstream parameter names: q (not query) with the lowercased title
        // alone — the year gate is applied client-side on the results.
        var searchUrl = handler.Requests[0].RequestUri!.ToString();
        Assert.Contains("searchType=text", searchUrl);
        Assert.Contains("q=movie", searchUrl);
        Assert.DoesNotContain("query=", searchUrl);
    }

    [Fact]
    public async Task Search_ImdbMiss_FallsBackToTextSearch()
    {
        var handler = new StubHttpMessageHandler()
            .EnqueueJson("""{"success": true, "data": []}""")
            .EnqueueJson(SubtitleFixtures.Read("subsource", "movies-search.json"))
            .EnqueueJson(SubtitleFixtures.Read("subsource", "subtitles.json"));

        var provider = Create(handler);
        var result = await provider.SearchAsync(MovieRequest(["en"]), CancellationToken.None);

        // Round 0: the imdb lookup answers empty; round 1: the text retry
        // resolves the title; round 2: the subtitles page.
        Assert.Equal(3, handler.Requests.Count);
        Assert.Contains("searchType=imdb", handler.Requests[0].RequestUri!.ToString());
        Assert.Contains("searchType=text", handler.Requests[1].RequestUri!.ToString());
        Assert.NotEmpty(result.Candidates);
    }

    [Fact]
    public async Task Search_TitleNotResolved_IsAnEmptyResult()
    {
        var handler = new StubHttpMessageHandler()
            .EnqueueJson("""{"success": true, "data": []}""");

        var provider = Create(handler);
        var result = await provider.SearchAsync(MovieRequest(["en"], imdbId: null), CancellationToken.None);

        // No honest movieId → no subtitles round at all.
        Assert.Empty(result.Candidates);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Search_WrongYearTitleMatch_IsRejected()
    {
        var handler = new StubHttpMessageHandler()
            .EnqueueJson("""{"success": true, "data": [{ "movieId": "sub-movie-x", "title": "Movie", "releaseYear": 1999 }]}""");

        var provider = Create(handler);
        var result = await provider.SearchAsync(MovieRequest(["en"], imdbId: null), CancellationToken.None);

        // Right name, wrong year is a different release — no subtitles for it.
        Assert.Empty(result.Candidates);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Search_Episode_AppendsSeasonAndEpisodeParameters()
    {
        var handler = new StubHttpMessageHandler()
            .EnqueueJson(SubtitleFixtures.Read("subsource", "movies-search.json"))
            .EnqueueJson(SubtitleFixtures.Read("subsource", "subtitles.json"));

        var provider = Create(handler);
        // Episode shape of the movie request (season/episode both set) —
        // the imdb round resolves the title either way.
        var request = new SubtitleSearchRequest(
            "Movie", 2019, Season: 1, Episode: 2,
            ReleaseName: null, VideoHash: null, Languages: ["en"], ImdbId: "tt4321");
        await provider.SearchAsync(request, CancellationToken.None);

        var subtitlesUrl = handler.Requests[1].RequestUri!.ToString();
        Assert.Contains("seasonNumber=1", subtitlesUrl);
        Assert.Contains("episodeNumber=2", subtitlesUrl);
    }

    [Fact]
    public async Task Search_LanguagesMapToSourceNames()
    {
        var handler = new StubHttpMessageHandler()
            .EnqueueJson(SubtitleFixtures.Read("subsource", "movies-search.json"))
            .EnqueueJson(SubtitleFixtures.Read("subsource", "subtitles.json"))
            .EnqueueJson(SubtitleFixtures.Read("subsource", "subtitles.json"));

        var provider = Create(handler);
        await provider.SearchAsync(MovieRequest(["zh", "zh-TW"]), CancellationToken.None);

        // Chinese uploads live under a single source language name.
        Assert.Contains("language=chinese_bg_code", handler.Requests[1].RequestUri!.ToString());
        Assert.Contains("language=chinese_bg_code", handler.Requests[2].RequestUri!.ToString());
    }

    [Fact]
    public async Task Search_UnmappedLanguage_SkipsTheSubtitlesRound()
    {
        var handler = new StubHttpMessageHandler()
            .EnqueueJson(SubtitleFixtures.Read("subsource", "movies-search.json"));

        var provider = Create(handler);
        var result = await provider.SearchAsync(MovieRequest(["xx"]), CancellationToken.None);

        // No source name for the code — no honest query for it.
        Assert.Empty(result.Candidates);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Search_SuccessFalse_IsAnEmptyResult()
    {
        var handler = new StubHttpMessageHandler()
            .EnqueueJson(SubtitleFixtures.Read("subsource", "movies-search.json"))
            .EnqueueJson("""{"success": false}""");

        var provider = Create(handler);
        var result = await provider.SearchAsync(MovieRequest(["en"]), CancellationToken.None);

        Assert.Empty(result.Candidates);
    }

    [Fact]
    public async Task Search_NotFoundData_IsAnEmptyResult()
    {
        var handler = new StubHttpMessageHandler()
            .EnqueueJson(SubtitleFixtures.Read("subsource", "movies-search.json"))
            .EnqueueJson("""{"success": true, "data": []}""");

        var provider = Create(handler);
        var result = await provider.SearchAsync(MovieRequest(["en"]), CancellationToken.None);

        Assert.Empty(result.Candidates);
    }

    [Fact]
    public async Task RateLimited429_MapsToCooldownException()
    {
        var handler = new StubHttpMessageHandler().EnqueueStatus(HttpStatusCode.TooManyRequests);

        var provider = Create(handler);
        var exception = await Assert.ThrowsAsync<SubtitleProviderException>(
            () => provider.SearchAsync(MovieRequest(["en"]), CancellationToken.None));

        Assert.Equal(SubtitleProviderFailureReason.RateLimited, exception.Reason);
        Assert.Equal(TimeSpan.FromHours(1), exception.Cooldown);
    }

    [Fact]
    public async Task Unauthorized401_MapsToTheUnauthorizedBucket()
    {
        var handler = new StubHttpMessageHandler().EnqueueStatus(HttpStatusCode.Unauthorized);

        var provider = Create(handler);
        var exception = await Assert.ThrowsAsync<SubtitleProviderException>(
            () => provider.SearchAsync(MovieRequest(["en"]), CancellationToken.None));

        // A configured key the server rejects surfaces as the auth bucket,
        // not as a generic failure.
        Assert.Equal(SubtitleProviderFailureReason.Unauthorized, exception.Reason);
    }

    [Fact]
    public async Task Timeout_MapsToTimeoutBucket()
    {
        var handler = new StubHttpMessageHandler().EnqueueHang();

        var provider = new SubSourceProvider(
            SubtitleSettingsDouble.Create(
                secrets: new System.Collections.Generic.Dictionary<string, string>
                {
                    [SubtitleSourceSettingKeys.SubSource.ApiKey] = ApiKey
                }).Object,
            NewCache(),
            handler.ToClient(timeout: TimeSpan.FromMilliseconds(100)),
            NullLogger<SubSourceProvider>.Instance);

        var exception = await Assert.ThrowsAsync<SubtitleProviderException>(
            () => provider.SearchAsync(MovieRequest(["en"]), CancellationToken.None));

        Assert.Equal(SubtitleProviderFailureReason.Timeout, exception.Reason);
    }

    [Fact]
    public async Task TestConnection_WithoutApiKey_FailsFastWithoutHttpRequest()
    {
        var handler = new StubHttpMessageHandler();

        var provider = Create(handler, apiKey: null);
        var exception = await Assert.ThrowsAsync<SubtitleProviderException>(
            () => provider.TestConnectionAsync(CancellationToken.None));

        // The controller maps this onto the failed test outcome with the
        // settings-card guidance message — no keyless probe is sent because
        // the v1 API can only answer 401 to one.
        Assert.Equal(SubtitleProviderFailureReason.MissingCredentials, exception.Reason);
        Assert.Contains("API key", exception.Message);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task TestConnection_WithApiKey_ProbesMoviesSearch()
    {
        var handler = new StubHttpMessageHandler()
            .EnqueueJson(SubtitleFixtures.Read("subsource", "movies-search.json"));

        var provider = Create(handler);
        var outcome = await provider.TestConnectionAsync(CancellationToken.None);

        Assert.True(outcome.Ok);
        var url = handler.SingleRequestUri!.ToString();
        Assert.Contains("/api/v1/movies/search", url);
        Assert.Contains("searchType=text", url);
        Assert.Contains("q=lingarr", url);
        Assert.Contains("api_key=sub-key", url);
    }

    [Fact]
    public async Task Download_FetchesRawBytesFromTheV1DownloadRoute()
    {
        var payload = "1\r\n00:00:01,000 --> 00:00:03,000\r\nFake subtitle body.\r\n"u8.ToArray();
        var handler = new StubHttpMessageHandler()
            .EnqueueJson(SubtitleFixtures.Read("subsource", "movies-search.json"))
            .EnqueueJson(SubtitleFixtures.Read("subsource", "subtitles.json"))
            .Enqueue(request => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(payload)
            });

        var provider = Create(handler);
        var search = await provider.SearchAsync(MovieRequest(["en"]), CancellationToken.None);
        var candidate = search.Candidates[0]; // sub-sub-1, the first English entry

        var download = await provider.DownloadAsync(
            new SubtitleCandidateRef(candidate.ExternalId), CancellationToken.None);

        // v1 download answers the subtitle file bytes themselves — the
        // payload passes through untouched (no JSON string-array join).
        Assert.Equal(3, handler.Requests.Count);
        var downloadUrl = handler.Requests[2].RequestUri!.ToString();
        Assert.Contains("/api/v1/subtitles/sub-sub-1/download", downloadUrl);
        Assert.Contains("api_key=sub-key", downloadUrl);
        Assert.Equal(payload, download.Content);
        Assert.Equal("Movie.2019.WEBDL-1080p.en.srt", download.FileName);
        Assert.Equal("srt", download.Format);
    }
}
