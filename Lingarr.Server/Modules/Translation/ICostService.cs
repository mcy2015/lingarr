using Lingarr.Server.Models.Api;

namespace Lingarr.Server.Modules.Translation;

public interface ICostService
{
    /// <summary>
    /// Aggregates completed requests' translated characters by provider for a
    /// month (current month when null) and prices them via the rate settings.
    /// </summary>
    Task<CostSummary> GetCostSummary(int? year, int? month);

    /// <summary>Current-month cost for one provider; null when unpriced or unused.</summary>
    Task<double?> GetProviderMonthlyCostAsync(string provider);

    /// <summary>Current-month total cost; null when any used provider is unpriced.</summary>
    Task<double?> GetCurrentMonthTotalCostAsync();

    /// <summary>USD per 1M source characters per provider, from settings.</summary>
    Task<Dictionary<string, decimal>> GetRates();
}
