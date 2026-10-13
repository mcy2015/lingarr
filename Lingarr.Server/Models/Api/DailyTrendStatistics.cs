namespace Lingarr.Server.Models.Api;

/// <summary>
/// Daily output/cost series for the overview trend card (PM analysis P1-1).
/// Aggregated from completed translation requests priced via the provider
/// rate table; costs are null while any usage in the window is unpriced
/// (same "unknown, never $0" rule as <see cref="CostSummary"/>).
/// </summary>
public class DailyTrendStatistics
{
    /// <summary>Contiguous day buckets, oldest first, zero-filled for days without activity.</summary>
    public List<DailyTrendPoint> Days { get; set; } = [];

    /// <summary>Write-backs (completed requests with an output file) over the window.</summary>
    public int TotalWritten { get; set; }

    /// <summary>Summed provider cost over the window; null while any usage is unpriced.</summary>
    public double? TotalCost { get; set; }

    /// <summary>False when at least one provider with usage has no rate configured.</summary>
    public bool RatesConfigured { get; set; } = true;
}

/// <summary>One day of the trend series.</summary>
public class DailyTrendPoint
{
    public DateOnly Date { get; set; }

    /// <summary>Requests completed with a written subtitle file on this day.</summary>
    public int WrittenCount { get; set; }

    /// <summary>Priced character usage of this day; null while any usage is unpriced.</summary>
    public double? Cost { get; set; }
}
