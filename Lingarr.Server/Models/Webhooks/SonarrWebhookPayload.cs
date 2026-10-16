using System.Text.Json.Serialization;

namespace Lingarr.Server.Models.Webhooks;

public class SonarrWebhookPayload
{
    /// <summary>
    /// Sonarr v3 event name ("Download", "Grab", "Rename", "Test"…). The
    /// subtitle-search boost is gated on Download — only that event means
    /// "a file just landed". Missing/blank keeps the boost, mirroring the
    /// Radarr payload's legacy-sender decision.
    /// </summary>
    [JsonPropertyName("eventType")]
    public string? EventType { get; set; }

    [JsonPropertyName("series")]
    public SonarrWebhookSeries? Series { get; set; }

    [JsonPropertyName("episodes")]
    public List<SonarrWebhookEpisode>? Episodes { get; set; }
}

public class SonarrWebhookSeries
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;
}

public class SonarrWebhookEpisode
{
    [JsonPropertyName("id")]
    public int Id { get; set; }
}
