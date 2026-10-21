using Lingarr.Core.Configuration;
using Lingarr.Server.Modules.Settings;
using Lingarr.Server.Modules.Subtitle.TimelineSync;

namespace Lingarr.Server.Modules.Subtitle.PostProcessing;

/// <summary>
/// Timeline-sync post-processing stage (B05): after a subtitle lands on
/// disk (download pipeline, via <see cref="PostProcessContext.MediaVideoPath"/>),
/// align it to the media's audio envelope when
/// <c>timeline_sync_on_download</c> is on. The stage delegates every gate
/// to <see cref="ITimelineSyncService"/> and only maps outcomes; failures
/// ride the history row as "<c>TimelineSync: {reason}</c>" (the composite's
/// containment), never as a failed landing — and never touch the file.
/// <para>
/// Off by default (<c>timeline_sync_on_download</c> seeded "false"): like
/// the variant stage, mutating a stored file never runs implicitly; the
/// manual API is always available regardless of this switch.
/// </para>
/// <para>
/// <see cref="PostProcessContext.PreviewOnly"/> short-circuits before any
/// media audio decode: batch previews sweep the library, and decoding
/// every video there is not a preview.
/// </para>
/// </summary>
public sealed class TimelineSyncStage : ISubtitlePostProcessor
{
    /// <inheritdoc />
    public string Name => "TimelineSync";

    private readonly ITimelineSyncService _syncService;
    private readonly ISettingService _settingService;

    public TimelineSyncStage(
        ITimelineSyncService syncService,
        ISettingService settingService)
    {
        _syncService = syncService;
        _settingService = settingService;
    }

    /// <inheritdoc />
    public async Task<PostProcessResult> ProcessAsync(
        string filePath,
        PostProcessContext context,
        CancellationToken cancellationToken)
    {
        var enabled = await _settingService.GetSetting(
            SettingKeys.SubtitleSearch.TimelineSyncOnDownload);
        if (!string.Equals(enabled, "true", StringComparison.OrdinalIgnoreCase))
        {
            return PostProcessResult.Unchanged("timeline sync on download is off");
        }

        if (context.PreviewOnly)
        {
            return PostProcessResult.Unchanged(
                "timeline-sync is skipped in preview mode (media audio is not decoded)");
        }

        if (string.IsNullOrWhiteSpace(context.MediaVideoPath) || !File.Exists(context.MediaVideoPath))
        {
            return PostProcessResult.Unchanged("no media video file to sync against");
        }

        if (!SubtitleTimelineRewriter.IsSupported(filePath))
        {
            return PostProcessResult.Unchanged($"unsupported format '{Path.GetExtension(filePath)}'");
        }

        var outcome = await _syncService.SyncFileAsync(filePath, context.MediaVideoPath, cancellationToken);
        return outcome.Status switch
        {
            TimelineSyncStatus.Corrected => new PostProcessResult(true, outcome.Reason),
            TimelineSyncStatus.Failed => new PostProcessResult(false, Error: outcome.Reason),
            _ => PostProcessResult.Unchanged(outcome.Reason)
        };
    }
}
