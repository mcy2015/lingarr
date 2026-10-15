using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Lingarr.Core.Data;
using Lingarr.Core.Entities;
using Lingarr.Core.Enum;
using Lingarr.Server.Modules.Media;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Lingarr.Server.Tests.Services;

/// <summary>
/// PRD card A piggyback coverage against a real SQLite schema and real media
/// files on disk, with ffprobe stubbed: the same probe that feeds the subtitle
/// track rows also backfills movies/episodes.audio_languages — but only while
/// the column is null (*arr payloads outrank the local probe), and never with
/// an empty array (unknown stays null). The subtitle rows themselves must stay
/// byte-for-byte identical to the pre-card-A behavior now that the report also
/// carries video/audio streams.
/// </summary>
public class EmbeddedTrackAudioLanguageTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly LingarrDbContext _dbContext;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lingarr-emb-audio-" + Guid.NewGuid().ToString("N"));
    private readonly List<string> _probedPaths = [];

    public EmbeddedTrackAudioLanguageTests()
    {
        Directory.CreateDirectory(_root);
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _dbContext = new LingarrDbContext(new DbContextOptionsBuilder<LingarrDbContext>()
            .UseSqlite(_connection)
            .Options);
        _dbContext.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        _connection.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    // One probe result: a video stream, two audio streams (chi + english) and
    // one Chinese text subtitle track — the shape the un-filtered ffprobe
    // report now has.
    private const string MixedContainerReport =
        """
        { "streams": [
            { "index": 0, "codec_name": "h264", "codec_type": "video" },
            { "index": 1, "codec_name": "eac3", "codec_type": "audio", "tags": { "language": "chi" } },
            { "index": 2, "codec_name": "ac3", "codec_type": "audio", "tags": { "language": "english" } },
            { "index": 3, "codec_name": "subrip", "codec_type": "subtitle", "tags": { "language": "chi", "title": "简体中文" } }
        ] }
        """;

    private EmbeddedTrackService CreateService(string report = MixedContainerReport) => new(
        _dbContext,
        NullLogger<EmbeddedTrackService>.Instance,
        ffprobeRunner: (filePath, _) =>
        {
            _probedPaths.Add(filePath);
            return Task.FromResult(report);
        });

    private async Task<Movie> SeedMovieAsync(int id, string fileName, string[]? audioLanguages = null)
    {
        File.WriteAllBytes(Path.Combine(_root, fileName), new byte[16]);
        var movie = new Movie
        {
            Id = id,
            RadarrId = id,
            Title = $"movie {id}",
            Path = _root,
            FileName = fileName,
            DateAdded = DateTimeOffset.UtcNow,
            AudioLanguages = audioLanguages
        };
        _dbContext.Movies.Add(movie);
        await _dbContext.SaveChangesAsync();
        return movie;
    }

    private async Task<Episode> SeedEpisodeAsync(int id, string fileName, string[]? audioLanguages = null)
    {
        // Production convention (EpisodeSync): Path is the episode directory,
        // FileName the file name without extension.
        File.WriteAllBytes(Path.Combine(_root, fileName), new byte[16]);
        var show = new Show { SonarrId = 1, Title = "Show", Path = "/tv/Show", DateAdded = DateTimeOffset.UtcNow };
        var season = new Season { SeasonNumber = 1, Show = show, IncludeInTranslation = true };
        show.Seasons.Add(season);
        var episode = new Episode
        {
            Id = id,
            SonarrId = id,
            EpisodeNumber = id,
            Title = $"episode {id}",
            Path = _root,
            FileName = fileName,
            Season = season,
            DateAdded = DateTimeOffset.UtcNow,
            AudioLanguages = audioLanguages
        };
        season.Episodes.Add(episode);
        _dbContext.Shows.Add(show);
        await _dbContext.SaveChangesAsync();
        return episode;
    }

    [Fact]
    public async Task NullAudioLanguagesColumn_IsBackfilledFromTheAudioStreams()
    {
        await SeedMovieAsync(1, "movie1.mkv");

        var summary = await CreateService().ScanAll();

        Assert.Equal(1, summary.Probed);
        var stored = await _dbContext.Movies.AsNoTracking().SingleAsync(movie => movie.Id == 1);
        Assert.Equal(new[] { "zh", "en" }, stored.AudioLanguages);

        // The marker/mtime increment applies to the piggyback too: a second
        // run probes nothing and keeps the value stable.
        _probedPaths.Clear();
        var second = await CreateService().ScanAll();
        Assert.Equal(1, second.Skipped);
        Assert.Empty(_probedPaths);
        stored = await _dbContext.Movies.AsNoTracking().SingleAsync(movie => movie.Id == 1);
        Assert.Equal(new[] { "zh", "en" }, stored.AudioLanguages);
    }

    [Fact]
    public async Task SyncWrittenAudioLanguages_AreNeverOverwrittenByTheProbe()
    {
        // *arr outranks the local probe — a sync-written value stays even
        // when ffprobe reports different languages.
        await SeedMovieAsync(1, "movie1.mkv", audioLanguages: ["en"]);

        await CreateService().ScanAll();

        var stored = await _dbContext.Movies.AsNoTracking().SingleAsync(movie => movie.Id == 1);
        Assert.Equal(new[] { "en" }, stored.AudioLanguages);
    }

    [Fact]
    public async Task UnrecognizedAudioLanguages_LeaveTheColumnNull_NeverAnEmptyArray()
    {
        var undReport =
            """
            { "streams": [
                { "index": 0, "codec_name": "h264", "codec_type": "video" },
                { "index": 1, "codec_name": "eac3", "codec_type": "audio", "tags": { "language": "und" } }
            ] }
            """;
        await SeedMovieAsync(1, "movie1.mkv");

        await CreateService(undReport).ScanAll();

        var stored = await _dbContext.Movies.AsNoTracking().SingleAsync(movie => movie.Id == 1);
        Assert.Null(stored.AudioLanguages);
    }

    [Fact]
    public async Task Episodes_GetTheSamePiggyback_AsMovies()
    {
        await SeedEpisodeAsync(1, "show.s01e01.mkv");

        await CreateService().ScanAll();

        var stored = await _dbContext.Episodes.AsNoTracking().SingleAsync(episode => episode.Id == 1);
        Assert.Equal(new[] { "zh", "en" }, stored.AudioLanguages);
    }

    [Fact]
    public async Task SubtitleTrackRows_StayIdenticalToTheLegacyBehavior()
    {
        await SeedMovieAsync(1, "movie1.mkv");

        await CreateService().ScanAll();

        var rows = await _dbContext.MediaSubtitleTracks
            .AsNoTracking()
            .Where(track => track.MediaType == MediaType.Movie && track.MediaId == 1)
            .OrderBy(track => track.StreamIndex)
            .ToListAsync();

        // One subtitle track + one scan marker, exactly as before the codec_type
        // split — the video and audio streams of the report never leak in.
        Assert.Equal(2, rows.Count);

        var subtitle = rows.Single(track => track.StreamIndex != MediaSubtitleTrack.ScanMarkerStreamIndex);
        Assert.Equal(3, subtitle.StreamIndex);
        Assert.Equal("zh", subtitle.Language);              // chi folds onto zh
        Assert.Equal("chi", subtitle.RawLanguage);
        Assert.Equal("简体中文", subtitle.Title);
        Assert.Equal("srt", subtitle.Codec);                // subrip → srt
        Assert.True(subtitle.IsTextBased);
        Assert.Equal(Path.Combine(_root, "movie1.mkv"), subtitle.FilePath);
        Assert.NotNull(subtitle.MediaHash);

        var marker = rows.Single(track => track.StreamIndex == MediaSubtitleTrack.ScanMarkerStreamIndex);
        Assert.Equal(MediaSubtitleTrack.ScanMarkerStreamIndex, marker.StreamIndex);
        Assert.Equal(subtitle.MediaHash, marker.MediaHash);
    }

    [Fact]
    public async Task TwoMoviesWithDifferentLanguages_BothGetTheirOwnValue()
    {
        await SeedMovieAsync(1, "movie1.mkv");
        await SeedMovieAsync(2, "movie2.mkv");

        var service = new EmbeddedTrackService(
            _dbContext,
            NullLogger<EmbeddedTrackService>.Instance,
            ffprobeRunner: (filePath, _) =>
            {
                _probedPaths.Add(filePath);
                return Task.FromResult(filePath.EndsWith("movie1.mkv", StringComparison.Ordinal)
                    ? MixedContainerReport
                    : """{ "streams": [ { "index": 1, "codec_type": "audio", "tags": { "language": "jpn" } } ] }""");
            });
        await service.ScanAll();

        var first = await _dbContext.Movies.AsNoTracking().SingleAsync(movie => movie.Id == 1);
        var second = await _dbContext.Movies.AsNoTracking().SingleAsync(movie => movie.Id == 2);
        Assert.Equal(new[] { "zh", "en" }, first.AudioLanguages);
        Assert.Equal(new[] { "ja" }, second.AudioLanguages);
    }
}
