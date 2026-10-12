using Lingarr.Core.Entities;
using Microsoft.AspNetCore.Authorization;
using Lingarr.Server.Interfaces.Services;
using Microsoft.AspNetCore.Mvc;

namespace Lingarr.Server.Controllers;

[ApiController]
[Authorize("LingarrAccess")]
[Route("api/[controller]")]
public class StatisticsController : ControllerBase
{
    private readonly IStatisticsService _statisticsService;

    public StatisticsController(IStatisticsService statisticsService)
    {
        _statisticsService = statisticsService;
    }

    [HttpGet]
    public async Task<ActionResult<Statistics>> GetStatistics()
    {
        var stats = await _statisticsService.GetStatistics();
        return Ok(stats);
    }
    
    /// <summary>
    /// Overview pipeline strip payload (gap-audit A2): media count, missing
    /// subtitle tracks, Hangfire queue depth, online providers, write-backs
    /// this month, 30-day success rate. Monthly cost is null until A5.
    /// </summary>
    [HttpGet("pipeline")]
    public async Task<ActionResult<Models.Api.PipelineStatistics>> GetPipelineStatistics()
    {
        return Ok(await _statisticsService.GetPipelineStatistics());
    }

    [HttpGet("daily/{days}")]
    public async Task<ActionResult<IEnumerable<DailyStatistics>>> GetDailyStats(int days = 30)
    {
        var stats = await _statisticsService.GetDailyStatistics(days);
        return Ok(stats);
    }

    [HttpPost("reset")]
    public async Task<ActionResult> ResetStatistics()
    {
        await _statisticsService.ResetStatistics();
        return Ok(new { message = "Statistics have been reset successfully" });
    }
}