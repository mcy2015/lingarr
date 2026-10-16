using System;
using System.Linq;
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
/// OpenSubtitles adapter against the recorded fixture (M2 acceptance): the
/// search URL branches (hash / imdb / title), candidate fields flatten from
/// data[].attributes.files[], the failure buckets (429 with and without
/// Retry-After, timeout, missing credentials) map onto
/// <see cref="SubtitleProviderException"/>, and the two-step download
/// returns the untouched payload.
/// </summary>
public class OpenSubtitlesProviderTests
{
    private static readonly string SearchFixture =
        SubtitleFixtures.Read("opensubtitles", "search.json");

    private static readonly string DownloadFixture =
        SubtitleFixtures.Read("opensubtitles", "download.json");

    private static SubtitleCandidateCache NewCache() =>
        new(new MemoryCache(new MemoryCacheOptions()), NullLogger<SubtitleCandidateCache>.Instance);

    private static OpenSubtitlesProvider Create(
        StubHttpMessageHandler handler, string? apiKey = "test-key", string? baseUrl = null)
    {
        var secrets = apiKey is null
            ? null
            : new System.Collections.Generic.Dictionary<string, string>
            {
                [SubtitleSourceSettingKeys.OpenSubtitles.ApiKey] = apiKey
            };
        var settings = baseUrl is null
            ? null
            : new System.Collections.Generic.Dictionary<string, string>
            {
                [SubtitleSourceSettingKeys.OpenSubtitles.BaseUrl] = baseUrl
            };
        var access = SubtitleSettingsDouble.Create(settings, secrets);
        return new OpenSubtitlesProvider(
            access.Object, NewCache(), handler.ToClient(), NullLogger<OpenSubtitlesProvider>.Instance);
    }

    private static SubtitleSearchRequest MovieRequest(string? hash = null) => new(
        "Movie", 2019, Season: null, Episode: null,
        ReleaseName: "Movie.2019.WEBDL-1080p", VideoHash: hash, Languages: ["en"], ImdbId: null);

    [Fact]
    public async Task SearchByTitle_ParsesCandidatesFromFixture()
    {
        var handler = new StubHttpMessageHandler().EnqueueJson(SearchFixture);

        var provider = Create(handler);
        var result = await provider.SearchAsync(MovieRequest(), CancellationToken.None);

        Assert.Equal(2, result.Candidates.Count);

        var first = result.Candidates[0];
        Assert.Equal("opensubtitles", first.Provider);
        Assert.Equal("en", first.Language); // eng -> en
        Assert.False(first.Forced);
        Assert.False(first.HearingImpaired);
        // release_notes outrank the file name when present.
        Assert.Equal("Movie.2019.WEBDL-1080p", first.ReleaseInfo);
        Assert.Equal("fixture-uploader", first.Uploader);
        Assert.Equal("srt", first.Format);
        Assert.False(first.HashMatch); // title branch, not a hash query
        Assert.StartsWith("subtitle-candidate:opensubtitles:", first.ExternalId);
        Assert.Equal(0, first.MatchScore); // provider never scores (②)

        var second = result.Candidates[1];
        Assert.Equal("zh", second.Language); // zho -> zh
        Assert.True(second.Forced);
        Assert.True(second.HearingImpaired);
        // No release_notes: the file name is the release fact.
        Assert.Equal("Movie.2019.WEBDL-1080p.zho.forced.srt", second.ReleaseInfo);

        // Title branch: query= + languages=eng (ISO 639-3 request mapping).
        var url = Assert.IsType<Uri>(handler.SingleRequestUri).ToString();
        Assert.Contains("subtitles?query=Movie&languages=eng", url);
        var request = handler.SingleRequest;
        Assert.Equal("test-key", Assert.Single(request.Headers.GetValues("Api-Key")));
        Assert.Contains(request.Headers.UserAgent, agent => agent.Product!.Name == "Lingarr");
    }

    [Fact]
    public async Task SearchByHash_QueriesMovieHashesAndMarksHashMatch()
    {
        var handler = new StubHttpMessageHandler().EnqueueJson(SearchFixture);

        var provider = Create(handler);
        var result = await provider.SearchAsync(MovieRequest(hash: "abc123def4567890"), CancellationToken.None);

        var url = Assert.IsType<Uri>(handler.SingleRequestUri).ToString();
        Assert.Contains("movie_hashes=abc123def4567890", url);
        Assert.All(result.Candidates, candidate => Assert.True(candidate.HashMatch));
    }

    [Fact]
    public async Task SearchByImdbId_PrefersImdbOverTitle()
    {
        var handler = new StubHttpMessageHandler().EnqueueJson(SearchFixture);
        var provider = Create(handler);

        var request = MovieRequest() with { ImdbId = "tt4154796" };
        await provider.SearchAsync(request, CancellationToken.None);

        var url = handler.SingleRequestUri!.ToString();
        Assert.Contains("imdb_id=4154796", url); // tt prefix stripped
        Assert.DoesNotContain("query=", url);
    }

    [Fact]
    public async Task EmptyResult_ReturnsZeroCandidates()
    {
        var handler = new StubHttpMessageHandler()
            .EnqueueJson("""{"total_pages": 1, "data": []}""");

        var provider = Create(handler);
        var result = await provider.SearchAsync(MovieRequest(), CancellationToken.None);

        Assert.Empty(result.Candidates);
    }

