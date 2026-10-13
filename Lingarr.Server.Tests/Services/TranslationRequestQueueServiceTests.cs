using System;
using System.Threading;
using System.Threading.Tasks;
using Lingarr.Core.Data;
using Lingarr.Core.Entities;
using Lingarr.Core.Enum;
using Lingarr.Server.Modules.Subtitle;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;
using Lingarr.Server.Modules.Settings;
using Lingarr.Server.Modules.Translation;

namespace Lingarr.Server.Tests.Services;

/// <summary>
/// Regression tests for the EnqueueRequest entity copy: the persisted row must
/// carry every field from the staged request. It already dropped Source once
/// and ContentHash once - fields the queue UI and import dedup depend on.
/// </summary>
public class TranslationRequestQueueServiceTests : IDisposable
{
    private readonly LingarrDbContext _dbContext;
    private readonly TranslationRequestQueueService _service;
    private readonly Mock<ITranslationRequestEventService> _eventService = new();

    public TranslationRequestQueueServiceTests()
    {
        _dbContext = new LingarrDbContext(new DbContextOptionsBuilder<LingarrDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

        var jobQueue = new Mock<IJobQueue>();
        jobQueue.Setup(q => q.EnqueueTranslationJob(It.IsAny<TranslationRequest>())).Returns("job-1");

        _service = new TranslationRequestQueueService(
            _dbContext,
            jobQueue.Object,
            new Mock<IAsyncTranslationRegistry>().Object,
            new Mock<ITranslationRequestQueryService>().Object,
            new Mock<IContentTranslationService>().Object,
            _eventService.Object,
            new Mock<IProgressService>().Object,
            new Mock<ISettingService>().Object,
            new Mock<ISubtitleService>().Object,
            Mock.Of<ILogger<TranslationRequestQueueService>>());
    }

    public void Dispose() => _dbContext.Database.EnsureDeleted();

    [Fact]
    public async Task CreateImportRequest_PersistsSourceAndContentHash()
    {
        var id = await _service.CreateImportRequest(
            "/imports/Show.S01E05.chs.srt", "Show - S01E05 - Title", "en", "zh",
            mediaId: 10, mediaType: MediaType.Episode, contentHash: "abc123");

        var row = await _dbContext.TranslationRequests.SingleAsync(r => r.Id == id);

        Assert.Equal(TranslationRequestSource.Import, row.Source);
        Assert.Equal("abc123", row.ContentHash);
        Assert.Equal(10, row.MediaId);
        Assert.Equal(MediaType.Episode, row.MediaType);
        Assert.Equal(TranslationStatus.Pending, row.Status);
    }

    [Fact]
    public async Task EnqueueRequest_EnqueuedTimelineEventCarriesNoMessage()
    {
        // QA P3-2: the Enqueued row used to carry the Hangfire job id, which
        // reads as noise in the UI. Queue-position data is unavailable, so the
        // message must stay empty; the client copy names the queue instead.
        await _service.CreateImportRequest("/imports/loose.srt", "loose", "en", "zh",
            mediaId: null, mediaType: MediaType.Movie);

        _eventService.Verify(e => e.LogTimelineEvent(
            It.IsAny<int>(),
            TranslationEventType.Enqueued,
            TranslationStatus.Pending,
            null,
            null), Times.Once);
    }

    [Fact]
    public async Task CreateImportRequest_UnboundRequest_KeepsNullMediaAndHash()
    {
        var id = await _service.CreateImportRequest(
            "/imports/loose.srt", "loose", "en", "zh", mediaId: null, mediaType: MediaType.Movie);

        var row = await _dbContext.TranslationRequests.SingleAsync(r => r.Id == id);

        Assert.Null(row.MediaId);
        Assert.Null(row.ContentHash);
        Assert.Equal(TranslationRequestSource.Import, row.Source);
    }
}
