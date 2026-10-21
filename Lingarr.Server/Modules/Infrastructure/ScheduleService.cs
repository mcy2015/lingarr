using Hangfire;
using Hangfire.Storage;
using Lingarr.Core.Configuration;
using Lingarr.Core.Enum;
using Lingarr.Server.Hubs;
using Lingarr.Server.Models;
using Microsoft.AspNetCore.SignalR;
using Microsoft.OpenApi.Extensions;
using Newtonsoft.Json;
using Lingarr.Server.Modules.Media;
using Lingarr.Server.Modules.Settings;
using Lingarr.Server.Modules.Statistics;
using Lingarr.Server.Modules.Sync;
using Lingarr.Server.Modules.Translation;

namespace Lingarr.Server.Modules.Infrastructure;

public class ScheduleService : IScheduleService
{
    /// <summary>
    /// Queues the Hangfire server is configured to work (InfrastructureModule).
    /// The depth endpoint unions these with whatever queues the storage
    /// reports, so unlisted-but-used queues still surface.
    /// </summary>
    public static readonly string[] ServerQueues =
        ["movies", "shows", "system", "translation", "webhook", "default"];

    /// <summary>
    /// Built-in recurring jobs and where their cron comes from. The catalog
    /// backs Initialize(), the enable/disable endpoints, the cron-update
    /// endpoint and the disabled-row merge in GetRecurringJobs() — one
    /// structure, four consumers. The eight built-ins are the whole job
    /// catalogue: nothing else creates recurring jobs anymore (legacy
    /// custom_recurring_jobs entries keep running, frozen per precedent).
    /// </summary>
    private sealed record BuiltinJob(
        string Id,
        string JobType,
        string? CronSetting,
        string FallbackCron);

    private static readonly List<BuiltinJob> BuiltinJobs =
    [
        new("SyncMovieJob", "SyncMovieJob", SettingKeys.Automation.MovieSchedule, "0 4 * * *"),
        new("SyncShowJob", "SyncShowJob", SettingKeys.Automation.ShowSchedule, "0 4 * * *"),
        new("AutomatedTranslationJob", "AutomatedTranslationJob",
            SettingKeys.Automation.TranslationSchedule, "0 4 * * *"),
        new("CleanupJob", "CleanupJob", SettingKeys.Automation.CleanupSchedule, Cron.Weekly()),
        new("StatisticsJob", "StatisticsJob", SettingKeys.Automation.StatisticsSchedule, Cron.Daily()),
        new("EmbeddedTrackScanJob", "EmbeddedTrackScanJob",
            SettingKeys.Automation.EmbeddedTrackSchedule, "30 4 * * *"),
        // 7th built-in (subtitle-search M3, design ⑤): queue=system, cron
        // seeded "0 5 * * *" by M0008 — an hour behind the sync wave. The
        // subtitle_search_enabled master switch additionally short-circuits
        // Execute(), so disabling either the schedule row or the feature
        // stops the rounds.
        new("SubtitleSearchJob", "SubtitleSearchJob",
            SettingKeys.Automation.SubtitleSearchSchedule, "0 5 * * *"),
        // 8th built-in (B02 subtitle upgrade): queue=system, cron seeded
        // "0 4 * * *" by M0014 — inside the maintenance wave. The
        // subtitle_upgrade_enabled master switch (seeded false) governs the
        // registration exactly like the search job's, so an off feature
        // never registers no-op rounds.
        new("SubtitleUpgradeJob", "SubtitleUpgradeJob",
            SettingKeys.Automation.SubtitleUpgradeSchedule, "0 4 * * *")
    ];

    private readonly IHubContext<JobProgressHub> _hubContext;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<IScheduleService> _logger;

