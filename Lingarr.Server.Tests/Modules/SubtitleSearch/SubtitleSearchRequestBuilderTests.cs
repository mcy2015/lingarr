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
}
