using Hangfire;
using Lingarr.Core.Configuration;
using Lingarr.Core.Enum;
using Lingarr.Server.Filters;
using Lingarr.Server.Modules.Integration;
using Lingarr.Server.Modules.Sync;
using Microsoft.OpenApi.Extensions;
using Lingarr.Server.Modules.Infrastructure;
using Lingarr.Server.Modules.Settings;

namespace Lingarr.Server.Modules.Sync;

public class SyncMovieJob
{
    private readonly IRadarrService _radarrService;
    private readonly ILogger<SyncMovieJob> _logger;
    private readonly IScheduleService _scheduleService;
    private readonly IMovieSyncService _movieSyncService;
    private readonly ISettingService _settingService;

    public SyncMovieJob(
        IRadarrService radarrService,
        ILogger<SyncMovieJob> logger,
        IScheduleService scheduleService,
        IMovieSyncService movieSyncService,
        ISettingService settingService)
    {
        _radarrService = radarrService;
        _logger = logger;
        _scheduleService = scheduleService;
        _movieSyncService = movieSyncService;
        _settingService = settingService;
    }

    [DisableConcurrentExecution(timeoutInSeconds: 5 * 60)]
    [AutomaticRetry(Attempts = 0)]
    [Queue("movies")]
    public async Task Execute()
    {
        var jobName = JobContextFilter.GetCurrentJobTypeName();
        _logger.LogInformation("Radarr sync job initiated");

        try
        {
            await _scheduleService.UpdateJobState(jobName, JobStatus.Processing.GetDisplayName());

            // No whole-run transaction on purpose: a multi-hour outer transaction holds
            // the SQLite write lock and blocks WAL checkpoints. Batches of 100 commit
            // autonomously inside MovieSyncService; a mid-run failure leaves a partial
            // sync that the next run completes idempotently.
            var defaultInclude = await _settingService.GetSetting(SettingKeys.Integration.RadarrDefaultInclude) == "true";
            var existingRadarrIds = await _movieSyncService.SyncMovies(
                _radarrService.GetMovies(), defaultInclude);

            _logger.LogInformation("Fetched and synced {Count} movies from Radarr", existingRadarrIds.Count);

            await _movieSyncService.RemoveNonExistentMovies(existingRadarrIds);

            await _scheduleService.UpdateJobState(jobName, JobStatus.Succeeded.GetDisplayName());
            _logger.LogInformation("Movies synced successfully.");
        }
        catch (Exception ex)
        {
            await _scheduleService.UpdateJobState(jobName, JobStatus.Failed.GetDisplayName());
            _logger.LogError(ex, "An error occurred when syncing movies.");
            // Attempts=0 means no retry storm: rethrowing only marks the
            // job Failed in Hangfire storage, so the failure survives
            // restarts on the schedule screen and feeds the server
            // exception filter's file log.
            throw;
        }
    }
}
