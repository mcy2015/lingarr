using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Hangfire;
using Hangfire.MemoryStorage;
using Hangfire.States;
using Lingarr.Core.Configuration;
using Lingarr.Server.Models;
using Lingarr.Server.Modules.Infrastructure;
using Lingarr.Server.Modules.Settings;
using Lingarr.Server.Modules.Statistics;
using Lingarr.Server.Modules.Translation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json;
using Moq;
using Xunit;

namespace Lingarr.Server.Tests.Services;

/// <summary>
/// Covers the schedule-screen server surface against Hangfire.MemoryStorage:
/// queue depths, the enable/disable lifecycle (including the shared
/// automation switch), user-created recurring jobs and the disabled-row
/// merge in GetRecurringJobs().
/// </summary>
public class ScheduleServiceTests : IDisposable
{
    private readonly FakeSettingService _settingService = new();
    private readonly ScheduleService _service;
    private readonly JobStorage? _previousStorage;

    public ScheduleServiceTests()
    {
        // The getter itself throws when no storage was ever installed, so
        // the restore value has to come through a tolerant probe.
        _previousStorage = TryGetCurrentStorage();
        JobStorage.Current = new MemoryStorage();

        var services = new ServiceCollection();
        services.AddSingleton<ISettingService>(_settingService);
        var provider = services.BuildServiceProvider();

        _service = new ScheduleService(
            null!,
            provider,
            NullLogger<ScheduleService>.Instance);
    }

