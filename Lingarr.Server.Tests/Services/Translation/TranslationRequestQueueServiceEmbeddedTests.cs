using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Lingarr.Core.Data;
using Lingarr.Core.Entities;
using Lingarr.Core.Enum;
using Lingarr.Server.Models.FileSystem;
using Lingarr.Server.Modules.Media;
using Lingarr.Server.Modules.Settings;
using Lingarr.Server.Modules.Subtitle;
using Lingarr.Server.Modules.Translation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Lingarr.Server.Tests.Services.Translation;

/// <summary>
/// F2b manual initiation through <see cref="TranslationRequestQueueService.CreateEmbeddedTrackRequest"/>:
/// the available-language union gate (target covered externally or by another
/// embedded track → <see cref="TargetLanguageAlreadyPresentException"/>),
/// track validation, and the happy path creating a regular pending request
/// from the prepared translation source.
/// </summary>
public sealed class TranslationRequestQueueServiceEmbeddedTests : IDisposable
{
    private readonly LingarrDbContext _dbContext;
    private readonly Mock<ISubtitleService> _subtitleServiceMock = new();
    private readonly Mock<IEmbeddedTrackService> _embeddedTrackServiceMock = new();
    private readonly TranslationRequestQueueService _queueService;
    private readonly List<Subtitles> _externalSubtitles = [];

