using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Lingarr.Core.Data;
using Lingarr.Core.Entities;
using Lingarr.Server.Controllers;
using Lingarr.Server.Exceptions;
using Lingarr.Server.Modules.Subtitle.TimelineSync;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;

namespace Lingarr.Server.Tests.Controllers;

/// <summary>
/// <see cref="TimelineSyncController"/> contracts (B05): the manual sync
/// and restore endpoints validate media ownership (the subtitle must live
/// in the media's directory and derive from the video's base name) before
/// the service runs, map gate outcomes to 200-with-status and environment
/// failures to 400, and restore works without the video file on disk.
/// </summary>
public sealed class TimelineSyncControllerTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "lingarr-timeline-controller-" + Guid.NewGuid().ToString("N"));

    private readonly Mock<ITimelineSyncService> _sync = new();

    public TimelineSyncControllerTests()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Path.Combine(_directory, "Movie.2023.mkv"), "video");
        File.WriteAllText(Path.Combine(_directory, "Movie.2023.zh.srt"),
            "1\n00:00:10,000 --> 00:00:12,000\nhello\n");
    }

    private TimelineSyncController CreateController()
    {
        var options = new DbContextOptionsBuilder<LingarrDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var dbContext = new LingarrDbContext(options);
        dbContext.Movies.Add(new Movie
        {
            RadarrId = 1,
            Title = "Movie",
            FileName = "Movie.2023.mkv",
            Path = _directory,
            DateAdded = DateTimeOffset.UtcNow
        });
        dbContext.SaveChanges();
        return new TimelineSyncController(dbContext, _sync.Object);
    }

    private TimelineSyncController CreateEpisodeController()
    {
        var options = new DbContextOptionsBuilder<LingarrDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var dbContext = new LingarrDbContext(options);
        var show = new Show
        {
            SonarrId = 1,
            Title = "Show",
            Path = _directory,
            DateAdded = DateTimeOffset.UtcNow
        };
        var season = new Season { SeasonNumber = 1, Show = show };
        dbContext.Episodes.Add(new Episode
        {
            SonarrId = 1,
            EpisodeNumber = 1,
            Title = "Episode",
            FileName = "Movie.2023.mkv", // reuse the on-disk fixtures
            Path = _directory,
            Season = season,
            DateAdded = DateTimeOffset.UtcNow
        });
        dbContext.SaveChanges();
        return new TimelineSyncController(dbContext, _sync.Object);
    }

    private static TimelineSyncRequestBody Body(
        string mediaType = "Movie", int mediaId = 1, string? subtitlePath = null) =>
        new(mediaType, mediaId,
            subtitlePath ?? Path.Combine(Path.GetTempPath(), "placeholder"));

    [Fact]
    public async Task Sync_ValidRequest_PassesOwnershipAndCallsService()
    {
        var controller = CreateController();
        var subtitle = Path.Combine(_directory, "Movie.2023.zh.srt");
        var video = Path.Combine(_directory, "Movie.2023.mkv");
        _sync.Setup(service => service.SyncFileAsync(subtitle, video, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TimelineSyncOutcome(TimelineSyncStatus.Corrected, "corrected", 1.0, -2.5, 0.88, 12));

        var response = await controller.Sync(
            Body(subtitlePath: subtitle), CancellationToken.None);

        var result = Assert.IsType<OkObjectResult>(response.Result);
        var body = Assert.IsType<TimelineSyncResponse>(result.Value);
        Assert.Equal(TimelineSyncStatus.Corrected, body.Status);
        _sync.Verify(service => service.SyncFileAsync(subtitle, video, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Sync_EpisodeMediaType_ResolvesTheEpisodeVideo()
    {
        var controller = CreateEpisodeController();
        var subtitle = Path.Combine(_directory, "Movie.2023.zh.srt");
        var video = Path.Combine(_directory, "Movie.2023.mkv");
        _sync.Setup(service => service.SyncFileAsync(subtitle, video, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TimelineSyncOutcome(TimelineSyncStatus.NoCorrectionNeeded));

        var response = await controller.Sync(
            Body(mediaType: "Episode", subtitlePath: subtitle), CancellationToken.None);

        Assert.IsType<OkObjectResult>(response.Result);
        _sync.Verify(service => service.SyncFileAsync(subtitle, video, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Sync_SubtitleOutsideTheMediaDirectory_Returns400()
    {
        var controller = CreateController();
        var foreign = Path.Combine(Path.GetTempPath(), "elsewhere-" + Guid.NewGuid().ToString("N"), "Movie.2023.zh.srt");
        Directory.CreateDirectory(Path.GetDirectoryName(foreign)!);
        File.WriteAllText(foreign, "x");

        var response = await controller.Sync(Body(subtitlePath: foreign), CancellationToken.None);

        var result = Assert.IsType<BadRequestObjectResult>(response.Result);
        Assert.Contains("does not belong", result.Value!.ToString());
        _sync.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Sync_SubtitleNotDerivedFromTheVideoName_Returns400()
    {
        var controller = CreateController();
        var foreign = Path.Combine(_directory, "Other.Show.zh.srt");
        File.WriteAllText(foreign, "x");

        var response = await controller.Sync(Body(subtitlePath: foreign), CancellationToken.None);

        var result = Assert.IsType<BadRequestObjectResult>(response.Result);
        Assert.Contains("video file name", result.Value!.ToString());
        _sync.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Sync_PathTraversalOutsideTheRoot_Returns400()
    {
        var controller = CreateController();
        var escape = Path.Combine(_directory, "..", "escape.srt");

        var response = await controller.Sync(Body(subtitlePath: escape), CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(response.Result);
        _sync.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Sync_UnknownMedia_Returns400()
    {
        var controller = CreateController();

        var response = await controller.Sync(
            Body(mediaId: 99, subtitlePath: Path.Combine(_directory, "Movie.2023.zh.srt")),
            CancellationToken.None);

        var result = Assert.IsType<BadRequestObjectResult>(response.Result);
        Assert.Contains("does not exist", result.Value!.ToString());
    }

    [Fact]
    public async Task Sync_InvalidMediaType_Returns400()
    {
        var controller = CreateController();

        var response = await controller.Sync(
            Body(mediaType: "Season", subtitlePath: Path.Combine(_directory, "Movie.2023.zh.srt")),
            CancellationToken.None);

        var result = Assert.IsType<BadRequestObjectResult>(response.Result);
        Assert.Contains("mediaType", result.Value!.ToString());
    }

    [Fact]
    public async Task Sync_VideoFileMissingOnDisk_Returns400()
    {
        var controller = CreateController();
        File.Delete(Path.Combine(_directory, "Movie.2023.mkv"));

        var response = await controller.Sync(
            Body(subtitlePath: Path.Combine(_directory, "Movie.2023.zh.srt")), CancellationToken.None);

        var result = Assert.IsType<BadRequestObjectResult>(response.Result);
        Assert.Contains("video file", result.Value!.ToString());
    }

    [Fact]
    public async Task Sync_SubtitleMissingOnDisk_Returns400()
    {
        var controller = CreateController();
        File.Delete(Path.Combine(_directory, "Movie.2023.zh.srt"));

        var response = await controller.Sync(
            Body(subtitlePath: Path.Combine(_directory, "Movie.2023.zh.srt")), CancellationToken.None);

        var result = Assert.IsType<BadRequestObjectResult>(response.Result);
        Assert.Contains("was not found on the server", result.Value!.ToString());
    }

    [Fact]
    public async Task Sync_GateFailure_Returns200WithFailedStatus()
    {
        var controller = CreateController();
        var subtitle = Path.Combine(_directory, "Movie.2023.zh.srt");
        _sync.Setup(service => service.SyncFileAsync(subtitle, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TimelineSyncOutcome(
                TimelineSyncStatus.Failed, "correlation score 0.031 is below the configured minimum 0.10", Cues: 12));

        var response = await controller.Sync(Body(subtitlePath: subtitle), CancellationToken.None);

        var result = Assert.IsType<OkObjectResult>(response.Result);
        var body = Assert.IsType<TimelineSyncResponse>(result.Value);
        Assert.Equal(TimelineSyncStatus.Failed, body.Status);
        Assert.Contains("below the configured minimum", body.Reason);
    }

    [Fact]
    public async Task Sync_FfmpegUnavailable_Returns400WithHint()
    {
        var controller = CreateController();
        var subtitle = Path.Combine(_directory, "Movie.2023.zh.srt");
        _sync.Setup(service => service.SyncFileAsync(subtitle, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new FFprobeUnavailableException("ffmpeg was not found on PATH. Install ffmpeg…"));

        var response = await controller.Sync(Body(subtitlePath: subtitle), CancellationToken.None);

        var result = Assert.IsType<BadRequestObjectResult>(response.Result);
        Assert.Contains("ffmpeg", result.Value!.ToString());
    }

    [Fact]
    public async Task Restore_WithBackup_Returns200AndCallsRestore()
    {
        var controller = CreateController();
        var subtitle = Path.Combine(_directory, "Movie.2023.zh.srt");

        var response = await controller.Restore(Body(subtitlePath: subtitle), CancellationToken.None);

        var result = Assert.IsType<OkObjectResult>(response.Result);
        var body = Assert.IsType<TimelineSyncRestoreResponse>(result.Value);
        Assert.Equal(subtitle, body.SubtitlePath);
        _sync.Verify(service => service.RestoreFileAsync(subtitle, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Restore_WithoutBackup_Returns400()
    {
        var controller = CreateController();
        var subtitle = Path.Combine(_directory, "Movie.2023.zh.srt");
        _sync.Setup(service => service.RestoreFileAsync(subtitle, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("no timeline-sync backup exists…"));

        var response = await controller.Restore(Body(subtitlePath: subtitle), CancellationToken.None);

        var result = Assert.IsType<BadRequestObjectResult>(response.Result);
        Assert.Contains("backup", result.Value!.ToString());
    }

    [Fact]
    public async Task Restore_DoesNotRequireTheVideoFile()
    {
        var options = new DbContextOptionsBuilder<LingarrDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var dbContext = new LingarrDbContext(options);
        var movie = new Movie
        {
            RadarrId = 2,
            Title = "NoVideo",
            FileName = "Gone.2020.mkv", // never created on disk
            Path = _directory,
            DateAdded = DateTimeOffset.UtcNow
        };
        dbContext.Movies.Add(movie);
        dbContext.SaveChanges();
        var controller = new TimelineSyncController(dbContext, _sync.Object);
        var ownedSubtitle = Path.Combine(_directory, "Gone.2020.zh.srt");
        File.WriteAllText(ownedSubtitle, "1\n00:00:01,000 --> 00:00:02,000\nx\n");

        var response = await controller.Restore(
            Body(mediaId: movie.Id, subtitlePath: ownedSubtitle), CancellationToken.None);

        Assert.IsType<OkObjectResult>(response.Result);
        _sync.Verify(service => service.RestoreFileAsync(ownedSubtitle, It.IsAny<CancellationToken>()), Times.Once);
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
