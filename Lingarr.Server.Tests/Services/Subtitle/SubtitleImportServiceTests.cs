using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Lingarr.Core.Data;
using Lingarr.Core.Entities;
using Lingarr.Core.Enum;
using Lingarr.Server.Modules.Subtitle;
using Moq;
using Xunit;
using Lingarr.Server.Modules.Translation;

namespace Lingarr.Server.Tests.Services.Subtitle;

public class SubtitleImportServiceTests : IDisposable
{
    private readonly string _root;
    private readonly LingarrDbContext _dbContext;
    private readonly Mock<ITranslationRequestQueueService> _queueService;
    private readonly SubtitleImportService _service;

    public SubtitleImportServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "lingarr-import-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);

        var environment = new Mock<IHostEnvironment>();
        environment.SetupGet(e => e.ContentRootPath).Returns(_root);

        _queueService = new Mock<ITranslationRequestQueueService>();
        _dbContext = new LingarrDbContext(new DbContextOptionsBuilder<LingarrDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

        _service = new SubtitleImportService(
            environment.Object,
            new SubtitleEncodingService(),
            _queueService.Object,
            _dbContext,
            Mock.Of<ILogger<SubtitleImportService>>());
    }

    public void Dispose()
    {
        Directory.Delete(_root, recursive: true);
        _dbContext.Database.EnsureDeleted();
    }

    private static IFormFile FormFile(string fileName, byte[] content)
    {
        var stream = new MemoryStream(content);
        return new FormFile(stream, 0, content.Length, "files", fileName);
    }

    [Fact]
    public async Task ImportFiles_UnsupportedExtension_ReportsErrorAndSkipsQueue()
    {
        var results = await _service.ImportFiles(
            [FormFile("subtitles.sub", [1, 2, 3])], "en", "zh", translate: true);

        var result = Assert.Single(results);
        Assert.False(result.Success);
        Assert.Contains("Unsupported", result.Error);
        Assert.Null(result.TranslationRequestId);
        _queueService.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ImportFiles_GbkEncodedFile_WritesUtf8Content()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        const string text = "1\n00:00:01,000 --> 00:00:02,000\n简体中文字幕\n";
        var gbkBytes = Encoding.GetEncoding("gb2312").GetBytes(text);

        var results = await _service.ImportFiles(
            [FormFile("movie.chs.srt", gbkBytes)], "en", "zh", translate: false);

        var result = Assert.Single(results);
        Assert.True(result.Success, result.Error);
        Assert.NotNull(result.SavedPath);
        Assert.Equal(text, await File.ReadAllTextAsync(result.SavedPath));
        _queueService.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ImportFiles_TranslateTrue_EnqueuesUnboundRequest()
    {
        var results = await _service.ImportFiles(
            [FormFile("Show.S01E05.chs.srt", "1\n00:00:01,000 --> 00:00:02,000\nhi\n"u8.ToArray())],
            "en", "zh", translate: true);

        var result = Assert.Single(results);
        Assert.True(result.Success, result.Error);
        Assert.False(result.MediaBound);
        Assert.NotNull(result.TranslationRequestId);
        _queueService.Verify(q => q.CreateImportRequest(
            It.Is<string>(p => p != null && File.Exists(p)),
            "Show.S01E05.chs",
            "en",
            "zh",
            null,
            MediaType.Movie,
            It.IsAny<string?>()), Times.Once);
    }

    [Fact]
    public async Task ImportFiles_ChineseConvertTraditionalToSimplified_WritesSimplifiedContent()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        const string traditional = "1\n00:00:01,000 --> 00:00:02,000\n這是繁體中文字幕\n";
        var big5Bytes = Encoding.GetEncoding("big5").GetBytes(traditional);

        var results = await _service.ImportFiles(
            [FormFile("movie.cht.srt", big5Bytes)], "zh", "zh", translate: false,
            chineseConvert: "simplified");

        var result = Assert.Single(results);
        Assert.True(result.Success, result.Error);
        Assert.Contains("这是繁体中文字幕", await File.ReadAllTextAsync(result.SavedPath!));
    }

