using Hangfire;
using Hangfire.Storage;
using Lingarr.Core.Configuration;
using Lingarr.Core.Enum;
using Lingarr.Server.Hubs;
using Lingarr.Server.Models;
using Microsoft.AspNetCore.SignalR;
using Microsoft.OpenApi.Extensions;
using Newtonsoft.Json;
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
    /// backs Initialize(), the enable/disable endpoints and the disabled-row
    /// merge in GetRecurringJobs() — one structure, three consumers.
    /// </summary>
    private sealed record BuiltinJob(string Id, string JobType, string? CronSetting, string FallbackCron);

    private static readonly List<BuiltinJob> BuiltinJobs =
    [
        new("SyncMovieJob", "SyncMovieJob", SettingKeys.Automation.MovieSchedule, "0 4 * * *"),
        new("SyncShowJob", "SyncShowJob", SettingKeys.Automation.ShowSchedule, "0 4 * * *"),
        new("AutomatedTranslationJob", "AutomatedTranslationJob",
            SettingKeys.Automation.TranslationSchedule, "0 4 * * *"),
        new("CleanupJob", "CleanupJob", null, Cron.Weekly()),
        new("StatisticsJob", "StatisticsJob", null, Cron.Daily())
    ];

    /// <summary>Job types new recurring jobs may be created from — never arbitrary types.</summary>
    public static IReadOnlyList<string> RegisteredJobTypes =>
        BuiltinJobs.Select(job => job.JobType).ToList();

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
            SettingKeys.Automation.AutomationEnabled
        ]);

        if (TimeZoneConfiguration.ResolutionWarning != null)
        {
            _logger.LogWarning("{Warning}", TimeZoneConfiguration.ResolutionWarning);
        }

        var disabled = await ReadDisabledJobIds(settingService);

        _logger.LogInformation("Configuring media indexers.");
        foreach (var setting in settings)
        {
            switch (setting.Key)
            {
                case "movie_schedule":
                    if (!disabled.Contains("SyncMovieJob"))
                    {
                        RecurringJob.AddOrUpdate<SyncMovieJob>(
                            "SyncMovieJob",
                            job => job.Execute(),
                            setting.Value,
                            new RecurringJobOptions { TimeZone = TimeZoneConfiguration.Current });
                    }
                    break;
                case "show_schedule":
                    if (!disabled.Contains("SyncShowJob"))
                    {
                        RecurringJob.AddOrUpdate<SyncShowJob>(
                            "SyncShowJob",
                            job => job.Execute(),
                            setting.Value,
                            new RecurringJobOptions { TimeZone = TimeZoneConfiguration.Current });
                    }
                    break;
                case "automation_enabled":
                    if (setting.Value == "true" && !disabled.Contains("AutomatedTranslationJob"))
                    {
                        var translationSchedule = await settingService.GetSetting("translation_schedule");
                        RecurringJob.AddOrUpdate<AutomatedTranslationJob>(
                            "AutomatedTranslationJob",
                            job => job.Execute(),
                            translationSchedule,
                            new RecurringJobOptions { TimeZone = TimeZoneConfiguration.Current });
                    }
                    break;
            }
        }

        if (!disabled.Contains("CleanupJob"))
        {
            RecurringJob.AddOrUpdate<CleanupJob>(
                "CleanupJob",
                job => job.Execute(),
                Cron.Weekly,
                new RecurringJobOptions { TimeZone = TimeZoneConfiguration.Current });
        }

        if (!disabled.Contains("StatisticsJob"))
        {
            RecurringJob.AddOrUpdate<StatisticsJob>(
                "StatisticsJob",
                job => job.Execute(),
                Cron.Daily,
                new RecurringJobOptions { TimeZone = TimeZoneConfiguration.Current });
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
            SettingKeys.Automation.AutomationEnabled
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
            // setting write also drives the existing SettingChangedListener,
            // the direct registration below makes it effective immediately.
            await settingService.SetSetting(
                SettingKeys.Automation.AutomationEnabled,
                enabled ? "true" : "false");
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
    public async Task<RecurringJobStatus?> CreateRecurringJob(string jobId, string jobType, string cron)
    {
        jobId = jobId.Trim();
        cron = cron.Trim();

        if (string.IsNullOrEmpty(jobId) || jobId.Any(char.IsWhiteSpace) ||
            !RegisteredJobTypes.Contains(jobType, StringComparer.Ordinal) ||
            !CronExpressionValidator.IsValid(cron))
        {
            return null;
        }

        var existingIds = JobStorage.Current.GetConnection().GetRecurringJobs()
            .Select(job => job.Id)
            .ToHashSet(StringComparer.Ordinal);
        if (existingIds.Contains(jobId) ||
            BuiltinJobs.Any(job => string.Equals(job.Id, jobId, StringComparison.Ordinal)))
        {
            return null;
        }

        using var scope = _serviceProvider.CreateScope();
        var settingService = scope.ServiceProvider.GetRequiredService<ISettingService>();
        var customJobs = await ReadCustomJobs(settingService);
        if (customJobs.Any(job => string.Equals(job.JobId, jobId, StringComparison.Ordinal)))
        {
            return null;
        }

        customJobs.Add(new CustomRecurringJob
        {
            JobId = jobId,
            JobType = jobType,
            Cron = cron,
            Enabled = true
        });
        await settingService.SetSetting(
            SettingKeys.Automation.CustomRecurringJobs,
            SerializeCustomJobs(customJobs));

        RegisterRecurring(jobId, jobType, cron);

        return new RecurringJobStatus
        {
            Id = jobId,
            Cron = cron,
            JobMethod = jobType,
            Enabled = true,
            CurrentState = JobStatus.Planned.GetDisplayName()
        };
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
            default:
                throw new ArgumentException($"Unsupported job type '{jobType}'.", nameof(jobType));
        }
    }

    /// <summary>
    /// A built-in is effectively disabled when it carries the disabled flag,
    /// or — for the automated translation job — when the automation switch
    /// the settings screen shares is off.
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
        return builtin.Id == "AutomatedTranslationJob" &&
               settings.GetValueOrDefault(SettingKeys.Automation.AutomationEnabled) != "true";
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
