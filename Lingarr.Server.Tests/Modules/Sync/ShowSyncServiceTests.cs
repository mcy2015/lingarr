using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Lingarr.Core.Data;
using Lingarr.Core.Entities;
using Lingarr.Server.Models.Integrations;
using Lingarr.Server.Modules.Integration;
using Lingarr.Server.Modules.Sync;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Lingarr.Server.Tests.Modules.Sync;

/// <summary>
/// End-to-end show sync over a real SQLite context: the episode list is fetched
/// once per season and shared by the season-path derivation and the episode diff,
/// batches commit autonomously (no outer transaction), and orphan removal runs as
/// set-based deletes.
/// </summary>
public class ShowSyncServiceTests : IDisposable
{
    private const string Added = "2026-01-01T00:00:00Z";

    private readonly SqliteConnection _connection;
    private readonly DeleteFailureInterceptor _deleteInterceptor;
    private readonly LingarrDbContext _context;
    private readonly Mock<ISonarrService> _sonarrService;
    private readonly ShowSyncService _service;

    public ShowSyncServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _deleteInterceptor = new DeleteFailureInterceptor();
        var options = new DbContextOptionsBuilder<LingarrDbContext>()
            .UseSqlite(_connection)
            .AddInterceptors(_deleteInterceptor)
            .Options;
        _context = new LingarrDbContext(options);
        _context.Database.EnsureCreated();

        _sonarrService = new Mock<ISonarrService>();
        var pathConversion = new PathConversionService(_context);
        _service = new ShowSyncService(
            _context,
            new ShowSync(_context, new ImageSync(_context)),
            new SeasonSync(_context, pathConversion, NullLogger<SeasonSync>.Instance),
            new EpisodeSync(pathConversion),
            _sonarrService.Object,
            NullLogger<ShowSyncService>.Instance);
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    private static SonarrShow SonarrShow(int id, string title = "Show", params int[] seasonNumbers)
    {
        var show = new SonarrShow
        {
            Id = id,
            Title = title,
            Path = $"/tv/{title}",
            Added = Added,
            SeasonFolder = true
        };
        foreach (var number in seasonNumbers)
        {
            show.Seasons.Add(new SonarrSeason { SeasonNumber = number });
        }
        return show;
    }

