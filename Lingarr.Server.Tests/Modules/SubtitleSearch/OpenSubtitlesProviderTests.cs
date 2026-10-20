using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Lingarr.Server.Modules.SubtitleSearch;
using Lingarr.Server.Modules.SubtitleSearch.Abstractions;
using Lingarr.Server.Modules.SubtitleSearch.Orchestration;
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
/// returns the untouched payload. R3 adds the /download quota-header
/// contracts: the requests/remaining/reset_time_utc trio lands in the
/// singleton snapshot store (case-insensitive header lookup), a 406
/// quota-exhaustion verdict carries the parsed reset moment, and absent or
/// garbled quota headers never wipe a good snapshot.
/// </summary>
[Collection("SubtitleHttpThrottle")]
public class OpenSubtitlesProviderTests
{
    // The request throttle (design ⑧) is process-wide static state — reset
    // it before every test so each case starts with an immediate first
    // request and the documented 1 req/s OpenSubtitles limit.
    public OpenSubtitlesProviderTests()
    {
        SubtitleHttp.ClearRequestThrottling();
    }

    private static readonly string SearchFixture =
        SubtitleFixtures.Read("opensubtitles", "search.json");

    private static readonly string DownloadFixture =
        SubtitleFixtures.Read("opensubtitles", "download.json");

    private static SubtitleCandidateCache NewCache() =>
        new(new MemoryCache(new MemoryCacheOptions()), NullLogger<SubtitleCandidateCache>.Instance);

