using Lingarr.Contracts.Plugins;
using Lingarr.Server.Modules.SubtitleSearch.Abstractions;

namespace Lingarr.Server.Modules.SubtitleSearch.Manifests;

/// <summary>Settings form of the SubSource source (subtitle-search M2): no credentials, optional base-URL override.</summary>
public sealed class SubSourcePluginManifest : ISubtitleSourceManifest
{
    public string Provider => "subsource";

    public string DisplayName => "SubSource";

    public string? Description =>
        "Free subtitle source with no credentials. Movie and episode coverage in English plus multi-language supplements.";

    public IReadOnlyList<PluginSettingField> Settings { get; } =
    [
        new()
        {
            Key = SubtitleSourceSettingKeys.SubSource.BaseUrl,
            Label = "API base URL",
            Type = PluginSettingType.Url,
            Required = false,
            Default = "https://api.subsource.net",
            Description = "Override only for tests against a local fake source."
        }
    ];
}
