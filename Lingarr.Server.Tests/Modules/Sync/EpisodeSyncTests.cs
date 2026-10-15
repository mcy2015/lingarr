using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Lingarr.Core.Data;
using Lingarr.Core.Entities;
using Lingarr.Core.Enum;
using Lingarr.Server.Models.Integrations;
using Lingarr.Server.Modules.Sync;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Lingarr.Server.Tests.Modules.Sync;

/// <summary>
/// Pins the add/update/remove/unchanged diff semantics of the episode sync now that
/// paths come from the episodeFile object embedded in the season episode response
/// (previously one HTTP call per episode).
/// </summary>
public class EpisodeSyncTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly LingarrDbContext _context;
    private readonly EpisodeSync _episodeSync;

    public EpisodeSyncTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        var options = new DbContextOptionsBuilder<LingarrDbContext>()
            .UseSqlite(_connection)
            .Options;
        _context = new LingarrDbContext(options);
        _context.Database.EnsureCreated();
        _episodeSync = new EpisodeSync(new PathConversionService(_context));
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    private async Task<Season> SeedSeasonAsync(params int[] sonarrEpisodeIds)
    {
        var show = new Show
        {
            SonarrId = 1,
            Title = "Show",
            Path = "/tv/Show",
            DateAdded = DateTimeOffset.UtcNow
        };
        var season = new Season { SeasonNumber = 1, Show = show, IncludeInTranslation = true };
        // Reachable from the show graph, otherwise Shows.Add never discovers the
        // season/episodes and SaveChanges persists the show alone.
        show.Seasons.Add(season);
        foreach (var sonarrId in sonarrEpisodeIds)
        {
            // Written with the production convention: Path is the episode directory,
            // FileName the file name without extension.
            season.Episodes.Add(new Episode
            {
                SonarrId = sonarrId,
                EpisodeNumber = sonarrId,
                Title = $"Old title {sonarrId}",
                FileName = $"old.{sonarrId}",
                Path = "/tv/Show/Season 1",
                Season = season,
                DateAdded = null
            });
        }

        _context.Shows.Add(show);
        await _context.SaveChangesAsync();
        return season;
    }

    private static SonarrEpisode SonarrEpisode(
        int id,
        string path,
        DateTimeOffset? dateAdded = null,
        bool hasFile = true,
        string? title = null) => new()
    {
        Id = id,
        EpisodeNumber = id,
        Title = title ?? $"Episode {id}",
        SeasonNumber = 1,
        HasFile = hasFile,
        EpisodeFile = hasFile
            ? new SonarrEpisodeFile { Path = path, DateAdded = dateAdded }
            : null
    };

    [Fact]
    public async Task SyncEpisodes_AddsNewEpisodesFromEmbeddedEpisodeFile()
    {
        var season = await SeedSeasonAsync();
        var dateAdded = new DateTimeOffset(2026, 2, 3, 4, 5, 6, TimeSpan.Zero);
        var episodes = new List<SonarrEpisode>
        {
            SonarrEpisode(10, "/tv/Show/Season 1/Show.S01E10.mkv", dateAdded)
        };

        await _episodeSync.SyncEpisodes(season, episodes, defaultInclude: false);
        await _context.SaveChangesAsync();

        var stored = await _context.Episodes.AsNoTracking().SingleAsync(e => e.SonarrId == 10);
        Assert.Equal("Show.S01E10", stored.FileName);
        Assert.Equal("/tv/Show/Season 1", stored.Path);
        Assert.Equal("Episode 10", stored.Title);
        Assert.Equal(dateAdded, stored.DateAdded);
        Assert.False(stored.IncludeInTranslation);
    }

    [Fact]
    public async Task SyncEpisodes_UpdatesExistingEpisodeInPlace()
    {
        var season = await SeedSeasonAsync(10);
        var newDate = new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.Zero);
        var episodes = new List<SonarrEpisode>
        {
            SonarrEpisode(10, "/tv/Show/Season 2/Show.S02E10.mkv", newDate)
        };

        await _episodeSync.SyncEpisodes(season, episodes);
        await _context.SaveChangesAsync();

        Assert.Equal(1, await _context.Episodes.CountAsync());
        var stored = await _context.Episodes.AsNoTracking().SingleAsync(e => e.SonarrId == 10);
        Assert.Equal("Episode 10", stored.Title);
        Assert.Equal("Show.S02E10", stored.FileName);
        Assert.Equal("/tv/Show/Season 2", stored.Path);
        Assert.Equal(newDate, stored.DateAdded);
    }

    [Fact]
    public async Task SyncEpisodes_NoChanges_LeavesEntityUnchanged()
    {
        var season = await SeedSeasonAsync(10);
        // The response reproduces exactly the stored values (title, file name,
        // directory, no dateAdded), so the sync must not dirty the entity.
        var episodes = new List<SonarrEpisode>
        {
            SonarrEpisode(10, "/tv/Show/Season 1/old.10.mkv", title: "Old title 10")
        };

        await _episodeSync.SyncEpisodes(season, episodes);

        // Same values assigned -> DetectChanges finds nothing -> no UPDATE statement.
        var entry = _context.Entry(season.Episodes.Single(e => e.SonarrId == 10));
        Assert.Equal(EntityState.Unchanged, entry.State);
    }

    [Fact]
    public async Task SyncEpisodes_RemovesEpisodesMissingFromResponse()
    {
        var season = await SeedSeasonAsync(10, 11, 12);
        var episodes = new List<SonarrEpisode>
        {
            SonarrEpisode(10, "/tv/Show/Season 1/Show.S01E10.mkv"),
            SonarrEpisode(12, "/tv/Show/Season 1/Show.S01E12.mkv")
        };

        await _episodeSync.SyncEpisodes(season, episodes);
        await _context.SaveChangesAsync();

        var remaining = await _context.Episodes.Select(e => e.SonarrId).OrderBy(id => id).ToListAsync();
        Assert.Equal(new List<int> { 10, 12 }, remaining);
    }

    [Fact]
    public async Task SyncEpisodes_NullEpisodes_IsNoOp()
    {
        var season = await SeedSeasonAsync(10);

        await _episodeSync.SyncEpisodes(season, null);

        Assert.Single(season.Episodes);
        Assert.Equal(1, await _context.Episodes.CountAsync());
    }

    [Fact]
    public async Task SyncEpisodes_EpisodeWithoutFile_IsNotAddedButRetainedWhenKnown()
    {
        // hasFile=false episodes are skipped for upsert, but still count as
        // "existing in Sonarr" so a known DB episode is not orphan-removed.
        var season = await SeedSeasonAsync(10);
        var episodes = new List<SonarrEpisode>
        {
            SonarrEpisode(10, string.Empty, hasFile: false),
            SonarrEpisode(11, "/tv/Show/Season 1/Show.S01E11.mkv", hasFile: false)
        };

        await _episodeSync.SyncEpisodes(season, episodes);
        await _context.SaveChangesAsync();

        Assert.Equal(1, await _context.Episodes.CountAsync());
        Assert.NotNull(await _context.Episodes.SingleOrDefaultAsync(e => e.SonarrId == 10));
        Assert.Null(await _context.Episodes.SingleOrDefaultAsync(e => e.SonarrId == 11));
    }

    [Fact]
    public async Task SyncEpisodes_AppliesConfiguredPathMapping()
    {
        _context.PathMappings.Add(new PathMapping
        {
            SourcePath = "/tv",
            DestinationPath = "/mnt/nas/tv",
            MediaType = MediaType.Show
        });
        await _context.SaveChangesAsync();

        var season = await SeedSeasonAsync();
        var episodes = new List<SonarrEpisode>
        {
            SonarrEpisode(10, "/tv/Show/Season 1/Show.S01E10.mkv")
        };

        await _episodeSync.SyncEpisodes(season, episodes);
        await _context.SaveChangesAsync();

        var stored = await _context.Episodes.AsNoTracking().SingleAsync(e => e.SonarrId == 10);
        Assert.Equal("/mnt/nas/tv/Show/Season 1", stored.Path);
    }
}
