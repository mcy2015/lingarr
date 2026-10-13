using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Lingarr.Contracts.Exceptions;
using Lingarr.Contracts.Models;
using Lingarr.Contracts.Translation;
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
/// Active connection probe (PM analysis P0, prototype screen-06
/// 「测试连接」): a real minimal EN→ZH translation per provider, never a
/// 500 — provider failures land as ok=false with a message.
/// </summary>
public class TranslateControllerConnectionTests
{
    private static Mock<ITranslationService> CreateTranslationService(
        string? translateResult = "你好",
        LanguagePair? pair = null)
    {
        pair ??= new LanguagePair { Source = "en", Target = "zh", Tier = MatchTier.Exact };
        var serviceMock = new Mock<ITranslationService>();
        serviceMock
            .Setup(service => service.GetLanguagePair(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(pair);
        serviceMock
            .Setup(service => service.TranslateAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<List<string>?>(),
                It.IsAny<List<string>?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(translateResult ?? string.Empty);
        return serviceMock;
    }

    private static TranslateController CreateController(
        Mock<ITranslationServiceFactory> factoryMock) =>
        new(
            factoryMock.Object,
            new Mock<ITranslationRequestService>().Object,
            new Mock<ISettingService>().Object,
            new LanguageCodeService(),
            new Mock<ITranslationHealthService>().Object,
            new Mock<IGlossaryService>().Object,
            new Mock<ICostService>().Object,
            NullLogger<TranslateController>.Instance);

    /// <summary>
    /// The actions return Ok(result), so the payload sits inside the
    /// OkObjectResult rather than on ActionResult&lt;T&gt;.Value.
    /// </summary>
    private static async Task<TranslationTestResult> Invoke(
        TranslateController controller,
        string serviceType)
    {
        var actionResult = await controller.TestServiceConnection(serviceType, CancellationToken.None);
        return (TranslationTestResult)((OkObjectResult)actionResult.Result!).Value!;
    }

    [Fact]
    public async Task TestConnection_Success_ReturnsOkWithLatencyAndTranslation()
    {
        var serviceMock = CreateTranslationService(translateResult: "你好");
        var factoryMock = new Mock<ITranslationServiceFactory>();
        factoryMock
            .Setup(factory => factory.CreateTranslationService("openai"))
            .Returns(serviceMock.Object);
        var controller = CreateController(factoryMock);

        var result = await Invoke(controller, "openai");

        Assert.True(result.Ok);
        Assert.Equal(200, result.Status);
        Assert.True(result.LatencyMs >= 0);
        Assert.Contains("你好", result.Message);
        Assert.Contains("Hello", result.Message);
        serviceMock.Verify(service => service.TranslateAsync(
            "Hello",
            "en",
            "zh",
            null,
            null,
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task TestConnection_ProviderThrows_ReturnsOkFalseWithoutThrowing()
    {
        var serviceMock = CreateTranslationService();
        serviceMock
            .Setup(service => service.TranslateAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<List<string>?>(),
                It.IsAny<List<string>?>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TranslationException("Retry limit reached after TooManyRequests.", new InvalidOperationException("boom")));
        var factoryMock = new Mock<ITranslationServiceFactory>();
        factoryMock
            .Setup(factory => factory.CreateTranslationService("gemini"))
            .Returns(serviceMock.Object);
        var controller = CreateController(factoryMock);

        var result = await Invoke(controller, "gemini");

        Assert.False(result.Ok);
        Assert.Null(result.Status);
        Assert.Contains("Retry limit reached", result.Message);
    }

    [Fact]
    public async Task TestConnection_HttpFailure_ExtractsProviderStatusCode()
    {
        var serviceMock = CreateTranslationService();
        serviceMock
            .Setup(service => service.TranslateAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<List<string>?>(),
                It.IsAny<List<string>?>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException(
                "rate limited",
                null,
                HttpStatusCode.TooManyRequests));
        var factoryMock = new Mock<ITranslationServiceFactory>();
        factoryMock
            .Setup(factory => factory.CreateTranslationService("gemini"))
            .Returns(serviceMock.Object);
        var controller = CreateController(factoryMock);

        var result = await Invoke(controller, "gemini");

        Assert.False(result.Ok);
        Assert.Equal(429, result.Status);
        Assert.Contains("rate limited", result.Message);
    }

    [Fact]
    public async Task TestConnection_UnsupportedLanguagePair_ReturnsOkFalseWithoutCallingProvider()
    {
        var serviceMock = CreateTranslationService();
        serviceMock
            .Setup(service => service.GetLanguagePair(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((LanguagePair?)null);
        var factoryMock = new Mock<ITranslationServiceFactory>();
        factoryMock
            .Setup(factory => factory.CreateTranslationService("deepl"))
            .Returns(serviceMock.Object);
        var controller = CreateController(factoryMock);

        var result = await Invoke(controller, "deepl");

        Assert.False(result.Ok);
        Assert.Contains("does not support", result.Message);
        serviceMock.Verify(service => service.TranslateAsync(
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<List<string>?>(),
            It.IsAny<List<string>?>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task TestConnection_UnknownServiceType_ReturnsOkFalse()
    {
        var factoryMock = new Mock<ITranslationServiceFactory>();
        factoryMock
            .Setup(factory => factory.CreateTranslationService("nope"))
            .Throws(new ArgumentException("Unsupported translation service type", "serviceType"));
        var controller = CreateController(factoryMock);

        var result = await Invoke(controller, "nope");

        Assert.False(result.Ok);
        Assert.Contains("Unsupported translation service type", result.Message);
    }

    [Fact]
    public async Task TestConnection_CancelledToken_Throws()
    {
        var factoryMock = new Mock<ITranslationServiceFactory>();
        factoryMock
            .Setup(factory => factory.CreateTranslationService("openai"))
            .Returns(CreateTranslationService().Object);
        var controller = CreateController(factoryMock);
        using var cancellationTokenSource = new CancellationTokenSource();
        await cancellationTokenSource.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            controller.TestServiceConnection("openai", cancellationTokenSource.Token));
    }
}