    [Fact]
    public async Task RateLimited429_MapsToCooldownException()
    {
        var handler = new StubHttpMessageHandler().EnqueueStatus(HttpStatusCode.TooManyRequests, retryAfter: "7200");

        var provider = Create(handler);
        var exception = await Assert.ThrowsAsync<SubtitleProviderException>(
            () => provider.SearchAsync(MovieRequest(), CancellationToken.None));

        Assert.Equal(SubtitleProviderFailureReason.RateLimited, exception.Reason);
        Assert.Equal(7200, exception.RetryAfterSeconds);
        // ⑧: one hour — unless the source explicitly asks for longer.
        Assert.Equal(TimeSpan.FromSeconds(7200), exception.Cooldown);
    }

    [Fact]
    public async Task RateLimited429WithoutRetryAfter_CoolsDownOneHour()
    {
        var handler = new StubHttpMessageHandler().EnqueueStatus(HttpStatusCode.TooManyRequests);

        var provider = Create(handler);
        var exception = await Assert.ThrowsAsync<SubtitleProviderException>(
            () => provider.SearchAsync(MovieRequest(), CancellationToken.None));

        Assert.Equal(TimeSpan.FromHours(1), exception.Cooldown);
    }

    [Fact]
    public async Task Timeout_MapsToTimeoutBucketWithCooldown()
    {
        var handler = new StubHttpMessageHandler().EnqueueHang();

        var secrets = new System.Collections.Generic.Dictionary<string, string>
        {
            [SubtitleSourceSettingKeys.OpenSubtitles.ApiKey] = "test-key"
        };
        var provider = new OpenSubtitlesProvider(
            SubtitleSettingsDouble.Create(secrets: secrets).Object,
            NewCache(),
            handler.ToClient(timeout: TimeSpan.FromMilliseconds(100)),
            NullLogger<OpenSubtitlesProvider>.Instance);

        var exception = await Assert.ThrowsAsync<SubtitleProviderException>(
            () => provider.SearchAsync(MovieRequest(), CancellationToken.None));

        Assert.Equal(SubtitleProviderFailureReason.Timeout, exception.Reason);
        Assert.Equal(TimeSpan.FromMinutes(10), exception.Cooldown);
    }

    [Fact]
    public async Task MissingApiKey_FailsBeforeAnyHttpRequest()
    {
        var handler = new StubHttpMessageHandler();

        var provider = Create(handler, apiKey: null);
        var exception = await Assert.ThrowsAsync<SubtitleProviderException>(
            () => provider.SearchAsync(MovieRequest(), CancellationToken.None));

        Assert.Equal(SubtitleProviderFailureReason.MissingCredentials, exception.Reason);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Unauthorized401_MapsToCredentialFailure()
    {
        var handler = new StubHttpMessageHandler().EnqueueStatus(HttpStatusCode.Unauthorized);

        var provider = Create(handler);
        var exception = await Assert.ThrowsAsync<SubtitleProviderException>(
            () => provider.SearchAsync(MovieRequest(), CancellationToken.None));

        Assert.Equal(SubtitleProviderFailureReason.Unauthorized, exception.Reason);
        Assert.Null(exception.Cooldown); // until credentials change (⑧)
    }

    [Fact]
    public async Task Download_TwoStepsResolveFileIdToPayload()
    {
        var payload = "1\n00:00:01,000 --> 00:00:03,000\nHello\n"u8.ToArray();
        var handler = new StubHttpMessageHandler()
            .EnqueueJson(SearchFixture)
            .EnqueueJson(DownloadFixture)
            .Enqueue(request => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(payload)
            });

        var provider = Create(handler);
        var search = await provider.SearchAsync(MovieRequest(), CancellationToken.None);
        var candidate = search.Candidates[0];

        var download = await provider.DownloadAsync(
            new SubtitleCandidateRef(candidate.ExternalId), CancellationToken.None);

        Assert.Equal("opensubtitles", download.Provider);
        Assert.Equal("Movie.2019.WEBDL-1080p.srt", download.FileName);
        Assert.Equal("srt", download.Format);
        Assert.Equal(payload, download.Content);

        // POST /download with the cached file_id, then GET the returned link.
        Assert.Equal(3, handler.Requests.Count);
        Assert.Equal(HttpMethod.Post, handler.Requests[1].Method);
        Assert.True(handler.Requests[1].RequestUri!.ToString().EndsWith("/download"));
        Assert.Equal("https://dl.opensubtitles.org/files/fixture/100613334",
            handler.Requests[2].RequestUri!.ToString());
    }

    [Fact]
    public async Task Download_ExpiredCandidateRef_ReportsNotFound()
    {
        var handler = new StubHttpMessageHandler().EnqueueJson(SearchFixture);

        var provider = Create(handler);
        await provider.SearchAsync(MovieRequest(), CancellationToken.None);

        // 10-minute TTL (②): an unknown key is the expired-window case.
        var exception = await Assert.ThrowsAsync<SubtitleProviderException>(
            () => provider.DownloadAsync(
                new SubtitleCandidateRef("subtitle-candidate:opensubtitles:not-a-real-key"),
                CancellationToken.None));

        Assert.Equal(SubtitleProviderFailureReason.NotFound, exception.Reason);
    }

    [Fact]
    public async Task TestConnection_ProbesWithALightweightSearch()
    {
        var handler = new StubHttpMessageHandler().EnqueueJson(SearchFixture);

        var provider = Create(handler);
        var outcome = await provider.TestConnectionAsync(CancellationToken.None);

        Assert.True(outcome.Ok);
        Assert.Contains("40", outcome.Message); // quota constant surfaces in the verdict
        Assert.Contains("languages=eng", handler.SingleRequestUri!.ToString());
    }

    [Fact]
    public void DailyDownloadQuota_IsTheDocumentedForty()
    {
        // Written into quota_limit (display-only waterline, PM decision ⑩).
        Assert.Equal(40, OpenSubtitlesProvider.DailyDownloadQuota);
    }
}