    private static SonarrEpisode SonarrEpisode(int id, int seasonNumber, string path) => new()
    {
        Id = id,
        EpisodeNumber = id,
        Title = $"Episode {id}",
        SeasonNumber = seasonNumber,
        HasFile = true,
        EpisodeFile = new SonarrEpisodeFile
        {
            Path = path,
            DateAdded = new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero)
        }
    };

    private void SetupEpisodes(int seriesId, int seasonNumber, params SonarrEpisode[] episodes)
    {
        _sonarrService
            .Setup(s => s.GetEpisodes(seriesId, seasonNumber))
            .ReturnsAsync(episodes.ToList());
    }

    [Fact]
    public async Task SyncShows_NullLanguageElementsInEpisodeFilePayload_DoNotThrow()
    {
        // includeEpisodeFile=true (the 0.0.54 fix) surfaced Sonarr's null
        // ELEMENTS in the languages aggregate ([null] for unknown) — the
        // fallback chain dereferenced .Name and killed the whole SyncShowJob.
        var show = SonarrShow(1, "Show", 1);
        var withNulls = SonarrEpisode(10, 1, "/tv/Show/Season 1/Show.S01E10.mkv");
        withNulls.EpisodeFile!.Languages =
        [
            null!, // the production payload shape that crashed
            new IntegrationLanguage { Id = 1, Name = "Chinese" }
        ];
        var allNulls = SonarrEpisode(11, 1, "/tv/Show/Season 1/Show.S01E11.mkv");
        allNulls.EpisodeFile!.Languages = [null!];
        SetupEpisodes(1, 1, withNulls, allNulls);

        // the whole run must complete instead of aborting on a null element
        await _service.SyncShows(new List<SonarrShow> { show }, defaultInclude: true);

        var synced = await _context.Episodes.AsNoTracking()
            .Where(e => e.Season.ShowId == 1)
            .ToDictionaryAsync(e => e.SonarrId);
        // the real entry survives the null neighbours
        Assert.Equal(["zh"], synced[10].AudioLanguages);
        // nothing recognizable → the column stays null ("unknown") for the
        // embedded-scan ffprobe piggyback to fill later
        Assert.Null(synced[11].AudioLanguages);
    }

    [Fact]
    public async Task SyncShows_FetchesEpisodeListOncePerSeason()
    {
        // One season previously triggered one fetch for the season path, one for the
        // episode diff, plus one lookup per episode. Now a single fetch must feed both.
        var show = SonarrShow(1, "Show", 1, 2);
        SetupEpisodes(1, 1, SonarrEpisode(10, 1, "/tv/Show/Season 1/Show.S01E10.mkv"));
        SetupEpisodes(1, 2, SonarrEpisode(20, 2, "/tv/Show/Season 2/Show.S02E20.mkv"));

        await _service.SyncShows(new List<SonarrShow> { show }, defaultInclude: true);

        _sonarrService.Verify(s => s.GetEpisodes(1, 1), Times.Once);
        _sonarrService.Verify(s => s.GetEpisodes(1, 2), Times.Once);
        _sonarrService.VerifyNoOtherCalls();

        var season1 = await _context.Seasons.AsNoTracking().SingleAsync(s => s.SeasonNumber == 1);
        var season2 = await _context.Seasons.AsNoTracking().SingleAsync(s => s.SeasonNumber == 2);
        Assert.Equal("/tv/Show/Season 1", season1.Path);
        Assert.Equal("/tv/Show/Season 2", season2.Path);

        var episode = await _context.Episodes.AsNoTracking().SingleAsync(e => e.SonarrId == 10);
        Assert.Equal("Show.S01E10", episode.FileName);
        Assert.Equal("/tv/Show/Season 1", episode.Path);
    }

    [Fact]
    public async Task SyncShows_SecondRunWithSameData_IsIdempotent()
    {
        var show = SonarrShow(1, "Show", 1);
        SetupEpisodes(1, 1, SonarrEpisode(10, 1, "/tv/Show/Season 1/Show.S01E10.mkv"));
        var input = new List<SonarrShow> { show };

        await _service.SyncShows(input, defaultInclude: true);
        await _service.SyncShows(input, defaultInclude: true);

        Assert.Equal(1, await _context.Shows.CountAsync());
        Assert.Equal(1, await _context.Seasons.CountAsync());
        Assert.Equal(1, await _context.Episodes.CountAsync());
        var stored = await _context.Shows
            .Include(s => s.Seasons)
            .ThenInclude(season => season.Episodes)
            .SingleAsync();
        Assert.Equal("Show", stored.Title);
        Assert.Equal($"/tv/Show", stored.Path);
        Assert.All(stored.Seasons.SelectMany(s => s.Episodes),
            episode => Assert.NotEqual(default, episode.Id));
    }

    [Fact]
    public async Task SyncShows_UpdatesExistingShowAndEpisodes()
    {
        var original = SonarrShow(1, "Old Title", 1);
        SetupEpisodes(1, 1, SonarrEpisode(10, 1, "/tv/Old Title/Season 1/Old.S01E10.mkv"));
        await _service.SyncShows(new List<SonarrShow> { original }, defaultInclude: true);

        var updated = SonarrShow(1, "New Title", 1);
        SetupEpisodes(1, 1, SonarrEpisode(10, 1, "/tv/New Title/Season 1/New.S01E10.mkv"));
        await _service.SyncShows(new List<SonarrShow> { updated }, defaultInclude: true);

        var show = await _context.Shows.AsNoTracking().SingleAsync();
        Assert.Equal("New Title", show.Title);
        Assert.Equal("/tv/New Title", show.Path);

        var episode = await _context.Episodes.AsNoTracking().SingleAsync();
        Assert.Equal("New.S01E10", episode.FileName);
        Assert.Equal("/tv/New Title/Season 1", episode.Path);
        var season = await _context.Seasons.AsNoTracking().SingleAsync();
        Assert.Equal("/tv/New Title/Season 1", season.Path);
    }

    [Fact]
    public async Task SyncShows_ClearsChangeTrackerAfterEachBatch()
    {
        // More than one batch of 100 shows; without ChangeTracker.Clear() the
        // tracked graph would keep growing across the whole run.
        var shows = Enumerable.Range(1, 150).Select(i => SonarrShow(i, $"Show {i}")).ToList();

        await _service.SyncShows(shows, defaultInclude: true);

        Assert.Equal(150, await _context.Shows.CountAsync());
        Assert.Empty(_context.ChangeTracker.Entries());
    }

    [Fact]
    public async Task SyncShows_MidRunFailure_KeepsCommittedBatchesOnly()
    {
        // 250 shows, the 250th fails while fetching its episodes: batches of 100
        // have committed twice (200 shows); shows 201-250 were never saved. This is
        // the partial-sync contract that replaced the removed outer transaction —
        // the next run closes the gap idempotently.
        var shows = Enumerable.Range(1, 250).Select(i => SonarrShow(i, $"Show {i}", 1)).ToList();
        _sonarrService
            .Setup(s => s.GetEpisodes(It.IsAny<int>(), It.IsAny<int>()))
            .ReturnsAsync(new List<SonarrEpisode>());
        _sonarrService
            .Setup(s => s.GetEpisodes(250, 1))
            .ThrowsAsync(new HttpRequestException("simulated sonarr outage"));

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            _service.SyncShows(shows, defaultInclude: true));

        Assert.Equal(200, await _context.Shows.CountAsync());
    }

    [Fact]
    public async Task SyncShow_Single_UsesMergedEpisodeFetch()
    {
        var show = SonarrShow(1, "Show", 1);
        SetupEpisodes(1, 1, SonarrEpisode(10, 1, "/tv/Show/Season 1/Show.S01E10.mkv"));

        var result = await _service.SyncShow(show, defaultInclude: true);

        Assert.Equal(1, result.Id);
        _sonarrService.Verify(s => s.GetEpisodes(1, 1), Times.Once);
        Assert.Equal(1, await _context.Episodes.CountAsync());
    }

    [Fact]
    public async Task RemoveNonExistentShows_DeletesOrphansWithChildren_SetBased()
    {
        var survivor = SonarrShow(1, "Survivor", 1);
        SetupEpisodes(1, 1, SonarrEpisode(10, 1, "/tv/Survivor/Season 1/Survivor.S01E10.mkv"));
        var orphan = SonarrShow(2, "Orphan", 1);
        SetupEpisodes(2, 1, SonarrEpisode(20, 1, "/tv/Orphan/Season 1/Orphan.S01E20.mkv"));
        await _service.SyncShows(new List<SonarrShow> { survivor, orphan }, defaultInclude: true);

        _context.Images.Add(new Image { Type = "poster", Path = "/img/orphan", ShowId = 2 });
        // A movie image must be left alone by show orphan removal.
        _context.Movies.Add(new Movie
        {
            Id = 1,
            RadarrId = 1,
            Title = "Unrelated Movie",
            FileName = null,
            Path = null,
            DateAdded = DateTimeOffset.UtcNow
        });
        _context.Images.Add(new Image { Type = "poster", Path = "/img/movie", MovieId = 1 });
        await _context.SaveChangesAsync();

        await _service.RemoveNonExistentShows(new HashSet<int> { 1 });

        Assert.Equal(1, await _context.Shows.CountAsync());
        Assert.Equal(1, await _context.Seasons.CountAsync());
        Assert.Equal(1, await _context.Episodes.CountAsync());
        Assert.Equal(10, (await _context.Episodes.AsNoTracking().SingleAsync()).SonarrId);

        // Orphan show images are gone; unrelated movie images survive.
        var remainingImagePaths = await _context.Images.AsNoTracking().Select(i => i.Path).ToListAsync();
        Assert.DoesNotContain("/img/orphan", remainingImagePaths);
        Assert.Contains("/img/movie", remainingImagePaths);
    }

    [Fact]
    public async Task RemoveNonExistentShows_NoOrphans_IsNoOp()
    {
        var show = SonarrShow(1, "Show", 1);
        SetupEpisodes(1, 1, SonarrEpisode(10, 1, "/tv/Show/Season 1/Show.S01E10.mkv"));
        await _service.SyncShows(new List<SonarrShow> { show }, defaultInclude: true);

        await _service.RemoveNonExistentShows(new HashSet<int> { 1 });

        Assert.Equal(1, await _context.Shows.CountAsync());
        Assert.Equal(1, await _context.Episodes.CountAsync());
    }

    // ── QA P1/P2: delete-chain guardrails, atomicity, chunking ─────

    /// <summary>
    /// Seeds N shows (SonarrId = 1..count) directly in the DB, each with one
    /// season and one episode, using the production write conventions
    /// (episode Path is the season directory, FileName the file name).
    /// </summary>
    private async Task SeedShowsAsync(int count)
    {
        var now = DateTimeOffset.UtcNow;
        for (var i = 1; i <= count; i++)
        {
            var show = new Show
            {
                SonarrId = i, Title = $"Show {i}", Path = $"/tv/Show {i}", DateAdded = now
            };
            var season = new Season { SeasonNumber = 1, Show = show };
            season.Episodes.Add(new Episode
            {
                SonarrId = i * 100,
                EpisodeNumber = 1,
                Title = "Pilot",
                FileName = $"Show {i}.S01E01",
                Path = $"/tv/Show {i}/Season 1",
                Season = season,
                DateAdded = now
            });
            show.Seasons.Add(season);
            _context.Shows.Add(show);
        }

        await _context.SaveChangesAsync();
    }

    [Fact]
    public async Task RemoveNonExistentShows_EmptyUpstreamList_SkipsDeletion()
    {
        // HTTP 200 + empty array is the signature of a reverse-proxy outage or
        // a reinstalled Sonarr with an empty library — it must not wipe the
        // show library: nothing is deleted at all.
        var show = SonarrShow(1, "Show", 1);
        SetupEpisodes(1, 1, SonarrEpisode(10, 1, "/tv/Show/Season 1/Show.S01E10.mkv"));
        await _service.SyncShows(new List<SonarrShow> { show }, defaultInclude: true);

        await _service.RemoveNonExistentShows(new HashSet<int>());

        Assert.Equal(1, await _context.Shows.CountAsync());
        Assert.Equal(1, await _context.Seasons.CountAsync());
        Assert.Equal(1, await _context.Episodes.CountAsync());
    }

    [Fact]
    public async Task RemoveNonExistentShows_RefusesWhenMoreThanHalfWouldGo()
    {
        // 51 of 100 shows (51%) would be removed — over the guardrail, so the
        // pass is refused and the graph is left untouched.
        await SeedShowsAsync(100);

        await _service.RemoveNonExistentShows(Enumerable.Range(1, 49).ToHashSet());

        Assert.Equal(100, await _context.Shows.CountAsync());
        Assert.Equal(100, await _context.Seasons.CountAsync());
        Assert.Equal(100, await _context.Episodes.CountAsync());
    }

    [Fact]
    public async Task RemoveNonExistentShows_AllowsWhenJustUnderHalf()
    {
        // 49 of 100 (49%) is a legitimate cleanup under the guardrail and
        // deletes the full child graph of the orphans.
        await SeedShowsAsync(100);

        await _service.RemoveNonExistentShows(Enumerable.Range(1, 51).ToHashSet());

        Assert.Equal(51, await _context.Shows.CountAsync());
        Assert.Equal(51, await _context.Seasons.CountAsync());
        Assert.Equal(51, await _context.Episodes.CountAsync());
    }

    [Fact]
    public async Task RemoveNonExistentShows_FailureBetweenStatements_RollsBackEverything()
    {
        // The episode DELETE (statement 1) succeeds, the season DELETE
        // (statement 2) fails: the wrapping transaction must roll the episode
        // deletes back — the four ExecuteDeletes used to run in four separate
        // implicit transactions, leaving a half-deleted show graph.
        await SeedShowsAsync(10);
        _deleteInterceptor.FailOnDeleteNumber = 2;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _service.RemoveNonExistentShows(Enumerable.Range(1, 6).ToHashSet()));

        Assert.Equal(10, await _context.Shows.CountAsync());
        Assert.Equal(10, await _context.Seasons.CountAsync());
        Assert.Equal(10, await _context.Episodes.CountAsync());
    }

    [Fact]
    public async Task RemoveNonExistentShows_MoreThanOneChunk_DeletesExactlyTheOrphans()
    {
        // 550 orphan shows span two 500-id chunks — pins the chunked
        // semantics (every survivor and its children intact) and the bounded
        // parameter count per statement (500, far under SQLite's 32766
        // host-parameter limit, which the old single-Contains delete hit
        // around a 32k-id library).
        const int survivors = 650;
        await SeedShowsAsync(1200);

        await _service.RemoveNonExistentShows(Enumerable.Range(1, survivors).ToHashSet());

        Assert.Equal(survivors, await _context.Shows.CountAsync());
        Assert.Equal(survivors, await _context.Seasons.CountAsync());
        Assert.Equal(survivors, await _context.Episodes.CountAsync());
        var remaining = await _context.Shows.AsNoTracking()
            .Select(show => show.SonarrId)
            .ToListAsync();
        Assert.Equal(Enumerable.Range(1, survivors).ToList(), remaining.OrderBy(id => id).ToList());
    }

    /// <summary>
    /// Fails the Nth DELETE statement (1-based, 0 = never) to simulate an
    /// outage between the delete statements of the orphan-removal pass.
    /// </summary>
    private sealed class DeleteFailureInterceptor : DbCommandInterceptor
    {
        private int _deleteCount;
        public int FailOnDeleteNumber;

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (FailOnDeleteNumber > 0
                && command.CommandText.StartsWith("DELETE", StringComparison.OrdinalIgnoreCase)
                && ++_deleteCount == FailOnDeleteNumber)
            {
                throw new InvalidOperationException(
                    $"Simulated failure on DELETE statement #{FailOnDeleteNumber}");
            }

            return ValueTask.FromResult(result);
        }
    }
}