    private static JobStorage? TryGetCurrentStorage()
    {
        try
        {
            return JobStorage.Current;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    public void Dispose()
    {
        JobStorage.Current = _previousStorage!;
        GC.SuppressFinalize(this);
    }

    // ── GetQueueDepths ──────────────────────────────────────────────

    [Fact]
    public void GetQueueDepths_EmptyStorage_ListsEveryServerQueueAtZero()
    {
        var depths = _service.GetQueueDepths();

        var names = depths.Select(depth => depth.Queue).ToArray();
        Assert.Equal(ScheduleService.ServerQueues, names);
        Assert.All(depths, depth =>
        {
            Assert.Equal(0, depth.Enqueued);
            Assert.Equal(0, depth.Processing);
        });
    }

    [Fact]
    public void GetQueueDepths_CountsEnqueuedJobsPerQueue()
    {
        // MemoryStorage routes every enqueue without a QueueAttribute into
        // one internal queue, so the assertion pins totals rather than the
        // per-queue split the SQLite storage produces in production.
        var client = new BackgroundJobClient(JobStorage.Current);
        client.Create<StatisticsJob>(job => job.Execute(), new EnqueuedState("translation"));
        client.Create<StatisticsJob>(job => job.Execute(), new EnqueuedState("system"));
        client.Create<StatisticsJob>(job => job.Execute(), new EnqueuedState("system"));

        var depths = _service.GetQueueDepths();

        Assert.Equal(3, depths.Sum(depth => depth.Enqueued));
        Assert.Equal(3, depths.Single(depth => depth.Queue == "system").Enqueued);
        Assert.Equal(0, depths.Single(depth => depth.Queue == "translation").Enqueued);
        Assert.All(depths, depth => Assert.Equal(0, depth.Processing));
    }

    [Fact]
    public void GetQueueDepths_StorageUnavailable_ReturnsEmpty()
    {
        JobStorage.Current = null!;

        Assert.Empty(_service.GetQueueDepths());
    }

    // ── SetJobEnabled: built-ins ────────────────────────────────────

    [Fact]
    public async Task SetJobEnabled_DisableThenEnable_RoundTripsBuiltIn()
    {
        Assert.True(await _service.SetJobEnabled("StatisticsJob", false));

        // persisted state keeps it off across restarts…
        var disabled = ReadDisabledSetting();
        Assert.Contains("StatisticsJob", disabled);
        // …and the registration is gone from storage
        Assert.DoesNotContain(await _service.GetRecurringJobs(), job => job.Id == "StatisticsJob" && job.Enabled);

        Assert.True(await _service.SetJobEnabled("StatisticsJob", true));

        Assert.DoesNotContain(ReadDisabledSetting(), id => id == "StatisticsJob");
        var revived = (await _service.GetRecurringJobs()).Single(job => job.Id == "StatisticsJob");
        Assert.True(revived.Enabled);
        // no setting override -> the catalog fallback (Cron.Daily) applies
        Assert.Equal(Cron.Daily(), revived.Cron);
    }

    [Fact]
    public async Task SetJobEnabled_AutomatedTranslation_SharesTheAutomationSwitch()
    {
        Assert.True(await _service.SetJobEnabled("AutomatedTranslationJob", false));
        Assert.Equal("false", _settingService.Values[SettingKeys.Automation.AutomationEnabled]);
        Assert.DoesNotContain(await _service.GetRecurringJobs(), job => job.Id == "AutomatedTranslationJob" && job.Enabled);

        _settingService.Values[SettingKeys.Automation.TranslationSchedule] = "0 5 * * *";
        Assert.True(await _service.SetJobEnabled("AutomatedTranslationJob", true));

        Assert.Equal("true", _settingService.Values[SettingKeys.Automation.AutomationEnabled]);
        var job = (await _service.GetRecurringJobs()).Single(job => job.Id == "AutomatedTranslationJob");
        Assert.True(job.Enabled);
        Assert.Equal("0 5 * * *", job.Cron);
    }

    [Fact]
    public async Task SetJobEnabled_UnknownJob_ReturnsFalse()
    {
        Assert.False(await _service.SetJobEnabled("DoesNotExist", true));
        Assert.False(await _service.SetJobEnabled("DoesNotExist", false));
    }

    // ── SetJobEnabled: subtitle-search master-switch linkage (AC7) ──

    [Fact]
    public async Task SetJobEnabled_SubtitleSearch_SharesTheMasterSwitch()
    {
        // AC7 (the broken chain): the schedule toggle used to only register
        // the job while subtitle_search_enabled (seeded false by M0008)
        // short-circuited every Execute() round. The toggle now mirrors the
        // automation-switch precedent — one source of truth, written on both
        // directions, disabled-jobs list stays out of it.
        _settingService.Values[SettingKeys.Automation.SubtitleSearchSchedule] = "15 5 * * *";

        Assert.True(await _service.SetJobEnabled("SubtitleSearchJob", true));

        Assert.Equal("true", _settingService.Values[SettingKeys.SubtitleSearch.Enabled]);
        Assert.DoesNotContain("SubtitleSearchJob", ReadDisabledSetting());
        var enabled = (await _service.GetRecurringJobs()).Single(job => job.Id == "SubtitleSearchJob");
        Assert.True(enabled.Enabled);
        Assert.Equal("15 5 * * *", enabled.Cron);

        Assert.True(await _service.SetJobEnabled("SubtitleSearchJob", false));

        Assert.Equal("false", _settingService.Values[SettingKeys.SubtitleSearch.Enabled]);
        Assert.DoesNotContain("SubtitleSearchJob", ReadDisabledSetting());
        var disabledRow = (await _service.GetRecurringJobs()).Single(job => job.Id == "SubtitleSearchJob");
        Assert.False(disabledRow.Enabled);
        // the persisted cron survives for the merged disabled row
        Assert.Equal("15 5 * * *", disabledRow.Cron);
    }

    [Fact]
    public async Task SetJobEnabled_SubtitleSearchEnable_HealsALegacyDisabledListEntry()
    {
        // Pre-linkage toggle-offs wrote the job into disabled_recurring_jobs;
        // IsBuiltinDisabled honours that list first, so an enable that left
        // the stale entry behind would stay disabled after the next
        // Initialize().
        _settingService.Values[SettingKeys.Automation.DisabledRecurringJobs] =
            JsonConvert.SerializeObject(new List<string> { "SubtitleSearchJob" });

        Assert.True(await _service.SetJobEnabled("SubtitleSearchJob", true));

        Assert.Equal("true", _settingService.Values[SettingKeys.SubtitleSearch.Enabled]);
        Assert.DoesNotContain("SubtitleSearchJob", ReadDisabledSetting());
        Assert.True((await _service.GetRecurringJobs())
            .Single(job => job.Id == "SubtitleSearchJob").Enabled);
    }

    [Fact]
    public async Task GetRecurringJobs_SubtitleSearchMasterSwitchOff_ListsJobAsDisabled()
    {
        // The M0008 seeded default (no "true" in the settings) must surface
        // the row as disabled — the honest state, since Execute() would
        // short-circuit every round of a registered job.
        _settingService.Values[SettingKeys.Automation.SubtitleSearchSchedule] = "0 5 * * *";

        var job = (await _service.GetRecurringJobs()).Single(job => job.Id == "SubtitleSearchJob");

        Assert.False(job.Enabled);
        Assert.Equal("0 5 * * *", job.Cron);
        Assert.Equal("SubtitleSearchJob", job.JobMethod);
    }

    // ── SetJobEnabled: subtitle-upgrade master-switch linkage (B02) ──

    [Fact]
    public async Task GetRecurringJobs_UpgradeSwitchOff_ListsJobAsDisabled()
    {
        // The M0014 seeded default (no "true" in the settings): the upgrade
        // round must surface as a disabled row, never as a registered job
        // whose Execute() short-circuits — the same honest state as the
        // search job's master switch.
        _settingService.Values[SettingKeys.Automation.SubtitleUpgradeSchedule] = "0 4 * * *";

        var job = (await _service.GetRecurringJobs()).Single(job => job.Id == "SubtitleUpgradeJob");

        Assert.False(job.Enabled);
        Assert.Equal("0 4 * * *", job.Cron);
        Assert.Equal("SubtitleUpgradeJob", job.JobMethod);
    }

    [Fact]
    public async Task SetJobEnabled_SubtitleUpgrade_SharesTheMasterSwitch()
    {
        // The schedule toggle is the upgrade switch's UI surface: both
        // directions write subtitle_upgrade_enabled, and the cron comes from
        // the seeded subtitle_upgrade_schedule row.
        _settingService.Values[SettingKeys.Automation.SubtitleUpgradeSchedule] = "30 4 * * *";

        Assert.True(await _service.SetJobEnabled("SubtitleUpgradeJob", true));

        Assert.Equal("true", _settingService.Values[SettingKeys.SubtitleSearch.UpgradeEnabled]);
        Assert.DoesNotContain("SubtitleUpgradeJob", ReadDisabledSetting());
        var enabled = (await _service.GetRecurringJobs()).Single(job => job.Id == "SubtitleUpgradeJob");
        Assert.True(enabled.Enabled);
        Assert.Equal("30 4 * * *", enabled.Cron);

        Assert.True(await _service.SetJobEnabled("SubtitleUpgradeJob", false));

        Assert.Equal("false", _settingService.Values[SettingKeys.SubtitleSearch.UpgradeEnabled]);
        Assert.DoesNotContain("SubtitleUpgradeJob", ReadDisabledSetting());
        var disabledRow = (await _service.GetRecurringJobs()).Single(job => job.Id == "SubtitleUpgradeJob");
        Assert.False(disabledRow.Enabled);
        // the persisted cron survives for the merged disabled row
        Assert.Equal("30 4 * * *", disabledRow.Cron);
    }

    [Fact]
    public async Task Initialize_SubtitleSearchSwitchOff_SkipsUntilToggledOn()
    {
        var translationRequests = new Mock<ITranslationRequestService>();
        translationRequests
            .Setup(service => service.ResumeTranslationRequests())
            .Returns(Task.CompletedTask);

        using var provider = BuildProvider(translationRequests.Object);
        var service = new ScheduleService(
            null!,
            provider,
            NullLogger<ScheduleService>.Instance);

        _settingService.Values[SettingKeys.Automation.MovieSchedule] = "0 4 * * *";
        _settingService.Values[SettingKeys.Automation.ShowSchedule] = "0 4 * * *";
        _settingService.Values[SettingKeys.Automation.AutomationEnabled] = "true";

        await service.Initialize();

        // master switch off (the seeded default): not registered, offered as
        // a disabled row instead of a no-op recurring job.
        Assert.False((await service.GetRecurringJobs())
            .Single(job => job.Id == "SubtitleSearchJob").Enabled);

        // flipping the toggle registers the round immediately
        Assert.True(await service.SetJobEnabled("SubtitleSearchJob", true));
        Assert.True((await service.GetRecurringJobs())
            .Single(job => job.Id == "SubtitleSearchJob").Enabled);
    }

    // ── GetRecurringJobs: disabled-row merge ────────────────────────

    [Fact]
    public async Task GetRecurringJobs_AutomationOff_ListsJobAsDisabled()
    {
        _settingService.Values[SettingKeys.Automation.AutomationEnabled] = "false";
        _settingService.Values[SettingKeys.Automation.TranslationSchedule] = "0 4 * * *";

        var job = (await _service.GetRecurringJobs()).Single(job => job.Id == "AutomatedTranslationJob");

        Assert.False(job.Enabled);
        Assert.Equal("0 4 * * *", job.Cron);
        Assert.Equal("AutomatedTranslationJob", job.JobMethod);
    }

    [Fact]
    public async Task GetRecurringJobs_DisabledFlag_ListsBuiltInWithSettingCron()
    {
        _settingService.Values[SettingKeys.Automation.DisabledRecurringJobs] =
            JsonConvert.SerializeObject(new List<string> { "SyncMovieJob" });
        _settingService.Values[SettingKeys.Automation.MovieSchedule] = "0 */2 * * *";

        var job = (await _service.GetRecurringJobs()).Single(job => job.Id == "SyncMovieJob");

        Assert.False(job.Enabled);
        Assert.Equal("0 */2 * * *", job.Cron);
    }

    // ── CreateRecurringJob ──────────────────────────────────────────

    // ── Initialize honours persisted state ──────────────────────────

    [Fact]
    public async Task Initialize_RegistersEnabledCustomJobsAndSkipsDisabledBuiltIns()
    {
        var translationRequests = new Mock<ITranslationRequestService>();
        translationRequests
            .Setup(service => service.ResumeTranslationRequests())
            .Returns(Task.CompletedTask);

        using var provider = BuildProvider(translationRequests.Object);
        var service = new ScheduleService(
            null!,
            provider,
            NullLogger<ScheduleService>.Instance);

        _settingService.Values[SettingKeys.Automation.MovieSchedule] = "0 4 * * *";
        _settingService.Values[SettingKeys.Automation.ShowSchedule] = "0 4 * * *";
        _settingService.Values[SettingKeys.Automation.AutomationEnabled] = "false";
        _settingService.Values[SettingKeys.Automation.DisabledRecurringJobs] =
            JsonConvert.SerializeObject(new List<string> { "StatisticsJob" });
        _settingService.Values[SettingKeys.Automation.CustomRecurringJobs] = JsonConvert.SerializeObject(
            new List<CustomRecurringJob>
            {
                new() { JobId = "nightly-scan", JobType = "StatisticsJob", Cron = "0 5 * * *", Enabled = true },
                new() { JobId = "weekly-wipe", JobType = "CleanupJob", Cron = "0 2 * * 0", Enabled = false }
            });

        await service.Initialize();

        var jobs = await service.GetRecurringJobs();

        // enabled custom job registered, disabled one only listed
        Assert.True(jobs.Single(job => job.Id == "nightly-scan").Enabled);
        Assert.False(jobs.Single(job => job.Id == "weekly-wipe").Enabled);
        // disabled built-in skipped, automation-off built-in only listed
        Assert.False(jobs.Single(job => job.Id == "StatisticsJob").Enabled);
        Assert.False(jobs.Single(job => job.Id == "AutomatedTranslationJob").Enabled);
        Assert.True(jobs.Single(job => job.Id == "CleanupJob").Enabled);
        Assert.True(jobs.Single(job => job.Id == "SyncMovieJob").Enabled);
    }

    // ── UpdateJobCron: the single cron edit entry ─────────────────

    [Fact]
    public async Task UpdateJobCron_Builtin_PersistsSettingAndReRegisters()
    {
        _settingService.Values[SettingKeys.Automation.MovieSchedule] = "0 4 * * *";

        Assert.Equal(UpdateCronResult.Updated, await _service.UpdateJobCron("SyncMovieJob", " 0 5 * * * "));

        Assert.Equal("0 5 * * *", _settingService.Values[SettingKeys.Automation.MovieSchedule]);
        var job = (await _service.GetRecurringJobs()).Single(job => job.Id == "SyncMovieJob");
        Assert.True(job.Enabled);
        Assert.Equal("0 5 * * *", job.Cron);
    }

    [Fact]
    public async Task UpdateJobCron_BuiltinWithAutomationOff_PersistsWithoutRegistering()
    {
        _settingService.Values[SettingKeys.Automation.AutomationEnabled] = "false";
        _settingService.Values[SettingKeys.Automation.TranslationSchedule] = "0 4 * * *";

        Assert.Equal(UpdateCronResult.Updated,
            await _service.UpdateJobCron("AutomatedTranslationJob", "0 6 * * *"));

        Assert.Equal("0 6 * * *", _settingService.Values[SettingKeys.Automation.TranslationSchedule]);
        var job = (await _service.GetRecurringJobs()).Single(job => job.Id == "AutomatedTranslationJob");
        // disabled merge row still shows the persisted cron, ready to enable
        Assert.False(job.Enabled);
        Assert.Equal("0 6 * * *", job.Cron);
    }

    [Fact]
    public async Task UpdateJobCron_CustomJob_UpdatesPersistedCronWhileEnabled()
    {
        _settingService.Values[SettingKeys.Automation.CustomRecurringJobs] = JsonConvert.SerializeObject(
            new List<CustomRecurringJob>
            {
                new() { JobId = "nightly-scan", JobType = "StatisticsJob", Cron = "0 5 * * *", Enabled = true }
            });

        Assert.Equal(UpdateCronResult.Updated, await _service.UpdateJobCron("nightly-scan", "30 5 * * *"));

        Assert.Equal("30 5 * * *", ReadCustomJobsSetting().Single().Cron);
        var job = (await _service.GetRecurringJobs()).Single(job => job.Id == "nightly-scan");
        Assert.True(job.Enabled);
        Assert.Equal("30 5 * * *", job.Cron);
    }

    [Fact]
    public async Task UpdateJobCron_CustomJobDisabled_PersistsOnly()
    {
        _settingService.Values[SettingKeys.Automation.CustomRecurringJobs] = JsonConvert.SerializeObject(
            new List<CustomRecurringJob>
            {
                new() { JobId = "nightly-scan", JobType = "StatisticsJob", Cron = "0 5 * * *", Enabled = false }
            });

        Assert.Equal(UpdateCronResult.Updated, await _service.UpdateJobCron("nightly-scan", "15 5 * * *"));

        var job = (await _service.GetRecurringJobs()).Single(job => job.Id == "nightly-scan");
        Assert.False(job.Enabled);
        Assert.Equal("15 5 * * *", job.Cron);
    }

    [Fact]
    public async Task UpdateJobCron_InvalidCron_ReturnsInvalidCron()
    {
        Assert.Equal(UpdateCronResult.InvalidCron, await _service.UpdateJobCron("SyncMovieJob", "77 * * * *"));
        Assert.Equal(UpdateCronResult.InvalidCron, await _service.UpdateJobCron("SyncMovieJob", "  "));
    }

    [Fact]
    public async Task UpdateJobCron_UnknownJob_ReturnsNotFound()
    {
        Assert.Equal(UpdateCronResult.NotFound, await _service.UpdateJobCron("DoesNotExist", "0 5 * * *"));
    }

    [Fact]
    public async Task UpdateJobCron_MissingSettingRow_ReturnsSettingRowMissing()
    {
        _settingService.MissingKeys.Add(SettingKeys.Automation.MovieSchedule);

        Assert.Equal(UpdateCronResult.SettingRowMissing,
            await _service.UpdateJobCron("SyncMovieJob", "0 5 * * *"));
    }

    // ── Cleanup/Statistics honour their settings keys ──────────────

    [Fact]
    public async Task SetJobEnabled_StatisticsEnable_UsesConfiguredScheduleSetting()
    {
        _settingService.Values[SettingKeys.Automation.StatisticsSchedule] = "30 1 * * *";

        Assert.True(await _service.SetJobEnabled("StatisticsJob", true));

        var job = (await _service.GetRecurringJobs()).Single(job => job.Id == "StatisticsJob");
        Assert.True(job.Enabled);
        Assert.Equal("30 1 * * *", job.Cron);
    }

    [Fact]
    public async Task Initialize_UsesConfiguredCleanupAndStatisticsCron()
    {
        var translationRequests = new Mock<ITranslationRequestService>();
        translationRequests
            .Setup(service => service.ResumeTranslationRequests())
            .Returns(Task.CompletedTask);

        using var provider = BuildProvider(translationRequests.Object);
        var service = new ScheduleService(
            null!,
            provider,
            NullLogger<ScheduleService>.Instance);

        _settingService.Values[SettingKeys.Automation.MovieSchedule] = "0 4 * * *";
        _settingService.Values[SettingKeys.Automation.ShowSchedule] = "0 4 * * *";
        _settingService.Values[SettingKeys.Automation.AutomationEnabled] = "true";
        _settingService.Values[SettingKeys.Automation.TranslationSchedule] = "0 4 * * *";
        _settingService.Values[SettingKeys.Automation.CleanupSchedule] = "0 3 * * 1";
        _settingService.Values[SettingKeys.Automation.StatisticsSchedule] = "0 2 * * *";

        await service.Initialize();

        var jobs = await service.GetRecurringJobs();
        Assert.Equal("0 3 * * 1", jobs.Single(job => job.Id == "CleanupJob").Cron);
        Assert.Equal("0 2 * * *", jobs.Single(job => job.Id == "StatisticsJob").Cron);
        Assert.True(jobs.Single(job => job.Id == "AutomatedTranslationJob").Enabled);
    }

    // ── legacy custom_recurring_jobs stay runnable (frozen chain) ──

    [Fact]
    public async Task LegacyCustomJob_CanBeToggledAndEditedButNotCreated()
    {
        // creation is gone; a pre-existing definition keeps working end to end
        _settingService.Values[SettingKeys.Automation.CustomRecurringJobs] = JsonConvert.SerializeObject(
            new List<CustomRecurringJob>
            {
                new() { JobId = "weekly-wipe", JobType = "CleanupJob", Cron = "0 2 * * 0", Enabled = true }
            });

        Assert.True(await _service.SetJobEnabled("weekly-wipe", false));
        var disabledRow = (await _service.GetRecurringJobs()).Single(job => job.Id == "weekly-wipe");
        Assert.False(disabledRow.Enabled);

        Assert.True(await _service.SetJobEnabled("weekly-wipe", true));
        var enabledRow = (await _service.GetRecurringJobs()).Single(job => job.Id == "weekly-wipe");
        Assert.True(enabledRow.Enabled);
        Assert.Equal("0 2 * * 0", enabledRow.Cron);

        Assert.Equal(UpdateCronResult.Updated, await _service.UpdateJobCron("weekly-wipe", "30 2 * * 0"));
        var edited = (await _service.GetRecurringJobs()).Single(job => job.Id == "weekly-wipe");
        Assert.Equal("30 2 * * 0", edited.Cron);
    }

    // ── helpers ─────────────────────────────────────────────────────

    private ServiceProvider BuildProvider(ITranslationRequestService? translationRequests = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ISettingService>(_settingService);
        if (translationRequests != null)
        {
            services.AddSingleton(translationRequests);
        }
        return services.BuildServiceProvider();
    }

    private List<string> ReadDisabledSetting()
    {
        var json = _settingService.Values.GetValueOrDefault(SettingKeys.Automation.DisabledRecurringJobs);
        return string.IsNullOrEmpty(json)
            ? []
            : JsonConvert.DeserializeObject<List<string>>(json) ?? [];
    }

    private List<CustomRecurringJob> ReadCustomJobsSetting()
    {
        var json = _settingService.Values.GetValueOrDefault(SettingKeys.Automation.CustomRecurringJobs);
        return string.IsNullOrEmpty(json)
            ? []
            : JsonConvert.DeserializeObject<List<CustomRecurringJob>>(json) ?? [];
    }

    /// <summary>
    /// Dictionary-backed ISettingService: writes land in Values so tests can
    /// assert exactly what the schedule endpoints persisted. Keys listed in
    /// MissingKeys behave like absent settings rows — SetSetting returns
    /// false, mirroring the production guard.
    /// </summary>
    private sealed class FakeSettingService : ISettingService
    {
        public Dictionary<string, string> Values { get; } = new();

        public HashSet<string> MissingKeys { get; } = new();

        public event SettingChangedHandler? SettingChanged
        {
            add { }
            remove { }
        }

        public Task<string?> GetSetting(string key) =>
            Task.FromResult<string?>(MissingKeys.Contains(key) ? null : Values.GetValueOrDefault(key));

        public Task<Dictionary<string, string>> GetSettings(IEnumerable<string> keys) =>
            Task.FromResult(keys
                .Where(key => !MissingKeys.Contains(key))
                .ToDictionary(key => key, key => Values.GetValueOrDefault(key) ?? string.Empty));

        public Task<bool> SetSetting(string key, string value)
        {
            if (MissingKeys.Contains(key))
            {
                return Task.FromResult(false);
            }
            Values[key] = value;
            return Task.FromResult(true);
        }

        public Task<bool> SetSettings(Dictionary<string, string> settings)
        {
            foreach (var (key, value) in settings)
            {
                Values[key] = value;
            }
            return Task.FromResult(true);
        }

        public Task<List<T>> GetSettingAsJson<T>(string key) where T : class =>
            Task.FromResult(new List<T>());

        public Task<bool> SetEncryptedSetting(string key, string value) => Task.FromResult(true);

        public Task<string?> GetEncryptedSetting(string key) => Task.FromResult<string?>(null);

        public Task<Dictionary<string, bool>> GetEncryptedSettingStates(IEnumerable<string> keys) =>
            Task.FromResult(new Dictionary<string, bool>());
    }
}
