using Lingarr.Core.Configuration;
using Lingarr.Core.Data;
using Lingarr.Core.Entities;
using Lingarr.Server.Models;
using Lingarr.Server.Models.Api;
using Microsoft.EntityFrameworkCore;
using Lingarr.Server.Modules.Settings;

namespace Lingarr.Server.Modules.Translation;

/// <summary>
/// Usage × pricing rollups (A5). Characters are recorded per request at
/// completion; rates live in settings as provider -> USD per 1M characters.
/// A provider with usage but no rate prices as null (unknown), never as $0.
/// </summary>
public class CostService : ICostService
{
    private readonly LingarrDbContext _dbContext;
    private readonly ISettingService _settingService;

    private static (int Year, int Month) Today => (
        TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, TimeZoneConfiguration.Current).Year,
        TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, TimeZoneConfiguration.Current).Month);

    public CostService(LingarrDbContext dbContext, ISettingService settingService)
    {
        _dbContext = dbContext;
        _settingService = settingService;
    }

    /// <inheritdoc />
    public async Task<CostSummary> GetCostSummary(int? year, int? month)
    {
        var (y, m) = year is int yy && month is int mm ? (yy, mm) : Today;
        var monthStartLocal = new DateTime(y, m, 1);
        var monthStart = new DateTimeOffset(monthStartLocal,
            TimeZoneConfiguration.Current.GetUtcOffset(monthStartLocal));
        var monthEnd = monthStart.AddMonths(1);

        var rates = await GetRates();
        var usage = await _dbContext.TranslationRequests
            .Where(r => r.CompletedAt >= monthStart
                && r.CompletedAt < monthEnd
                && r.ServiceType != null
                && r.CharactersTranslated != null)
            .GroupBy(r => r.ServiceType!)
            .Select(g => new
            {
                Provider = g.Key,
                Requests = g.Count(),
                Characters = g.Sum(x => x.CharactersTranslated!.Value)
            })
            .ToListAsync();

        var providers = usage
            .OrderByDescending(u => u.Characters)
            .Select(u => new ProviderCost
            {
                Provider = u.Provider,
                Requests = u.Requests,
                Characters = u.Characters,
                // A missing rate entry prices as unknown (null), a configured
                // $0 rate prices as genuinely free — keep them distinct.
                Cost = Price(u.Characters,
                    rates.TryGetValue(u.Provider, out var rate) ? rate : null)
            })
            .ToList();

        var unpriced = providers.Any(p => p.Cost is null);
        return new CostSummary
        {
            Month = $"{y:D4}-{m:D2}",
            Providers = providers,
            RatesConfigured = !unpriced,
            TotalCost = unpriced ? null : providers.Sum(p => p.Cost!.Value)
        };
    }

    /// <inheritdoc />
    public async Task<double?> GetProviderMonthlyCostAsync(string provider)
    {
        var summary = await GetCostSummary(null, null);
        return summary.Providers.FirstOrDefault(x => x.Provider == provider)?.Cost;
    }

    /// <summary>
    /// Current-month completed request count via the same aggregation
    /// <see cref="GetCostSummary"/> prices (CompletedAt inside the local
    /// month, ServiceType set, CharactersTranslated recorded) — the service
    /// card's monthlyRequests rides the cost rollup's exact criteria.
    /// Unused providers report 0, never null.
    /// </summary>
    public async Task<int> GetProviderMonthlyRequestsAsync(string provider)
    {
        var summary = await GetCostSummary(null, null);
        return summary.Providers.FirstOrDefault(x => x.Provider == provider)?.Requests ?? 0;
    }

    /// <inheritdoc />
    public async Task<double?> GetCurrentMonthTotalCostAsync()
    {
        var summary = await GetCostSummary(null, null);
        return summary.TotalCost;
    }

    /// <inheritdoc />
    public async Task<Dictionary<string, decimal>> GetRates()
    {
        var entries = await _settingService.GetSettingAsJson<ProviderRate>(
            SettingKeys.Cost.ProviderRates) ?? new List<ProviderRate>();
        return entries
            .Where(e => !string.IsNullOrEmpty(e.Provider))
            .GroupBy(e => e.Provider, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Last().RatePerMillion, StringComparer.OrdinalIgnoreCase);
    }


    /// <summary>USD for a character count at a per-1M rate; null without a rate.</summary>
    internal static double? Price(long characters, decimal? ratePerMillion) =>
        ratePerMillion is null ? null : (double)ratePerMillion * characters / 1_000_000;
}
