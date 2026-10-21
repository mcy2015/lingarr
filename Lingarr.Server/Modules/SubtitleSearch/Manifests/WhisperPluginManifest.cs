using Lingarr.Contracts.Plugins;
using Lingarr.Core.Configuration;
using Lingarr.Server.Modules.SubtitleSearch.Abstractions;

namespace Lingarr.Server.Modules.SubtitleSearch.Manifests;

/// <summary>
/// Settings manifest of the whisper transcription source (B04). The full
/// wiring form (endpoint / model / key reveal / connection test) lives on
/// the dedicated Whisper settings card; this manifest exists so the source
/// row on the subtitle-sources card renders a real display name and an
/// honest credentials badge — the API key is declared as its secret field,
/// and the source itself is registry-gated until endpoint+model+key are
/// all present.
/// </summary>
public sealed class WhisperPluginManifest : ISubtitleSourceManifest
{
    public string Provider => "whisper";

    public string DisplayName => "whisper";

    public string? Description =>
        "Whisper transcription fallback: when regular sources miss, a 120-second clip of the "
        + "audio track is transcribed through an OpenAI-compatible endpoint and offered as a "
        + "candidate in the audio's language. Appears in the source chain only after endpoint, "
        + "model and key are configured on the Whisper settings card.";

    public IReadOnlyList<PluginSettingField> Settings { get; } =
    [
        new()
        {
            Key = SettingKeys.SubtitleSearch.Whisper.ApiKey,
            Label = "API key",
            Type = PluginSettingType.Secret,
            Required = true,
            Description = "Bearer credential of the transcription endpoint. Stored encrypted."
        }
    ];
}
