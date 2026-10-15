using Lingarr.Core.Entities;
using Lingarr.Core.Enum;
using Lingarr.Contracts.Models;
using Lingarr.Server.Models.Api;
using Lingarr.Server.Models.FileSystem;

namespace Lingarr.Server.Modules.Translation;

/// <summary>
/// The media item already covers the requested target language (external
/// subtitle file or embedded track), so initiating a translation would
/// duplicate it. Carries a user-readable message; the endpoint maps it to
/// HTTP 409.
/// </summary>
public sealed class TargetLanguageAlreadyPresentException(string message) : Exception(message);

/// <summary>
/// Lifecycle operations on translation requests: creation, enqueueing, cancellation,
/// removal, retry, resume and status updates.
/// </summary>
public interface ITranslationRequestQueueService
{
    /// <summary>
    /// Creates a new translation request for a subtitle file and enqueues it for processing.
    /// </summary>
    Task<int> CreateRequest(TranslateAbleSubtitle translateAbleSubtitle);

    /// <summary>
    /// Creates a translation request for a manually imported subtitle file and
    /// enqueues it for processing. Unbound imports (null mediaId) get the neutral
    /// Movie bucket; bound imports carry the resolved Movie/Episode id so outputs
    /// are written next to the media like any library request.
    /// </summary>
    Task<int> CreateImportRequest(
        string subtitlePath,
        string title,
        string sourceLanguage,
        string targetLanguage,
        int? mediaId,
        MediaType mediaType,
        string? contentHash = null,
        bool isBilingual = false);

    /// <summary>
    /// F2b: creates a translation request whose source is a text-based
    /// embedded subtitle track. Extracts the track via ffmpeg (reusing any
    /// existing extraction), materializes it next to the video so the output
    /// lands in the media directory, then runs the regular creation path
    /// (request record, events, progress unchanged).
    /// <para>
    /// Throws <see cref="TargetLanguageAlreadyPresentException"/> when the
    /// target language is already covered by an external file or another
    /// embedded track of the same media; throws
    /// <see cref="InvalidOperationException"/> for graphic tracks, unknown
    /// track languages or extraction failures.
    /// </para>
    /// </summary>
    Task<int> CreateEmbeddedTrackRequest(
        MediaType mediaType,
        int mediaId,
        int streamIndex,
        string targetLanguage,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Cooperatively pauses a running translation at the next batch boundary.
    /// </summary>
    Task<string?> PauseTranslationRequest(TranslationRequest pauseRequest);

    /// <summary>
    /// Releases a paused translation so the running job continues.
    /// </summary>
    Task<string?> ResumePausedTranslationRequest(TranslationRequest resumeRequest);

    /// <summary>
    /// Creates translation requests for multiple media items, handling subtitle discovery
    /// and source language resolution server-side.
    /// </summary>
    Task CreateBulkRequest(BulkTranslateRequest request);

    /// <summary>
    /// Cancels an existing translation request and its associated background job.
    /// </summary>
    Task<string?> CancelTranslationRequest(TranslationRequest cancelRequest);

    /// <summary>
    /// Removes an existing translation request and its associated background job.
    /// </summary>
    Task<string?> RemoveTranslationRequest(TranslationRequest cancelRequest);

    /// <summary>
    /// Retries an existing translation request.
    /// </summary>
    Task<string?> RetryTranslationRequest(TranslationRequest retryRequest);

    /// <summary>
    /// Resumes a Failed, Cancelled or Interrupted translation request by re-running
    /// the same row.
    /// </summary>
    Task<string?> ResumeTranslationRequest(TranslationRequest resumeRequest);

    /// <summary>
    /// Resumes all pending and in-progress translation requests by re-enqueueing them in the job queue.
    /// </summary>
    Task ResumeTranslationRequests();

    /// <summary>
    /// Clears the MediaHash property for the associated media entity (Movie or Episode)
    /// when a translation job fails or is cancelled.
    /// </summary>
    Task ClearMediaHash(TranslationRequest translationRequest);

    /// <summary>
    /// Updates the status and job ID of an existing translation request.
    /// </summary>
    Task<TranslationRequest> UpdateTranslationRequest(
        TranslationRequest translationRequest,
        TranslationStatus status,
        string? jobId = null);
}
