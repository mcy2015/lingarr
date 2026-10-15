using Lingarr.Core.Data;
using Lingarr.Core.Entities;
using Lingarr.Core.Enum;
using Lingarr.Server.Modules.Media;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace Lingarr.Server.Tests.Services;

/// <summary>
/// F2b extraction behaviour of <see cref="EmbeddedTrackService"/>: the ffmpeg
/// process is injected as a stub (no real binary runs), the content root is a
/// temp directory, so these tests exercise the reuse check, the ffmpeg
/// argument shape, partial-file cleanup, materialization next to the video
/// and the readable failure messages.
/// </summary>
public sealed class EmbeddedTrackServiceExtractionTests : IDisposable
{
    private readonly string _contentRoot = Path.Combine(Path.GetTempPath(), $"lingarr_f2b_{Guid.NewGuid():N}");
    private readonly LingarrDbContext _dbContext;
    private readonly Mock<IHostEnvironment> _environment = new();

    // ffmpeg stub: records argv and (optionally) writes the output file.
    private readonly List<IReadOnlyList<string>> _invocations = [];
    private Func<string, Task<string>> _run = _ => Task.FromResult(string.Empty);

    private EmbeddedTrackService CreateService() => new(
        _dbContext,
        NullLogger<EmbeddedTrackService>.Instance,
        _environment.Object,
        async (binary, arguments, cancellationToken) =>
        {
            _invocations.Add(arguments);
            return await _run(arguments[^1]);
        },
        ffmpegPathResolver: () => "/usr/bin/ffmpeg");

    public EmbeddedTrackServiceExtractionTests()
    {
        Directory.CreateDirectory(_contentRoot);
        var options = new DbContextOptionsBuilder<LingarrDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _dbContext = new LingarrDbContext(options);
        _environment.SetupGet(environment => environment.ContentRootPath).Returns(_contentRoot);
    }

    private void Dispose(bool disposing)
    {
        _dbContext.Dispose();
        try { Directory.Delete(_contentRoot, recursive: true); } catch { /* temp cleanup */ }
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    private async Task<string> CreateContainerWithTrack(
        string language = "en",
        bool isTextBased = true,
        string codec = "srt",
        int streamIndex = 2)
    {
        var container = Path.Combine(_contentRoot, "movie.mkv");
        await File.WriteAllTextAsync(container, "container-bytes");

        _dbContext.Movies.Add(new Movie
        {
            Id = 7,
            RadarrId = 1,
            Title = "Test Movie",
            Path = _contentRoot,
            FileName = "Test.Movie.2023",
            DateAdded = DateTimeOffset.UtcNow
        });
        _dbContext.MediaSubtitleTracks.Add(new MediaSubtitleTrack
        {
            MediaType = MediaType.Movie,
            MediaId = 7,
            StreamIndex = streamIndex,
            Language = language,
            RawLanguage = language,
            Title = null,
            Codec = codec,
            IsTextBased = isTextBased,
            FilePath = container
        });
        await _dbContext.SaveChangesAsync();
        return container;
    }

    // ── ExtractTrack ───────────────────────────────────────────────

    [Fact]
    public async Task ExtractTrack_RunsFfmpegWithMapAndSrtCodec_AndReturnsCachePath()
    {
        var container = await CreateContainerWithTrack();
        _run = async target =>
        {
            await File.WriteAllTextAsync(target, "1\n00:00:01,000 --> 00:00:02,000\nhi\n");
            return string.Empty;
        };

        var path = await CreateService().ExtractTrack(MediaType.Movie, 7, 2);

        Assert.Single(_invocations);
        var arguments = _invocations[0];
        // maps the requested stream and forces SRT output
        Assert.Equal("-y", arguments[0]);
        Assert.Contains("0:2", arguments);
        Assert.Contains("srt", arguments);
        Assert.EndsWith(".srt", path);
        Assert.Contains("embedded.movie.7.s2.srt", Path.GetFileName(path));
        Assert.True(File.Exists(path));

        // the container path is passed as the input right after -i
        var inputIndex = Array.IndexOf(arguments.ToArray(), "-i");
        Assert.Equal(container, arguments[inputIndex + 1]);
    }

    [Fact]
    public async Task ExtractTrack_ReusesExistingNonEmptyExtraction_WithoutRunningFfmpeg()
    {
        await CreateContainerWithTrack();
        _run = async target =>
        {
            await File.WriteAllTextAsync(target, "1\n00:00:01,000 --> 00:00:02,000\nhi\n");
            return string.Empty;
        };
        var service = CreateService();

        var first = await service.ExtractTrack(MediaType.Movie, 7, 2);
        var second = await service.ExtractTrack(MediaType.Movie, 7, 2);

        Assert.Equal(first, second);
        Assert.Single(_invocations); // second call hit the cache
    }

    [Fact]
    public async Task ExtractTrack_EmptyLeftoverDoesNotCountAsCached()
    {
        await CreateContainerWithTrack();
        var service = CreateService();
        var expected = Path.Combine(
            _contentRoot, "config", "imports", "embedded", "embedded.movie.7.s2.srt");
        Directory.CreateDirectory(Path.GetDirectoryName(expected)!);
        await File.WriteAllTextAsync(expected, string.Empty); // zero-byte leftover

        _run = async target =>
        {
            await File.WriteAllTextAsync(target, "content");
            return string.Empty;
        };

        var path = await service.ExtractTrack(MediaType.Movie, 7, 2);

        Assert.Equal(expected, path);
        Assert.Single(_invocations); // leftover forced a re-run
    }

    [Fact]
    public async Task ExtractTrack_FfmpegFailure_DeletesPartialFileAndThrowsReadableError()
    {
        await CreateContainerWithTrack();
        var service = CreateService();
        _run = async target =>
        {
            await File.WriteAllTextAsync(target, "partial"); // ffmpeg wrote, then died
            throw new InvalidOperationException("ffmpeg exited with code 1. cannot open stream");
        };

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.ExtractTrack(MediaType.Movie, 7, 2));

        Assert.Contains("code 1", error.Message);
        // no partial file survives for the reuse check to trust
        Assert.False(File.Exists(Path.Combine(
            _contentRoot, "config", "imports", "embedded", "embedded.movie.7.s2.srt")));
    }

