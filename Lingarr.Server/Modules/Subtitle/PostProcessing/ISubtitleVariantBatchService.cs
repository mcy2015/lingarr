using Lingarr.Server.Models.Api;

namespace Lingarr.Server.Modules.Subtitle.PostProcessing;

/// <summary>
/// Batch-applies the Chinese variant conversion (through the post-processing
/// composite) to the subtitles already on disk across the whole library
/// (B01). Two-phase like the normalize service: <see cref="PreviewAsync"/>
/// counts what would change without touching a single file; <see cref="ExecuteAsync"/>
/// converts, per file, only files whose parsed text actually changes —
/// already-converted and Chinese-free files keep their bytes and mtime.
/// </summary>
public interface ISubtitleVariantBatchService
{
    /// <summary>
    /// Counts the library subtitle files the current variant mode would
    /// rewrite. Pure read: no file is written (mtimes stay untouched). Throws
    /// <see cref="InvalidOperationException"/> when the configured mode is
    /// "none"/unset (nothing to preview — pick a variant first) or when the
    /// library holds more than 500 subtitle files (batch cap; refuses instead
    /// of truncating silently).
    /// </summary>
    Task<SubtitlePostProcessPreviewResponse> PreviewAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Converts the existing library subtitles per the configured variant
    /// mode. Per-file idempotent: a file is rewritten only when the variant
    /// conversion changes its parsed lines; every file is re-evaluated at
    /// execution time, so the result never depends on the preview having run.
    /// Failures are reported per file and never block the rest.
    /// </summary>
    Task<SubtitlePostProcessExecuteResponse> ExecuteAsync(CancellationToken cancellationToken);
}
