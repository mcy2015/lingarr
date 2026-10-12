using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Lingarr.Core.Data;
using Lingarr.Core.Entities;
using Lingarr.Core.Enum;
using Lingarr.Server.Interfaces.Services;
using Lingarr.Server.Models;
using Lingarr.Server.Models.Api;
using Lingarr.Server.Services;
using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;

namespace Lingarr.Server.Tests.Services;

public class CostServiceTests : IDisposable
{
    private readonly LingarrDbContext _dbContext;
    private readonly Mock<ISettingService> _settingService = new();

    public CostServiceTests()
    {
        _dbContext = new LingarrDbContext(new DbContextOptionsBuilder<LingarrDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
    }

    public void Dispose() => _dbContext.Database.EnsureDeleted();

    private CostService CreateService() => new(_dbContext, _settingService.Object);

    private void SetupRates(params (string Provider, decimal Rate)[] rates)
    {
        _settingService
            .Setup(s => s.GetSettingAsJson<ProviderRate>(It.IsAny<string>()))
            .ReturnsAsync(rates.Select(r => new ProviderRate
            {
                Provider = r.Provider,
                RatePerMillion = r.Rate
            }).ToList());
    }

    private async Task<TranslationRequest> SeedAsync(
        string serviceType,
        TranslationStatus status,
        int characters,
        DateTimeOffset completedAt)
    {
        var request = new TranslationRequest
        {
            Title = "T",
            SourceLanguage = "en",
            TargetLanguage = "nl",
            MediaType = MediaType.Movie,
            Status = status,
            ServiceType = serviceType,
            CharactersTranslated = characters,
            CompletedAt = status == TranslationStatus.Completed ? completedAt : null
        };
        _dbContext.TranslationRequests.Add(request);
        await _dbContext.SaveChangesAsync();
        return request;
    }

    [Fact]
    public async Task SumsProviderCostsForTheMonth()
    {
        var now = DateTimeOffset.UtcNow;
        await SeedAsync("openai", TranslationStatus.Completed, 1_000_000, now);
        await SeedAsync("openai", TranslationStatus.Completed, 500_000, now);
        await SeedAsync("deepl", TranslationStatus.Completed, 2_000_000, now);
        SetupRates(("openai", 2.0m), ("deepl", 25.0m));

        var summary = await CreateService().GetCostSummary(null, null);

        // openai 1.5M chars × $2 + deepl 2M chars × $25
        Assert.Equal(53.0, summary.TotalCost);
        Assert.True(summary.RatesConfigured);
        var openai = summary.Providers.Single(p => p.Provider == "openai");
        Assert.Equal(2, openai.Requests);
        Assert.Equal(1_500_000, openai.Characters);
        Assert.Equal(3.0, openai.Cost);
        var deepl = summary.Providers.Single(p => p.Provider == "deepl");
        Assert.Equal(50.0, deepl.Cost);
    }

    [Fact]
    public async Task UnpricedProviderMakesTotalNull()
    {
        var now = DateTimeOffset.UtcNow;
        await SeedAsync("openai", TranslationStatus.Completed, 1_000_000, now);
        await SeedAsync("mystery", TranslationStatus.Completed, 100_000, now);
        SetupRates(("openai", 2.0m));

        var summary = await CreateService().GetCostSummary(null, null);

        Assert.Null(summary.TotalCost);
        Assert.False(summary.RatesConfigured);
        Assert.Equal(2.0, summary.Providers.Single(p => p.Provider == "openai").Cost);
        Assert.Null(summary.Providers.Single(p => p.Provider == "mystery").Cost);
    }

    [Fact]
    public async Task IgnoresRequestsOutsideTheMonthAndWithoutCharacters()
    {
        var now = DateTimeOffset.UtcNow;
        await SeedAsync("openai", TranslationStatus.Completed, 1_000_000, now);
        await SeedAsync("openai", TranslationStatus.Completed, 9_999_999, now.AddMonths(-2));
        await SeedAsync("openai", TranslationStatus.InProgress, 9_999_999, now);
        SetupRates(("openai", 2.0m));

        var summary = await CreateService().GetCostSummary(now.Year, now.Month);

        Assert.Equal(2.0, summary.TotalCost);
        Assert.Single(summary.Providers);
    }

    [Fact]
    public async Task ProviderMonthlyCostReturnsThatProvidersCost()
    {
        var now = DateTimeOffset.UtcNow;
        await SeedAsync("openai", TranslationStatus.Completed, 1_000_000, now);
        await SeedAsync("deepl", TranslationStatus.Completed, 1_000_000, now);
        SetupRates(("openai", 2.0m), ("deepl", 25.0m));

        var cost = await CreateService().GetProviderMonthlyCostAsync("deepl");

        Assert.Equal(25.0, cost);
    }

    [Fact]
    public async Task NoUsageYieldsEmptySummaryWithNullTotal()
    {
        SetupRates(("openai", 2.0m));

        var summary = await CreateService().GetCostSummary(null, null);

        Assert.Empty(summary.Providers);
        Assert.Equal(0, summary.TotalCost);
        Assert.True(summary.RatesConfigured);
    }
}
