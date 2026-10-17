using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Lingarr.Core.Data;
using Lingarr.Core.Entities;
using Lingarr.Core.Enum;
using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;
using Lingarr.Server.Modules.Settings;
using Lingarr.Server.Modules.Statistics;
using Lingarr.Server.Modules.Translation;

namespace Lingarr.Server.Tests.Services;

/// <summary>
/// P1-1 overview trend card: the daily write-back/cost aggregation over
/// completed translation requests, priced via the provider rate table.
/// </summary>
public class DailyTrendStatisticsTests : IDisposable
{
    private readonly LingarrDbContext _dbContext;
    private readonly Mock<ISettingService> _settingService = new();
    private readonly Mock<ITranslationHealthService> _healthService = new();
    private readonly Mock<ICostService> _costService = new();

    public DailyTrendStatisticsTests()
    {
        _dbContext = new LingarrDbContext(new DbContextOptionsBuilder<LingarrDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        _costService.Setup(c => c.GetRates()).ReturnsAsync(new Dictionary<string, decimal>());
    }

    public void Dispose() => _dbContext.Database.EnsureDeleted();

    private StatisticsService CreateService() =>
        new(_dbContext, _settingService.Object, _healthService.Object, _costService.Object);

    private static TranslationRequest Request(
        DateTimeOffset? completedAt,
        string? serviceType = "openai",
        int? characters = 1_000_000,
        string? translatedSubtitle = "/out/a.zh.srt") => new()
    {
        Title = "T",
        SourceLanguage = "en",
        TargetLanguage = "zh",
        MediaType = MediaType.Movie,
        Status = completedAt is null ? TranslationStatus.InProgress : TranslationStatus.Completed,
        CompletedAt = completedAt,
        ServiceType = serviceType,
        CharactersTranslated = characters,
        TranslatedSubtitle = translatedSubtitle
    };

    /// <summary>Seeds rows with the desired CompletedAt (a second pass is needed
    /// because SaveChangesAsync stamps CreatedAt, and CompletedAt is settable).</summary>
    private async Task SeedAsync(params TranslationRequest[] requests)
    {
        _dbContext.TranslationRequests.AddRange(requests);
        await _dbContext.SaveChangesAsync();
    }

    [Fact]
    public async Task BucketsByLocalDay_AndZeroFillsMissingDays()
    {
        // Anchor seeds to local noon so the -1h offset below can never cross
        // a day boundary regardless of the host timezone or the wall clock.
        var localNow = DateTimeOffset.Now;
        var todayNoon = new DateTimeOffset(localNow.Date.AddHours(12), localNow.Offset);
        await SeedAsync(
            Request(todayNoon.AddDays(-2).AddHours(-1), characters: null),
            Request(todayNoon.AddDays(-2), characters: null),
            Request(todayNoon, characters: null));

        var trend = await CreateService().GetDailyTrendStatistics(14);

        Assert.Equal(14, trend.Days.Count);
        Assert.Equal(1, trend.Days[^1].WrittenCount); // today bucket
        Assert.Equal(2, trend.Days[^3].WrittenCount); // two days ago
        Assert.Equal(0, trend.Days[^4].WrittenCount); // zero-filled
        Assert.All(trend.Days, point => Assert.Equal(0.0, point.Cost!.Value));
    }

    [Fact]
    public async Task PricesDailyCostsFromProviderRates()
    {
        // openai: $2 per 1M characters
        _costService.Setup(c => c.GetRates())
            .ReturnsAsync(new Dictionary<string, decimal> { ["openai"] = 2m });

        var today = DateTimeOffset.UtcNow;
        await SeedAsync(
            Request(today, characters: 1_000_000),
            Request(today.AddDays(-1), characters: 500_000));

        var trend = await CreateService().GetDailyTrendStatistics(14);

        Assert.True(trend.RatesConfigured);
        Assert.Equal(3.0, trend.TotalCost!.Value);
        Assert.Equal(2.0, trend.Days[^1].Cost!.Value);
        Assert.Equal(1.0, trend.Days[^2].Cost!.Value);
    }

    [Fact]
    public async Task UnpricedUsageMakesCostsUnknown_NeverZero()
    {
        var today = DateTimeOffset.UtcNow;
        await SeedAsync(
            Request(today, serviceType: "openai"),
            Request(today.AddDays(-1), serviceType: "ollama")); // ollama has no rate

        var trend = await CreateService().GetDailyTrendStatistics(14);

        Assert.False(trend.RatesConfigured);
        Assert.Null(trend.TotalCost);
        Assert.All(trend.Days, point => Assert.Null(point.Cost));
        // write-backs stay countable even when cost is unknown
        Assert.Equal(2, trend.TotalWritten);
    }

    [Fact]
    public async Task WrittenCount_RequiresWrittenFile_CompletedOnly()
    {
        var today = DateTimeOffset.UtcNow;
        await SeedAsync(
            Request(today, translatedSubtitle: "/out/a.zh.srt"),
            Request(today, translatedSubtitle: null), // completed but no file
            Request(null)); // in progress, no CompletedAt

        var trend = await CreateService().GetDailyTrendStatistics(14);

        Assert.Equal(1, trend.TotalWritten);
    }

    [Fact]
    public async Task WindowExcludesOlderRequests()
    {
        await SeedAsync(Request(DateTimeOffset.UtcNow.AddDays(-30)));

        var trend = await CreateService().GetDailyTrendStatistics(14);

        Assert.Equal(0, trend.TotalWritten);
        Assert.All(trend.Days, point => Assert.Equal(0, point.WrittenCount));
    }

    [Fact]
    public async Task EmptyDatabase_YieldsZeroFilledSeries()
    {
        var trend = await CreateService().GetDailyTrendStatistics(14);

        Assert.Equal(14, trend.Days.Count);
        Assert.Equal(0, trend.TotalWritten);
        Assert.Equal(0.0, trend.TotalCost!.Value);
        Assert.True(trend.RatesConfigured);
        Assert.All(trend.Days, point =>
        {
            Assert.Equal(0, point.WrittenCount);
            Assert.Equal(0.0, point.Cost!.Value);
        });
    }
}
