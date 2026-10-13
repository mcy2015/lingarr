using Lingarr.Core.Entities;
using Microsoft.AspNetCore.Authorization;
using Lingarr.Server.Models.Api;
using Microsoft.AspNetCore.Mvc;
using Lingarr.Server.Modules.Statistics;
using Lingarr.Server.Modules.Translation;

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

    /// <summary>
    /// Daily write-back/cost series for the overview trend card (PM analysis
    /// P1-1): per-day completed write-backs plus usage priced via the provider
    /// rate table; costs are null while any usage is unpriced.
    /// </summary>
    [HttpGet("trend/{days}")]
    public async Task<ActionResult<Models.Api.DailyTrendStatistics>> GetDailyTrend(int days = 14)
    {
        return Ok(await _statisticsService.GetDailyTrendStatistics(days));
    }

    [HttpPost("reset")]
    public async Task<ActionResult> ResetStatistics()
    {
        await _statisticsService.ResetStatistics();
        return Ok(new { message = "Statistics have been reset successfully" });
    }
}