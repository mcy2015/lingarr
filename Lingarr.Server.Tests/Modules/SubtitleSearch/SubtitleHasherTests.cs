using System;
using System.IO;
using Lingarr.Server.Modules.SubtitleSearch.Providers;
using Xunit;

namespace Lingarr.Server.Tests.Modules.SubtitleSearch;

/// <summary>
/// OpenSubtitles movie-hash vectors (tech design ②/⑩): hand-computed
/// expectations for the empty file, single-word files, the uint64 wrap, the
/// 16 KiB overlap below 128 KiB (official behaviour — overlapping words
/// count twice) and the plain two-chunk case above 128 KiB. The provider's
/// hash hit short-circuit (③) rests on this being exact.
/// </summary>
public class SubtitleHasherTests
{
    [Fact]
    public void EmptyFile_HashesToZero()
    {
        using var stream = new MemoryStream();

        Assert.Equal("0000000000000000", SubtitleHasher.Compute(stream));
    }

    [Fact]
    public void SingleWordFile_SumsWordTwiceBelowOneChunk()
    {
        // word = 1 (LE), size = 8. Head and tail reads cover the same
        // single chunk and the word is summed twice: 8 + 1 + 1 = 10.
        using var stream = new MemoryStream([0x01, 0, 0, 0, 0, 0, 0, 0]);

        Assert.Equal("000000000000000a", SubtitleHasher.Compute(stream));
    }

    [Fact]
    public void FullWord_WrapsAt64Bits()
    {
        // word = 0xFFFFFFFFFFFFFFFF summed twice + size 8 -> wraps to 6
        using var stream = new MemoryStream([0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF]);

        Assert.Equal("0000000000000006", SubtitleHasher.Compute(stream));
    }

    [Fact]
    public void TwoWordFile_SumsBothWordsTwice()
    {
        // <= 64 KiB: head and tail reads cover the same chunk, so both words
        // are counted twice: (1 + 1) * 2 + 16 = 20
        using var stream = new MemoryStream(16);
        stream.Write(new byte[] { 0x01, 0, 0, 0, 0, 0, 0, 0 }, 0, 8);
        stream.Write(new byte[] { 0x01, 0, 0, 0, 0, 0, 0, 0 }, 0, 8);

        Assert.Equal("0000000000000014", SubtitleHasher.Compute(stream));
    }

    [Fact]
    public void FileBelow128KiB_CountsOverlappingWordsTwice()
    {
        // The documented reference behaviour for 64 KiB < size <= 128 KiB:
        // chunk1 = [0, 64K), chunk2 = [size-64K, size) — they overlap and the
        // overlapping words are summed twice. 100000 bytes, one non-zero
        // byte at 40000 (LSB of word 5000, inside the overlap):
        // 100000 + 1 + 1 = 100002 = 0x186a2.
        var bytes = new byte[100_000];
        bytes[40_000] = 0x01;
        using var stream = new MemoryStream(bytes);

        Assert.Equal("00000000000186a2", SubtitleHasher.Compute(stream));
    }

    [Fact]
    public void FileAbove128KiB_SumsHeadAndTailDisjoint()
    {
        // 200000 bytes: byte 0 (word 0, chunk1 only) = 1, byte 199999 (LSB
        // of the final word, chunk2 only) = 2 -> 200000 + 1 + 2 = 200003.
        var bytes = new byte[200_000];
        bytes[0] = 0x01;
        bytes[199_999] = 0x02;
        using var stream = new MemoryStream(bytes);

        // NOTE: words are aligned to the START of each chunk, so the 0x02
        // at byte 199999 is the high byte of chunk2's final word ->
        // 200000 + 1 + (2 << 56).
        Assert.Equal("0200000000030d41", SubtitleHasher.Compute(stream));
    }

    [Fact]
    public void StreamPosition_IsIrrelevant()
    {
        using var stream = new MemoryStream([0x01, 0, 0, 0, 0, 0, 0, 0]);
        stream.Seek(0, SeekOrigin.End);

        Assert.Equal("000000000000000a", SubtitleHasher.Compute(stream));
    }

    [Fact]
    public void NonSeekableStream_IsRejected()
    {
        using var seekable = new MemoryStream([0x01]);
        using var nonSeekable = new NonSeekableStream(seekable);

        Assert.Throws<ArgumentException>(() => SubtitleHasher.Compute(nonSeekable));
    }

    private sealed class NonSeekableStream(Stream inner) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
