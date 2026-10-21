using Hangfire;
using Lingarr.Core.Configuration;
using Lingarr.Server.Modules.Settings;

namespace Lingarr.Server.Modules.Integration;

/// <summary>
/// Fire-and-forget Plex library refresh (B03), enqueued by the subtitle
/// download pipeline and the translation writeback whenever
/// <c>plex_refresh_enabled</c> is on. Deliberately no deduplication queue:
/// a landing storm enqueues a string of jobs and this job's own protections
/// merge them — most triggers no-op instead of stacking work.
/// <para>
/// Two layers keep a burst cheap: Hangfire's
/// <see cref="DisableConcurrentExecutionAttribute"/> serialises queue
/// workers, and the in-process guard (the EmbeddedTrackScanJob
/// <c>_scanInProgress</c> precedent) makes an overlapping trigger return
/// immediately instead of waiting out the lock — waiting is what turns one
/// slow refresh into a chain of lock timeouts. Deployment is a single
/// container, so the process-level flag covers every real overlap window.
/// </para>
/// <para>
/// Settings are re-read on execution even though the enqueue side already
/// gated on the switch: a burst enqueued right before the user saves
/// "disabled" must unwind as a fast success, not fire at the old value.
/// Unconfigured or disabled means success — the trigger did its job by
/// arriving; nothing here is worth a retry.
/// </para>
/// </summary>
public class PlexRefreshJob
{
    private readonly ISettingService _settingService;
    private readonly PlexService _plexService;
    private readonly ILogger<PlexRefreshJob> _logger;

    public PlexRefreshJob(
        ISettingService settingService,
        PlexService plexService,
        ILogger<PlexRefreshJob> logger)
    {
        _settingService = settingService;
        _plexService = plexService;
        _logger = logger;
    }

    // See the class remarks: an overlapping trigger logs and succeeds
    // without touching Plex; the refresh that is already running covers it.
    private static volatile bool _refreshInProgress;

    [DisableConcurrentExecution(timeoutInSeconds: 120)]
    [AutomaticRetry(Attempts = 2)]
    [Queue("system")]
    public Task Execute() => Execute(CancellationToken.None);

    /// <summary>Token-aware entry point (SubtitleUpgradeJob precedent).</summary>
    public async Task Execute(CancellationToken cancellationToken)
    {
        var enabled = await _settingService.GetSetting(SettingKeys.Integration.PlexRefreshEnabled);
        if (enabled != "true")
        {
            _logger.LogInformation(
                "Plex refresh is disabled; the enqueued trigger succeeds without calling Plex.");
            return;
        }

        if (_refreshInProgress)
        {
            _logger.LogInformation(
                "Plex refresh skipped: a previous trigger is still refreshing. This trigger succeeds without calling Plex.");
            return;
        }

        _refreshInProgress = true;
        try
        {
            // The service resolves the URL/token itself and logs (never
            // throws) whatever goes wrong — refreshes are best-effort.
            await _plexService.RefreshLibraryAsync(cancellationToken);
        }
        finally
        {
            _refreshInProgress = false;
        }
    }
}
