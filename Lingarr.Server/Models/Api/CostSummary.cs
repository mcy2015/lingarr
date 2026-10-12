namespace Lingarr.Server.Models.Api;

/// <summary>
/// Monthly translation cost rollup (A5): per-provider usage joined with the
/// provider rate settings. A null cost on a row (or on the total) means that
/// provider has usage but no rate configured — priced as unknown, not free.
/// </summary>
public class CostSummary
{
    /// <summary>Month covered, "yyyy-MM" in the instance timezone.</summary>
    public required string Month { get; init; }

    public double? TotalCost { get; init; }

    /// <summary>True when every provider with recorded usage has a rate.</summary>
    public bool RatesConfigured { get; init; }

    public required List<ProviderCost> Providers { get; init; }
}

public class ProviderCost
{
    public required string Provider { get; init; }
    public int Requests { get; init; }
    public long Characters { get; init; }
    public double? Cost { get; init; }
}
