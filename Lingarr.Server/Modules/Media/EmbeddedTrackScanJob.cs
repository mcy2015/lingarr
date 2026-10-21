using Hangfire;
using Lingarr.Core.Enum;
using Lingarr.Server.Exceptions;
using Lingarr.Server.Filters;
using Lingarr.Server.Modules.Infrastructure;
using Microsoft.OpenApi.Extensions;

namespace Lingarr.Server.Modules.Media;

/// <summary>
/// Daily ffprobe sweep over every movie and episode file, persisting the
/// embedded subtitle tracks the detail page's「内嵌轨道」section shows
/// (F2a). Skips files whose mtime matches the stored scan marker, so steady
/// runs are cheap; the per-run batch limit (audit P0-6) bounds first-full
/// runs and resumes from the markers on the next trigger.
/// </summary>
public class EmbeddedTrackScanJob
{
    private readonly IEmbeddedTrackService _embeddedTrackService;
    private readonly IScheduleService _scheduleService;
    private readonly ILogger<EmbeddedTrackScanJob> _logger;

    public EmbeddedTrackScanJob(
        IEmbeddedTrackService embeddedTrackService,
        IScheduleService scheduleService,
        ILogger<EmbeddedTrackScanJob> logger)
    {
        _embeddedTrackService = embeddedTrackService;
        _scheduleService = scheduleService;
        _logger = logger;
    }

    // Audit P0-6: the previous DisableConcurrentExecution(5min) +
    // AutomaticRetry(0) combination meant "a first full run that outlives
    // the lock timeout makes every later daily trigger fail". Replaced by an
    // in-process guard: deployment is a single container (Hangfire SQLite
    // storage is single-server by design), so a process-level flag covers
    // every real overlap window. When the previous instance is still
    // running, this trigger logs, reports success and leaves the work to
    // the next scheduled run (which resumes from the stored markers).
    private static volatile bool _scanInProgress;

    [AutomaticRetry(Attempts = 0)]
    [Queue("system")]
    public async Task Execute()
    {
        var jobName = JobContextFilter.GetCurrentJobTypeName();
        _logger.LogInformation("Embedded track scan job initiated");

        if (_scanInProgress)
        {
            _logger.LogInformation(
                "Embedded track scan skipped: the previous run is still processing. This trigger succeeds without scanning; the next scheduled run resumes from the stored markers.");
            await _scheduleService.UpdateJobState(jobName, JobStatus.Succeeded.GetDisplayName());
            return;
        }

        _scanInProgress = true;
        try
        {
            await _scheduleService.UpdateJobState(jobName, JobStatus.Processing.GetDisplayName());

            var summary = await _embeddedTrackService.ScanAll();
            if (summary.Failed > 0)
            {
                // Individual probe failures were already logged per file; the
                // job still counts as run — the next run retries the failures.
                _logger.LogWarning(
                    "Embedded track scan completed with {Failed} failed files out of {Total}.",
                    summary.Failed, summary.Total);
            }

            await _scheduleService.UpdateJobState(jobName, JobStatus.Succeeded.GetDisplayName());
            _logger.LogInformation("Embedded track scan job finished successfully.");
        }
        catch (FFprobeUnavailableException ex)
        {
            // Environment problem, not a per-file one: fail the job with the
            // actionable message instead of recording "no tracks" everywhere.
            await _scheduleService.UpdateJobState(jobName, JobStatus.Failed.GetDisplayName());
            _logger.LogError(
                ex,
                "Embedded track scan aborted — the Docker image ships ffmpeg; for other setups install ffmpeg or set LINGARR_FFPROBE_PATH.");
            // Attempts=0: the rethrow marks the job Failed in Hangfire
            // storage (persisted on the schedule screen) instead of letting
            // the failure evaporate with the process.
            throw;
        }
        catch (Exception ex)
        {
            await _scheduleService.UpdateJobState(jobName, JobStatus.Failed.GetDisplayName());
            _logger.LogError(ex, "An error occurred during the embedded track scan.");
            // Attempts=0: same rationale — persist the Failed state and let
            // the server exception filter capture the stack in the file log.
            throw;
        }
        finally
        {
            _scanInProgress = false;
        }
    }
}
