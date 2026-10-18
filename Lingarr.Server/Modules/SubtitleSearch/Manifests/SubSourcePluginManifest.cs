using Lingarr.Contracts.Plugins;
using Lingarr.Server.Modules.SubtitleSearch.Abstractions;

namespace Lingarr.Server.Modules.SubtitleSearch.Manifests;

/// <summary>
/// Settings form of the SubSource source: API key (secret, required since
/// the 2026-10 v1 API migration — requests without a key answer 401) plus
/// the optional base-URL override used by the fake-source E2E round
/// (PRD AC25).
/// </summary>
public sealed class SubSourcePluginManifest : ISubtitleSourceManifest
{
    public string Provider => "subsource";

    public string DisplayName => "SubSource";

    public string? Description =>
        "SubSource v1 API (api.subsource.net/api/v1). Movie and episode coverage across many languages; an API key "
        + "is required on every request (the keyless legacy API was retired upstream in 2026-10). "
        + "Optional proxy: subtitle_subsource_proxy_url overrides the global subtitle_proxy_url for this source; proxy changes apply after a restart.";

    public IReadOnlyList<PluginSettingField> Settings { get; } =
    [
        new()
        {
            Key = SubtitleSourceSettingKeys.SubSource.ApiKey,
            Label = "API key",
            Type = PluginSettingType.Secret,
            Required = true,
            Description = "SubSource API key. Stored encrypted."
        },
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
