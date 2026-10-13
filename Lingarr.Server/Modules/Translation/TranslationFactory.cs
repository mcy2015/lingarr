using GTranslate.Translators;
using Lingarr.Contracts.Translation;
using Lingarr.Server.Modules.Translation;
using Microsoft.Extensions.Hosting;
using Lingarr.Server.Modules.Settings;
using Lingarr.Server.Modules.Translation.Base;

namespace Lingarr.Server.Modules.Translation;

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
        var name = serviceType.ToLowerInvariant();

        // Exact key first ("libretranslate" is itself a legal key and is never
        // mangled); only when nothing matches do we retry with the common
        // "translator"/"translate" suffix stripped so caller-facing spellings
        // like "bingtranslator"/"googletranslator" resolve to "bing"/"google".
        ITranslationService? service = null;
        var resolved = name;
        foreach (var candidate in ServiceKeyCandidates(name))
        {
            service = TryCreateService(candidate, languageCodeService);
            if (service is not null)
            {
                resolved = candidate;
                break;
            }
        }

        if (service is null)
        {
            throw new ArgumentException("Unsupported translation service type", nameof(serviceType));
        }

        // Feed the shared health tracker from the base retry loop; plugin ABI
        // implementations (no base class) intentionally stay untracked. The
        // canonical key keeps snapshots aligned with the plugin registry ids.
        if (service is Base.BaseTranslationService builtIn)
        {
            builtIn.SetHealthTracking(
                _serviceProvider.GetRequiredService<ITranslationHealthService>(),
                resolved);
        }

        return service;
    }

    /// <summary>
    /// Candidate service keys for one requested type: the lowercased input
    /// first, then the input with the "translator" and "translate" suffixes
    /// stripped (longest suffix first so "xtranslator" never loses a stray
    /// "r"). Empty candidates are skipped.
    /// </summary>
    private static IEnumerable<string> ServiceKeyCandidates(string name)
    {
        yield return name;
        if (name.EndsWith("translator", StringComparison.Ordinal))
        {
            var stripped = name[..^"translator".Length];
            if (stripped.Length > 0)
            {
                yield return stripped;
            }
        }
        else if (name.EndsWith("translate", StringComparison.Ordinal))
        {
            var stripped = name[..^"translate".Length];
            if (stripped.Length > 0)
            {
                yield return stripped;
            }
        }
    }

    private ITranslationService? TryCreateService(string name, LanguageCodeService languageCodeService) =>
        name switch
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
            _ => _serviceProvider.GetKeyedService<ITranslationService>(name)
        };

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