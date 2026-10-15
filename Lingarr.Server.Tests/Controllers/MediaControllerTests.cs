using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Lingarr.Core.Data;
using Lingarr.Core.Entities;
using Lingarr.Core.Enum;
using Lingarr.Server.Controllers;
using Lingarr.Server.Models;
using Lingarr.Server.Models.Api;
using Lingarr.Server.Models.FileSystem;
using Lingarr.Server.Modules.Media;
using Lingarr.Server.Modules.Subtitle;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Lingarr.Server.Tests.Controllers;

/// <summary>
/// Embedded-section contract of GET api/media/{mediaType}/{id}/subtitles:
/// the files list keeps its shape while embedded tracks and the
/// embeddedScanned flag ride along — empty+false before the first probe,
/// empty+true for a probed container with no tracks, rows+true after a scan.
/// </summary>
public class MediaControllerTests
{
    private readonly Mock<ISubtitleService> _subtitleService = new();
    private readonly LingarrDbContext _dbContext;

    public MediaControllerTests()
    {
        var options = new DbContextOptionsBuilder<LingarrDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _dbContext = new LingarrDbContext(options);
    }

    private MediaController CreateController(Mock<IMediaLibraryService>? library = null) => new(
        new Mock<IMediaService>().Object,
        (library ?? new Mock<IMediaLibraryService>()).Object,
        _subtitleService.Object,
        _dbContext);

    private static Movie NewMovie(int id, string path, string fileName) => new()
    {
        Id = id,
        RadarrId = id,
        Title = $"movie {id}",
        Path = path,
        FileName = fileName,
        DateAdded = DateTimeOffset.UtcNow
    };

    private static (Show show, Season season, Episode ep1, Episode ep2) NewShow(int showId)
    {
        var show = new Show
        {
            Id = showId,
            SonarrId = showId,
            Title = $"show {showId}",
            Path = $"/tv/show{showId}",
            DateAdded = DateTimeOffset.UtcNow
        };
        var season = new Season
        {
            Id = showId * 100,
            SeasonNumber = 1,
            Path = $"/tv/show{showId}/s01",
            ShowId = show.Id,
            Show = show
        };
        var ep1 = new Episode
        {
            Id = showId * 100 + 1,
            SonarrId = 1,
            EpisodeNumber = 1,
            Title = "ep1",
            Path = season.Path,
            FileName = "show.s01e01.mkv",
            SeasonId = season.Id,
            Season = season
        };
        var ep2 = new Episode
        {
            Id = showId * 100 + 2,
            SonarrId = 2,
            EpisodeNumber = 2,
            Title = "ep2",
            Path = season.Path,
            FileName = "show.s01e02.mkv",
            SeasonId = season.Id,
            Season = season
        };
        season.Episodes = [ep1, ep2];
        show.Seasons = [season];
        return (show, season, ep1, ep2);
    }

    private static MediaSubtitleTrack Track(
        MediaType mediaType,
        int mediaId,
        int streamIndex,
        string codec,
        bool isTextBased,
        string? language = null,
        string? rawLanguage = null,
        string? mediaHash = null) => new()
    {
        MediaType = mediaType,
        MediaId = mediaId,
        StreamIndex = streamIndex,
        Language = language,
        RawLanguage = rawLanguage,
        Title = null,
        Codec = codec,
        IsTextBased = isTextBased,
        FilePath = "/media/file.mkv",
        MediaHash = mediaHash
    };

    // ── movie: files + embedded in one payload ─────────────────────

