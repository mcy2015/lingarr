using System.Collections.Generic;
using System.Threading.Tasks;
using Lingarr.Core.Configuration;
using Lingarr.Server.Controllers;
using Lingarr.Server.Models;
using Lingarr.Server.Models.Api;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Xunit;
using Lingarr.Server.Modules.Settings;
using Lingarr.Server.Modules.Translation;

namespace Lingarr.Server.Tests.Controllers;

/// <summary>
/// Import estimate endpoint (PM analysis P0): a malformed request body
/// without a file list must price like an empty queue instead of
/// crashing with a NullReferenceException.
/// </summary>
public class ImportControllerTests
{
    [Fact]
    public async Task Estimate_NullFiles_ReturnsZeroCostInsteadOfThrowing()
    {
        var settingsMock = new Mock<ISettingService>();
        settingsMock
            .Setup(settings => settings.GetSetting(SettingKeys.Translation.ServiceType))
            .ReturnsAsync("openai");
        var costMock = new Mock<ICostService>();
        costMock
            .Setup(cost => cost.GetRates())
            .ReturnsAsync(new Dictionary<string, decimal> { ["openai"] = 2.0m });
        var controller = new ImportController(
            new ImportEstimateService(settingsMock.Object, costMock.Object));

        var actionResult = await controller.Estimate(new ImportEstimateRequest());

        var result = (ImportEstimateResult)((OkObjectResult)actionResult.Result!).Value!;
        Assert.Equal(0, result.Cost);
        Assert.Equal("USD", result.Currency);
    }
}
