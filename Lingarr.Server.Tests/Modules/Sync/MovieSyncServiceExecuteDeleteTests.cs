using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Lingarr.Core.Data;
using Lingarr.Core.Entities;
using Lingarr.Server.Models.Integrations;
using Lingarr.Server.Modules.Sync;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Lingarr.Server.Tests.Modules.Sync;

/// <summary>
/// Audit S6 (S2 precedent): orphaned movies are removed with set-based
/// ExecuteDeletes — a constant number of SQL statements per run (one DELETE
/// per table per 500-id chunk), zero Include graphs materialized — instead of
/// one tracked DELETE per image and per movie. Also pins the QA P1/P2 delete
/// chain guardrails: an empty upstream list or a >50% removal ratio skips the
/// pass, and the delete statements share one transaction so a mid-pass
/// failure rolls everything back.
/// </summary>
public class MovieSyncServiceExecuteDeleteTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly CommandCounter _counter;
    private readonly LingarrDbContext _context;
    private readonly MovieSyncService _service;
    private readonly MovieSync _movieSync;

    public MovieSyncServiceExecuteDeleteTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _counter = new CommandCounter();
        var options = new DbContextOptionsBuilder<LingarrDbContext>()
            .UseSqlite(_connection)
            .AddInterceptors(_counter)
            .Options;
        _context = new LingarrDbContext(options);
        _context.Database.EnsureCreated();
        _counter.Reset();

        var pathConversion = new PathConversionService(_context);
        _movieSync = new MovieSync(
            _context,
            pathConversion,
            NullLogger<MovieSync>.Instance,
            new ImageSync(_context));
        _service = new MovieSyncService(
            _context,
            _movieSync,
            NullLogger<MovieSyncService>.Instance);
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    /// <summary>Seeds N movies (RadarrId = Id = 1..count), optionally with one image each.</summary>
    private async Task SeedMoviesAsync(int count, bool withImages = false)
    {
        var now = DateTimeOffset.UtcNow;
        for (var i = 1; i <= count; i++)
        {
            _context.Movies.Add(new Movie
            {
                Id = i, RadarrId = i, Title = $"Movie {i}", FileName = $"Movie {i}",
                Path = $"/movies/Movie {i}", DateAdded = now
            });
            if (withImages)
            {
                _context.Images.Add(new Image { Type = "poster", Path = $"/img/{i}", MovieId = i });
            }
        }

        await _context.SaveChangesAsync();
    }

    [Fact]
    public async Task RemoveNonExistentMovies_IssuesConstantDeleteCommandCount()
    {
        // 100 movies with one image each; 49 go away (49% — under the 50%
        // guardrail, so the pass actually runs). Statement count must not scale
        // with K (the old tracked removal issued 98 DELETEs + 1 save) and 49
        // orphans still fit a single 500-id chunk.
        const int total = 100;
        const int survivors = 51;
        await SeedMoviesAsync(total, withImages: true);
        // A show image must survive movie orphan removal.
        _context.Shows.Add(new Show
        {
            Id = 1, SonarrId = 1, Title = "Show", Path = "/tv/Show", DateAdded = DateTimeOffset.UtcNow
        });
        _context.Images.Add(new Image { Type = "poster", Path = "/img/show", ShowId = 1 });
        await _context.SaveChangesAsync();
        _counter.Reset();

        await _service.RemoveNonExistentMovies(Enumerable.Range(1, survivors).ToList());

        // Constant: exactly one DELETE per table for one chunk of orphans.
        Assert.Equal(2, _counter.DeleteCommands);
        // Zero Include materialization: the only SELECT is the RadarrId projection.
        Assert.Equal(1, _counter.SelectCommands);

        var remainingRadarrIds = await _context.Movies.AsNoTracking()
            .Select(movie => movie.RadarrId)
            .ToListAsync();
        Assert.Equal(Enumerable.Range(1, survivors).ToList(), remainingRadarrIds.OrderBy(id => id).ToList());

        // Survivor posters plus the show image; every orphan poster is gone.
        var remainingImages = await _context.Images.AsNoTracking()
            .Select(image => image.Path)
            .ToListAsync();
        Assert.Equal(survivors + 1, remainingImages.Count);
        Assert.Contains("/img/show", remainingImages);
    }

    [Fact]
    public async Task RemoveNonExistentMovies_NoOrphans_IssuesNoDeletes()
    {
        _context.Movies.Add(new Movie
        {
            Id = 1, RadarrId = 1, Title = "Movie", FileName = "Movie",
            Path = "/movies/Movie", DateAdded = DateTimeOffset.UtcNow
        });
        await _context.SaveChangesAsync();
        _counter.Reset();

        await _service.RemoveNonExistentMovies(new List<int> { 1 });

        Assert.Equal(0, _counter.DeleteCommands);
        Assert.Equal(1, await _context.Movies.CountAsync());
    }

    // ── QA P1: empty-upstream and >50% guardrails ──────────────────

    [Fact]
    public async Task RemoveNonExistentMovies_EmptyUpstreamList_SkipsDeletion()
    {
        // HTTP 200 + empty array is the signature of a reverse-proxy outage or
        // a reinstalled Radarr with an empty library — it must not wipe the
        // local library, and no DELETE statement may even run.
        await SeedMoviesAsync(3);
        _counter.Reset();

        await _service.RemoveNonExistentMovies(new List<int>());

        Assert.Equal(0, _counter.DeleteCommands);
        Assert.Equal(3, await _context.Movies.CountAsync());
    }

    [Fact]
    public async Task RemoveNonExistentMovies_RefusesWhenMoreThanHalfWouldGo()
    {
        // 51 of 100 movies (51%) would be removed — over the guardrail, so the
        // pass is refused without a single DELETE.
        await SeedMoviesAsync(100);
        _counter.Reset();

        await _service.RemoveNonExistentMovies(Enumerable.Range(1, 49).ToList());

        Assert.Equal(0, _counter.DeleteCommands);
        Assert.Equal(100, await _context.Movies.CountAsync());
    }

    [Fact]
    public async Task RemoveNonExistentMovies_AllowsWhenJustUnderHalf()
    {
        // 49 of 100 (49%) is a legitimate cleanup under the guardrail.
        await SeedMoviesAsync(100);

        await _service.RemoveNonExistentMovies(Enumerable.Range(1, 51).ToList());

        var remaining = await _context.Movies.AsNoTracking()
            .Select(movie => movie.RadarrId)
            .ToListAsync();
        Assert.Equal(Enumerable.Range(1, 51).ToList(), remaining.OrderBy(id => id).ToList());
    }

    [Fact]
    public async Task RemoveNonExistentMovies_ExactlyHalf_IsAllowed()
    {
        // The guardrail is strictly "more than half": removing exactly half
        // (e.g. the library was split in two upstream) still runs.
        await SeedMoviesAsync(100);

        await _service.RemoveNonExistentMovies(Enumerable.Range(1, 50).ToList());

        Assert.Equal(50, await _context.Movies.CountAsync());
    }

    // ── QA P2: atomicity of the delete pass ────────────────────────

    [Fact]
    public async Task RemoveNonExistentMovies_FailureBetweenStatements_RollsBackEverything()
    {
        // The image DELETE (statement 1) succeeds, the movie DELETE (statement
        // 2) fails: without the wrapping transaction the images would already
        // be gone while the movies survive — a half-deleted library.
        await SeedMoviesAsync(10, withImages: true);
        _counter.Reset();
        _counter.FailOnDeleteNumber = 2;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _service.RemoveNonExistentMovies(Enumerable.Range(1, 6).ToList()));
        _counter.FailOnDeleteNumber = null;

        // Statement 1 ran and statement 2 was reached — the rollback, not a
        // skipped pass, is what preserves the rows.
        Assert.Equal(2, _counter.DeleteCommands);
        Assert.Equal(10, await _context.Movies.CountAsync());
        Assert.Equal(10, await _context.Images.AsNoTracking().CountAsync(image => image.MovieId != null));
    }

    // ── chunked deletes (>500 orphan ids) ──────────────────────────

    [Fact]
    public async Task RemoveNonExistentMovies_MoreThanOneChunk_DeletesExactlyTheOrphans()
    {
        // 550 orphans span two 500-id chunks: the statement count becomes
        // tables x chunks (2 x 2 = 4) and every non-orphan survives. This pins
        // the chunked semantics and the bounded parameter count per statement
        // (500, far under SQLite's 32766 host-parameter limit — the old
        // single-Contains delete overflowed it around 32k library ids).
        const int survivors = 650;
        await SeedMoviesAsync(1200);
        _counter.Reset();

        await _service.RemoveNonExistentMovies(Enumerable.Range(1, survivors).ToList());

        Assert.Equal(4, _counter.DeleteCommands);
        var remaining = await _context.Movies.AsNoTracking()
            .Select(movie => movie.RadarrId)
            .ToListAsync();
        Assert.Equal(survivors, remaining.Count);
        Assert.Equal(Enumerable.Range(1, survivors).ToList(), remaining.OrderBy(id => id).ToList());
    }

    // ── QA: hasFile without a movieFile resource (MovieSync NRE) ───

    [Fact]
    public async Task SyncMovie_HasFileWithoutMovieFile_DoesNotThrow()
    {
        // Radarr occasionally reports hasFile=true with a null movieFile
        // resource; the old `movie.MovieFile.Path` dereference aborted the
        // whole streaming sync. The missing file now folds to the empty-path
        // convention — same null-tolerance as Quality/Size on the same method.
        var movie = new RadarrMovie
        {
            Id = 1,
            Title = "Ghost",
            Path = "/movies/Ghost",
            RootFolderPath = "/movies",
            Added = "2026-01-01T00:00:00Z",
            HasFile = true,
            MovieFile = null,
            Images = new List<IntegrationImage>()
        };

        var entity = await _movieSync.SyncMovie(movie, defaultInclude: true);

        Assert.NotNull(entity);
        Assert.Equal(1, entity.RadarrId);
        Assert.Equal(string.Empty, entity.Path);
        Assert.Equal(string.Empty, entity.FileName);
    }

    /// <summary>Counts DELETE statements and reader (SELECT) commands after
    /// Reset(); optionally fails the Nth DELETE (1-based) to simulate an
    /// outage between delete statements.</summary>
    private sealed class CommandCounter : DbCommandInterceptor
    {
        public int DeleteCommands;
        public int SelectCommands;
        public int? FailOnDeleteNumber;

        public void Reset()
        {
            DeleteCommands = 0;
            SelectCommands = 0;
        }

        private void Count(DbCommand command)
        {
            if (command.CommandText.StartsWith("DELETE", StringComparison.OrdinalIgnoreCase))
            {
                DeleteCommands++;
                if (FailOnDeleteNumber == DeleteCommands)
                {
                    throw new InvalidOperationException(
                        $"Simulated failure on DELETE statement #{DeleteCommands}");
                }
            }
            else if (command.CommandText.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase))
            {
                SelectCommands++;
            }
        }

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result)
        {
            Count(command);
            return result;
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Count(command);
            return ValueTask.FromResult(result);
        }

        public override InterceptionResult<int> NonQueryExecuting(
            DbCommand command, CommandEventData eventData,
            InterceptionResult<int> result)
        {
            Count(command);
            return result;
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            Count(command);
            return ValueTask.FromResult(result);
        }
    }
}
