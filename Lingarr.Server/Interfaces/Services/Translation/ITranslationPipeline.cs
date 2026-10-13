using Lingarr.Contracts.Translation;
using Lingarr.Core.Entities;
using Lingarr.Server.Interfaces.Services.Translation;
using Lingarr.Server.Models.FileSystem;

namespace Lingarr.Server.Interfaces.Services.Translation;

/// <summary>
/// Input for a single pipeline run. File-based jobs pass subtitle items read
/// from disk (mergeStackedLines enabled); content translations pass in-memory
/// lines without timing (stacking merge disabled, batch requires &gt;1 line).
/// </summary>
public sealed record TranslationRun(
    TranslationRequest Request,
    List<SubtitleItem> Subtitles,
    bool MergeStackedLines = true,
    bool BatchRequiresMultipleLines = false);

/// <summary>
/// Resolved dispatch plan: settings + service chain. Kept as a separate phase
/// so callers can fail fast before creating any request rows.
/// </summary>
public sealed record TranslationDispatch(
    IReadOnlyList<TranslationServiceEntry> Services,
    string ServiceType,
    bool StripSubtitleFormatting,
    bool PreserveLineBreaks)
{
    public bool UseBatch { get; init; }
    public int MaxBatchSize { get; init; } = 10000;
    public int ContextBefore { get; init; }
    public int ContextAfter { get; init; }
    public bool UseTranslatedContext { get; init; }
}

/// <summary>
/// Outcome of a pipeline run: the translated items (mutated in place, same
/// list reference) plus the service identity that actually produced them,
/// for statistics and translator-info stamping by the caller. The service is
/// derived from the per-position provenance of the run (majority producer,
/// most recent on ties) so a run completed by a fallback reports the
/// fallback, not the configured primary. The primary is only reported when
/// the run translated nothing new.
/// </summary>
public sealed record TranslationRunResult(
    List<SubtitleItem> TranslatedSubtitles,
    string ServiceType,
    ITranslationService PrimaryService,
    bool UsedBatchTranslation,
    bool StripSubtitleFormatting);

/// <summary>
/// The shared translation core used by both execution paths: the Hangfire
/// <c>TranslationJob</c> (file source) and the <c>/translate/content</c>
/// pipeline (in-memory source). Owns dispatch settings, service chain
/// resolution and the batch-vs-individual engine invocation. Request
/// lifecycle, validation, resume hydration and result persistence stay with
/// the callers.
/// </summary>
public interface ITranslationPipeline
{
    /// <summary>
    /// Resolves settings and the service chain. Throws TranslationException when no
    /// usable service is configured — call before creating request rows.
    /// </summary>
    Task<TranslationDispatch> PrepareAsync();

    /// <summary>
    /// Executes the translation. Pass a dispatch from <see cref="PrepareAsync"/> or
    /// omit it to resolve inline.
    /// </summary>
    Task<TranslationRunResult> RunAsync(
        TranslationRun run,
        TranslationDispatch? dispatch = null,
        CancellationToken cancellationToken = default);
}
