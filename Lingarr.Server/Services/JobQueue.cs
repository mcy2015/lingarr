using Hangfire;
using Lingarr.Core.Entities;
using Lingarr.Server.Interfaces.Services;
using Lingarr.Server.Jobs;

namespace Lingarr.Server.Services;

/// <summary>
/// Isolates all Hangfire API usage of the translation request services: enqueueing of
/// translation/proofread jobs, job deletion and inspection of the translation queue.
/// The queue name and job argument shape mirror the previous inline implementations.
/// </summary>
public class JobQueue : IJobQueue
{
    private readonly IBackgroundJobClient _backgroundJobClient;

    public JobQueue(IBackgroundJobClient backgroundJobClient)
    {
        _backgroundJobClient = backgroundJobClient;
    }

    /// <inheritdoc />
    public string EnqueueTranslationJob(TranslationRequest translationRequest)
    {
        return _backgroundJobClient.Enqueue<TranslationJob>(job =>
            job.Execute(translationRequest, CancellationToken.None)
        );
    }

    /// <inheritdoc />
    public string EnqueueProofreadJob(TranslationRequest translationRequest)
    {
        return _backgroundJobClient.Enqueue<ProofreadJob>(job =>
            job.Execute(translationRequest, CancellationToken.None));
    }

    /// <inheritdoc />
    public void Delete(string jobId)
    {
        _backgroundJobClient.Delete(jobId);
    }

    /// <inheritdoc />
    public ILookup<int, string> GetQueuedTranslationJobIds()
    {
        var monitoringApi = JobStorage.Current.GetMonitoringApi();
        return monitoringApi.EnqueuedJobs("translation", 0, int.MaxValue)
            .Select(queuedJob => new
            {
                JobId = queuedJob.Key,
                Request = queuedJob.Value?.Job?.Args?.FirstOrDefault() as TranslationRequest
            })
            .Where(queuedJob => queuedJob.Request != null)
            .ToLookup(queuedJob => queuedJob.Request!.Id, queuedJob => queuedJob.JobId);
    }
}
