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
using Lingarr.Server.Modules.Settings;
using Lingarr.Server.Modules.Subtitle;
using Lingarr.Server.Modules.SubtitleSearch.Pipeline;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
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
    /// Backs the subtitle-need rule's target resolution; defaults to no
    /// configured targets (conservative: nothing auto-exempts).
    private readonly Mock<ISettingService> _settingService = new();
    private readonly LingarrDbContext _dbContext;

    public MediaControllerTests()
    {
        var options = new DbContextOptionsBuilder<LingarrDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _dbContext = new LingarrDbContext(options);
        _settingService
            .Setup(service => service.GetSettingAsJson<TargetLanguage>(It.IsAny<string>()))
            .ReturnsAsync([]);
    }

    private MediaController CreateController(Mock<IMediaLibraryService>? library = null) => new(
        new Mock<IMediaService>().Object,
        (library ?? new Mock<IMediaLibraryService>()).Object,
        _subtitleService.Object,
        new AudioLanguageService(
            _dbContext,
            _settingService.Object,
            NullHttpClientFactory.Instance,
            NullLogger<AudioLanguageService>.Instance),
        _dbContext,
        new TargetLanguageResolver(_settingService.Object),
        new SubtitleCoverageRefresher(
            _dbContext, _subtitleService.Object, new TargetLanguageResolver(_settingService.Object),
            NullLogger<SubtitleCoverageRefresher>.Instance));

    private static Movie NewMovie(int id, string path, string fileName, string[]? audioLanguages = null) => new()
    {
        Id = id,
        RadarrId = id,
        Title = $"movie {id}",
        Path = path,
        FileName = fileName,
        DateAdded = DateTimeOffset.UtcNow,
        AudioLanguages = audioLanguages
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
    public async Task GetMoviesAndGetShows_ClampPageSizeAndPageNumber()
    {
        // same host-input guard as /library (audit P2): a 5000-wide page
        // must clamp to 100 and a non-positive page number must never reach
        // the service's Skip arithmetic with a negative count.
        var media = new Mock<IMediaService>();
        media.Setup(s => s.GetMovies(
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<bool>(),
                It.IsAny<int>(), It.IsAny<int>()))
            .ReturnsAsync(new PagedResult<MovieResponse> { TotalCount = 0, PageNumber = 1, PageSize = 1 });
        media.Setup(s => s.GetShows(
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<bool>(),
                It.IsAny<int>(), It.IsAny<int>()))
            .ReturnsAsync(new PagedResult<Show> { TotalCount = 0, PageNumber = 1, PageSize = 1 });
        var controller = new MediaController(
            media.Object,
            new Mock<IMediaLibraryService>().Object,
            _subtitleService.Object,
            new AudioLanguageService(
                _dbContext,
                _settingService.Object,
                NullHttpClientFactory.Instance,
                NullLogger<AudioLanguageService>.Instance),
            _dbContext,
            new TargetLanguageResolver(_settingService.Object),
            new SubtitleCoverageRefresher(
                _dbContext, _subtitleService.Object, new TargetLanguageResolver(_settingService.Object),
                NullLogger<SubtitleCoverageRefresher>.Instance));

        await controller.GetMovies(null, null, ascending: true, pageSize: 5000, pageNumber: 0);
        await controller.GetShows(null, null, ascending: true, pageSize: 5000, pageNumber: -2);

        media.Verify(s => s.GetMovies(null, null, true, 1, 100), Times.Once);
        media.Verify(s => s.GetShows(null, null, true, 1, 100), Times.Once);
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

    // ── subtitle card audio languages (PRD card A) ──────────────────

    [Fact]
    public async Task GetMediaSubtitles_CarriesMovieAudioLanguages()
    {
        _dbContext.Movies.Add(NewMovie(30, "/movies/audio", "audio.mkv", ["zh", "en"]));
        await _dbContext.SaveChangesAsync();
        _subtitleService
            .Setup(service => service.GetAllSubtitles(It.IsAny<string>()))
            .ReturnsAsync([]);

        var payload = Assert.IsType<MediaSubtitlesResponse>(
            Assert.IsType<OkObjectResult>((await CreateController().GetMediaSubtitles(MediaType.Movie, 30)).Result).Value!);

        Assert.Equal(["zh", "en"], payload.AudioLanguages);
    }

    [Fact]
    public async Task GetMediaSubtitles_ShowAggregatesEpisodeAudio_UnknownDominates()
    {
        var (show, _, ep1, ep2) = NewShow(31);
        ep1.AudioLanguages = ["zh"];
        ep2.AudioLanguages = ["en"];
        _dbContext.Shows.Add(show);
        await _dbContext.SaveChangesAsync();
        _subtitleService
            .Setup(service => service.GetAllSubtitles(It.IsAny<string>()))
            .ReturnsAsync([]);

        var unionPayload = Assert.IsType<MediaSubtitlesResponse>(
            Assert.IsType<OkObjectResult>((await CreateController().GetMediaSubtitles(MediaType.Show, 31)).Result).Value!);
        Assert.Equal(["en", "zh"], unionPayload.AudioLanguages);

        // one unknown episode is enough to make the whole show unknown
        ep2.AudioLanguages = null;
        await _dbContext.SaveChangesAsync();
        var unknownPayload = Assert.IsType<MediaSubtitlesResponse>(
            Assert.IsType<OkObjectResult>((await CreateController().GetMediaSubtitles(MediaType.Show, 31)).Result).Value!);
        Assert.Null(unknownPayload.AudioLanguages);
    }

    // ── manual subtitle-need mark (PRD card A) ───────────────────────

    [Fact]
    public async Task SetSubtitleNeed_Movie_WritesOverrideAndFlipsImmediately()
    {
        var movie = NewMovie(40, "/movies/native", "native.mkv", ["zh"]);
        movie.NeedsTranslation = false; // the scan judged it exempt
        _dbContext.Movies.Add(movie);
        await _dbContext.SaveChangesAsync();

        var action = await CreateController().SetSubtitleNeed(MediaType.Movie, 40, new SubtitleNeedRequest { Needed = true });

        var payload = Assert.IsType<SubtitleNeedResponse>(Assert.IsType<OkObjectResult>(action.Result).Value!);
        Assert.True(payload.NeedsTranslation);
        Assert.True(payload.AudioOverride);
        Assert.False(payload.NeedsNoSubtitles); // the frontend contract field
        Assert.Equal(["zh"], payload.AudioLanguages);

        var stored = await _dbContext.Movies.AsNoTracking().SingleAsync(m => m.Id == 40);
        Assert.True(stored.AudioOverride);
        Assert.True(stored.NeedsTranslation);

        // reversible: marking it back as not needing flips both flags again
        var flip = await CreateController().SetSubtitleNeed(MediaType.Movie, 40, new SubtitleNeedRequest { Needed = false });
        var flipPayload = Assert.IsType<SubtitleNeedResponse>(Assert.IsType<OkObjectResult>(flip.Result).Value!);
        Assert.False(flipPayload.NeedsTranslation);
        Assert.True(flipPayload.NeedsNoSubtitles);
        Assert.False(flipPayload.AudioOverride);
        var storedAfterFlip = await _dbContext.Movies.AsNoTracking().SingleAsync(m => m.Id == 40);
        Assert.False(storedAfterFlip.AudioOverride);
        Assert.False(storedAfterFlip.NeedsTranslation);
    }

    [Fact]
    public async Task SetSubtitleNeed_Episode_WritesTheSingleRow()
    {
        var (show, _, ep1, _) = NewShow(41);
        _dbContext.Shows.Add(show);
        await _dbContext.SaveChangesAsync();

        var action = await CreateController().SetSubtitleNeed(MediaType.Episode, ep1.Id, new SubtitleNeedRequest { Needed = true });

        var payload = Assert.IsType<SubtitleNeedResponse>(Assert.IsType<OkObjectResult>(action.Result).Value!);
        Assert.True(payload.NeedsTranslation);
        var stored = await _dbContext.Episodes.AsNoTracking().SingleAsync(e => e.Id == ep1.Id);
        Assert.True(stored.AudioOverride);
        Assert.True(stored.NeedsTranslation);
    }

    [Fact]
    public async Task SetSubtitleNeed_Show_CascadesToEveryEpisode()
    {
        var (show, _, ep1, ep2) = NewShow(42);
        ep1.AudioLanguages = ["zh"];
        // ep2's audio stays unknown → the aggregated response reports null
        _dbContext.Shows.Add(show);
        await _dbContext.SaveChangesAsync();

        var action = await CreateController().SetSubtitleNeed(MediaType.Show, 42, new SubtitleNeedRequest { Needed = true });

        var payload = Assert.IsType<SubtitleNeedResponse>(Assert.IsType<OkObjectResult>(action.Result).Value!);
        Assert.True(payload.NeedsTranslation);
        Assert.False(payload.NeedsNoSubtitles);
        Assert.True(payload.AudioOverride);
        Assert.Null(payload.AudioLanguages);

        var stored = await _dbContext.Episodes.AsNoTracking().Where(e => e.Season.ShowId == 42).ToListAsync();
        Assert.Equal(2, stored.Count);
        Assert.All(stored, episode =>
        {
            Assert.True(episode.AudioOverride);
            Assert.True(episode.NeedsTranslation);
        });
    }

    [Fact]
    public async Task SetSubtitleNeed_Episode_NotNeeded_PersistsAllThreeFlags()
    {
        // QA6 P1-1: the Episode branch used to skip NoSubtitleNeeded, so the
        // library aggregate (episodes.All(NoSubtitleNeeded)) and the gap
        // fill's SQL (needs_translation=1 AND no_subtitle_needed=0) flipped
        // the manual exemption right back. Needed=false must persist the
        // full three-column state, same as the Movie branch.
        var (show, _, ep1, _) = NewShow(43);
        ep1.NeedsTranslation = true; // the scan judged it a gap
        _dbContext.Shows.Add(show);
        await _dbContext.SaveChangesAsync();

        var action = await CreateController().SetSubtitleNeed(MediaType.Episode, ep1.Id, new SubtitleNeedRequest { Needed = false });

        var payload = Assert.IsType<SubtitleNeedResponse>(Assert.IsType<OkObjectResult>(action.Result).Value!);
        Assert.False(payload.NeedsTranslation);
        Assert.True(payload.NeedsNoSubtitles); // the frontend contract field
        Assert.False(payload.AudioOverride);

        var stored = await _dbContext.Episodes.AsNoTracking().SingleAsync(e => e.Id == ep1.Id);
        Assert.False(stored.AudioOverride);
        Assert.False(stored.NeedsTranslation);
        Assert.True(stored.NoSubtitleNeeded); // the previously-missing write
    }

    [Fact]
    public async Task SetSubtitleNeed_NullClearsTheMark_AndTheAudioRuleTakesOverAgain()
    {
        // needed:null is the third state — clear the manual mark so the
        // automatic audio rule decides: zh audio + zh target → auto-exempt
        _settingService
            .Setup(service => service.GetSettingAsJson<TargetLanguage>(It.IsAny<string>()))
            .ReturnsAsync([new TargetLanguage { Code = "zh", Name = "Chinese" }]);
        var movie = NewMovie(45, "/movies/clear", "clear.mkv", ["zh"]);
        _dbContext.Movies.Add(movie);
        await _dbContext.SaveChangesAsync();

        // mark needed first (override=true, un-exempted)
        await CreateController().SetSubtitleNeed(MediaType.Movie, 45, new SubtitleNeedRequest { Needed = true });
        var marked = await _dbContext.Movies.AsNoTracking().SingleAsync(m => m.Id == 45);
        Assert.True(marked.AudioOverride);
        Assert.True(marked.NeedsTranslation);

        // clear: null returns the item to the automatic rule — zh audio
        // covers the zh target, so the exemption re-applies
        var action = await CreateController().SetSubtitleNeed(MediaType.Movie, 45, new SubtitleNeedRequest { Needed = null });
        var payload = Assert.IsType<SubtitleNeedResponse>(Assert.IsType<OkObjectResult>(action.Result).Value!);
        Assert.Null(payload.AudioOverride);
        Assert.True(payload.NeedsNoSubtitles);
        Assert.False(payload.NeedsTranslation);

        var stored = await _dbContext.Movies.AsNoTracking().SingleAsync(m => m.Id == 45);
        Assert.Null(stored.AudioOverride);
        Assert.False(stored.NeedsTranslation);
        Assert.True(stored.NoSubtitleNeeded);
    }

    [Fact]
    public async Task SetSubtitleNeed_Show_NotNeeded_CascadesAllThreeFlagsToEveryEpisode()
    {
        // QA6 P1-1, show cascade: marking a show as not needing subtitles
        // must persist the exemption column on every episode too — otherwise
        // the show-level aggregate recomputes "still needing" and the UI
        // flips back on the next refresh.
        var (show, _, ep1, ep2) = NewShow(44);
        ep1.NeedsTranslation = true;
        ep2.NeedsTranslation = true;
        _dbContext.Shows.Add(show);
        await _dbContext.SaveChangesAsync();

        var action = await CreateController().SetSubtitleNeed(MediaType.Show, 44, new SubtitleNeedRequest { Needed = false });

        var payload = Assert.IsType<SubtitleNeedResponse>(Assert.IsType<OkObjectResult>(action.Result).Value!);
        Assert.False(payload.NeedsTranslation);
        Assert.True(payload.NeedsNoSubtitles);
        Assert.False(payload.AudioOverride);

        var stored = await _dbContext.Episodes.AsNoTracking().Where(e => e.Season.ShowId == 44).ToListAsync();
        Assert.Equal(2, stored.Count);
        Assert.All(stored, episode =>
        {
            Assert.False(episode.AudioOverride);
            Assert.False(episode.NeedsTranslation);
            Assert.True(episode.NoSubtitleNeeded); // the previously-missing write
        });
    }

    [Fact]
    public async Task SetSubtitleNeed_UnknownIdOrUnsupportedType_Returns404()
    {
        Assert.IsType<NotFoundResult>(
            (await CreateController().SetSubtitleNeed(MediaType.Movie, 999, new SubtitleNeedRequest { Needed = true })).Result);
        Assert.IsType<NotFoundResult>(
            (await CreateController().SetSubtitleNeed(MediaType.Show, 999, new SubtitleNeedRequest { Needed = true })).Result);
        Assert.IsType<NotFoundResult>(
            (await CreateController().SetSubtitleNeed(MediaType.Season, 1, new SubtitleNeedRequest { Needed = true })).Result);
    }
}

/// <summary>
/// PUT api/media/{mediaType}/{id}/target-languages (B08): the per-media
/// target-language override endpoint. Real SQLite because the endpoint
/// refreshes the coverage-derived flags through the production
/// SubtitleCoverageRefresher (ExecuteUpdate), and the subtitle scan is
/// mocked empty so the need rule decides on audio alone — with global
/// targets [zh,en] and en audio, the three stored states are fully
/// discriminating: override [zh] → needs (en no longer exempts), NULL →
/// exempt (global list), "" → not needing and NOT exempt (explicit
/// 「无目标」, not the audio exemption).
/// </summary>
public class MediaTargetLanguagesEndpointTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly LingarrDbContext _dbContext;
    private readonly Mock<ISubtitleService> _subtitleService = new();
    private readonly TargetLanguageResolver _resolver;
    private readonly MediaController _controller;

    public MediaTargetLanguagesEndpointTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _dbContext = new TargetDbContext(new DbContextOptionsBuilder<LingarrDbContext>()
            .UseSqlite(_connection)
            .UseSnakeCaseNamingConvention()
            .Options);
        _dbContext.Database.EnsureCreated();

        var settings = new Mock<ISettingService>();
        settings
            .Setup(service => service.GetSettingAsJson<TargetLanguage>(It.IsAny<string>()))
            .ReturnsAsync(new List<TargetLanguage>
            {
                new() { Name = "zh", Code = "zh" },
                new() { Name = "en", Code = "en" }
            });
        _subtitleService
            .Setup(service => service.GetAllSubtitles(It.IsAny<string>()))
            .ReturnsAsync(new List<Subtitles>());

        _resolver = new TargetLanguageResolver(settings.Object);
        var refresher = new SubtitleCoverageRefresher(
            _dbContext, _subtitleService.Object, _resolver,
            NullLogger<SubtitleCoverageRefresher>.Instance);
        _controller = new MediaController(
            null!, null!, _subtitleService.Object, null!, _dbContext, _resolver, refresher);
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    private sealed class TargetDbContext(DbContextOptions options) : LingarrDbContext(options);

    [Fact]
    public async Task SetTargetLanguages_Movie_RoundTripsAllThreeStates()
    {
        var movie = new Movie
        {
            Id = 1,
            RadarrId = 1,
            Title = "Dune",
            Path = "/movies/dune",
            FileName = "dune.mkv",
            DateAdded = DateTimeOffset.UtcNow,
            AudioLanguages = ["en"]
        };
        _dbContext.Movies.Add(movie);
        await _dbContext.SaveChangesAsync();

        // override [zh]: en audio stops exempting, zh is missing → needs
        var set = await _controller.SetTargetLanguages(
            MediaType.Movie, 1, new TargetLanguagesRequest { Languages = ["zh"] });
        var payload = PayloadOf(set);
        var codes = LanguagesOf(payload);
        Assert.NotNull(codes);
        Assert.Equal(new[] { "zh" }, codes);
        Assert.Equal("override", SourceOf(payload));
        var stored = await _dbContext.Movies.AsNoTracking().SingleAsync(m => m.Id == 1);
        Assert.Equal("zh", stored.TargetLanguagesOverride);
        Assert.True(stored.NeedsTranslation);
        Assert.False(stored.NoSubtitleNeeded);

        // explicit「无目标」: nothing can be missing — and the exemption
        // flag stays FALSE (the state is reversible, not an audio exemption)
        var setNone = await _controller.SetTargetLanguages(
            MediaType.Movie, 1, new TargetLanguagesRequest { Languages = [] });
        var nonePayload = LanguagesOf(PayloadOf(setNone));
        Assert.NotNull(nonePayload);
        Assert.Empty(nonePayload);
        stored = await _dbContext.Movies.AsNoTracking().SingleAsync(m => m.Id == 1);
        Assert.Equal(string.Empty, stored.TargetLanguagesOverride);
        Assert.False(stored.NeedsTranslation);
        Assert.False(stored.NoSubtitleNeeded);

        // clear (null): back to following the global list — en audio exempts
        var cleared = await _controller.SetTargetLanguages(
            MediaType.Movie, 1, new TargetLanguagesRequest { Languages = null });
        var clearPayload = PayloadOf(cleared);
        Assert.Null(LanguagesOf(clearPayload));
        Assert.Equal("global", SourceOf(clearPayload));
        stored = await _dbContext.Movies.AsNoTracking().SingleAsync(m => m.Id == 1);
        Assert.Null(stored.TargetLanguagesOverride);
        Assert.False(stored.NeedsTranslation);
        Assert.True(stored.NoSubtitleNeeded); // the global audio exemption
    }

    [Fact]
    public async Task SetTargetLanguages_Show_StoresOnceAndRefreshesEveryEpisode()
    {
        var show = new Show
        {
            Id = 2,
            SonarrId = 2,
            Title = "Shōgun",
            Path = "/tv/shogun",
            DateAdded = DateTimeOffset.UtcNow
        };
        var season = new Season
        {
            Id = 20,
            SeasonNumber = 1,
            Path = "/tv/shogun/s01",
            ShowId = show.Id,
            Show = show
        };
        season.Episodes.Add(new Episode
        {
            Id = 201,
            SonarrId = 1,
            EpisodeNumber = 1,
            Title = "ep1",
            Path = season.Path,
            FileName = "shogun.s01e01.mkv",
            SeasonId = season.Id,
            Season = season,
            AudioLanguages = ["en"]
        });
        season.Episodes.Add(new Episode
        {
            Id = 202,
            SonarrId = 2,
            EpisodeNumber = 2,
            Title = "ep2",
            Path = season.Path,
            FileName = "shogun.s01e02.mkv",
            SeasonId = season.Id,
            Season = season,
            AudioLanguages = ["en"]
        });
        show.Seasons.Add(season);
        _dbContext.Shows.Add(show);
        await _dbContext.SaveChangesAsync();

        var set = await _controller.SetTargetLanguages(
            MediaType.Show, 2, new TargetLanguagesRequest { Languages = ["zh"] });
        var payload = PayloadOf(set);
        Assert.Equal("show", (string?)payload.GetType().GetProperty("target")!.GetValue(payload));

        // the SHOW carries the column; every episode's flags were refreshed
        // under the effective [zh] set (en audio no longer exempts)
        var storedShow = await _dbContext.Shows.AsNoTracking().SingleAsync(s => s.Id == 2);
        Assert.Equal("zh", storedShow.TargetLanguagesOverride);
        var episodes = await _dbContext.Episodes.AsNoTracking()
            .Where(e => e.Season.ShowId == 2)
            .ToListAsync();
        Assert.Equal(2, episodes.Count);
        Assert.All(episodes, episode =>
        {
            Assert.True(episode.NeedsTranslation);
            Assert.False(episode.NoSubtitleNeeded);
        });
    }

    [Fact]
    public async Task SetTargetLanguages_EpisodeOrSeason_IsUnsupportedAndUnknownIds404()
    {
        // episodes inherit their show's override — there is no per-episode
        // column to write, so the episode/season forms are rejected
        Assert.IsType<NotFoundResult>(
            await _controller.SetTargetLanguages(MediaType.Episode, 1, new TargetLanguagesRequest()));
        Assert.IsType<NotFoundResult>(
            await _controller.SetTargetLanguages(MediaType.Season, 1, new TargetLanguagesRequest()));
        Assert.IsType<NotFoundResult>(
            await _controller.SetTargetLanguages(MediaType.Movie, 999, new TargetLanguagesRequest()));
        Assert.IsType<NotFoundResult>(
            await _controller.SetTargetLanguages(MediaType.Show, 999, new TargetLanguagesRequest()));
    }

    private static object PayloadOf(IActionResult action) =>
        Assert.IsType<OkObjectResult>(action).Value!;

    private static string[]? LanguagesOf(object payload) =>
        (string[]?)payload.GetType().GetProperty("languages")!.GetValue(payload);

    private static string? SourceOf(object payload) =>
        (string?)payload.GetType().GetProperty("source")!.GetValue(payload);
}