    [Fact]
    public async Task ImportFiles_DuplicateFileName_GetsUniqueSuffixNotOverwrite()
    {
        await _service.ImportFiles([FormFile("same.srt", "one"u8.ToArray())], "en", "zh", translate: false);

        var results = await _service.ImportFiles([FormFile("same.srt", "two"u8.ToArray())], "en", "zh", translate: false);

        var second = Assert.Single(results);
        Assert.NotNull(second.SavedPath);
        Assert.Equal("two", await File.ReadAllTextAsync(second.SavedPath));
        Assert.Contains("(", Path.GetFileName(second.SavedPath));
    }

    [Fact]
    public async Task ImportFiles_PathLikeFileName_StoresBasenameOnly()
    {
        var results = await _service.ImportFiles(
            [FormFile("../../evil.srt", "ok"u8.ToArray())], "en", "zh", translate: false);

        var result = Assert.Single(results);
        Assert.True(result.Success, result.Error);
        Assert.Equal("evil.srt", Path.GetFileName(result.SavedPath));
        Assert.Equal(Path.Combine(_root, "config", "imports"), Path.GetDirectoryName(result.SavedPath));
    }

    // ── directed imports ──────────────────────────────────────────────

    private async Task SeedMediaAsync()
    {
        _dbContext.Movies.Add(new Movie
        {
            RadarrId = 1, Title = "Dune: Part Two", Path = Path.Combine(_root, "media", "dune.mkv"),
            FileName = "dune.mkv", DateAdded = DateTimeOffset.Now
        });
        var show = new Show { SonarrId = 1, Title = "Shōgun", Path = Path.Combine(_root, "media", "tv", "shogun"), DateAdded = DateTimeOffset.Now };
        var season = new Season { SeasonNumber = 1, Show = show };
        show.Seasons.Add(season);
        season.Episodes.Add(new Episode
        {
            SonarrId = 5, EpisodeNumber = 5, Title = "Broken to the Fist",
            Path = Path.Combine(_root, "media", "tv", "shogun", "e5.mkv"), FileName = "e5.mkv", Season = season
        });
        season.Episodes.Add(new Episode
        {
            SonarrId = 6, EpisodeNumber = 6, Title = "Broken to the Fist II",
            Path = string.Empty, FileName = string.Empty, Season = season
        });
        _dbContext.Shows.Add(show);
        await _dbContext.SaveChangesAsync();
    }

    [Fact]
    public async Task ImportFiles_DirectedMovie_StoresNextToVideoAndBindsRequest()
    {
        await SeedMediaAsync();
        var movie = await _dbContext.Movies.SingleAsync();

        var results = await _service.ImportFiles(
            [FormFile("dune.chs.srt", "sub"u8.ToArray())],
            "en", "zh", translate: true, mediaType: "Movie", mediaId: movie.Id);

        var result = Assert.Single(results);
        Assert.True(result.Success, result.Error);
        Assert.True(result.MediaBound);
        Assert.Equal(Path.GetDirectoryName(movie.Path), Path.GetDirectoryName(result.SavedPath));
        _queueService.Verify(q => q.CreateImportRequest(
            result.SavedPath!, "Dune: Part Two", "en", "zh", movie.Id, MediaType.Movie,
            It.IsAny<string?>()), Times.Once);
    }

    [Fact]
    public async Task ImportFiles_DirectedShowSeason_MatchesEpisodeByParsedNumber()
    {
        await SeedMediaAsync();
        var show = await _dbContext.Shows.SingleAsync();
        var episode = await _dbContext.Episodes.SingleAsync(e => e.EpisodeNumber == 5);

        var results = await _service.ImportFiles(
            [FormFile("Shogun.S01E05.chs.srt", "sub"u8.ToArray())],
            "en", "zh", translate: true, mediaType: "Show", mediaId: show.Id, season: 1);

        var result = Assert.Single(results);
        Assert.True(result.Success, result.Error);
        Assert.True(result.MediaBound);
        Assert.Equal(Path.GetDirectoryName(episode.Path), Path.GetDirectoryName(result.SavedPath));
        _queueService.Verify(q => q.CreateImportRequest(
            result.SavedPath!,
            "Shōgun - S01E05 - Broken to the Fist",
            "en", "zh", episode.Id, MediaType.Episode,
            It.IsAny<string?>()), Times.Once);
    }

