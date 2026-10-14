using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Lingarr.Core.Entities;
using Lingarr.Server.Controllers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;
using Lingarr.Server.Modules.Settings;

namespace Lingarr.Server.Tests.Controllers;

/// <summary>
/// P2-5 audit lines: every settings write path logs one Information entry
/// carrying the key(s) and whether the value was stored encrypted — and
/// never the value itself.
/// </summary>
public class SettingControllerTests
{
    private readonly Mock<ISettingService> _settingService = new();
    private readonly Mock<ILogger<SettingController>> _logger = new();
    private readonly SettingController _controller;

    public SettingControllerTests()
    {
        _controller = new SettingController(_settingService.Object, _logger.Object);
    }

    [Fact]
    public async Task SetSetting_Success_LogsKeyWithoutValue()
    {
        _settingService.Setup(s => s.SetSetting("movie_schedule", "0 4 * * *")).ReturnsAsync(true);

        await _controller.SetSetting(new Setting { Key = "movie_schedule", Value = "0 4 * * *" });

        AssertInformationLog(count: 1, containing: "movie_schedule", notContaining: "0 4 * * *");
    }

    [Fact]
    public async Task SetSetting_Failure_DoesNotLogAnAuditLine()
    {
        _settingService.Setup(s => s.SetSetting(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(false);

        var action = await _controller.SetSetting(new Setting { Key = "movie_schedule", Value = "x" });

        Assert.IsType<BadRequestObjectResult>(action.Result);
        AssertInformationLog(count: 0, containing: "movie_schedule");
    }

    [Fact]
    public async Task SetSettings_Success_LogsAllKeysWithoutValues()
    {
        _settingService
            .Setup(s => s.SetSettings(It.IsAny<Dictionary<string, string>>()))
            .ReturnsAsync(true);

        await _controller.SetSettings(new Dictionary<string, string>
        {
            ["movie_schedule"] = "secret-1",
            ["show_schedule"] = "secret-2"
        });

        AssertInformationLog(count: 1, containing: "movie_schedule", notContaining: "secret-1");
        AssertInformationLog(count: 1, containing: "show_schedule", notContaining: "secret-2");
    }

    [Fact]
    public async Task SetEncryptedSetting_Success_LogsKeyAndEncryptionMarkerWithoutValue()
    {
        _settingService
            .Setup(s => s.SetEncryptedSetting("api_key", "super-secret-key"))
            .ReturnsAsync(true);

        await _controller.SetEncryptedSetting(new Setting { Key = "api_key", Value = "super-secret-key" });

        AssertInformationLog(count: 1, containing: "api_key", notContaining: "super-secret-key");
        AssertInformationLog(count: 1, containing: "encrypted");
    }

    /// <summary>
    /// Verifies the controller emitted exactly <paramref name="count"/>
    /// Information entries whose rendered message contains (and optionally
    /// does not contain) the given fragments.
    /// </summary>
    private void AssertInformationLog(
        int count,
        string containing,
        string? notContaining = null)
    {
        _logger.Verify(
            x => x.Log(
                LogLevel.Information,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((state, _) =>
                    state.ToString()!.Contains(containing)
                    && (notContaining == null || !state.ToString()!.Contains(notContaining))),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Exactly(count));
    }
}
