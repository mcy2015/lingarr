using Lingarr.Core.Entities;

namespace Lingarr.Server.Interfaces.Services;

/// <summary>
/// Single seam for all Hangfire API usage (enqueue, delete and queue inspection) used by the
/// translation request services. Registered as a singleton; Hangfire clients are stateless
/// and thread-safe.
/// </summary>
public interface IJobQueue
{
    /// <summary>
    /// Enqueues a translation job for the given request on the translation queue.
    /// </summary>
    /// <returns>The Hangfire job id</returns>
    string EnqueueTranslationJob(TranslationRequest translationRequest);

    /// <summary>
    /// Enqueues a proofread job for the given request on the translation queue.
    /// </summary>
    /// <returns>The Hangfire job id</returns>
    string EnqueueProofreadJob(TranslationRequest translationRequest);

    /// <summary>
    /// Deletes a background job by id, no-op safe for already completed jobs.
    /// </summary>
    void Delete(string jobId);

    /// <summary>
    /// Returns the ids of translation jobs currently queued (not yet running),
    /// keyed by the translation request id carried as the job argument.
    /// </summary>
    ILookup<int, string> GetQueuedTranslationJobIds();
}
