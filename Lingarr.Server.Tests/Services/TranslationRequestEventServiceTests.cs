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

    // ===== P1-2 typed timeline events =====

    [Fact]
    public async Task LogTimelineEvent_AlwaysWrites_RepeatedBatchEventsAreKept()
    {
        var request = await SeedRequestAsync();

        await _service.LogTimelineEvent(request.Id, TranslationEventType.BatchCompleted,
            TranslationStatus.InProgress, message: "batch 1/2 · 128/256 lines");
        await _service.LogTimelineEvent(request.Id, TranslationEventType.BatchCompleted,
            TranslationStatus.InProgress, message: "batch 2/2 · 256/256 lines");

        var events = await _service.GetEvents(request.Id);
        Assert.Equal(2, events.Count);
        Assert.All(events, e => Assert.Equal(TranslationEventType.BatchCompleted, e.EventType));
        Assert.Equal("batch 1/2 · 128/256 lines", events[0].Message);
    }

    [Fact]
    public async Task DegradedEvent_CarriesFallbackProviderAndEdge()
    {
        var request = await SeedRequestAsync();

        await _service.LogTimelineEvent(request.Id, TranslationEventType.ProviderFailed,
            TranslationStatus.InProgress, "gemini", "429 · TooManyRequests");
        await _service.LogTimelineEvent(request.Id, TranslationEventType.Degraded,
            TranslationStatus.InProgress, "openai", "gemini -> openai");

        var events = await _service.GetEvents(request.Id);
        Assert.Equal(2, events.Count);
        Assert.Equal("gemini", events[0].Provider);
        Assert.Equal(TranslationEventType.ProviderFailed, events[0].EventType);
        Assert.Equal("openai", events[1].Provider);
    }

    [Fact]
    public async Task GetEvents_ReturnsOldestFirst()
    {
        var request = await SeedRequestAsync();

        await _service.LogEvent(request.Id, TranslationStatus.Pending, eventType: TranslationEventType.Created);
        await _service.LogEvent(request.Id, TranslationStatus.InProgress, eventType: TranslationEventType.Started);
        await _service.LogEvent(request.Id, TranslationStatus.Completed, eventType: TranslationEventType.Completed);

        var events = await _service.GetEvents(request.Id);

        Assert.Equal(3, events.Count);
        Assert.Equal(TranslationEventType.Created, events[0].EventType);
        Assert.Equal(TranslationEventType.Started, events[1].EventType);
        Assert.Equal(TranslationEventType.Completed, events[2].EventType);
    }

    [Fact]
    public async Task GetRecentEvents_ExcludesBatchProgressButKeepsFallbackEvents()
    {
        var request = await SeedRequestAsync();

        await _service.LogEvent(request.Id, TranslationStatus.Pending, eventType: TranslationEventType.Created);
        await _service.LogTimelineEvent(request.Id, TranslationEventType.ProviderFailed,
            TranslationStatus.InProgress, "gemini", "429");
        await _service.LogTimelineEvent(request.Id, TranslationEventType.Degraded,
            TranslationStatus.InProgress, "openai", "gemini -> openai");
        await _service.LogTimelineEvent(request.Id, TranslationEventType.BatchCompleted,
            TranslationStatus.InProgress, message: "batch 1/2");
        await _service.LogTimelineEvent(request.Id, TranslationEventType.BatchCompleted,
            TranslationStatus.InProgress, message: "batch 2/2");

        var events = await _service.GetRecentEvents(10);

        // batch rows would drown the feed; failures/degradations are the story
        Assert.Equal(3, events.Count);
        Assert.DoesNotContain(events, e => e.EventType == TranslationEventType.BatchCompleted);
        Assert.Contains(events, e => e.EventType == TranslationEventType.Degraded);
    }

    [Fact]
    public async Task LogEvent_DedupIgnoresGranularTimelineRows()
    {
        var request = await SeedRequestAsync();

        // InProgress lifecycle event, then batch progress, then InProgress again
        // (e.g. resume after pause) — the resume must not be suppressed by the
        // batch row that happens to carry the same status.
        await _service.LogEvent(request.Id, TranslationStatus.InProgress, eventType: TranslationEventType.Started);
        await _service.LogTimelineEvent(request.Id, TranslationEventType.BatchCompleted,
            TranslationStatus.InProgress, message: "batch 1/2");
        await _service.LogEvent(request.Id, TranslationStatus.Paused);
        await _service.LogEvent(request.Id, TranslationStatus.InProgress);

        var events = await _service.GetEvents(request.Id);

        Assert.Equal(4, events.Count);
        Assert.Equal(TranslationStatus.InProgress, events[^1].Status);
        Assert.Null(events[^1].EventType); // plain status transition, not typed
    }

    [Fact]
    public async Task RequestExists_DistinguishesKnownIds()
    {
        var request = await SeedRequestAsync();

        Assert.True(await _service.RequestExists(request.Id));
        Assert.False(await _service.RequestExists(request.Id + 4242));
    }

    private async Task<TranslationRequest> SeedRequestAsync()
    {
        var request = new TranslationRequest
        {
            Title = "Shōgun — S01E05",
            SourceLanguage = "en",
            TargetLanguage = "zh",
            MediaType = MediaType.Episode,
            Status = TranslationStatus.InProgress
        };
        _dbContext.TranslationRequests.Add(request);
        await _dbContext.SaveChangesAsync();
        return request;
    }
}
