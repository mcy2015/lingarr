using System.Reflection;
using Lingarr.Server.Modules.SubtitleSearch.Abstractions;

namespace Lingarr.Server.Modules.SubtitleSearch;

/// <summary>
/// One registered subtitle source: the provider instance plus the settings
/// metadata the registry could resolve. <see cref="Manifest"/> is null when
/// no <see cref="ISubtitleSourceManifest"/> is registered for the source
/// (every v1 source ships one; the null path stays for test doubles).
/// </summary>
public sealed class RegisteredSubtitleProvider
{
    /// <summary>The provider implementation.</summary>
    public required ISubtitleProvider Provider { get; init; }

    /// <summary>Whether the source needs credentials — from <see cref="SubtitleProviderAttribute"/>.</summary>
    public required bool RequiresAuth { get; init; }

    /// <summary>
    /// B04: the source's own search budget, from
    /// <see cref="SubtitleProviderAttribute.SearchTimeoutSeconds"/>; null
    /// (and zero seconds on the attribute) means the orchestrator's round
    /// default (10s, or the testing constructor's override) applies — every
    /// pre-B04 source keeps exactly its previous budget.
    /// </summary>
    public TimeSpan? SearchBudget { get; init; }

    /// <summary>
    /// Settings-form manifest matched by provider key (IPluginManifest
    /// shape reuse, tech design ① — fed by the dedicated
    /// <see cref="ISubtitleSourceManifest"/> service type so the translation
    /// plugin registry never sees the subtitle sources).
    /// </summary>
    public ISubtitleSourceManifest? Manifest { get; init; }
}

/// <summary>
/// Catalog of the compiled-in subtitle sources, built per scope from the
/// <see cref="ISubtitleProvider"/> DI registrations — the parallel of
/// the translation <c>PluginRegistry</c> (tech design ①), minus the external
/// DLL loading: subtitle sources ship compiled-in only. Scoped because the
/// providers resolve per-request settings through the scoped
/// <c>ISettingsAccess</c> (credential hot reload, ⑧).
/// <para>
/// Source-key matching against the registered
/// <see cref="ISubtitleSourceManifest"/> instances is case-insensitive,
/// mirroring the translation registry's dictionary semantics.
/// </para>
/// </summary>
public sealed class SubtitleProviderRegistry
{
    private readonly List<RegisteredSubtitleProvider> _providers;
    private readonly Dictionary<string, RegisteredSubtitleProvider> _byProvider;

    public SubtitleProviderRegistry(
        IEnumerable<ISubtitleProvider> providers,
        IEnumerable<ISubtitleSourceManifest> manifests,
        ILogger<SubtitleProviderRegistry> logger)
    {
        _providers = [];
        _byProvider = new Dictionary<string, RegisteredSubtitleProvider>(StringComparer.OrdinalIgnoreCase);

        // First manifest per key wins, as with duplicate provider classes
        // below — registration order defines precedence deterministically.
        var manifestByProvider = new Dictionary<string, ISubtitleSourceManifest>(StringComparer.OrdinalIgnoreCase);
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

            // B04 settings gate: a source that declares itself settings-gated
            // joins the registry only while its settings are complete — the
            // unconfigured whisper source neither lists nor queries. The
            // registry is built per scope, so the gate re-evaluates on the
            // next request after the missing setting is saved (no restart).
            // A gate crash reads as "not configured" — never a dead registry.
            if (provider is ISettingsGatedSubtitleProvider gated && !IsConfigured(gated))
            {
                logger.LogDebug(
                    "Subtitle provider {Provider} is not fully configured; it stays out of the registry until its settings complete.",
                    name);
                continue;
            }

            manifestByProvider.TryGetValue(name, out var manifest);
            var entry = new RegisteredSubtitleProvider
            {
                Provider = provider,
                RequiresAuth = attribute?.RequiresAuth ?? false,
                SearchBudget = attribute is { SearchTimeoutSeconds: > 0 }
                    ? TimeSpan.FromSeconds(attribute.SearchTimeoutSeconds)
                    : null,
                Manifest = manifest
            };

            _providers.Add(entry);
            _byProvider[name] = entry;
        }

        // Registered manifests without a matching subtitle provider indicate
        // a typo'd source key — surfaced at debug level so they stay findable
        // without spamming production logs.
        foreach (var unmatched in manifestByProvider.Keys.Where(key => !_byProvider.ContainsKey(key)))
        {
            logger.LogDebug(
                "Subtitle source manifest {Provider} has no matching subtitle provider.",
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

    /// <summary>
    /// B04 gate evaluation inside the synchronous constructor: one blocking
    /// wait on the provider's own async check (the BuildSourceHandler
    /// precedent of a one-time synchronous settings read). Exceptions read
    /// as "not configured" — a settings hiccup must never break the registry.
    /// </summary>
    private static bool IsConfigured(ISettingsGatedSubtitleProvider gated)
    {
        try
        {
            return gated.IsConfiguredAsync(CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return false;
        }
    }
}
