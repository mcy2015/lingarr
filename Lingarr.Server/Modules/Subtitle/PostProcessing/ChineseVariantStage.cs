using System.Text;
using Lingarr.Server.Models.FileSystem;
using Lingarr.Server.Modules.Subtitle;

namespace Lingarr.Server.Modules.Subtitle.PostProcessing;

/// <summary>
/// Chinese variant (简繁) conversion stage for landed subtitle files, reusing
/// the import flow's OpenCC converter (<see cref="ChineseSubtitleConverter"/>).
/// Same philosophy as the import option (and its header comment): converting
/// alters the stored text, so the mode defaults to "none" and the stage is a
/// no-op until the setting says otherwise.
/// <para>
/// Round-trips through the existing parsers/writers per extension. For ASS/
/// SSA the style layer ([V4+ Styles] header, Format: and Style: lines) is
/// carried verbatim inside <see cref="SsaFormat.Styles"/> and re-emitted
/// byte-for-byte by <see cref="SsaWriter"/> — only the Dialogue text changes.
/// </para>
/// <para>
/// Idempotent by construction: the converted output is compared line-wise
/// against the parsed input, and the file is only rewritten when a line
/// actually changed — a second run over converted text (or a file with no
/// Chinese content) leaves the bytes and mtime untouched.
/// </para>
/// </summary>
public sealed class ChineseVariantStage : ISubtitlePostProcessor
{
    private static readonly string[] SupportedExtensions = [".srt", ".ass", ".ssa", ".vtt"];

    private readonly ISubtitleEncodingService _encodingService;
    private readonly ILogger<ChineseVariantStage> _logger;

    public ChineseVariantStage(
        ISubtitleEncodingService encodingService,
        ILogger<ChineseVariantStage> logger)
    {
        _encodingService = encodingService;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "ChineseVariant";

    /// <inheritdoc />
    public async Task<PostProcessResult> ProcessAsync(
        string filePath,
        PostProcessContext context,
        CancellationToken cancellationToken)
    {
        // Never on by default — "none" (or a missing row) leaves the file
        // byte-identical to a build without this stage.
        var mode = context.ChineseVariantMode?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(mode) || mode == "none")
        {
            return PostProcessResult.Unchanged("chinese variant conversion is off");
        }

        var extension = Path.GetExtension(filePath).ToLowerInvariant();
        if (!SupportedExtensions.Contains(extension))
        {
            return PostProcessResult.Unchanged($"unsupported format '{extension}'");
        }

        // Read through the encoding service: pipeline files arrive as UTF-8
        // anyway, while a legacy GBK/Big5 file on disk still parses correctly
        // (and is rewritten as UTF-8 — but only when its text actually changes).
        var bytes = await File.ReadAllBytesAsync(filePath, cancellationToken);
        var utf8 = _encodingService.ConvertToUtf8(bytes);

        List<SubtitleItem> items;
        try
        {
            using var input = new MemoryStream(utf8);
            items = ParserFor(extension).ParseStream(input, Encoding.UTF8);
        }
        catch (Exception ex)
        {
            // A file this stage cannot parse is not an error — the subtitle
            // stays exactly as it landed.
            _logger.LogDebug(ex, "Variant conversion skipped '{FilePath}': unparseable.", filePath);
            return PostProcessResult.Unchanged($"could not parse '{Path.GetFileName(filePath)}': {ex.Message}");
        }

        var changed = false;
        foreach (var item in items)
        {
            changed |= ConvertLines(item.Lines, mode);
            ConvertLines(item.PlaintextLines, mode);
        }

        if (!changed)
        {
            return PostProcessResult.Unchanged("already in the target variant");
        }

        if (context.PreviewOnly)
        {
            return new PostProcessResult(true, $"would convert to {mode}");
        }

        // The writers emit TranslatedLines (SrtWriter/VttWriter exclusively,
        // SsaWriter preferentially), so the converted lines move there.
        foreach (var item in items)
        {
            item.TranslatedLines = [.. item.Lines];
        }

        var temporary = filePath + ".lingarr-postprocess";
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

        return new PostProcessResult(true, $"converted to {mode}");
    }

    /// <summary>Converts every line in place; true when any line changed.</summary>
    private static bool ConvertLines(List<string> lines, string mode)
    {
        var changed = false;
        for (var index = 0; index < lines.Count; index++)
        {
            var converted = ChineseSubtitleConverter.Convert(lines[index], mode);
            if (string.Equals(converted, lines[index], StringComparison.Ordinal))
            {
                continue;
            }

            lines[index] = converted;
            changed = true;
        }

        return changed;
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
