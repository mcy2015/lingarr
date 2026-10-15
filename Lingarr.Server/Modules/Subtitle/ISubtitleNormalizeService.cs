using Lingarr.Server.Models.Api;

namespace Lingarr.Server.Modules.Subtitle;

/// <summary>
/// Machine-readable skip reasons of a normalize preview. The client maps
/// each onto a localized explanation; the server never localizes them.
/// </summary>
public static class SubtitleNormalizeSkipReasons
{
    /// <summary>Movie scope: the subtitle's directory holds no video file.</summary>
    public const string NoVideoInDirectory = "noVideoInDirectory";

    /// <summary>Movie scope: the subtitle's directory holds more than one video file.</summary>
    public const string AmbiguousVideo = "ambiguousVideo";

    /// <summary>Show scope: no episode in the subtitle's directory matches its base name.</summary>
    public const string NoEpisodeMatch = "noEpisodeMatch";

    /// <summary>Show scope: several episodes match the subtitle's base name.</summary>
    public const string AmbiguousEpisodeMatch = "ambiguousEpisodeMatch";

    /// <summary>No language tag the parser recognizes (content sniffing is not used here).</summary>
    public const string LanguageUnknown = "languageUnknown";

    /// <summary>Dual-language file (scjp / 简日 …) — renaming to one language code would lie.</summary>
    public const string DualLanguage = "dualLanguage";

    /// <summary>A file already occupies the planned new name on disk.</summary>
    public const string TargetConflict = "targetConflict";

    /// <summary>Two files of this batch would claim the same new name.</summary>
    public const string TargetCollisionInBatch = "targetCollisionInBatch";

    /// <summary>The file name already is the normalized form.</summary>
    public const string AlreadyNormalized = "alreadyNormalized";
}

/// <summary>
/// Batch-renames existing, loosely named external subtitles of one media
/// item onto the <c>{media base name}.{language}.{caption}.{ext}</c> form
/// (F3). Two-phase by design: <see cref="Preview"/> computes a diff without
/// touching disk; <see cref="Execute"/> only renames items the user
/// confirmed back, re-validating each one at execution time.
/// </summary>
public interface ISubtitleNormalizeService
{
    /// <summary>
    /// Computes the rename plan for one movie or show. Anything ambiguous
    /// (no/ambiguous media match, unknown or dual language, target conflicts,
    /// already-normalized names) lands in <c>skipped</c> with a reason instead
    /// of the executable list. Pure read: no file is created, moved or
    /// deleted. Throws <see cref="KeyNotFoundException"/> for an unknown
    /// media id or a media item without a stored path, and
    /// <see cref="InvalidOperationException"/> when the scan exceeds the
    /// 500-file cap.
    /// </summary>
    Task<SubtitleNormalizePreviewResponse> Preview(string mediaType, int mediaId);

    /// <summary>
    /// Renames the confirmed preview items. Every item is re-validated
    /// (source exists, target free, target stays in the source directory)
    /// and executed independently — one failure never blocks or rolls back
    /// the others. Each successful rename is written to the information log
    /// with old path, new path, language and media title.
    /// </summary>
    Task<SubtitleNormalizeExecuteResponse> Execute(
        string mediaType,
        int mediaId,
        List<SubtitleNormalizeExecuteItem> confirmedItems);
}
