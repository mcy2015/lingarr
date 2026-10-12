using Lingarr.Core.Entities;
using Microsoft.AspNetCore.Authorization;
using Lingarr.Server.Interfaces.Services;
using Lingarr.Server.Models.Api;
using Microsoft.AspNetCore.Mvc;

namespace Lingarr.Server.Controllers;

[ApiController]
[Authorize("LingarrAccess")]
[Route("api/[controller]")]
public class StatisticsController : ControllerBase
{
    private readonly IStatisticsService _statisticsService;
    private readonly ICostService _costService;

    public StatisticsController(
        IStatisticsService statisticsService,
        ICostService costService)
    {
        _statisticsService = statisticsService;
        _costService = costService;
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
    /// this month, 30-day success rate, monthly cost (A5).
    /// </summary>
    [HttpGet("pipeline")]
    public async Task<ActionResult<Models.Api.PipelineStatistics>> GetPipelineStatistics()
    {
        return Ok(await _statisticsService.GetPipelineStatistics());
    }

    /// <summary>
    /// Monthly cost rollup (A5): per-provider usage priced via the rate
    /// settings; defaults to the current month.
    /// </summary>
    [HttpGet("cost")]
    public async Task<ActionResult<CostSummary>> GetCostSummary([FromQuery] int? year, [FromQuery] int? month)
    {
        return Ok(await _costService.GetCostSummary(year, month));
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