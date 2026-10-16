using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Lingarr.Core.Data;
using Lingarr.Core.Entities;
using Lingarr.Core.Enum;
using Lingarr.Server.Hubs;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;
using Lingarr.Server.Modules.Subtitle;
using Lingarr.Server.Modules.Translation;

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
            new Mock<IHubContext<TranslationRequestsHub>>().Object,
            new Mock<ITranslationRequestEventService>().Object,
            new Mock<ISubtitleService>().Object);
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

    private static TranslationRequest MediaRequest(int mediaId, TranslationStatus status) => new()
    {
        Title = $"Request media {mediaId}",
        SourceLanguage = "en",
        TargetLanguage = "nl",
        MediaType = MediaType.Movie,
        MediaId = mediaId,
        Status = status
    };

    [Fact]
    public async Task GetTranslationRequests_MediaIdsFilter_MatchesOnlyTheNumericIds()
    {
        await SeedAsync(
            MediaRequest(12, TranslationStatus.Completed),
            MediaRequest(34, TranslationStatus.Pending),
            MediaRequest(99, TranslationStatus.Completed));

        var result = await _service.GetTranslationRequests(
            null, "CreatedAt", false, 1, 20, mediaIds: "12, 34");

        Assert.Equal(2, result.TotalCount);
        Assert.Equal(
            new[] { 12, 34 }.OrderBy(id => id),
            result.Items.Select(r => r.MediaId!.Value).OrderBy(id => id));
    }

    [Fact]
    public async Task GetTranslationRequests_MediaIdsFilter_SkipsNonNumericTokens()
    {
        // mediaIds is free-form query input — a malformed token must not
        // throw (int.Parse used to 500 the whole history list).
        await SeedAsync(
            MediaRequest(12, TranslationStatus.Completed),
            MediaRequest(34, TranslationStatus.Pending));

        var result = await _service.GetTranslationRequests(
            null, "CreatedAt", false, 1, 20, mediaIds: "12,abc,34");

        Assert.Equal(2, result.TotalCount);
        Assert.DoesNotContain(result.Items, r => r.MediaId is null);

        // no numeric token at all: same as an empty param — no filter
        var unfiltered = await _service.GetTranslationRequests(
            null, "CreatedAt", false, 1, 20, mediaIds: "abc,def");

        Assert.Equal(2, unfiltered.TotalCount);
    }
}
