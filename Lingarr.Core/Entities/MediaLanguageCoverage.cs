using System.Text.Json.Serialization;

namespace Lingarr.Core.Entities;

/// <summary>
/// Per-language subtitle coverage of one media item, maintained by the
/// statistics scan. For shows Count is the number of episodes with a
/// subtitle in that language (Bytes the total file size); for movies
/// Count is 0 or 1.
/// <para>
/// B07 dimension split: a file whose only caption is "forced" (a
/// foreign-audio-only track) never counts as full coverage — it lands in
/// <see cref="ForcedCount"/>/<see cref="ForcedBytes"/> instead. The two
/// dimensions are presented separately (the forced badge) and a forced-only
/// language still counts as missing for the full search. The forced fields
/// are omitted while zero so a library without forced files serializes
/// byte-identically to the pre-B07 snapshot (no whole-library rewrite on
/// the next scan).
/// <para>
/// B04 adds the embedded dimension (<see cref="EmbeddedCount"/>) alongside
/// forced — same badge shape, different semantics: embedded counts toward
/// the need rule while the embedded_track_counts_as_coverage setting is on.
/// The two dimensions never feed each other.
/// </para>
/// </summary>
public class MediaLanguageCoverage
{
    [JsonPropertyName("count")]
    public int Count { get; set; }

    [JsonPropertyName("bytes")]
    public long Bytes { get; set; }

    [JsonPropertyName("forcedCount")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int ForcedCount { get; set; }

    [JsonPropertyName("forcedBytes")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public long ForcedBytes { get; set; }

    /// <summary>
    /// B04 embedded dimension: a parallel coverage dimension alongside the
    /// B07 forced split — the number of media units (0/1 for movies,
    /// episodes for shows) whose container carries a recognized embedded
    /// subtitle track in this language. Written ONLY while the
    /// embedded_track_counts_as_coverage setting is on; a library that never
    /// enables it (or one whose tracks are absent) serializes byte-identically
    /// to the pre-B04 snapshot. Unlike the forced dimension, an
    /// embedded-covered target language DOES satisfy the need rule while the
    /// setting is on — that is the entire point of the switch — so the need
    /// verdicts follow the setting, never the JSON alone.
    /// </summary>
    [JsonPropertyName("embeddedCount")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int EmbeddedCount { get; set; }
}
