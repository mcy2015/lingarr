using Lingarr.Contracts.Plugins;
using Lingarr.Server.Modules.SubtitleSearch.Abstractions;

namespace Lingarr.Server.Modules.SubtitleSearch.Manifests;

/// <summary>Settings form of the Gestdown source (subtitle-search M2): no credentials, optional base-URL override.</summary>
public sealed class GestdownPluginManifest : ISubtitleSourceManifest
{
    public string Provider => "gestdown";

    public string DisplayName => "Gestdown";

    public string? Description =>
        "Free episode-focused English source mirroring the Addic7ed catalogue through an open API — no account needed. "
        + "Optional proxy: subtitle_gestdown_proxy_url overrides the global subtitle_proxy_url for this source; proxy changes apply after a restart.";

    public IReadOnlyList<PluginSettingField> Settings { get; } =
    [
        new()
        {
            Key = SubtitleSourceSettingKeys.Gestdown.BaseUrl,
            Label = "API base URL",
            Type = PluginSettingType.Url,
            Required = false,
            Default = "https://api.gestdown.info",
            Description = "Override only for tests against a local fake source."
        }
    ];
}
