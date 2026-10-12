using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Lingarr.Core.Data;
using Lingarr.Core.Entities;
using Lingarr.Core.Enum;
using Lingarr.Server.Hubs;
using Lingarr.Server.Interfaces.Services;
using Lingarr.Server.Services;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;

namespace Lingarr.Server.Tests.Services;

public class TranslationRequestQueryServiceTests : IDisposable
{
    private readonly LingarrDbContext _dbContext;
    private readonly TranslationRequestQueryService _service;

    public TranslationRequestQueryServiceTests()
    {
        _dbContext = new LingarrDbContext(new DbContextOptionsBuilder<LingarrDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

        _service = new TranslationRequestQueryService(
            _dbContext,
            new Mock<IHubContext<TranslationRequestsHub>>(,
            new Mock<ISubtitleService>().Object).Object,
            new Mock<ITranslationRequestEventService>().Object);
    }

    public void Dispose() => _dbContext.Database.EnsureDeleted();

    private async Task SeedAsync(params TranslationRequest[] requests)
    {
        foreach (var request in requests)
        {
            _dbContext.TranslationRequests.Add(request);
        }
        await _dbContext.SaveChangesAsync();
    }

    private static TranslationRequest Request(
        int id,
        TranslationStatus status,
        TranslationRequestSource source = TranslationRequestSource.Library,
        string target = "nl")
    {
        return new TranslationRequest
        {
            Title = $"Request {id}",
            SourceLanguage = "en",
            TargetLanguage = target,
            MediaType = MediaType.Movie,
            Status = status,
            Source = source
        };
    }

    [Fact]
    public async Task GetTranslationRequests_StatusFilter_ReturnsOnlyMatchingStatus()
    {
        await SeedAsync(
            Request(1, TranslationStatus.Pending),
            Request(2, TranslationStatus.Completed),
            Request(3, TranslationStatus.Failed));

        var result = await _service.GetTranslationRequests(
            null, "CreatedAt", false, 1, 20, TranslationStatus.Completed);

        var item = Assert.Single(result.Items);
        Assert.Equal(TranslationStatus.Completed, item.Status);
        Assert.Equal(1, result.TotalCount);
    }

    [Fact]
    public async Task GetTranslationRequests_NoFilter_ReturnsAllWithSource()
    {
        await SeedAsync(
            Request(1, TranslationStatus.Pending),
            Request(2, TranslationStatus.Completed, TranslationRequestSource.Import));

        var result = await _service.GetTranslationRequests(null, "CreatedAt", false, 1, 20);

        Assert.Equal(2, result.TotalCount);
        Assert.Contains(result.Items, item => item.Source == TranslationRequestSource.Import);
        Assert.Contains(result.Items, item => item.Source == TranslationRequestSource.Library);
    }

    [Fact]
    public async Task GetStatusCounts_GroupsByStatus()
    {
        await SeedAsync(
            Request(1, TranslationStatus.Pending),
            Request(2, TranslationStatus.Pending),
            Request(3, TranslationStatus.Failed));

        var counts = await _service.GetStatusCounts();

        Assert.Equal(2, counts[TranslationStatus.Pending]);
        Assert.Equal(1, counts[TranslationStatus.Failed]);
        Assert.False(counts.ContainsKey(TranslationStatus.Completed));
    }
}
