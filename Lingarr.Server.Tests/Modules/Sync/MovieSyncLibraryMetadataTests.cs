using System;
using System.Collections.Generic;
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
/// B2 library metadata on the Radarr sync (M0008 columns): quality
/// (movieFile.quality.quality.name), size_bytes (movieFile.size), imdb_rating
/// (ratings.imdb, only when votes &gt; 0) and the M0008 year/imdb_id search
/// keys must all land on insert and be payload-authoritative on re-sync.
/// Radarr always carries these fields on a hasFile movie, so unlike audio
/// languages a missing block maps onto null without any preserve semantics.
/// </summary>
public class MovieSyncLibraryMetadataTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly LingarrDbContext _context;
    private readonly MovieSync _movieSync;

    public MovieSyncLibraryMetadataTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _context = new LingarrDbContext(new DbContextOptionsBuilder<LingarrDbContext>()
            .UseSqlite(_connection)
            .Options);
        _context.Database.EnsureCreated();
        _movieSync = new MovieSync(
            _context,
            new PathConversionService(_context),
            NullLogger<MovieSync>.Instance,
            new ImageSync(_context));
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    private static RadarrMovie Movie(
        string? quality = "WEBDL-1080p",
        long? size = 2_500_000_000,
        int? imdbVotes = 1234,
        double? imdbValue = 7.8,
        int? year = 2019,
        string? imdbId = "tt4154796") => new()
    {
        Id = 1,
        Title = "Movie",
        Path = "/movies/Movie",
        RootFolderPath = "/movies",
        Added = "2026-01-01T00:00:00Z",
        HasFile = true,
        Year = year,
        ImdbId = imdbId,
        Ratings = imdbVotes.HasValue || imdbValue.HasValue
            ? new RadarrRatings
            {
                Imdb = new RadarrRatingSource { Votes = imdbVotes ?? 0, Value = imdbValue }
            }
            : null,
        MovieFile = new RadarrMovieFile
        {
            Path = "/movies/Movie/Movie.mkv",
            Quality = quality == null
                ? null
                : new RadarrMovieFileQuality
                {
                    Quality = new RadarrQualityItem { Id = 1, Name = quality }
                },
            Size = size
        },
        Images = new List<IntegrationImage>()
    };

    private async Task<Movie> SyncAndReloadAsync(RadarrMovie movie)
    {
        await _movieSync.SyncMovie(movie);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
        return await _context.Movies.AsNoTracking().SingleAsync(m => m.RadarrId == 1);
    }

    [Fact]
    public async Task FullPayload_PersistsAllMetadataColumns()
    {
        var stored = await SyncAndReloadAsync(Movie());

        Assert.Equal("WEBDL-1080p", stored.Quality);
        Assert.Equal(2_500_000_000L, stored.SizeBytes);
        Assert.Equal(7.8, stored.ImdbRating);
        Assert.Equal(2019, stored.Year);
        Assert.Equal("tt4154796", stored.ImdbId);
    }

    [Fact]
    public async Task RatingWithoutVotes_StaysNull()
    {
        // Radarr reports votes=0 / value=0 when there is no real user
        // aggregate yet — the column must not store the fake 0.
        var stored = await SyncAndReloadAsync(Movie(imdbVotes: 0, imdbValue: 0));

        Assert.Null(stored.ImdbRating);
    }

    [Fact]
    public async Task MissingRatingsBlock_StaysNull()
    {
        var stored = await SyncAndReloadAsync(Movie(imdbVotes: null, imdbValue: null));

        Assert.Null(stored.ImdbRating);
    }

    [Fact]
    public async Task MissingBlocks_PersistNullOnInsert()
    {
        var stored = await SyncAndReloadAsync(Movie(quality: null, size: null, year: null, imdbId: null));

        Assert.Null(stored.Quality);
        Assert.Null(stored.SizeBytes);
        Assert.Null(stored.Year);
        Assert.Null(stored.ImdbId);
    }

    [Fact]
    public async Task Resync_IsPayloadAuthoritative()
    {
        // Radarr always carries these fields on a hasFile movie, so a re-sync
        // overwrites — no "never clear" preserve rule like audio languages.
        await SyncAndReloadAsync(Movie());

        var stored = await SyncAndReloadAsync(Movie(
            quality: "BluRay-2160p",
            size: 9_876_543_210,
            imdbVotes: 250_000,
            imdbValue: 8.4,
            year: 2021,
            imdbId: "tt9999999"));

        Assert.Equal("BluRay-2160p", stored.Quality);
        Assert.Equal(9_876_543_210L, stored.SizeBytes);
        Assert.Equal(8.4, stored.ImdbRating);
        Assert.Equal(2021, stored.Year);
        Assert.Equal("tt9999999", stored.ImdbId);
    }

    [Fact]
    public async Task Resync_WithoutRatings_ClearsTheColumn()
    {
        await SyncAndReloadAsync(Movie());

        var stored = await SyncAndReloadAsync(Movie(imdbVotes: null, imdbValue: null));

        Assert.Null(stored.ImdbRating);
    }
}
