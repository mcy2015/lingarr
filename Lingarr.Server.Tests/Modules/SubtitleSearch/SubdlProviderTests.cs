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
/// SubDL adapter against the recorded fixture (M2 acceptance): GET /auto
/// with the uppercase language mapping, the optional api_key (absent for
/// anonymous access), the failure buckets, and the site-relative download
/// path joined onto the (overridable) base URL.
/// </summary>
public class SubdlProviderTests
{
    private static SubtitleCandidateCache NewCache() =>
        new(new MemoryCache(new MemoryCacheOptions()), NullLogger<SubtitleCandidateCache>.Instance);

    private static SubdlProvider Create(
        StubHttpMessageHandler handler, string? apiKey = null, string? baseUrl = null)
    {
        var secrets = apiKey is null
            ? null
            : new System.Collections.Generic.Dictionary<string, string>
            {
                [SubtitleSourceSettingKeys.Subdl.ApiKey] = apiKey
            };
        var settings = baseUrl is null
            ? null
            : new System.Collections.Generic.Dictionary<string, string>
            {
                [SubtitleSourceSettingKeys.Subdl.BaseUrl] = baseUrl
            };
        return new SubdlProvider(
            SubtitleSettingsDouble.Create(settings, secrets).Object,
            NewCache(), handler.ToClient(), NullLogger<SubdlProvider>.Instance);
    }

    private static SubtitleSearchRequest MovieRequest() => new(
        "Movie", 2019, Season: null, Episode: null,
        ReleaseName: null, VideoHash: null, Languages: ["zh", "en"], ImdbId: null);

    [Fact]
    public async Task Search_ParsesCandidatesWithMappedLanguages()
    {
        var handler = new StubHttpMessageHandler()
            .EnqueueJson(SubtitleFixtures.Read("subdl", "search.json"));

        var provider = Create(handler);
        var result = await provider.SearchAsync(MovieRequest(), CancellationToken.None);

        // languages=ZH,EN (uppercase mapping); the fixture's ZH and EN
        // results map back onto canonical codes.
        var url = handler.SingleRequestUri!.ToString();
        Assert.Contains("languages=ZH,EN", url);
        Assert.DoesNotContain("api_key=", url); // anonymous by default

        Assert.Equal(2, result.Candidates.Count);
        Assert.Equal("zh", result.Candidates[0].Language);
        Assert.Equal("en", result.Candidates[1].Language);
        Assert.Equal("Movie.2019.WEBDL-1080p", result.Candidates[0].ReleaseInfo);
        Assert.Equal("fixture-author", result.Candidates[0].Uploader);
        Assert.Equal("zip", result.Candidates[0].Format);
        Assert.False(result.Candidates[0].HashMatch);
        Assert.StartsWith("subtitle-candidate:subdl:", result.Candidates[0].ExternalId);
    }

    [Fact]
    public async Task Search_WithApiKey_SendsIt()
    {
        var handler = new StubHttpMessageHandler()
            .EnqueueJson(SubtitleFixtures.Read("subdl", "search.json"));

        var provider = Create(handler, apiKey: "public-key");
        await provider.SearchAsync(MovieRequest(), CancellationToken.None);

        Assert.Contains("api_key=public-key", handler.SingleRequestUri!.ToString());
    }

    [Fact]
    public async Task StatusFalse_IsAnEmptyResult()
    {
        var handler = new StubHttpMessageHandler()
            .EnqueueJson("""{"status": false, "error": null, "results": []}""");

        var provider = Create(handler);
        var result = await provider.SearchAsync(MovieRequest(), CancellationToken.None);

        Assert.Empty(result.Candidates);
    }

    [Fact]
    public async Task RateLimited429_MapsToCooldownException()
    {
        var handler = new StubHttpMessageHandler()
            .EnqueueStatus(HttpStatusCode.TooManyRequests, retryAfter: "120");

        var provider = Create(handler);
        var exception = await Assert.ThrowsAsync<SubtitleProviderException>(
            () => provider.SearchAsync(MovieRequest(), CancellationToken.None));

        Assert.Equal(SubtitleProviderFailureReason.RateLimited, exception.Reason);
        // 120s < 1h: the ⑧ floor wins.
        Assert.Equal(TimeSpan.FromHours(1), exception.Cooldown);
    }

    [Fact]
    public async Task Timeout_MapsToTimeoutBucket()
    {
        var handler = new StubHttpMessageHandler().EnqueueHang();

        var provider = new SubdlProvider(
            SubtitleSettingsDouble.Create().Object,
            NewCache(),
            handler.ToClient(timeout: TimeSpan.FromMilliseconds(100)),
            NullLogger<SubdlProvider>.Instance);

        var exception = await Assert.ThrowsAsync<SubtitleProviderException>(
            () => provider.SearchAsync(MovieRequest(), CancellationToken.None));

        Assert.Equal(SubtitleProviderFailureReason.Timeout, exception.Reason);
    }

    [Fact]
    public async Task Download_ResolvesSiteRelativePathAgainstConfiguredBase()
    {
        var payload = new byte[] { 0x50, 0x4B, 0x03, 0x04 }; // zip magic
        var handler = new StubHttpMessageHandler()
            .EnqueueJson(SubtitleFixtures.Read("subdl", "search.json"))
            .Enqueue(request => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(payload)
            });

        // Base override is the fake-source mechanism (PRD AC25) — the
        // site-relative /subtitle/... path must join onto it.
        var provider = Create(handler, baseUrl: "http://fake.subdl/");
        var search = await provider.SearchAsync(MovieRequest(), CancellationToken.None);

        var download = await provider.DownloadAsync(
            new SubtitleCandidateRef(search.Candidates[0].ExternalId), CancellationToken.None);

        Assert.Equal("http://fake.subdl/subtitle/movie-2019-ZH.zip",
            handler.Requests[1].RequestUri!.ToString());
        Assert.Equal("zip", download.Format);
        Assert.Equal("movie-2019-ZH.zip", download.FileName);
        Assert.Equal(payload, download.Content);
    }
}
