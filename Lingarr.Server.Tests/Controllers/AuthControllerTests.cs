using System.Threading.Tasks;
using Lingarr.Server.Controllers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;
using Lingarr.Server.Modules.Auth;
using Lingarr.Server.Modules.Settings;

namespace Lingarr.Server.Tests.Controllers;

/// <summary>
/// The settings UI gates the enable-authentication switch on
/// GET /api/auth/users/any, so the route must exist and return the raw
/// boolean the frontend expects (an earlier regression left the endpoint
/// unwired and enabling authentication always failed with 404).
/// </summary>
public class AuthControllerTests
{
    private readonly Mock<IAuthService> _authService = new();
    private readonly Mock<ISettingService> _settingService = new();
    private readonly AuthController _controller;

    public AuthControllerTests()
    {
        _controller = new AuthController(
            _authService.Object,
            _settingService.Object,
            NullLogger<AuthController>.Instance);
    }

    [Fact]
    public async Task HasAnyUsers_ReturnsTrueWhenAUserExists()
    {
        _authService.Setup(s => s.HasAnyUsers()).ReturnsAsync(true);

        var result = await _controller.HasAnyUsers();

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal(true, ok.Value);
    }

    [Fact]
    public async Task HasAnyUsers_ReturnsFalseForEmptyUserTable()
    {
        _authService.Setup(s => s.HasAnyUsers()).ReturnsAsync(false);

        var result = await _controller.HasAnyUsers();

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal(false, ok.Value);
    }
}