    public TranslationRequestQueueServiceEmbeddedTests()
    {
        var options = new DbContextOptionsBuilder<LingarrDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _dbContext = new LingarrDbContext(options);

        var jobQueueMock = new Mock<IJobQueue>();
        jobQueueMock
            .Setup(queue => queue.EnqueueTranslationJob(It.IsAny<TranslationRequest>()))
            .Returns((TranslationRequest request) => $"job-{request.Id}");

        _subtitleServiceMock
            .Setup(service => service.GetSubtitles(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(() => _externalSubtitles.ToList());

        _embeddedTrackServiceMock
            .Setup(service => service.PrepareTranslationSource(
                It.IsAny<MediaType>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((MediaType mediaType, int mediaId, int streamIndex, CancellationToken _) =>
                $"/movies/prepared.{mediaId}.s{streamIndex}.srt");

        _queueService = new TranslationRequestQueueService(
            _dbContext,
            jobQueueMock.Object,
            new Mock<IAsyncTranslationRegistry>().Object,
            new Mock<ITranslationRequestQueryService>().Object,
            ContentTranslationMock(),
            new Mock<ITranslationRequestEventService>().Object,
            new Mock<IProgressService>().Object,
            new Mock<ISettingService>().Object,
            _subtitleServiceMock.Object,
            _embeddedTrackServiceMock.Object,
            NullLogger<TranslationRequestQueueService>.Instance);
    }

    private static IContentTranslationService ContentTranslationMock()
    {
        var mock = new Mock<IContentTranslationService>();
        mock.Setup(service => service.FormatMediaTitle(It.IsAny<int>(), It.IsAny<MediaType>()))
            .ReturnsAsync((int mediaId, MediaType _) => $"Movie {mediaId}");
        return mock.Object;
    }

    public void Dispose()
    {
        _dbContext.Dispose();
    }

    private async Task<Movie> CreateMovieWithTracks()
    {
        var movie = new Movie
        {
            Id = 7,
            RadarrId = 1,
            Title = "Dune",
            Path = "/movies/dune-2024",
            FileName = "Dune.Part.Two.2024",
            DateAdded = DateTimeOffset.UtcNow
        };
        _dbContext.Movies.Add(movie);
        _dbContext.MediaSubtitleTracks.AddRange(
            new MediaSubtitleTrack
            {
                MediaType = MediaType.Movie,
                MediaId = 7,
                StreamIndex = 2,
                Language = "en",
                RawLanguage = "eng",
                Title = "English",
                Codec = "srt",
                IsTextBased = true,
                FilePath = "/movies/dune-2024/Dune.Part.Two.2024.mkv"
            },
            new MediaSubtitleTrack
            {
                MediaType = MediaType.Movie,
                MediaId = 7,
                StreamIndex = 3,
                Language = "zh",
                RawLanguage = "chi",
                Title = "简体中文",
                Codec = "srt",
                IsTextBased = true,
                FilePath = "/movies/dune-2024/Dune.Part.Two.2024.mkv"
            },
            new MediaSubtitleTrack
            {
                MediaType = MediaType.Movie,
                MediaId = 7,
                StreamIndex = 4,
                Language = null,
                RawLanguage = "und",
                Title = null,
                Codec = "srt",
                IsTextBased = true,
                FilePath = "/movies/dune-2024/Dune.Part.Two.2024.mkv"
            },
            new MediaSubtitleTrack
            {
                MediaType = MediaType.Movie,
                MediaId = 7,
                StreamIndex = 5,
                Language = "zh-TW",
                RawLanguage = "zh-TW",
                Title = null,
                Codec = "pgs",
                IsTextBased = false,
                FilePath = "/movies/dune-2024/Dune.Part.Two.2024.mkv"
            });
        await _dbContext.SaveChangesAsync();
        return movie;
    }

    [Fact]
    public async Task CreateEmbeddedTrackRequest_CreatesPendingRequestFromPreparedSource()
    {
        await CreateMovieWithTracks();

        // ja is covered neither externally nor by an embedded track
        var requestId = await _queueService.CreateEmbeddedTrackRequest(MediaType.Movie, 7, 2, "ja");

        var request = await _dbContext.TranslationRequests.FindAsync(requestId);
        Assert.NotNull(request);
        Assert.Equal(TranslationStatus.Pending, request.Status);
        Assert.Equal(7, request.MediaId);
        Assert.Equal("en", request.SourceLanguage);
        Assert.Equal("ja", request.TargetLanguage);
        Assert.Equal("/movies/prepared.7.s2.srt", request.SubtitleToTranslate);

        _embeddedTrackServiceMock.Verify(service => service.PrepareTranslationSource(
            MediaType.Movie, 7, 2, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CreateEmbeddedTrackRequest_RejectsTargetCoveredByEmbeddedTrack()
    {
        await CreateMovieWithTracks();

        // stream 2 is en; another embedded track (stream 3) already carries zh
        var error = await Assert.ThrowsAsync<TargetLanguageAlreadyPresentException>(
            () => _queueService.CreateEmbeddedTrackRequest(MediaType.Movie, 7, 2, "zh"));

        Assert.Contains("zh", error.Message);
        _embeddedTrackServiceMock.Verify(service => service.PrepareTranslationSource(
            It.IsAny<MediaType>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CreateEmbeddedTrackRequest_RejectsTargetCoveredByExternalFile()
    {
        await CreateMovieWithTracks();
        _externalSubtitles.Add(new Subtitles
        {
            Path = "/movies/dune-2024/Dune.Part.Two.2024.ko.srt",
            FileName = "Dune.Part.Two.2024.ko",
            Language = "ko",
            Languages = ["ko"],
            Caption = string.Empty,
            Format = ".srt"
        });

        await Assert.ThrowsAsync<TargetLanguageAlreadyPresentException>(
            () => _queueService.CreateEmbeddedTrackRequest(MediaType.Movie, 7, 2, "KO"));

        Assert.Empty(_dbContext.TranslationRequests);
    }

    [Fact]
    public async Task CreateEmbeddedTrackRequest_RejectsGraphicTrack()
    {
        await CreateMovieWithTracks();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _queueService.CreateEmbeddedTrackRequest(MediaType.Movie, 7, 5, "ja"));
        Assert.Contains("image-based", error.Message);
    }

    [Fact]
    public async Task CreateEmbeddedTrackRequest_RejectsUnrecognizedTrackLanguage()
    {
        await CreateMovieWithTracks();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _queueService.CreateEmbeddedTrackRequest(MediaType.Movie, 7, 4, "ja"));
        Assert.Contains("could not be recognized", error.Message);
    }

    [Fact]
    public async Task CreateEmbeddedTrackRequest_RejectsMissingTrackRow()
    {
        await CreateMovieWithTracks();

        await Assert.ThrowsAsync<DeepL.NotFoundException>(
            () => _queueService.CreateEmbeddedTrackRequest(MediaType.Movie, 7, 99, "ja"));
    }

    [Fact]
    public async Task CreateEmbeddedTrackRequest_RejectsUnsupportedMediaType()
    {
        await CreateMovieWithTracks();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _queueService.CreateEmbeddedTrackRequest(MediaType.Show, 7, 2, "ja"));
        Assert.Contains("movies and episodes", error.Message);
    }
}
