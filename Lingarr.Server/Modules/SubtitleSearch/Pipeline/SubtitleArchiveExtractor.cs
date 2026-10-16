using System.Text;
using Lingarr.Server.Modules.Subtitle;
using Lingarr.Server.Modules.SubtitleSearch.Abstractions;
using SharpCompress.Archives;
using SharpCompress.Common;

namespace Lingarr.Server.Modules.SubtitleSearch.Pipeline;

/// <summary>
/// One picked subtitle payload out of a downloaded file
/// (<see cref="SubtitleArchiveExtractor"/>).
/// </summary>
/// <param name="FileName">File name of the picked entry (or of the plain payload).</param>
/// <param name="Content">Payload bytes of the picked entry.</param>
public sealed record ExtractedSubtitle(string FileName, byte[] Content);

/// <summary>
/// Archive detection and entry picking of the download pipeline (tech
/// design ⑦ step ①). Detection runs on magic numbers, never on the
/// source-declared format: subtitle sites habitually serve zips without a
/// content type, and a declared format that contradicts the payload magic
/// is an <see cref="SubtitleProviderFailureReason.InvalidResponse"/> (the
/// "fake zip" acceptance case).
/// <para>
/// Entry selection: among subtitle-extension entries, file-name language
/// tags win (a tagged entry matching the requested language first, any
/// tagged entry second), then the largest payload. Non-subtitle entries
/// (nfo, jpg, urls) never win. Archive entry names are decoded GB18030 —
/// Chinese subtitle packs overwhelmingly carry GBK-encoded names without
/// the UTF-8 flag; the flag is still honoured for entries that set it.
/// </para>
/// </summary>
public sealed class SubtitleArchiveExtractor
{
    /// <summary>
    /// Hard cap of entries opened from one archive (QA2 P2-5): a subtitle
    /// pack carries a handful of files; thousands of entries is a zip bomb
    /// and the archive is rejected before any entry streams.
    /// </summary>
    public const int MaxArchiveEntries = 64;

    /// <summary>
    /// Hard cap of one extracted entry (QA2 P2-5): subtitle texts are
    /// kilobytes-to-megabytes; a single entry past 50MB is a decompression
    /// bomb and fails as InvalidResponse mid-copy, before the MemoryStream
    /// grows unbounded.
    /// </summary>
    public const long MaxEntryBytes = 50L * 1024 * 1024;

    private static readonly string[] SubtitleExtensions =
        [".srt", ".ass", ".ssa", ".vtt"];

    private static readonly string[] ArchiveFormats =
        ["zip", "rar", "7z", "gz", "tar"];

    private static readonly byte[] ZipMagic1 = [0x50, 0x4B, 0x03, 0x04];
    private static readonly byte[] ZipMagic2 = [0x50, 0x4B, 0x05, 0x06];
    private static readonly byte[] ZipMagic3 = [0x50, 0x4B, 0x07, 0x08];
    private static readonly byte[] RarMagic = [0x52, 0x61, 0x72, 0x21, 0x1A, 0x07];
    private static readonly byte[] SevenZipMagic = [0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C];

    /// <summary>
    /// Extracts the subtitle payload. Plain (non-archive) payloads pass
    /// through untouched; archives are opened by magic number and one entry
    /// is picked per the ⑦ rule.
    /// </summary>
    /// <param name="payload">The raw download payload.</param>
    /// <param name="fileName">Remote file name of the payload.</param>
    /// <param name="declaredFormat">Source-declared format ("zip", "srt"…).</param>
    /// <param name="requestedLanguage">Language the candidate was found under; drives entry preference.</param>
    /// <param name="provider">Source key, for error attribution.</param>
    public ExtractedSubtitle Extract(
        byte[] payload,
        string? fileName,
        string? declaredFormat,
        string? requestedLanguage,
        string provider)
    {
        ArgumentNullException.ThrowIfNull(payload);

        if (!LooksLikeArchive(payload))
        {
            if (declaredFormat is not null && ArchiveFormats.Contains(declaredFormat, StringComparer.OrdinalIgnoreCase))
            {
                // Source declared an archive but the bytes are not one — the
                // "fake zip" case: record it as an unparseable response, not
                // a transient failure (no cooldown, ⑧).
                throw SubtitleProviderException.ForInvalidResponse(
                    provider,
                    $"declared format '{declaredFormat}' but the payload carries no archive magic number.");
            }

            return new ExtractedSubtitle(fileName ?? "subtitle.srt", payload);
        }

        var picked = PickEntry(payload, requestedLanguage, provider);
        return picked ?? throw SubtitleProviderException.ForInvalidResponse(
            provider,
            "the archive contains no subtitle entry (.srt/.ass/.ssa/.vtt).");
    }

