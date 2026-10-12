namespace Lingarr.Server.Models;

/// <summary>
/// One row of the provider rate setting (JSON array): USD per 1M source
/// characters for the provider, used by the cost rollups (A5).
/// </summary>
public class ProviderRate
{
    public string Provider { get; set; } = "";
    public decimal RatePerMillion { get; set; }
}
