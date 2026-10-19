using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Lingarr.Contracts.Models;
using Lingarr.Core.Configuration;
using Lingarr.Core.Data;
using Lingarr.Core.Entities;
using Lingarr.Core.Enum;
using Lingarr.Server.Models.Api;
using Lingarr.Server.Models.FileSystem;
using Lingarr.Server.Modules.Subtitle;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;
using Lingarr.Server.Modules.Media;
using Lingarr.Server.Modules.Settings;
using Lingarr.Server.Modules.Translation;

namespace Lingarr.Server.Tests.Services;

/// <summary>
/// Regression tests for the EnqueueRequest entity copy: the persisted row must
/// carry every field from the staged request. It already dropped Source once
/// and ContentHash once - fields the queue UI and import dedup depend on.
/// R11 adds the bulk summary coverage: created/skip counting and reason codes.
/// </summary>
public class TranslationRequestQueueServiceTests : IDisposable
{
    private readonly LingarrDbContext _dbContext;
    private readonly TranslationRequestQueueService _service;
    private readonly Mock<ITranslationRequestEventService> _eventService = new();
    private readonly Mock<ISettingService> _settingService = new();
    private readonly Mock<ISubtitleService> _subtitleService = new();
    private readonly Mock<IContentTranslationService> _contentTranslationService = new();

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
            _contentTranslationService.Object,
            _eventService.Object,
            new Mock<IProgressService>().Object,
            _settingService.Object,
            _subtitleService.Object,
            new Mock<IEmbeddedTrackService>().Object,
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

    // ── R11: bulk translate summary (created count + skip reason codes) ──

