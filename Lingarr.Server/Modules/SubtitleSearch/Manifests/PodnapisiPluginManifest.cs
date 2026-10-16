using Lingarr.Contracts.Plugins;
using Lingarr.Server.Modules.SubtitleSearch.Abstractions;

namespace Lingarr.Server.Modules.SubtitleSearch.Manifests;

/// <summary>Settings form of the Podnapisi source (subtitle-search M2): no credentials, optional base-URL override.</summary>
public sealed class PodnapisiPluginManifest : ISubtitleSourceManifest
{
    public string Provider => "podnapisi";

    public string DisplayName => "Podnapisi";

    public string? Description =>
        "Long-standing free subtitle index with an XML search interface; movies and episodes, many languages.";

    public IReadOnlyList<PluginSettingField> Settings { get; } =
    [
        new()
        {
            Key = SubtitleSourceSettingKeys.Podnapisi.BaseUrl,
            Label = "API base URL",
            Type = PluginSettingType.Url,
            Required = false,
            Default = "https://www.podnapisi.net",
            Description = "Override only for tests against a local fake source."
        }
    ];
}
