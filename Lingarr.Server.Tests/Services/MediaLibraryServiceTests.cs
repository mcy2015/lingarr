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
using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;
using Lingarr.Server.Modules.Infrastructure;
using Lingarr.Server.Modules.Settings;
using Lingarr.Server.Modules.Media;
using Lingarr.Server.Modules.Translation;

namespace Lingarr.Server.Tests.Services;

public class MediaLibraryServiceTests : IDisposable
{
    private readonly LingarrDbContext _dbContext;
    private readonly Mock<ISettingService> _settingService = new();
    private readonly Mock<ICostService> _costService = new();

    public MediaLibraryServiceTests()
    {
        _dbContext = new LingarrDbContext(new DbContextOptionsBuilder<LingarrDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

        _settingService
            .Setup(s => s.GetSettingAsJson<TargetLanguage>(It.IsAny<string>()))
            .ReturnsAsync(new List<TargetLanguage> { new() { Name = "Chinese", Code = "zh" } });
        _settingService
            .Setup(s => s.GetSettingAsJson<Language>(It.IsAny<string>()))
            .ReturnsAsync(new List<Language> { new() { code = "en", name = "English" } });
        _costService
            .Setup(c => c.GetRates())
            .ReturnsAsync(new Dictionary<string, decimal> { ["libretranslate"] = 5.0m });
        _settingService
            .Setup(s => s.GetSetting(It.IsAny<string>()))
            .ReturnsAsync("libretranslate");
    }

    public void Dispose() => _dbContext.Database.EnsureDeleted();

    private MediaLibraryService CreateService() =>
        new(_dbContext, _settingService.Object, _costService.Object, null!);

    private async Task<Show> SeedShowAsync(
        string title,
        int episodeCount,
        Dictionary<string, MediaLanguageCoverage>? coverage = null)
    {
        var show = new Show
        {
            SonarrId = 1,
            Title = title,
            Path = "/tv/" + title,
            DateAdded = DateTimeOffset.UtcNow,
            SubtitleCoverage = coverage
        };
        var season = new Season
        {
            SeasonNumber = 1,
            Path = "/tv/" + title + "/S01",
            Show = show
        };
        for (var i = 1; i <= episodeCount; i++)
        {
            season.Episodes.Add(new Episode
            {
                SonarrId = i,
                EpisodeNumber = i,
                Title = "E" + i,
                FileName = $"{title}.S01E{i:00}.mkv",
                Season = season
            });
        }
        show.Seasons.Add(season);
        _dbContext.Shows.Add(show);
        await _dbContext.SaveChangesAsync();
        return show;
    }

    private async Task<Movie> SeedMovieAsync(
        string title,
        Dictionary<string, MediaLanguageCoverage>? coverage = null)
    {
        var movie = new Movie
        {
            RadarrId = 1,
            Title = title,
            FileName = title + ".mkv",
            Path = "/movies/" + title,
            DateAdded = DateTimeOffset.UtcNow,
            SubtitleCoverage = coverage
        };
        _dbContext.Movies.Add(movie);
        await _dbContext.SaveChangesAsync();
        return movie;
    }

    private async Task SeedRequestAsync(
        MediaType mediaType,
        int mediaId,
        TranslationStatus status,
        DateTimeOffset? updatedAt = null,
        int characters = 10_000)
    {
        _dbContext.TranslationRequests.Add(new TranslationRequest
        {
            Title = "req",
            SourceLanguage = "en",
            TargetLanguage = "zh",
            MediaType = mediaType,
            MediaId = mediaId,
            Status = status,
            CharactersTranslated = characters,
            ServiceType = "libretranslate",
            CompletedAt = status is TranslationStatus.Completed ? DateTimeOffset.UtcNow : null,
            UpdatedAt = updatedAt ?? DateTimeOffset.UtcNow
        });
        await _dbContext.SaveChangesAsync();
    }

    private Task<MediaLibraryItem> FirstShowAsync() =>
        ItemAsync(MediaType.Show);

    private async Task<MediaLibraryItem> ItemAsync(MediaType type) =>
        (await CreateService().GetLibrary(null, null, null, null, "Title", true, 1, 50))
        .Items.Single(i => i.MediaType == type);

    [Fact]
    public async Task BuildsTracksFromCoverageAndMissingTargets()
    {
        var show = await SeedShowAsync("Shogun", 10, new Dictionary<string, MediaLanguageCoverage>
        {
            ["en"] = new() { Count = 10, Bytes = 100_000 },
            ["zh"] = new() { Count = 3, Bytes = 30_000 }
        });

        var item = await FirstShowAsync();

        Assert.Equal(10, item.EpisodeCount);
        Assert.Equal(1, item.SeasonCount);
        var en = item.Tracks.Single(t => t.Language == "en");
        Assert.Equal("complete", en.State);
        var zh = item.Tracks.Single(t => t.Language == "zh");
        Assert.Equal(3, zh.Have);
        Assert.Equal(10, zh.Total);
        Assert.Equal("partial", zh.State);
        // target language without any coverage shows as missing
        Assert.Contains(item.Tracks, t => t.Language == "zh");
    }

    [Fact]
    public async Task InProgressOutranksPendingFailedAndCompleted()
    {
        var show = await SeedShowAsync("Mixed", 3);
        var episodes = show.Seasons.SelectMany(s => s.Episodes).OrderBy(e => e.EpisodeNumber).ToList();
        await SeedRequestAsync(MediaType.Show, episodes[0].Id, TranslationStatus.Completed);
        await SeedRequestAsync(MediaType.Show, episodes[1].Id, TranslationStatus.Failed);
        await SeedRequestAsync(MediaType.Show, episodes[2].Id, TranslationStatus.InProgress);

        var item = await FirstShowAsync();

        Assert.Equal(MediaTranslationState.InProgress, item.State);
        Assert.Equal(1, item.CompletedCount);
        Assert.Equal(1, item.FailedCount);
        Assert.NotNull(item.LastActivityAt);
    }

    [Fact]
    public async Task FailedStateWhenNoActiveRequestsRemain()
    {
        var show = await SeedShowAsync("Broken", 2);
        var episodes = show.Seasons.SelectMany(s => s.Episodes).ToList();
        await SeedRequestAsync(MediaType.Show, episodes[0].Id, TranslationStatus.Completed);
        await SeedRequestAsync(MediaType.Show, episodes[1].Id, TranslationStatus.Failed);

        var item = await FirstShowAsync();

        Assert.Equal(MediaTranslationState.Failed, item.State);
    }

    [Fact]
    public async Task NoRequestsYieldsNoneStateAndCarriesCoverage()
    {
        await SeedShowAsync("Fresh", 4, new Dictionary<string, MediaLanguageCoverage>
        {
            ["en"] = new() { Count = 4, Bytes = 40_000 }
        });

        var item = await FirstShowAsync();

        Assert.Equal(MediaTranslationState.None, item.State);
        Assert.Null(item.LastActivityAt);
    }

    [Fact]
    public async Task MissingTargetFilterKeepsIncompleteItems()
    {
        await SeedShowAsync("Complete", 2, new Dictionary<string, MediaLanguageCoverage>
        {
            ["zh"] = new() { Count = 2, Bytes = 100 }
        });
        await SeedShowAsync("Partial", 2, new Dictionary<string, MediaLanguageCoverage>
        {
            ["zh"] = new() { Count = 1, Bytes = 100 }
        });

        var result = await CreateService().GetLibrary(null, null, null, "zh", "Title", true, 1, 50);

        var title = result.Items.Single().Title;
        Assert.Equal("Partial", title);
        Assert.Equal(1, result.TotalCount);
    }

    [Fact]
    public async Task StateFilterAndMixedTypePaging()
    {
        await SeedShowAsync("Show A", 1);
        await SeedMovieAsync("Movie B");
        var movie = (await _dbContext.Movies.ToListAsync()).Single(m => m.Title == "Movie B");
        await SeedRequestAsync(MediaType.Movie, movie.Id, TranslationStatus.Pending);

        var result = await CreateService().GetLibrary(null, null, "Queued", null, "Title", true, 1, 50);

        Assert.Equal("Movie B", result.Items.Single().Title);
    }

    [Fact]
    public async Task MovieTrackCompleteWhenCovered()
    {
        await SeedMovieAsync("Dune", new Dictionary<string, MediaLanguageCoverage>
        {
            ["en"] = new() { Count = 1, Bytes = 50_000 }
        });

        var item = await ItemAsync(MediaType.Movie);

        Assert.Equal(1, item.EpisodeCount);
        Assert.Equal("complete", item.Tracks.Single(t => t.Language == "en").State);
        Assert.Equal("none", item.Tracks.Single(t => t.Language == "zh").State);
    }

    [Fact]
    public async Task EstimateScalesSourceBytesByMissingEpisodes()
    {
        // 10 episodes, en coverage 10 episodes / 100 000 bytes, zh 2 episodes.
        await SeedShowAsync("Shogun", 10, new Dictionary<string, MediaLanguageCoverage>
        {
            ["en"] = new() { Count = 10, Bytes = 100_000 },
            ["zh"] = new() { Count = 2, Bytes = 1_000 }
        });
        // Movie fully missing zh, en source of 20 000 bytes.
        var movie = await SeedMovieAsync("Dune", new Dictionary<string, MediaLanguageCoverage>
        {
            ["en"] = new() { Count = 1, Bytes = 20_000 }
        });

        var estimate = await CreateService().Estimate(
        [
            new EstimateItem { MediaType = MediaType.Show, Id = 1 },
            new EstimateItem { MediaType = MediaType.Movie, Id = movie.Id }
        ], "zh");

        // show: 100k bytes / 10 episodes × 8 missing = 80k; movie: 20k → 100k bytes → 50k chars
        Assert.Equal(50_000, estimate.Characters);
        Assert.Equal("libretranslate", estimate.Provider);
        Assert.Equal(0.25, estimate.Cost);
    }

    [Fact]
    public async Task EstimateIsNullCostWithoutRate()
    {
        _costService.Setup(c => c.GetRates()).ReturnsAsync(new Dictionary<string, decimal>());
        await SeedMovieAsync("Dune", new Dictionary<string, MediaLanguageCoverage>
        {
            ["en"] = new() { Count = 1, Bytes = 20_000 }
        });

        var estimate = await CreateService().Estimate(
        [
            new EstimateItem { MediaType = MediaType.Movie, Id = 1 }
        ], "zh");

        Assert.Equal(10_000, estimate.Characters);
        Assert.Null(estimate.Cost);
    }

    [Fact]
    public async Task EstimateWithServiceTypeJsonArray_PricesParsedPrimaryProvider()
    {
        // StartupService normalises service_type into a JSON array — the
        // estimate must look the rate up for the parsed provider, not the
        // raw array string (which never matches a rate key).
        _settingService
            .Setup(s => s.GetSetting(SettingKeys.Translation.ServiceType))
            .ReturnsAsync("[\"libretranslate\",\"openai\"]");
        await SeedMovieAsync("Dune", new Dictionary<string, MediaLanguageCoverage>
        {
            ["en"] = new() { Count = 1, Bytes = 20_000 }
        });

        var estimate = await CreateService().Estimate(
        [
            new EstimateItem { MediaType = MediaType.Movie, Id = 1 }
        ], "zh");

        Assert.Equal("libretranslate", estimate.Provider);
        // 20k bytes → 10k chars at 5.0 per 1M
        Assert.Equal(0.05, estimate.Cost);
    }
}
