using System;
using System.Collections.Generic;
using System.Data.Common;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Lingarr.Core.Data;
using Lingarr.Core.Entities;
using Lingarr.Core.Enum;
using Lingarr.Server.Models;
using Lingarr.Server.Modules.Settings;
using Lingarr.Server.Modules.SubtitleSearch.Requests;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;

namespace Lingarr.Server.Tests.Modules.SubtitleSearch;

/// <summary>
/// Request building from library entities (tech design ②/⑤, M3
/// acceptance): Movie and Episode field mapping — episodes resolve
/// Year/ImdbId through Season→Show in one query — plus the lazy hash rule:
/// hashes are computed only when the queried source set consumes them, and
/// the second build for the same file does zero IO (the compute delegate
/// counts).
/// </summary>
public class SubtitleSearchRequestBuilderTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly BuilderDbContext _dbContext;
    private readonly Mock<ISettingService> _settingService = new();
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lingarr-reqbuilder-" + Guid.NewGuid().ToString("N"));

    public SubtitleSearchRequestBuilderTests()
    {
        Directory.CreateDirectory(_root);
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _dbContext = new BuilderDbContext(new DbContextOptionsBuilder<LingarrDbContext>()
            .UseSqlite(_connection)
            .Options);
        _dbContext.Database.EnsureCreated();

        var show = new Show
        {
            SonarrId = 1, Title = "Series", Path = "/media/series", DateAdded = DateTimeOffset.UtcNow,
            Year = 2015, ImdbId = "tt2222222"
        };
        var season = new Season { SeasonNumber = 2, ShowId = show.Id, Show = show };
        _dbContext.Shows.Add(show);
        _dbContext.Seasons.Add(season);
        _dbContext.Episodes.Add(new Episode
        {
            SonarrId = 10, EpisodeNumber = 5, Title = "The Episode",
            Path = Path.Combine(_root, "series"), FileName = "Series.S02E05.1080p.mkv",
            DateAdded = DateTimeOffset.UtcNow, SeasonId = season.Id, Season = season
        });
        _dbContext.Movies.Add(new Movie
        {
            RadarrId = 1, Title = "Film", Path = _root, FileName = "Film.2019.WEBDL-1080p.mkv",
            DateAdded = DateTimeOffset.UtcNow, Year = 2019, ImdbId = "tt1111111"
        });
        _dbContext.SaveChangesAsync().GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        _connection.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private sealed class BuilderDbContext(DbContextOptions options) : LingarrDbContext(options);

    private SubtitleSearchRequestBuilder Create(LazyMediaHasher hasher) =>
        new(_dbContext, _settingService.Object, hasher);

    private void WithLanguages(params string[] targetAndSourceCodes)
    {
        // Target/source JSON shape: the codes drive the language union.
        _settingService
            .Setup(service => service.GetSettingAsJson<TargetLanguage>(It.IsAny<string>()))
            .ReturnsAsync(targetAndSourceCodes.Take(1)
                .Select(code => new TargetLanguage { Name = code, Code = code }).ToList());
        _settingService
            .Setup(service => service.GetSettingAsJson<Lingarr.Contracts.Models.SourceLanguage>(It.IsAny<string>()))
            .ReturnsAsync(targetAndSourceCodes.Skip(1)
                .Select(code => new Lingarr.Contracts.Models.SourceLanguage { Name = code, Code = code })
                .ToList());
    }

    [Fact]
    public async Task Build_MovieMapsEveryField()
    {
        WithLanguages("zh", "en");
        var videoFile = Path.Combine(_root, "Film.2019.WEBDL-1080p.mkv");
        await File.WriteAllTextAsync(videoFile, "video-bytes");
        var videoHashes = 0;
        var hasher = new LazyMediaHasher(
            videoHashComputer: _ => { videoHashes++; return "abc123"; },
            shooterHashComputer: _ => "shoot");

        var built = await Create(hasher).BuildAsync(
            MediaType.Movie, MediaIdOf<Movie>(),
            providersToQuery: ["opensubtitles", "shooter"],
            languagesOverride: null);

        Assert.NotNull(built);
        var request = built!.Request;
        Assert.Equal("Film", request.Title);
        Assert.Equal(2019, request.Year);
        Assert.Null(request.Season);
        Assert.Null(request.Episode);
        // ReleaseName = the video file name (scene-name dots preserved).
        Assert.Equal("Film.2019.WEBDL-1080p.mkv", request.ReleaseName);
        Assert.Equal("tt1111111", request.ImdbId);
        Assert.Equal("abc123", request.VideoHash);
        Assert.Equal("shoot", request.ShooterFileHash);
        Assert.Equal(videoFile, built.MediaFilePath);
        // The override-free path falls back to the settings union (zh ∪ en).
        Assert.Equal(new[] { "zh", "en" }, request.Languages);
        Assert.Equal(1, videoHashes);
    }

    [Fact]
    public async Task Build_EpisodeResolvesThroughSeasonShow()
    {
        WithLanguages("zh");
        var hasher = new LazyMediaHasher();

        var built = await Create(hasher).BuildAsync(
            MediaType.Episode, MediaIdOf<Episode>(), ["opensubtitles"], null);

        Assert.NotNull(built);
        var request = built!.Request;
        Assert.Equal("Series", request.Title);         // show title, not episode title
        Assert.Equal(2015, request.Year);              // show year
        Assert.Equal("tt2222222", request.ImdbId);     // show imdb
        Assert.Equal(2, request.Season);
        Assert.Equal(5, request.Episode);
        Assert.Equal("Series.S02E05.1080p.mkv", request.ReleaseName);
        // No video file exists on disk (only the directory) → no hash, no
        // crash: the missing file is a gap, not an error.
        Assert.Null(request.VideoHash);
    }

    [Fact]
    public async Task Build_LanguageOverrideReplacesTheUnion()
    {
        WithLanguages("zh", "en", "ja");
        var hasher = new LazyMediaHasher();

        var built = await Create(hasher).BuildAsync(
            MediaType.Movie, MediaIdOf<Movie>(), ["subsource"], ["ko"]);

        Assert.Equal(new[] { "ko" }, built!.Request.Languages);
    }

    [Fact]
    public async Task Build_HashIsLazy_AgainstTheQueriedSourceSet()
    {
        // Real-cause note (unskipped): LazyMediaHasher skips the compute
        // delegate entirely when the media file does not exist — the fixture
        // never wrote the video file, so every hash assertion below used to
        // see null/0. The file must actually exist for laziness to be about
        // the source set, not about a missing file.
        var videoFile = Path.Combine(_root, "Film.2019.WEBDL-1080p.mkv");
        await File.WriteAllTextAsync(videoFile, "lazy-hash-bytes");

        WithLanguages("zh");
        var videoHashes = 0;
        var shooterHashes = 0;
        var hasher = new LazyMediaHasher(
            videoHashComputer: _ => { videoHashes++; return "hash"; },
            shooterHashComputer: _ => { shooterHashes++; return "shoot"; });

        var builder = Create(hasher);

        // Sources that consume neither hash → zero hash IO.
        var none = await builder.BuildAsync(MediaType.Movie, MediaIdOf<Movie>(), ["subsource", "subdl"], null);
        Assert.Null(none!.Request.VideoHash);
        Assert.Null(none.Request.ShooterFileHash);
        Assert.Equal(0, videoHashes);
        Assert.Equal(0, shooterHashes);

        // Only opensubtitles → only the moviehash.
        var osOnly = await builder.BuildAsync(MediaType.Movie, MediaIdOf<Movie>(), ["opensubtitles"], null);
        Assert.Equal("hash", osOnly!.Request.VideoHash);
        Assert.Null(osOnly.Request.ShooterFileHash);
        Assert.Equal(1, videoHashes);
        Assert.Equal(0, shooterHashes);
    }

    [Fact]
    public async Task Build_QueryOverridesTheDerivedTitle_KeepingEpisodeContext()
    {
        WithLanguages("zh");
        var hasher = new LazyMediaHasher();
        var builder = Create(hasher);

        // A manual-search keyword replaces the derived library title as the
        // search keyword; year/season/episode context stays resolved from
        // the library entity.
        var movie = await builder.BuildAsync(
            MediaType.Movie, MediaIdOf<Movie>(), ["subsource"], null,
            query: "Alternate.Release.Name");
        Assert.Equal("Alternate.Release.Name", movie!.Request.Title);
        Assert.Equal(2019, movie.Request.Year);           // library year kept
        Assert.Equal("tt1111111", movie.Request.ImdbId);  // library imdb kept

        var episode = await builder.BuildAsync(
            MediaType.Episode, MediaIdOf<Episode>(), ["subsource"], null,
            query: "  Spaced.Out.Keyword  ");
        Assert.Equal("Spaced.Out.Keyword", episode!.Request.Title); // trimmed
        Assert.Equal(2015, episode.Request.Year);
        Assert.Equal(2, episode.Request.Season);
        Assert.Equal(5, episode.Request.Episode);
        Assert.Equal("Series.S02E05.1080p.mkv", episode.Request.ReleaseName);

        // Blank query is "no override" — the derived title survives.
        var untouched = await builder.BuildAsync(
            MediaType.Movie, MediaIdOf<Movie>(), ["subsource"], null, query: "   ");
        Assert.Equal("Film", untouched!.Request.Title);
    }

    [Fact]
    public async Task Build_QueryWithTrailingSeasonEpisodeTag_StripsItAndKeepsLibraryContext()
    {
        // R3b: the front end pre-fills "Show Name S01E01" as the query — the
        // trailing tag is stripped from the keyword (so "Show S01E01" is
        // semantically equal to the default derived request on every
        // provider), the library's structured season/episode WIN over the
        // tag's numbers, and a movie request never gains episode fields.
        WithLanguages("zh");
        var hasher = new LazyMediaHasher();
        var builder = Create(hasher);

        var episode = await builder.BuildAsync(
            MediaType.Episode, MediaIdOf<Episode>(), ["subsource"], null,
            query: "Series S01E01");
        Assert.Equal("Series", episode!.Request.Title); // tag stripped off the keyword
        Assert.Equal(2, episode.Request.Season);        // library S02 wins over the tag's S01
        Assert.Equal(5, episode.Request.Episode);       // library E05 wins over the tag's E01
        Assert.Equal(2015, episode.Request.Year);       // show context preserved

        var movie = await builder.BuildAsync(
            MediaType.Movie, MediaIdOf<Movie>(), ["subsource"], null,
            query: "Alternate Title S02E03");
        Assert.Equal("Alternate Title", movie!.Request.Title);
        Assert.Null(movie.Request.Season);   // a movie query is never episode-ified
        Assert.Null(movie.Request.Episode);
    }

    [Fact]
    public async Task Build_SecondBuildOfTheSameFileDoesZeroHashIO()
    {
        WithLanguages("zh");
        var videoFile = Path.Combine(_root, "Film.2019.WEBDL-1080p.mkv");
        await File.WriteAllTextAsync(videoFile, "stable-bytes");
        var videoHashes = 0;
        var hasher = new LazyMediaHasher(videoHashComputer: _ => { videoHashes++; return "hash"; });
        var builder = Create(hasher);

        await builder.BuildAsync(MediaType.Movie, MediaIdOf<Movie>(), ["opensubtitles"], null);
        await builder.BuildAsync(MediaType.Movie, MediaIdOf<Movie>(), ["opensubtitles"], null);
        await builder.BuildAsync(MediaType.Movie, MediaIdOf<Movie>(), ["opensubtitles", "shooter"], null);

        // One physical compute across three builds (the third adds shooter,
        // which never had a compute delegate invoked).
        Assert.Equal(1, videoHashes);
    }

    [Fact]
    public async Task Build_EditingTheFileInvalidatesTheCache()
    {
        WithLanguages("zh");
        var videoFile = Path.Combine(_root, "Film.2019.WEBDL-1080p.mkv");
        await File.WriteAllTextAsync(videoFile, "before");
        var videoHashes = 0;
        var hasher = new LazyMediaHasher(videoHashComputer: _ => { videoHashes++; return "hash"; });
        var builder = Create(hasher);

        await builder.BuildAsync(MediaType.Movie, MediaIdOf<Movie>(), ["opensubtitles"], null);
        File.SetLastWriteTimeUtc(videoFile, DateTime.UtcNow.AddMinutes(5));
        await builder.BuildAsync(MediaType.Movie, MediaIdOf<Movie>(), ["opensubtitles"], null);

        Assert.Equal(2, videoHashes);
    }

    [Fact]
    public async Task Build_UnknownMediaOrEmptyLanguages_ReturnsNull()
    {
        WithLanguages("zh");
        var hasher = new LazyMediaHasher();
        var builder = Create(hasher);

        Assert.Null(await builder.BuildAsync(MediaType.Movie, 999999, ["opensubtitles"], null));
        Assert.Null(await builder.BuildAsync(MediaType.Show, 1, ["opensubtitles"], null));

        // No targets/sources configured and no override: nothing to search.
        _settingService
            .Setup(service => service.GetSettingAsJson<TargetLanguage>(It.IsAny<string>()))
            .ReturnsAsync(new List<TargetLanguage>());
        _settingService
            .Setup(service => service.GetSettingAsJson<Lingarr.Contracts.Models.SourceLanguage>(It.IsAny<string>()))
            .ReturnsAsync(new List<Lingarr.Contracts.Models.SourceLanguage>());
        Assert.Null(await builder.BuildAsync(MediaType.Movie, MediaIdOf<Movie>(), ["opensubtitles"], null));
    }

    private int MediaIdOf<TEntity>() where TEntity : class
    {
        return typeof(TEntity) == typeof(Movie)
            ? _dbContext.Movies.AsNoTracking().Single().Id
            : _dbContext.Episodes.AsNoTracking().Single().Id;
    }

    // --- persistent video-hash write-back (design ⑧ polish + M0011: a fresh
    // moviehash lands in the dedicated video_hash column — NULL-guarded,
    // fire-and-forget. media_hash is fully owned by the translation pipeline
    // and never touched here).

    [Fact]
    public async Task WriteBackVideoHash_FillsOnlyRowsWhoseVideoHashIsNull()
    {
        // Production write-side conventions of this test's DbContext: the
        // seeded ctor movie ("Film", Path=_root) carries the empty-string
        // media_hash sentinel the sync/translation writers leave behind;
        // three explicit rows pin the video_hash NULL/value cases.
        var nullHashMovie = new Movie
        {
            RadarrId = 2, Title = "NullHash", Path = _root, FileName = "NullHash.2020.mkv",
            DateAdded = DateTimeOffset.UtcNow, Year = 2020, MediaHash = null, VideoHash = null
        };
        var filledMovie = new Movie
        {
            RadarrId = 3, Title = "Filled", Path = _root, FileName = "Filled.2021.mkv",
            DateAdded = DateTimeOffset.UtcNow, Year = 2021,
            MediaHash = "translation-pipeline-hash", VideoHash = "existing-video-hash"
        };
        var nullHashEpisode = new Episode
        {
            SonarrId = 11, EpisodeNumber = 6, Title = "NullHash Episode",
            // A tracked instance keeps EF's identity map happy; the nav sets
            // SeasonId, and setting both from different instances conflicts.
            Season = _dbContext.Seasons.Single(),
            Path = Path.Combine(_root, "series"), FileName = "Series.S02E06.1080p.mkv",
            DateAdded = DateTimeOffset.UtcNow,
            MediaHash = null, VideoHash = null
        };
        _dbContext.Movies.AddRange(nullHashMovie, filledMovie);
        _dbContext.Episodes.Add(nullHashEpisode);
        await _dbContext.SaveChangesAsync();

        // Path round-trip: the builder's ResolveMediaFile combines the very
        // (Path, FileName) columns the write-back matches on.
        var written = await LazyMediaHasher.WriteBackVideoHashAsync(
            _dbContext, Path.Combine(_root, "NullHash.2020.mkv"), "os-moviehash");
        Assert.Equal(1, written);
        var writtenMovie = await _dbContext.Movies.AsNoTracking().SingleAsync(m => m.RadarrId == 2);
        Assert.Equal("os-moviehash", writtenMovie.VideoHash);
        Assert.Null(writtenMovie.MediaHash); // ownership: the translation pipeline's column is untouched

        // Episode rows share the column: one write for its own file.
        var episodeWrite = await LazyMediaHasher.WriteBackVideoHashAsync(
            _dbContext, Path.Combine(_root, "series", "Series.S02E06.1080p.mkv"), "os-moviehash-ep");
        Assert.Equal(1, episodeWrite);
        var writtenEpisode = await _dbContext.Episodes.AsNoTracking().SingleAsync(e => e.SonarrId == 11);
        Assert.Equal("os-moviehash-ep", writtenEpisode.VideoHash);
        Assert.Null(writtenEpisode.MediaHash);

        // An existing video_hash is NEVER overwritten (the guard now only
        // keeps duplicate lazy computes from racing each other).
        var refused = await LazyMediaHasher.WriteBackVideoHashAsync(
            _dbContext, Path.Combine(_root, "Filled.2021.mkv"), "new-hash");
        Assert.Equal(0, refused);
        var refusedMovie = await _dbContext.Movies.AsNoTracking().SingleAsync(m => m.RadarrId == 3);
        Assert.Equal("existing-video-hash", refusedMovie.VideoHash);
        Assert.Equal("translation-pipeline-hash", refusedMovie.MediaHash);

        // The media_hash empty-string sentinel no longer guards anything —
        // video_hash has no sentinel, so the ctor movie's NULL column is a
        // legitimate write target while its media_hash stays exactly as the
        // translation pipeline left it.
        var sentinelBypassed = await LazyMediaHasher.WriteBackVideoHashAsync(
            _dbContext, Path.Combine(_root, "Film.2019.WEBDL-1080p.mkv"), "new-hash");
        Assert.Equal(1, sentinelBypassed);
        var sentinelMovie = await _dbContext.Movies.AsNoTracking().SingleAsync(m => m.RadarrId == 1);
        Assert.Equal("new-hash", sentinelMovie.VideoHash);
        Assert.Equal(string.Empty, sentinelMovie.MediaHash);

        // A path matching no library row is a no-op, not an error.
        Assert.Equal(0, await LazyMediaHasher.WriteBackVideoHashAsync(
            _dbContext, Path.Combine(_root, "Unknown.2022.mkv"), "os-moviehash"));
    }

    [Fact]
    public async Task LazyMediaHasher_TriggersTheWriteBackDelegateOnFreshComputeOnly()
    {
        var videoFile = Path.Combine(_root, "WriteBack.2019.mkv");
        await File.WriteAllTextAsync(videoFile, "write-back-bytes");
        var recorded = new List<(string FilePath, string Hash)>();
        var gate = new SemaphoreSlim(1, 1);
        var hasher = new LazyMediaHasher(
            videoHashComputer: _ => "computed-hash",
            videoHashWriteBack: (filePath, hash) =>
            {
                gate.Wait();
                recorded.Add((filePath, hash));
                gate.Release();
                return Task.CompletedTask;
            });

        var hash = hasher.VideoHash(videoFile);
        Assert.Equal("computed-hash", hash);

        // Missing file: no hash, no write-back.
        Assert.Null(hasher.VideoHash(Path.Combine(_root, "missing.mkv")));
        // Shooter hash computes but never persists (the column holds the
        // OpenSubtitles moviehash only).
        _ = hasher.ShooterHash(videoFile);

        // The fire-and-forget write-back of the successful compute runs on
        // the thread pool — give it a moment to land before asserting.
        for (var attempt = 0; attempt < 50 && recorded.Count == 0; attempt++)
        {
            await Task.Delay(10);
        }

        var writeBack = Assert.Single(recorded);
        Assert.Equal(videoFile, writeBack.FilePath);
        Assert.Equal("computed-hash", writeBack.Hash);
    }

    [Fact]
    public async Task Build_WhisperRoundCarriesTheMediaFileAndAudioLanguages()
    {
        // B04 lazy gate (hash-style): the whisper source consumes the
        // container file itself and the media's audio languages — a round
        // that queries whisper carries both, every other round carries
        // neither (no whisper column reaches the request).
        WithLanguages("zh", "en");
        // tracked entities (seeded by the ctor) mutated in place — no
        // detached-copy attach conflict
        _dbContext.Movies.Single().AudioLanguages = ["en"];
        _dbContext.Episodes.Single().AudioLanguages = ["ja"];
        _dbContext.SaveChanges();

        var movieId = MediaIdOf<Movie>();
        var episodeId = MediaIdOf<Episode>();
        var built = await Create(new LazyMediaHasher()).BuildAsync(
            MediaType.Movie, movieId, ["subsource", "whisper"], null);
        Assert.Equal(
            Path.Combine(_root, "Film.2019.WEBDL-1080p.mkv"),
            built!.Request.MediaFilePath);
        Assert.Equal(new[] { "en" }, built.Request.AudioLanguages);

        // episode form resolves through Season→Show the same way
        var episodeBuilt = await Create(new LazyMediaHasher()).BuildAsync(
            MediaType.Episode, episodeId, ["whisper"], null);
        Assert.NotNull(episodeBuilt!.Request.MediaFilePath);
        Assert.Equal(new[] { "ja" }, episodeBuilt.Request.AudioLanguages);

        // a round without whisper pays nothing
        var plain = await Create(new LazyMediaHasher()).BuildAsync(
            MediaType.Movie, movieId, ["subsource"], null);
        Assert.Null(plain!.Request.MediaFilePath);
        Assert.Null(plain.Request.AudioLanguages);
    }
}
