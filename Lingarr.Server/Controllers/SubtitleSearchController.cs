using System.Diagnostics;
using Lingarr.Contracts.Plugins;
using Lingarr.Contracts.Settings;
using Lingarr.Core.Entities;
using Lingarr.Core.Enum;
using Lingarr.Server.Modules.SubtitleSearch;
using Lingarr.Server.Modules.SubtitleSearch.Abstractions;
using Lingarr.Server.Modules.SubtitleSearch.Orchestration;
using Lingarr.Server.Modules.SubtitleSearch.Pipeline;
using Lingarr.Server.Modules.SubtitleSearch.Queue;
using Lingarr.Server.Modules.SubtitleSearch.Requests;
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
/// Manual search request (tech design ⑥): the front end fires one per
/// enabled source in parallel (the per-source rendering ruling); the
/// provider is therefore required here.
/// </summary>
/// <param name="MediaId">Library id of the movie/episode.</param>
/// <param name="MediaType">"Movie" or "Episode".</param>
/// <param name="Languages">Language codes to search for.</param>
/// <param name="Provider">Source key to query (required per design ⑥).</param>
public sealed record SubtitleSearchRequestBody(
    int MediaId,
    string MediaType,
    string[] Languages,
    string Provider);

/// <summary>One scored candidate as the front end renders it (cache-opaque external id kept).</summary>
public sealed record SubtitleCandidateResponse(
    string Provider,
    string ExternalId,
    string Language,
    int MatchScore,
    bool HashMatch,
    bool Forced,
    bool HearingImpaired,
    string Format,
    string? ReleaseInfo,
    string? Uploader,
    long? Size);

/// <summary>Search response: candidates plus the cache key the download endpoint needs.</summary>
public sealed record SubtitleSearchResponse(
    string CacheKey,
    IReadOnlyList<SubtitleCandidateResponse> Candidates,
    IReadOnlyList<SubtitleSourceOutcome> SourceOutcomes);

/// <summary>
/// Manual download request (⑥): the picked candidate is addressed by the
/// search's cache key plus its index — no candidate payload round-trips
/// through the client.
/// </summary>
/// <param name="CacheKey">CacheKey of the preceding search.</param>
/// <param name="CandidateIndex">Index into that search's candidate list.</param>
/// <param name="MediaId">Library id of the movie/episode.</param>
/// <param name="MediaType">"Movie" or "Episode".</param>
/// <param name="Source">"Manual" (default), "Auto" or "Webhook".</param>
public sealed record SubtitleDownloadRequestBody(
    string CacheKey,
    int CandidateIndex,
    int MediaId,
    string MediaType,
    string? Source);

/// <summary>Download response: the landing path plus the history row id.</summary>
public sealed record SubtitleDownloadResponse(
    int HistoryId,
    string? FilePath,
    string Result,
    string? Error,
    int? TranslationRequestId,
    bool TranslationDuplicate);

/// <summary>Pin/unpin request (⑥).</summary>
/// <param name="MediaId">Library id of the movie/episode.</param>
/// <param name="MediaType">"Movie" or "Episode".</param>
/// <param name="Pinned">True pins the queue row to rank 0.</param>
public sealed record SubtitlePinRequestBody(int MediaId, string MediaType, bool Pinned);

/// <summary>Backlog snapshot of the batch card (⑥): all counts real, one SQL aggregate.</summary>
public sealed record SubtitleBacklogResponse(
    int Backlog,
    int PinnedCount,
    int WebhookBoostedCount,
    int NextRoundCount);

/// <summary>
/// One source card of the providers list (settings page): the manifest
/// shape the card renders — display name, description, settings form —
/// plus the two auth flags. <see cref="RequiresAuth"/> comes from the
/// registry attribute; <see cref="CredentialsConfigured"/> is true when
/// any Secret field of the manifest has a stored non-blank value. Entries
/// without a manifest (test doubles) degrade to the provider key, a null
/// description and an empty settings list instead of failing the list.
/// </summary>
public sealed record SubtitleProviderResponse(
    string Provider,
    string DisplayName,
    string? Description,
    bool RequiresAuth,
    bool CredentialsConfigured,
    IReadOnlyList<PluginSettingField> Settings);

