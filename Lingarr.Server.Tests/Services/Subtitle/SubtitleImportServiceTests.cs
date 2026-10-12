using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Lingarr.Server.Interfaces.Services;
using Lingarr.Server.Interfaces.Services.Subtitle;
using Lingarr.Server.Services;
using Lingarr.Server.Services.Subtitle;
using Moq;
using Xunit;

namespace Lingarr.Server.Tests.Services.Subtitle;

public class SubtitleImportServiceTests : IDisposable
{
    private readonly string _root;
    private readonly Mock<ITranslationRequestQueueService> _queueService;
    private readonly SubtitleImportService _service;

    public SubtitleImportServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "lingarr-import-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);

        var environment = new Mock<IHostEnvironment>();
        environment.SetupGet(e => e.ContentRootPath).Returns(_root);

        _queueService = new Mock<ITranslationRequestQueueService>();

        _service = new SubtitleImportService(
            environment.Object,
            new SubtitleEncodingService(),
            _queueService.Object,
            NullLogger());
    }

    public void Dispose()
    {
        Directory.Delete(_root, recursive: true);
    }

    private static ILogger<SubtitleImportService> NullLogger()
        => Mock.Of<ILogger<SubtitleImportService>>();

    private static IFormFile FormFile(string fileName, byte[] content)
    {
        var stream = new MemoryStream(content);
        return new FormFile(stream, 0, content.Length, "files", fileName);
    }

    [Fact]
    public async Task ImportFiles_UnsupportedExtension_ReportsErrorAndSkipsQueue()
    {
        var results = await _service.ImportFiles(
            [FormFile("subtitles.vtt", [1, 2, 3])], "en", "zh", translate: true);

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
    public async Task ImportFiles_TranslateTrue_EnqueuesRequestWithStoredPath()
    {
        var results = await _service.ImportFiles(
            [FormFile("Show.S01E05.chs.srt", "1\n00:00:01,000 --> 00:00:02,000\nhi\n"u8.ToArray())],
            "en", "zh", translate: true);

        var result = Assert.Single(results);
        Assert.True(result.Success, result.Error);
        Assert.NotNull(result.TranslationRequestId);
        _queueService.Verify(q => q.CreateImportRequest(
            It.Is<string>(p => p != null && File.Exists(p)),
            "Show.S01E05.chs", // title is the stored file name without extension
            "en",
            "zh"), Times.Once);
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
}
