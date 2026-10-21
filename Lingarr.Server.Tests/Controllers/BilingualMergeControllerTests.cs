using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Lingarr.Core.Data;
using Lingarr.Core.Entities;
using Lingarr.Server.Controllers;
using Lingarr.Server.Modules.Subtitle;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;

namespace Lingarr.Server.Tests.Controllers;

/// <summary>
/// <see cref="BilingualMergeController"/> contracts (B06): the merge
/// endpoint validates ownership of BOTH subtitle paths against the named
/// media (TimelineSync precedent — directory containment plus video-base
/// derivation) before the service runs, and maps the service's
/// request-shape refusals to 400.
/// </summary>
public sealed class BilingualMergeControllerTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "lingarr-bilingual-merge-ctl-" + Guid.NewGuid().ToString("N"));

    private readonly Mock<IBilingualMergeService> _merge = new();

    public BilingualMergeControllerTests()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Path.Combine(_directory, "Movie.2023.mkv"), "video");
        File.WriteAllText(Path.Combine(_directory, "Movie.2023.zh.srt"),
            "1\n00:00:10,000 --> 00:00:12,000\n你好\n");
        File.WriteAllText(Path.Combine(_directory, "Movie.2023.en.srt"),
            "1\n00:00:10,000 --> 00:00:12,000\nhello\n");
    }

    private BilingualMergeController CreateController()
    {
        var options = new DbContextOptionsBuilder<LingarrDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var dbContext = new LingarrDbContext(options);
        dbContext.Movies.Add(new Movie
        {
            RadarrId = 1,
            Title = "Movie",
            FileName = "Movie.2023.mkv",
            Path = _directory,
            DateAdded = DateTimeOffset.UtcNow
        });
        dbContext.SaveChanges();
        return new BilingualMergeController(dbContext, _merge.Object);
    }

    private static BilingualMergeRequestBody Body(
        string mediaType = "Movie",
        int mediaId = 1,
        string? primaryPath = null,
        string primaryLanguage = "zh",
        string? secondaryPath = null,
        string secondaryLanguage = "en") =>
        new(
            mediaType,
            mediaId,
            primaryPath ?? Path.Combine(Path.GetTempPath(), "placeholder"),
            primaryLanguage,
            secondaryPath ?? Path.Combine(Path.GetTempPath(), "placeholder"),
            secondaryLanguage);

    [Fact]
    public async Task Merge_ValidRequest_PassesOwnershipAndCallsService()
    {
        var controller = CreateController();
        var primary = Path.Combine(_directory, "Movie.2023.zh.srt");
        var secondary = Path.Combine(_directory, "Movie.2023.en.srt");
        _merge.Setup(service => service.MergeAsync(
                primary, "zh", secondary, "en", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BilingualMergeResult(
                Path.Combine(_directory, "Movie.2023.zh.en.srt"),
                PrimaryCues: 100, SecondaryCues: 100,
                PairedCues: 96, PrimaryOnlyCues: 2, SecondaryOnlyCues: 2));

        var response = await controller.Merge(
            Body(primaryPath: primary, secondaryPath: secondary), CancellationToken.None);

        var result = Assert.IsType<OkObjectResult>(response.Result);
        var body = Assert.IsType<BilingualMergeResponse>(result.Value);
        Assert.Equal("Movie.2023.zh.en.srt", body.OutputFileName);
        Assert.Equal(96, body.PairedCues);
        _merge.Verify(service => service.MergeAsync(
            primary, "zh", secondary, "en", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Merge_EpisodeMediaType_ResolvesTheEpisodeDirectory()
    {
        var options = new DbContextOptionsBuilder<LingarrDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var dbContext = new LingarrDbContext(options);
        var show = new Show
        {
            SonarrId = 1,
            Title = "Show",
            Path = _directory,
            DateAdded = DateTimeOffset.UtcNow
        };
        dbContext.Episodes.Add(new Episode
        {
            SonarrId = 1,
            EpisodeNumber = 1,
            Title = "Episode",
            FileName = "Movie.2023.mkv", // reuse the on-disk fixtures
            Path = _directory,
            Season = new Season { SeasonNumber = 1, Show = show },
            DateAdded = DateTimeOffset.UtcNow
        });
        dbContext.SaveChanges();
        var controller = new BilingualMergeController(dbContext, _merge.Object);
        var primary = Path.Combine(_directory, "Movie.2023.zh.srt");
        var secondary = Path.Combine(_directory, "Movie.2023.en.srt");
        _merge.Setup(service => service.MergeAsync(
                primary, "zh", secondary, "en", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BilingualMergeResult(primary, 1, 1, 1, 0, 0));

        var response = await controller.Merge(
            Body(mediaType: "Episode", primaryPath: primary, secondaryPath: secondary),
            CancellationToken.None);

        Assert.IsType<OkObjectResult>(response.Result);
        _merge.Verify(service => service.MergeAsync(
            primary, "zh", secondary, "en", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Merge_PrimaryOutsideTheMediaDirectory_Returns400()
    {
        var controller = CreateController();
        var foreign = Path.Combine(Path.GetTempPath(), "elsewhere-" + Guid.NewGuid().ToString("N"), "Movie.2023.zh.srt");
        Directory.CreateDirectory(Path.GetDirectoryName(foreign)!);
        File.WriteAllText(foreign, "x");

        var response = await controller.Merge(
            Body(
                primaryPath: foreign,
                secondaryPath: Path.Combine(_directory, "Movie.2023.en.srt")),
            CancellationToken.None);

        var result = Assert.IsType<BadRequestObjectResult>(response.Result);
        Assert.Contains("does not belong", result.Value!.ToString());
        _merge.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Merge_SecondaryNotDerivedFromTheVideoName_Returns400()
    {
        var controller = CreateController();
        var foreign = Path.Combine(_directory, "Other.Show.en.srt");
        File.WriteAllText(foreign, "x");

        var response = await controller.Merge(
            Body(
                primaryPath: Path.Combine(_directory, "Movie.2023.zh.srt"),
                secondaryPath: foreign),
            CancellationToken.None);

        var result = Assert.IsType<BadRequestObjectResult>(response.Result);
        Assert.Contains("video file name", result.Value!.ToString());
        _merge.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Merge_PathTraversalOutsideTheRoot_Returns400()
    {
        var controller = CreateController();
        var escape = Path.Combine(_directory, "..", "escape.zh.srt");

        var response = await controller.Merge(
            Body(
                primaryPath: escape,
                secondaryPath: Path.Combine(_directory, "Movie.2023.en.srt")),
            CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(response.Result);
        _merge.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Merge_UnknownMedia_Returns400()
    {
        var controller = CreateController();

        var response = await controller.Merge(
            Body(
                mediaId: 99,
                primaryPath: Path.Combine(_directory, "Movie.2023.zh.srt"),
                secondaryPath: Path.Combine(_directory, "Movie.2023.en.srt")),
            CancellationToken.None);

        var result = Assert.IsType<BadRequestObjectResult>(response.Result);
        Assert.Contains("does not exist", result.Value!.ToString());
    }

    [Fact]
    public async Task Merge_InvalidMediaType_Returns400()
    {
        var controller = CreateController();

        var response = await controller.Merge(
            Body(
                mediaType: "Show",
                primaryPath: Path.Combine(_directory, "Movie.2023.zh.srt"),
                secondaryPath: Path.Combine(_directory, "Movie.2023.en.srt")),
            CancellationToken.None);

        var result = Assert.IsType<BadRequestObjectResult>(response.Result);
        Assert.Contains("mediaType", result.Value!.ToString());
    }

    [Fact]
    public async Task Merge_SubtitleMissingOnDisk_Returns400()
    {
        var controller = CreateController();
        File.Delete(Path.Combine(_directory, "Movie.2023.en.srt"));

        var response = await controller.Merge(
            Body(
                primaryPath: Path.Combine(_directory, "Movie.2023.zh.srt"),
                secondaryPath: Path.Combine(_directory, "Movie.2023.en.srt")),
            CancellationToken.None);

        var result = Assert.IsType<BadRequestObjectResult>(response.Result);
        Assert.Contains("was not found on the server", result.Value!.ToString());
    }

    [Fact]
    public async Task Merge_EmptyPath_Returns400()
    {
        var controller = CreateController();

        var response = await controller.Merge(
            Body(
                primaryPath: "",
                secondaryPath: Path.Combine(_directory, "Movie.2023.en.srt")),
            CancellationToken.None);

        var result = Assert.IsType<BadRequestObjectResult>(response.Result);
        Assert.Contains("required", result.Value!.ToString());
        _merge.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Merge_ServiceRefusal_Returns400()
    {
        var controller = CreateController();
        _merge.Setup(service => service.MergeAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException(
                "both files carry the same language 'zh' — a bilingual merge needs two different languages."));

        var response = await controller.Merge(
            Body(
                primaryPath: Path.Combine(_directory, "Movie.2023.zh.srt"),
                secondaryPath: Path.Combine(_directory, "Movie.2023.en.srt"),
                secondaryLanguage: "zh"),
            CancellationToken.None);

        var result = Assert.IsType<BadRequestObjectResult>(response.Result);
        Assert.Contains("same language", result.Value!.ToString());
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
