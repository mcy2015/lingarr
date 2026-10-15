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
/// Movie sync consumes the Radarr library as a stream: batches of 100 commit on
/// their own, the change tracker is reset per batch, and a failure mid-stream keeps
/// the already committed batches (partial sync, completed idempotently by the next
/// run now that the outer transaction is gone).
/// </summary>
public class MovieSyncServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly LingarrDbContext _context;
    private readonly MovieSyncService _service;

    public MovieSyncServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        var options = new DbContextOptionsBuilder<LingarrDbContext>()
            .UseSqlite(_connection)
            .Options;
        _context = new LingarrDbContext(options);
        _context.Database.EnsureCreated();

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

    private static RadarrMovie RadarrMovie(int id, string title = "Movie", bool hasFile = true)
    {
        return new RadarrMovie
        {
            Id = id,
            Title = $"{title} {id}",
            Path = $"/movies/{title} {id}",
            RootFolderPath = "/movies",
            Added = "2026-01-01T00:00:00Z",
            HasFile = hasFile,
            MovieFile = new RadarrMovieFile { Path = $"/movies/{title} {id}/{title} {id}.mkv" },
            Images = new List<IntegrationImage>()
        };
    }

    private static async IAsyncEnumerable<RadarrMovie> MovieStream(
        int count, bool throwAtEnd = false)
    {
        for (var i = 1; i <= count; i++)
        {
            await Task.Yield();
            yield return RadarrMovie(i);
        }

        if (throwAtEnd)
        {
            throw new InvalidOperationException("simulated stream failure");
        }
    }

    private static async IAsyncEnumerable<RadarrMovie> MoviesOf(params RadarrMovie[] movies)
    {
        foreach (var movie in movies)
        {
            await Task.Yield();
            yield return movie;
        }
    }

    [Fact]
    public async Task SyncMovies_StreamsAllBatches_PersistsAllAndReturnsIds()
    {
        var ids = await _service.SyncMovies(MovieStream(250), defaultInclude: true);

        Assert.Equal(Enumerable.Range(1, 250).ToList(), ids);
        Assert.Equal(250, await _context.Movies.CountAsync());
        Assert.Empty(_context.ChangeTracker.Entries());
    }

    [Fact]
    public async Task SyncMovies_MidStreamFailure_KeepsCommittedBatchesOnly()
    {
        // The stream dies after 250 movies: batches of 100 have committed twice
        // (200 rows); the trailing 50 were never saved.
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _service.SyncMovies(MovieStream(250, throwAtEnd: true), defaultInclude: true));

        Assert.Equal(200, await _context.Movies.CountAsync());
    }

    [Fact]
    public async Task SyncMovies_IncludesIdsOfMoviesWithoutFiles()
    {
        // No-file movies are not persisted, but their ids still count as "exists in
        // Radarr" so they are not treated as orphans and removed.
        var ids = await _service.SyncMovies(
            MoviesOf(RadarrMovie(1), RadarrMovie(2, hasFile: false)),
            defaultInclude: true);

        Assert.Equal(new List<int> { 1, 2 }, ids);
        Assert.Equal(1, await _context.Movies.CountAsync());
        Assert.Equal(1, (await _context.Movies.AsNoTracking().SingleAsync()).RadarrId);
    }

    [Fact]
    public async Task SyncMovies_SecondRunWithSameData_IsIdempotent()
    {
        await _service.SyncMovies(MovieStream(3), defaultInclude: true);
        await _service.SyncMovies(MovieStream(3), defaultInclude: true);

        Assert.Equal(3, await _context.Movies.CountAsync());
        var stored = await _context.Movies.AsNoTracking()
            .Where(m => m.RadarrId == 1)
            .SingleAsync();
        Assert.Equal("Movie 1", stored.Title);
        Assert.Equal("Movie 1", stored.FileName);
        Assert.Equal("/movies/Movie 1", stored.Path);
    }

    [Fact]
    public async Task SyncMovies_UpdatesExistingMovieInPlace()
    {
        await _service.SyncMovies(MovieStream(1), defaultInclude: true);

        var updated = new RadarrMovie
        {
            Id = 1,
            Title = "Renamed",
            Path = "/movies/Renamed",
            RootFolderPath = "/movies",
            Added = "2026-06-01T00:00:00Z",
            HasFile = true,
            MovieFile = new RadarrMovieFile { Path = "/movies/Renamed/Renamed.mkv" },
            Images = new List<IntegrationImage>()
        };
        var ids = await _service.SyncMovies(MoviesOf(updated), defaultInclude: true);

        Assert.Equal(new List<int> { 1 }, ids);
        Assert.Equal(1, await _context.Movies.CountAsync());
        var stored = await _context.Movies.AsNoTracking().SingleAsync();
        Assert.Equal("Renamed", stored.Title);
        Assert.Equal("Renamed", stored.FileName);
        Assert.Equal("/movies/Renamed", stored.Path);
    }

    [Fact]
    public async Task RemoveNonExistentMovies_RemovesMissingMoviesAndTheirImages()
    {
        await _service.SyncMovies(MovieStream(2), defaultInclude: true);
        _context.Images.Add(new Image { Type = "poster", Path = "/img/1", MovieId = 1 });
        _context.Images.Add(new Image { Type = "poster", Path = "/img/2", MovieId = 2 });
        await _context.SaveChangesAsync();

        await _service.RemoveNonExistentMovies(new List<int> { 1 });

        var remaining = await _context.Movies.AsNoTracking().Select(m => m.RadarrId).ToListAsync();
        Assert.Equal(new List<int> { 1 }, remaining);
        var remainingImages = await _context.Images.AsNoTracking().Select(i => i.Path).ToListAsync();
        Assert.Equal(new List<string> { "/img/1" }, remainingImages);
    }
}
