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
/// assrt adapter against the recorded fixture (M2 acceptance): the token in
/// the query, search→detail file flattening, the status!=0 error envelope,
/// missing token failing before any HTTP, and the failure buckets.
/// </summary>
public class AssrtProviderTests
{
    private static SubtitleCandidateCache NewCache() =>
        new(new MemoryCache(new MemoryCacheOptions()), NullLogger<SubtitleCandidateCache>.Instance);

    private static AssrtProvider Create(StubHttpMessageHandler handler, string? token = "test-token")
    {
        var secrets = token is null
            ? null
            : new System.Collections.Generic.Dictionary<string, string>
            {
                [SubtitleSourceSettingKeys.Assrt.Token] = token
            };
        return new AssrtProvider(
            SubtitleSettingsDouble.Create(secrets: secrets).Object,
            NewCache(), handler.ToClient(), NullLogger<AssrtProvider>.Instance);
    }

    private static SubtitleSearchRequest MovieRequest() => new(
        "Movie", 2019, Season: null, Episode: null,
        ReleaseName: null, VideoHash: null, Languages: ["zh"], ImdbId: null);

    [Fact]
    public async Task Search_FetchesDetailPerSubAndFlattensFileList()
    {
        var handler = new StubHttpMessageHandler()
            .EnqueueJson(SubtitleFixtures.Read("assrt", "search.json"))
            .EnqueueJson(SubtitleFixtures.Read("assrt", "detail.json"))
            .EnqueueJson(SubtitleFixtures.Read("assrt", "detail.json"));

        var provider = Create(handler);
        var result = await provider.SearchAsync(MovieRequest(), CancellationToken.None);

        // Search + one detail round per sub id (fixture carries two ids).
        Assert.Equal(3, handler.Requests.Count);
        var searchUrl = handler.Requests[0].RequestUri!.AbsoluteUri;
        Assert.Contains("sub/search", searchUrl);
        Assert.Contains("q=Movie%202019", searchUrl); // space escaped in the query
        Assert.Contains("token=test-token", searchUrl);
        Assert.Contains("id=671234", handler.Requests[1].RequestUri!.AbsoluteUri);
        Assert.Contains("id=671235", handler.Requests[2].RequestUri!.AbsoluteUri);

        // 2 sub ids x 2 filelist entries = 4 candidates.
        Assert.Equal(4, result.Candidates.Count);
        var candidate = result.Candidates[0];
        Assert.Equal("assrt", candidate.Provider);
        Assert.Equal("zh", candidate.Language); // Chinese mainstay: primary requested code
        Assert.Equal("电影2019", candidate.ReleaseInfo); // native_name preferred over title
        Assert.Equal("ass", candidate.Format);
        Assert.Equal("srt", result.Candidates[1].Format);
        Assert.StartsWith("subtitle-candidate:assrt:", candidate.ExternalId);
        Assert.False(candidate.HashMatch);
    }

    [Fact]
    public async Task SearchEpisode_IncludesSeasonEpisodePatternInQuery()
    {
        var handler = new StubHttpMessageHandler()
            .EnqueueJson(SubtitleFixtures.Read("assrt", "search.json"))
            .EnqueueJson(SubtitleFixtures.Read("assrt", "detail.json"))
            .EnqueueJson(SubtitleFixtures.Read("assrt", "detail.json"));

        var provider = Create(handler);
        var episode = new SubtitleSearchRequest(
            "Show", null, Season: 1, Episode: 2,
            ReleaseName: null, VideoHash: null, Languages: ["zh"], ImdbId: null);
        await provider.SearchAsync(episode, CancellationToken.None);

        Assert.Contains("q=Show%20S01E02", handler.Requests[0].RequestUri!.AbsoluteUri);
    }

    [Fact]
    public async Task EmptySearch_IsAnEmptyResult()
    {
        var handler = new StubHttpMessageHandler()
            .EnqueueJson("""{"status": 0, "sub": {"subs": []}}""");

        var provider = Create(handler);
        var result = await provider.SearchAsync(MovieRequest(), CancellationToken.None);

        Assert.Empty(result.Candidates);
        Assert.Single(handler.Requests); // no detail rounds without sub ids
    }

    [Fact]
    public async Task StatusEnvelopeError_MapsToInvalidResponse()
    {
        var handler = new StubHttpMessageHandler()
            .EnqueueJson("""{"status": 7, "message": "invalid token"}""");

        var provider = Create(handler);
        var exception = await Assert.ThrowsAsync<SubtitleProviderException>(
            () => provider.SearchAsync(MovieRequest(), CancellationToken.None));

        Assert.Equal(SubtitleProviderFailureReason.InvalidResponse, exception.Reason);
        Assert.Contains("invalid token", exception.Message);
    }

    [Fact]
    public async Task MissingToken_FailsBeforeAnyHttpRequest()
    {
        var handler = new StubHttpMessageHandler();

        var provider = Create(handler, token: null);
        var exception = await Assert.ThrowsAsync<SubtitleProviderException>(
            () => provider.SearchAsync(MovieRequest(), CancellationToken.None));

        Assert.Equal(SubtitleProviderFailureReason.MissingCredentials, exception.Reason);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task RateLimited429_MapsToCooldownException()
    {
        var handler = new StubHttpMessageHandler().EnqueueStatus(HttpStatusCode.TooManyRequests);

        var provider = Create(handler);
        var exception = await Assert.ThrowsAsync<SubtitleProviderException>(
            () => provider.SearchAsync(MovieRequest(), CancellationToken.None));

        Assert.Equal(SubtitleProviderFailureReason.RateLimited, exception.Reason);
        Assert.Equal(TimeSpan.FromHours(1), exception.Cooldown);
    }

    [Fact]
    public async Task Timeout_MapsToTimeoutBucket()
    {
        var handler = new StubHttpMessageHandler().EnqueueHang();

        var provider = new AssrtProvider(
            SubtitleSettingsDouble.Create(
                secrets: new System.Collections.Generic.Dictionary<string, string>
                {
                    [SubtitleSourceSettingKeys.Assrt.Token] = "test-token"
                }).Object,
            NewCache(),
            handler.ToClient(timeout: TimeSpan.FromMilliseconds(100)),
            NullLogger<AssrtProvider>.Instance);

        var exception = await Assert.ThrowsAsync<SubtitleProviderException>(
            () => provider.SearchAsync(MovieRequest(), CancellationToken.None));

        Assert.Equal(SubtitleProviderFailureReason.Timeout, exception.Reason);
    }

    [Fact]
    public async Task Download_FetchesFileFromCachedUrl()
    {
        var payload = "[Script Info]"u8.ToArray();
        var handler = new StubHttpMessageHandler()
            .EnqueueJson(SubtitleFixtures.Read("assrt", "search.json"))
            .EnqueueJson(SubtitleFixtures.Read("assrt", "detail.json"))
            .EnqueueJson(SubtitleFixtures.Read("assrt", "detail.json"))
            .Enqueue(request => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(payload)
            });

        var provider = Create(handler);
        var search = await provider.SearchAsync(MovieRequest(), CancellationToken.None);

        var download = await provider.DownloadAsync(
            new SubtitleCandidateRef(search.Candidates[1].ExternalId), CancellationToken.None);

        Assert.Equal("https://api.assrt.net/sub/download/671234/fixture-2.srt",
            handler.Requests[3].RequestUri!.AbsoluteUri);
        Assert.Equal("Movie.2019.WEBDL-1080p.chs.srt", download.FileName);
        Assert.Equal("srt", download.Format);
        Assert.Equal(payload, download.Content);
    }
}
