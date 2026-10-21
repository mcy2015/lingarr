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

public class SyncShowJob
{
    private readonly ISonarrService _sonarrService;
    private readonly ILogger<SyncShowJob> _logger;
    private readonly IScheduleService _scheduleService;
    private readonly IShowSyncService _showSyncService;
    private readonly ISettingService _settingService;

    public SyncShowJob(
        ISonarrService sonarrService,
        ILogger<SyncShowJob> logger,
        IScheduleService scheduleService,
        IShowSyncService showSyncService,
        ISettingService settingService)
    {
        _sonarrService = sonarrService;
        _logger = logger;
        _scheduleService = scheduleService;
        _showSyncService = showSyncService;
        _settingService = settingService;
    }

    [DisableConcurrentExecution(timeoutInSeconds: 5 * 60)]
    [AutomaticRetry(Attempts = 0)]
    [Queue("shows")]
    public async Task Execute()
    {
        var jobName = JobContextFilter.GetCurrentJobTypeName();
        _logger.LogInformation("Sonarr sync job initiated");

        try
        {
            await _scheduleService.UpdateJobState(jobName, JobStatus.Processing.GetDisplayName());

            var shows = await _sonarrService.GetShows();
            if (shows == null) return;

            _logger.LogInformation("Fetched {ShowCount} shows from Sonarr", shows.Count);

            var defaultInclude = await _settingService.GetSetting(SettingKeys.Integration.SonarrDefaultInclude) == "true";

            // No whole-run transaction on purpose (see SyncMovieJob): batches of 100
            // commit autonomously inside ShowSyncService; a mid-run failure leaves a
            // partial sync that the next run completes idempotently.
            await _showSyncService.SyncShows(shows, defaultInclude);
            await _showSyncService.RemoveNonExistentShows(shows.Select(s => s.Id).ToHashSet());

            await _scheduleService.UpdateJobState(jobName, JobStatus.Succeeded.GetDisplayName());
            _logger.LogInformation("Shows synced successfully.");
        }
        catch (Exception ex)
        {
            await _scheduleService.UpdateJobState(jobName, JobStatus.Failed.GetDisplayName());
            _logger.LogError(ex, "An error occurred when syncing shows.");
            // Attempts=0 means no retry storm: rethrowing only marks the
            // job Failed in Hangfire storage, so the failure survives
            // restarts on the schedule screen and feeds the server
            // exception filter's file log.
            throw;
        }
    }
}
