using System.Reflection;
using Lingarr.Contracts.Interfaces.Plugins;
using Lingarr.Server.Modules.SubtitleSearch.Abstractions;

namespace Lingarr.Server.Modules.SubtitleSearch;

/// <summary>
/// One registered subtitle source: the provider instance plus the settings
/// metadata the registry could resolve. <see cref="Manifest"/> stays null
/// until the source manifests land (M2) — the lookup path itself ships in M1
/// so manifests only need to be registered in DI from then on.
/// </summary>
public sealed class RegisteredSubtitleProvider
{
    /// <summary>The provider implementation.</summary>
    public required ISubtitleProvider Provider { get; init; }

    /// <summary>Whether the source needs credentials — from <see cref="SubtitleProviderAttribute"/>.</summary>
    public required bool RequiresAuth { get; init; }

    /// <summary>
    /// Settings-form manifest matched by provider key (IPluginManifest
    /// reuse, tech design ①). Null when no manifest is registered for the
    /// source yet.
    /// </summary>
    public IPluginManifest? Manifest { get; init; }
}

/// <summary>
/// Catalog of the compiled-in subtitle sources, built once at startup from
/// the <see cref="ISubtitleProvider"/> DI registrations — the parallel of
/// the translation <c>PluginRegistry</c> (tech design ①), minus the external
/// DLL loading: subtitle sources ship compiled-in only.
/// <para>
/// Source-key matching against the registered <c>IPluginManifest</c>
/// instances is case-insensitive, mirroring the translation registry's
/// dictionary semantics; translation manifests without a matching subtitle
/// provider are simply not linked.
/// </para>
/// </summary>
public sealed class SubtitleProviderRegistry
{
    private readonly List<RegisteredSubtitleProvider> _providers;
    private readonly Dictionary<string, RegisteredSubtitleProvider> _byProvider;

    public SubtitleProviderRegistry(
        IEnumerable<ISubtitleProvider> providers,
        IEnumerable<IPluginManifest> manifests,
        ILogger<SubtitleProviderRegistry> logger)
    {
        _providers = [];
        _byProvider = new Dictionary<string, RegisteredSubtitleProvider>(StringComparer.OrdinalIgnoreCase);

        // First manifest per key wins, as with duplicate provider classes
        // below — registration order defines precedence deterministically.
        var manifestByProvider = new Dictionary<string, IPluginManifest>(StringComparer.OrdinalIgnoreCase);
        foreach (var manifest in manifests)
        {
            if (!manifestByProvider.ContainsKey(manifest.Provider))
            {
                manifestByProvider[manifest.Provider] = manifest;
            }
        }

        foreach (var provider in providers)
        {
            var attribute = provider.GetType().GetCustomAttribute<SubtitleProviderAttribute>();
            var name = attribute?.Name ?? provider.Provider;
            if (_byProvider.ContainsKey(name))
            {
                logger.LogWarning(
                    "Subtitle provider {Provider} is already registered; ignoring {ProviderType}.",
                    name,
                    provider.GetType().FullName);
                continue;
            }

            manifestByProvider.TryGetValue(name, out var manifest);
            var entry = new RegisteredSubtitleProvider
            {
                Provider = provider,
                RequiresAuth = attribute?.RequiresAuth ?? false,
                Manifest = manifest
            };

            _providers.Add(entry);
            _byProvider[name] = entry;
        }

        // Registered manifests without a subtitle provider are the
        // translation plugins — expected until M2 adds the source manifests;
        // surfaced at debug level so a typo'd source key is still findable.
        foreach (var unmatched in manifestByProvider.Keys.Where(key => !_byProvider.ContainsKey(key)))
        {
            logger.LogDebug(
                "Manifest {Provider} has no matching subtitle provider.",
                unmatched);
        }
    }

    /// <summary>All registered sources, in registration order.</summary>
    public IReadOnlyList<RegisteredSubtitleProvider> All => _providers;

    /// <summary>Case-insensitive lookup by source key; null when unknown.</summary>
    public RegisteredSubtitleProvider? Find(string provider)
    {
        return _byProvider.TryGetValue(provider, out var entry) ? entry : null;
    }
}
