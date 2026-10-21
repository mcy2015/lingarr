namespace Lingarr.Server.Modules.SubtitleSearch.Abstractions;

/// <summary>
/// Declares a compiled-in subtitle source (tech design ②). The registry
/// reads the attribute for the settings-form metadata (source key and
/// whether the source needs credentials) — the parallel of
/// <c>PluginProviderAttribute</c> for translation plugins.
/// <para>
/// B04 adds the optional per-source search budget: sources whose search
/// legitimately takes longer than the design-② 10s default (whisper's
/// ffmpeg clip extraction + transcription) declare it here and the
/// orchestrator applies it INSTEAD of the round's default budget. Zero (the
/// default) keeps the round budget — every existing source stays on it.
/// </para>
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
public sealed class SubtitleProviderAttribute : Attribute
{
    /// <summary>Stable lowercase source key; must match the ISubtitleProvider.Provider value.</summary>
    public string Name { get; }

    /// <summary>Whether the source needs user-supplied credentials (API key/token) in settings.</summary>
    public bool RequiresAuth { get; }

    /// <summary>
    /// Per-source search budget in seconds; 0 = the round's default (10s).
    /// </summary>
    public int SearchTimeoutSeconds { get; }

    public SubtitleProviderAttribute(
        string name,
        bool requiresAuth = false,
        int searchTimeoutSeconds = 0)
    {
        Name = name;
        RequiresAuth = requiresAuth;
        SearchTimeoutSeconds = searchTimeoutSeconds;
    }
}
