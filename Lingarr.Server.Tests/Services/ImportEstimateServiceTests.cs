using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Lingarr.Core.Configuration;
using Lingarr.Server.Models;
using Moq;
using Xunit;
using Lingarr.Server.Modules.Settings;
using Lingarr.Server.Modules.Media;
using Lingarr.Server.Modules.Translation;

namespace Lingarr.Server.Tests.Services;

/// <summary>
/// Import cost estimate (PM analysis P0, prototype screen-04): the queued
/// files' characters priced with the shared cost-service rates — same
/// rate/pricing convention as MediaLibraryService.Estimate, with the
/// "no rate = unknown (null), not $0" semantics.
/// </summary>
public class ImportEstimateServiceTests
{
    private readonly Mock<ISettingService> _settingService = new();
    private readonly Mock<ICostService> _costService = new();

    private ImportEstimateService CreateService() => new(_settingService.Object, _costService.Object);

    private void SetupDefaultProvider(string? serviceType)
    {
        _settingService
            .Setup(settings => settings.GetSetting(SettingKeys.Translation.ServiceType))
            .ReturnsAsync(serviceType);
    }

    private void SetupRates(params (string Provider, decimal Rate)[] rates)
    {
        _costService
            .Setup(costService => costService.GetRates())
            .ReturnsAsync(new Dictionary<string, decimal>(
                rates.Select(rate => new KeyValuePair<string, decimal>(rate.Provider, rate.Rate))));
    }

    private static List<ImportEstimateFile> Files(params (string Name, long Characters)[] files) =>
        [.. files.Select(file => new ImportEstimateFile { Name = file.Name, Characters = file.Characters })];

    [Fact]
    public async Task Estimate_WithRate_PricesSummedCharacters()
    {
        SetupDefaultProvider("openai");
        SetupRates(("openai", 2.0m));

        var result = await CreateService().Estimate(
            Files(("a.srt", 1_000), ("b.ass", 1_500)));

        Assert.Equal(2.0 * 2_500 / 1_000_000, result.Cost!.Value, precision: 12);
        Assert.Equal("USD", result.Currency);
    }

    [Fact]
    public async Task Estimate_WithoutRate_ReturnsNullCost()
    {
        SetupDefaultProvider("openai");
        SetupRates(("deepl", 0.5m));

        var result = await CreateService().Estimate(
            Files(("a.srt", 1_000)));

        Assert.Null(result.Cost);
        Assert.Equal("USD", result.Currency);
    }

    [Fact]
    public async Task Estimate_ServiceTypeList_UsesPrimaryProvider()
    {
        SetupDefaultProvider("[\"openai\",\"deepl\"]");
        SetupRates(("openai", 2.0m), ("deepl", 0.5m));

        var result = await CreateService().Estimate(
            Files(("a.srt", 1_000)));

        // priced at the primary (openai), not the fallback
        Assert.Equal(2.0 * 1_000 / 1_000_000, result.Cost!.Value, precision: 12);
    }

    [Fact]
    public async Task Estimate_MissingServiceType_FallsBackToDefault()
    {
        SetupDefaultProvider(null);
        SetupRates((SettingKeys.Translation.DefaultServiceType, 3.0m));

        var result = await CreateService().Estimate(
            Files(("a.srt", 2_000)));

        Assert.Equal(3.0 * 2_000 / 1_000_000, result.Cost!.Value, precision: 12);
    }

    [Fact]
    public async Task Estimate_NoFiles_ZeroCharactersAtConfiguredRate()
    {
        SetupDefaultProvider("openai");
        SetupRates(("openai", 2.0m));

        var result = await CreateService().Estimate(Files());

        Assert.Equal(0, result.Cost);
    }

    [Fact]
    public async Task Estimate_NullFiles_ZeroCostWithoutThrowing()
    {
        SetupDefaultProvider("openai");
        SetupRates(("openai", 2.0m));

        var result = await CreateService().Estimate(null);

        Assert.Equal(0, result.Cost);
        Assert.Equal("USD", result.Currency);
    }
}
