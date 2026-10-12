using System;
using System.Linq;
using System.Threading.Tasks;
using Lingarr.Core.Data;
using Lingarr.Core.Entities;
using Lingarr.Core.Enum;
using Lingarr.Server.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Lingarr.Server.Tests.Services;

public class TranslationRequestEventServiceTests : IDisposable
{
    private readonly LingarrDbContext _dbContext;
    private readonly TranslationRequestEventService _service;

    public TranslationRequestEventServiceTests()
    {
        _dbContext = new LingarrDbContext(new DbContextOptionsBuilder<LingarrDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        _service = new TranslationRequestEventService(_dbContext);
    }

    public void Dispose() => _dbContext.Database.EnsureDeleted();

    [Fact]
    public async Task GetRecentEvents_ReturnsNewestFirst_WithRequestTitles()
    {
        var request = new TranslationRequest
        {
            Title = "Shōgun — S01E05",
            SourceLanguage = "en",
            TargetLanguage = "nl",
            MediaType = MediaType.Episode,
            Status = TranslationStatus.Completed
        };
        _dbContext.TranslationRequests.Add(request);
        await _dbContext.SaveChangesAsync();

        await _service.LogEvent(request.Id, TranslationStatus.Pending);
        await _service.LogEvent(request.Id, TranslationStatus.InProgress);
        await _service.LogEvent(request.Id, TranslationStatus.Completed, "Written back");

        var events = await _service.GetRecentEvents(10);

        Assert.Equal(3, events.Count);
        Assert.Equal(TranslationStatus.Completed, events[0].Status);
        Assert.Equal("Shōgun — S01E05", events[0].RequestTitle);
        Assert.Equal(request.Id, events[0].RequestId);
        Assert.Equal("Written back", events[0].Message);
        Assert.Equal(TranslationStatus.Pending, events[^1].Status);
    }

    [Fact]
    public async Task GetRecentEvents_RespectsTake()
    {
        var request = new TranslationRequest
        {
            Title = "T",
            SourceLanguage = "en",
            TargetLanguage = "nl",
            MediaType = MediaType.Movie,
            Status = TranslationStatus.Completed
        };
        _dbContext.TranslationRequests.Add(request);
        await _dbContext.SaveChangesAsync();

        await _service.LogEvent(request.Id, TranslationStatus.Pending);
        await _service.LogEvent(request.Id, TranslationStatus.InProgress);

        var events = await _service.GetRecentEvents(1);

        Assert.Single(events);
        Assert.Equal(TranslationStatus.InProgress, events[0].Status);
    }
}
