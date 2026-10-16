using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Lingarr.Server.Modules.SubtitleSearch.Providers;

/// <summary>
/// OpenSubtitles video hash ("moviehash"): the strong per-file match key of
/// tech design ②/③ — a hit scores 100 and short-circuits scoring. The
/// algorithm is a public convention used across the OS ecosystem
/// (opensubtitles official API documentation, "movie hash" computation):
/// <code>
/// hash  = (uint64) file size
/// hash += sum of LE uint64 words in the first 64 KiB
/// hash += sum of LE uint64 words in the last 64 KiB   (chunks overlap below 128 KiB — official behaviour)
/// output = hash mod 2^64 as 16 lowercase hex digits
/// </code>
/// Factual algorithm description only; no upstream code copied (licensing
/// note ⑬).
/// </summary>
public static class SubtitleHasher
{
    private const int ChunkSize = 64 * 1024;

    /// <summary>Hashes a video file opened for reading. The stream position is irrelevant; it is not closed.</summary>
    public static string Compute(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        long fileSize;
        long lastChunkOffset;
        try
        {
            fileSize = stream.Length;
            // Clamped so files below one chunk hash their (only) bytes once
            // per pass; a file of <= 128 KiB legitimately double-counts the
            // overlapping words — that is the reference behaviour.
            lastChunkOffset = Math.Max(0, fileSize - ChunkSize);
        }
        catch (NotSupportedException)
        {
            throw new ArgumentException("The stream must be seekable (length required).", nameof(stream));
        }

        unchecked
        {
            // Both passes always run: for files below one chunk the head and
            // tail reads cover the same bytes and legitimately double-count
            // them — the reference behaviour, pinned by the small-file tests.
            ulong hash = (ulong)fileSize;
            hash += SumWords(stream, 0);
            hash += SumWords(stream, lastChunkOffset);

            return hash.ToString("x16");
        }
    }

    /// <summary>Hashes a video file by path (opens read-only, sequential scan hints).</summary>
    public static string Compute(string filePath)
    {
        using var stream = new FileStream(
            filePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 128 * 1024, FileOptions.SequentialScan);
        return Compute(stream);
    }

    /// <summary>Sums the little-endian uint64 words of one 64 KiB chunk read from <paramref name="offset"/> (wraps at 2^64).</summary>
    private static ulong SumWords(Stream stream, long offset)
    {
        stream.Seek(offset, SeekOrigin.Begin);

        var buffer = new byte[ChunkSize];
        var totalRead = 0;
        while (totalRead < buffer.Length)
        {
            var read = stream.Read(buffer, totalRead, buffer.Length - totalRead);
            if (read <= 0)
            {
                break; // short file: hash what exists
            }

            totalRead += read;
        }

        ulong sum = 0;
        var fullWords = totalRead / sizeof(ulong);
        for (var i = 0; i < fullWords; i++)
        {
            sum += BinaryPrimitives.ReadUInt64LittleEndian(buffer.AsSpan(i * sizeof(ulong)));
        }

        return sum;
    }
}
