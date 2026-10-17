using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Lingarr.Server.Modules.SubtitleSearch;
using Lingarr.Server.Modules.SubtitleSearch.Abstractions;
using Lingarr.Server.Modules.SubtitleSearch.Providers;
using Lingarr.Server.Modules.SubtitleSearch.Requests;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Lingarr.Server.Tests.Modules.SubtitleSearch;

/// <summary>
/// Xunlei adapter against the recorded fixture (batch R2 acceptance): the
/// hash rides in the path (<c>/subxl/{hash}.json</c>, one GET per search),
/// the observed <c>language</c> values map onto the canonical zh codes
/// (bilingual 简体&amp;英语 → zh direct hit), garbage-language entries are
/// dropped instead of polluting the pool, percent-encoded sname values
/// decode, a missing filehash answers empty without any HTTP, and the
/// failure buckets map correctly. The filehash vectors are golden-tested
/// against constants derived independently of the implementation.
/// </summary>
public class XunleiProviderTests
{
    // The 61440-byte golden vector below — a real uppercase-hex SHA1 of the
    // spec algorithm, doubling as an arbitrary syntactically valid hash.
    private const string FileHash = "A97217016EE754926A348EABBE522346E6992150";

    private static SubtitleCandidateCache NewCache() =>
        new(new MemoryCache(new MemoryCacheOptions()), NullLogger<SubtitleCandidateCache>.Instance);

    private static XunleiProvider Create(StubHttpMessageHandler handler)
        => new(
            SubtitleSettingsDouble.Create().Object,
            NewCache(), handler.ToClient(), NullLogger<XunleiProvider>.Instance);

    private static SubtitleSearchRequest Request(string[] languages, string? fileHash = FileHash) => new(
        "Movie", 2019, Season: null, Episode: null,
        ReleaseName: "Movie.2019.WEBDL-1080p", VideoHash: null,
        Languages: languages, ImdbId: null, XunleiFileHash: fileHash);

    [Fact]
    public async Task Search_ParsesSublist_MapsLanguages_DropsUnrecognized()
    {
        var handler = new StubHttpMessageHandler()
            .EnqueueJson(SubtitleFixtures.Read("xunlei", "search.json"));

        var provider = Create(handler);
        var result = await provider.SearchAsync(Request(["zh", "zh-TW"]), CancellationToken.None);

        var url = handler.SingleRequestUri!.ToString();
        Assert.Contains("/subxl/" + FileHash + ".json", url);
        Assert.Equal(HttpMethod.Get, handler.SingleRequest.Method);

        // 7 fixture entries: the GBK-mangled "\uFFFD体" and the
        // "未知语言" entries are dropped; the other five survive.
        Assert.Equal(5, result.Candidates.Count);
        Assert.All(result.Candidates, candidate => Assert.True(candidate.HashMatch));
        Assert.All(result.Candidates, candidate => Assert.Equal("xunlei", candidate.Provider));

        // Language mapping: 简体 → zh, 繁体 → zh-TW.
        Assert.Equal(["zh", "zh-TW", "zh", "zh", "zh"],
            result.Candidates.Select(candidate => candidate.Language).ToArray());

        // Bilingual 简体&英语 → zh (direct-hit terminal state, no translation hop).
        Assert.Equal("zh", result.Candidates[2].Language);
        Assert.Equal("Movie.2019.WEBDL-1080p.chs&eng.srt", result.Candidates[2].ReleaseInfo);

        // Format from the file name (sname when present, surl otherwise).
        Assert.Equal("srt", result.Candidates[0].Format);
        Assert.Equal("ass", result.Candidates[1].Format);
        Assert.Equal("ass", result.Candidates[4].Format); // sname-less entry falls back to surl

        // External ids resolve through the candidate cache.
        Assert.StartsWith("subtitle-candidate:xunlei:", result.Candidates[0].ExternalId);
    }

    [Fact]
    public async Task Search_DecodesPercentEncodedSname()
    {
        var handler = new StubHttpMessageHandler()
            .EnqueueJson(SubtitleFixtures.Read("xunlei", "search.json"));

        var provider = Create(handler);
        var result = await provider.SearchAsync(Request(["zh", "zh-TW"]), CancellationToken.None);

        // "%E7%94%B5%E5%BD%B1%3A%5C…" decodes to 电影:\… — the unescaped
        // form is the honest display/release name.
        Assert.Equal("电影:\\Movie.2019.cht.ass", result.Candidates[1].ReleaseInfo);
        // UTF-8 percent encoding of 简体双语 decodes the same way.
        Assert.Equal("Movie.2019.WEBDL-1080p.简体双语.srt", result.Candidates[3].ReleaseInfo);
    }

