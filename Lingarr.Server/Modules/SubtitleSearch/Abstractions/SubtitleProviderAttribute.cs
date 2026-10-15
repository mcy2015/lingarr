namespace Lingarr.Server.Modules.SubtitleSearch.Abstractions;

/// <summary>
/// Declares a compiled-in subtitle source (tech design ②). The registry
/// reads the attribute for the settings-form metadata (source key and
/// whether the source needs credentials) — the parallel of
/// <c>PluginProviderAttribute</c> for translation plugins.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
public sealed class SubtitleProviderAttribute : Attribute
{
    /// <summary>Stable lowercase source key; must match the ISubtitleProvider.Provider value.</summary>
    public string Name { get; }

    /// <summary>Whether the source needs user-supplied credentials (API key/token) in settings.</summary>
    public bool RequiresAuth { get; }

    public SubtitleProviderAttribute(string name, bool requiresAuth = false)
    {
        Name = name;
        RequiresAuth = requiresAuth;
    }
}
