using Lingarr.Contracts.Interfaces.Plugins;
using Lingarr.Contracts.Settings;
using Lingarr.Server.Interfaces.Services;
using Lingarr.Server.Interfaces.Services.Translation;
using Lingarr.Server.Modules.Infrastructure;
using Lingarr.Server.Services;
using Lingarr.Server.Services.Plugins;
using Lingarr.Server.Services.Plugins.Manifests;
using Lingarr.Server.Services.Translation;

namespace Lingarr.Server.Modules.Translation;

/// <summary>
/// Translation requests, pipelines, pricing and the translation provider
/// plugin system.
/// </summary>
public static class TranslationModule
{
    public static void AddTranslationModule(this IServiceCollection services)
    {
        services.AddSingleton<IAsyncTranslationRegistry, AsyncTranslationRegistry>();
        services.AddSingleton<IJobQueue, JobQueue>();
        services.AddScoped<ITranslationRequestQueryService, TranslationRequestQueryService>();
        services.AddScoped<ITranslationRequestQueueService, TranslationRequestQueueService>();
        services.AddScoped<IProofreadRequestService, ProofreadRequestService>();
        services.AddScoped<IContentTranslationService, ContentTranslationService>();
        services.AddScoped<ITranslationRequestService, TranslationRequestService>();
        services.AddScoped<ITranslationRequestEventService, TranslationRequestEventService>();
        services.AddScoped<IProgressService, ProgressService>();
        services.AddScoped<IGlossaryService, GlossaryService>();
        services.AddScoped<ICostService, CostService>();
        services.AddScoped<IImportEstimateService, ImportEstimateService>();
        services.AddSingleton<ITranslationHealthService, TranslationHealthService>();

        // Register translate services
        services.AddScoped<ITranslationServiceFactory, TranslationFactory>();
        services.AddScoped<ITranslationPipeline, TranslationPipeline>();
        services.AddSingleton<LanguageCodeService>();
        services.AddSingleton<IRequestTemplateService, RequestTemplateService>();

        // Register manifests
        services.AddSingleton<IPluginManifest, AnthropicPluginManifest>();
        services.AddSingleton<IPluginManifest, OpenAiPluginManifest>();
        services.AddSingleton<IPluginManifest, GeminiPluginManifest>();
        services.AddSingleton<IPluginManifest, DeepSeekPluginManifest>();
        services.AddSingleton<IPluginManifest, MistralPluginManifest>();
        services.AddSingleton<IPluginManifest, XAiPluginManifest>();
        services.AddSingleton<IPluginManifest, LocalAiPluginManifest>();
        services.AddSingleton<IPluginManifest, DeepLPluginManifest>();
        services.AddSingleton<IPluginManifest, LibreTranslatePluginManifest>();
        services.AddSingleton<IPluginManifest, GoogleTranslatePluginManifest>();
        services.AddSingleton<IPluginManifest, BingTranslatePluginManifest>();
        services.AddSingleton<IPluginManifest, MicrosoftTranslatePluginManifest>();
        services.AddSingleton<IPluginManifest, YandexTranslatePluginManifest>();

        // Plugin discovery and the read settings
        services.AddSingleton<IPluginRegistry, PluginRegistry>();
        services.AddScoped<ISettingsAccess, SettingsAccess>();

        // Scan for external plugins before the container is ready
        var pluginLoaderLogger = LoggerFactory
            .Create(InfrastructureModule.AddLogProviders)
            .CreateLogger<PluginLoader>();
        var pluginLoader = new PluginLoader(pluginLoaderLogger);
        pluginLoader.LoadPlugins(services);
        services.AddSingleton(pluginLoader);
    }
}
