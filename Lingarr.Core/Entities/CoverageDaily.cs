using System.ComponentModel.DataAnnotations;
using Lingarr.Core.Enum;

namespace Lingarr.Core.Entities;

/// <summary>
/// Daily subtitle-coverage snapshot per media type and language (M0009).
/// Maintained exclusively by incremental upserts at day-end — never a full
/// library rescan — which is what keeps the statistics round O(changed
/// media) instead of O(library).
/// </summary>
public class CoverageDaily
{
    /// <summary>Snapshot day (UTC) — part of the composite PK.</summary>
    [Key]
    public required DateOnly Date { get; set; }

    /// <summary>Media type the row counts — Movie or Episode.</summary>
    [Key]
    public required MediaType MediaType { get; set; }

    /// <summary>Normalized language code — part of the composite PK.</summary>
    [Key]
    public required string Language { get; set; }

    /// <summary>Media items with a subtitle in the language that day.</summary>
    public int Covered { get; set; }

    /// <summary>Media items in scope that day.</summary>
    public int Total { get; set; }
}