    [Fact]
    public async Task ExtractTrack_MissingTrackRow_ThrowsWithScanHint()
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => CreateService().ExtractTrack(MediaType.Movie, 404, 2));
        Assert.Contains("embedded track scan", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExtractTrack_GraphicTrack_IsRejected()
    {
        await CreateContainerWithTrack(language: "zh", isTextBased: false, codec: "pgs", streamIndex: 3);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => CreateService().ExtractTrack(MediaType.Movie, 7, 3));
        Assert.Contains("image-based", error.Message);
    }

    [Fact]
    public async Task ExtractTrack_MissingContainerFile_ThrowsReadableError()
    {
        var container = await CreateContainerWithTrack();
        File.Delete(container);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => CreateService().ExtractTrack(MediaType.Movie, 7, 2));
        Assert.Contains("no longer exists", error.Message);
    }

    [Fact]
    public async Task ExtractTrack_BadFfmpegPathConfiguration_ThrowsUnavailable()
    {
        await CreateContainerWithTrack();
        var service = new EmbeddedTrackService(
            _dbContext,
            NullLogger<EmbeddedTrackService>.Instance,
            _environment.Object,
            ffmpegRunner: (binary, arguments, cancellationToken) => throw new InvalidOperationException("runner must not be reached"),
            ffmpegPathResolver: () => throw new Lingarr.Server.Exceptions.FFprobeUnavailableException(
                "ffmpeg was not found on PATH."));
        await Assert.ThrowsAsync<Lingarr.Server.Exceptions.FFprobeUnavailableException>(
            () => service.ExtractTrack(MediaType.Movie, 7, 2));
        Assert.Empty(_invocations);
    }

    // ── PrepareTranslationSource ───────────────────────────────────

    [Fact]
    public async Task PrepareTranslationSource_MaterializesSrtNextToTheVideo_AndReusesIt()
    {
        await CreateContainerWithTrack(language: "ja");
        var service = CreateService();
        _run = async target =>
        {
            await File.WriteAllTextAsync(target, "1\n00:00:01,000 --> 00:00:02,000\nこんにちは\n");
            return string.Empty;
        };

        var sourcePath = await service.PrepareTranslationSource(MediaType.Movie, 7, 2);

        Assert.Equal(Path.Combine(_contentRoot, "Test.Movie.2023.ja.srt"), sourcePath);
        Assert.True(File.Exists(sourcePath));
        Assert.Single(_invocations);

        // second preparation: extraction cached, materialized file present
        var again = await service.PrepareTranslationSource(MediaType.Movie, 7, 2);
        Assert.Equal(sourcePath, again);
        Assert.Single(_invocations);
    }
}
