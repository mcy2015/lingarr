using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Lingarr.Core.Configuration;
using Lingarr.Core.Data;
using Lingarr.Core.Entities;
using Lingarr.Core.Enum;
using Lingarr.Server.Models;
using Lingarr.Server.Models.Api;
using Lingarr.Server.Modules.Media;
using Lingarr.Server.Modules.Settings;
using Lingarr.Server.Modules.Translation;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Lingarr.Server.Tests.Services;

/// <summary>
/// SQL-translation coverage for the pushed-down library aggregation
/// (scale audit P0-7/P0-8): InMemory never validates translatability, so the
/// aggregate queries (projection + anti-join "newest request per media" +
/// GROUP BY rollups + paging inputs) run against a real SQLite schema here.
/// Behavioural parity with the previous in-memory aggregation is asserted on
/// the same fixtures: state precedence, latest-request-wins, tie-breaking,
/// filters, ordering and paging boundaries, plus the single-media direct
/// path behind /media/library?id=.
/// </summary>
public class MediaLibraryServiceSqliteTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly LingarrDbContext _dbContext;
    private readonly Mock<ISettingService> _settingService = new();
    private readonly Mock<ICostService> _costService = new();
    private int _nextExternalId = 100;

    public MediaLibraryServiceSqliteTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _dbContext = new LingarrDbContext(new DbContextOptionsBuilder<LingarrDbContext>()
            .UseSqlite(_connection)
            .Options);
        _dbContext.Database.EnsureCreated();

        _settingService
            .Setup(s => s.GetSettingAsJson<TargetLanguage>(It.IsAny<string>()))
            .ReturnsAsync(new List<TargetLanguage> { new() { Name = "Chinese", Code = "zh" } });
        _costService
            .Setup(c => c.GetRates())
            .ReturnsAsync(new Dictionary<string, decimal> { ["libretranslate"] = 5.0m });
        _settingService
            .Setup(s => s.GetSetting(It.IsAny<string>()))
            .ReturnsAsync("libretranslate");
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        _connection.Dispose();
    }

    private MediaLibraryService CreateService() =>
        new(_dbContext, _settingService.Object, _costService.Object,
            NullLogger<MediaLibraryService>.Instance);

    private async Task<Show> SeedShowAsync(
        string title,
        int[] episodesPerSeason,
        Dictionary<string, MediaLanguageCoverage>? coverage = null,
        int? explicitId = null)
    {
        var show = new Show
        {
            SonarrId = _nextExternalId++,
            Title = title,
            Path = "/tv/" + title,
            DateAdded = DateTimeOffset.UtcNow,
            SubtitleCoverage = coverage
        };
        if (explicitId is not null)
        {
            show.Id = explicitId.Value;
        }
        for (var seasonNumber = 1; seasonNumber <= episodesPerSeason.Length; seasonNumber++)
        {
            var season = new Season
            {
                SeasonNumber = seasonNumber,
                Path = $"/tv/{title}/S{seasonNumber:00}",
                Show = show
            };
            for (var i = 1; i <= episodesPerSeason[seasonNumber - 1]; i++)
            {
                season.Episodes.Add(new Episode
                {
                    SonarrId = i,
                    EpisodeNumber = i,
                    Title = $"S{seasonNumber}E{i}",
                    FileName = $"{title}.S{seasonNumber:00}E{i:00}.mkv",
                    Season = season
                });
            }
            show.Seasons.Add(season);
        }
        _dbContext.Shows.Add(show);
        await _dbContext.SaveChangesAsync();
        return show;
    }

    private async Task<Movie> SeedMovieAsync(
        string title,
        Dictionary<string, MediaLanguageCoverage>? coverage = null,
        int? explicitId = null)
    {
        var movie = new Movie
        {
            RadarrId = _nextExternalId++,
            Title = title,
            FileName = title + ".mkv",
            Path = "/movies/" + title,
            DateAdded = DateTimeOffset.UtcNow,
            SubtitleCoverage = coverage
        };
        if (explicitId is not null)
        {
            movie.Id = explicitId.Value;
        }
        _dbContext.Movies.Add(movie);
        await _dbContext.SaveChangesAsync();
        return movie;
    }

    private async Task SeedRequestAsync(
        MediaType mediaType,
        int mediaId,
        TranslationStatus status,
        DateTimeOffset? updatedAt = null)
    {
        var request = new TranslationRequest
        {
            Title = "req",
            SourceLanguage = "en",
            TargetLanguage = "zh",
            MediaType = mediaType,
            MediaId = mediaId,
            Status = status,
            UpdatedAt = updatedAt ?? DateTimeOffset.UtcNow
        };
        _dbContext.TranslationRequests.Add(request);
        await _dbContext.SaveChangesAsync();
        if (updatedAt is not null)
        {
            // SaveChanges' UpdateTimestamps overwrites UpdatedAt on Added rows,
            // so the intended timestamp is applied set-based, bypassing the
            // change tracker.
            await _dbContext.TranslationRequests
                .Where(r => r.Id == request.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.UpdatedAt, updatedAt.Value));
        }
    }

    private static List<Episode> EpisodesOf(Show show) =>
        show.Seasons.SelectMany(season => season.Episodes).ToList();

    [Fact]
    public async Task AggregatesAcrossSeasonsAndMedia_OrdersAndPagesInSql()
    {
        // Alpha: 2 seasons, 3 episodes, newest per episode = 2 completed + 1 in progress.
        var alpha = await SeedShowAsync("Alpha", [2, 1]);
        var alphaEpisodes = EpisodesOf(alpha);
        var alphaNewest = DateTimeOffset.UtcNow.AddMinutes(5);
        await SeedRequestAsync(MediaType.Show, alphaEpisodes[0].Id, TranslationStatus.Completed, DateTimeOffset.UtcNow.AddDays(-3));
        await SeedRequestAsync(MediaType.Show, alphaEpisodes[1].Id, TranslationStatus.Completed, DateTimeOffset.UtcNow.AddDays(-2));
        await SeedRequestAsync(MediaType.Show, alphaEpisodes[2].Id, TranslationStatus.InProgress, alphaNewest);

        // Beta: ep1 latest Failed (after an older Completed), ep2 Completed.
        var beta = await SeedShowAsync("Beta", [2]);
        var betaEpisodes = EpisodesOf(beta);
        await SeedRequestAsync(MediaType.Show, betaEpisodes[0].Id, TranslationStatus.Completed, DateTimeOffset.UtcNow.AddDays(-5));
        await SeedRequestAsync(MediaType.Show, betaEpisodes[0].Id, TranslationStatus.Failed, DateTimeOffset.UtcNow.AddDays(-1));
        await SeedRequestAsync(MediaType.Show, betaEpisodes[1].Id, TranslationStatus.Completed, DateTimeOffset.UtcNow.AddDays(-4));

        var caper = await SeedMovieAsync("Caper");
        await SeedRequestAsync(MediaType.Movie, caper.Id, TranslationStatus.Pending);
        await SeedMovieAsync("Dormant");

        var service = CreateService();

        // page 1 of 2 over the title-ordered mixed list (Alpha, Beta, Caper, Dormant)
        var page1 = await service.GetLibrary(null, null, null, null, "Title", true, 1, 2);
        Assert.Equal(4, page1.TotalCount);
        Assert.Equal(["Alpha", "Beta"], page1.Items.Select(i => i.Title).ToArray());

        var alphaItem = page1.Items.Single(i => i.Title == "Alpha");
        Assert.Equal(MediaType.Show, alphaItem.MediaType);
        Assert.Equal(2, alphaItem.SeasonCount);
        Assert.Equal(3, alphaItem.EpisodeCount);
        Assert.Equal(MediaTranslationState.InProgress, alphaItem.State);
        Assert.Equal(2, alphaItem.CompletedCount);
        Assert.Equal(0, alphaItem.FailedCount);
        Assert.Equal(alphaNewest, alphaItem.LastActivityAt);
        // the inline-detail show graph hydrates for the page's shows only
        Assert.NotNull(alphaItem.Show);
        Assert.Equal(2, alphaItem.Show.Seasons.Count);

        var betaItem = page1.Items.Single(i => i.Title == "Beta");
        Assert.Equal(MediaTranslationState.Failed, betaItem.State);
        Assert.Equal(1, betaItem.CompletedCount);
        Assert.Equal(1, betaItem.FailedCount);
        Assert.NotNull(betaItem.Show);

        // page 2: movies only — no Show graph attached
        var page2 = await service.GetLibrary(null, null, null, null, "Title", true, 2, 2);
        Assert.Equal(4, page2.TotalCount);
        Assert.Equal(["Caper", "Dormant"], page2.Items.Select(i => i.Title).ToArray());
        var page2Items = page2.Items.ToArray();
        Assert.Equal(MediaTranslationState.Queued, page2Items[0].State);
        Assert.Equal(MediaTranslationState.None, page2Items[1].State);
        Assert.Equal(1, page2Items[0].EpisodeCount);
        Assert.All(page2.Items, item => Assert.Null(item.Show));

        // one past the end: empty page, truthful totals
        var page3 = await service.GetLibrary(null, null, null, null, "Title", true, 3, 2);
        Assert.Equal(4, page3.TotalCount);
        Assert.Empty(page3.Items);
    }

    [Fact]
    public async Task SearchFilterPushesDownAcrossMediaTypes()
    {
        await SeedShowAsync("Apple", [1]);
        await SeedShowAsync("Banana", [1]);
        await SeedMovieAsync("Apricot");

        var result = await CreateService().GetLibrary(null, "ap", null, null, "Title", true, 1, 20);

        Assert.Equal(2, result.TotalCount);
        Assert.Equal(["Apple", "Apricot"], result.Items.Select(i => i.Title).ToArray());
    }

    [Fact]
    public async Task NewestRequestPerEpisodeWins_EqualTimestampsBreakById()
    {
        var show = await SeedShowAsync("Ties", [2]);
        var episodes = EpisodesOf(show);
        var tie = DateTimeOffset.UtcNow.AddDays(-1);

        // ep1: three generations — only the newest counts.
        await SeedRequestAsync(MediaType.Show, episodes[0].Id, TranslationStatus.Pending, tie.AddDays(-3));
        await SeedRequestAsync(MediaType.Show, episodes[0].Id, TranslationStatus.Failed, tie.AddDays(-2));
        await SeedRequestAsync(MediaType.Show, episodes[0].Id, TranslationStatus.Completed, tie.AddDays(-1));

        // ep2: identical UpdatedAt — the higher Id (seeded later) is the keeper.
        await SeedRequestAsync(MediaType.Show, episodes[1].Id, TranslationStatus.Failed, tie);
        await SeedRequestAsync(MediaType.Show, episodes[1].Id, TranslationStatus.Completed, tie);

        var item = (await CreateService().GetLibrary(MediaType.Show, null, null, null, "Title", true, 1, 20))
            .Items.Single();

        Assert.Equal(MediaTranslationState.Completed, item.State);
        Assert.Equal(2, item.CompletedCount);
        Assert.Equal(0, item.FailedCount);
        Assert.Equal(tie, item.LastActivityAt);
    }

    [Fact]
    public async Task StateAndTargetLanguageFiltersStayTruthful()
    {
        await SeedShowAsync("Full-zh", [2], new Dictionary<string, MediaLanguageCoverage>
        {
            ["zh"] = new() { Count = 2, Bytes = 100 }
        });
        await SeedShowAsync("Half-zh", [2], new Dictionary<string, MediaLanguageCoverage>
        {
            ["zh"] = new() { Count = 1, Bytes = 100 }
        });
        var blown = await SeedMovieAsync("Blown");
        await SeedRequestAsync(MediaType.Movie, blown.Id, TranslationStatus.Failed);

        var service = CreateService();

        // "missing zh" keeps partial coverage and no-coverage items
        var missingZh = await service.GetLibrary(null, null, null, "zh", "Title", true, 1, 20);
        Assert.Equal(2, missingZh.TotalCount);
        Assert.Equal(["Blown", "Half-zh"], missingZh.Items.Select(i => i.Title).ToArray());

        // state filter narrows to the failed movie
        var failed = await service.GetLibrary(null, null, "Failed", null, "Title", true, 1, 20);
        Assert.Equal("Blown", failed.Items.Single().Title);
    }

    [Fact]
    public async Task SingleMediaDirectPath_BuildsOneItemWithoutFullAggregation()
    {
        // a show and a movie share the numeric id: shows win for a null
        // mediaType, mirroring the old FirstOrDefault-over-lists behaviour.
        const int sharedId = 10;
        var show = await SeedShowAsync("Solo", [2], explicitId: sharedId);
        var episodes = EpisodesOf(show);
        await SeedRequestAsync(MediaType.Show, episodes[0].Id, TranslationStatus.Completed, DateTimeOffset.UtcNow.AddDays(-2));
        await SeedRequestAsync(MediaType.Show, episodes[1].Id, TranslationStatus.Pending, DateTimeOffset.UtcNow.AddDays(-1));
        await SeedMovieAsync("Twin", explicitId: sharedId);

        var service = CreateService();

        var showItem = await service.GetLibraryItem(MediaType.Show, sharedId);
        Assert.NotNull(showItem);
        Assert.Equal("Solo", showItem.Title);
        Assert.Equal(2, showItem.EpisodeCount);
        Assert.Equal(1, showItem.SeasonCount);
        Assert.Equal(MediaTranslationState.Queued, showItem.State);
        Assert.Equal(1, showItem.CompletedCount);
        Assert.NotNull(showItem.Show);
        Assert.Equal(2, showItem.Show.Seasons.Single().Episodes.Count);

        // null mediaType resolves the show, not the movie with the same id
        Assert.Equal("Solo", (await service.GetLibraryItem(null, sharedId))!.Title);

        // typed lookup finds the movie instead
        var movieItem = await service.GetLibraryItem(MediaType.Movie, sharedId);
        Assert.NotNull(movieItem);
        Assert.Equal("Twin", movieItem.Title);
        Assert.Equal(1, movieItem.EpisodeCount);
        Assert.Equal(MediaTranslationState.None, movieItem.State);
        Assert.Null(movieItem.Show);

        // unknown id and type mismatches stay 404-shaped
        Assert.Null(await service.GetLibraryItem(MediaType.Movie, 999));
        Assert.Null(await service.GetLibraryItem(null, 999));
    }
}
