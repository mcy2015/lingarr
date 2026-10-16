using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Lingarr.Core.Data;
using Lingarr.Core.Entities;
using Lingarr.Server.Models.Integrations;
using Lingarr.Server.Modules.Sync;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Lingarr.Server.Tests.Modules.Sync;

/// <summary>
/// The season path is now derived from the episodeFile object inside the pre-fetched
/// episode list — the season sync must not issue HTTP requests of its own.
/// </summary>
public class SeasonSyncTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly LingarrDbContext _context;
    private readonly SeasonSync _seasonSync;

    public SeasonSyncTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        var options = new DbContextOptionsBuilder<LingarrDbContext>()
            .UseSqlite(_connection)
            .Options;
        _context = new LingarrDbContext(options);
        _context.Database.EnsureCreated();
        _seasonSync = new SeasonSync(
            _context,
            new PathConversionService(_context),
            NullLogger<SeasonSync>.Instance);
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    private async Task<Show> SeedShowAsync()
    {
        var show = new Show
        {
            SonarrId = 1,
            Title = "Show",
            Path = "/tv/Show",
            DateAdded = DateTimeOffset.UtcNow
        };
        _context.Shows.Add(show);
        await _context.SaveChangesAsync();
        return show;
    }

    private static SonarrEpisode EpisodeWithFile(int id, string path) => new()
    {
        Id = id,
        EpisodeNumber = id,
        Title = $"Episode {id}",
        SeasonNumber = 1,
        HasFile = true,
        EpisodeFile = new SonarrEpisodeFile
        {
            Path = path,
            DateAdded = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)
        }
    };

    [Fact]
    public async Task SyncSeason_DerivesPathFromFirstEpisodeWithFile()
    {
        var show = await SeedShowAsync();
        var episodes = new List<SonarrEpisode>
        {
            EpisodeWithFile(10, "/tv/Show/Season 1/Show.S01E10.mkv"),
            EpisodeWithFile(11, "/tv/Show/Season 1/Show.S01E11.mkv")
        };

        var seasonEntity = await _seasonSync.SyncSeason(show, new SonarrSeason { SeasonNumber = 1 }, episodes);
        await _context.SaveChangesAsync();

        var stored = await _context.Seasons.AsNoTracking().SingleAsync();
        Assert.Equal("/tv/Show/Season 1", stored.Path);
        Assert.Equal(show.Id, stored.ShowId);
        Assert.Same(seasonEntity, show.Seasons.Single());
    }

    [Fact]
    public async Task SyncSeason_NormalizesWindowsPathsFromEpisodeFile()
    {
        var show = await SeedShowAsync();
        var episodes = new List<SonarrEpisode>
        {
            EpisodeWithFile(10, "C:\\tv\\Show\\Season 1\\Show.S01E10.mkv")
        };

        await _seasonSync.SyncSeason(show, new SonarrSeason { SeasonNumber = 1 }, episodes);
        await _context.SaveChangesAsync();

        var stored = await _context.Seasons.AsNoTracking().SingleAsync();
        Assert.Equal("/tv/Show/Season 1", stored.Path);
    }

    [Fact]
    public async Task SyncSeason_UpdatesExistingSeasonPath()
    {
        var show = await SeedShowAsync();
        _context.Seasons.Add(new Season
        {
            SeasonNumber = 1,
            Path = "/tv/Show/Old Season Path",
            Show = show
        });
        await _context.SaveChangesAsync();

        var episodes = new List<SonarrEpisode>
        {
            EpisodeWithFile(10, "/tv/Show/Season 1/Show.S01E10.mkv")
        };

        await _seasonSync.SyncSeason(show, new SonarrSeason { SeasonNumber = 1 }, episodes);
        await _context.SaveChangesAsync();

        Assert.Equal(1, await _context.Seasons.CountAsync());
        var stored = await _context.Seasons.AsNoTracking().SingleAsync();
        Assert.Equal("/tv/Show/Season 1", stored.Path);
    }

    [Fact]
    public async Task SyncSeason_NoEpisodesWithFile_LeavesPathEmpty()
    {
        var show = await SeedShowAsync();

        await _seasonSync.SyncSeason(show, new SonarrSeason { SeasonNumber = 1 }, new List<SonarrEpisode>());
        await _context.SaveChangesAsync();

        var stored = await _context.Seasons.AsNoTracking().SingleAsync();
        Assert.Equal(string.Empty, stored.Path);
    }

    [Fact]
    public async Task SyncSeason_NullEpisodes_LeavesPathEmpty()
    {
        var show = await SeedShowAsync();

        await _seasonSync.SyncSeason(show, new SonarrSeason { SeasonNumber = 1 }, null);
        await _context.SaveChangesAsync();

        var stored = await _context.Seasons.AsNoTracking().SingleAsync();
        Assert.Equal(string.Empty, stored.Path);
    }

    [Fact]
    public async Task SyncSeason_FirstHasFileEpisodeWithoutAPath_SkipsToTheEpisodeWithAPath()
    {
        // Audit P2-9: hasFile=true with an empty episodeFile.path is a real
        // Sonarr shape. The season directory must come from the first episode
        // that actually owns a usable file path — its own directory — instead
        // of the plain first hasFile episode (which used to fold the season
        // onto the "/Season N" placeholder while a real path sat unused).
        var show = await SeedShowAsync();
        var pathless = EpisodeWithFile(10, string.Empty);
        var withPath = EpisodeWithFile(11, "/tv/Show/Season 2/Show.S02E11.mkv");

        await _seasonSync.SyncSeason(
            show, new SonarrSeason { SeasonNumber = 2 }, new List<SonarrEpisode> { pathless, withPath });
        await _context.SaveChangesAsync();

        var stored = await _context.Seasons.AsNoTracking().SingleAsync();
        Assert.Equal("/tv/Show/Season 2", stored.Path);
    }

    [Fact]
    public async Task SyncSeason_NewSeason_HonorsDefaultInclude()
    {
        var show = await SeedShowAsync();

        await _seasonSync.SyncSeason(
            show, new SonarrSeason { SeasonNumber = 1 }, new List<SonarrEpisode>(), defaultInclude: false);
        await _context.SaveChangesAsync();

        var stored = await _context.Seasons.AsNoTracking().SingleAsync();
        Assert.False(stored.IncludeInTranslation);
    }
}
