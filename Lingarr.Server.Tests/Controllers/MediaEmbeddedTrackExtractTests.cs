using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Lingarr.Core.Data;
using Lingarr.Core.Entities;
using Lingarr.Core.Enum;
using Lingarr.Server.Controllers;
using Lingarr.Server.Models.Api;
using Lingarr.Server.Modules.Media;
using Lingarr.Server.Modules.Settings;
using Lingarr.Server.Modules.Translation;
using Lingarr.Server.Modules.Subtitle;
using Lingarr.Server.Modules.SubtitleSearch.Pipeline;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Lingarr.Server.Tests.Controllers;

/// <summary>
/// B04 extract endpoint against real SQLite, a real temp media tree and the
/// production EmbeddedTrackService (only the ffmpeg process is stubbed —
/// the stub writes the SRT payload to the output path named in its
/// arguments, exactly where the real binary would). Pins the idempotence
/// contract (an existing sidecar answers 200 with created=false and an
/// untouched mtime), the placement ({videoBase}.{language}.srt next to the
/// video), the file-card visibility after extraction and the honest 409s.
/// </summary>
public class MediaEmbeddedTrackExtractTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly LingarrDbContext _dbContext;
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "lingarr-extract-" + Guid.NewGuid().ToString("N"));

    private readonly MediaController _controller;
    private readonly ISubtitleService _subtitleService;

    public MediaEmbeddedTrackExtractTests()
    {
        Directory.CreateDirectory(_root);
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _dbContext = new ExtractDbContext(new DbContextOptionsBuilder<LingarrDbContext>()
            .UseSqlite(_connection)
            .UseSnakeCaseNamingConvention()
            .Options);
        _dbContext.Database.EnsureCreated();

        var settings = new Lingarr.Server.Tests.Modules.SubtitleSearch.InMemorySettingService();
        settings.SetSetting(
            Lingarr.Core.Configuration.SettingKeys.Translation.TargetLanguages,
            /* lang=json */ """[{"name":"Chinese","code":"zh"}]""").GetAwaiter().GetResult();
        _subtitleService = new SubtitleService(
            NullLogger<SubtitleService>.Instance, new LanguageCodeService(), settings);
        var environment = new Mock<IHostEnvironment>();
        environment.SetupGet(host => host.ContentRootPath).Returns(_root);
        var embeddedTrackService = new EmbeddedTrackService(
            _dbContext,
            NullLogger<EmbeddedTrackService>.Instance,
            environment.Object,
            ffmpegRunner: (_, arguments, _) =>
            {
                // The real ffmpeg writes the conversion output to the last
                // argument — the stub writes a fixed SRT payload there.
                var target = arguments[^1];
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.WriteAllText(target, "1\n00:00:01,000 --> 00:00:02,000\n提取的字幕\n");
                return Task.FromResult(string.Empty);
            },
            ffmpegPathResolver: () => "ffmpeg");
        var resolver = new TargetLanguageResolver(settings);
        _controller = new MediaController(
            null!, null!, _subtitleService, null!, _dbContext, resolver,
            new SubtitleCoverageRefresher(
                _dbContext, _subtitleService, resolver,
                NullLogger<SubtitleCoverageRefresher>.Instance),
            embeddedTrackService);
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        _connection.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private sealed class ExtractDbContext(DbContextOptions options) : LingarrDbContext(options);

    /// <summary>Property of an anonymous JSON body (the endpoint answers anonymous objects).</summary>
    private static T? BodyProp<T>(object body, string name)
        where T : class
    {
        var value = body.GetType().GetProperty(name)!.GetValue(body);
        return (T?)value;
    }

    private async Task<(Movie Movie, string Container)> SeedMovieAsync(int radarrId, string fileName)
    {
        var directory = Path.Combine(_root, $"movie{radarrId}");
        Directory.CreateDirectory(directory);
        var movie = new Movie
        {
            RadarrId = radarrId,
            Title = $"movie {radarrId}",
            Year = 2020,
            Path = directory,
            // dotted release base name WITHOUT extension — the sidecar must
            // extend it, never eat a segment as an extension
            FileName = fileName,
            DateAdded = DateTimeOffset.UtcNow,
            AudioLanguages = ["en"],
            NeedsTranslation = true
        };
        _dbContext.Movies.Add(movie);
        // save FIRST: the track row needs the database-assigned movie id
        await _dbContext.SaveChangesAsync();
        var container = Path.Combine(directory, fileName + ".mkv");
        await File.WriteAllTextAsync(container, "container");
        _dbContext.MediaSubtitleTracks.Add(new MediaSubtitleTrack
        {
            MediaType = MediaType.Movie,
            MediaId = movie.Id,
            StreamIndex = 2,
            Language = "zh",
            RawLanguage = "chi",
            Title = "简体中文",
            Codec = "srt",
            IsTextBased = true,
            FilePath = container
        });
        await _dbContext.SaveChangesAsync();
        return (movie, container);
    }

    [Fact]
    public async Task Extract_CreatesTheSidecarNextToTheVideoAndSurfacesItOnTheFileCard()
    {
        var (movie, _) = await SeedMovieAsync(1, "Movie.2023.WEBDL-1080p");

        var action = await _controller.ExtractEmbeddedTrack(MediaType.Movie, movie.Id, 2);

        var ok = Assert.IsType<OkObjectResult>(action);
        Assert.Equal("True", BodyProp<object>(ok.Value!, "created")!.ToString());
        // dotted release base name survives: only the video extension is gone
        var sidecar = Path.Combine(movie.Path!, "Movie.2023.WEBDL-1080p.zh.srt");
        Assert.Equal(sidecar, BodyProp<string>(ok.Value!, "path"));
        Assert.True(File.Exists(sidecar));
        Assert.Contains("提取的字幕", await File.ReadAllTextAsync(sidecar));

        // the file card (GET subtitles) lists the new row — the endpoint
        // dropped the directory's cached scan on purpose
        var files = await _controller.GetMediaSubtitles(MediaType.Movie, movie.Id);
        var okFiles = Assert.IsType<OkObjectResult>(files.Result);
        var payload = Assert.IsType<MediaSubtitlesResponse>(okFiles.Value!);
        Assert.Contains(payload.Files, file => file.FileName == "Movie.2023.WEBDL-1080p.zh.srt");
    }

    [Fact]
    public async Task Extract_IsIdempotent_AnExistingSidecarAnswers200WithUntouchedMtime()
    {
        var (movie, _) = await SeedMovieAsync(2, "movie2");

        var first = await _controller.ExtractEmbeddedTrack(MediaType.Movie, movie.Id, 2);
        var firstBody = Assert.IsType<OkObjectResult>(first).Value!;
        Assert.Equal("True", BodyProp<object>(firstBody, "created")!.ToString());

        var sidecar = BodyProp<string>(firstBody, "path")!;
        var before = File.GetLastWriteTimeUtc(sidecar);
        var beforeContent = await File.ReadAllTextAsync(sidecar);

        var second = await _controller.ExtractEmbeddedTrack(MediaType.Movie, movie.Id, 2);
        var secondBody = Assert.IsType<OkObjectResult>(second).Value!;
        Assert.Equal("False", BodyProp<object>(secondBody, "created")!.ToString());
        Assert.Equal(sidecar, BodyProp<string>(secondBody, "path"));
        // the idempotent hit neither re-ran ffmpeg nor touched the file
        Assert.Equal(before, File.GetLastWriteTimeUtc(sidecar));
        Assert.Equal(beforeContent, await File.ReadAllTextAsync(sidecar));
    }

    [Fact]
    public async Task Extract_GraphicTrack_Answers409WithTheReadableReason()
    {
        var (movie, container) = await SeedMovieAsync(3, "movie3");
        _dbContext.MediaSubtitleTracks.Add(new MediaSubtitleTrack
        {
            MediaType = MediaType.Movie,
            MediaId = movie.Id,
            StreamIndex = 7,
            Language = "zh",
            RawLanguage = "chi",
            Title = null,
            Codec = "pgs",
            IsTextBased = false,
            FilePath = container
        });
        await _dbContext.SaveChangesAsync();

        var action = await _controller.ExtractEmbeddedTrack(MediaType.Movie, movie.Id, 7);

        var conflict = Assert.IsType<ConflictObjectResult>(action);
        Assert.Contains("image-based", BodyProp<string>(conflict.Value!, "message"));
    }

    [Fact]
    public async Task Extract_UnknownRowOrMedia_Answers409Or404()
    {
        var (movie, _) = await SeedMovieAsync(4, "movie4");

        // stream index that was never probed → 409 with the scan hint
        var unknownTrack = await _controller.ExtractEmbeddedTrack(MediaType.Movie, movie.Id, 99);
        Assert.IsType<ConflictObjectResult>(unknownTrack);

        // media id that does not exist → 404
        var unknownMedia = await _controller.ExtractEmbeddedTrack(MediaType.Movie, 424242, 2);
        Assert.IsType<NotFoundResult>(unknownMedia);
    }

    [Fact]
    public async Task Extract_Episode_AddressesTheEpisodesOwnTrackRow()
    {
        // embedded track rows are owned by the EPISODE — the endpoint must
        // resolve episode ids, not only movies.
        var directory = Path.Combine(_root, "show8-s01");
        Directory.CreateDirectory(directory);
        var show = new Show
        {
            SonarrId = 8, Title = "show 8", Path = directory, DateAdded = DateTimeOffset.UtcNow
        };
        var season = new Season { SeasonNumber = 1, Path = directory, ShowId = show.Id, Show = show };
        var episode = new Episode
        {
            SonarrId = 81,
            EpisodeNumber = 1,
            Title = "pilot",
            Path = directory,
            FileName = "show8.s01e01",
            DateAdded = DateTimeOffset.UtcNow,
            SeasonId = season.Id,
            Season = season,
            NeedsTranslation = true
        };
        season.Episodes.Add(episode);
        show.Seasons.Add(season);
        _dbContext.Shows.Add(show);
        // save FIRST: the track row needs the database-assigned episode id
        await _dbContext.SaveChangesAsync();
        var container = Path.Combine(directory, "show8.s01e01.mkv");
        await File.WriteAllTextAsync(container, "container");
        _dbContext.MediaSubtitleTracks.Add(new MediaSubtitleTrack
        {
            MediaType = MediaType.Episode,
            MediaId = episode.Id,
            StreamIndex = 3,
            Language = "zh",
            RawLanguage = "chi",
            Title = null,
            Codec = "srt",
            IsTextBased = true,
            FilePath = container
        });
        await _dbContext.SaveChangesAsync();

        var action = await _controller.ExtractEmbeddedTrack(MediaType.Episode, episode.Id, 3);

        var body = Assert.IsType<OkObjectResult>(action).Value!;
        Assert.Equal("True", BodyProp<object>(body, "created")!.ToString());
        Assert.Equal(Path.Combine(directory, "show8.s01e01.zh.srt"), BodyProp<string>(body, "path"));
        Assert.True(File.Exists(BodyProp<string>(body, "path")!));
    }
}
