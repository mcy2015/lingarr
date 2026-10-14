using Hangfire;
using Microsoft.AspNetCore.Authorization;
using Lingarr.Core.Configuration;
using Lingarr.Server.Models;
using Microsoft.AspNetCore.Mvc;
using Lingarr.Server.Modules.Infrastructure;
using Lingarr.Server.Modules.Sync;
using Lingarr.Server.Modules.Translation;

namespace Lingarr.Server.Controllers;

[ApiController]
[Authorize("LingarrAccess")]
[Route("api/[controller]")]
public class ScheduleController : ControllerBase
{
    private readonly IScheduleService _scheduleService;
    private readonly IBackgroundJobClient _backgroundJobClient;

    public ScheduleController(
        IScheduleService scheduleService,
        IBackgroundJobClient backgroundJobClient)
    {
        _backgroundJobClient = backgroundJobClient;
        _scheduleService = scheduleService;
    }

    /// <summary>
    /// Retrieves information about all jobs in the system.
    /// </summary>
    /// <returns>A list of job information including status, progress, and other details.</returns>
    [HttpGet("jobs")]
    public async Task<IActionResult> RecurringJobs()
    {
        var jobs = await _scheduleService.GetRecurringJobs();
        return Ok(jobs);
    }

    /// <summary>
    /// The effective schedule time zone (IANA id): crons are interpreted in
    /// it and schedule times are displayed in it. Warning is non-null when
    /// TZ is unset or invalid and the zone fell back to UTC.
    /// </summary>
    [HttpGet("time-zone")]
    public IActionResult TimeZone()
    {
        return Ok(new ScheduleTimeZone
        {
            IanaId = TimeZoneConfiguration.IanaId,
            Warning = TimeZoneConfiguration.ResolutionWarning
        });
    }

    /// <summary>
    /// Live queue depth for the schedule screen strip: enqueued and
    /// processing counts per Hangfire queue.
    /// </summary>
    /// <returns>One entry per configured server queue (plus any queue the storage reports).</returns>
    [HttpGet("depth")]
    public IActionResult QueueDepths()
    {
        return Ok(_scheduleService.GetQueueDepths());
    }

    /// <summary>
    /// Enables a recurring job: re-registers it under the same id with the
    /// cron its definition carries (built-in setting or custom cron).
    /// </summary>
    /// <param name="jobId">Recurring job id (built-in or user-created).</param>
    /// <returns>200 on success; 404 when the job id is unknown.</returns>
    [HttpPost("job/{jobId}/enable")]
    public async Task<IActionResult> EnableJob(string jobId)
    {
        if (!await _scheduleService.SetJobEnabled(jobId, true))
        {
            return NotFound($"Recurring job '{jobId}' not found.");
        }
        return Ok();
    }

    /// <summary>
    /// Disables a recurring job: removes the Hangfire registration and
    /// persists the state so a restart keeps it off.
    /// </summary>
    /// <param name="jobId">Recurring job id (built-in or user-created).</param>
    /// <returns>200 on success; 404 when the job id is unknown.</returns>
    [HttpPost("job/{jobId}/disable")]
    public async Task<IActionResult> DisableJob(string jobId)
    {
        if (!await _scheduleService.SetJobEnabled(jobId, false))
        {
            return NotFound($"Recurring job '{jobId}' not found.");
        }
        return Ok();
    }

    /// <summary>
    /// Updates the cron of a recurring job. This is the single edit entry
    /// for schedules — built-ins persist to their settings key and are
    /// re-registered immediately.
    /// </summary>
    /// <param name="jobId">Recurring job id (built-in or user-created).</param>
    /// <param name="request">The new cron expression.</param>
    /// <returns>
    /// 200 on success; 400 for an invalid cron; 404 for an unknown job id;
    /// 409 when the backing settings row is missing (migrations not run).
    /// </returns>
    [HttpPut("job/{jobId}/cron")]
    public async Task<IActionResult> UpdateCron(string jobId, [FromBody] UpdateCronRequest request)
    {
        if (!CronExpressionValidator.IsValid(request.Cron))
        {
            return BadRequest("Invalid cron expression.");
        }

        var result = await _scheduleService.UpdateJobCron(jobId, request.Cron);
        return result switch
        {
            UpdateCronResult.Updated => Ok(),
            UpdateCronResult.NotFound => NotFound($"Recurring job '{jobId}' not found."),
            UpdateCronResult.SettingRowMissing => Conflict(
                $"The settings row backing '{jobId}' is missing; run migrations."),
            _ => BadRequest("Invalid cron expression.")
        };
    }

    /// <summary>
    /// 
    /// </summary>
    /// <returns></returns>
    [HttpPost("job/start")]
    public IActionResult StartJob([FromBody] StartJobRequest recurringJob)
    {
        RecurringJob.TriggerJob(recurringJob.JobName);
        return Ok();
    }

    /// <summary>
    /// Enqueues a background job to automate translation.
    /// </summary>
    /// <returns>Returns an HTTP 200 OK response indicating that the job has been successfully enqueued.</returns>
    [HttpGet("job/automation")]
    public IActionResult StartAutomationJob()
    {
        _backgroundJobClient.Enqueue<AutomatedTranslationJob>(job => job.Execute());
        return Ok();
    }

    /// <summary>
    /// Enqueues a background job to retrieve movie data.
    /// </summary>
    /// <returns>Returns an HTTP 200 OK response indicating that the job has been successfully enqueued.</returns>
    [HttpGet("job/movie")]
    public IActionResult StartMovieJob()
    {
        _backgroundJobClient.Enqueue<SyncMovieJob>(job => job.Execute());
        return Ok();
    }

    /// <summary>
    /// Enqueues a background job to retrieve show data.
    /// </summary>
    /// <returns>Returns an HTTP 200 OK response indicating that the job has been successfully enqueued.</returns>
    [HttpGet("job/show")]
    public IActionResult StartShowJob()
    {
        _backgroundJobClient.Enqueue<SyncShowJob>(job => job.Execute());
        return Ok();
    }
    
    /// <summary>
    /// Attempts to remove a background job from the queue or stop it if it's already running.
    /// </summary>
    /// <param name="jobId">The unique identifier of the job to be removed.</param>
    /// <returns>
    /// Returns an HTTP 200 OK response with a success message if the job was successfully removed or stopped.
    /// Returns an HTTP 404 Not Found response if the job doesn't exist or couldn't be removed.
    /// </returns>
    [HttpDelete("job/remove/{jobId}")]
    public ActionResult<string> RemoveJob(string jobId)
    {
        bool result = _backgroundJobClient.Delete(jobId);
    
        if (result)
        {
            return Ok($"Job {jobId} has been successfully removed or stopped.");
        }

        return NotFound($"Job {jobId} not found or could not be removed.");
    }
    
    /// <summary>
    /// Enqueues a background job to reindex shows and movies
    /// </summary>
    /// <returns>Returns an HTTP 200 OK response indicating that both jobs has been successfully enqueued.</returns>
    [HttpPost("job/index/movies")]
    public IActionResult IndexMovies()
    {
        _backgroundJobClient.Enqueue<SyncMovieJob>(job => job.Execute());
        return Ok();
    }
    
    /// <summary>
    /// Enqueues a background job to reindex shows and movies
    /// </summary>
    /// <returns>Returns an HTTP 200 OK response indicating that both jobs has been successfully enqueued.</returns>
    [HttpPost("job/index/shows")]
    public IActionResult IndexShows()
    {
        _backgroundJobClient.Enqueue<SyncShowJob>(job => job.Execute());
        return Ok();
    }
}