/// <summary>
/// Manual surface of the subtitle search (tech design ⑥). M2 filled the
/// provider test endpoint; M3/M4 add search (per-source, front end fans
/// out), download (cacheKey + index through the pipeline), pin and backlog.
/// History/kpi land with their own batches (M5/M6).
/// </summary>
[ApiController]
[Authorize("LingarrAccess")]
[Route("api/subtitle-search")]
public class SubtitleSearchController : ControllerBase
{
    private readonly SubtitleProviderRegistry _providerRegistry;
    private readonly ISettingsAccess _settingsAccess;
    private readonly SubtitleSearchOrchestrator _orchestrator;
    private readonly SubtitleSearchQueueService _queueService;
    private readonly SubtitlePinService _pinService;
    private readonly SubtitleDownloadPipeline _pipeline;
    private readonly SubtitleSearchRequestBuilder _requestBuilder;
    private readonly ILogger<SubtitleSearchController> _logger;

    public SubtitleSearchController(
        SubtitleProviderRegistry providerRegistry,
        ISettingsAccess settingsAccess,
        SubtitleSearchOrchestrator orchestrator,
        SubtitleSearchQueueService queueService,
        SubtitlePinService pinService,
        SubtitleDownloadPipeline pipeline,
        SubtitleSearchRequestBuilder requestBuilder,
        ILogger<SubtitleSearchController> logger)
    {
        _providerRegistry = providerRegistry;
        _settingsAccess = settingsAccess;
        _orchestrator = orchestrator;
        _queueService = queueService;
        _pinService = pinService;
        _pipeline = pipeline;
        _requestBuilder = requestBuilder;
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
    /// One-source search for the manual dialog (⑥): the front end issues
    /// one call per enabled source in parallel and renders each response as
    /// it arrives — per-source 10s timeout, independent retries. Returns
    /// scored candidates plus the cache key the download call needs.
    /// </summary>
    [HttpPost("search")]
    public async Task<IActionResult> Search(
        [FromBody] SubtitleSearchRequestBody body,
        CancellationToken cancellationToken)
    {
        if (!TryParseMediaType(body.MediaType, out var mediaType))
        {
            return BadRequest(new { message = "mediaType must be 'Movie' or 'Episode'." });
        }

        if (body.Languages is not { Length: > 0 })
        {
            return BadRequest(new { message = "At least one language is required." });
        }

        if (string.IsNullOrWhiteSpace(body.Provider))
        {
            return BadRequest(new { message = "provider is required (per-source search, tech design ⑥)." });
        }

        if (_providerRegistry.Find(body.Provider) is null)
        {
            return NotFound(new { message = $"Unknown subtitle provider '{body.Provider}'.", provider = body.Provider });
        }

        // Library facts (year/imdb/release name, lazy hashes when the picked
        // source consumes one) resolved server-side; the requested languages
        // override the target∪source default.
        var built = await _requestBuilder.BuildAsync(
            mediaType, body.MediaId, [body.Provider], body.Languages, cancellationToken);
        if (built is null)
        {
            return NotFound(new
            {
                message = $"{mediaType} {body.MediaId} does not exist in the library (or no language is configured)."
            });
        }

        var outcome = await _orchestrator.SearchAsync(
            built.Request, mediaType, body.MediaId, body.Provider, cancellationToken);

        return Ok(new SubtitleSearchResponse(
            outcome.CacheKey,
            outcome.Candidates.Select(candidate => new SubtitleCandidateResponse(
                candidate.Provider,
                candidate.ExternalId,
                candidate.Language,
                candidate.MatchScore,
                candidate.HashMatch,
                candidate.Forced,
                candidate.HearingImpaired,
                candidate.Format,
                candidate.ReleaseInfo,
                candidate.Uploader,
                candidate.Size)).ToList(),
            outcome.SourceOutcomes));
    }

    /// <summary>
    /// Downloads the picked candidate through the full pipeline (⑦) and
    /// answers with the landing path plus the history row id. Expired cache
    /// keys answer 410 (run a new search, ②).
    /// </summary>
    [HttpPost("download")]
    public async Task<IActionResult> Download(
        [FromBody] SubtitleDownloadRequestBody body,
        CancellationToken cancellationToken)
    {
        var candidates = _orchestrator.ResolveCache(body.CacheKey);
        if (candidates is null)
        {
            return StatusCode(StatusCodes.Status410Gone, new
            {
                message = "The search result expired (10-minute window); run a new search.",
                cacheKey = body.CacheKey
            });
        }

        if (body.CandidateIndex < 0 || body.CandidateIndex >= candidates.Count)
        {
            return BadRequest(new
            {
                message = $"candidateIndex {body.CandidateIndex} is out of range (0..{candidates.Count - 1})."
            });
        }

        if (!TryParseMediaType(body.MediaType, out var mediaType))
        {
            return BadRequest(new { message = "mediaType must be 'Movie' or 'Episode'." });
        }

        var candidate = candidates[body.CandidateIndex];
        var source = ParseSource(body.Source);
        var sessionId = _orchestrator.SessionIdOf(body.CacheKey, candidate);
        var outcome = await _pipeline.DownloadAsync(
            candidate, mediaType, body.MediaId, source, sessionId, cancellationToken);

        return Ok(new SubtitleDownloadResponse(
            outcome.HistoryId,
            outcome.FilePath,
            outcome.Result.ToString(),
            outcome.Error,
            outcome.TranslationRequestId,
            outcome.TranslationDuplicate));
    }

    /// <summary>
    /// Pins or unpins one media item's queue row (⑤/⑥). Pinning an item
    /// that was never queued enqueues it; pinning a skipped row revives it.
    /// </summary>
    [HttpPost("pin")]
    public async Task<IActionResult> Pin([FromBody] SubtitlePinRequestBody body, CancellationToken cancellationToken)
    {
        if (!TryParseMediaType(body.MediaType, out var mediaType))
        {
            return BadRequest(new { message = "mediaType must be 'Movie' or 'Episode'." });
        }

        var state = await _pinService.SetPinnedAsync(mediaType, body.MediaId, body.Pinned, cancellationToken);
        if (state is null)
        {
            return BadRequest(new { message = "mediaType must be 'Movie' or 'Episode'." });
        }

        return Ok(new
        {
            mediaType = state.MediaType.ToString(),
            mediaId = state.MediaId,
            pinned = state.Pinned,
            priorityRank = state.PriorityRank.ToString(),
            status = state.Status.ToString()
        });
    }

    /// <summary>
    /// Backlog snapshot for the batch card (⑥): backlog, pinned count,
    /// active webhook boosts and the next round's pickup count — one
    /// aggregate, no placeholders.
    /// </summary>
    [HttpGet("backlog")]
    public async Task<IActionResult> Backlog(CancellationToken cancellationToken)
    {
        var snapshot = await _queueService.GetBacklogSnapshotAsync(cancellationToken);
        return Ok(new SubtitleBacklogResponse(
            snapshot.Backlog,
            snapshot.PinnedCount,
            snapshot.WebhookBoostedCount,
            snapshot.NextRoundCount));
    }

    /// <summary>
    /// Per-source cards for the settings page: one entry per registered
    /// source in registry order — manifest form fields plus the auth flags,
    /// with credentialsConfigured read live from the encrypted settings
    /// store (any stored Secret field counts, so the badge updates on the
    /// SettingUpdatesHub hot reload). A secret read that fails degrades
    /// that one source to credentialsConfigured=false; the list itself
    /// never 500s over it.
    /// </summary>
    [HttpGet("providers")]
    public async Task<IActionResult> GetProviders()
    {
        var providers = new List<SubtitleProviderResponse>(_providerRegistry.All.Count);
        foreach (var entry in _providerRegistry.All)
        {
            providers.Add(new SubtitleProviderResponse(
                entry.Provider.Provider,
                entry.Manifest?.DisplayName ?? entry.Provider.Provider,
                entry.Manifest?.Description,
                entry.RequiresAuth,
                await IsCredentialConfigured(entry),
                entry.Manifest?.Settings ?? []));
        }

        return Ok(providers);
    }

    /// <summary>
    /// Whether any Secret field of the source manifest has a stored
    /// non-blank value. Sources without secrets (and manifest-less test
    /// doubles) are never configured; a failing read degrades to false
    /// with a log line instead of failing the endpoint.
    /// </summary>
    private async Task<bool> IsCredentialConfigured(RegisteredSubtitleProvider entry)
    {
        if (entry.Manifest is null)
        {
            return false;
        }

        try
        {
            foreach (var field in entry.Manifest.Settings)
            {
                if (field.Type != PluginSettingType.Secret)
                {
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(await _settingsAccess.GetEncryptedSettingAsync(field.Key)))
                {
                    return true;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Could not read credentials of subtitle source {Provider}; rendering it as unconfigured.",
                entry.Provider.Provider);
            return false;
        }

        return false;
    }

    private static bool TryParseMediaType(string? value, out MediaType mediaType)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case "movie":
                mediaType = MediaType.Movie;
                return true;
            case "episode":
                mediaType = MediaType.Episode;
                return true;
            default:
                mediaType = default;
                return false;
        }
    }

    private static SubtitleDownloadSource ParseSource(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            "auto" => SubtitleDownloadSource.Auto,
            "webhook" => SubtitleDownloadSource.Webhook,
            _ => SubtitleDownloadSource.Manual
        };
}