    [Fact]
    public async Task Search_FiltersClientSideAgainstTheRequestedLanguages()
    {
        var handler = new StubHttpMessageHandler()
            .EnqueueJson(SubtitleFixtures.Read("xunlei", "search.json"));

        var provider = Create(handler);
        var result = await provider.SearchAsync(Request(["zh"]), CancellationToken.None);

        // Only zh requested: the 繁体 (zh-TW) entry is filtered out
        // client-side (SubSource precedent — the endpoint has no lang filter).
        Assert.Equal(4, result.Candidates.Count);
        Assert.All(result.Candidates, candidate => Assert.Equal("zh", candidate.Language));
    }

    [Fact]
    public async Task Search_WithoutFileHash_ReturnsEmptyWithoutHttpRequest()
    {
        // No hash means either "xunlei not queried this round" or "file
        // under the 0xF000 floor" (the hasher answers null for both) —
        // hash-only API, empty, not a fake keyword search, no exception.
        var handler = new StubHttpMessageHandler();

        var provider = Create(handler);
        var result = await provider.SearchAsync(Request(["zh"], fileHash: null), CancellationToken.None);

        Assert.Empty(result.Candidates);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task EmptySublist_IsAnEmptyResult()
    {
        var handler = new StubHttpMessageHandler()
            .EnqueueJson(SubtitleFixtures.Read("xunlei", "empty.json"));

        var provider = Create(handler);
        var result = await provider.SearchAsync(Request(["zh"]), CancellationToken.None);

        Assert.Empty(result.Candidates);
    }

    [Fact]
    public async Task MalformedJson_MapsToInvalidResponse()
    {
        var handler = new StubHttpMessageHandler()
            .EnqueueJson(SubtitleFixtures.Read("xunlei", "garbage.json"));

        var provider = Create(handler);
        var exception = await Assert.ThrowsAsync<SubtitleProviderException>(
            () => provider.SearchAsync(Request(["zh"]), CancellationToken.None));

        Assert.Equal(SubtitleProviderFailureReason.InvalidResponse, exception.Reason);
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
    public async Task Download_FetchesTheSurl()
    {
        var payload = "1\r\n00:00:01,000 --> 00:00:02,000\r\n字幕"u8.ToArray();
        var handler = new StubHttpMessageHandler()
            .EnqueueJson(SubtitleFixtures.Read("xunlei", "search.json"))
            .Enqueue(request => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(payload)
            });

        var provider = Create(handler);
        var search = await provider.SearchAsync(Request(["zh", "zh-TW"]), CancellationToken.None);

        var download = await provider.DownloadAsync(
            new SubtitleCandidateRef(search.Candidates[0].ExternalId), CancellationToken.None);

        Assert.Equal("http://files.fixture.xunlei.com/subtitle/63001.srt",
            handler.Requests[1].RequestUri!.ToString());
        Assert.Equal("srt", download.Format);
        Assert.Equal("Movie.2019.WEBDL-1080p.chs.srt", download.FileName); // decoded sname
        Assert.Equal(payload, download.Content);
    }

    [Fact]
    public async Task Download_WithoutSname_FallsBackToProviderLanguageFileName()
    {
        var handler = new StubHttpMessageHandler()
            .EnqueueJson(SubtitleFixtures.Read("xunlei", "search.json"))
            .Enqueue(request => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent("1\r\n00:00:01,000 --> 00:00:02,000\r\n字幕"u8.ToArray())
            });

        var provider = Create(handler);
        var search = await provider.SearchAsync(Request(["zh", "zh-TW"]), CancellationToken.None);

        // The sname-less entry (index 4) falls back to xunlei.{language}.srt
        // when the response carries no file name either (shooter mirror).
        var download = await provider.DownloadAsync(
            new SubtitleCandidateRef(search.Candidates[4].ExternalId), CancellationToken.None);

        Assert.Equal("xunlei.zh.srt", download.FileName);
    }

    [Fact]
    public async Task TestConnection_ProbesWithTheDummyZeroHash()
    {
        var handler = new StubHttpMessageHandler()
            .EnqueueJson(SubtitleFixtures.Read("xunlei", "empty.json"));

        var provider = Create(handler);
        var outcome = await provider.TestConnectionAsync(CancellationToken.None);

        Assert.True(outcome.Ok);
        // The endpoint answers unknown hashes with a hot-list fallback, so
        // any 200 JSON body proves aliveness — forty '0's as the dummy.
        Assert.EndsWith("/subxl/" + new string('0', 40) + ".json", handler.SingleRequestUri!.ToString());
    }

    // ===== Filehash golden vectors (XunleiFileHasher) =====
    //
    // Independent derivation (python3, run 2026-10-17 against the R2 spec,
    // not against the C# implementation):
    //
    //   import hashlib
    //   def xunlei_hash(size, mk):
    //       if size < 0xF000: return None
    //       seg = 0x5000
    //       offs = [0, size // 3, size - seg]
    //       data = b"".join(bytes(mk(o + i) for i in range(seg)) for o in offs)
    //       return hashlib.sha1(data).hexdigest().upper()
    //
    //   xunlei_hash(100000, lambda i: i % 251)
    //       -> 'D4090EA01D10880E8327F6FBDE52CD6F75715FE7'
    //   xunlei_hash(61440, lambda i: i % 251)
    //       -> 'A97217016EE754926A348EABBE522346E6992150'
    //   xunlei_hash(61441, lambda i: (i*7 + 13) % 256)
    //       -> '0B0DCDDF562042F6D5DA88276D08D6E7E438A291'

