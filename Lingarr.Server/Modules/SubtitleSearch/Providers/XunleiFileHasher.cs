using System.Security.Cryptography;

namespace Lingarr.Server.Modules.SubtitleSearch.Providers;

/// <summary>
/// Xunlei subtitle-endpoint file hash (batch R2, 2026-10): three sampled
/// segments of <c>0x5000</c> (20480) bytes read at the offsets
/// <c>[0, size/3, size-0x5000]</c> (integer division), concatenated in order
/// and SHA1-hashed into one <b>uppercase</b>-hex string. Files smaller than
/// <c>0xF000</c> (61440) bytes cannot be hashed by this algorithm —
/// <see cref="Compute(Stream)"/> answers null for them and the provider
/// honestly returns an empty result instead of pretending to match. At the
/// exact threshold the three segments tile the file without gaps or overlap;
/// above it they sample head/middle/tail. Factual algorithm description only;
/// clean-room implementation per the R2 protocol spec, no upstream code (⑬).
/// </summary>
public static class XunleiFileHasher
{
    /// <summary>Minimum hashable file size (<c>0xF000</c> = 61440 bytes).</summary>
    public const long MinimumFileSize = 0xF000;

    private const int SegmentLength = 0x5000; // 20480 bytes per sampled segment

    /// <summary>
    /// Computes the xunlei hash of a seekable stream; null when the stream is
    /// shorter than <see cref="MinimumFileSize"/>. The stream is not closed.
    /// </summary>
    public static string? Compute(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        long fileSize;
        try
        {
            fileSize = stream.Length;
        }
        catch (NotSupportedException)
        {
            throw new ArgumentException("The stream must be seekable (length required).", nameof(stream));
        }

        if (fileSize < MinimumFileSize)
        {
            return null; // below the algorithm's floor — not hashable, not an error
        }

        // The three segments concatenated in offset order are the hashed
        // payload. A short read is mathematically impossible for a truthful
        // length ≥ the threshold (offset2+0x5000 == size exactly), but the
        // per-segment read loops stay short-read tolerant like the shooter
        // hasher — a defensive EOF degrades to hashing what was readable
        // instead of throwing.
        var payload = new byte[SegmentLength * 3];
        var total = 0;
        foreach (var offset in SegmentOffsets(fileSize))
        {
            total += ReadSegment(stream, offset, payload, total);
        }

        return Convert.ToHexString(SHA1.HashData(payload.AsSpan(0, total)));
    }

    /// <summary>Computes the xunlei hash of a file by path; null when under the floor.</summary>
    public static string? Compute(string filePath)
    {
        using var stream = new FileStream(
            filePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 16 * 1024, FileOptions.SequentialScan);
        return Compute(stream);
    }

    /// <summary>
    /// The three sampled offsets [0, size/3, size-0x5000]. Integer division;
    /// public so the golden tests can pin the positions independently.
    /// </summary>
    public static IReadOnlyList<long> SegmentOffsets(long fileSize)
        => [0, fileSize / 3, fileSize - SegmentLength];

    /// <summary>
    /// Reads one full segment into <paramref name="buffer"/> at
    /// <paramref name="start"/>, tolerating streams that hand out short reads
    /// mid-segment; returns the number of bytes actually read.
    /// </summary>
    private static int ReadSegment(Stream stream, long offset, byte[] buffer, int start)
    {
        stream.Seek(offset, SeekOrigin.Begin);
        var totalRead = 0;
        while (totalRead < SegmentLength)
        {
            var read = stream.Read(buffer, start + totalRead, SegmentLength - totalRead);
            if (read <= 0)
            {
                break; // defensive EOF tolerance — see Compute
            }

            totalRead += read;
        }

        return totalRead;
    }
}
