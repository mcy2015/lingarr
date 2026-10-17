using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Lingarr.Core.Configuration;
using Lingarr.Core.Data;
using Lingarr.Core.Entities;
using Lingarr.Core.Enum;
using Lingarr.Server.Models;
using Lingarr.Server.Modules.Settings;
using Lingarr.Server.Modules.Subtitle;
using Lingarr.Server.Modules.SubtitleSearch.Pipeline;
using Lingarr.Server.Modules.Translation;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Lingarr.Server.Tests.Modules.SubtitleSearch;

/// <summary>
/// Walkthrough fix B1: the single-media refresher writes exactly what the
/// daily statistics scan would write — movies get their coverage snapshot +
/// need flags, episodes resolve to the owning show whose aggregated
/// SubtitleCoverageJson is rebuilt from fresh season scans together with
/// every episode flag — so a subtitle that lands on disk (download or
/// translation writeback) surfaces immediately instead of on the next daily
/// run. Real SQLite because the refresher writes through ExecuteUpdate; the
/// subtitle service is the production one so directory scans, filename
/// language parsing and byte statting run exactly as in production.
/// </summary>
public class SubtitleCoverageRefresherTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly LingarrDbContext _dbContext;
    private readonly InMemorySettingService _settings = new();
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "lingarr-refresher-" + Guid.NewGuid().ToString("N"));

    private readonly SubtitleCoverageRefresher _refresher;

    public SubtitleCoverageRefresherTests()
    {
        Directory.CreateDirectory(_root);
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _dbContext = new RefresherDbContext(new DbContextOptionsBuilder<LingarrDbContext>()
            .UseSqlite(_connection)
            .UseSnakeCaseNamingConvention()
            .Options);
        _dbContext.Database.EnsureCreated();

        // zh is the deployment target: covering it flips the need flag.
        _settings.SetSetting(
            SettingKeys.Translation.TargetLanguages,
            JsonSerializer.Serialize(new List<TargetLanguage>
            {
                new() { Name = "Chinese", Code = "zh" }
            })).GetAwaiter().GetResult();

        var subtitleService = new SubtitleService(
            NullLogger<SubtitleService>.Instance,
            new LanguageCodeService(),
            _settings);
        _refresher = new SubtitleCoverageRefresher(
            _dbContext, subtitleService, _settings, NullLogger<SubtitleCoverageRefresher>.Instance);
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        _connection.Dispose();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // temp cleanup is best-effort
        }

        GC.SuppressFinalize(this);
    }

    private string WriteSubtitle(string relativeDir, string fileName, int bytes)
    {
        var directory = Path.Combine(_root, relativeDir);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, fileName);
        File.WriteAllBytes(path, new byte[bytes]);
        return directory;
    }

    // ── movie: coverage snapshot + need flags, daily-scan parity ──────

    [Fact]
    public async Task RefreshAsync_Movie_RebuildsCoverageAndFlipsTheNeedFlag()
    {
        var directory = Path.Combine(_root, "movie1");
        Directory.CreateDirectory(directory);
        var movie = new Movie
        {
            RadarrId = 1,
            Title = "Movie",
            Year = 2019,
            Path = directory,
            FileName = "Movie.2019.WEBDL-1080p.mkv",
            DateAdded = DateTimeOffset.UtcNow,
            AudioLanguages = ["en"], // not a target: missing zh means needs
            NeedsTranslation = true
        };
        _dbContext.Movies.Add(movie);
        await _dbContext.SaveChangesAsync();

        WriteSubtitle("movie1", "Movie.2019.WEBDL-1080p.en.srt", 40);
        WriteSubtitle("movie1", "Movie.2019.WEBDL-1080p.zh.srt", 100);

        await _refresher.RefreshAsync(MediaType.Movie, movie.Id, CancellationToken.None);

        var stored = await _dbContext.Movies.AsNoTracking().SingleAsync(m => m.Id == movie.Id);
        Assert.NotNull(stored.SubtitleCoverage);
        // movie coverage semantics (MediaLanguageCoverage): 0/1 per language
        Assert.Equal(1, stored.SubtitleCoverage!["zh"].Count);
        Assert.Equal(100, stored.SubtitleCoverage!["zh"].Bytes);
        Assert.Equal(1, stored.SubtitleCoverage!["en"].Count);
        Assert.Equal(40, stored.SubtitleCoverage!["en"].Bytes);
        // zh is covered and the audio is no target → no longer needing
        Assert.False(stored.NeedsTranslation);
        Assert.False(stored.NoSubtitleNeeded);
    }

    [Fact]
    public async Task RefreshAsync_MovieWithoutPath_ClearsStaleCoverage()
    {
        // parity with the daily scan's pathless-movie branch: no directory,
        // no coverage (null, not {}) and the rule judges on audio alone.
        var movie = new Movie
        {
            RadarrId = 2,
            Title = "Pathless",
            Year = 2020,
            Path = null,
            FileName = null,
            DateAdded = DateTimeOffset.UtcNow,
            SubtitleCoverageJson = "{\"zh\":{\"count\":1,\"bytes\":1}}",
            AudioLanguages = ["en"],
            NeedsTranslation = false
        };
        _dbContext.Movies.Add(movie);
        await _dbContext.SaveChangesAsync();

        await _refresher.RefreshAsync(MediaType.Movie, movie.Id, CancellationToken.None);

        var stored = await _dbContext.Movies.AsNoTracking().SingleAsync(m => m.Id == movie.Id);
        Assert.Null(stored.SubtitleCoverageJson);
        Assert.True(stored.NeedsTranslation);
    }

    // ── episode/show: the show aggregate is refreshed, no daily wait ──

    private async Task<Show> SeedShowAsync(int showId, string relativeDir, params string[] episodeFileNames)
    {
        var directory = Path.Combine(_root, relativeDir);
        Directory.CreateDirectory(directory);
        var show = new Show
        {
            Id = showId,
            SonarrId = showId,
            Title = $"show {showId}",
            Path = Path.Combine(_root, $"show{showId}-root"),
            DateAdded = DateTimeOffset.UtcNow,
            SubtitleCoverageJson = "{\"en\":{\"count\":2,\"bytes\":999}}" // stale daily snapshot
        };
        var season = new Season
        {
            Id = showId * 10,
            SeasonNumber = 1,
            Path = directory,
            ShowId = show.Id,
            Show = show
        };
        var episodeId = showId * 100;
        foreach (var fileName in episodeFileNames)
        {
            season.Episodes.Add(new Episode
            {
                Id = ++episodeId,
                SonarrId = episodeId,
                EpisodeNumber = episodeId - showId * 100,
                Title = fileName,
                Path = directory,
                FileName = fileName,
                SeasonId = season.Id,
                Season = season,
                NeedsTranslation = true // what the last daily scan left behind
            });
        }

        show.Seasons.Add(season);
        _dbContext.Shows.Add(show);
        await _dbContext.SaveChangesAsync();
        return show;
    }

    [Fact]
    public async Task RefreshAsync_Episode_RebuildsTheShowAggregateAndEpisodeFlags()
    {
        var show = await SeedShowAsync(1, "show1/s01",
            "show1.s01e01.mkv", "show1.s01e02.mkv");
        var episodes = show.Seasons.SelectMany(s => s.Episodes).OrderBy(e => e.Id).ToList();
        WriteSubtitle("show1/s01", "show1.s01e01.zh.srt", 60);

        await _refresher.RefreshAsync(MediaType.Episode, episodes[0].Id, CancellationToken.None);

        var storedShow = await _dbContext.Shows.AsNoTracking().SingleAsync(s => s.Id == show.Id);
        Assert.NotNull(storedShow.SubtitleCoverage);
        // the stale en snapshot is gone: fresh scan attributes the one file
        // to episode 1 — zh covered for exactly one episode.
        Assert.False(storedShow.SubtitleCoverage.ContainsKey("en"));
        Assert.Equal(1, storedShow.SubtitleCoverage!["zh"].Count);
        Assert.Equal(60, storedShow.SubtitleCoverage!["zh"].Bytes);

        var storedEpisodes = await _dbContext.Episodes.AsNoTracking()
            .Where(e => e.Season.ShowId == show.Id)
            .OrderBy(e => e.Id)
            .ToListAsync();
        Assert.False(storedEpisodes[0].NeedsTranslation); // zh landed for it
        Assert.True(storedEpisodes[1].NeedsTranslation); // still missing zh
    }

    [Fact]
    public async Task RefreshAsync_ShowById_RebuildsTheSameAggregate()
    {
        var show = await SeedShowAsync(2, "show2/s01",
            "show2.s01e01.mkv", "show2.s01e02.mkv");
        WriteSubtitle("show2/s01", "show2.s01e01.zh.srt", 10);
        WriteSubtitle("show2/s01", "show2.s01e02.zh.srt", 20);

        await _refresher.RefreshAsync(MediaType.Show, show.Id, CancellationToken.None);

        var storedShow = await _dbContext.Shows.AsNoTracking().SingleAsync(s => s.Id == show.Id);
        Assert.Equal(2, storedShow.SubtitleCoverage!["zh"].Count);
        Assert.Equal(30, storedShow.SubtitleCoverage!["zh"].Bytes);

        var storedEpisodes = await _dbContext.Episodes.AsNoTracking()
            .Where(e => e.Season.ShowId == show.Id)
            .ToListAsync();
        Assert.All(storedEpisodes, episode => Assert.False(episode.NeedsTranslation));
    }

    [Fact]
    public async Task RefreshAsync_EpisodeOfPathlessSeason_KeepsStoredShowSnapshot()
    {
        // the daily scan's rule: a show whose seasons carry no path keeps
        // its stored coverage — nothing was scanned, so nothing fresher
        // exists. The episode flags still re-run on audio/override alone.
        var show = await SeedShowAsync(3, "show3/s01", "show3.s01e01.mkv");
        var season = await _dbContext.Seasons.SingleAsync(s => s.ShowId == show.Id);
        season.Path = null;
        await _dbContext.SaveChangesAsync();
        var staleJson = show.SubtitleCoverageJson;
        var episodeId = show.Seasons.SelectMany(s => s.Episodes).Single().Id;

        await _refresher.RefreshAsync(MediaType.Episode, episodeId, CancellationToken.None);

        var storedShow = await _dbContext.Shows.AsNoTracking().SingleAsync(s => s.Id == show.Id);
        Assert.Equal(staleJson, storedShow.SubtitleCoverageJson);
    }

    [Fact]
    public async Task RefreshAsync_UnknownMedia_IsASilentNoOp()
    {
        await _refresher.RefreshAsync(MediaType.Movie, 424242, CancellationToken.None);
        await _refresher.RefreshAsync(MediaType.Episode, 424243, CancellationToken.None);
        await _refresher.RefreshAsync(MediaType.Show, 424244, CancellationToken.None);
    }

    private sealed class RefresherDbContext(DbContextOptions options) : LingarrDbContext(options);
}
