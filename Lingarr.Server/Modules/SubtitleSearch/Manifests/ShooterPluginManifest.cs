using Lingarr.Contracts.Plugins;
using Lingarr.Server.Modules.SubtitleSearch.Abstractions;

namespace Lingarr.Server.Modules.SubtitleSearch.Manifests;

/// <summary>Settings form of the shooter source (subtitle-search M2): no credentials, optional base-URL override.</summary>
public sealed class ShooterPluginManifest : ISubtitleSourceManifest
{
    public string Provider => "shooter";

    public string DisplayName => "shooter";

    public string? Description =>
        "Chinese subtitle fallback matching by exact file hash (segmented MD5). Best-effort service with no SLA; "
        + "the source can be disabled independently when it misbehaves. "
        + "Optional proxy: subtitle_shooter_proxy_url overrides the global subtitle_proxy_url for this source; proxy changes apply after a restart.";

    public IReadOnlyList<PluginSettingField> Settings { get; } =
    [
        new()
        {
            Key = SubtitleSourceSettingKeys.Shooter.BaseUrl,
            Label = "API base URL",
            Type = PluginSettingType.Url,
            Required = false,
            Default = "https://shooter.cn",
            Description = "Override only for tests against a local fake source."
        }
    ];
}
