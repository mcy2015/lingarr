using System;
using System.Data.Common;
using System.Threading.Tasks;
using Lingarr.Core.Entities;
using Lingarr.Core.Data;
using Lingarr.Core.Enum;
using Lingarr.Server.Modules.Media;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Lingarr.Server.Tests.Modules.Media;

/// <summary>
/// Walk-through options C/E write paths of <see cref="AudioLanguageService"/>:
/// manual language editing (movie, show cascade, reset) and the sync guards
/// that keep manual values safe. The remote ASR call itself (ffmpeg clip +
/// HTTP) is exercised end-to-end against a stub service in the browser
/// verification, not here.
/// </summary>
public class AudioLanguageServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly LingarrDbContext _context;
    private readonly AudioLanguageService _service;

    public AudioLanguageServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        var options = new DbContextOptionsBuilder<LingarrDbContext>()
            .UseSqlite(_connection)
            .Options;
        _context = new LingarrDbContext(options);
        _context.Database.EnsureCreated();
        _service = new AudioLanguageService(
            _context,
            new Moq.Mock<Lingarr.Server.Modules.Settings.ISettingService>().Object,
            NullHttpClientFactory.Instance,
            NullLogger<AudioLanguageService>.Instance);
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    private (Show Show, Episode Ep1, Episode Ep2) NewShowWithTwoEpisodes()
    {
        var show = new Show
        {
            SonarrId = 1,
            Title = "Show",
            Path = "/tv/Show",
            DateAdded = DateTimeOffset.UtcNow
        };
        var season = new Season { SeasonNumber = 1, Show = show };
        season.Episodes.Add(new Episode { SonarrId = 10, EpisodeNumber = 1, Title = "One", Path = "/tv/Show/S01", FileName = "e1", Season = season });
        season.Episodes.Add(new Episode { SonarrId = 11, EpisodeNumber = 2, Title = "Two", Path = "/tv/Show/S01", FileName = "e2", Season = season });
        show.Seasons.Add(season);
        _context.Shows.Add(show);
        _context.SaveChanges();
        return (show, show.Seasons[0].Episodes[0], show.Seasons[0].Episodes[1]);
    }

    [Fact]
    public async Task SetManual_Movie_NormalizesCodesAndFlagsManual()
    {
        var movie = new Movie
        {
            RadarrId = 7, Title = "Movie", Path = "/movies/Movie", FileName = "movie",
            DateAdded = DateTimeOffset.UtcNow
        };
        _context.Movies.Add(movie);
        _context.SaveChanges();

        var result = await _service.SetManualAsync(MediaType.Movie, movie.Id, ["ZH", "en", "bogus"]);

        Assert.Equal(["zh", "en"], result.Languages);
        var stored = await _context.Movies.AsNoTracking().SingleAsync();
        Assert.Equal(["zh", "en"], stored.AudioLanguages);
        Assert.True(stored.AudioLanguagesManual);
    }

    [Fact]
    public async Task SetManual_Show_CascadesToEveryEpisode()
    {
        var (show, _, _) = NewShowWithTwoEpisodes();

        var result = await _service.SetManualAsync(MediaType.Show, show.Id, ["ko"]);

        Assert.Equal(["ko"], result.Languages);
        Assert.Equal("2 episodes", result.TargetDescription);
        var episodes = await _context.Episodes.AsNoTracking().ToListAsync();
        Assert.All(episodes, episode =>
        {
            Assert.Equal(["ko"], episode.AudioLanguages);
            Assert.True(episode.AudioLanguagesManual);
        });
    }

    [Fact]
    public async Task SetManual_EmptyList_ResetsToUnknownAndAutomatic()
    {
        var (show, ep1, _) = NewShowWithTwoEpisodes();
        ep1.AudioLanguages = ["en"];
        ep1.AudioLanguagesManual = true;
        _context.SaveChanges();

        await _service.SetManualAsync(MediaType.Episode, ep1.Id, []);

        var stored = await _context.Episodes.AsNoTracking().SingleAsync(e => e.Id == ep1.Id);
        Assert.Null(stored.AudioLanguages);
        Assert.NotEqual(true, stored.AudioLanguagesManual);
    }
}
