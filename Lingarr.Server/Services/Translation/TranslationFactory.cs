using GTranslate.Translators;
using Lingarr.Contracts.Translation;
using Lingarr.Server.Interfaces.Services;
using Lingarr.Server.Interfaces.Services.Translation;
using Microsoft.Extensions.Hosting;

namespace Lingarr.Server.Services.Translation;

public class TranslationFactory : ITranslationServiceFactory
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<TranslationFactory> _logger;
    private readonly string _staticsPath;

    public TranslationFactory(IServiceProvider serviceProvider,
        ILogger<TranslationFactory> logger,
        IHostEnvironment environment)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        // Resolve relative to the content root so non-container runs work
        // (in Docker the content root is /app, so the path is unchanged).
        _staticsPath = Path.Combine(environment.ContentRootPath, "Statics");
    }

    /// <inheritdoc />
    public ITranslationService CreateTranslationService(string serviceType)
    {
        var languageCodeService = _serviceProvider.GetRequiredService<LanguageCodeService>();
        return serviceType.ToLower() switch
        {
            "libretranslate" => new LibreService(
                _serviceProvider.GetRequiredService<IHttpClientFactory>().CreateClient(),
                _serviceProvider.GetRequiredService<ISettingService>(),
                _serviceProvider.GetRequiredService<ILogger<LibreService>>(),
                languageCodeService),

            "google" => new GTranslatorService<GoogleTranslator>(
                _serviceProvider,
                _serviceProvider.GetRequiredService<IHttpClientFactory>(),
                $"{_staticsPath}/google_languages.json",
                _serviceProvider.GetRequiredService<ISettingService>(),
                _serviceProvider.GetRequiredService<ILogger<GoogleTranslator>>(),
                languageCodeService
            ),

            "bing" => new GTranslatorService<BingTranslator>(
                _serviceProvider,
                _serviceProvider.GetRequiredService<IHttpClientFactory>(),
                $"{_staticsPath}/bing_languages.json",
                _serviceProvider.GetRequiredService<ISettingService>(),
                _serviceProvider.GetRequiredService<ILogger<BingTranslator>>(),
                languageCodeService
            ),

            "microsoft" => new GTranslatorService<MicrosoftTranslator>(
                _serviceProvider,
                _serviceProvider.GetRequiredService<IHttpClientFactory>(),
                $"{_staticsPath}/microsoft_languages.json",
                _serviceProvider.GetRequiredService<ISettingService>(),
                _serviceProvider.GetRequiredService<ILogger<MicrosoftTranslator>>(),
                languageCodeService
            ),

            "yandex" => new GTranslatorService<YandexTranslator>(
                _serviceProvider,
                _serviceProvider.GetRequiredService<IHttpClientFactory>(),
                $"{_staticsPath}/yandex_languages.json",
                _serviceProvider.GetRequiredService<ISettingService>(),
                _serviceProvider.GetRequiredService<ILogger<YandexTranslator>>(),
                languageCodeService
            ),

            "deepl" => new DeepLService(
                _serviceProvider.GetRequiredService<ISettingService>(),
                _serviceProvider.GetRequiredService<ILogger<DeepLService>>(),
                languageCodeService
            ),

            "openai" => new OpenAiService(
                _serviceProvider.GetRequiredService<ISettingService>(),
                _serviceProvider.GetRequiredService<ILogger<OpenAiService>>(),
                languageCodeService,
                _serviceProvider.GetRequiredService<IRequestTemplateService>(),
                _serviceProvider.GetRequiredService<HttpClient>(),
                _serviceProvider.GetRequiredService<IHttpClientFactory>()
            ),

            "anthropic" => new AnthropicService(
                _serviceProvider.GetRequiredService<ISettingService>(),
                _serviceProvider.GetRequiredService<HttpClient>(),
                _serviceProvider.GetRequiredService<ILogger<AnthropicService>>(),
                languageCodeService,
                _serviceProvider.GetRequiredService<IRequestTemplateService>()
            ),

            "localai" => new LocalAiService(
                _serviceProvider.GetRequiredService<ISettingService>(),
                _serviceProvider.GetRequiredService<HttpClient>(),
                _serviceProvider.GetRequiredService<ILogger<LocalAiService>>(),
                languageCodeService,
                _serviceProvider.GetRequiredService<IRequestTemplateService>()
            ),

            "deepseek" => new DeepSeekService(
                _serviceProvider.GetRequiredService<ISettingService>(),
                _serviceProvider.GetRequiredService<HttpClient>(),
                _serviceProvider.GetRequiredService<ILogger<DeepSeekService>>(),
                languageCodeService,
                _serviceProvider.GetRequiredService<IRequestTemplateService>()
            ),

            "mistral" => new MistralService(
                _serviceProvider.GetRequiredService<ISettingService>(),
                _serviceProvider.GetRequiredService<HttpClient>(),
                _serviceProvider.GetRequiredService<IHttpClientFactory>(),
                _serviceProvider.GetRequiredService<ILogger<MistralService>>(),
                languageCodeService,
                _serviceProvider.GetRequiredService<IRequestTemplateService>()
            ),

            "xai" => new XAiService(
                _serviceProvider.GetRequiredService<ISettingService>(),
                _serviceProvider.GetRequiredService<HttpClient>(),
                _serviceProvider.GetRequiredService<IHttpClientFactory>(),
                _serviceProvider.GetRequiredService<ILogger<XAiService>>(),
                languageCodeService,
                _serviceProvider.GetRequiredService<IRequestTemplateService>()
            ),

            "gemini" => new GoogleGeminiService(
                _serviceProvider.GetRequiredService<ISettingService>(),
                _serviceProvider.GetRequiredService<HttpClient>(),
                _serviceProvider.GetRequiredService<ILogger<GoogleGeminiService>>(),
                languageCodeService,
                _serviceProvider.GetRequiredService<IRequestTemplateService>()
            ),

            // load any registered plugins
            _ => _serviceProvider.GetKeyedService<ITranslationService>(serviceType.ToLowerInvariant())
                 ?? throw new ArgumentException("Unsupported translation service type", nameof(serviceType))
        };
    }

    /// <inheritdoc />
    public IReadOnlyList<TranslationServiceEntry> CreateTranslationServices(IReadOnlyList<string> serviceTypes)
    {
        var services = new List<TranslationServiceEntry>(serviceTypes.Count);
        foreach (var serviceType in serviceTypes)
        {
            var name = serviceType.ToLowerInvariant();
            try
            {
                var service = CreateTranslationService(name);
                services.Add(new TranslationServiceEntry(name, service, service as IBatchTranslationService));
            }
            catch (ArgumentException)
            {
                _logger.LogWarning("Skipping unknown translation service '{ServiceType}'.", name);
            }
        }
        return services;
    }
}