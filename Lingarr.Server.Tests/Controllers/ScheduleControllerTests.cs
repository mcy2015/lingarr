using System.Threading.Tasks;
using Hangfire;
using Lingarr.Server.Controllers;
using Lingarr.Server.Models;
using Lingarr.Server.Modules.Infrastructure;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Xunit;

namespace Lingarr.Server.Tests.Controllers;

/// <summary>
/// Status-code mapping for the two schedule endpoints whose contracts
/// changed with the cron single-edit-entry work: PUT job/{id}/cron and the
/// create-job type restriction.
/// </summary>
public class ScheduleControllerTests
{
    private readonly Mock<IScheduleService> _scheduleService = new();
    private readonly Mock<IBackgroundJobClient> _backgroundJobClient = new();
    private readonly ScheduleController _controller;

    public ScheduleControllerTests()
    {
        _controller = new ScheduleController(_scheduleService.Object, _backgroundJobClient.Object);
    }

    [Fact]
    public async Task UpdateCron_InvalidCron_Returns400()
    {
        var action = await _controller.UpdateCron("SyncMovieJob", new UpdateCronRequest { Cron = "77 * * * *" });

        Assert.IsType<BadRequestObjectResult>(action);
    }

    [Fact]
    public async Task UpdateCron_UnknownJob_Returns404()
    {
        _scheduleService
            .Setup(service => service.UpdateJobCron("DoesNotExist", "0 5 * * *"))
            .ReturnsAsync(UpdateCronResult.NotFound);

        var action = await _controller.UpdateCron("DoesNotExist", new UpdateCronRequest { Cron = "0 5 * * *" });

        Assert.IsType<NotFoundObjectResult>(action);
    }

    [Fact]
    public async Task UpdateCron_SettingRowMissing_Returns409()
    {
        _scheduleService
            .Setup(service => service.UpdateJobCron("SyncMovieJob", "0 5 * * *"))
            .ReturnsAsync(UpdateCronResult.SettingRowMissing);

        var action = await _controller.UpdateCron("SyncMovieJob", new UpdateCronRequest { Cron = "0 5 * * *" });

        Assert.IsType<ConflictObjectResult>(action);
    }

    [Fact]
    public async Task UpdateCron_Updated_Returns200()
    {
        _scheduleService
            .Setup(service => service.UpdateJobCron("SyncMovieJob", "0 5 * * *"))
            .ReturnsAsync(UpdateCronResult.Updated);

        var action = await _controller.UpdateCron("SyncMovieJob", new UpdateCronRequest { Cron = "0 5 * * *" });

        Assert.IsType<OkResult>(action);
    }
}
