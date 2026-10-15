using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Lingarr.Core.Data;
using Lingarr.Core.Entities;
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
/// per table), zero Include graphs materialized — instead of one tracked
/// DELETE per image and per movie.
/// </summary>
public class MovieSyncServiceExecuteDeleteTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly CommandCounter _counter;
    private readonly LingarrDbContext _context;
    private readonly MovieSyncService _service;

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
        var movieSync = new MovieSync(
            _context,
            pathConversion,
            NullLogger<MovieSync>.Instance,
            new ImageSync(_context));
        _service = new MovieSyncService(
            _context,
            movieSync,
            NullLogger<MovieSyncService>.Instance);
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task RemoveNonExistentMovies_IssuesConstantDeleteCommandCount()
    {
        // 50 movies with one image each; 49 go away. Statement count must not
        // scale with K (the old tracked removal issued 99 DELETEs + 1 save).
        const int total = 50;
        var now = DateTimeOffset.UtcNow;
        for (var i = 1; i <= total; i++)
        {
            _context.Movies.Add(new Movie
            {
                Id = i, RadarrId = i, Title = $"Movie {i}", FileName = $"Movie {i}",
                Path = $"/movies/Movie {i}", DateAdded = now
            });
            _context.Images.Add(new Image
            {
                Type = "poster", Path = $"/img/{i}", MovieId = i
            });
        }
        // A show image must survive movie orphan removal.
        _context.Shows.Add(new Show
        {
            Id = 1, SonarrId = 1, Title = "Show", Path = "/tv/Show", DateAdded = now
        });
        _context.Images.Add(new Image { Type = "poster", Path = "/img/show", ShowId = 1 });
        await _context.SaveChangesAsync();
        _counter.Reset();

        await _service.RemoveNonExistentMovies(new List<int> { 1 });

        // Constant: exactly one DELETE per table, no matter how many orphans.
        Assert.Equal(2, _counter.DeleteCommands);
        // Zero Include materialization: the only SELECT is the orphan count.
        Assert.Equal(1, _counter.SelectCommands);

        Assert.Equal(1, await _context.Movies.CountAsync());
        Assert.Equal(1, (await _context.Movies.AsNoTracking().SingleAsync()).RadarrId);
        var remainingImages = await _context.Images.AsNoTracking().Select(i => i.Path).ToListAsync();
        Assert.Equal(new List<string> { "/img/1", "/img/show" }, remainingImages.OrderBy(p => p).ToList());
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

    /// <summary>Counts DELETE statements and reader (SELECT) commands after Reset().</summary>
    private sealed class CommandCounter : DbCommandInterceptor
    {
        public int DeleteCommands;
        public int SelectCommands;

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
