using System.Collections.Generic;
using Lingarr.Core.Data;
using Lingarr.Server.Controllers;
using Lingarr.Server.Models.Api;
using Lingarr.Server.Modules.Sync;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;
using Lingarr.Server.Modules.Subtitle;
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace Lingarr.Server.Tests.Controllers;

public class SubtitleControllerNormalizeTests
{
    private static SubtitleController CreateController(Mock<ISubtitleNormalizeService>? normalize = null) => new(
        new Mock<ISubtitleService>().Object,
        new Mock<ISubtitleImportService>().Object,
        (normalize ?? new Mock<ISubtitleNormalizeService>()).Object,
        new PathConversionService(
            new LingarrDbContext(
                new DbContextOptionsBuilder<LingarrDbContext>()
                    .UseInMemoryDatabase(Guid.NewGuid().ToString())
                    .Options)));

    [Fact]
    public async Task NormalizePreview_InvalidMediaType_ReturnsBadRequest()
    {
        var controller = CreateController();

        var response = await controller.NormalizePreview(
            new SubtitleNormalizeRequest { MediaType = "Season", MediaId = 1 });

        var result = Assert.IsType<BadRequestObjectResult>(response.Result);
        Assert.Contains("mediaType", result.Value!.ToString());
    }

    [Fact]
    public async Task NormalizePreview_UnknownMedia_ReturnsNotFound()
    {
        var normalize = new Mock<ISubtitleNormalizeService>();
        normalize
            .Setup(service => service.Preview("Movie", 42))
            .ThrowsAsync(new KeyNotFoundException("Movie 42 was not found."));

        var controller = CreateController(normalize);

        var response = await controller.NormalizePreview(
            new SubtitleNormalizeRequest { MediaType = "Movie", MediaId = 42 });

        var result = Assert.IsType<NotFoundObjectResult>(response.Result);
        Assert.Contains("Movie 42", result.Value!.ToString());
    }

    [Fact]
    public async Task NormalizePreview_ScanOverCap_ReturnsBadRequest()
    {
        var normalize = new Mock<ISubtitleNormalizeService>();
        normalize
            .Setup(service => service.Preview("Show", 7))
            .ThrowsAsync(new InvalidOperationException("more than 500"));

        var controller = CreateController(normalize);

        var response = await controller.NormalizePreview(
            new SubtitleNormalizeRequest { MediaType = "Show", MediaId = 7 });

        Assert.IsType<BadRequestObjectResult>(response.Result);
    }

    [Fact]
    public async Task NormalizePreview_ValidRequest_ReturnsPreviewBody()
    {
        var preview = new SubtitleNormalizePreviewResponse
        {
            Writable = true,
            Items = [new SubtitleNormalizePreviewItem { OldName = "a.chs.srt", NewName = "a.zh.srt" }],
            Skipped = [new SubtitleNormalizeSkippedItem { Path = "/x/b.scjp.srt", Reason = "dualLanguage" }]
        };
        var normalize = new Mock<ISubtitleNormalizeService>();
        normalize.Setup(service => service.Preview("Movie", 1)).ReturnsAsync(preview);

        var controller = CreateController(normalize);

        var response = await controller.NormalizePreview(
            new SubtitleNormalizeRequest { MediaType = "Movie", MediaId = 1 });

        var result = Assert.IsType<OkObjectResult>(response.Result);
        Assert.Same(preview, result.Value);
    }

    [Fact]
    public async Task NormalizeExecute_EmptyItems_ReturnsBadRequest()
    {
        var controller = CreateController();

        var response = await controller.NormalizeExecute(
            new SubtitleNormalizeExecuteRequest { MediaType = "Movie", MediaId = 1, Items = [] });

        Assert.IsType<BadRequestObjectResult>(response.Result);
    }

    [Fact]
    public async Task NormalizeExecute_ValidRequest_PassesItemsToService()
    {
        var items = new List<SubtitleNormalizeExecuteItem>
        {
            new() { OldPath = "/m/a.chs.srt", NewPath = "/m/a.zh.srt", Language = "zh" }
        };
        var executed = new SubtitleNormalizeExecuteResponse { Renamed = 1 };
        var normalize = new Mock<ISubtitleNormalizeService>();
        normalize
            .Setup(service => service.Execute("Movie", 1, items))
            .ReturnsAsync(executed);

        var controller = CreateController(normalize);

        var response = await controller.NormalizeExecute(
            new SubtitleNormalizeExecuteRequest { MediaType = "Movie", MediaId = 1, Items = items });

        var result = Assert.IsType<OkObjectResult>(response.Result);
        Assert.Same(executed, result.Value);
        normalize.Verify(service => service.Execute("Movie", 1, items), Times.Once);
    }
}
