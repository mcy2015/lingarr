namespace Lingarr.Server.Models.Api;

/// <summary>
/// Import cost estimate result. Cost is null when the default provider has
/// no rate configured — the same "unknown, not free" semantics as the
/// library estimate (B2).
/// </summary>
public class ImportEstimateResult
{
    public double? Cost { get; init; }
    public required string Currency { get; init; }
}
