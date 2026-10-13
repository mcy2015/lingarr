using Lingarr.Core.Configuration;
using Lingarr.Server.Interfaces.Services;
using Lingarr.Server.Models;
using Lingarr.Server.Models.Api;
using Lingarr.Server.Services.Translation;

namespace Lingarr.Server.Services;

/// <summary>
/// Import cost estimate (PM analysis P0): sums the queued files' estimated
/// translatable characters and prices them with the shared cost-service
/// rates, mirroring MediaLibraryService.Estimate so both surfaces agree.
/// </summary>
public class ImportEstimateService : IImportEstimateService
{
    private const string Currency = "USD";

    private readonly ISettingService _settingService;
    private readonly ICostService _costService;

    public ImportEstimateService(ISettingService settingService, ICostService costService)
    {
        _settingService = settingService;
        _costService = costService;
    }

    /// <inheritdoc />
    public async Task<ImportEstimateResult> Estimate(List<ImportEstimateFile>? files)
    {
        // Defensive: a null file list (malformed request body) prices like
        // an empty queue instead of throwing — the UI treats both as "0".
        var characters = (long)(files ?? []).Sum(file => Math.Max(0, file.Characters));

        // The default (primary) provider prices the estimate; the parse
        // handles both the plain-string and JSON-array service_type forms.
        var defaultProvider = TranslationServices.Parse(
            await _settingService.GetSetting(SettingKeys.Translation.ServiceType))[0];

        // A missing rate means "unknown", not free — TryGetValue keeps that
        // distinct from a configured $0 rate (same as the library estimate).
        var rates = await _costService.GetRates();
        var cost = rates.TryGetValue(defaultProvider, out var rate)
            ? CostService.Price(characters, rate)
            : null;

        return new ImportEstimateResult
        {
            Cost = cost,
            Currency = Currency
        };
    }
}
