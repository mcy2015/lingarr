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
/// assrt adapter against the recorded fixture (M2 acceptance): the token in
/// the query, search→detail file flattening, the status!=0 error envelope,
/// missing token failing before any HTTP, and the failure buckets.
/// </summary>
[Collection("SubtitleHttpThrottle")]
public class AssrtProviderTests
{
    // The request throttle (design ⑧) is process-wide static state — reset
    // it before every test so each case starts with an immediate first
    // request, then lift the assrt pacing (official 20/min) so the
    // multi-round fan-out cases below stay fast; the interval itself is
    // pinned by Search_TwoConsecutiveRequestsWaitForThePublishedLimit.
    public AssrtProviderTests()
    {
        SubtitleHttp.ClearRequestThrottling();
        SubtitleHttp.SetMinimumRequestInterval("assrt", TimeSpan.Zero);
    }

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
    public async Task Search_QueryOverride_UsesTheTagStrippedKeyword()
    {
        // R3b: the locked Query override replaces the title term; a trailing
        // SxxEyy tag is stripped from it and the STRUCTURED season/episode
        // is what lands in the keyword — "Show S01E01" as a query equals the
        // default derived request on every provider.
        var handler = new StubHttpMessageHandler()
            .EnqueueJson(SubtitleFixtures.Read("assrt", "search.json"))
            .EnqueueJson(SubtitleFixtures.Read("assrt", "detail.json"))
            .EnqueueJson(SubtitleFixtures.Read("assrt", "detail.json"));

        var provider = Create(handler);
        var episode = new SubtitleSearchRequest(
            "Show", null, Season: 1, Episode: 2,
            ReleaseName: null, VideoHash: null, Languages: ["zh"], ImdbId: null,
            Query: "Show S01E01");
        await provider.SearchAsync(episode, CancellationToken.None);

        var searchUrl = handler.Requests[0].RequestUri!.AbsoluteUri;
        Assert.Contains("q=Show%20S01E02", searchUrl); // structured tag, not the query's S01E01
        Assert.DoesNotContain("S01E01", searchUrl);
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
    public async Task Search_TwoConsecutiveRequestsWaitForThePublishedLimit()
    {
        // assrt's official window is 20 requests per minute with the quota
        // shared between the token and the calling IP — the throttle table
        // paces the source at one request per 3s. The per-source throttle
        // waits instead of rejecting: the second request of a burst must
        // sit out the window (tolerance: at least ~90% of the interval,
        // bounded well below two intervals), same shape as the
        // OpenSubtitles pacing case.
        SubtitleHttp.SetMinimumRequestInterval("assrt", SubtitleHttp.AssrtRequestInterval);
        var handler = new StubHttpMessageHandler()
            .EnqueueJson("""{"status": 0, "sub": {"subs": []}}""")
            .EnqueueJson("""{"status": 0, "sub": {"subs": []}}""");

        var provider = Create(handler);
        var firstStopwatch = System.Diagnostics.Stopwatch.StartNew();
        await provider.SearchAsync(MovieRequest(), CancellationToken.None);
        firstStopwatch.Stop();
        var secondStopwatch = System.Diagnostics.Stopwatch.StartNew();
        await provider.SearchAsync(MovieRequest(), CancellationToken.None);
        secondStopwatch.Stop();

        Assert.True(firstStopwatch.ElapsedMilliseconds < (int)(SubtitleHttp.AssrtRequestInterval.TotalMilliseconds * 0.9),
            $"first request must leave immediately (took {firstStopwatch.ElapsedMilliseconds}ms)");
        var minimum = (int)(SubtitleHttp.AssrtRequestInterval.TotalMilliseconds * 0.9);
        var maximum = (int)(SubtitleHttp.AssrtRequestInterval.TotalMilliseconds * 2 + 300);
        Assert.InRange((int)secondStopwatch.ElapsedMilliseconds, minimum, maximum);
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

    [Fact]
    public async Task Download_CarriesTheQuotaRemainingOfTheProducingDetailRound()
    {
        // assrt reports the account's remaining downloads on the detail
        // round (quota_remain — the documented assumption of the adapter):
        // the snapshot rides the candidate context into the download result
        // and from there into the quota telemetry.
        var detailWithQuota = """
        {
          "status": 0,
          "sub": {
            "quota_remain": 37,
            "subs": [
              {
                "id": 671234,
                "title": "Movie 2019",
                "native_name": "电影2019",
                "filelist": [
                  { "f": "Movie.2019.WEBDL-1080p.chs&eng.ass", "url": "https://api.assrt.net/sub/download/671234/quota.ass" }
                ]
              }
            ]
          }
        }
        """;
        var payload = "[Script Info]"u8.ToArray();
        var handler = new StubHttpMessageHandler()
            .EnqueueJson(SubtitleFixtures.Read("assrt", "search.json"))
            .EnqueueJson(detailWithQuota)
            .EnqueueJson(SubtitleFixtures.Read("assrt", "detail.json"))
            .Enqueue(request => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(payload)
            });

        var provider = Create(handler);
        var search = await provider.SearchAsync(MovieRequest(), CancellationToken.None);

        var download = await provider.DownloadAsync(
            new SubtitleCandidateRef(search.Candidates[0].ExternalId), CancellationToken.None);

        Assert.Equal(37, download.QuotaRemaining);
    }

    [Fact]
    public async Task Download_WithoutQuotaFieldReportsNull()
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
            new SubtitleCandidateRef(search.Candidates[0].ExternalId), CancellationToken.None);

