using Lingarr.Contracts.Interfaces.Plugins;

namespace Lingarr.Server.Modules.Translation.Plugins;

public sealed class RegisteredPlugin
{
    public required IPluginManifest Manifest { get; init; }
    public required bool IsBuiltIn { get; init; }
    public string? SourceFile { get; init; }
}
