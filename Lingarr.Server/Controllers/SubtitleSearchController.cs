using Lingarr.Server.Modules.SubtitleSearch;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Lingarr.Server.Controllers;

/// <summary>
/// Manual surface of the subtitle search (tech design ⑥). M1 ships the
/// skeleton only: the provider-health endpoints answer with a structured
/// 501 until the M2 source batch fills them in; unknown source keys are
/// rejected with 404 up front so route shape and auth are exercisable now.
/// Search/download/pin/backlog/history/kpi endpoints land with their
/// milestone batches (M2–M6).
/// </summary>
[ApiController]
[Authorize("LingarrAccess")]
[Route("api/subtitle-search")]
public class SubtitleSearchController : ControllerBase
{
    private readonly SubtitleProviderRegistry _providerRegistry;

    public SubtitleSearchController(SubtitleProviderRegistry providerRegistry)
    {
        _providerRegistry = providerRegistry;
    }

    /// <summary>
    /// Credentials/connectivity/quota check for one source. Known sources
    /// answer 501 until the M2 source implementations land; unknown names
    /// 404 before that so the registry lookup stays observable.
    /// </summary>
    /// <param name="provider">Source key ("opensubtitles"…).</param>
    [HttpPost("test/{provider}")]
    public IActionResult TestProvider(string provider)
    {
        if (_providerRegistry.Find(provider) is null)
        {
            return NotFound(new { message = $"Unknown subtitle provider '{provider}'.", provider });
        }

        return StatusCode(StatusCodes.Status501NotImplemented, new
        {
            message = "The subtitle source health check is not implemented yet; it lands with the M2 source batch.",
            provider
        });
    }

    /// <summary>
    /// Per-source cards: enabled, cooldown state, last error and the daily
    /// quota row. Answers 501 until the M2 source batch lands; the provider
    /// keys in the body are the real registry contents, not a placeholder
    /// list (empty means no source registered yet).
    /// </summary>
    [HttpGet("providers")]
    public IActionResult GetProviders()
    {
        return StatusCode(StatusCodes.Status501NotImplemented, new
        {
            message = "The subtitle source list with health and quota state lands with the M2 source batch.",
            providers = _providerRegistry.All.Select(entry => entry.Provider.Provider).ToArray()
        });
    }
}
