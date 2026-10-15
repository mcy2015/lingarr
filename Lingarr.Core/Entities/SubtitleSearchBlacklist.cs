using System.ComponentModel.DataAnnotations;

namespace Lingarr.Core.Entities;

/// <summary>
/// Provider-level ban observation (tech design ⑧): records cases like an
/// assrt account suspended for scraping. Blacklist rows only inform the
/// source card — they never disable a source automatically. Table created by
/// M0008; written by hand/operator tooling, read by the providers endpoint.
/// </summary>
public class SubtitleSearchBlacklist
{
    [Key]
    public int Id { get; set; }

    /// <summary>Source key the observation is about ("assrt"…).</summary>
    public required string Provider { get; set; }

    /// <summary>Human-readable observation ("account suspended for scraping").</summary>
    public required string Reason { get; set; }

    /// <summary>When the observation lapses; null = keep showing it.</summary>
    public DateTimeOffset? ExpiresAt { get; set; }
}
