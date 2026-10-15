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
/// runs are cheap.
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

    [DisableConcurrentExecution(timeoutInSeconds: 5 * 60)]
    [AutomaticRetry(Attempts = 0)]
    [Queue("system")]
    public async Task Execute()
    {
        var jobName = JobContextFilter.GetCurrentJobTypeName();
        _logger.LogInformation("Embedded track scan job initiated");

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
                "Embedded track scan aborted: {Message} — the Docker image ships ffmpeg; for other setups install ffmpeg or set LINGARR_FFPROBE_PATH.",
                ex.Message);
        }
        catch (Exception ex)
        {
            await _scheduleService.UpdateJobState(jobName, JobStatus.Failed.GetDisplayName());
            _logger.LogError(ex,
                "An error occurred during the embedded track scan. Exception details: {ExceptionMessage}, Stack Trace: {StackTrace}",
                ex.Message, ex.StackTrace);
        }
    }
}