    private static bool LooksLikeArchive(byte[] payload) =>
        StartsWith(payload, ZipMagic1) || StartsWith(payload, ZipMagic2) || StartsWith(payload, ZipMagic3)
        || StartsWith(payload, RarMagic)
        || StartsWith(payload, SevenZipMagic);

    private static bool StartsWith(byte[] payload, byte[] magic) =>
        payload.Length >= magic.Length && magic.AsSpan().SequenceEqual(payload.AsSpan(0, magic.Length));

    /// <summary>
    /// Opens the archive (magic number already verified) and picks one
    /// subtitle entry: requested-language tag &gt; any language tag &gt;
    /// largest untagged. Ties fall to the larger payload, then to archive
    /// order (first wins).
    /// </summary>
    private static ExtractedSubtitle? PickEntry(
        byte[] payload,
        string? requestedLanguage,
        string provider)
    {
        try
        {
            using var stream = new MemoryStream(payload);
            // GB18030 default for unflagged entry names (⑦): Chinese packs
            // carry GBK names; UTF-8-flagged names still decode as UTF-8.
            // SharpCompress 0.38: ReaderOptions/ArchiveEncoding live in
            // SharpCompress.Readers — pass via the reader options factory.
            var options = new SharpCompress.Readers.ReaderOptions
            {
                ArchiveEncoding = new ArchiveEncoding
                {
                    Default = Encoding.GetEncoding("GB18030")
                }
            };
            using var archive = ArchiveFactory.Open(stream, options);

            // Entry-count budget first (QA2 P2-5): counting materializes the
            // central directory once and rejects bomb archives before any
            // entry payload streams.
            var entries = archive.Entries.ToList();
            if (entries.Count > MaxArchiveEntries)
            {
                throw SubtitleProviderException.ForInvalidResponse(
                    provider,
                    $"the archive carries {entries.Count} entries, over the {MaxArchiveEntries}-entry budget.");
            }

            IEntry? best = null;
            var bestRank = -1;
            var bestSize = -1L;
            foreach (var entry in entries)
            {
                if (entry.IsDirectory || string.IsNullOrEmpty(entry.Key))
                {
                    continue;
                }

                var name = Path.GetFileName(entry.Key);
                if (!SubtitleExtensions.Contains(Path.GetExtension(name).ToLowerInvariant()))
                {
                    continue;
                }

                var (rank, size) = RankEntry(name, entry.Size, requestedLanguage);
                if (rank > bestRank || (rank == bestRank && size > bestSize))
                {
                    best = entry;
                    bestRank = rank;
                    bestSize = size;
                }
            }

            if (best is null)
            {
                return null;
            }

            using var entryStream = ((SharpCompress.Archives.IArchiveEntry)best).OpenEntryStream();
            using var copied = new MemoryStream();
            // Bounded copy (QA2 P2-5): entry.Size is the *declared* size and
            // cannot be trusted — the cap is enforced on the bytes actually read.
            var chunk = new byte[81920];
            long total = 0;
            int read;
            while ((read = entryStream.Read(chunk, 0, chunk.Length)) > 0)
            {
                total += read;
                if (total > MaxEntryBytes)
                {
                    throw SubtitleProviderException.ForInvalidResponse(
                        provider,
                        $"archive entry '{best.Key}' decompressed past the {MaxEntryBytes / (1024 * 1024)}MB budget.");
                }

                copied.Write(chunk, 0, read);
            }

            return new ExtractedSubtitle(
                Path.GetFileName(best.Key!),
                copied.ToArray());
        }
        catch (SubtitleProviderException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Magic matched but the archive is corrupt/unsupported — a
            // parse failure of the response body, not source unhealthiness.
            throw SubtitleProviderException.ForInvalidResponse(
                provider, $"the archive could not be read ({ex.Message}).", ex);
        }
    }

    /// <summary>
    /// Entry preference per ⑦: 3 = file name carries the requested
    /// language, 2 = carries any language tag, 1 = untagged subtitle.
    /// Language tags are detected per dot-segment (the CreateFilePath
    /// convention) — a scene base name like
    /// "Movie.2023.WEBDL-1080p.zh" does not parse as a whole, its ".zh"
    /// segment does.
    /// </summary>
    private static (int Rank, long Size) RankEntry(string name, long size, string? requestedLanguage)
    {
        var rank = 1;
        foreach (var segment in Path.GetFileNameWithoutExtension(name).Split('.'))
        {
            if (!SubtitleLanguageParser.TryParse(segment, out var languages))
            {
                continue;
            }

            rank = requestedLanguage is not null
                   && languages.Contains(requestedLanguage, StringComparer.OrdinalIgnoreCase)
                ? 3
                : 2;
            if (rank == 3)
            {
                break;
            }
        }

        return (rank, size);
    }
}
