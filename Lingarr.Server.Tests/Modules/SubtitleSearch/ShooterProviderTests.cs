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
/// shooter adapter against the recorded fixture (M2 acceptance): the hash
/// travels in the query (with the chn/eng lang mapping), every hit is a
/// filehash hit, a missing filehash answers empty without any HTTP, and the
/// failure buckets map correctly.
/// </summary>
public class ShooterProviderTests
{
    private const string FileHash = "d41d8cd98f00b204e9800998ecf8427e;0cc175b9c0f1b6a831c399e269772661";

    private static SubtitleCandidateCache NewCache() =>
        new(new MemoryCache(new MemoryCacheOptions()), NullLogger<SubtitleCandidateCache>.Instance);

    private static ShooterProvider Create(StubHttpMessageHandler handler)
        => new(
            SubtitleSettingsDouble.Create().Object,
            NewCache(), handler.ToClient(), NullLogger<ShooterProvider>.Instance);

    private static SubtitleSearchRequest Request(string[] languages, string? fileHash = FileHash) => new(
        "Movie", 2019, Season: null, Episode: null,
        ReleaseName: "Movie.2019.WEBDL-1080p", VideoHash: null,
        Languages: languages, ImdbId: null, ShooterFileHash: fileHash);

    [Fact]
    public async Task Search_ParsesFilesAndMarksHashMatch()
    {
        var handler = new StubHttpMessageHandler()
            .EnqueueJson(SubtitleFixtures.Read("shooter", "search.json"));

        var provider = Create(handler);
        var result = await provider.SearchAsync(Request(["zh"]), CancellationToken.None);

        var url = handler.SingleRequestUri!.ToString();
        Assert.Contains("api/subapi.php", url); // R4 hotfix: the endpoint was renamed upstream
        Assert.Contains("filehash=" + Uri.EscapeDataString(FileHash), url);
        Assert.Contains("pathinfo=Movie.2019.WEBDL-1080p", url); // parameter renamed from path=
        Assert.Contains("format=json", url);
        Assert.Contains("lang=chn", url); // zh -> chn
        Assert.Equal(HttpMethod.Post, handler.SingleRequest.Method);

        // 2 entries x (2 + 1) files = 3 candidates, all hash hits.
        Assert.Equal(3, result.Candidates.Count);
        Assert.All(result.Candidates, candidate => Assert.True(candidate.HashMatch));
        var first = result.Candidates[0];
        Assert.Equal("shooter", first.Provider);
        Assert.Equal("zh", first.Language);
        Assert.Equal("Movie.2019.WEBDL-1080p.chs", first.ReleaseInfo);
        Assert.Equal("srt", first.Format);
        Assert.Equal("ass", result.Candidates[1].Format);
        Assert.StartsWith("subtitle-candidate:shooter:", first.ExternalId);
    }

    [Fact]
    public async Task Search_EnglishMapsToEng()
    {
        var handler = new StubHttpMessageHandler()
            .EnqueueJson(SubtitleFixtures.Read("shooter", "search.json"));

        var provider = Create(handler);
        await provider.SearchAsync(Request(["en"]), CancellationToken.None);

        Assert.Contains("lang=eng", handler.SingleRequestUri!.ToString());
    }

