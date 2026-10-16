using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Lingarr.Server.Modules.SubtitleSearch.Providers;
using Xunit;

namespace Lingarr.Server.Tests.Modules.SubtitleSearch;

/// <summary>
/// shooter.cn filehash vectors (tech design ⑩): the official sample reads
/// four 4096-byte segments at the quarter offsets [0, size/4, size/2,
/// 3*size/4] and joins the four lowercase-hex MD5 digests with ';'. Offsets
/// are pinned explicitly; digests are checked against known constants and
/// independently sliced segments.
/// </summary>
public class ShooterFileHasherTests
{
    private const int SegmentLength = 4096;

    [Theory]
    [InlineData(0, new long[] { 0, 0, 0, 0 })]
    [InlineData(1, new long[] { 0, 0, 0, 0 })]
    [InlineData(100, new long[] { 0, 25, 50, 75 })]
    [InlineData(4096, new long[] { 0, 1024, 2048, 3072 })]
    [InlineData(8 * 1024 * 1024, new long[] { 0, 2097152, 4194304, 6291456 })]
    public void SegmentOffsets_AreTheQuarterPositions(long fileSize, long[] expected)
    {
        Assert.Equal(expected, ShooterFileHasher.SegmentOffsets(fileSize));
    }

    [Fact]
    public void SingleByteFile_AllFourSegmentsHashTheByte()
    {
        // MD5("a") is the classic known digest; a 1-byte file collapses all
        // four quarter offsets onto 0 and the overlapping reads are the
        // documented behaviour.
        using var stream = new MemoryStream([(byte)'a']);

        var digest = "0cc175b9c0f1b6a831c399e269772661";
        Assert.Equal(string.Join(';', Enumerable.Repeat(digest, 4)), ShooterFileHasher.Compute(stream));
    }

    [Fact]
    public void EmptyFile_HashesTheEmptyDigestFourTimes()
    {
        using var stream = new MemoryStream();

        var digest = "d41d8cd98f00b204e9800998ecf8427e";
        Assert.Equal(string.Join(';', Enumerable.Repeat(digest, 4)), ShooterFileHasher.Compute(stream));
    }

    [Fact]
    public void SmallFile_ReadsFromEachQuarterOffsetToTheEnd()
    {
        // 8 bytes: offsets [0, 2, 4, 6] and each read runs to EOF — the
        // official sample reads 4096 bytes per offset, short reads allowed.
        using var stream = new MemoryStream("12345678"u8.ToArray());

        Assert.Equal(
            "25d55ad283aa400af464c76d713c07ad;5bd2026f128662763c532f2f4b6f2476;" +
            "674f3c2c1a8a6f90461e8a66fb5550ba;35f4a8d465e6e1edc05f3d8ab658c551",
            ShooterFileHasher.Compute(stream));
    }

    [Fact]
    public void MediumFile_JoinsCappedSegmentDigests()
    {
        var bytes = new byte[20_000];
        new Random(42).NextBytes(bytes);

        // offsets [0, 5000, 10000, 15000]; each read caps at 4096 bytes so
        // the final tail beyond 19096 is deliberately not hashed.
        var expected = string.Join(';', new long[] { 0, 5000, 10000, 15000 }
            .Select(offset => Convert.ToHexStringLower(MD5.HashData(
                bytes.AsSpan((int)offset, Math.Min(SegmentLength, bytes.Length - (int)offset))))));

        using var stream = new MemoryStream(bytes);
        Assert.Equal(expected, ShooterFileHasher.Compute(stream));
    }

    [Fact]
    public void LargeFile_JoinsFourFullSegmentDigests()
    {
        var bytes = new byte[SegmentLength * 8];
        new Random(7).NextBytes(bytes);
        var offsets = ShooterFileHasher.SegmentOffsets(bytes.Length);
        Assert.Equal([0L, 8192L, 16384L, 24576L], offsets);

        var expected = string.Join(';', offsets
            .Select(offset => Convert.ToHexStringLower(MD5.HashData(bytes.AsSpan((int)offset, SegmentLength)))));

        using var stream = new MemoryStream(bytes);
        Assert.Equal(expected, ShooterFileHasher.Compute(stream));
    }
}
