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
/// Podnapisi adapter against the recorded XML fixture (M2 acceptance): the
/// search URL carries keywords/language(/season/episode), subtitle nodes
/// parse release/uploader/format/hearing-impaired, the download link joins
/// onto the configured base, and the failure buckets map correctly.
/// Protocol per the subliminal (MIT) reference, fixture-pinned here.
/// </summary>
public class PodnapisiProviderTests
{
    private static SubtitleCandidateCache NewCache() =>
        new(new MemoryCache(new MemoryCacheOptions()), NullLogger<SubtitleCandidateCache>.Instance);

    private static PodnapisiProvider Create(StubHttpMessageHandler handler, string? baseUrl = null)
    {
        var settings = baseUrl is null
            ? null
            : new System.Collections.Generic.Dictionary<string, string>
            {
                [SubtitleSourceSettingKeys.Podnapisi.BaseUrl] = baseUrl
            };
        return new PodnapisiProvider(
            SubtitleSettingsDouble.Create(settings).Object,
            NewCache(), handler.ToClient(), NullLogger<PodnapisiProvider>.Instance);
    }

    private static StubHttpMessageHandler EnqueueSearchFixture()
        => new StubHttpMessageHandler().EnqueueXml(SubtitleFixtures.Read("podnapisi", "search.xml"));

    [Fact]
    public async Task Search_ParsesSubtitleNodes()
    {
        var handler = EnqueueSearchFixture();

        var provider = Create(handler);
        var movie = new SubtitleSearchRequest(
            "Movie", 2019, Season: null, Episode: null,
            ReleaseName: null, VideoHash: null, Languages: ["en"], ImdbId: null);
        var result = await provider.SearchAsync(movie, CancellationToken.None);

        var url = handler.SingleRequestUri!.ToString();
        Assert.Contains("keywords=Movie", url);
        Assert.Contains("language=en", url);
        Assert.Contains("year=2019", url);

        Assert.Equal(2, result.Candidates.Count);
        var candidate = result.Candidates[0];
        Assert.Equal("podnapisi", candidate.Provider);
        Assert.Equal("en", candidate.Language);
        Assert.Equal("Movie.2019.WEBDL-1080p", candidate.ReleaseInfo);
        Assert.Equal("fixture-uploader", candidate.Uploader);
        Assert.Equal("zip", candidate.Format);
        Assert.False(candidate.HearingImpaired);
        Assert.True(result.Candidates[1].HearingImpaired); // hearing-impaired="1"
        Assert.Equal("srt", result.Candidates[1].Format);
        Assert.StartsWith("subtitle-candidate:podnapisi:", candidate.ExternalId);
    }

    [Fact]
    public async Task SearchEpisode_AddsSeasonAndEpisodeParameters()
    {
        var handler = EnqueueSearchFixture();

        var provider = Create(handler);
        var episode = new SubtitleSearchRequest(
            "Show", null, Season: 1, Episode: 2,
            ReleaseName: null, VideoHash: null, Languages: ["en"], ImdbId: null);
        await provider.SearchAsync(episode, CancellationToken.None);

        var url = handler.SingleRequestUri!.ToString();
        Assert.Contains("season=1", url);
        Assert.Contains("episode=2", url);
    }

    [Fact]
    public async Task EmptyFeed_IsAnEmptyResult()
    {
        var handler = new StubHttpMessageHandler()
            .EnqueueXml("<?xml version='1.0' encoding='UTF-8'?><subtitles></subtitles>");

        var provider = Create(handler);
        var movie = new SubtitleSearchRequest(
            "Movie", null, Season: null, Episode: null,
            ReleaseName: null, VideoHash: null, Languages: ["en"], ImdbId: null);
        var result = await provider.SearchAsync(movie, CancellationToken.None);

        Assert.Empty(result.Candidates);
    }

    [Fact]
    public async Task RateLimited429_MapsToCooldownException()
    {
        var handler = new StubHttpMessageHandler().EnqueueStatus(HttpStatusCode.TooManyRequests);

        var provider = Create(handler);
        var movie = new SubtitleSearchRequest(
            "Movie", null, Season: null, Episode: null,
            ReleaseName: null, VideoHash: null, Languages: ["en"], ImdbId: null);

        var exception = await Assert.ThrowsAsync<SubtitleProviderException>(
            () => provider.SearchAsync(movie, CancellationToken.None));

        Assert.Equal(SubtitleProviderFailureReason.RateLimited, exception.Reason);
        Assert.Equal(TimeSpan.FromHours(1), exception.Cooldown);
    }

    [Fact]
    public async Task Timeout_MapsToTimeoutBucket()
    {
        var handler = new StubHttpMessageHandler().EnqueueHang();

        var provider = new PodnapisiProvider(
            SubtitleSettingsDouble.Create().Object,
            NewCache(),
            handler.ToClient(timeout: TimeSpan.FromMilliseconds(100)),
            NullLogger<PodnapisiProvider>.Instance);
        var movie = new SubtitleSearchRequest(
            "Movie", null, Season: null, Episode: null,
            ReleaseName: null, VideoHash: null, Languages: ["en"], ImdbId: null);

        var exception = await Assert.ThrowsAsync<SubtitleProviderException>(
            () => provider.SearchAsync(movie, CancellationToken.None));

        Assert.Equal(SubtitleProviderFailureReason.Timeout, exception.Reason);
    }

    [Fact]
    public async Task Download_FetchesPayloadFromResolvedLink()
    {
        var payload = new byte[] { 0x50, 0x4B, 0x03, 0x04 };
        var handler = new StubHttpMessageHandler()
            .EnqueueXml(SubtitleFixtures.Read("podnapisi", "search.xml"))
            .Enqueue(request => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(payload)
            });

        var provider = Create(handler, baseUrl: "http://fake.podnapisi/");
        var movie = new SubtitleSearchRequest(
            "Movie", null, Season: null, Episode: null,
            ReleaseName: null, VideoHash: null, Languages: ["en"], ImdbId: null);
        var search = await provider.SearchAsync(movie, CancellationToken.None);

        var download = await provider.DownloadAsync(
            new SubtitleCandidateRef(search.Candidates[0].ExternalId), CancellationToken.None);

        // Relative link="/subtitles/download/4123456" joined onto the base.
        Assert.Equal("http://fake.podnapisi/subtitles/download/4123456",
            handler.Requests[1].RequestUri!.ToString());
        Assert.Equal("zip", download.Format);
        Assert.Equal(payload, download.Content);
    }
}
