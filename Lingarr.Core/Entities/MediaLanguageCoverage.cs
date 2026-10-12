using System.Text.Json.Serialization;

namespace Lingarr.Core.Entities;

/// <summary>
/// Per-language subtitle coverage of one media item, maintained by the
/// statistics scan. For shows Count is the number of episodes with a
/// subtitle in that language (Bytes the total file size); for movies
/// Count is 0 or 1.
/// </summary>
public class MediaLanguageCoverage
{
    [JsonPropertyName("count")]
    public int Count { get; set; }

    [JsonPropertyName("bytes")]
    public long Bytes { get; set; }
}
