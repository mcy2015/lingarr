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
/// PRD card A audio-language mapping on the Radarr sync: the ffprobe truth
/// string (mediaInfo.audioLanguages) outranks the languages aggregate
/// ([{id,name}] guess), both normalize onto the shared ISO codes (chi→zh),
/// and a payload without a recognized code leaves the column null so the
/// embedded-scan piggyback can fill it later — an existing value is never
/// cleared by a missing payload.
/// </summary>
public class MovieSyncAudioLanguageTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly LingarrDbContext _context;
    private readonly MovieSync _movieSync;

    public MovieSyncAudioLanguageTests()
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
        string? mediaInfoAudioLanguages = null,
        List<IntegrationLanguage>? languages = null) => new()
    {
        Id = 1,
        Title = "Movie",
        Path = "/movies/Movie",
        RootFolderPath = "/movies",
        Added = "2026-01-01T00:00:00Z",
        HasFile = true,
        MovieFile = new RadarrMovieFile
        {
            Path = "/movies/Movie/Movie.mkv",
            MediaInfo = mediaInfoAudioLanguages == null
                ? null
                : new IntegrationMediaInfo { AudioLanguages = mediaInfoAudioLanguages },
            Languages = languages
        },
        Images = new List<IntegrationImage>()
    };

    private static List<IntegrationLanguage> Names(params string[] names) =>
        names.Select((name, index) => new IntegrationLanguage { Id = index + 1, Name = name }).ToList();

    private async Task<Movie> SyncAndReloadAsync(RadarrMovie movie)
    {
        await _movieSync.SyncMovie(movie);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
        return await _context.Movies.AsNoTracking().SingleAsync(m => m.RadarrId == 1);
    }

    [Fact]
    public async Task MediaInfoTruthString_IsPreferredAndNormalized()
    {
        var stored = await SyncAndReloadAsync(
            Movie(mediaInfoAudioLanguages: "english / chi", languages: Names("French")));

        // mediaInfo.audioLanguages (ffprobe truth) wins over the aggregate;
        // chi folds onto zh.
        Assert.Equal(["en","zh"], stored.AudioLanguages);
    }

    [Fact]
    public async Task LanguagesAggregate_IsTheFallbackWhenMediaInfoIsMissing()
    {
        var stored = await SyncAndReloadAsync(Movie(languages: Names("English", "Unknown")));

        Assert.Equal(["en"], stored.AudioLanguages);
    }

    [Fact]
    public async Task NullLanguageElements_SurviveTheFallbackWithoutThrowing()
    {
        // *arr serves [null] elements for unknown languages; the fallback
        // chain must skip them instead of dereferencing .Name
        var languages = new List<IntegrationLanguage>
        {
            null!,
            new IntegrationLanguage { Id = 1, Name = "Chinese" }
        };

        var stored = await SyncAndReloadAsync(Movie(languages: languages));

        Assert.Equal(["zh"], stored.AudioLanguages);
    }

    [Fact]
    public async Task EmptyOrUndPayload_LeavesTheColumnNull()
    {
        var stored = await SyncAndReloadAsync(Movie(mediaInfoAudioLanguages: "und", languages: Names("Unknown")));

        Assert.Null(stored.AudioLanguages);

        // Same for a payload with neither field present at all.
        stored = await SyncAndReloadAsync(Movie());
        Assert.Null(stored.AudioLanguages);
    }

    [Fact]
    public async Task MissingPayload_NeverClearsAnExistingValue()
    {
        // An ffprobe-filled column survives a sync whose payload carries no
        // audio language information.
        await SyncAndReloadAsync(Movie(mediaInfoAudioLanguages: "chi"));

        var stored = await SyncAndReloadAsync(Movie());

        Assert.Equal(["zh"], stored.AudioLanguages);
    }

    [Fact]
    public async Task NewPayload_UpdatesAnExistingValue()
    {
        // *arr outranks the local probe: when the payload does carry data, a
        // re-sync overwrites whatever was there before.
        await SyncAndReloadAsync(Movie(mediaInfoAudioLanguages: "chi"));

        var stored = await SyncAndReloadAsync(Movie(mediaInfoAudioLanguages: "english"));

        Assert.Equal(["en"], stored.AudioLanguages);
    }

    [Fact]
    public async Task AudioOverride_IsNotTouchedByTheSync()
    {
        await SyncAndReloadAsync(Movie(mediaInfoAudioLanguages: "chi"));
        var entity = await _context.Movies.SingleAsync(m => m.RadarrId == 1);
        entity.AudioOverride = true; // set by the manual override endpoint
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        var stored = await SyncAndReloadAsync(Movie(mediaInfoAudioLanguages: "english"));

        Assert.True(stored.AudioOverride);
    }
}
