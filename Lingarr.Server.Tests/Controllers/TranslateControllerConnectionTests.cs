using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Lingarr.Contracts.Exceptions;
using Lingarr.Contracts.Models;
using Lingarr.Contracts.Translation;
using Lingarr.Core.Configuration;
using Lingarr.Server.Controllers;
using Lingarr.Server.Modules.Translation;
using Lingarr.Server.Models;
using Lingarr.Server.Models.Api;
using Lingarr.Server.Tests.Services.Translation;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;
using Lingarr.Server.Modules.Settings;
using Lingarr.Server.Modules.Translation.Base;

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
        Mock<ITranslationServiceFactory> factoryMock,
        Mock<ISettingService>? settingsMock = null) =>
        new(
            factoryMock.Object,
            new Mock<ITranslationRequestService>().Object,
            (settingsMock ?? new Mock<ISettingService>()).Object,
            new LanguageCodeService(),
            new Mock<ITranslationHealthService>().Object,
            new Mock<IGlossaryService>().Object,
            new Mock<ICostService>().Object,
            NullLogger<TranslateController>.Instance);

    /// <summary>
    /// Configures the source/target language settings used to derive the
    /// default probe pair.
    /// </summary>
    private static void SetupLanguages(
        Mock<ISettingService> settingsMock,
        string sourceCode,
        string targetCode)
    {
        settingsMock
            .Setup(settings => settings.GetSettingAsJson<Language>(
                SettingKeys.Translation.SourceLanguages))
            .ReturnsAsync([new Language { code = sourceCode, name = sourceCode }]);
        settingsMock
            .Setup(settings => settings.GetSettingAsJson<TargetLanguage>(
                SettingKeys.Translation.TargetLanguages))
            .ReturnsAsync([new TargetLanguage { Name = targetCode, Code = targetCode }]);
    }

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
    public async Task TestConnection_WrappedHttpFailure_ExtractsStatusCodeFromInnerChain()
    {
        // The shared retry pipeline wraps provider HttpRequestExceptions in a
        // TranslationException — the 429 only exists on the inner exception.
        var serviceMock = CreateTranslationService();
        serviceMock
            .Setup(service => service.TranslateAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<List<string>?>(),
                It.IsAny<List<string>?>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TranslationException(
                "Retry limit reached after TooManyRequests.",
                new HttpRequestException("rate limited", null, HttpStatusCode.TooManyRequests)));
        var factoryMock = new Mock<ITranslationServiceFactory>();
        factoryMock
            .Setup(factory => factory.CreateTranslationService("openai"))
            .Returns(serviceMock.Object);
        var controller = CreateController(factoryMock);

        var result = await Invoke(controller, "openai");

        Assert.False(result.Ok);
        Assert.Equal(429, result.Status);
        Assert.Contains("Retry limit reached", result.Message);
    }

    [Fact]
    public async Task TestConnection_UsesConfiguredLanguagePairInsteadOfHardcodedEnZh()
    {
        var serviceMock = CreateTranslationService(pair:
            new LanguagePair { Source = "ja", Target = "zh", Tier = MatchTier.Exact });
        var factoryMock = new Mock<ITranslationServiceFactory>();
        factoryMock
            .Setup(factory => factory.CreateTranslationService("openai"))
            .Returns(serviceMock.Object);
        var settingsMock = new Mock<ISettingService>();
        SetupLanguages(settingsMock, sourceCode: "ja", targetCode: "zh");
        var controller = CreateController(factoryMock, settingsMock);

        var result = await Invoke(controller, "openai");

        Assert.True(result.Ok);
        serviceMock.Verify(service => service.GetLanguagePair(
            "ja",
            "zh",
            It.IsAny<CancellationToken>()), Times.Once);
        serviceMock.Verify(service => service.TranslateAsync(
            "Hello",
            "ja",
            "zh",
            null,
            null,
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task TestConnection_InvalidConfiguredCodesFallBackToEnZh()
    {
        var serviceMock = CreateTranslationService(pair:
            new LanguagePair { Source = "en", Target = "zh", Tier = MatchTier.Exact });
        var factoryMock = new Mock<ITranslationServiceFactory>();
        factoryMock
            .Setup(factory => factory.CreateTranslationService("openai"))
            .Returns(serviceMock.Object);
        var settingsMock = new Mock<ISettingService>();
        SetupLanguages(settingsMock, sourceCode: "not-a-code", targetCode: "");
        var controller = CreateController(factoryMock, settingsMock);

        var result = await Invoke(controller, "openai");

        Assert.True(result.Ok);
        serviceMock.Verify(service => service.GetLanguagePair(
            "en",
            "zh",
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task TestConnection_ProbeRunsSingleAttemptAgainstRealRetryPipeline()
    {
        // End-to-end over the real BaseTranslationService retry loop: the
        // probe must report the wrapped 429 immediately (one provider call,
        // no backoff ladder) instead of hanging the button for seconds.
        var probeService = new RateLimitedTranslationService();
        var factoryMock = new Mock<ITranslationServiceFactory>();
        factoryMock
            .Setup(factory => factory.CreateTranslationService("openai"))
            .Returns(probeService);
        var controller = CreateController(factoryMock);

        var result = await Invoke(controller, "openai");

        Assert.False(result.Ok);
        Assert.Equal(429, result.Status);
        Assert.Equal(1, probeService.OperationCalls);
        Assert.Contains("Retry limit reached", result.Message);
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