    private static byte[] PatternBytes(int size, Func<int, byte> pattern)
    {
        var bytes = new byte[size];
        for (var i = 0; i < size; i++)
        {
            bytes[i] = pattern(i);
        }

        return bytes;
    }

    [Fact]
    public void Compute_MatchesTheIndependentlyDerivedGoldenVectors()
    {
        // 100000 bytes of (i % 251): offsets [0, 33333, 79520].
        using (var stream = new MemoryStream(PatternBytes(100_000, i => (byte)(i % 251))))
        {
            Assert.Equal("D4090EA01D10880E8327F6FBDE52CD6F75715FE7", XunleiFileHasher.Compute(stream));
        }

        // Exactly 0xF000: the boundary itself is hashable.
        using (var stream = new MemoryStream(PatternBytes(61_440, i => (byte)(i % 251))))
        {
            Assert.Equal("A97217016EE754926A348EABBE522346E6992150", XunleiFileHasher.Compute(stream));
        }

        // 0xF001 with a different pattern keeps the vector independent.
        using (var stream = new MemoryStream(PatternBytes(0xF001, i => (byte)((i * 7 + 13) % 256))))
        {
            Assert.Equal("0B0DCDDF562042F6D5DA88276D08D6E7E438A291", XunleiFileHasher.Compute(stream));
        }
    }

    [Fact]
    public void Compute_AtTheThreshold_SegmentsTileTheWholeFile()
    {
        // At exactly 0xF000 the three segments [0, 20480, 40960] tile the
        // file without gaps, so the sampled SHA1 equals the whole-file
        // SHA1 — an independent property check of the offset math.
        var bytes = PatternBytes(61_440, i => (byte)(i % 251));
        var wholeFileSha1 = Convert.ToHexString(SHA1.HashData(bytes));

        using var stream = new MemoryStream(bytes);
        Assert.Equal(wholeFileSha1, XunleiFileHasher.Compute(stream));
    }

    [Fact]
    public void Compute_OutputsFortyUppercaseHexCharacters()
    {
        using var stream = new MemoryStream(PatternBytes(100_000, i => (byte)(i % 251)));

        var hash = XunleiFileHasher.Compute(stream);

        Assert.Equal(40, hash!.Length);
        Assert.All(hash, character => Assert.True(
            character is >= '0' and <= '9' or >= 'A' and <= 'F',
            $"expected uppercase hex, got '{character}'"));
    }

    [Fact]
    public void Compute_UnderTheFloor_ReturnsNull()
    {
        // 0xEFFF = 61439: one byte under the algorithm's minimum — not
        // hashable, null (the provider turns that into an honest Empty).
        using var stream = new MemoryStream(PatternBytes(0xEFFF, i => (byte)(i % 251)));

        Assert.Null(XunleiFileHasher.Compute(stream));
    }

    [Theory]
    [InlineData(61_440, new long[] { 0, 20_480, 40_960 })]
    [InlineData(100_000, new long[] { 0, 33_333, 79_520 })]
    [InlineData(1_000_000, new long[] { 0, 333_333, 979_520 })]
    public void SegmentOffsets_AreHeadThirdAndTailMinusSegment(long fileSize, long[] expected)
    {
        Assert.Equal(expected, XunleiFileHasher.SegmentOffsets(fileSize));
    }

    // ===== LazyMediaHasher.XunleiHash (the "" sentinel round-trip) =====

    [Fact]
    public void LazyHasher_UnderFloorFile_AnswersNull()
    {
        // A file below the 0xF000 floor hashes to the "" cache sentinel;
        // the public surface must normalize it back onto null (the provider
        // then answers Empty), and the cache must not throw on the null.
        var path = Path.Combine(Path.GetTempPath(), "lingarr-xunlei-small-" + Guid.NewGuid().ToString("N"));
        try
        {
            File.WriteAllBytes(path, new byte[1024]);
            Assert.Null(new LazyMediaHasher().XunleiHash(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void LazyHasher_HashableFile_ComputesThroughTheDefaultDelegate()
    {
        // The production wiring (no injected delegate) hashes a real
        // floor-passing file with XunleiFileHasher — same golden vector as
        // the stream test, proving the path-based entry point.
        var path = Path.Combine(Path.GetTempPath(), "lingarr-xunlei-file-" + Guid.NewGuid().ToString("N"));
        try
        {
            File.WriteAllBytes(path, PatternBytes(61_440, i => (byte)(i % 251)));
            Assert.Equal("A97217016EE754926A348EABBE522346E6992150", new LazyMediaHasher().XunleiHash(path));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
