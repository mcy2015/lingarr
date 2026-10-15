using System.ComponentModel.DataAnnotations;

namespace Lingarr.Core.Entities;

/// <summary>
/// Daily per-source usage counters (M0009). Rows are upserted in the same
/// transaction after every Search/Download (⑩):
/// INSERT ... ON CONFLICT(date, provider) DO UPDATE searches/downloads = +1.
/// <see cref="QuotaLimit"/> carries the source constant for the front-end
/// waterline (OpenSubtitles 40/day, 0 = no published limit) — display only,
/// never a circuit breaker (PM decision, ⑩).
/// </summary>
public class ProviderQuotaUsage
{
    /// <summary>Usage day (UTC) — part of the composite PK.</summary>
    [Key]
    public required DateOnly Date { get; set; }

    /// <summary>Source key — part of the composite PK.</summary>
    [Key]
    public required string Provider { get; set; }

    /// <summary>Search requests this day.</summary>
    public int Searches { get; set; }

    /// <summary>Download requests this day.</summary>
    public int Downloads { get; set; }

    /// <summary>Source's published daily limit (display only); 0 = none published.</summary>
    public int QuotaLimit { get; set; }

    /// <summary>Remaining quota as reported by the source; null when the source does not report it.</summary>
    public int? QuotaRemaining { get; set; }
}
