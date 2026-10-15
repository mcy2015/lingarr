using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Lingarr.Core.Configuration;
using Lingarr.Core.Data;
using Lingarr.Core.Entities;
using Lingarr.Core.Enum;
using Lingarr.Server.Modules.Settings;
using Lingarr.Server.Modules.Statistics;
using Lingarr.Server.Modules.Translation;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;

namespace Lingarr.Server.Tests.Services;

/// <summary>
/// SQL-translation coverage for the pushed-down daily trend aggregation
/// (scale audit P1-10): the per-day GROUP BY provider (nullable key),
/// conditional counts and character sums run against a real SQLite schema
/// here, where InMemory would silently skip translatability checks.
/// </summary>
public class DailyTrendStatisticsSqliteTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly LingarrDbContext _dbContext;
    private readonly Mock<ISettingService> _settingService = new();
    private readonly Mock<ITranslationHealthService> _healthService = new();
    private readonly Mock<ICostService> _costService = new();

    public DailyTrendStatisticsSqliteTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _dbContext = new LingarrDbContext(new DbContextOptionsBuilder<LingarrDbContext>()
            .UseSqlite(_connection)
            .Options);
        _dbContext.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        _connection.Dispose();
    }

    private StatisticsService CreateService() =>
        new(_dbContext, _settingService.Object, _healthService.Object, _costService.Object);

    private async Task SeedCompletedAsync(
        DateTimeOffset completedAt,
        string? serviceType,
        int? characters,
        string? translatedSubtitle = "/out/a.zh.srt")
    {
        _dbContext.TranslationRequests.Add(new TranslationRequest
        {
            Title = "T",
            SourceLanguage = "en",
            TargetLanguage = "zh",
            MediaType = MediaType.Movie,
            Status = TranslationStatus.Completed,
            CompletedAt = completedAt,
            ServiceType = serviceType,
            CharactersTranslated = characters,
            TranslatedSubtitle = translatedSubtitle
        });
        await _dbContext.SaveChangesAsync();
    }

    /// <summary>Midday of a local day as UTC — safe from midnight/DST edges
    /// under whichever time zone the test host runs.</summary>
    private static DateTimeOffset MiddayUtc(DateOnly localDate)
    {
        var timeZone = TimeZoneConfiguration.Current;
        var local = localDate.ToDateTime(TimeOnly.MinValue).AddHours(12);
        return new DateTimeOffset(local, timeZone.GetUtcOffset(local)).ToUniversalTime();
    }

    private static DateOnly TodayLocal =>
        DateOnly.FromDateTime(
            TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, TimeZoneConfiguration.Current).DateTime);

    [Fact]
    public async Task GroupsPerDayAndProviderInSql()
    {
        // openai at $2 per 1M characters
        _costService.Setup(c => c.GetRates())
            .ReturnsAsync(new Dictionary<string, decimal> { ["openai"] = 2m });

        var today = TodayLocal;
        await SeedCompletedAsync(MiddayUtc(today), "openai", 1_000_000, "/out/a.zh.srt");
        await SeedCompletedAsync(MiddayUtc(today.AddDays(-1)), "openai", 500_000, null);
        // still running: no CompletedAt, never part of the trend
        _dbContext.TranslationRequests.Add(new TranslationRequest
        {
            Title = "T",
            SourceLanguage = "en",
            TargetLanguage = "zh",
            MediaType = MediaType.Movie,
            Status = TranslationStatus.InProgress,
            ServiceType = "openai",
            CharactersTranslated = 9_999_999
        });
        await _dbContext.SaveChangesAsync();

        var trend = await CreateService().GetDailyTrendStatistics(14);

        Assert.Equal(14, trend.Days.Count);
        Assert.True(trend.RatesConfigured);
        Assert.Equal(3.0, trend.TotalCost!.Value);
        Assert.Equal(1, trend.TotalWritten);
        Assert.Equal(2.0, trend.Days[^1].Cost!.Value);
        Assert.Equal(1, trend.Days[^1].WrittenCount);
        Assert.Equal(1.0, trend.Days[^2].Cost!.Value);
        Assert.Equal(0, trend.Days[^2].WrittenCount);
        Assert.Equal(0.0, trend.Days[^3].Cost!.Value);
        Assert.Equal(0, trend.Days[^3].WrittenCount);
    }

    [Fact]
    public async Task NullProviderRowsCountAsWritten_ButNeverPriceCharacters()
    {
        // rates deliberately empty: characters are absent on both rows, so
        // nothing may flag the window as unpriced.
        _costService.Setup(c => c.GetRates())
            .ReturnsAsync(new Dictionary<string, decimal>());

        var today = TodayLocal;
        await SeedCompletedAsync(MiddayUtc(today), null, null, "/out/a.zh.srt");
        await SeedCompletedAsync(MiddayUtc(today), "openai", null, "/out/b.zh.srt");

        var trend = await CreateService().GetDailyTrendStatistics(14);

        Assert.True(trend.RatesConfigured);
        Assert.Equal(0.0, trend.TotalCost!.Value);
        Assert.Equal(2, trend.TotalWritten);
        Assert.Equal(2, trend.Days[^1].WrittenCount);
        Assert.Equal(0.0, trend.Days[^1].Cost!.Value);
    }
}
