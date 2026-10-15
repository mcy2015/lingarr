using System.ComponentModel.DataAnnotations;
using Lingarr.Core.Enum;

namespace Lingarr.Core.Entities;

/// <summary>
/// One search query against one source (M0009 telemetry). A query that
/// returns nothing still lands a row with ResultCount = 0 — that is what
/// makes the per-source hit-rate KPI honest (no silent success bias).
/// </summary>
public class SubtitleSearchSession
{
    [Key]
    public int Id { get; set; }

    /// <summary>Searched media type — Movie or Episode (polymorphic, no FK).</summary>
    public required MediaType MediaType { get; set; }

    /// <summary>Id of the searched movie / episode.</summary>
    public required int MediaId { get; set; }

    /// <summary>Source key that was queried.</summary>
    public required string Provider { get; set; }

    /// <summary>Normalized language code the query asked for.</summary>
    public required string QueryLanguage { get; set; }

    /// <summary>Candidate count the source returned (0 rows are logged too).</summary>
    public int ResultCount { get; set; }

    /// <summary>Whether the round ended with a usable candidate for the target language.</summary>
    public bool HitTarget { get; set; }

    /// <summary>Query timestamp — append-only table, no updated_at.</summary>
    public required DateTimeOffset CreatedAt { get; set; }
}
