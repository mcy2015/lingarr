using System.Security.Cryptography;

namespace Lingarr.Server.Modules.SubtitleSearch.Providers;

/// <summary>
/// shooter.cn file hash (tech design ⑩). The official API sample
/// (shooter.cn api/subapi.php doc, mirrored by every working client such as
/// qzane/SPlayerSubDownloader): four segments of 4096 bytes read at the
/// quarter positions [0, size/4, size/2, 3*size/4]; each segment is
/// MD5-hashed and the four lowercase-hex digests are joined with ';' into
/// one filehash string. Short reads at the tail of small files are the
/// documented behaviour — the segments are offsets into the file, not a
/// partition. Factual algorithm description only; no upstream code copied (⑬).
/// </summary>
public static class ShooterFileHasher
{
    private const int SegmentLength = 4096;

    /// <summary>Computes the shooter filehash of a seekable stream. The stream is not closed.</summary>
    public static string Compute(Stream stream)
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

        return string.Join(';', SegmentOffsets(fileSize).Select(offset => HashSegment(stream, offset)));
    }

    /// <summary>Computes the shooter filehash of a file by path.</summary>
    public static string Compute(string filePath)
    {
        using var stream = new FileStream(
            filePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 16 * 1024, FileOptions.SequentialScan);
        return Compute(stream);
    }

    /// <summary>
    /// The four quarter offsets of the official sample. Integer division;
    /// for files under 16 bytes offsets collapse onto each other and the
    /// overlapping reads are the documented behaviour.
    /// </summary>
    public static IReadOnlyList<long> SegmentOffsets(long fileSize)
    {
        return [0, fileSize / 4, fileSize / 2, 3 * fileSize / 4];
    }

    private static string HashSegment(Stream stream, long offset)
    {
        stream.Seek(offset, SeekOrigin.Begin);
        var buffer = new byte[SegmentLength];
        var totalRead = 0;
        while (totalRead < buffer.Length)
        {
            var read = stream.Read(buffer, totalRead, buffer.Length - totalRead);
            if (read <= 0)
            {
                break; // short tail read on small files
            }

            totalRead += read;
        }

        return Convert.ToHexStringLower(MD5.HashData(buffer.AsSpan(0, totalRead)));
    }
}
