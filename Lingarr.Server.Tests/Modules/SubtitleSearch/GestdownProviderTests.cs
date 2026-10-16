using System;
using System.Linq;
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
/// Gestdown adapter against the recorded fixture (M2 acceptance): exact-name
/// show resolution beats the first hit, the episode page parses Addic7ed
/// style versions, a documented 404 is a miss not an error, movies answer
/// empty without any HTTP call, and the failure buckets map correctly.
/// </summary>
public class GestdownProviderTests
{
    private static SubtitleCandidateCache NewCache() =>
        new(new MemoryCache(new MemoryCacheOptions()), NullLogger<SubtitleCandidateCache>.Instance);

    private static GestdownProvider Create(StubHttpMessageHandler handler, string? baseUrl = null)
    {
        var settings = baseUrl is null
            ? null
            : new System.Collections.Generic.Dictionary<string, string>
            {
                [SubtitleSourceSettingKeys.Gestdown.BaseUrl] = baseUrl
            };
        return new GestdownProvider(
            SubtitleSettingsDouble.Create(settings).Object,
            NewCache(), handler.ToClient(), NullLogger<GestdownProvider>.Instance);
    }

    private static SubtitleSearchRequest EpisodeRequest() => new(
        "Show", 2020, Season: 1, Episode: 2,
        ReleaseName: "Show.S01E02.WEBDL-1080p", VideoHash: null, Languages: ["en"], ImdbId: null);

    [Fact]
    public async Task Search_ResolvesExactShowMatchAndParsesVersions()
    {
        var handler = new StubHttpMessageHandler()
            .EnqueueJson(SubtitleFixtures.Read("gestdown", "shows-search.json"))
            .EnqueueJson(SubtitleFixtures.Read("gestdown", "subtitles-get.json"));

        var provider = Create(handler, baseUrl: "http://fake.gestdown/");
        var result = await provider.SearchAsync(EpisodeRequest(), CancellationToken.None);

        // The fixture lists "Other Show" first — the exact title match
        // ("Show", uuid e3c8…) must win.
        Assert.Contains("shows/search?search=Show", handler.Requests[0].RequestUri!.ToString());
        Assert.Contains("/e3c8ec3c-5e4c-5b83-cb1f-34a3d6b2b234/1/2/en",
            handler.Requests[1].RequestUri!.ToString());

        Assert.Equal(2, result.Candidates.Count);
        var candidate = result.Candidates[0];
        Assert.Equal("gestdown", candidate.Provider);
        Assert.Equal("en", candidate.Language);
        Assert.Equal("720p.WEB-DL", candidate.ReleaseInfo);
        Assert.Equal("srt", candidate.Format);
        Assert.False(candidate.HashMatch);
        Assert.True(result.Candidates[1].HearingImpaired);
        Assert.StartsWith("subtitle-candidate:gestdown:", candidate.ExternalId);
    }

    [Fact]
    public async Task Search_MovieRequest_ReturnsEmptyWithoutHttpRequest()
    {
        var handler = new StubHttpMessageHandler();

        var provider = Create(handler);
        var movie = new SubtitleSearchRequest(
            "Movie", 2019, Season: null, Episode: null,
            ReleaseName: null, VideoHash: null, Languages: ["en"], ImdbId: null);
        var result = await provider.SearchAsync(movie, CancellationToken.None);

        Assert.Empty(result.Candidates);
        Assert.Empty(handler.Requests); // episode-only catalogue (⑩)
    }

    [Fact]
    public async Task Search_UnknownShow_IsAnEmptyResult()
    {
        var handler = new StubHttpMessageHandler()
            .EnqueueJson("[]");

        var provider = Create(handler);
        var result = await provider.SearchAsync(EpisodeRequest(), CancellationToken.None);

        Assert.Empty(result.Candidates);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Search_EpisodeMiss404_IsAnEmptyResultNotAnError()
    {
        var handler = new StubHttpMessageHandler()
            .EnqueueJson(SubtitleFixtures.Read("gestdown", "shows-search.json"))
            .EnqueueStatus(HttpStatusCode.NotFound);

        var provider = Create(handler);
        var result = await provider.SearchAsync(EpisodeRequest(), CancellationToken.None);

        // Documented "no subtitles for this episode" — a miss, not a failure.
        Assert.Empty(result.Candidates);
    }

    [Fact]
    public async Task RateLimited429_MapsToCooldownException()
    {
        var handler = new StubHttpMessageHandler()
            .EnqueueStatus(HttpStatusCode.TooManyRequests, retryAfter: "7200");

        var provider = Create(handler);
        var exception = await Assert.ThrowsAsync<SubtitleProviderException>(
            () => provider.SearchAsync(EpisodeRequest(), CancellationToken.None));

        Assert.Equal(SubtitleProviderFailureReason.RateLimited, exception.Reason);
        Assert.Equal(TimeSpan.FromSeconds(7200), exception.Cooldown);
    }

    [Fact]
    public async Task Timeout_MapsToTimeoutBucket()
    {
        var handler = new StubHttpMessageHandler().EnqueueHang();

        var provider = new GestdownProvider(
            SubtitleSettingsDouble.Create().Object,
            NewCache(),
            handler.ToClient(timeout: TimeSpan.FromMilliseconds(100)),
            NullLogger<GestdownProvider>.Instance);

        var exception = await Assert.ThrowsAsync<SubtitleProviderException>(
            () => provider.SearchAsync(EpisodeRequest(), CancellationToken.None));

        Assert.Equal(SubtitleProviderFailureReason.Timeout, exception.Reason);
    }

    [Fact]
    public async Task Download_FetchesSrtFromResolvedLink()
    {
        var payload = "1\n00:00:01,000 --> 00:00:02,000\nHi\r\n"u8.ToArray();
        var handler = new StubHttpMessageHandler()
            .EnqueueJson(SubtitleFixtures.Read("gestdown", "shows-search.json"))
            .EnqueueJson(SubtitleFixtures.Read("gestdown", "subtitles-get.json"))
            .Enqueue(request => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(payload)
            });

        var provider = Create(handler, baseUrl: "http://fake.gestdown/");
        var search = await provider.SearchAsync(EpisodeRequest(), CancellationToken.None);

        var download = await provider.DownloadAsync(
            new SubtitleCandidateRef(search.Candidates[0].ExternalId), CancellationToken.None);

        Assert.Contains("/subtitles/download/sub-uuid-1", handler.Requests[2].RequestUri!.ToString());
        Assert.Equal("srt", download.Format);
        Assert.Equal(payload, download.Content);
    }
}
