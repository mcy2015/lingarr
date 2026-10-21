using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Lingarr.Core.Configuration;
using Lingarr.Server.Exceptions;
using Lingarr.Server.Modules.Settings;
using Lingarr.Server.Modules.Subtitle.PostProcessing;
using Lingarr.Server.Modules.Subtitle.TimelineSync;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Lingarr.Server.Tests.Modules.Subtitle.PostProcessing;

/// <summary>
/// <see cref="TimelineSyncStage"/> contracts (B05, mounted on the B01
/// framework): the on-download switch gates everything, preview mode
/// never decodes media audio, missing video paths skip, outcome mapping
/// rides the history row, and an ffmpeg-unavailable failure is contained
/// by the composite (AC3 — the subscriber never crashes, the file stays).
/// </summary>
public sealed class TimelineSyncStageTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "lingarr-timeline-stage-" + Guid.NewGuid().ToString("N"));

    private readonly Mock<ITimelineSyncService> _sync = new();
    private readonly Mock<ISettingService> _settings = new();

    public TimelineSyncStageTests()
    {
        Directory.CreateDirectory(_directory);
    }

    private TimelineSyncStage StageEnabled(bool on, out string videoPath, string subtitlePath = "movie.srt")
    {
        _settings
            .Setup(service => service.GetSetting(SettingKeys.SubtitleSearch.TimelineSyncOnDownload))
            .ReturnsAsync(on ? "true" : "false");
        videoPath = Path.Combine(_directory, "movie.2023.mkv");
        File.WriteAllText(videoPath, "video");
        File.WriteAllText(Path.Combine(_directory, subtitlePath), "1\n00:00:10,000 --> 00:00:12,000\nhello\n");
        return new TimelineSyncStage(_sync.Object, _settings.Object);
    }

    [Fact]
    public async Task SwitchOff_IsANoOpWithoutServiceCall()
    {
        var stage = StageEnabled(on: false, out _);

        var result = await stage.ProcessAsync(
            Path.Combine(_directory, "movie.srt"),
            new PostProcessContext(MediaVideoPath: Path.Combine(_directory, "movie.2023.mkv")),
            CancellationToken.None);

        Assert.False(result.Changed);
        Assert.NotNull(result.Reason);
        _sync.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task PreviewOnly_SkipsWithoutDecoding()
    {
        var stage = StageEnabled(on: true, out _);
        var video = Path.Combine(_directory, "movie.2023.mkv");

        var result = await stage.ProcessAsync(
            Path.Combine(_directory, "movie.srt"),
            new PostProcessContext(PreviewOnly: true, MediaVideoPath: video),
            CancellationToken.None);

        Assert.False(result.Changed);
        Assert.Contains("preview", result.Reason);
        _sync.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task MissingVideoPath_Skips()
    {
        var stage = StageEnabled(on: true, out _);

        var result = await stage.ProcessAsync(
            Path.Combine(_directory, "movie.srt"),
            new PostProcessContext(),
            CancellationToken.None);

        Assert.False(result.Changed);
        Assert.Contains("no media video file", result.Reason);
        _sync.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task VideoFileMissingOnDisk_Skips()
    {
        _settings
            .Setup(service => service.GetSetting(SettingKeys.SubtitleSearch.TimelineSyncOnDownload))
            .ReturnsAsync("true");
        var stage = new TimelineSyncStage(_sync.Object, _settings.Object);
        var video = Path.Combine(_directory, "absent.mkv");

        var result = await stage.ProcessAsync(
            Path.Combine(_directory, "movie.srt"),
            new PostProcessContext(MediaVideoPath: video),
            CancellationToken.None);

        Assert.False(result.Changed);
        _sync.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task UnsupportedFormat_SkipsBeforeService()
    {
        _settings
            .Setup(service => service.GetSetting(SettingKeys.SubtitleSearch.TimelineSyncOnDownload))
            .ReturnsAsync("true");
        var stage = new TimelineSyncStage(_sync.Object, _settings.Object);
        var video = Path.Combine(_directory, "movie.2023.mkv");
        File.WriteAllText(video, "video");

        var result = await stage.ProcessAsync(
            Path.Combine(_directory, "movie.sub"),
            new PostProcessContext(MediaVideoPath: video),
            CancellationToken.None);

        Assert.False(result.Changed);
        Assert.Contains("unsupported format", result.Reason);
        _sync.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task CorrectedOutcome_ReportsChanged()
    {
        var stage = StageEnabled(on: true, out var video);
        var subtitle = Path.Combine(_directory, "movie.srt");
        _sync.Setup(service => service.SyncFileAsync(subtitle, video, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TimelineSyncOutcome(TimelineSyncStatus.Corrected, "corrected 12 cues", 1.0, -2.5, 0.88, 12));

        var result = await stage.ProcessAsync(
            subtitle, new PostProcessContext(MediaVideoPath: video), CancellationToken.None);

        Assert.True(result.Changed);
        Assert.Equal("corrected 12 cues", result.Reason);
    }

    [Fact]
    public async Task FailedOutcome_RidesTheHistoryRowAsError()
    {
        var stage = StageEnabled(on: true, out var video);
        var subtitle = Path.Combine(_directory, "movie.srt");
        _sync.Setup(service => service.SyncFileAsync(subtitle, video, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TimelineSyncOutcome(TimelineSyncStatus.Failed, "correlation score 0.030 is below...", 1.0, 0, 0.03, 12));

        var result = await stage.ProcessAsync(
            subtitle, new PostProcessContext(MediaVideoPath: video), CancellationToken.None);

        Assert.False(result.Changed);
        Assert.Null(result.Reason);
        Assert.Equal("correlation score 0.030 is below...", result.Error);
    }

    [Fact]
    public async Task NoCorrectionOutcome_IsAnUnchangedSkip()
    {
        var stage = StageEnabled(on: true, out var video);
        var subtitle = Path.Combine(_directory, "movie.srt");
        _sync.Setup(service => service.SyncFileAsync(subtitle, video, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TimelineSyncOutcome(TimelineSyncStatus.NoCorrectionNeeded, "already aligned within 200ms."));

        var result = await stage.ProcessAsync(
            subtitle, new PostProcessContext(MediaVideoPath: video), CancellationToken.None);

        Assert.False(result.Changed);
        Assert.Null(result.Error);
    }

    [Fact]
    public async Task CompositeContainsFfmpegUnavailable_StageDoesNotCrashTheLanding()
    {
        var stage = StageEnabled(on: true, out var video);
        var subtitle = Path.Combine(_directory, "movie.srt");
        _sync.Setup(service => service.SyncFileAsync(subtitle, video, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new FFprobeUnavailableException("ffmpeg was not found on PATH."));

        var composite = new CompositePostProcessor(
            [stage], NullLogger<CompositePostProcessor>.Instance);

        var result = await composite.ProcessAsync(
            subtitle, new PostProcessContext(MediaVideoPath: video), CancellationToken.None);

        Assert.False(result.Changed);
        Assert.NotNull(result.Error);
        Assert.StartsWith("TimelineSync: ", result.Error);
        Assert.Contains("not found on PATH", result.Error);
        // The landed subtitle is still on disk, untouched.
        Assert.True(File.Exists(subtitle));
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
