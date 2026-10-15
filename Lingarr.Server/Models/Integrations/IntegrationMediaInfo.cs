using System.Text.Json.Serialization;

namespace Lingarr.Server.Models.Integrations;

/// <summary>
/// The mediaInfo block of a *arr file resource (PRD card A).
/// <see cref="AudioLanguages"/> is the slash-joined ffprobe truth string
/// ("english / chi" — empty tags already filtered out by *arr), the
/// highest-trust audio source in the card A chain:
/// mediaInfo.audioLanguages &gt; languages aggregate &gt; local ffprobe.
/// </summary>
public class IntegrationMediaInfo
{
    [JsonPropertyName("audioLanguages")]
    public string? AudioLanguages { get; set; }
}
