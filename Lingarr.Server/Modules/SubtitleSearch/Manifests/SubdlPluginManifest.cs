using Lingarr.Contracts.Plugins;
using Lingarr.Server.Modules.SubtitleSearch.Abstractions;

namespace Lingarr.Server.Modules.SubtitleSearch.Manifests;

/// <summary>
/// Settings form of the SubDL source (subtitle-search M2): an optional API
/// key (the public API grants anonymous access at lower limits) plus the
/// optional base-URL override.
/// </summary>
public sealed class SubdlPluginManifest : ISubtitleSourceManifest
{
    public string Provider => "subdl";

    public string DisplayName => "SubDL";

    public string? Description =>
        "Free movie-focused subtitle source. No key required; an optional API key (docs.subdl.com) raises the rate limits. "
        + "Stored encrypted. Optional proxy: subtitle_subdl_proxy_url overrides the global subtitle_proxy_url for this source; "
        + "proxy changes apply after a restart.";

    public IReadOnlyList<PluginSettingField> Settings { get; } =
    [
        new()
        {
            Key = SubtitleSourceSettingKeys.Subdl.ApiKey,
            Label = "API key (optional)",
            Type = PluginSettingType.Secret,
            Required = false,
            Description = "SubDL API key. Anonymous access works without one."
        },
        new()
        {
            Key = SubtitleSourceSettingKeys.Subdl.BaseUrl,
            Label = "API base URL",
            Type = PluginSettingType.Url,
            Required = false,
            Default = "https://api.subdl.com",
            Description = "Override only for tests against a local fake source."
        }
    ];
}
