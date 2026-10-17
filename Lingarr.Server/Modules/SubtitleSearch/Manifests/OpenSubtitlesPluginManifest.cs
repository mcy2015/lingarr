using Lingarr.Contracts.Plugins;
using Lingarr.Server.Modules.SubtitleSearch.Abstractions;

namespace Lingarr.Server.Modules.SubtitleSearch.Manifests;

/// <summary>
/// Settings form of the OpenSubtitles source (subtitle-search M2): API key
/// (secret, required) plus the optional base-URL override used by the
/// fake-source E2E round (PRD AC25).
/// </summary>
public sealed class OpenSubtitlesPluginManifest : ISubtitleSourceManifest
{
    public string Provider => "opensubtitles";

    public string DisplayName => "OpenSubtitles";

    public string? Description =>
        "Official OpenSubtitles.com API. The largest catalogue and the only source with exact-file hash matches. "
        + "Free API keys are limited to 40 downloads per day (displayed as a waterline, never enforced). "
        + "Optional proxy: subtitle_opensubtitles_proxy_url overrides the global subtitle_proxy_url for this source; proxy changes apply after a restart.";

    public IReadOnlyList<PluginSettingField> Settings { get; } =
    [
        new()
        {
            Key = SubtitleSourceSettingKeys.OpenSubtitles.ApiKey,
            Label = "API key",
            Type = PluginSettingType.Secret,
            Required = true,
            Description = "OpenSubtitles.com API key. Stored encrypted."
        },
        new()
        {
            Key = SubtitleSourceSettingKeys.OpenSubtitles.BaseUrl,
            Label = "API base URL",
            Type = PluginSettingType.Url,
            Required = false,
            Default = "https://api.opensubtitles.com/api/v1",
            Description = "Override only for tests against a local fake source."
        }
    ];
}