    /// <summary>
    /// Wires the settings/content/subtitle mocks the bulk path reads. The
    /// subtitle resolution is driven by <paramref name="sourceSubtitleFor"/>:
    /// it maps a (path, fileName) pair to the source subtitle file name that
    /// exists there, or null when the directory has no translatable source.
    /// </summary>
    private void SetupBulkPipeline(Func<string, string, string?> sourceSubtitleFor)
    {
        _settingService
            .Setup(settings => settings.GetSettingAsJson<SourceLanguage>(SettingKeys.Translation.SourceLanguages))
            .ReturnsAsync([new SourceLanguage { Name = "English", Code = "en" }]);
        _settingService
            .Setup(settings => settings.GetSetting(SettingKeys.Translation.IgnoreCaptions))
            .ReturnsAsync("false");
        _contentTranslationService
            .Setup(content => content.FormatMediaTitle(It.IsAny<int>(), It.IsAny<MediaType>()))
            .ReturnsAsync((int mediaId, MediaType _) => $"Title {mediaId}");
        _subtitleService
            .Setup(subtitles => subtitles.GetSubtitles(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync((string path, string fileName) =>
            {
                var source = sourceSubtitleFor(path, fileName);
                return source == null
                    ? new List<Subtitles>()
                    : [new Subtitles { Path = source, FileName = source, Language = "en", Format = ".srt" }];
            });
        _subtitleService
            .Setup(subtitles => subtitles.SelectSourceSubtitle(
                It.IsAny<List<Subtitles>>(), It.IsAny<HashSet<string>>(), It.IsAny<string>()))
            .Returns((List<Subtitles> subtitles, HashSet<string> _, string _) =>
                subtitles.Count == 0
                    ? null
                    : new SelectedSourceSubtitle
                    {
                        Subtitle = subtitles[0],
                        SourceLanguage = subtitles[0].Language,
                        AvailableLanguages = ["en"]
                    });
    }

    private static Movie NewMovie(string title, string? path, string? fileName) => new()
    {
        RadarrId = Random.Shared.Next(1000, 999_999),
        Title = title,
        Path = path,
        FileName = fileName,
        DateAdded = DateTime.UtcNow
    };

    private BulkTranslateRequest MovieBulk(params int[] mediaIds) => new()
    {
        MediaIds = [.. mediaIds],
        TargetLanguage = "zh",
        MediaType = MediaType.Movie
    };

    [Fact]
    public async Task CreateBulkRequest_MixedMovies_ReportsCreatedAndSkipReasonCodes()
    {
        SetupBulkPipeline((path, _) => path == "/movies/has-source" ? "/movies/has-source/Has.Source.2023.en.srt" : null);

        var hasSource = NewMovie("Has Source", "/movies/has-source", "Has.Source.2023.mkv");
        var noPath = NewMovie("No Path", null, "No.Path.2023.mkv");
        var noSource = NewMovie("No Source", "/movies/no-source", "No.Source.2023.mkv");
        _dbContext.Movies.AddRange(hasSource, noPath, noSource);
        await _dbContext.SaveChangesAsync();

        var summary = await _service.CreateBulkRequest(MovieBulk(hasSource.Id, noPath.Id, noSource.Id));

        Assert.Equal(1, summary.Created);
        Assert.Equal(2, summary.Skipped.Count);
        Assert.Contains(summary.Skipped, skip =>
            skip.MediaId == noPath.Id && skip.Title == "No Path" && skip.Reason == BulkSkip.NoPathReason);
        Assert.Contains(summary.Skipped, skip =>
            skip.MediaId == noSource.Id && skip.Title == "No Source" && skip.Reason == BulkSkip.NoSourceReason);

        var request = await _dbContext.TranslationRequests.SingleAsync();
        Assert.Equal(hasSource.Id, request.MediaId);
        Assert.Equal(TranslationStatus.Pending, request.Status);

        // Resubmitting the same selection: the has-source movie now deduplicates
        // against the active request, so nothing new is created — while the
        // no-path/no-source skips stay visible.
        var second = await _service.CreateBulkRequest(MovieBulk(hasSource.Id, noPath.Id, noSource.Id));
        Assert.Equal(0, second.Created);
        Assert.Equal(2, second.Skipped.Count);
        Assert.Equal(1, await _dbContext.TranslationRequests.CountAsync());
    }

    [Fact]
    public async Task CreateBulkRequest_ShowSkips_AggregateToSingleRowAndKeepProcessingEpisodes()
    {
        // The only skip trigger in this show is "no source" on E2, so the
        // aggregated reason is deterministic regardless of episode order; E1
        // after (or before) it must still produce a request.
        SetupBulkPipeline((_, fileName) => fileName == "E1.mkv" ? "/shows/mixed/S01/E1.en.srt" : null);

        var show = new Show
        {
            SonarrId = 11,
            Title = "Mixed Show",
            Path = "/shows/mixed",
            DateAdded = DateTime.UtcNow
        };
        var season = new Season { SeasonNumber = 1, Path = "/shows/mixed/S01", Show = show };
        var episodeWithSource = new Episode
        {
            SonarrId = 101, EpisodeNumber = 1, Title = "E1", FileName = "E1.mkv",
            Path = "/shows/mixed/S01/E1.mkv", Season = season
        };
        var episodeNoSource = new Episode
        {
            SonarrId = 102, EpisodeNumber = 2, Title = "E2", FileName = "E2.mkv",
            Path = "/shows/mixed/S01/E2.mkv", Season = season
        };
        season.Episodes.AddRange([episodeWithSource, episodeNoSource]);
        show.Seasons.Add(season);
        _dbContext.Shows.Add(show);
        await _dbContext.SaveChangesAsync();

        var summary = await _service.CreateBulkRequest(new BulkTranslateRequest
        {
            MediaIds = [show.Id],
            TargetLanguage = "zh",
            MediaType = MediaType.Show
        });

        Assert.Equal(1, summary.Created);
        var skip = Assert.Single(summary.Skipped);
        Assert.Equal(show.Id, skip.MediaId);
        Assert.Equal("Mixed Show", skip.Title);
        Assert.Equal(BulkSkip.NoSourceReason, skip.Reason);

        // one skip row, but the translatable episode still got its request
        var request = await _dbContext.TranslationRequests.SingleAsync();
        Assert.Equal(episodeWithSource.Id, request.MediaId);
        Assert.Equal(MediaType.Episode, request.MediaType);
    }

    [Fact]
    public async Task CreateBulkRequest_ShowWithoutSeasonPath_ReportsNoPathSkip()
    {
        SetupBulkPipeline((_, _) => "/never/used.en.srt");

        var show = new Show
        {
            SonarrId = 12,
            Title = "Pathless Show",
            Path = "/shows/pathless",
            DateAdded = DateTime.UtcNow
        };
        var season = new Season { SeasonNumber = 1, Path = null, Show = show };
        season.Episodes.Add(new Episode
        {
            SonarrId = 201, EpisodeNumber = 1, Title = "E1", FileName = "E1.mkv",
            Path = "/shows/pathless/S01/E1.mkv", Season = season
        });
        show.Seasons.Add(season);
        _dbContext.Shows.Add(show);
        await _dbContext.SaveChangesAsync();

        var summary = await _service.CreateBulkRequest(new BulkTranslateRequest
        {
            MediaIds = [show.Id],
            TargetLanguage = "zh",
            MediaType = MediaType.Show
        });

        // the subtitle pipeline must not even run for a season without a path
        _subtitleService.Verify(
            subtitles => subtitles.GetSubtitles(It.IsAny<string>(), It.IsAny<string>()),
            Times.Never);

        Assert.Equal(0, summary.Created);
        var skip = Assert.Single(summary.Skipped);
        Assert.Equal(show.Id, skip.MediaId);
        Assert.Equal("Pathless Show", skip.Title);
        Assert.Equal(BulkSkip.NoPathReason, skip.Reason);
    }
}