    [Fact]
    public async Task GetMediaSubtitles_Movie_CarriesFilesAndEmbeddedTracks()
    {
        var movie = NewMovie(1, "/movies/dune", "dune.mkv");
        _dbContext.Movies.Add(movie);
        _dbContext.MediaSubtitleTracks.AddRange(
            Track(MediaType.Movie, 1, 2, "srt", true, "zh", "chi", "key"),
            Track(MediaType.Movie, 1, 3, "pgs", false, "zh", "zho", "key"),
            Track(MediaType.Movie, 1, MediaSubtitleTrack.ScanMarkerStreamIndex, "", false, mediaHash: "key"));
        await _dbContext.SaveChangesAsync();
        _subtitleService
            .Setup(service => service.GetAllSubtitles("/movies/dune"))
            .ReturnsAsync([new Subtitles
            {
                Path = "/movies/dune/dune.en.srt",
                FileName = "dune.en.srt",
                Language = "en",
                Languages = ["en"],
                Format = ".srt"
            }]);

        var action = await CreateController().GetMediaSubtitles(MediaType.Movie, 1);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var payload = Assert.IsType<MediaSubtitlesResponse>(ok.Value!);
        var file = Assert.Single(payload.Files);
        Assert.Equal("dune.en.srt", file.FileName);
        Assert.Equal(["en"], file.Languages);

        Assert.Equal(2, payload.Embedded.Count);
        Assert.Equal(2, payload.Embedded[0].StreamIndex);
        Assert.Equal("zh", payload.Embedded[0].Language);
        Assert.Equal("chi", payload.Embedded[0].RawLanguage);
        Assert.Equal("srt", payload.Embedded[0].Codec);
        Assert.True(payload.Embedded[0].IsTextBased);
        Assert.Equal("pgs", payload.Embedded[1].Codec);
        Assert.False(payload.Embedded[1].IsTextBased);
        Assert.True(payload.EmbeddedScanned);
    }

    [Fact]
    public async Task GetMediaSubtitles_MovieNeverScanned_EmbeddedEmptyAndFlagFalse()
    {
        _dbContext.Movies.Add(NewMovie(2, "/movies/none", "none.mkv"));
        await _dbContext.SaveChangesAsync();
        _subtitleService
            .Setup(service => service.GetAllSubtitles(It.IsAny<string>()))
            .ReturnsAsync([]);

        var action = await CreateController().GetMediaSubtitles(MediaType.Movie, 2);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var payload = Assert.IsType<MediaSubtitlesResponse>(ok.Value!);
        Assert.Empty(payload.Files);
        Assert.Empty(payload.Embedded);
        Assert.False(payload.EmbeddedScanned);
    }

    [Fact]
    public async Task GetMediaSubtitles_MovieScannedWithoutTracks_EmbeddedEmptyButFlagTrue()
    {
        // The scan-marker row alone means "probed, zero tracks".
        _dbContext.Movies.Add(NewMovie(3, "/movies/clean", "clean.mkv"));
        _dbContext.MediaSubtitleTracks.Add(
            Track(MediaType.Movie, 3, MediaSubtitleTrack.ScanMarkerStreamIndex, "", false, mediaHash: "key"));
        await _dbContext.SaveChangesAsync();
        _subtitleService
            .Setup(service => service.GetAllSubtitles(It.IsAny<string>()))
            .ReturnsAsync([]);

        var action = await CreateController().GetMediaSubtitles(MediaType.Movie, 3);

        var payload = Assert.IsType<MediaSubtitlesResponse>(Assert.IsType<OkObjectResult>(action.Result).Value!);
        Assert.Empty(payload.Embedded);
        Assert.True(payload.EmbeddedScanned);
    }

    // ── show: aggregation over episode rows ────────────────────────

    [Fact]
    public async Task GetMediaSubtitles_Show_AggregatesEpisodeTracksAndStaysUnscannedUntilEveryEpisodeProbed()
    {
        var (show, _, ep1, ep2) = NewShow(5);
        _dbContext.Shows.Add(show);
        // both episodes scanned: text track on ep1, graphic on ep2
        _dbContext.MediaSubtitleTracks.AddRange(
            Track(MediaType.Episode, ep1.Id, 2, "ass", true, "zh", "chi", "key1"),
            Track(MediaType.Episode, ep1.Id, MediaSubtitleTrack.ScanMarkerStreamIndex, "", false, mediaHash: "key1"),
            Track(MediaType.Episode, ep2.Id, 4, "pgs", false, "zh", "zho", "key2"),
            Track(MediaType.Episode, ep2.Id, MediaSubtitleTrack.ScanMarkerStreamIndex, "", false, mediaHash: "key2"));
        await _dbContext.SaveChangesAsync();
        _subtitleService
            .Setup(service => service.GetAllSubtitles(It.IsAny<string>()))
            .ReturnsAsync([]);

        var action = await CreateController().GetMediaSubtitles(MediaType.Show, 5);

        var payload = Assert.IsType<MediaSubtitlesResponse>(Assert.IsType<OkObjectResult>(action.Result).Value!);
        // both episodes' rows render, ordered by episode then stream index
        Assert.Equal(new[] { 2, 4 }, payload.Embedded.Select(track => track.StreamIndex).ToArray());
        Assert.True(payload.EmbeddedScanned);

        // A newly added, not-yet-probed episode flips the flag back to false
        // while the scanned episodes' tracks still render.
        var season = await _dbContext.Seasons.FirstAsync();
        var ep3 = new Episode
        {
            SonarrId = 3,
            EpisodeNumber = 3,
            Title = "ep3",
            Path = season.Path,
            FileName = "show.s01e03.mkv",
            SeasonId = season.Id,
            Season = season
        };
        _dbContext.Episodes.Add(ep3);
        await _dbContext.SaveChangesAsync();

        var refreshed = Assert.IsType<MediaSubtitlesResponse>(
            (Assert.IsType<OkObjectResult>((await CreateController().GetMediaSubtitles(MediaType.Show, 5)).Result)).Value!);
        Assert.Equal(2, refreshed.Embedded.Count);
        Assert.False(refreshed.EmbeddedScanned);
    }

