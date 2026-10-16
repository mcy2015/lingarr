using Lingarr.Contracts.Interfaces.Plugins;

namespace Lingarr.Server.Modules.SubtitleSearch.Abstractions;

/// <summary>
/// Settings-form manifest of one subtitle source (tech design ①): the same
/// shape contract as the translation <c>IPluginManifest</c>, but deliberately
/// a distinct DI service type. The translation <c>PluginRegistry</c> consumes
/// <c>IEnumerable&lt;IPluginManifest&gt;</c> to render the translation plugin
/// cards — registering subtitle manifests under that type would leak the
/// seven sources onto the translation settings page. Deriving from the
/// contract keeps every rendering field (settings fields, secrets, defaults)
/// reusable while the feeds stay disjoint.
/// </summary>
public interface ISubtitleSourceManifest : IPluginManifest
{
}
