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
        string? title = null,
        string? mediaInfoAudioLanguages = null,
        List<IntegrationLanguage>? fileLanguages = null,
        List<IntegrationLanguage>? episodeLanguages = null) => new()
    {
        Id = id,
        EpisodeNumber = id,
        Title = title ?? $"Episode {id}",
        SeasonNumber = 1,
        HasFile = hasFile,
        EpisodeFile = hasFile
            ? new SonarrEpisodeFile
            {
                Path = path,
                DateAdded = dateAdded,
                MediaInfo = mediaInfoAudioLanguages == null
                    ? null
                    : new IntegrationMediaInfo { AudioLanguages = mediaInfoAudioLanguages },
                Languages = fileLanguages
            }
            : null,
        Languages = episodeLanguages
    };

    private static List<IntegrationLanguage> Names(params string[] names) =>
        names.Select((name, index) => new IntegrationLanguage { Id = index + 1, Name = name }).ToList();

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

    // ── PRD card A: audio-language mapping ─────────────────────────

    [Fact]
    public async Task SyncEpisodes_MediaInfoTruthString_IsPreferredAndNormalized()
    {
        var season = await SeedSeasonAsync();
        var episodes = new List<SonarrEpisode>
        {
            SonarrEpisode(10, "/tv/Show/Season 1/Show.S01E10.mkv",
                mediaInfoAudioLanguages: "english / chi",
                fileLanguages: Names("French"))
        };

        await _episodeSync.SyncEpisodes(season, episodes);
        await _context.SaveChangesAsync();

        var stored = await _context.Episodes.AsNoTracking().SingleAsync(e => e.SonarrId == 10);
        Assert.Equal(["en","zh"], stored.AudioLanguages);
    }

    [Fact]
    public async Task SyncEpisodes_FileLanguagesAggregate_IsTheFallback()
    {
        var season = await SeedSeasonAsync();
        var episodes = new List<SonarrEpisode>
        {
            SonarrEpisode(10, "/tv/Show/Season 1/Show.S01E10.mkv",
                fileLanguages: Names("English", "Unknown"))
        };

        await _episodeSync.SyncEpisodes(season, episodes);
        await _context.SaveChangesAsync();

        var stored = await _context.Episodes.AsNoTracking().SingleAsync(e => e.SonarrId == 10);
        Assert.Equal(["en"], stored.AudioLanguages);
    }

    [Fact]
    public async Task SyncEpisodes_EpisodeLanguages_AreTheLastResortWithoutEpisodeFile()
    {
        // hasFile=true but no episodeFile resource — the episode-level mirror
        // is the only *arr source left.
        var season = await SeedSeasonAsync();
        var episodes = new List<SonarrEpisode>
        {
            SonarrEpisode(10, string.Empty, episodeLanguages: Names("English"))
        };

        await _episodeSync.SyncEpisodes(season, episodes);
        await _context.SaveChangesAsync();

        var stored = await _context.Episodes.AsNoTracking().SingleAsync(e => e.SonarrId == 10);
        Assert.Equal(["en"], stored.AudioLanguages);
    }

    [Fact]
    public async Task SyncEpisodes_EmptyOrUndPayload_LeavesTheColumnNull()
    {
        var season = await SeedSeasonAsync();
        var episodes = new List<SonarrEpisode>
        {
            SonarrEpisode(10, "/tv/Show/Season 1/Show.S01E10.mkv",
                mediaInfoAudioLanguages: "und",
                fileLanguages: Names("Unknown"))
        };

        await _episodeSync.SyncEpisodes(season, episodes);
        await _context.SaveChangesAsync();

        var stored = await _context.Episodes.AsNoTracking().SingleAsync(e => e.SonarrId == 10);
        Assert.Null(stored.AudioLanguages);
    }

    [Fact]
    public async Task SyncEpisodes_MissingPayload_NeverClearsAnExistingValue()
    {
        var season = await SeedSeasonAsync();
        var first = new List<SonarrEpisode>
        {
            SonarrEpisode(10, "/tv/Show/Season 1/Show.S01E10.mkv", mediaInfoAudioLanguages: "chi")
        };
        await _episodeSync.SyncEpisodes(season, first);
        await _context.SaveChangesAsync();

        // A later payload without audio data must not wipe the value the
        // first sync (or the ffprobe piggyback) wrote.
        var second = new List<SonarrEpisode>
        {
            SonarrEpisode(10, "/tv/Show/Season 1/Show.S01E10.mkv")
        };
        await _episodeSync.SyncEpisodes(season, second);
        await _context.SaveChangesAsync();

        var stored = await _context.Episodes.AsNoTracking().SingleAsync(e => e.SonarrId == 10);
        Assert.Equal(["zh"], stored.AudioLanguages);
    }
}