    [Fact]
    public async Task ImportFiles_DirectedShowSeason_UnmatchedFileFallsBackToImports()
    {
        await SeedMediaAsync();
        var show = await _dbContext.Shows.SingleAsync();

        var results = await _service.ImportFiles(
            [FormFile("Shogun.S01E99.chs.srt", "sub"u8.ToArray())],
            "en", "zh", translate: true, mediaType: "Show", mediaId: show.Id, season: 1);

        var result = Assert.Single(results);
        Assert.True(result.Success, result.Error);
        Assert.False(result.MediaBound);
        Assert.StartsWith(Path.Combine(_root, "config", "imports"), result.SavedPath);
        _queueService.Verify(q => q.CreateImportRequest(
            result.SavedPath!, "Shogun.S01E99.chs", "en", "zh",
            null, MediaType.Movie, It.IsAny<string?>()), Times.Once);
    }

    [Fact]
    public async Task ImportFiles_DirectedShowSeason_EpisodeWithoutVideoPathFallsBack()
    {
        await SeedMediaAsync();
        var show = await _dbContext.Shows.SingleAsync();

        var results = await _service.ImportFiles(
            [FormFile("Shogun.S01E06.chs.srt", "sub"u8.ToArray())],
            "en", "zh", translate: true, mediaType: "Show", mediaId: show.Id, season: 1);

        var result = Assert.Single(results);
        Assert.True(result.Success, result.Error);
        Assert.False(result.MediaBound);
        Assert.StartsWith(Path.Combine(_root, "config", "imports"), result.SavedPath);
    }

    [Fact]
    public async Task ImportFiles_SameContentSameTarget_SecondImportSkipsEnqueueAsDuplicate()
    {
        const string content = "1\n00:00:01,000 --> 00:00:02,000\nhello\n";
        var hash = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();
        _dbContext.TranslationRequests.Add(new TranslationRequest
        {
            Title = "existing", SourceLanguage = "en", TargetLanguage = "zh",
            SubtitleToTranslate = "/imports/first.srt", MediaType = MediaType.Movie,
            Status = TranslationStatus.Completed, ContentHash = hash, Source = TranslationRequestSource.Import
        });
        await _dbContext.SaveChangesAsync();

        var results = await _service.ImportFiles(
            [FormFile("dup.srt", Encoding.UTF8.GetBytes(content))], "en", "zh", translate: true);

        var result = Assert.Single(results);
        Assert.True(result.Success);
        Assert.NotNull(result.DuplicateOfRequestId);
        Assert.NotNull(result.Warning);
        Assert.Null(result.TranslationRequestId);
        _queueService.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ImportFiles_SameContentDifferentTarget_IsNotADuplicate()
    {
        const string content = "1\n00:00:01,000 --> 00:00:02,000\nhello\n";
        var hash = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();
        _dbContext.TranslationRequests.Add(new TranslationRequest
        {
            Title = "existing", SourceLanguage = "en", TargetLanguage = "nl",
            SubtitleToTranslate = "/imports/first.srt", MediaType = MediaType.Movie,
            Status = TranslationStatus.Completed, ContentHash = hash, Source = TranslationRequestSource.Import
        });
        await _dbContext.SaveChangesAsync();

        var results = await _service.ImportFiles(
            [FormFile("dup.srt", Encoding.UTF8.GetBytes(content))], "en", "zh", translate: true);

        var result = Assert.Single(results);
        Assert.Null(result.DuplicateOfRequestId);
        _queueService.Verify(q => q.CreateImportRequest(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<int?>(), It.IsAny<MediaType>(), It.IsAny<string?>()), Times.Once);
    }
}
