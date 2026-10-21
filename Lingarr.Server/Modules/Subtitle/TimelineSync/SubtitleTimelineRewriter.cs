using System.Text;
using Lingarr.Server.Models.FileSystem;
using Lingarr.Server.Modules.Subtitle;

namespace Lingarr.Server.Modules.Subtitle.TimelineSync;

/// <summary>
/// Result of one in-place timeline rewrite (B05).
/// </summary>
/// <param name="Cues">Number of cues written.</param>
/// <param name="ClampedCues">Cues whose clamped-to-zero start/end or flipped duration was adjusted.</param>
/// <param name="LastEndSeconds">The last cue's end time after the rewrite.</param>
public sealed record TimelineRewriteResult(int Cues, int ClampedCues, double LastEndSeconds);

/// <summary>
/// Rewrites a subtitle file's cue times (<c>t' = t×R + offset</c>) through
/// the existing parsers/writers (B05), so format quirks — the ASS style
/// layer, SRT numbering, VTT header — survive byte-for-byte outside the
/// time columns. The original file is backed up as
/// <c>{name}.lingarr-tl-bk</c> before any byte of it is touched.
/// </summary>
public sealed class SubtitleTimelineRewriter
{
    /// <summary>The backup file suffix (the sync idempotence gate).</summary>
    public const string BackupSuffix = ".lingarr-tl-bk";

    /// <summary>
    /// Known video container extensions — the ONLY suffixes ever stripped
    /// from a video file name. A release base name's dots
    /// ("Show.S01E01.WEBDL-1080p") must never be read as extensions.
    /// </summary>
    private static readonly string[] VideoNameExtensions =
        [".mkv", ".mp4", ".m4v", ".avi", ".mov", ".wmv", ".mpg", ".mpeg", ".ts", ".m2ts"];

    private static readonly string[] SupportedExtensions = [".srt", ".ass", ".ssa", ".vtt"];

    private readonly ISubtitleEncodingService _encodingService;

    public SubtitleTimelineRewriter(ISubtitleEncodingService encodingService)
    {
        _encodingService = encodingService;
    }

    /// <summary>Whether the file extension is one the rewriter round-trips.</summary>
    public static bool IsSupported(string filePath) =>
        SupportedExtensions.Contains(Path.GetExtension(filePath).ToLowerInvariant());

    /// <summary>The backup path of a subtitle file (same directory, suffix appended).</summary>
    public static string BackupPathFor(string filePath) => filePath + BackupSuffix;

    /// <summary>
    /// The video file name without its known video extension (the subtitle
    /// naming base). Never uses <see cref="Path.GetExtension"/> — scene
    /// names carry dots that are not extensions.
    /// </summary>
    public static string VideoBaseName(string videoFileName)
    {
        foreach (var extension in VideoNameExtensions)
        {
            if (videoFileName.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            {
                return videoFileName[..^extension.Length];
            }
        }

        return videoFileName;
    }

    /// <summary>
    /// Parses a subtitle file into cues (via the encoding service, so a
    /// legacy GBK/Big5 file reads correctly). Throws on unparseable input.
    /// </summary>
    public List<SubtitleItem> Read(string filePath)
    {
        var extension = Path.GetExtension(filePath).ToLowerInvariant();
        var utf8 = _encodingService.ConvertToUtf8(File.ReadAllBytes(filePath));
        using var input = new MemoryStream(utf8);
        return ParserFor(extension).ParseStream(input, Encoding.UTF8);
    }

    /// <summary>
    /// Applies <c>t' = round(t×R + offset)</c> to every cue and rewrites
    /// the file in place. Negative results clamp to zero; a cue whose end
    /// collapses onto its start is lifted to start+1ms so no zero/negative
    /// duration cue is ever written. The backup is created (copy, never
    /// overwrite) BEFORE the new content lands, and the new content lands
    /// via a temporary file + atomic move — any failure leaves the original
    /// bytes untouched.
    /// </summary>
    public async Task<TimelineRewriteResult> RewriteAsync(
        string filePath,
        double ratio,
        double offsetSeconds,
        CancellationToken cancellationToken)
    {
        var items = Read(filePath);
        var offsetMs = offsetSeconds * 1000.0;
        var clamped = 0;
        var lastEnd = 0;

        foreach (var item in items)
        {
            var start = (int)Math.Round(item.StartTime * ratio + offsetMs);
            var end = (int)Math.Round(item.EndTime * ratio + offsetMs);

            if (start < 0 || end < 0 || end <= start)
            {
                clamped++;
            }

            start = Math.Max(0, start);
            end = Math.Max(start + 1, Math.Max(0, end));
            item.StartTime = start;
            item.EndTime = end;
            lastEnd = Math.Max(lastEnd, end);

            // The writers emit TranslatedLines (SrtWriter/VttWriter
            // exclusively, SsaWriter preferentially) — the unchanged text
            // must move there for the round trip to keep it.
            item.TranslatedLines = [.. item.Lines];
        }

        File.Copy(filePath, BackupPathFor(filePath), overwrite: false);

        var extension = Path.GetExtension(filePath).ToLowerInvariant();
        var temporary = filePath + ".lingarr-tl-rewrite";
        try
        {
            await using (var output = File.Create(temporary))
            {
                await WriterFor(extension).WriteStreamAsync(
                    output, items, stripSubtitleFormatting: false, bilingual: false);
            }

            File.Move(temporary, filePath, overwrite: true);
        }
        catch
        {
            try
            {
                File.Delete(temporary);
            }
            catch (IOException)
            {
                // best-effort cleanup of the abandoned temp file
            }

            throw;
        }

        return new TimelineRewriteResult(items.Count, clamped, lastEnd / 1000.0);
    }

    private static ISubtitleParser ParserFor(string extension) => extension switch
    {
        ".ass" or ".ssa" => new SsaParser(),
        ".vtt" => new VttParser(),
        _ => new SrtParser()
    };

    private static ISubtitleWriter WriterFor(string extension) => extension switch
    {
        ".ass" or ".ssa" => new SsaWriter(),
        ".vtt" => new VttWriter(),
        _ => new SrtWriter()
    };
}
