using System.Diagnostics;
using Lingarr.Server.Modules.SubtitleSearch;
using Lingarr.Server.Modules.SubtitleSearch.Abstractions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Lingarr.Server.Controllers;

/// <summary>
/// Result shape of the source connection test — the translate-test
/// precedent (TranslateController.TestServiceConnection) applied to
/// subtitle sources: provider failures land as ok=false with a readable
/// message instead of a 500, latency is measured around the probe.
/// </summary>
/// <param name="Ok">Whether the source proved usable.</param>
/// <param name="LatencyMs">Round-trip milliseconds of the probe.</param>
/// <param name="Message">Human-readable verdict (PRD AC2 distinguishes 429/timeout/credentials).</param>
public sealed record SubtitleSourceTestResult(bool Ok, double LatencyMs, string Message);

/// <summary>
/// Manual surface of the subtitle search (tech design ⑥). M2 fills the
/// provider test endpoint: sources implementing
/// <see cref="ISubtitleProviderHealth"/> run one real lightweight probe;
/// registry entries without a probe keep the structured 501 so the skeleton
/// contract stays observable; unknown source keys 404. Search/download/pin/
/// backlog/history/kpi endpoints land with their milestone batches (M3–M6).
/// </summary>
[ApiController]
[Authorize("LingarrAccess")]
[Route("api/subtitle-search")]
public class SubtitleSearchController : ControllerBase
{
    private readonly SubtitleProviderRegistry _providerRegistry;
    private readonly ILogger<SubtitleSearchController> _logger;

    public SubtitleSearchController(
        SubtitleProviderRegistry providerRegistry,
        ILogger<SubtitleSearchController> logger)
    {
        _providerRegistry = providerRegistry;
        _logger = logger;
    }

    /// <summary>
    /// Credentials/connectivity check for one source (tech design ⑥): one
    /// real lightweight request — OpenSubtitles quota-page style search,
    /// minimal keyword queries elsewhere. Credentials are read from the
    /// settings keys the source manifest declares; failures distinguish
    /// 429/timeout/invalid credentials (PRD AC2) and never 500.
    /// </summary>
    /// <param name="provider">Source key ("opensubtitles"…).</param>
    /// <param name="cancellationToken">Token to cancel the probe.</param>
    [HttpPost("test/{provider}")]
    public async Task<IActionResult> TestProvider(string provider, CancellationToken cancellationToken)
    {
        var entry = _providerRegistry.Find(provider);
        if (entry is null)
        {
            return NotFound(new { message = $"Unknown subtitle provider '{provider}'.", provider });
        }

        if (entry.Provider is not ISubtitleProviderHealth health)
        {
            return StatusCode(StatusCodes.Status501NotImplemented, new
            {
                message = "This subtitle source does not implement a connection test.",
                provider
            });
        }

        var stopwatch = Stopwatch.StartNew();
        try
        {
            var outcome = await health.TestConnectionAsync(cancellationToken);
            stopwatch.Stop();
            return Ok(new SubtitleSourceTestResult(outcome.Ok, stopwatch.Elapsed.TotalMilliseconds, outcome.Message));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (SubtitleProviderException ex)
        {
            stopwatch.Stop();
            _logger.LogWarning(
                "Subtitle source test for {Provider} failed: {Reason}.", provider, ex.Reason);
            return Ok(new SubtitleSourceTestResult(false, stopwatch.Elapsed.TotalMilliseconds, ex.Message));
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _logger.LogWarning(ex, "Subtitle source test for {Provider} crashed.", provider);
            return Ok(new SubtitleSourceTestResult(
                false,
                stopwatch.Elapsed.TotalMilliseconds,
                ex.InnerException?.Message ?? ex.Message));
        }
    }

    /// <summary>
    /// Per-source cards: enabled, cooldown state, last error and the daily
    /// quota row. Answers 501 until the M3 automation batch lands the
    /// cooldown/quota wiring; the provider keys in the body are the real
    /// registry contents, not a placeholder list (empty means no source
    /// registered yet).
    /// </summary>
    [HttpGet("providers")]
    public IActionResult GetProviders()
    {
        return StatusCode(StatusCodes.Status501NotImplemented, new
        {
            message = "The subtitle source list with health and quota state lands with the M3 automation batch.",
            providers = _providerRegistry.All.Select(entry => entry.Provider.Provider).ToArray()
        });
    }
}
