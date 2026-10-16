using Lingarr.Contracts.Plugins;
using Lingarr.Server.Modules.SubtitleSearch.Abstractions;

namespace Lingarr.Server.Modules.SubtitleSearch.Manifests;

/// <summary>
/// Settings form of the assrt source (subtitle-search M2): token (secret,
/// required) plus the optional base-URL override.
/// </summary>
public sealed class AssrtPluginManifest : ISubtitleSourceManifest
{
    public string Provider => "assrt";

    public string DisplayName => "assrt";

    public string? Description =>
        "Chinese subtitle mainstay (assrt.net). Requires a free API token from the site. "
        + "Mind the source's usage policy — scraping-like volume gets accounts suspended. "
        + "Optional proxy: subtitle_assrt_proxy_url overrides the global subtitle_proxy_url for this source; proxy changes apply after a restart.";

    public IReadOnlyList<PluginSettingField> Settings { get; } =
    [
        new()
        {
            Key = SubtitleSourceSettingKeys.Assrt.Token,
            Label = "API token",
            Type = PluginSettingType.Secret,
            Required = true,
            Description = "assrt.net API token. Stored encrypted.",
            MinLength = 1,
            ValidationErrorMessage = "Value must not be empty"
        },
        new()
        {
            Key = SubtitleSourceSettingKeys.Assrt.BaseUrl,
            Label = "API base URL",
            Type = PluginSettingType.Url,
            Required = false,
            Default = "https://api.assrt.net/v1",
            Description = "Override only for tests against a local fake source."
        }
    ];
}
