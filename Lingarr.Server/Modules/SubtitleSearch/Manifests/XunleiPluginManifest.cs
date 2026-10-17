using Lingarr.Contracts.Plugins;
using Lingarr.Server.Modules.SubtitleSearch.Abstractions;

namespace Lingarr.Server.Modules.SubtitleSearch.Manifests;

/// <summary>
/// Settings form of the xunlei source (batch R2, 2026-10): no credentials,
/// optional base-URL and proxy overrides. The production endpoint is plain
/// HTTP on port 8000 — the protocol reality upstream (no TLS exists), which
/// is why the URL override carries no HTTPS-only expectation.
/// </summary>
public sealed class XunleiPluginManifest : ISubtitleSourceManifest
{
    public string Provider => "xunlei";

    public string DisplayName => "Xunlei";

    public string? Description =>
        "Xunlei (迅雷) subtitle endpoint: no authentication, hash-only matching, simplified/traditional Chinese "
        + "and bilingual entries hit zh directly without translation. "
        + "Base URL points at scripts/dev/fake-subtitle-sources.py for the E2E fake-source round. "
        + "Optional proxy: subtitle_xunlei_proxy_url overrides the global subtitle_proxy_url for this source; "
        + "proxy changes apply after a restart.";

    public IReadOnlyList<PluginSettingField> Settings { get; } =
    [
        new()
        {
            Key = SubtitleSourceSettingKeys.Xunlei.BaseUrl,
            Label = "API base URL",
            Type = PluginSettingType.Url,
            Required = false,
            Default = "http://sub.xmp.xunlei.com:8000",
            Description = "Override only for tests against a local fake source."
        },
        new()
        {
            Key = SubtitleSourceSettingKeys.Xunlei.ProxyUrl,
            Label = "Proxy URL",
            Type = PluginSettingType.Url,
            Required = false,
            Description = "Optional per-source proxy (http://host:port) overriding the global subtitle proxy; applies after a restart."
        }
    ];
}
