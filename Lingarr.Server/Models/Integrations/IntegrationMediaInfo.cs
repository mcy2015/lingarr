using System.Text.Json.Serialization;

namespace Lingarr.Server.Models.Integrations;

/// <summary>
/// The mediaInfo block of a *arr file resource (PRD card A).
/// <see cref="AudioLanguages"/> is the slash-joined ffprobe truth string
/// ("english / chi" — empty tags already filtered out by *arr), the
/// highest-trust audio source in the card A chain:
/// mediaInfo.audioLanguages &gt; languages aggregate &gt; local ffprobe.
/// <para>
/// The remaining members (media-file detail batch) are the same ffprobe
/// facts Sonarr/Radarr render in their file info: codecs and channel
/// count as served, the pixel resolution string and the runTime —
/// Radarr's "1:57:00" (h:mm:ss) and Sonarr's "43:12" (mm:ss) both parse
/// largest-unit-first.
/// </para>
/// </summary>
public class IntegrationMediaInfo
{
    [JsonPropertyName("audioLanguages")]
    public string? AudioLanguages { get; set; }

    [JsonPropertyName("videoCodec")]
    public string? VideoCodec { get; set; }

    [JsonPropertyName("audioCodec")]
    public string? AudioCodec { get; set; }

    /// <summary>Channel count as *arr serves it — a JSON number (5.1, 2.0).</summary>
    [JsonPropertyName("audioChannels")]
    public double? AudioChannels { get; set; }

    [JsonPropertyName("resolution")]
    public string? Resolution { get; set; }

    [JsonPropertyName("runTime")]
    public string? RunTime { get; set; }

    /// <summary>
    /// <see cref="RunTime"/> folded into whole minutes. *arr serves the
    /// string largest-unit-first (Radarr "1:57:00" h:mm:ss, Sonarr episodes
    /// "43:12" mm:ss), so progressive ×60 accumulation covers both; null
    /// when absent or unparseable.
    /// </summary>
    public int? RuntimeMinutes
    {
        get
        {
            if (string.IsNullOrWhiteSpace(RunTime)) return null;
            var parts = RunTime.Split(':');
            if (parts.Length is < 2 or > 3) return null;
            var seconds = 0d;
            foreach (var part in parts)
            {
                if (!double.TryParse(part.Trim(), out var value)) return null;
                seconds = seconds * 60 + value;
            }
            return (int)Math.Round(seconds / 60);
        }
    }
}
