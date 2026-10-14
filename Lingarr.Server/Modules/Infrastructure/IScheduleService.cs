using Lingarr.Server.Models;

namespace Lingarr.Server.Modules.Infrastructure;

/// <summary>
/// Defines a service responsible for initializing and configuring recurring jobs for media indexing.
/// </summary>
public interface IScheduleService
{
    /// <summary>
    /// Initializes the scheduling service by configuring recurring jobs for media indexing.
    /// </summary>
    /// <remarks>
    /// This method should perform the following tasks:
    /// 1. Retrieve movie and show schedule settings from the configuration service.
    /// 2. Configure recurring jobs for fetching movies and shows based on the retrieved settings.
    /// 3. Use a job scheduling system to schedule jobs with the specified cron expressions.
    /// 4. Re-register user-created recurring jobs and skip jobs the user disabled.
    /// </remarks>
    /// <returns>A task that represents the asynchronous initialization operation.</returns>
    Task Initialize();


    /// <summary>
    /// Gets a list of recurring jobs with their current status. Disabled jobs
    /// are included as Enabled=false rows so the caller can offer re-enabling.
    /// </summary>
    /// <returns>A list of recurring job statuses</returns>
    Task<List<RecurringJobStatus>> GetRecurringJobs();

    /// <summary>
    /// Live per-queue Hangfire depth: enqueued and processing counts for the
    /// configured server queues (plus any queue the storage reports).
    /// </summary>
    /// <returns>One entry per known queue; empty when storage is unavailable.</returns>
    List<QueueDepthStatus> GetQueueDepths();

    /// <summary>
    /// Enables or disables a recurring job. Disabling removes the Hangfire
    /// registration and persists the state so a restart keeps it off;
    /// enabling re-registers it under the same id.
    /// The automated translation job shares its switch with the
    /// automation_enabled setting (settings · automation batch switch).
    /// </summary>
    /// <param name="jobId">Recurring job id (built-in or user-created)</param>
    /// <param name="enabled">true to register, false to remove</param>
    /// <returns>True when the job id is known and the change was applied.</returns>
    Task<bool> SetJobEnabled(string jobId, bool enabled);

    /// <summary>
    /// Updates the cron expression of a recurring job. The schedule screen's
    /// job table is the single place a cron may be edited: built-ins persist
    /// to their settings key, custom jobs to their persisted definition, and
    /// a currently enabled job is re-registered immediately.
    /// </summary>
    /// <param name="jobId">Recurring job id (built-in or user-created)</param>
    /// <param name="cron">New 5-field cron expression</param>
    /// <returns>Updated, or the reason the change was rejected.</returns>
    Task<UpdateCronResult> UpdateJobCron(string jobId, string cron);

    /// <summary>
    /// Gets the current state of a specific job.
    /// </summary>
    /// <param name="jobId">The ID of the job</param>
    /// <returns>The current state of the job</returns>
    string GetJobState(string jobId);

    /// <summary>
    /// Updates the state of a job and notifies clients.
    /// </summary>
    /// <param name="jobId">The ID of the job</param>
    /// <param name="state">The current state</param>
    Task UpdateJobState(string jobId, string state);
}

/// <summary>Outcome of a cron update attempt on a recurring job.</summary>
public enum UpdateCronResult
{
    /// <summary>The cron was persisted and the job re-registered when enabled.</summary>
    Updated,

    /// <summary>No recurring job (built-in or user-created) carries this id.</summary>
    NotFound,

    /// <summary>The expression is not a valid 5-field cron.</summary>
    InvalidCron,

    /// <summary>The backing settings row is absent — migrations have not run.</summary>
    SettingRowMissing
}