    public ScheduleService(
        IHubContext<JobProgressHub> hubContext,
        IServiceProvider serviceProvider,
        ILogger<IScheduleService> logger)
    {
        _serviceProvider = serviceProvider;
        _hubContext = hubContext;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task Initialize()
    {
        using var scope = _serviceProvider.CreateScope();
        var settingService = scope.ServiceProvider.GetRequiredService<ISettingService>();
        var translationRequestService = scope.ServiceProvider.GetRequiredService<ITranslationRequestService>();

        var settings = await settingService.GetSettings([
            SettingKeys.Automation.MovieSchedule,
            SettingKeys.Automation.ShowSchedule,
            SettingKeys.Automation.TranslationSchedule,
            SettingKeys.Automation.CleanupSchedule,
            SettingKeys.Automation.StatisticsSchedule,
            SettingKeys.Automation.EmbeddedTrackSchedule,
            SettingKeys.Automation.SubtitleSearchSchedule,
            SettingKeys.Automation.SubtitleUpgradeSchedule,
            SettingKeys.Automation.AutomationEnabled,
            SettingKeys.SubtitleSearch.Enabled,
            SettingKeys.SubtitleSearch.UpgradeEnabled
        ]);

        if (TimeZoneConfiguration.ResolutionWarning != null)
        {
            _logger.LogWarning("{Warning}", TimeZoneConfiguration.ResolutionWarning);
        }

        var disabled = await ReadDisabledJobIds(settingService);

        _logger.LogInformation("Configuring recurring jobs.");
        foreach (var builtin in BuiltinJobs)
        {
            if (IsBuiltinDisabled(builtin, disabled, settings))
            {
                continue;
            }
            RegisterRecurring(builtin.Id, builtin.JobType, ResolveBuiltinCron(builtin, settings));
        }

        foreach (var custom in await ReadCustomJobs(settingService))
        {
            if (!custom.Enabled)
            {
                continue;
            }
            RegisterRecurring(custom.JobId, custom.JobType, custom.Cron);
        }

        _logger.LogInformation("Cleaning up orphaned processing jobs.");
        var monitor = JobStorage.Current.GetMonitoringApi();
        var processingJobs = monitor.ProcessingJobs(0, int.MaxValue);
        foreach (var job in processingJobs)
        {
            BackgroundJob.Delete(job.Key);
            _logger.LogWarning("Cleaned up orphaned processing job {JobId}", job.Key);
        }

        _logger.LogInformation("Starting pending translation requests.");
        await translationRequestService.ResumeTranslationRequests();
    }

    /// <inheritdoc />
    public async Task<List<RecurringJobStatus>> GetRecurringJobs()
    {
        var monitor = JobStorage.Current.GetMonitoringApi();
        var recurringJobs = JobStorage.Current.GetConnection().GetRecurringJobs();

        var result = recurringJobs
            .Select(job => MapToJobStatus(job, monitor))
            .OrderBy(j => j.Id)
            .ToList();

        // Disabled jobs are removed from storage, so they would vanish from
        // the list — merge the known definitions back in as Enabled=false
        // rows so the schedule screen keeps offering「启用」.
        using var scope = _serviceProvider.CreateScope();
        var settingService = scope.ServiceProvider.GetRequiredService<ISettingService>();
        var liveIds = result.Select(job => job.Id).ToHashSet(StringComparer.Ordinal);
        var disabled = await ReadDisabledJobIds(settingService);
        var settings = await settingService.GetSettings([
            SettingKeys.Automation.MovieSchedule,
            SettingKeys.Automation.ShowSchedule,
            SettingKeys.Automation.TranslationSchedule,
            SettingKeys.Automation.CleanupSchedule,
            SettingKeys.Automation.StatisticsSchedule,
            SettingKeys.Automation.EmbeddedTrackSchedule,
            SettingKeys.Automation.SubtitleSearchSchedule,
            SettingKeys.Automation.SubtitleUpgradeSchedule,
            SettingKeys.Automation.AutomationEnabled,
            SettingKeys.SubtitleSearch.Enabled,
            SettingKeys.SubtitleSearch.UpgradeEnabled
        ]);

        foreach (var builtin in BuiltinJobs)
        {
            if (liveIds.Contains(builtin.Id) || !IsBuiltinDisabled(builtin, disabled, settings))
            {
                continue;
            }

            result.Add(new RecurringJobStatus
            {
                Id = builtin.Id,
                Cron = ResolveBuiltinCron(builtin, settings),
                Queue = string.Empty,
                JobMethod = builtin.JobType,
                Enabled = false
            });
        }

        foreach (var custom in await ReadCustomJobs(settingService))
        {
            if (liveIds.Contains(custom.JobId) || custom.Enabled)
            {
                continue;
            }

            result.Add(new RecurringJobStatus
            {
                Id = custom.JobId,
                Cron = custom.Cron,
                Queue = string.Empty,
                JobMethod = custom.JobType,
                Enabled = false
            });
        }

        return result.OrderBy(job => job.Id).ToList();
    }

    /// <inheritdoc />
    public List<QueueDepthStatus> GetQueueDepths()
    {
        var result = new List<QueueDepthStatus>();
        try
        {
            var monitor = JobStorage.Current.GetMonitoringApi();

            var processingByQueue = monitor.ProcessingJobs(0, int.MaxValue)
                .GroupBy(job => job.Value?.Job?.Queue ?? "default")
                .ToDictionary(group => group.Key, group => (long)group.Count());

            var queueNames = ServerQueues
                .Concat(monitor.Queues().Select(queue => queue.Name))
                .Distinct(StringComparer.Ordinal);

            foreach (var name in queueNames)
            {
                result.Add(new QueueDepthStatus
                {
                    Queue = name,
                    Enqueued = Math.Max(0, monitor.EnqueuedCount(name)),
                    Processing = processingByQueue.GetValueOrDefault(name)
                });
            }
        }
        catch (Exception ex)
        {
            // A storage hiccup must not 500 the schedule screen — report no
            // depths and let the caller render zeros.
            _logger.LogWarning(ex, "Failed to read Hangfire queue depths.");
        }

        return result;
    }

    /// <inheritdoc />
    public async Task<bool> SetJobEnabled(string jobId, bool enabled)
    {
        using var scope = _serviceProvider.CreateScope();
        var settingService = scope.ServiceProvider.GetRequiredService<ISettingService>();

        var customJobs = await ReadCustomJobs(settingService);
        var custom = customJobs.FirstOrDefault(job =>
            string.Equals(job.JobId, jobId, StringComparison.Ordinal));
        if (custom != null)
        {
            custom.Enabled = enabled;
            await settingService.SetSetting(
                SettingKeys.Automation.CustomRecurringJobs,
                SerializeCustomJobs(customJobs));
            if (enabled)
            {
                RegisterRecurring(custom.JobId, custom.JobType, custom.Cron);
            }
            else
            {
                RecurringJob.RemoveIfExists(jobId);
            }
            return true;
        }

        var builtin = BuiltinJobs.FirstOrDefault(job =>
            string.Equals(job.Id, jobId, StringComparison.Ordinal));
        if (builtin == null)
        {
            return false;
        }

        if (builtin.Id == "AutomatedTranslationJob")
        {
            // Same source of truth as the settings · automation switch: the
            // persisted setting bootstraps Initialize(), the direct
            // registration below makes the toggle effective immediately.
            await settingService.SetSetting(
                SettingKeys.Automation.AutomationEnabled,
                enabled ? "true" : "false");
        }
        else if (builtin.Id == "SubtitleSearchJob")
        {
            // AC7 (the broken chain): the schedule toggle is the only UI
            // surface of the subtitle-search master switch
            // (subtitle_search_enabled, seeded false by M0008). Mirroring
            // the automation-switch precedent keeps one source of truth —
            // before this write the toggle registered the job while
            // Execute() still short-circuited every round. The persisted
            // setting also bootstraps Initialize() through
            // IsBuiltinDisabled below, and the direct registration makes
            // the toggle effective immediately.
            await settingService.SetSetting(
                SettingKeys.SubtitleSearch.Enabled,
                enabled ? "true" : "false");
            if (enabled)
            {
                // Heal a pre-linkage toggle-off: the old path wrote this
                // job into disabled_recurring_jobs, and IsBuiltinDisabled
                // honours that list first — an enable that left the stale
                // entry behind would stay disabled after the next
                // Initialize(). Automation never needed this because its
                // switch predates the list path.
                var disabled = await ReadDisabledJobIds(settingService);
                if (disabled.Remove(builtin.Id))
                {
                    await settingService.SetSetting(
                        SettingKeys.Automation.DisabledRecurringJobs,
                        JsonConvert.SerializeObject(disabled));
                }
            }
        }
        else if (builtin.Id == "SubtitleUpgradeJob")
        {
            // B02: the same shared-switch semantics as the search job — the
            // schedule toggle is the upgrade master switch's UI surface
            // (subtitle_upgrade_enabled, seeded false by M0014), written in
            // both directions so Initialize(), IsBuiltinDisabled and
            // Execute()'s short-circuit all read one source of truth.
            await settingService.SetSetting(
                SettingKeys.SubtitleSearch.UpgradeEnabled,
                enabled ? "true" : "false");
            if (enabled)
            {
                var disabled = await ReadDisabledJobIds(settingService);
                if (disabled.Remove(builtin.Id))
                {
                    await settingService.SetSetting(
                        SettingKeys.Automation.DisabledRecurringJobs,
                        JsonConvert.SerializeObject(disabled));
                }
            }
        }
        else
        {
            var disabled = await ReadDisabledJobIds(settingService);
            if (enabled)
            {
                disabled.Remove(jobId);
            }
            else
            {
                disabled.Add(jobId);
            }
            await settingService.SetSetting(
                SettingKeys.Automation.DisabledRecurringJobs,
                JsonConvert.SerializeObject(disabled));
        }

        if (enabled)
        {
            var cron = builtin.CronSetting == null
                ? builtin.FallbackCron
                : await ResolveBuiltinCronFromSettings(builtin.CronSetting, builtin.FallbackCron, settingService);
            RegisterRecurring(builtin.Id, builtin.JobType, cron);
        }
        else
        {
            RecurringJob.RemoveIfExists(jobId);
        }
        return true;
    }

    /// <inheritdoc />
    public async Task<UpdateCronResult> UpdateJobCron(string jobId, string cron)
    {
        cron = cron.Trim();
        if (!CronExpressionValidator.IsValid(cron))
        {
            return UpdateCronResult.InvalidCron;
        }

        using var scope = _serviceProvider.CreateScope();
        var settingService = scope.ServiceProvider.GetRequiredService<ISettingService>();

        var customJobs = await ReadCustomJobs(settingService);
        var custom = customJobs.FirstOrDefault(job =>
            string.Equals(job.JobId, jobId, StringComparison.Ordinal));
        if (custom != null)
        {
            custom.Cron = cron;
            await settingService.SetSetting(
                SettingKeys.Automation.CustomRecurringJobs,
                SerializeCustomJobs(customJobs));
            if (custom.Enabled)
            {
                RegisterRecurring(custom.JobId, custom.JobType, cron);
            }
            return UpdateCronResult.Updated;
        }

        var builtin = BuiltinJobs.FirstOrDefault(job =>
            string.Equals(job.Id, jobId, StringComparison.Ordinal));
        if (builtin?.CronSetting == null)
        {
            return UpdateCronResult.NotFound;
        }

        // Fail loudly when the settings row is absent instead of silently
        // keeping the old cron on the next restart.
        if (!await settingService.SetSetting(builtin.CronSetting, cron))
        {
            return UpdateCronResult.SettingRowMissing;
        }

        var disabled = await ReadDisabledJobIds(settingService);
        var settings = await settingService.GetSettings([
            SettingKeys.Automation.AutomationEnabled,
            SettingKeys.SubtitleSearch.Enabled,
            SettingKeys.SubtitleSearch.UpgradeEnabled
        ]);
        if (!IsBuiltinDisabled(builtin, disabled, settings))
        {
            RegisterRecurring(builtin.Id, builtin.JobType, cron);
        }
        return UpdateCronResult.Updated;
    }

    public string GetJobState(string jobId)
    {
        var monitor = JobStorage.Current.GetMonitoringApi();

        // Check each possible state
        if (monitor.SucceededJobs(0, 1).Any(j => j.Key == jobId))
            return JobStatus.Succeeded.GetDisplayName();
        if (monitor.FailedJobs(0, 1).Any(j => j.Key == jobId))
            return JobStatus.Failed.GetDisplayName();
        if (monitor.ScheduledJobs(0, 1).Any(j => j.Key == jobId))
            return JobStatus.Scheduled.GetDisplayName();
        if (monitor.EnqueuedJobs("default", 0, 1).Any(j => j.Key == jobId))
            return JobStatus.Enqueued.GetDisplayName();

        return JobStatus.Planned.GetDisplayName();
    }

    public async Task UpdateJobState(string jobId, string state)
    {
        try
        {
            await _hubContext.Clients.Group("JobProgress")
                .SendAsync("JobStateUpdated", jobId, state);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating job state for job {JobId}", jobId);
            throw;
        }
    }

    private static async Task<string> ResolveBuiltinCronFromSettings(
        string cronSetting,
        string fallback,
        ISettingService settingService)
    {
        var settings = await settingService.GetSettings([cronSetting]);
        var value = settings.GetValueOrDefault(cronSetting);
        return string.IsNullOrWhiteSpace(value) ? fallback : value;
    }

    /// <summary>Registers a job id for a known job type — the single AddOrUpdate path.</summary>
    private static void RegisterRecurring(string jobId, string jobType, string cron)
    {
        var options = new RecurringJobOptions { TimeZone = TimeZoneConfiguration.Current };
        switch (jobType)
        {
            case "SyncMovieJob":
                RecurringJob.AddOrUpdate<SyncMovieJob>(jobId, job => job.Execute(), cron, options);
                break;
            case "SyncShowJob":
                RecurringJob.AddOrUpdate<SyncShowJob>(jobId, job => job.Execute(), cron, options);
                break;
            case "AutomatedTranslationJob":
                RecurringJob.AddOrUpdate<AutomatedTranslationJob>(jobId, job => job.Execute(), cron, options);
                break;
            case "CleanupJob":
                RecurringJob.AddOrUpdate<CleanupJob>(jobId, job => job.Execute(), cron, options);
                break;
            case "StatisticsJob":
                RecurringJob.AddOrUpdate<StatisticsJob>(jobId, job => job.Execute(), cron, options);
                break;
            case "EmbeddedTrackScanJob":
                RecurringJob.AddOrUpdate<EmbeddedTrackScanJob>(jobId, job => job.Execute(), cron, options);
                break;
            case "SubtitleSearchJob":
                RecurringJob.AddOrUpdate<Lingarr.Server.Modules.SubtitleSearch.Jobs.SubtitleSearchJob>(
                    jobId, job => job.Execute(), cron, options);
                break;
            case "SubtitleUpgradeJob":
                RecurringJob.AddOrUpdate<Lingarr.Server.Modules.SubtitleSearch.Jobs.SubtitleUpgradeJob>(
                    jobId, job => job.Execute(), cron, options);
                break;
            default:
                throw new ArgumentException($"Unsupported job type '{jobType}'.", nameof(jobType));
        }
    }

    /// <summary>
    /// A built-in is effectively disabled when it carries the disabled flag,
    /// or — for the automated translation job — when the automation switch
    /// the settings screen shares is off. The subtitle-search job follows the
    /// same shared-switch semantics through subtitle_search_enabled (AC7),
    /// and the subtitle-upgrade job through subtitle_upgrade_enabled (B02):
    /// the master switch each Execute() short-circuits on also governs the
    /// registration, so an off feature never registers no-op rounds and the
    /// schedule screen honestly offers「启用」on the merged disabled row.
    /// </summary>
    private static bool IsBuiltinDisabled(
        BuiltinJob builtin,
        HashSet<string> disabled,
        Dictionary<string, string> settings)
    {
        if (disabled.Contains(builtin.Id))
        {
            return true;
        }
        if (builtin.Id == "AutomatedTranslationJob")
        {
            return settings.GetValueOrDefault(SettingKeys.Automation.AutomationEnabled) != "true";
        }
        if (builtin.Id == "SubtitleSearchJob")
        {
            return settings.GetValueOrDefault(SettingKeys.SubtitleSearch.Enabled) != "true";
        }
        return builtin.Id == "SubtitleUpgradeJob" &&
               settings.GetValueOrDefault(SettingKeys.SubtitleSearch.UpgradeEnabled) != "true";
    }

    private static string ResolveBuiltinCron(BuiltinJob builtin, Dictionary<string, string> settings)
    {
        if (builtin.CronSetting == null)
        {
            return builtin.FallbackCron;
        }
        var value = settings.GetValueOrDefault(builtin.CronSetting);
        return string.IsNullOrWhiteSpace(value) ? builtin.FallbackCron : value;
    }

    private async Task<HashSet<string>> ReadDisabledJobIds(ISettingService settingService)
    {
        var json = await settingService.GetSetting(SettingKeys.Automation.DisabledRecurringJobs);
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }
        try
        {
            return JsonConvert.DeserializeObject<List<string>>(json)?.ToHashSet(StringComparer.Ordinal) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private async Task<List<CustomRecurringJob>> ReadCustomJobs(ISettingService settingService)
    {
        var json = await settingService.GetSetting(SettingKeys.Automation.CustomRecurringJobs);
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }
        try
        {
            return JsonConvert.DeserializeObject<List<CustomRecurringJob>>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static string SerializeCustomJobs(List<CustomRecurringJob> jobs)
    {
        return JsonConvert.SerializeObject(jobs);
    }

    private RecurringJobStatus MapToJobStatus(RecurringJobDto dto, IMonitoringApi monitor)
    {
        var status = new RecurringJobStatus
        {
            Id = dto.Id,
            Cron = dto.Cron,
            Queue = dto.Queue,
            JobMethod = dto.Job?.Method?.Name ?? string.Empty,
            NextExecution = dto.NextExecution,
            LastJobId = dto.LastJobId,
            LastJobState = dto.LastJobState,
            LastExecution = dto.LastExecution,
            CreatedAt = dto.CreatedAt,
            TimeZoneId = dto.TimeZoneId,
            Enabled = true
        };

        // Check if there's a currently running job for this recurring job
        if (!string.IsNullOrEmpty(dto.LastJobId))
        {
            var processingJobs = monitor.ProcessingJobs(0, int.MaxValue);
            var currentJob = processingJobs.FirstOrDefault(j =>
                j.Key == dto.LastJobId ||
                (j.Value?.Job?.Args?.Contains(dto.Id) ?? false));

            if (currentJob.Value != null)
            {
                status.IsCurrentlyRunning = true;
                status.CurrentState = "Processing";
                status.CurrentJobId = currentJob.Key;
            }
            else
            {
                // Check other states if not processing
                status.CurrentState = GetJobState(dto.LastJobId);
            }
        }

        return status;
    }
}
