using System;
using System.Threading;
using System.Threading.Tasks;
using Lingarr.Server.Controllers;
using Lingarr.Server.Models.Api;
using Lingarr.Server.Modules.Subtitle.PostProcessing;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Xunit;

namespace Lingarr.Server.Tests.Controllers;

/// <summary>
/// SubtitlePostProcessController mapping contracts (B01): preview/execute
/// bodies pass through; a refused batch (mode "none"/unset, over-cap scan)
/// maps InvalidOperationException to 400.
/// </summary>
public class SubtitlePostProcessControllerTests
{
    private static (SubtitlePostProcessController Controller, Mock<ISubtitleVariantBatchService> Batch)
        Create()
    {
        var batch = new Mock<ISubtitleVariantBatchService>();
        return (new SubtitlePostProcessController(batch.Object), batch);
    }

    [Fact]
    public async Task Preview_RefusedBatch_ReturnsBadRequest()
    {
        var (controller, batch) = Create();
        batch
            .Setup(service => service.PreviewAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("mode is 'none'"));

        var response = await controller.Preview(CancellationToken.None);

        var result = Assert.IsType<BadRequestObjectResult>(response.Result);
        Assert.Contains("none", result.Value!.ToString());
    }

    [Fact]
    public async Task Preview_ValidBatch_ReturnsCounts()
    {
        var preview = new SubtitlePostProcessPreviewResponse { Mode = "simplified", Scanned = 7, WouldChange = 3 };
        var (controller, batch) = Create();
        batch
            .Setup(service => service.PreviewAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(preview);

        var response = await controller.Preview(CancellationToken.None);

        var result = Assert.IsType<OkObjectResult>(response.Result);
        Assert.Same(preview, result.Value);
    }

    [Fact]
    public async Task Execute_RefusedBatch_ReturnsBadRequest()
    {
        var (controller, batch) = Create();
        batch
            .Setup(service => service.ExecuteAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("more than 500"));

        var response = await controller.Execute(CancellationToken.None);

        var result = Assert.IsType<BadRequestObjectResult>(response.Result);
        Assert.Contains("500", result.Value!.ToString());
    }

    [Fact]
    public async Task Execute_ValidBatch_ReturnsOutcome()
    {
        var outcome = new SubtitlePostProcessExecuteResponse
        {
            Mode = "traditional",
            Scanned = 4,
            Converted = 2,
            Skipped = 2,
            Failed = [new SubtitlePostProcessFailedItem { Path = "/m/a.srt", Error = "Thrower: locked" }]
        };
        var (controller, batch) = Create();
        batch
            .Setup(service => service.ExecuteAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(outcome);

        var response = await controller.Execute(CancellationToken.None);

        var result = Assert.IsType<OkObjectResult>(response.Result);
        Assert.Same(outcome, result.Value);
    }
}