    [Fact]
    public async Task GetMediaSubtitles_UnknownId_Returns404()
    {
        var action = await CreateController().GetMediaSubtitles(MediaType.Movie, 999);

        Assert.IsType<NotFoundResult>(action.Result);
    }

    // ── subtitle card cache (audit P1-5) ────────────────────────────

    // Unique per test run: the cache is static and lives across tests, so a
    // fixed path would leak state between them.
    private static string FreshDirectory() => $"/movies/cache-{Guid.NewGuid():N}";

    private void SetupScanResult(string directory, params string[] files) =>
        _subtitleService
            .Setup(service => service.GetAllSubtitles(directory))
            .ReturnsAsync(files.Select(file => new Subtitles
            {
                Path = $"{directory}/{file}.zh.srt",
                FileName = $"{file}.zh",
                Language = "zh",
                Languages = ["zh"],
                Format = ".srt"
            }).ToList());

    [Fact]
    public async Task GetMediaSubtitles_RepeatedRequest_IsServedFromCacheWithoutRescan()
    {
        var directory = FreshDirectory();
        var movie = NewMovie(20, directory, "movie.mkv");
        _dbContext.Movies.Add(movie);
        await _dbContext.SaveChangesAsync();
        SetupScanResult(directory, "movie");

        var first = await CreateController().GetMediaSubtitles(MediaType.Movie, 20);
        var second = await CreateController().GetMediaSubtitles(MediaType.Movie, 20);

        // the disk (GetAllSubtitles) is hit exactly once — audit P1-5
        _subtitleService.Verify(service => service.GetAllSubtitles(directory), Times.Once);

        var firstPayload = Assert.IsType<MediaSubtitlesResponse>(Assert.IsType<OkObjectResult>(first.Result).Value!);
        var secondPayload = Assert.IsType<MediaSubtitlesResponse>(Assert.IsType<OkObjectResult>(second.Result).Value!);
        Assert.Equal(
            firstPayload.Files.Select(file => file.FileName).ToList(),
            secondPayload.Files.Select(file => file.FileName).ToList());
        Assert.Single(secondPayload.Files);
    }

    [Fact]
    public async Task GetMediaSubtitles_ExpiredCache_RescansTheDirectory()
    {
        var originalTtl = MediaController.SubtitleFileCacheTtl;
        MediaController.SubtitleFileCacheTtl = TimeSpan.FromMilliseconds(-1); // instantly stale
        try
        {
            var directory = FreshDirectory();
            var movie = NewMovie(21, directory, "movie.mkv");
            _dbContext.Movies.Add(movie);
            await _dbContext.SaveChangesAsync();
            SetupScanResult(directory, "movie");

            await CreateController().GetMediaSubtitles(MediaType.Movie, 21);
            await CreateController().GetMediaSubtitles(MediaType.Movie, 21);

            _subtitleService.Verify(service => service.GetAllSubtitles(directory), Times.Exactly(2));
        }
        finally
        {
            MediaController.SubtitleFileCacheTtl = originalTtl;
        }
    }

