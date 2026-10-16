using System.ComponentModel.DataAnnotations;

namespace Lingarr.Core.Entities;

/// <summary>
/// Ban observation (tech design ⑧): operator rows record provider-level
/// cases like an assrt account suspended for scraping; the download pipeline
/// writes automatic per-candidate rows when fetched content proves
/// permanently unusable (unparseable archive, empty payload). Locked
/// read/write contract: the search filter side matches exactly on
/// (provider, external_id) within a 90-day window — auto rows always carry
/// <see cref="ExpiresAt"/>, operator rows may leave it null (= keep showing).
/// Table created by M0008; <see cref="ExternalId"/> added by M0010.
/// </summary>
public class SubtitleSearchBlacklist
{
    [Key]
    public int Id { get; set; }

    /// <summary>Source key the observation is about ("assrt"…).</summary>
    public required string Provider { get; set; }

    /// <summary>
    /// Source-side id of the banned subtitle entry; empty on hand-written
    /// provider-level rows (never matches a real candidate id).
    /// </summary>
    public string ExternalId { get; set; } = string.Empty;

    /// <summary>Human-readable observation ("account suspended for scraping").</summary>
    public required string Reason { get; set; }

    /// <summary>When the observation lapses; null = keep showing it.</summary>
    public DateTimeOffset? ExpiresAt { get; set; }
}
