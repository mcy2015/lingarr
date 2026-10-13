using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Lingarr.Contracts.Exceptions;
using Lingarr.Contracts.Models;
using Lingarr.Contracts.Translation;
using Lingarr.Core.Entities;
using Lingarr.Server.Controllers;
using Lingarr.Server.Interfaces.Services;
using Lingarr.Server.Interfaces.Services.Translation;
using Lingarr.Server.Models.Api;
using Lingarr.Server.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Lingarr.Server.Tests.Controllers;

/// <summary>
/// Single-line translation preview (PM analysis P0, prototype screen-10
/// 「试译预览」): runs one line through the named service — the provider's
/// TranslateAsync is the path that renders its saved request template —
/// and reports latency, per-call cost and glossary hits.
/// </summary>
public class TranslateControllerPreviewTests
{
    private readonly Mock<ITranslationServiceFactory> _factoryMock = new();
    private readonly Mock<IGlossaryService> _glossaryMock = new();
    private readonly Mock<ICostService> _costServiceMock = new();
    private readonly Mock<ITranslationService> _serviceMock = new();

    public TranslateControllerPreviewTests()
    {
        _factoryMock
            .Setup(factory => factory.CreateTranslationService(It.IsAny<string>()))
            .Returns(_serviceMock.Object);
        _serviceMock
            .Setup(service => service.GetLanguagePair(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((string source, string target, CancellationToken _) =>
                new LanguagePair { Source = source, Target = target, Tier = MatchTier.Exact });
        _glossaryMock
            .Setup(glossary => glossary.FindHits(It.IsAny<string>()))
            .ReturnsAsync([]);
        _costServiceMock
            .Setup(costService => costService.GetRates())
            .ReturnsAsync(new Dictionary<string, decimal>());
    }

    private TranslateController CreateController() =>
        new(
            _factoryMock.Object,
            new Mock<ITranslationRequestService>().Object,
            new Mock<ISettingService>().Object,
            new LanguageCodeService(),
            new Mock<ITranslationHealthService>().Object,
            _glossaryMock.Object,
            _costServiceMock.Object,
            NullLogger<TranslateController>.Instance);

    /// <summary>The actions return Ok(result), so the payload sits inside
    /// the OkObjectResult rather than on ActionResult&lt;T&gt;.Value.</summary>
    private async Task<TranslationPreviewResponse> Invoke(TranslationPreviewRequest request)
    {
        var actionResult = await CreateController().PreviewTranslation(request, CancellationToken.None);
        return (TranslationPreviewResponse)((OkObjectResult)actionResult.Result!).Value!;
    }

    private static TranslationPreviewRequest NewRequest(
        string text = "The Shōgun commands the Anjin to sail west.",
        string? source = null,
        string? target = null,
        string serviceType = "openai") => new()
    {
        ServiceType = serviceType,
        Text = text,
        SourceLanguage = source,
        TargetLanguage = target
    };

    [Fact]
    public async Task Preview_Success_ReturnsTranslationLatencyCostAndGlossaryHits()
    {
        const string text = "The Shōgun commands the Anjin to sail west.";
        _serviceMock
            .Setup(service => service.TranslateAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<List<string>?>(),
                It.IsAny<List<string>?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync("将军命令按针向西航行。");
        _glossaryMock
            .Setup(glossary => glossary.FindHits(text))
            .ReturnsAsync([
                new TranslationGlossary { SourceTerm = "Anjin", TargetTerm = "按针", IsLocked = true }
            ]);
        _costServiceMock
            .Setup(costService => costService.GetRates())
            .ReturnsAsync(new Dictionary<string, decimal> { ["openai"] = 2.0m });

        var result = await Invoke(NewRequest());

        Assert.True(result.Ok);
        Assert.Equal("将军命令按针向西航行。", result.Translated);
        Assert.True(result.LatencyMs >= 0);
        // the source text's characters at $2 per 1M
        Assert.Equal(2.0 * text.Length / 1_000_000, result.Cost!.Value, precision: 12);
        var hit = Assert.Single(result.GlossaryHits);
        Assert.Equal("Anjin", hit.SourceTerm);
        Assert.Equal("按针", hit.TargetTerm);
        Assert.True(hit.IsLocked);
        _serviceMock.Verify(service => service.TranslateAsync(
            text,
            "en",
            "zh",
            null,
            null,
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Preview_NoRateForProvider_CostIsNull()
    {
        _serviceMock
            .Setup(service => service.TranslateAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<List<string>?>(),
                It.IsAny<List<string>?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync("translation");

        var result = await Invoke(NewRequest());

        Assert.True(result.Ok);
        Assert.Null(result.Cost);
    }

    [Fact]
    public async Task Preview_LanguagesDefaultToEnZh()
    {
        _serviceMock
            .Setup(service => service.TranslateAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<List<string>?>(),
                It.IsAny<List<string>?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync("translation");

        await Invoke(NewRequest());

        _serviceMock.Verify(service => service.GetLanguagePair(
            "en",
            "zh",
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Preview_ExplicitLanguagesArePassedThrough()
    {
        _serviceMock
            .Setup(service => service.TranslateAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<List<string>?>(),
                It.IsAny<List<string>?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync("translation");

        await Invoke(NewRequest(source: "ja", target: "zh-TW"));

        _serviceMock.Verify(service => service.TranslateAsync(
            It.IsAny<string>(),
            "ja",
            "zh-TW",
            null,
            null,
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Preview_ProviderFails_ReturnsStructuredErrorWithoutThrowing()
    {
        _serviceMock
            .Setup(service => service.TranslateAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<List<string>?>(),
                It.IsAny<List<string>?>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TranslationException("OpenAI API key or model is not configured."));

        var result = await Invoke(NewRequest());

        Assert.False(result.Ok);
        Assert.Null(result.Translated);
        Assert.Contains("not configured", result.Message);
    }

    [Fact]
    public async Task Preview_UnsupportedLanguagePair_ReturnsStructuredError()
    {
        _serviceMock
            .Setup(service => service.GetLanguagePair(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((LanguagePair?)null);

        var result = await Invoke(NewRequest());

        Assert.False(result.Ok);
        Assert.Contains("does not support", result.Message);
        _serviceMock.Verify(service => service.TranslateAsync(
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<List<string>?>(),
            It.IsAny<List<string>?>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Preview_UnknownServiceType_ReturnsStructuredError()
    {
        _factoryMock
            .Setup(factory => factory.CreateTranslationService("nope"))
            .Throws(new ArgumentException("Unsupported translation service type", "serviceType"));

        var result = await Invoke(NewRequest(serviceType: "nope"));

        Assert.False(result.Ok);
        Assert.Contains("Unsupported translation service type", result.Message);
    }

    [Fact]
    public async Task Preview_EmptyText_ReturnsEmptyWithoutCallingProvider()
    {
        var result = await Invoke(NewRequest(text: "  "));

        Assert.True(result.Ok);
        Assert.Equal("  ", result.Translated);
        _serviceMock.Verify(service => service.TranslateAsync(
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<List<string>?>(),
            It.IsAny<List<string>?>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }
}