    [Fact]
    public async Task GetMediaSubtitles_ShowCachesEverySeasonDirectoryIndependently()
    {
        // two season directories under one show: the first view scans both,
        // the second view scans neither
        var (show, _, _, _) = NewShow(22);
        var seasonA = show.Seasons[0].Path!;
        var seasonB = seasonA + "-b";
        show.Seasons.Add(new Season
        {
            Id = 2300,
            SeasonNumber = 2,
            Path = seasonB,
            ShowId = show.Id,
            Show = show,
            Episodes = []
        });
        _dbContext.Shows.Add(show);
        await _dbContext.SaveChangesAsync();
        SetupScanResult(seasonA, "show.s01e01");
        SetupScanResult(seasonB, "show.s02e01");

        var first = await CreateController().GetMediaSubtitles(MediaType.Show, 22);
        var second = await CreateController().GetMediaSubtitles(MediaType.Show, 22);

        _subtitleService.Verify(service => service.GetAllSubtitles(seasonA), Times.Once);
        _subtitleService.Verify(service => service.GetAllSubtitles(seasonB), Times.Once);

        var payload = Assert.IsType<MediaSubtitlesResponse>(Assert.IsType<OkObjectResult>(second.Result).Value!);
        Assert.Equal(2, payload.Files.Count);
    }

    [Fact]
    public async Task GetMediaSubtitles_InvalidatedCache_Rescans()
    {
        var directory = FreshDirectory();
        var movie = NewMovie(23, directory, "movie.mkv");
        _dbContext.Movies.Add(movie);
        await _dbContext.SaveChangesAsync();
        SetupScanResult(directory, "movie");

        await CreateController().GetMediaSubtitles(MediaType.Movie, 23);
        MediaController.InvalidateSubtitleFileCache(directory);
        await CreateController().GetMediaSubtitles(MediaType.Movie, 23);

        _subtitleService.Verify(service => service.GetAllSubtitles(directory), Times.Exactly(2));
    }

    // ── library: pageSize clamp + single-media detail path (audit P0-8/P2) ──

    [Fact]
    public async Task GetLibrary_ClampsPageSizeInto1To100()
    {
        var library = new Mock<IMediaLibraryService>();
        library
            .Setup(s => s.GetLibrary(
                It.IsAny<MediaType?>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<bool>(),
                It.IsAny<int>(), It.IsAny<int>()))
            .ReturnsAsync(new PagedResult<MediaLibraryItem>
            {
                Items = [], TotalCount = 0, PageNumber = 1, PageSize = 1
            });
        var controller = CreateController(library);

        await controller.GetLibrary(null, null, null, null, null, null, ascending: true, pageSize: 5000, pageNumber: 1);
        await controller.GetLibrary(null, null, null, null, null, null, ascending: true, pageSize: 0, pageNumber: 1);

        library.Verify(s => s.GetLibrary(null, null, null, null, null, true, 1, 100), Times.Once);
        library.Verify(s => s.GetLibrary(null, null, null, null, null, true, 1, 1), Times.Once);
    }

    [Fact]
    public async Task GetLibrary_WithId_UsesDirectSingleMediaPath()
    {
        var item = new MediaLibraryItem
        {
            Id = 7,
            MediaType = MediaType.Show,
            Title = "Solo",
            Tracks = [],
            State = MediaTranslationState.Queued
        };
        var library = new Mock<IMediaLibraryService>();
        library
            .Setup(s => s.GetLibraryItem(MediaType.Show, 7))
            .ReturnsAsync(item);
        var controller = CreateController(library);

        var action = await controller.GetLibrary(
            MediaType.Show, 7, null, null, null, null, ascending: true, pageSize: 20, pageNumber: 1);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var page = Assert.IsType<PagedResult<MediaLibraryItem>>(ok.Value!);
        Assert.Same(item, Assert.Single(page.Items));
        Assert.Equal(1, page.TotalCount);
        Assert.Equal(1, page.PageSize);
        // the detail-page branch must never trigger the full aggregation
        library.Verify(s => s.GetLibraryItem(MediaType.Show, 7), Times.Once);
        library.Verify(s => s.GetLibrary(
            It.IsAny<MediaType?>(), It.IsAny<string?>(), It.IsAny<string?>(),
            It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<bool>(),
            It.IsAny<int>(), It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public async Task GetLibrary_WithUnknownId_Returns404()
    {
        var library = new Mock<IMediaLibraryService>();
        library
            .Setup(s => s.GetLibraryItem(It.IsAny<MediaType?>(), It.IsAny<int>()))
            .ReturnsAsync((MediaLibraryItem?)null);

        var action = await CreateController(library).GetLibrary(
            MediaType.Movie, 42, null, null, null, null, ascending: true, pageSize: 20, pageNumber: 1);

        Assert.IsType<NotFoundResult>(action.Result);
    }
}