    private static OpenSubtitlesProvider Create(
        StubHttpMessageHandler handler,
        SubtitleQuotaStore? quotaStore = null,
        string? apiKey = "test-key",
        string? baseUrl = null)
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
            access.Object,
            NewCache(),
            quotaStore ?? new SubtitleQuotaStore(),
            handler.ToClient(),
            NullLogger<OpenSubtitlesProvider>.Instance);
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
    public async Task Search_ResponseIso639ThreeLetter_FoldsToTwoLetterCodes()
    {
        // OpenSubtitles answers ISO 639-3 codes on the response side
        // ("ita") while the culture-table picker offers the two-letter
        // codes — the folding map bridges the two so a checked language
        // actually matches its candidates. Unknown three-letter codes pass
        // through unchanged instead of being dropped or guessed.
        var search = """
        {"total_pages": 1, "data": [
          {"id": "1-1", "type": "subtitle", "attributes": {"language": "ita", "forced": false, "hearing_impaired": false, "uploader": {"name": "u"}, "files": [{"file_id": 11, "file_name": "Movie.2019.ita.srt"}]}},
          {"id": "2-2", "type": "subtitle", "attributes": {"language": "qaa", "forced": false, "hearing_impaired": false, "uploader": {"name": "u"}, "files": [{"file_id": 12, "file_name": "Movie.2019.qaa.srt"}]}}
        ]}
        """;
        var handler = new StubHttpMessageHandler().EnqueueJson(search);

        var provider = Create(handler);
        var result = await provider.SearchAsync(MovieRequest(), CancellationToken.None);

        Assert.Equal(2, result.Candidates.Count);
        Assert.Equal("it", result.Candidates[0].Language); // ita -> it
        Assert.Equal("qaa", result.Candidates[1].Language); // unknown: kept verbatim
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
    public async Task RateLimited429WithZeroRemaining_MapsToDailyLimit()
    {
        // Official v1 rate-limit shape: the remaining-requests header reads
        // zero when the daily quota is exhausted — that outranks the generic
        // 429 mapping and cools down until UTC midnight (store-side test).
        var handler = new StubHttpMessageHandler().Enqueue(request =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.Add("X-RateLimit-Remaining-Requests", "0");
            response.Headers.Add("X-RateLimit-Reset",
                DateTimeOffset.UtcNow.AddHours(6).ToUnixTimeSeconds().ToString());
            return response;
        });

        var provider = Create(handler);
        var exception = await Assert.ThrowsAsync<SubtitleProviderException>(
            () => provider.SearchAsync(MovieRequest(), CancellationToken.None));

        Assert.Equal(SubtitleProviderFailureReason.DailyLimit, exception.Reason);
        // The cooldown span is deliberately null — the store resolves the
        // next UTC midnight, not a fixed offset.
        Assert.Null(exception.Cooldown);
        Assert.Equal(429, exception.HttpStatus);
        Assert.Contains("daily quota", exception.Message);
    }

    [Fact]
    public async Task Search_TwoConsecutiveRequestsWaitForTheOnePerSecondBudget()
    {
        // The per-source token bucket throttles by WAITING: the second
        // request of a burst must sit out the rest of the 1s window
        // (tolerance: the wait is at least ~90% of the interval, bounded
        // well below two intervals).
        var handler = new StubHttpMessageHandler()
            .EnqueueJson(SearchFixture)
            .EnqueueJson(SearchFixture);
        var provider = Create(handler);

        var firstStopwatch = System.Diagnostics.Stopwatch.StartNew();
        await provider.SearchAsync(MovieRequest(), CancellationToken.None);
        firstStopwatch.Stop();
        var secondStopwatch = System.Diagnostics.Stopwatch.StartNew();
        await provider.SearchAsync(MovieRequest(), CancellationToken.None);
        secondStopwatch.Stop();

        Assert.True(firstStopwatch.ElapsedMilliseconds < 900,
            $"first request must leave immediately (took {firstStopwatch.ElapsedMilliseconds}ms)");
        var minimum = (int)(SubtitleHttp.OpenSubtitlesRequestInterval.TotalMilliseconds * 0.9);
        var maximum = (int)(SubtitleHttp.OpenSubtitlesRequestInterval.TotalMilliseconds * 2 + 300);
        Assert.InRange((int)secondStopwatch.ElapsedMilliseconds, minimum, maximum);
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
            new SubtitleQuotaStore(),
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

    // --- R3: /download quota-header contracts (requests / remaining /
    // reset_time_utc) — snapshot capture, the 406 exhaustion verdict, and
    // the tolerant no-signal path.

    /// <summary>A /download step-1 response with the given quota headers.</summary>
    private static HttpResponseMessage DownloadLinkResponse(
        string? remaining = null,
        string? requests = null,
        DateTimeOffset? resetUtc = null,
        HttpStatusCode status = HttpStatusCode.OK)
    {
        var response = new HttpResponseMessage(status)
        {
            Content = new StringContent(DownloadFixture, Encoding.UTF8, "application/json")
        };
        if (remaining is not null)
        {
            response.Headers.Add("remaining", remaining);
        }

        if (requests is not null)
        {
            response.Headers.Add("requests", requests);
        }

        if (resetUtc is not null)
        {
            response.Headers.Add("reset_time_utc", resetUtc.Value.ToString("O"));
        }

        return response;
    }

    private static HttpResponseMessage PayloadResponse() => new(HttpStatusCode.OK)
    {
        Content = new ByteArrayContent("1\n00:00:01,000 --> 00:00:03,000\nHello\n"u8.ToArray())
    };

    [Fact]
    public async Task Download_CapturesTheQuotaHeaderTrioIntoTheSnapshot()
    {
        // Official /download contract (R3): requests/remaining/reset_time_utc.
        // Deliberately mixed casing — HttpHeaders lookup is case-insensitive
        // and so must the capture be.
        var resetUtc = new DateTimeOffset(2026, 10, 18, 0, 0, 0, TimeSpan.Zero);
        var quotaStore = new SubtitleQuotaStore();
        var handler = new StubHttpMessageHandler()
            .EnqueueJson(SearchFixture)
            .Enqueue(_ => DownloadLinkResponse("7", "33", resetUtc))
            .Enqueue(_ => PayloadResponse());

        var provider = Create(handler, quotaStore);
        var search = await provider.SearchAsync(MovieRequest(), CancellationToken.None);
        await provider.DownloadAsync(
            new SubtitleCandidateRef(search.Candidates[0].ExternalId), CancellationToken.None);

        var snapshot = quotaStore.Find("opensubtitles");
        Assert.NotNull(snapshot);
        Assert.Equal(7, snapshot.Remaining);
        Assert.Equal(33, snapshot.Requests);
        Assert.Null(snapshot.Total); // the source sends no total header
        Assert.Equal(resetUtc, snapshot.ResetUtc);
        Assert.InRange(
            snapshot.CapturedAtUtc,
            DateTimeOffset.UtcNow.AddMinutes(-1),
            DateTimeOffset.UtcNow.AddMinutes(1));
    }

    [Fact]
    public async Task Download_QuotaExhausted406_ThrowsDailyLimitCarryingTheReportedReset()
    {
        // Official quota-exhaustion verdict: 406 with remaining: 0 plus the
        // reset moment of the quota window. The exception must carry quota
        // semantics (the DailyLimit bucket — same structural path as the
        // 429-with-zero-remaining mapping) and the parsed reset, and the
        // snapshot mirrors the stall as remaining=0.
        var resetUtc = DateTimeOffset.UtcNow.AddHours(6);
        var quotaStore = new SubtitleQuotaStore();
        var handler = new StubHttpMessageHandler()
            .EnqueueJson(SearchFixture)
            .Enqueue(_ => DownloadLinkResponse(remaining: "0", resetUtc: resetUtc,
                status: HttpStatusCode.NotAcceptable));

        var provider = Create(handler, quotaStore);
        var search = await provider.SearchAsync(MovieRequest(), CancellationToken.None);
        var exception = await Assert.ThrowsAsync<SubtitleProviderException>(
            () => provider.DownloadAsync(
                new SubtitleCandidateRef(search.Candidates[0].ExternalId), CancellationToken.None));

        Assert.Equal(SubtitleProviderFailureReason.DailyLimit, exception.Reason);
        // No fixed span: the cooldown store resolves the until (reset or the
        // midnight fallback), never the exception itself.
        Assert.Null(exception.Cooldown);
        Assert.Equal(406, exception.HttpStatus);
        Assert.Equal(resetUtc, exception.QuotaResetUtc);
        Assert.Contains("daily quota", exception.Message);

        var snapshot = quotaStore.Find("opensubtitles");
        Assert.NotNull(snapshot);
        Assert.Equal(0, snapshot.Remaining);
        Assert.Equal(resetUtc, snapshot.ResetUtc);
    }

    [Fact]
    public async Task Download_QuotaExhausted406WithoutResetHeader_CoolsDownToUtcMidnight()
    {
        // The 406 verdict without a (parseable) reset_time_utc: the exception
        // carries no reset, and the cooldown falls back to the next UTC
        // midnight — the official quota-window boundary — under the
        // quota_exhausted reason (the pipeline's recording shape).
        var handler = new StubHttpMessageHandler()
            .EnqueueJson(SearchFixture)
            .Enqueue(_ => DownloadLinkResponse(remaining: "0", status: HttpStatusCode.NotAcceptable));

        var provider = Create(handler);
        var search = await provider.SearchAsync(MovieRequest(), CancellationToken.None);
        var exception = await Assert.ThrowsAsync<SubtitleProviderException>(
            () => provider.DownloadAsync(
                new SubtitleCandidateRef(search.Candidates[0].ExternalId), CancellationToken.None));

        Assert.Equal(SubtitleProviderFailureReason.DailyLimit, exception.Reason);
        Assert.Null(exception.QuotaResetUtc);

        var settings = new InMemorySettingService();
        var store = new SubtitleSourceCooldownStore(
            settings, NullLogger<SubtitleSourceCooldownStore>.Instance);
        await store.RecordQuotaExhaustedAsync("opensubtitles", exception.QuotaResetUtc, exception.Message);
        var cooldown = (await store.GetActiveAsync())["opensubtitles"];
        Assert.Equal(SubtitleSourceCooldownStore.QuotaExhaustedReason, cooldown.Reason);
        // Exactly the next UTC day boundary (the DailyLimit fallback rule).
        Assert.Equal(TimeSpan.Zero, cooldown.Until.TimeOfDay);
        Assert.Equal(cooldown.Until, new DateTimeOffset(cooldown.Until.UtcDateTime.Date, TimeSpan.Zero));
    }

    [Fact]
    public async Task Download_WithoutUsableQuotaHeaders_KeepsTheLastGoodSnapshot()
    {
        // Absent headers never create a snapshot; a garbled value (here:
        // remaining "soon") never wipes the last good observation — the
        // download itself still succeeds either way.
        var resetUtc = DateTimeOffset.UtcNow.AddHours(6);
        var quotaStore = new SubtitleQuotaStore();
        var handler = new StubHttpMessageHandler()
            .EnqueueJson(SearchFixture)
            .Enqueue(_ => DownloadLinkResponse("7", "33", resetUtc))
            .Enqueue(_ => PayloadResponse())
            .Enqueue(_ => DownloadLinkResponse(remaining: "soon"))
            .Enqueue(_ => PayloadResponse());

        var provider = Create(handler, quotaStore);
        var search = await provider.SearchAsync(MovieRequest(), CancellationToken.None);
        var candidateRef = new SubtitleCandidateRef(search.Candidates[0].ExternalId);
        await provider.DownloadAsync(candidateRef, CancellationToken.None);
        await provider.DownloadAsync(candidateRef, CancellationToken.None);

        var snapshot = quotaStore.Find("opensubtitles");
        Assert.NotNull(snapshot);
        Assert.Equal(7, snapshot.Remaining);
        Assert.Equal(33, snapshot.Requests);
        Assert.Equal(resetUtc, snapshot.ResetUtc);
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
