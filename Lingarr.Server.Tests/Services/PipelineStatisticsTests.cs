using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Lingarr.Core.Data;
using Lingarr.Core.Entities;
using Lingarr.Core.Enum;
using Lingarr.Server.Models;
using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;
using Lingarr.Server.Modules.Settings;
using Lingarr.Server.Modules.Statistics;
using Lingarr.Server.Modules.Translation;

namespace Lingarr.Server.Tests.Services;

public class PipelineStatisticsTests : IDisposable
{
    private readonly LingarrDbContext _dbContext;
    private readonly Mock<ISettingService> _settingService = new();
    private readonly Mock<ITranslationHealthService> _healthService = new();
    private readonly Mock<ICostService> _costService = new();

    public PipelineStatisticsTests()
    {
        _dbContext = new LingarrDbContext(new DbContextOptionsBuilder<LingarrDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        // TimeZoneConfiguration resolves TZ (UTC when unset, as in the SDK
        // container), so month/30-day boundaries align with UtcNow seeding.

        _settingService
            .Setup(s => s.GetSettingAsJson<TargetLanguage>(It.IsAny<string>()))
            .ReturnsAsync(new List<TargetLanguage> { new() { Name = "Dutch", Code = "nl" } });
        _healthService
            .Setup(h => h.GetOnlineServiceCountAsync())
            .ReturnsAsync(2);
        _costService
            .Setup(c => c.GetCurrentMonthTotalCostAsync())
            .ReturnsAsync((double?)null);
    }

    public void Dispose() => _dbContext.Database.EnsureDeleted();

    private StatisticsService CreateService() =>
        new(_dbContext, _settingService.Object, _healthService.Object, _costService.Object);

    private async Task SeedStatisticsAsync(int episodes, int movies, Dictionary<string, int> byLanguage)
    {
        _dbContext.Statistics.Add(new Statistics
        {
            TotalEpisodes = episodes,
            TotalMovies = movies,
            SubtitlesByLanguage = byLanguage
        });
        await _dbContext.SaveChangesAsync();
    }

    private static TranslationRequest Request(
        TranslationStatus status,
        DateTimeOffset? completedAt = null,
        string? translatedSubtitle = null)
    {
        return new TranslationRequest
        {
            Title = "T",
            SourceLanguage = "en",
            TargetLanguage = "nl",
            MediaType = MediaType.Movie,
            Status = status,
            CompletedAt = completedAt,
            TranslatedSubtitle = translatedSubtitle
        };
    }

    /// <summary>
    /// SaveChangesAsync stamps CreatedAt=now on Added entities
    /// (LingarrDbContext.UpdateTimestamps), so historical rows need a
    /// second pass that writes CreatedAt on the now-Modified entities.
    /// </summary>
    private async Task SaveAsync(params (TranslationRequest request, DateTimeOffset? createdAt)[] seeds)
    {
        _dbContext.TranslationRequests.AddRange(seeds.Select(seed => seed.request));
        await _dbContext.SaveChangesAsync();
        foreach (var (request, createdAt) in seeds)
        {
            if (createdAt is { } value)
            {
                request.CreatedAt = value;
            }
        }
        await _dbContext.SaveChangesAsync();
    }

    [Fact]
    public async Task GetPipelineStatistics_ComputesMediaCountAndMissingSubtitles()
    {
        // 10 episodes + 2 movies, only 4 nl subtitle files on disk
        await SeedStatisticsAsync(episodes: 10, movies: 2, new Dictionary<string, int> { ["nl"] = 4 });

        var pipeline = await CreateService().GetPipelineStatistics();

        Assert.Equal(12, pipeline.MediaCount);
        Assert.Equal(8, pipeline.MissingSubtitles); // 12 media - 4 nl files
        Assert.Equal(2, pipeline.ServicesOnline);
        Assert.Equal(0, pipeline.QueueDepth); // no Hangfire storage in tests
        Assert.Null(pipeline.MonthlyCost);
    }

    [Fact]
    public async Task GetPipelineStatistics_MissingSubtitles_FloorsAtZeroPerLanguage()
    {
        // more subtitle files than media (multi-language releases) must not go negative
        await SeedStatisticsAsync(episodes: 5, movies: 0, new Dictionary<string, int> { ["nl"] = 9 });

        var pipeline = await CreateService().GetPipelineStatistics();

        Assert.Equal(0, pipeline.MissingSubtitles);
    }

    [Fact]
    public async Task GetPipelineStatistics_SuccessRate_OverLast30DaysOnly()
    {
        await SeedStatisticsAsync(0, 0, new Dictionary<string, int>());
        await SaveAsync(
            (Request(TranslationStatus.Completed), DateTimeOffset.UtcNow.AddDays(-1)),
            (Request(TranslationStatus.Failed), DateTimeOffset.UtcNow.AddDays(-2)),
            // outside the window - must not affect the rate
            (Request(TranslationStatus.Failed), DateTimeOffset.UtcNow.AddDays(-40)));

        var pipeline = await CreateService().GetPipelineStatistics();

        Assert.Equal(50, pipeline.SuccessRate30Days);
    }

    [Fact]
    public async Task GetPipelineStatistics_SuccessRate_NullWithoutTerminalOutcomes()
    {
        await SeedStatisticsAsync(0, 0, new Dictionary<string, int>());

        var pipeline = await CreateService().GetPipelineStatistics();

        Assert.Null(pipeline.SuccessRate30Days);
    }

    [Fact]
    public async Task GetPipelineStatistics_WrittenThisMonth_CountsWriteBacksOnly()
    {
        await SeedStatisticsAsync(0, 0, new Dictionary<string, int>());
        await SaveAsync(
            (Request(TranslationStatus.Completed,
                    completedAt: DateTimeOffset.UtcNow.AddDays(-1), translatedSubtitle: "/tv/a.nl.srt"),
                DateTimeOffset.UtcNow.AddDays(-1)),
            // completed this month but no written file
            (Request(TranslationStatus.Completed, completedAt: DateTimeOffset.UtcNow.AddDays(-1)),
                DateTimeOffset.UtcNow.AddDays(-1)),
            // written back but last month
            (Request(TranslationStatus.Completed,
                    completedAt: DateTimeOffset.UtcNow.AddDays(-35), translatedSubtitle: "/tv/b.nl.srt"),
                DateTimeOffset.UtcNow.AddDays(-35)));

        var pipeline = await CreateService().GetPipelineStatistics();

        Assert.Equal(1, pipeline.WrittenThisMonth);
    }
}
