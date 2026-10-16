using System;
using System.Threading.Tasks;
using Lingarr.Core.Data;
using Lingarr.Core.Entities;
using Lingarr.Server.Models.Integrations;
using Lingarr.Server.Modules.Sync;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Lingarr.Server.Tests.Modules.Sync;

/// <summary>
/// M0008/B2 show metadata on the Sonarr sync (M2 batch of the subtitle
/// search): year/imdb_id (episode search-request keys, tech design ⑤) and
/// imdb_rating (ratings.imdb with real votes only) land on insert and are
/// payload-authoritative on re-sync — the MovieSync B2 precedent applied to
/// the series resource.
/// </summary>
public class ShowSyncLibraryMetadataTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly LingarrDbContext _context;
    private readonly ShowSync _showSync;

    public ShowSyncLibraryMetadataTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _context = new LingarrDbContext(new DbContextOptionsBuilder<LingarrDbContext>()
            .UseSqlite(_connection)
            .Options);
        _context.Database.EnsureCreated();
        _showSync = new ShowSync(_context, new ImageSync(_context));
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    private static SonarrShow Show(
        int? year = 2010,
        string? imdbId = "tt0944947",
        int? imdbVotes = 250_000,
        double? imdbValue = 9.4) => new()
    {
        Id = 1,
        Title = "Show",
        Path = "/tv/Show",
        Added = "2026-01-01T00:00:00Z",
        SeasonFolder = true,
        Year = year,
        ImdbId = imdbId,
        Ratings = imdbVotes.HasValue || imdbValue.HasValue
            ? new RadarrRatings
            {
                Imdb = new RadarrRatingSource { Votes = imdbVotes ?? 0, Value = imdbValue }
            }
            : null
    };

    private async Task<Show> SyncAndReloadAsync(SonarrShow show)
    {
        await _showSync.SyncShow(show);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
        return await _context.Shows.AsNoTracking().SingleAsync(s => s.SonarrId == 1);
    }

    [Fact]
    public async Task FullPayload_PersistsSearchAndRatingColumns()
    {
        var stored = await SyncAndReloadAsync(Show());

        Assert.Equal(2010, stored.Year);
        Assert.Equal("tt0944947", stored.ImdbId);
        Assert.Equal(9.4, stored.ImdbRating);
    }

    [Fact]
    public async Task MissingBlocks_PersistNullOnInsert()
    {
        var stored = await SyncAndReloadAsync(Show(year: null, imdbId: null, imdbVotes: null, imdbValue: null));

        Assert.Null(stored.Year);
        Assert.Null(stored.ImdbId);
        Assert.Null(stored.ImdbRating);
    }

    [Fact]
    public async Task RatingWithoutVotes_StaysNull()
    {
        // Sonarr reports votes=0 / value=0 when there is no real user
        // aggregate yet — the column must not store the fake 0.
        var stored = await SyncAndReloadAsync(Show(imdbVotes: 0, imdbValue: 0));

        Assert.Null(stored.ImdbRating);
    }

    [Fact]
    public async Task Resync_IsPayloadAuthoritative()
    {
        await SyncAndReloadAsync(Show());

        var stored = await SyncAndReloadAsync(Show(year: 2011, imdbId: "tt9999999", imdbVotes: 500_000, imdbValue: 8.1));

        Assert.Equal(2011, stored.Year);
        Assert.Equal("tt9999999", stored.ImdbId);
        Assert.Equal(8.1, stored.ImdbRating);
    }

    [Fact]
    public async Task Resync_WithoutMetadata_ClearsTheColumns()
    {
        await SyncAndReloadAsync(Show());

        var stored = await SyncAndReloadAsync(Show(year: null, imdbId: null, imdbVotes: null, imdbValue: null));

        Assert.Null(stored.Year);
        Assert.Null(stored.ImdbId);
        Assert.Null(stored.ImdbRating);
    }
}
