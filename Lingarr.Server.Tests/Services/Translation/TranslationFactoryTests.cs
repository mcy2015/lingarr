using System;
using System.Net.Http;
using GTranslate.Translators;
using Lingarr.Server.Modules.Settings;
using Lingarr.Server.Modules.Translation;
using Lingarr.Server.Modules.Translation.Base;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Lingarr.Server.Tests.Services.Translation;

/// <summary>
/// Service-type resolution in the translation factory (language-policy
/// "unknown" root cause): caller-facing spellings such as
/// "bingtranslator"/"googletranslate" must resolve to the canonical keys
/// "bing"/"google" by stripping the common suffix, while legal keys that
/// merely end in "translate" ("libretranslate") stay exact-match only and
/// genuinely unknown types still throw.
/// </summary>
public class TranslationFactoryTests
{
    private readonly TranslationFactory _factory;

    public TranslationFactoryTests()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(_ => new LanguageCodeService());
        services.AddSingleton<IHttpClientFactory, StubHttpClientFactory>();
        services.AddSingleton(_ => new HttpClient());
        services.AddSingleton(new Mock<ISettingService>().Object);
        services.AddSingleton(new Mock<ITranslationHealthService>().Object);
        var provider = services.BuildServiceProvider();

        var environment = new Mock<IHostEnvironment>();
        environment.Setup(e => e.ContentRootPath).Returns("/app");

        _factory = new TranslationFactory(
            provider,
            NullLogger<TranslationFactory>.Instance,
            environment.Object);
    }

    [Theory]
    [InlineData("bingtranslator", typeof(GTranslatorService<BingTranslator>))]
    [InlineData("googletranslator", typeof(GTranslatorService<GoogleTranslator>))]
    [InlineData("googletranslate", typeof(GTranslatorService<GoogleTranslator>))]
    [InlineData("microsofttranslator", typeof(GTranslatorService<MicrosoftTranslator>))]
    [InlineData("yandextranslate", typeof(GTranslatorService<YandexTranslator>))]
    public void Create_StripsTranslatorSuffix_ToCanonicalKey(string requested, Type expected)
    {
        var service = _factory.CreateTranslationService(requested);

        Assert.IsType(expected, service);
    }

    [Fact]
    public void Create_IsCaseInsensitive()
    {
        var service = _factory.CreateTranslationService("BingTranslator");

        Assert.IsType<GTranslatorService<BingTranslator>>(service);
    }

    [Fact]
    public void Create_LibreTranslate_MatchesExactlyAndIsNotStripped()
    {
        // "libretranslate" itself ends in "translate" — it is a legal key and
        // must hit the exact arm; suffix stripping would turn it into "libre"
        // and fail. The LibreService instance proves the exact path ran.
        var service = _factory.CreateTranslationService("libretranslate");

        Assert.IsType<LibreService>(service);
    }

    [Fact]
    public void Create_CanonicalKeysStillResolveDirectly()
    {
        var service = _factory.CreateTranslationService("bing");

        Assert.IsType<GTranslatorService<BingTranslator>>(service);
    }

    [Theory]
    [InlineData("definitelynotaservice")]
    [InlineData("notarealservicetranslator")] // stripped candidate is unknown too
    [InlineData("translate")]                 // stripping would leave an empty key
    public void Create_UnknownType_AfterSuffixRetry_StillThrows(string requested)
    {
        Assert.Throws<ArgumentException>(() => _factory.CreateTranslationService(requested));
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}
