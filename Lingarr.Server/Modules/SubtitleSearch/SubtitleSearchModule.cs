using Lingarr.Server.Modules.SubtitleSearch;

namespace Lingarr.Server.Modules.SubtitleSearch;

/// <summary>
/// Subtitle search &amp; download (docs/subtitle-search-tech-design.zh-CN.md):
/// provider ABI, registry, orchestration, download pipeline and history.
/// </summary>
public static class SubtitleSearchModule
{
    public static void AddSubtitleSearchModule(this IServiceCollection services)
    {
        // Singleton catalog over the ISubtitleProvider registrations. The M1
        // batch ships the registry and ABI only — M2 adds the seven v1
        // sources (each with its named HttpClient "subtitle-<provider>" and
        // DelegatingHandler chain: cooldown → token bucket → retry).
        services.AddSingleton<SubtitleProviderRegistry>();
    }
}
