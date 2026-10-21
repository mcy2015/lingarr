using Lingarr.Server.Models.FileSystem;
using Microsoft.Extensions.Logging;

namespace Lingarr.Server.Modules.Subtitle;

/// <summary>
/// Result of one two-file bilingual merge (B06).
/// </summary>
/// <param name="OutputPath">The written output file (naming per the subtitle naming template, <c>{base}.{primary}.{secondary}</c> with the default).</param>
/// <param name="PrimaryCues">Cues the primary (main-language, top line) file contributed.</param>
/// <param name="SecondaryCues">Cues the secondary (appended below) file contributed.</param>
/// <param name="PairedCues">Cues that paired into bilingual output cues.</param>
/// <param name="PrimaryOnlyCues">Unpaired primary cues preserved monolingual.</param>
/// <param name="SecondaryOnlyCues">Unpaired secondary cues preserved monolingual.</param>
public sealed record BilingualMergeResult(
    string OutputPath,
    int PrimaryCues,
    int SecondaryCues,
    int PairedCues,
    int PrimaryOnlyCues,
    int SecondaryOnlyCues);

/// <summary>
/// Merges two already-existing external subtitle files (e.g. a downloaded
/// zh file and an existing en file) into one bilingual file (B06). This is
/// the file-level counterpart of the translation flow's per-line bilingual
/// output — same writers, same line order: the primary language on top,
/// the secondary appended below.
/// </summary>
public interface IBilingualMergeService
{
    /// <summary>
    /// Merges <paramref name="secondaryPath"/> under
    /// <paramref name="primaryPath"/>: cues whose overlap covers at least
    /// half of the shorter cue pair into bilingual cues (primary timing
    /// wins); every unpaired cue is preserved monolingual.
    /// </summary>
    /// <exception cref="InvalidOperationException">Request-shape problem (same language, same file, missing file, both files empty, output would overwrite an input).</exception>
    Task<BilingualMergeResult> MergeAsync(
        string primaryPath,
        string primaryLanguage,
        string secondaryPath,
        string secondaryLanguage,
        CancellationToken cancellationToken);
}

/// <inheritdoc />
/// <remarks>
/// Pairing is a chronological greedy merge over both start-sorted cue
/// streams: a cue pairs with the current counterpart when their overlap
/// covers ≥ 50% of the shorter cue; the cue that ends first and found no
/// partner is flushed monolingual (a later cue of the other stream cannot
/// contain it any better — containment of the flushed cue would have
/// paired at ratio 1). This handles the acceptance fixtures exactly:
/// ≤200 ms misalignment pairs, ≥500 ms misalignment (on sub-second cues)
/// leaves both sides monolingual. Text placement follows the existing
/// bilingual writer contract: <see cref="SubtitleItem.TranslatedLines"/>
/// render on top, the <see cref="SubtitleItem.PlaintextLines"/>-joined
/// original below — so the primary language sits above the secondary in
/// every format SrtWriter/SsaWriter/VttWriter emit.
/// </remarks>
public sealed class BilingualMergeService : IBilingualMergeService
{
    /// <summary>Two cues pair when their overlap covers at least this fraction of the shorter cue.</summary>
    public const double PairOverlapRatio = 0.5;

    private readonly ISubtitleService _subtitleService;
    private readonly ILogger<BilingualMergeService> _logger;