    [Fact]
    public async Task Search_UnmappedLanguage_IsSkippedWithoutAnUpstreamRequest()
    {
        var handler = new StubHttpMessageHandler();

        var provider = Create(handler);
        var result = await provider.SearchAsync(Request(["ja"]), CancellationToken.None);

        // Manual any-language search: shooter has no lang value for "ja",
        // and a lang-less query would answer the whole library mislabelled
        // as ja — the language is skipped silently, zero upstream requests.
        Assert.Empty(result.Candidates);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Search_UnmappedLanguageAmongMapped_QueriesOnlyTheMappedOnes()
    {
        var handler = new StubHttpMessageHandler()
            .EnqueueJson(SubtitleFixtures.Read("shooter", "search.json"));

        var provider = Create(handler);
        var result = await provider.SearchAsync(Request(["ja", "zh"]), CancellationToken.None);

        // exactly one upstream request — for zh (chn); nothing for ja — and
        // the fixture's hits are labelled with the language that queried them
        Assert.Single(handler.Requests);
        Assert.Contains("lang=chn", handler.SingleRequestUri!.ToString());
        Assert.Equal(3, result.Candidates.Count);
        Assert.All(result.Candidates, candidate => Assert.Equal("zh", candidate.Language));
    }

    [Fact]
    public async Task Search_WithoutFileHash_ReturnsEmptyWithoutHttpRequest()
    {
        var handler = new StubHttpMessageHandler();

        var provider = Create(handler);
        var result = await provider.SearchAsync(Request(["zh"], fileHash: null), CancellationToken.None);

        // Hash-only API (⑩): no honest query without the filehash — empty,
        // not a fake keyword search.
        Assert.Empty(result.Candidates);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task EmptyArray_IsAnEmptyResult()
    {
        var handler = new StubHttpMessageHandler().EnqueueJson("[]");

        var provider = Create(handler);
        var result = await provider.SearchAsync(Request(["zh"]), CancellationToken.None);

        Assert.Empty(result.Candidates);
    }

    [Fact]
    public async Task RateLimited429_MapsToCooldownException()
    {
        var handler = new StubHttpMessageHandler().EnqueueStatus(HttpStatusCode.TooManyRequests);

        var provider = Create(handler);
        var exception = await Assert.ThrowsAsync<SubtitleProviderException>(
            () => provider.SearchAsync(Request(["zh"]), CancellationToken.None));

        Assert.Equal(SubtitleProviderFailureReason.RateLimited, exception.Reason);
        Assert.Equal(TimeSpan.FromHours(1), exception.Cooldown);
    }

    [Fact]
    public async Task Timeout_MapsToTimeoutBucket()
    {
        var handler = new StubHttpMessageHandler().EnqueueHang();

        var provider = new ShooterProvider(
            SubtitleSettingsDouble.Create().Object,
            NewCache(),
            handler.ToClient(timeout: TimeSpan.FromMilliseconds(100)),
            NullLogger<ShooterProvider>.Instance);

        var exception = await Assert.ThrowsAsync<SubtitleProviderException>(
            () => provider.SearchAsync(Request(["zh"]), CancellationToken.None));

        Assert.Equal(SubtitleProviderFailureReason.Timeout, exception.Reason);
    }

    [Fact]
    public async Task TestConnection_ProbesTheSubapiEndpoint()
    {
        var handler = new StubHttpMessageHandler().EnqueueJson("[]");

        var provider = Create(handler);
        var outcome = await provider.TestConnectionAsync(CancellationToken.None);

        Assert.True(outcome.Ok);
        // R4 hotfix: the probe must hit the renamed api/subapi.php route —
        // the old api/subtitle.php answers 404/TLS hang upstream.
        var url = handler.SingleRequestUri!.ToString();
        Assert.Contains("api/subapi.php", url);
        Assert.Contains("filehash=00000000000000000000000000000000", url);
        Assert.Equal(HttpMethod.Post, handler.SingleRequest.Method);
    }

    [Fact]
    public async Task Download_FetchesFileLink()
    {
        var payload = "1\r\n00:00:01,000 --> 00:00:02,000\r\n字幕"u8.ToArray();
        var handler = new StubHttpMessageHandler()
            .EnqueueJson(SubtitleFixtures.Read("shooter", "search.json"))
            .Enqueue(request => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(payload)
            });

        var provider = Create(handler);
        var search = await provider.SearchAsync(Request(["zh"]), CancellationToken.None);

        var download = await provider.DownloadAsync(
            new SubtitleCandidateRef(search.Candidates[0].ExternalId), CancellationToken.None);

        Assert.Equal("https://files.shooter.cn/fixture/file-1.srt",
            handler.Requests[1].RequestUri!.ToString());
        Assert.Equal("srt", download.Format);
        Assert.Equal(payload, download.Content);
    }
}
