using System;
using System.Net;
using System.Net.Http;
using System.Text;
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
/// SubSource adapter against the recorded fixture (M2 acceptance): POST
/// search per media shape, client-side language filter (ENG/CHS labels →
/// canonical codes), the failure buckets, and the getSub download whose
/// body lives inside the JSON response.
/// </summary>
public class SubSourceProviderTests
{
    private static SubtitleCandidateCache NewCache() =>
        new(new MemoryCache(new MemoryCacheOptions()), NullLogger<SubtitleCandidateCache>.Instance);

    private static SubSourceProvider Create(StubHttpMessageHandler handler, string? baseUrl = null)
    {
        var settings = baseUrl is null
            ? null
            : new System.Collections.Generic.Dictionary<string, string>
            {
                [SubtitleSourceSettingKeys.SubSource.BaseUrl] = baseUrl
            };
        return new SubSourceProvider(
            SubtitleSettingsDouble.Create(settings).Object,
            NewCache(), handler.ToClient(), NullLogger<SubSourceProvider>.Instance);
    }

    private static SubtitleSearchRequest MovieRequest(string[] languages) => new(
        "Movie", 2019, Season: null, Episode: null,
        ReleaseName: null, VideoHash: null, Languages: languages, ImdbId: null);

    [Fact]
    public async Task SearchMovie_ParsesCandidatesAndFiltersByLanguage()
    {
        var handler = new StubHttpMessageHandler()
            .EnqueueJson(SubtitleFixtures.Read("subsource", "search-movie.json"));

        var provider = Create(handler);
        var result = await provider.SearchAsync(MovieRequest(["en"]), CancellationToken.None);

        // The fixture carries one ENG and one CHS sub; only the requested
        // language survives the client-side filter.
        var candidate = Assert.Single(result.Candidates);
        Assert.Equal("subsource", candidate.Provider);
        Assert.Equal("en", candidate.Language);
        Assert.Equal("Movie.2019.WEBDL-1080p", candidate.ReleaseInfo);
        Assert.Equal("fixture-author", candidate.Uploader);
        Assert.False(candidate.HearingImpaired);
        Assert.False(candidate.HashMatch);
        Assert.StartsWith("subtitle-candidate:subsource:", candidate.ExternalId);

        var request = handler.SingleRequest;
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.True(request.RequestUri!.ToString().EndsWith("/api/searchMovie"),
            $"unexpected url {request.RequestUri}");
        Assert.NotNull(request.Content);
    }

    [Fact]
    public async Task SearchSeason_PostsShowSeasonEpisode()
    {
        var handler = new StubHttpMessageHandler()
            .EnqueueJson(SubtitleFixtures.Read("subsource", "search-season.json"));

        var provider = Create(handler);
        var episodeRequest = new SubtitleSearchRequest(
            "Show", null, Season: 1, Episode: 2,
            ReleaseName: null, VideoHash: null, Languages: ["en"], ImdbId: null);
        var result = await provider.SearchAsync(episodeRequest, CancellationToken.None);

        Assert.True(handler.SingleRequestUri!.ToString().EndsWith("/api/searchSeason"));
        var candidate = Assert.Single(result.Candidates);
        Assert.Equal("Show.S01E02.WEBDL-1080p", candidate.ReleaseInfo);
    }

    [Fact]
    public async Task NotFoundSub_IsAnEmptyResult()
    {
        var handler = new StubHttpMessageHandler()
            .EnqueueJson("""{"found": false, "subs": []}""");

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
    public async Task Timeout_MapsToTimeoutBucket()
    {
        var handler = new StubHttpMessageHandler().EnqueueHang();

        var provider = new SubSourceProvider(
            SubtitleSettingsDouble.Create().Object,
            NewCache(),
            handler.ToClient(timeout: TimeSpan.FromMilliseconds(100)),
            NullLogger<SubSourceProvider>.Instance);

        var exception = await Assert.ThrowsAsync<SubtitleProviderException>(
            () => provider.SearchAsync(MovieRequest(["en"]), CancellationToken.None));

        Assert.Equal(SubtitleProviderFailureReason.Timeout, exception.Reason);
    }

    [Fact]
    public async Task Download_ReturnsJoinedBodyAsPayload()
    {
        var handler = new StubHttpMessageHandler()
            .EnqueueJson(SubtitleFixtures.Read("subsource", "search-movie.json"))
            .EnqueueJson(SubtitleFixtures.Read("subsource", "getsub.json"));

        var provider = Create(handler);
        var search = await provider.SearchAsync(MovieRequest(["en"]), CancellationToken.None);
        var candidate = Assert.Single(search.Candidates);

        var download = await provider.DownloadAsync(
            new SubtitleCandidateRef(candidate.ExternalId), CancellationToken.None);

        Assert.Equal("subsource", download.Provider);
        Assert.Equal("srt", download.Format);
        Assert.EndsWith(".srt", download.FileName);
        var text = Encoding.UTF8.GetString(download.Content);
        Assert.Contains("Fake subtitle body.", text);
        Assert.Contains("Second cue.", text);
        Assert.Equal(2, handler.Requests.Count);
    }
}