    public BilingualMergeService(
        ISubtitleService subtitleService,
        ILogger<BilingualMergeService> logger)
    {
        _subtitleService = subtitleService;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<BilingualMergeResult> MergeAsync(
        string primaryPath,
        string primaryLanguage,
        string secondaryPath,
        string secondaryLanguage,
        CancellationToken cancellationToken)
    {
        primaryLanguage = (primaryLanguage ?? string.Empty).Trim();
        secondaryLanguage = (secondaryLanguage ?? string.Empty).Trim();
        if (primaryLanguage.Length == 0 || secondaryLanguage.Length == 0)
        {
            throw new InvalidOperationException("both language codes are required.");
        }

        if (string.Equals(primaryLanguage, secondaryLanguage, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"both files carry the same language '{primaryLanguage}' — a bilingual merge needs two different languages.");
        }

        if (string.Equals(primaryPath, secondaryPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("the two subtitle paths are the same file.");
        }

        if (!File.Exists(primaryPath))
        {
            throw new InvalidOperationException($"subtitle file '{primaryPath}' was not found on the server.");
        }

        if (!File.Exists(secondaryPath))
        {
            throw new InvalidOperationException($"subtitle file '{secondaryPath}' was not found on the server.");
        }

        var primaryCues = await ReadLeniently(primaryPath);
        var secondaryCues = await ReadLeniently(secondaryPath);
        if (primaryCues.Count == 0 && secondaryCues.Count == 0)
        {
            throw new InvalidOperationException("neither file contains any cue — there is nothing to merge.");
        }

        var (merged, paired, primaryOnly, secondaryOnly) = MergeCues(primaryCues, secondaryCues);

        // The SRT/VTT writers emit Position as the entry number — the merged
        // stream is chronological, so a clean 1..N renumbering is also the
        // honest numbering of the output.
        for (var index = 0; index < merged.Count; index++)
        {
            merged[index].Position = index + 1;
        }

        // Output naming goes through CreateFilePath so the configured naming
        // template applies ({原名}.{语言} default → {base}.{primary}, with the
        // secondary language appended as the tag → {base}.zh.en.srt|ass).
        var outputPath = await _subtitleService.CreateFilePath(
            primaryPath, primaryLanguage, secondaryLanguage);

        // A custom template can fold the name onto one of the inputs —
        // merging must never overwrite a file it just read.
        if (string.Equals(outputPath, primaryPath, StringComparison.OrdinalIgnoreCase)
            || string.Equals(outputPath, secondaryPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"the naming template resolves the output onto an input file ('{outputPath}') — adjust the subtitle naming template.");
        }

        // stripSubtitleFormatting stays off: the merge must not discard the
        // formatting of either input. An existing output from an earlier
        // merge is overwritten (re-merging is idempotent that way).
        await _subtitleService.WriteSubtitles(
            outputPath, merged, stripSubtitleFormatting: false, bilingual: true);

        _logger.LogInformation(
            "Bilingual merge of {Primary} ({PrimaryLanguage}) + {Secondary} ({SecondaryLanguage}): {Paired} paired, {PrimaryOnly} primary-only, {SecondaryOnly} secondary-only cues → {Output}",
            primaryPath, primaryLanguage, secondaryPath, secondaryLanguage,
            paired, primaryOnly, secondaryOnly, outputPath);

        return new BilingualMergeResult(
            outputPath, primaryCues.Count, secondaryCues.Count, paired, primaryOnly, secondaryOnly);
    }

    /// <summary>
    /// Reads one merge input. The parsers throw (rather than return an
    /// empty list) when a file carries no cue at all — a genuinely empty
    /// counterpart is a legal merge (「一空一满」: every cue of the other
    /// file passes through monolingual), so that case maps to zero cues.
    /// Both sides empty is refused by <see cref="MergeAsync"/>.
    /// </summary>
    private async Task<List<SubtitleItem>> ReadLeniently(string path)
    {
        try
        {
            return await _subtitleService.ReadSubtitles(path);
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException)
        {
            _logger.LogInformation(
                "Subtitle file {Path} carries no parsable cue ({Message}); the merge treats it as empty",
                path, ex.Message);
            return [];
        }
        catch (NotSupportedException ex)
        {
            throw new InvalidOperationException(ex.Message);
        }
        catch (IOException ex)
        {
            // Actionable 400 for a manual UI action (locked/unreadable file)
            // instead of an opaque 500.
            throw new InvalidOperationException($"reading subtitle file '{path}' failed: {ex.Message}");
        }
    }

    /// <summary>
    /// The chronological greedy pairing over both start-sorted streams.
    /// Output order never steps back: a flushed cue always starts at or
    /// before the current counterpart (containment would have paired).
    /// </summary>
    private static (List<SubtitleItem> Merged, int Paired, int PrimaryOnly, int SecondaryOnly) MergeCues(
        List<SubtitleItem> primaryCues,
        List<SubtitleItem> secondaryCues)
    {
        var primary = primaryCues.OrderBy(cue => cue.StartTime).ToList();
        var secondary = secondaryCues.OrderBy(cue => cue.StartTime).ToList();

        var merged = new List<SubtitleItem>(primary.Count + secondary.Count);
        var paired = 0;
        var primaryOnly = 0;
        var secondaryOnly = 0;
        var p = 0;
        var s = 0;

        while (p < primary.Count && s < secondary.Count)
        {
            var primaryCue = primary[p];
            var secondaryCue = secondary[s];

            if (Pairs(primaryCue, secondaryCue))
            {
                merged.Add(PairedCue(primaryCue, secondaryCue));
                paired++;
                p++;
                s++;
                continue;
            }

            if (primaryCue.EndTime <= secondaryCue.EndTime)
            {
                merged.Add(MonolingualCue(primaryCue));
                primaryOnly++;
                p++;
            }
            else
            {
                merged.Add(MonolingualCue(secondaryCue));
                secondaryOnly++;
                s++;
            }
        }

        for (; p < primary.Count; p++)
        {
            merged.Add(MonolingualCue(primary[p]));
            primaryOnly++;
        }

        for (; s < secondary.Count; s++)
        {
            merged.Add(MonolingualCue(secondary[s]));
            secondaryOnly++;
        }

        return (merged, paired, primaryOnly, secondaryOnly);
    }

    /// <summary>
    /// Pairing rule (B06): the overlap of the two cues must cover at least
    /// <see cref="PairOverlapRatio"/> of the shorter cue. Zero-duration
    /// (degenerate) cues pair on any overlap.
    /// </summary>
    private static bool Pairs(SubtitleItem primary, SubtitleItem secondary)
    {
        var overlap = Math.Min(primary.EndTime, secondary.EndTime)
            - Math.Max(primary.StartTime, secondary.StartTime);
        if (overlap <= 0)
        {
            return false;
        }

        var shorter = Math.Min(
            primary.EndTime - primary.StartTime,
            secondary.EndTime - secondary.StartTime);
        if (shorter <= 0)
        {
            return true;
        }

        return (double)overlap / shorter >= PairOverlapRatio;
    }

    /// <summary>
    /// One bilingual cue: the primary cue owns the timing; its text rides
    /// <see cref="SubtitleItem.TranslatedLines"/> (the top lines) while the
    /// secondary's text sits in Lines/PlaintextLines, which every bilingual
    /// writer appends below (SrtWriter.cs:34-56 / SsaWriter.cs:97-112).
    /// </summary>
    private static SubtitleItem PairedCue(SubtitleItem primary, SubtitleItem secondary)
    {
        var primaryText = primary.Lines.Count > 0 ? primary.Lines : primary.PlaintextLines;
        var secondaryText = secondary.Lines.Count > 0 ? secondary.Lines : secondary.PlaintextLines;
        var secondaryPlain = secondary.PlaintextLines.Count > 0 ? secondary.PlaintextLines : secondaryText;

        return new SubtitleItem
        {
            StartTime = primary.StartTime,
            EndTime = primary.EndTime,
            TranslatedLines = [.. primaryText],
            Lines = [.. secondaryText],
            PlaintextLines = [.. secondaryPlain],
            SsaDialogue = primary.SsaDialogue,
            SsaFormat = primary.SsaFormat
        };
    }

    /// <summary>
    /// One unpaired cue preserved in its own language: the text rides the
    /// top slot (<see cref="SubtitleItem.TranslatedLines"/>) with empty
    /// Lines/PlaintextLines, so the bilingual writers render exactly one
    /// line and append no "original".
    /// </summary>
    private static SubtitleItem MonolingualCue(SubtitleItem cue)
    {
        var text = cue.Lines.Count > 0 ? cue.Lines : cue.PlaintextLines;

        return new SubtitleItem
        {
            StartTime = cue.StartTime,
            EndTime = cue.EndTime,
            TranslatedLines = [.. text],
            Lines = [],
            PlaintextLines = [],
            SsaDialogue = cue.SsaDialogue,
            SsaFormat = cue.SsaFormat
        };
    }
}