// ── media-file detail batch: episodeFile quality/size/mediaInfo columns ──

public class EpisodeSyncMediaFileTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly LingarrDbContext _context;
    private readonly EpisodeSync _episodeSync;

    public EpisodeSyncMediaFileTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _context = new LingarrDbContext(new DbContextOptionsBuilder<LingarrDbContext>()
            .UseSqlite(_connection)
            .Options);
        _context.Database.EnsureCreated();
        _episodeSync = new EpisodeSync(new PathConversionService(_context));
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    private static SonarrEpisode MediaInfoEpisode(int id) => new()
    {
        Id = id,
        EpisodeNumber = id,
        Title = $"Episode {id}",
        SeasonNumber = 1,
        HasFile = true,
        EpisodeFile = new SonarrEpisodeFile
        {
            Path = "/tv/Show/Season 1/Show.S01E10.mkv",
            DateAdded = DateTimeOffset.UtcNow,
            Quality = new RadarrMovieFileQuality
            {
                Quality = new RadarrQualityItem { Id = 7, Name = "WEBDL-1080p" }
            },
            Size = 4_600_000_000,
            MediaInfo = new IntegrationMediaInfo
            {
                AudioLanguages = "english",
                VideoCodec = "h264",
                AudioCodec = "eac3",
                AudioChannels = 5.1,
                Resolution = "1920x1080",
                RunTime = "43:12"
            }
        }
    };

    /// <summary>One show/season for the whole test: both sync rounds run
    /// against the same season so the second round exercises the UPDATE
    /// branch, not a second insert.</summary>
    private async Task<Season> SeedSeasonAsync()
    {
        var show = new Show
        {
            SonarrId = 1,
            Title = "Show",
            Path = "/tv/Show",
            DateAdded = DateTimeOffset.UtcNow
        };
        var season = new Season { SeasonNumber = 1, Show = show };
        show.Seasons.Add(season);
        _context.Shows.Add(show);
        await _context.SaveChangesAsync();
        return season;
    }

    private async Task SyncRoundAsync(Season season, params SonarrEpisode[] episodes)
    {
        await _episodeSync.SyncEpisodes(season, episodes);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
    }

    private Task<Episode> StoredAsync() =>
        _context.Episodes.AsNoTracking().SingleAsync();

    [Fact]
    public async Task EpisodeFileFacts_PersistOnInsertAndStayAuthoritative()
    {
        var season = await SeedSeasonAsync();
        await SyncRoundAsync(season, MediaInfoEpisode(10));

        var stored = await StoredAsync();
        Assert.Equal("WEBDL-1080p", stored.Quality);
        Assert.Equal(4_600_000_000L, stored.SizeBytes);
        Assert.Equal("h264", stored.VideoCodec);
        Assert.Equal("eac3", stored.AudioCodec);
        Assert.Equal(5.1, stored.AudioChannels);
        Assert.Equal("1920x1080", stored.Resolution);
        // Sonarr's "43:12" (mm:ss) folds into 43 minutes
        Assert.Equal(43, stored.RuntimeMinutes);

        // re-sync without the file facts clears them (payload-authoritative,
        // mirroring the movie B2 metadata rule); the season reloads with its
        // episodes so the round takes the UPDATE branch
        var reloaded = await _context.Seasons
            .Include(s => s.Episodes)
            .SingleAsync();
        await SyncRoundAsync(
            reloaded,
            new SonarrEpisode
            {
                Id = 10,
                EpisodeNumber = 10,
                Title = "Episode 10",
                SeasonNumber = 1,
                HasFile = true,
                EpisodeFile = new SonarrEpisodeFile { Path = "/tv/Show/Season 1/Show.S01E10.mkv" }
            });

        var cleared = await StoredAsync();
        Assert.Null(cleared.Quality);
        Assert.Null(cleared.SizeBytes);
        Assert.Null(cleared.VideoCodec);
        Assert.Null(cleared.AudioCodec);
        Assert.Null(cleared.AudioChannels);
        Assert.Null(cleared.Resolution);
        Assert.Null(cleared.RuntimeMinutes);
    }
}