        // Absent quota field keeps null — the telemetry preserves its last
        // recorded value instead of overwriting it with garbage.
        Assert.Null(download.QuotaRemaining);
    }

    /// <summary>
    /// A search page whose id list exceeds the fan-out cap (QA1 P1-1): the
    /// detail round stops after 20 ids regardless of the page size — one
    /// HTTP round per id is serial IO and popular titles blow the 10s
    /// orchestrator budget otherwise.
    /// </summary>
    [Fact]
    public async Task Search_CapsTheDetailFanOutAtTwentySubIds()
    {
        var ids = string.Join(", ", Enumerable.Range(0, 25)
            .Select(i => $$"""{"id": {{671001 + i}}}"""));
        var search = $$$"""{"status": 0, "sub": {"subs": [{{{ids}}}]}}""";
        var handler = new StubHttpMessageHandler().EnqueueJson(search);
        var detail = SubtitleFixtures.Read("assrt", "detail.json");
        for (var i = 0; i < 20; i++)
        {
            handler.EnqueueJson(detail);
        }

        var provider = Create(handler);
        var result = await provider.SearchAsync(MovieRequest(), CancellationToken.None);

        // 1 search + exactly 20 detail rounds — the last one for the 20th id.
        Assert.Equal(21, handler.Requests.Count);
        Assert.Contains("id=671020", handler.Requests[^1].RequestUri!.AbsoluteUri);
        // The detail fixture flattens to 2 file candidates per round.
        Assert.Equal(40, result.Candidates.Count);
    }

    /// <summary>
    /// Budget cancellation mid-fan-out (QA1 P1-1): the candidates the
    /// completed detail rounds already produced survive — cancelling the
    /// tail must not discard the accumulated head.
    /// </summary>
    [Fact]
    public async Task Search_CancelledMidDetailKeepsTheAccumulatedCandidates()
    {
        var search = """{"status": 0, "sub": {"subs": [{"id": 671001}, {"id": 671002}, {"id": 671003}]}}""";
        var cts = new CancellationTokenSource();
        var handler = new StubHttpMessageHandler()
            .EnqueueJson(search)
            .EnqueueJson(SubtitleFixtures.Read("assrt", "detail.json"))
            .Enqueue(request =>
            {
                cts.Cancel(); // the budget fires while round 2 is in flight
                return null;  // hang until the cancellation unwinds it
            });

        var provider = Create(handler);
        var result = await provider.SearchAsync(MovieRequest(), cts.Token);

        // Round 1's two file candidates survive; round 2 cancelled; round 3
        // never started.
        Assert.Equal(2, result.Candidates.Count);
        Assert.Equal(3, handler.Requests.Count); // search + detail1 + detail2
    }
}
