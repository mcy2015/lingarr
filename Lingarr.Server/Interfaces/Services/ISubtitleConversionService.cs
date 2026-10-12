using Lingarr.Server.Models.TranslationRequests;

namespace Lingarr.Server.Interfaces.Services;

/// <summary>
/// Rebuilds existing translated subtitle files as monolingual or bilingual
/// without re-invoking any translation service. The persisted per-cue lines
/// (TranslationRequestLine) provide the authoritative source/target pairing.
/// </summary>
public interface ISubtitleConversionService
{
    /// <summary>
    /// Converts the output file of a single completed translation request
    /// to the requested form (bilingual = translated lines on top, original line below).
    /// </summary>
    Task<SubtitleConversionResult> ConvertAsync(int translationRequestId, bool bilingual);

    /// <summary>
    /// Converts every completed translation request that has an output file
    /// to the requested form. Already-converted requests are skipped.
    /// </summary>
    Task<List<SubtitleConversionResult>> ConvertAllAsync(bool bilingual);
}
