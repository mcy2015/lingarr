using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Lingarr.Contracts.Plugins;
using Lingarr.Contracts.Settings;
using Lingarr.Core.Data;
using Lingarr.Core.Entities;
using Lingarr.Core.Enum;
using Lingarr.Server.Models.FileSystem;
using Lingarr.Server.Modules.Infrastructure;
using Lingarr.Server.Modules.Subtitle;
using Lingarr.Server.Modules.SubtitleSearch;
using Lingarr.Server.Modules.SubtitleSearch.Abstractions;
using Lingarr.Server.Modules.SubtitleSearch.Orchestration;
using Lingarr.Server.Modules.SubtitleSearch.Pipeline;
using Lingarr.Server.Modules.SubtitleSearch.Providers;
using Lingarr.Server.Modules.SubtitleSearch.Queue;
using Lingarr.Server.Modules.SubtitleSearch.Requests;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

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
/// <param name="Query">
/// Optional keyword override (locked API contract): when set it replaces
/// the built title/keywords as the search term. Null keeps the
/// library-derived query.
/// </param>
public sealed record SubtitleSearchRequestBody(
    int MediaId,
    string MediaType,
    string[] Languages,
    string Provider,
    string? Query = null);

/// <summary>
/// One scoring contribution of a candidate (B09 detail drawer): the stable
/// dimension key the front end maps to a localized label, the points earned
/// (0 on a miss, negative for the wrong-episode cap row) and the hit flag.
/// </summary>
public sealed record SubtitleSignalResponse(string Name, int Points, bool Hit);

/// <summary>
/// One scored candidate as the front end renders it (cache-opaque external id kept).
/// <see cref="Signals"/> is the B09 score breakdown — additive tail
/// parameter, null-dropped when absent so legacy shapes stay byte-identical.
/// </summary>
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
    long? Size,
    IReadOnlyList<SubtitleSignalResponse>? Signals = null);

/// <summary>
/// Search response: candidates plus the cache key the download endpoint
/// needs, and the manual web-search escape hatch.
/// </summary>
/// <param name="WebSearchUrls">
/// Provider key → public-site deep link (the assrt throttling closure: the
/// user can go download by hand when the API path is rate-limited). All
/// links derive from the one media-level request, so every per-source
/// fan-out response carries the full map — a source that errored or was
/// skipped still gets its deep link once any sibling response arrives.
/// xunlei/shooter have no public search page and stay absent. Additive tail
/// parameter; null-dropping keeps legacy shapes byte-identical.
/// </param>
public sealed record SubtitleSearchResponse(
    string CacheKey,
    IReadOnlyList<SubtitleCandidateResponse> Candidates,
    IReadOnlyList<SubtitleSourceOutcome> SourceOutcomes,
    IReadOnlyDictionary<string, string>? WebSearchUrls = null);

/// <summary>
/// Manual download request (⑥): the picked candidate is addressed by the
/// search's cache key plus its index — no candidate payload round-trips
/// through the client.
/// </summary>
/// <param name="CacheKey">CacheKey of the preceding search.</param>
/// <param name="CandidateIndex">Index into that search's candidate list.</param>
/// <param name="MediaId">Library id of the movie/episode; must match the media the search ran for (QA3 P3-2).</param>
/// <param name="MediaType">"Movie" or "Episode"; must match the search's media (QA3 P3-2).</param>
/// <param name="Source">"Manual" (default), "Auto" or "Webhook".</param>
/// <param name="SkipTranslation">
/// Locked API contract: true keeps a target-language direct hit on disk but
/// never enqueues a translation request. Default false.
/// </param>
public sealed record SubtitleDownloadRequestBody(
    string CacheKey,
    int CandidateIndex,
    int MediaId,
    string MediaType,
    string? Source,
    bool SkipTranslation = false);

/// <summary>Download response: the landing path plus the history row id.</summary>
/// <param name="FilesLanded">How many files this download wrote — 1 for an ordinary candidate, N when a whole-season archive fanned out one entry per episode of the initiating season.</param>
/// <param name="CooldownUntilUtc">
/// UTC retry moment when <paramref name="Result"/> is CooldownSkipped — the
/// source sat in its RateLimited window and no upstream request was made
/// (the assrt 493 closure); null on every other result. Serializes as an
/// ISO-8601 UTC timestamp and is dropped by the null-dropping response
/// serializer when absent, mirroring the cooldown pair of the providers
/// endpoint.
/// </param>
public sealed record SubtitleDownloadResponse(
    int HistoryId,
    string? FilePath,
    string Result,
    string? Error,
    int? TranslationRequestId,
    bool TranslationDuplicate,
    int FilesLanded,
    DateTimeOffset? CooldownUntilUtc = null);

/// <summary>
/// Manual preview request (B09): addresses the picked candidate exactly the
/// way the download endpoint does — the search's cache key plus its index —
/// no candidate payload round-trips through the client. The preview fetches
/// the bytes but never lands them.
/// </summary>
/// <param name="CacheKey">CacheKey of the preceding search.</param>
/// <param name="CandidateIndex">Index into that search's candidate list.</param>
/// <param name="MediaId">Library id of the movie/episode; must match the search's media (QA3 P3-2).</param>
/// <param name="MediaType">"Movie" or "Episode"; must match the search's media (QA3 P3-2).</param>
public sealed record SubtitlePreviewRequestBody(
    string CacheKey,
    int CandidateIndex,
    int MediaId,
    string MediaType);

/// <summary>One cue of a preview: millisecond bounds plus the text lines.</summary>
public sealed record SubtitlePreviewCue(long StartMs, long EndMs, IReadOnlyList<string> Lines);

/// <summary>
/// Preview answer (B09): the parsed entry's format, the TOTAL cue count of
/// the file and the first <see cref="PreviewCueLimit"/> cues (start/end in
/// milliseconds plus text lines) — enough to judge a candidate before
/// downloading, small enough to stay a cheap click.
/// </summary>
public sealed record SubtitlePreviewResponse(
    string Format,
    int CueCount,
    IReadOnlyList<SubtitlePreviewCue> Cues);

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
/// The cooldown pair is the health state of design ⑧: a non-null
/// <see cref="CooldownUntil"/> means the source currently sits out
/// automated rounds (reason name in <see cref="CooldownReason"/>); expired
/// entries are dropped by the store, so null = healthy.
/// <see cref="Enabled"/> is the per-source switch
/// (<c>subtitle_{source}_enabled</c>, design ⑧ polish): false means the
/// aggregate and automated rounds skip the source — a manual per-source
/// search still queries it by name.
/// The quota pair is the R3 observability state: the source's own latest
/// claim about its daily download quota (captured from response headers by
/// the provider, kept in the singleton <see cref="SubtitleQuotaStore"/>).
/// Null for sources without a verified quota contract — only OpenSubtitles
/// captures today. Pass-through by design: staleness/exhaustion is the
/// front end's rendering concern (remaining &lt;= 0 or a past reset hides
/// the badge; the cooldown chip covers the exhausted case).
/// <see cref="WebsiteUrl"/> is the source's public site homepage (the
/// web-search escape hatch's fallback): null for xunlei/shooter, which have
/// no usable public search page — skipped/unconfigured sources still get a
/// link to jump to.
/// </summary>
public sealed record SubtitleProviderResponse(
    string Provider,
    string DisplayName,
    string? Description,
    bool RequiresAuth,
    bool CredentialsConfigured,
    IReadOnlyList<PluginSettingField> Settings,
    DateTimeOffset? CooldownUntil,
    string? CooldownReason,
    bool Enabled,
    int? QuotaRemaining = null,
    DateTimeOffset? QuotaResetUtc = null,
    string? WebsiteUrl = null);

/// <summary>
/// One row of the download history (screen 13): a subtitle_downloads row
/// joined with its library title. <see cref="MediaTitle"/> is null when the
/// owning movie/episode no longer exists (no FK on purpose, M0008) — the
/// row stays visible, the client renders it without a detail link.
/// </summary>
/// <param name="Id">subtitle_downloads id.</param>
/// <param name="MediaType">"Movie" or "Episode".</param>
/// <param name="MediaId">Library id of the owning movie/episode.</param>
/// <param name="MediaTitle">Resolved title; null for removed media.</param>
/// <param name="ShowId">Episode rows: the owning show's library id — the
/// detail route for an episode is the SHOW page, and routing with
/// <paramref name="MediaId"/> lands on /library/show/&lt;episodeId&gt;. Null for
/// movie rows and for episodes whose show no longer exists.</param>
/// <param name="Provider">Source key ("opensubtitles"…).</param>
/// <param name="Language">Normalized language code of the track.</param>
/// <param name="Source">How the attempt started — Manual/Auto/Webhook.</param>
/// <param name="Result">Succeeded/Failed/LowConfidenceSkipped/AlreadyExistsSkipped/CooldownSkipped.</param>
/// <param name="Error">Readable failure reason when <paramref name="Result"/> is Failed.</param>
/// <param name="FileName">File name of the landed subtitle; null when nothing was written.</param>
/// <param name="CreatedAt">Attempt timestamp.</param>
public sealed record SubtitleHistoryEntryResponse(
    int Id,
    string MediaType,
    int MediaId,
    string? MediaTitle,
    int? ShowId,
    string Provider,
    string Language,
    string Source,
    string Result,
    string? Error,
    string? FileName,
    DateTimeOffset CreatedAt);

/// <summary>
/// One keyset page of the history: rows in created_at DESC order plus the
/// opaque cursor of the next page (null on the last page).
/// </summary>
public sealed record SubtitleHistoryResponse(
    IReadOnlyList<SubtitleHistoryEntryResponse> Items,
    string? NextCursor);

/// <summary>
/// Manual surface of the subtitle search (tech design ⑥). M2 filled the
/// provider test endpoint; M3/M4 added search (per-source, front end fans
/// out), download (cacheKey + index through the pipeline), pin and backlog.
/// The observation batch adds the keyset-paginated download history
/// (screen 13) and the per-source cooldown health on the providers cards.
/// </summary>
[ApiController]
[Authorize("LingarrAccess")]
[Route("api/subtitle-search")]
public class SubtitleSearchController : ControllerBase
{
    private readonly SubtitleProviderRegistry _providerRegistry;
    private readonly ISettingsAccess _settingsAccess;
    private readonly SubtitleSearchOrchestrator _orchestrator;
    private readonly SubtitleQuotaStore _quotaStore;
    private readonly SubtitleSearchQueueService _queueService;
    private readonly SubtitlePinService _pinService;
    private readonly SubtitleDownloadPipeline _pipeline;
    private readonly SubtitleSearchRequestBuilder _requestBuilder;
    private readonly LingarrDbContext _dbContext;
    private readonly SubtitleArchiveExtractor _extractor;
    private readonly ISubtitleEncodingService _encodingService;
    private readonly ILogger<SubtitleSearchController> _logger;

    public SubtitleSearchController(
        SubtitleProviderRegistry providerRegistry,
        ISettingsAccess settingsAccess,
        SubtitleSearchOrchestrator orchestrator,
        SubtitleQuotaStore quotaStore,
        SubtitleSearchQueueService queueService,
        SubtitlePinService pinService,
        SubtitleDownloadPipeline pipeline,
        SubtitleSearchRequestBuilder requestBuilder,
        LingarrDbContext dbContext,
        SubtitleArchiveExtractor extractor,
        ISubtitleEncodingService encodingService,
        ILogger<SubtitleSearchController> logger)
    {
        _providerRegistry = providerRegistry;
        _settingsAccess = settingsAccess;
        _orchestrator = orchestrator;
        _quotaStore = quotaStore;
        _queueService = queueService;
        _pinService = pinService;
        _pipeline = pipeline;
        _requestBuilder = requestBuilder;
        _dbContext = dbContext;
        _extractor = extractor;
        _encodingService = encodingService;
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
            return BadRequest(new { message = "provider is required." });
        }

        if (_providerRegistry.Find(body.Provider) is null)
        {
            return NotFound(new { message = $"Unknown subtitle provider '{body.Provider}'.", provider = body.Provider });
        }

        // Library facts (year/imdb/release name, lazy hashes when the picked
        // source consumes one) resolved server-side; the requested languages
        // override the target∪source default. A keyword override (locked
        // contract, R3b) flows through the builder, which derives the
        // effective search term from it (trailing SxxEyy tags split off).
        var built = await _requestBuilder.BuildAsync(
            mediaType, body.MediaId, [body.Provider], body.Languages, cancellationToken,
            string.IsNullOrWhiteSpace(body.Query) ? null : body.Query.Trim());
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
                candidate.Size,
                // B09 score breakdown: null (pre-score shapes) drops the key,
                // so the additive tail never changes the legacy wire bytes.
                candidate.Signals is null
                    ? null
                    : candidate.Signals.Select(signal => new SubtitleSignalResponse(
                            signal.Name, signal.Points, signal.Hit)).ToList())).ToList(),
            outcome.SourceOutcomes,
            // Web-search escape hatch: the full per-source deep-link map of
            // the public sites, derived from the one media-level request —
            // independent of how THIS source's round went.
            SubtitleWebSearchUrls.BuildSearchUrls(built.Request)));
    }

    /// <summary>How many cues a preview answer carries (B09): the first 30.</summary>
    public const int PreviewCueLimit = 30;

    /// <summary>
    /// The whisper source's registry key (B04): transcription candidates have
    /// no subtitle bytes to preview — the「download」 is an ffmpeg extraction
    /// plus an API transcription, so preview answers 400 instead.
    /// </summary>
    private const string WhisperProviderKey = "whisper";

    /// <summary>
    /// Per-ref preview answers (B09): 60-second window, one entry per
    /// cacheKey+index. Pure anti-double-click storage — a dictionary with
    /// timestamps plus opportunistic eviction of expired entries (and of the
    /// oldest entries once the capacity cap is hit) keeps it bounded.
    /// </summary>
    private static readonly ConcurrentDictionary<
        string, (DateTimeOffset ExpiresAt, SubtitlePreviewResponse Response)> PreviewCache = new();

    private static readonly TimeSpan PreviewCacheTtl = TimeSpan.FromSeconds(60);
    private const int PreviewCacheCapacity = 128;

    /// <summary>
    /// Content preview of one candidate (B09): fetches the payload through
    /// the provider's ordinary download path, extracts/parses it ENTIRELY in
    /// memory and answers the first <see cref="PreviewCueLimit"/> cues —
    /// nothing is written to disk, no subtitle_downloads row, no coverage
    /// change, no cooldown/telemetry write (the zero-side-effect contract;
    /// contrast <see cref="Download"/>, which runs the full pipeline ⑦).
    /// Cache/expiry codes follow the download endpoint: expired search 410,
    /// media ownership mismatch or out-of-range index 400. A whisper
    /// candidate answers 400 (transcription has no previewable bytes).
    /// Provider-side failures never 500: content problems (InvalidResponse,
    /// unparseable text) answer 400, transport/auth/rate-limit problems 502.
    /// </summary>
    [HttpPost("preview")]
    public async Task<IActionResult> Preview(
        [FromBody] SubtitlePreviewRequestBody body,
        CancellationToken cancellationToken)
    {
        if (!TryParseMediaType(body.MediaType, out var mediaType))
        {
            return BadRequest(new { message = "mediaType must be 'Movie' or 'Episode'." });
        }

        var resolution = _orchestrator.TryResolveCache(body.CacheKey, mediaType, body.MediaId, out var candidates);
        if (resolution == SubtitleSearchOrchestrator.SubtitleCacheResolution.MediaMismatch)
        {
            return BadRequest(new
            {
                message = "The search result belongs to a different media item; run a new search for this one.",
                cacheKey = body.CacheKey
            });
        }

        if (resolution != SubtitleSearchOrchestrator.SubtitleCacheResolution.Resolved || candidates is null)
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

        var candidate = candidates[body.CandidateIndex];
        if (string.Equals(candidate.Provider, WhisperProviderKey, StringComparison.OrdinalIgnoreCase))
        {
            return BadRequest(new
            {
                message = "Whisper candidates are transcriptions — there is no subtitle file to preview."
            });
        }

        // Anti-double-click window: a fresh answer for this ref replays
        // without re-fetching from the source (only successes are cached —
        // a failed preview retries on the next click).
        var previewKey = body.CacheKey + ":" + body.CandidateIndex;
        var now = DateTimeOffset.UtcNow;
        if (PreviewCache.TryGetValue(previewKey, out var cached) && cached.ExpiresAt > now)
        {
            return Ok(cached.Response);
        }

        var registered = _providerRegistry.Find(candidate.Provider);
        if (registered is null)
        {
            return NotFound(new { message = $"Unknown subtitle provider '{candidate.Provider}'.", provider = candidate.Provider });
        }

        Lingarr.Server.Modules.SubtitleSearch.Abstractions.SubtitleDownload payload;
        try
        {
            payload = await registered.Provider.DownloadAsync(
                new SubtitleCandidateRef(candidate.ExternalId), cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (SubtitleProviderException ex)
        {
            // Upstream source problem (auth/rate-limit/transport) — the
            // candidate is fine, the fetch failed.
            return StatusCode(StatusCodes.Status502BadGateway, new { message = ex.Message });
        }

        // Memory extraction (the same extractor/parsers the pipeline uses,
        // ⑦ step ①/②) — plain payloads pass through, archives open from the
        // in-memory bytes; nothing is ever written to disk.
        ExtractedSubtitle entry;
        byte[] utf8;
        try
        {
            entry = _extractor.Extract(
                payload.Content, payload.FileName, payload.Format, candidate.Language, candidate.Provider);
            utf8 = _encodingService.ConvertToUtf8(entry.Content);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (SubtitleProviderException ex)
        {
            // Unparseable payload/fake archive — a permanent property of this
            // candidate, not a transport failure.
            return BadRequest(new { message = ex.Message });
        }
        catch (Exception ex)
        {
            return BadRequest(new { message = $"could not decode the payload: {ex.Message}" });
        }

        List<SubtitleItem> items;
        var format = PreviewFormatOf(entry.FileName);
        try
        {
            ISubtitleParser parser = format switch
            {
                "ass" or "ssa" => new SsaParser(),
                "vtt" => new VttParser(),
                _ => new SrtParser()
            };
            using var input = new MemoryStream(utf8);
            items = parser.ParseStream(input, Encoding.UTF8);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return BadRequest(new
            {
                message = $"could not parse '{entry.FileName}' as a subtitle: {ex.Message}"
            });
        }

        var response = new SubtitlePreviewResponse(
            format,
            items.Count,
            items.Take(PreviewCueLimit)
                .Select(item => new SubtitlePreviewCue(
                    item.StartTime,
                    item.EndTime,
                    (item.PlaintextLines.Count > 0 ? item.PlaintextLines : item.Lines).ToList()))
                .ToList());

        StorePreview(previewKey, response, now);
        return Ok(response);
    }

    /// <summary>
    /// Parses-format-of of a preview entry: the known subtitle extension of
    /// the entry name, lowercased without the dot; anything unrecognized
    /// parses as srt (the pipeline's own ConvertToSrt default — the SrtParser
    /// is the most tolerant of the three).
    /// </summary>
    private static string PreviewFormatOf(string fileName)
    {
        foreach (var extension in new[] { ".srt", ".ass", ".ssa", ".vtt" })
        {
            if (fileName.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            {
                return extension[1..].ToLowerInvariant();
            }
        }

        return "srt";
    }

    /// <summary>
    /// Stores one preview answer, keeping the dictionary bounded: expired
    /// entries go first, then the oldest survivors once the capacity cap is
    /// still exceeded. Best effort — a racing duplicate store is harmless
    /// (same key, equivalent answer).
    /// </summary>
    private static void StorePreview(string key, SubtitlePreviewResponse response, DateTimeOffset now)
    {
        if (PreviewCache.Count >= PreviewCacheCapacity)
        {
            foreach (var victim in PreviewCache
                         .Where(pair => pair.Value.ExpiresAt <= now)
                         .Select(pair => pair.Key)
                         .ToList())
            {
                PreviewCache.TryRemove(victim, out _);
            }

            if (PreviewCache.Count >= PreviewCacheCapacity)
            {
                foreach (var victim in PreviewCache
                             .OrderBy(pair => pair.Value.ExpiresAt)
                             .Take(PreviewCacheCapacity / 4)
                             .Select(pair => pair.Key)
                             .ToList())
                {
                    PreviewCache.TryRemove(victim, out _);
                }
            }
        }

        PreviewCache[key] = (now + PreviewCacheTtl, response);
    }

    /// <summary>
    /// Downloads the picked candidate through the full pipeline (⑦) and
    /// answers with the landing path plus the history row id. Expired cache
    /// keys answer 410 (run a new search, ②); a live key whose media does
    /// not match the requested media answers 400 (ownership binding,
    /// QA3 P3-2 — the search outcome belongs to exactly one library row).
    /// </summary>
    [HttpPost("download")]
    public async Task<IActionResult> Download(
        [FromBody] SubtitleDownloadRequestBody body,
        CancellationToken cancellationToken)
    {
        if (!TryParseMediaType(body.MediaType, out var mediaType))
        {
            return BadRequest(new { message = "mediaType must be 'Movie' or 'Episode'." });
        }

        var resolution = _orchestrator.TryResolveCache(body.CacheKey, mediaType, body.MediaId, out var candidates);
        if (resolution == SubtitleSearchOrchestrator.SubtitleCacheResolution.MediaMismatch)
        {
            return BadRequest(new
            {
                message = "The search result belongs to a different media item; run a new search for this one.",
                cacheKey = body.CacheKey
            });
        }

        if (resolution != SubtitleSearchOrchestrator.SubtitleCacheResolution.Resolved || candidates is null)
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

        var candidate = candidates[body.CandidateIndex];
        var source = ParseSource(body.Source);
        var sessionId = _orchestrator.SessionIdOf(body.CacheKey, candidate);
        // L2 correlation: re-establish the deciding search session around the
        // manual download — this request scope is not the one the search ran
        // in, but the session linkage lives in the orchestrator cache, so the
        // download's history/telemetry logs grep together with the search
        // round that surfaced the file. The pipeline re-stamps mediaKey and
        // this same session id around its own body.
        using var correlation = CorrelationContext.Push(searchSessionId: sessionId);
        var outcome = await _pipeline.DownloadAsync(
            candidate, mediaType, body.MediaId, source, sessionId, cancellationToken,
            body.SkipTranslation);

        return Ok(new SubtitleDownloadResponse(
            outcome.HistoryId,
            outcome.FilePath,
            outcome.Result.ToString(),
            outcome.Error,
            outcome.TranslationRequestId,
            outcome.TranslationDuplicate,
            outcome.FilesLanded,
            outcome.CooldownUntilUtc));
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
    /// Download history of the subtitle search (screen 13, tech design ⑥):
    /// keyset-paginated subtitle_downloads rows, newest first, joined with
    /// the library titles (episodes resolve through season→show). The
    /// cursor is opaque — base64("created_at|id") over the UTC round-trip
    /// form — and walks the ix_subtitle_downloads_created index with the id
    /// as the tiebreak so same-second rows paginate without gaps or dupes.
    /// Unknown providers answer an honest empty page, not a 404 (the filter
    /// is data, not an address).
    /// </summary>
    /// <param name="cursor">Opaque cursor of the previous page's last row; omitted on the first page.</param>
    /// <param name="limit">Page size, clamped to 1..100 (default 50).</param>
    /// <param name="mediaType">Optional "movie"/"episode" filter.</param>
    /// <param name="provider">Optional exact source-key filter.</param>
    /// <param name="result">Optional SubtitleDownloadResult enum-name filter.</param>
    /// <param name="source">Optional SubtitleDownloadSource enum-name filter (B02: the history screen's trigger filter — "Upgrade" isolates upgrade-round rows).</param>
    /// <param name="cancellationToken">Token to cancel the reads.</param>
    [HttpGet("history")]
    public async Task<IActionResult> History(
        string? cursor = null,
        int limit = 0,
        string? mediaType = null,
        string? provider = null,
        string? result = null,
        string? source = null,
        CancellationToken cancellationToken = default)
    {
        MediaType? parsedMediaType = null;
        if (!string.IsNullOrWhiteSpace(mediaType))
        {
            if (!TryParseMediaType(mediaType, out var parsed))
            {
                return BadRequest(new { message = "mediaType must be 'movie' or 'episode'." });
            }

            parsedMediaType = parsed;
        }

        SubtitleDownloadResult? parsedResult = null;
        if (!string.IsNullOrWhiteSpace(result))
        {
            if (!Enum.TryParse(result, ignoreCase: false, out SubtitleDownloadResult parsed)
                || !Enum.IsDefined(parsed))
            {
                return BadRequest(new
                {
                    message = "result must be one of Succeeded, Failed, LowConfidenceSkipped, AlreadyExistsSkipped, CooldownSkipped."
                });
            }

            parsedResult = parsed;
        }

        SubtitleDownloadSource? parsedSource = null;
        if (!string.IsNullOrWhiteSpace(source))
        {
            if (!Enum.TryParse(source, ignoreCase: false, out SubtitleDownloadSource parsedTrigger)
                || !Enum.IsDefined(parsedTrigger))
            {
                return BadRequest(new
                {
                    message = "source must be one of Manual, Auto, Webhook, Upgrade."
                });
            }

            parsedSource = parsedTrigger;
        }

        (DateTimeOffset CreatedAt, int Id)? boundary = null;
        if (!string.IsNullOrWhiteSpace(cursor))
        {
            if (!TryDecodeHistoryCursor(cursor, out var decoded))
            {
                return BadRequest(new { message = "cursor is not a valid history cursor." });
            }

            boundary = decoded;
        }

        limit = Math.Clamp(limit == 0 ? HistoryDefaultLimit : limit, 1, HistoryMaxLimit);

        var rows = _dbContext.SubtitleDownloads.AsNoTracking();
        if (parsedMediaType is { } type)
        {
            rows = rows.Where(download => download.MediaType == type);
        }
        if (!string.IsNullOrWhiteSpace(provider))
        {
            rows = rows.Where(download => download.Provider == provider);
        }
        if (parsedResult is { } outcome)
        {
            rows = rows.Where(download => download.Result == outcome);
        }
        if (parsedSource is { } trigger)
        {
            rows = rows.Where(download => download.Source == trigger);
        }
        if (boundary is { } edge)
        {
            // Strictly-newer rows only (the page is DESC): same-timestamp
            // rows fall through to the id tiebreak, which is what keeps
            // same-second bursts stable across pages. The tuple is
            // destructured up front so the predicate body stays a plain
            // column-vs-constant comparison.
            var edgeCreatedAt = edge.CreatedAt;
            var edgeId = edge.Id;
            rows = rows.Where(download =>
                download.CreatedAt < edgeCreatedAt
                || (download.CreatedAt == edgeCreatedAt && download.Id < edgeId));
        }

        var page = await rows
            .OrderByDescending(download => download.CreatedAt)
            .ThenByDescending(download => download.Id)
            .Select(download => new SubtitleHistoryRow(
                download.Id,
                download.MediaType,
                download.MediaId,
                download.Provider,
                download.Language,
                download.Source,
                download.Result,
                download.Error,
                download.FilePath,
                download.CreatedAt))
            .Take(limit + 1)
            .ToListAsync(cancellationToken);

        var hasMore = page.Count > limit;
        if (hasMore)
        {
            page.RemoveAt(limit);
        }

        var (titles, showIds) = await ResolveMediaTitlesAsync(page, cancellationToken);
        var items = page.Select(row => new SubtitleHistoryEntryResponse(
            row.Id,
            row.MediaType.ToString(),
            row.MediaId,
            titles.TryGetValue((row.MediaType, row.MediaId), out var title) ? title : null,
            // Episode rows link into the SHOW detail route — hand the client
            // the owning show's id so it does not route to /library/show/<episodeId>
            row.MediaType == MediaType.Episode
            && showIds.TryGetValue(row.MediaId, out var showId)
                ? showId
                : null,
            row.Provider,
            row.Language,
            row.Source.ToString(),
            row.Result.ToString(),
            row.Error,
            ExtractFileName(row.FilePath),
            row.CreatedAt)).ToList();

        return Ok(new SubtitleHistoryResponse(
            items,
            hasMore ? EncodeHistoryCursor(page[^1].CreatedAt, page[^1].Id) : null));
    }

    private const int HistoryDefaultLimit = 50;
    private const int HistoryMaxLimit = 100;

    /// <summary>Server-side projection of one history row (AsNoTracking read set).</summary>
    private sealed record SubtitleHistoryRow(
        int Id,
        MediaType MediaType,
        int MediaId,
        string Provider,
        string Language,
        SubtitleDownloadSource Source,
        SubtitleDownloadResult Result,
        string? Error,
        string FilePath,
        DateTimeOffset CreatedAt);

    /// <summary>
    /// Titles for one history page: the movie ids and episode ids of the
    /// page resolved in two AsNoTracking lookups (episodes join through
    /// season→show and render as "Show S01E02 Episode"). Media rows that
    /// no longer exist simply stay absent — the caller renders null.
    /// </summary>
    private async Task<(Dictionary<(MediaType, int), string> Titles, Dictionary<int, int> ShowIds)>
        ResolveMediaTitlesAsync(IReadOnlyList<SubtitleHistoryRow> page, CancellationToken cancellationToken)
    {
        var titles = new Dictionary<(MediaType, int), string>();
        var showIds = new Dictionary<int, int>();

        var movieIds = new List<int>();
        var episodeIds = new List<int>();
        foreach (var row in page)
        {
            if (row.MediaType == MediaType.Movie)
            {
                movieIds.Add(row.MediaId);
            }
            else
            {
                episodeIds.Add(row.MediaId);
            }
        }

        if (movieIds.Count > 0)
        {
            foreach (var pair in await _dbContext.Movies.AsNoTracking()
                         .Where(movie => movieIds.Contains(movie.Id))
                         .Select(movie => new { movie.Id, movie.Title })
                         .ToListAsync(cancellationToken))
            {
                titles[(MediaType.Movie, pair.Id)] = pair.Title;
            }
        }

        if (episodeIds.Count > 0)
        {
            foreach (var pair in await _dbContext.Episodes.AsNoTracking()
                         .Where(episode => episodeIds.Contains(episode.Id))
                         .Select(episode => new
                         {
                             episode.Id,
                             episode.Title,
                             episode.EpisodeNumber,
                             episode.Season.SeasonNumber,
                             ShowTitle = episode.Season.Show.Title,
                             OwningShowId = episode.Season.Show.Id
                         })
                         .ToListAsync(cancellationToken))
            {
                // Same SxxEyy shape the library/import pages show, so the
                // history row reads like every other episode reference.
                titles[(MediaType.Episode, pair.Id)] =
                    $"{pair.ShowTitle} S{pair.SeasonNumber:D2}E{pair.EpisodeNumber:D2} {pair.Title}";
                showIds[pair.Id] = pair.OwningShowId;
            }
        }

        return (titles, showIds);
    }

    /// <summary>
    /// File name of a landed subtitle. Path.GetFileName only splits on
    /// directory separators — release tags like "Movie.2023.WEBDL-1080p"
    /// survive untouched (the documented GetExtension trap).
    /// </summary>
    private static string? ExtractFileName(string? filePath) =>
        string.IsNullOrWhiteSpace(filePath) ? null : Path.GetFileName(filePath);

    /// <summary>base64("created_at|id") over the UTC round-trip form.</summary>
    private static string EncodeHistoryCursor(DateTimeOffset createdAt, int id)
        => Convert.ToBase64String(Encoding.UTF8.GetBytes(
            $"{createdAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture)}|{id}"));

    private static bool TryDecodeHistoryCursor(string cursor, out (DateTimeOffset CreatedAt, int Id) decoded)
    {
        decoded = default;
        try
        {
            var raw = Encoding.UTF8.GetString(Convert.FromBase64String(cursor));
            var separator = raw.LastIndexOf('|');
            if (separator <= 0 || separator == raw.Length - 1)
            {
                return false;
            }

            if (!DateTimeOffset.TryParseExact(
                    raw[..separator], "O", CultureInfo.InvariantCulture, DateTimeStyles.None,
                    out var createdAt)
                || !int.TryParse(raw[(separator + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
            {
                return false;
            }

            decoded = (createdAt, id);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>
    /// Per-source cards for the settings page: one entry per registered
    /// source in registry order — manifest form fields plus the auth flags,
    /// with credentialsConfigured read live from the encrypted settings
    /// store (any stored Secret field counts, so the badge updates on the
    /// SettingUpdatesHub hot reload). The cooldown pair comes from the
    /// orchestrator's active map (design ⑧ — expired entries already
    /// dropped), so a source sitting out rounds shows its until/reason
    /// without the card polling anything else. The enabled flag reads the
    /// per-source switch in one batched settings read (absent key =
    /// enabled — default-true rollout without a migration). A secret read
    /// that fails degrades that one source to credentialsConfigured=false;
    /// the list itself never 500s over it. The quota pair (R3) is the
    /// singleton <see cref="SubtitleQuotaStore"/>'s latest snapshot of the
    /// source — in-memory, provider-captured, null for sources without a
    /// verified quota contract.
    /// </summary>
    [HttpGet("providers")]
    public async Task<IActionResult> GetProviders()
    {
        // Health read through the already-injected orchestrator (its scoped
        // cooldown store round-trips the persisted map) — no new dependency.
        // A failing read degrades to "no cooldowns" rather than sinking the
        // settings page.
        var cooldowns = await GetActiveCooldownsSafeAsync();
        var enabledMap = await GetEnabledMapSafeAsync();
        var providers = new List<SubtitleProviderResponse>(_providerRegistry.All.Count);
        foreach (var entry in _providerRegistry.All)
        {
            var key = SubtitleSourceSettingKeys.EnabledKey(entry.Provider.Provider);
            cooldowns.TryGetValue(entry.Provider.Provider, out var cooldown);
            // R3 quota observability: the singleton store's latest snapshot
            // of this source, when its provider captured one (OpenSubtitles
            // today); every other source stays null and renders no badge.
            var quota = _quotaStore.Find(entry.Provider.Provider);
            providers.Add(new SubtitleProviderResponse(
                entry.Provider.Provider,
                entry.Manifest?.DisplayName ?? entry.Provider.Provider,
                entry.Manifest?.Description,
                entry.RequiresAuth,
                await IsCredentialConfigured(entry),
                entry.Manifest?.Settings ?? [],
                cooldown?.Until,
                cooldown?.Reason,
                !enabledMap.TryGetValue(key, out var stored) || SubtitleSourceSettingKeys.IsEnabled(stored),
                quota?.Remaining,
                quota?.ResetUtc,
                SubtitleWebSearchUrls.HomepageOf(entry.Provider.Provider)));
        }

        return Ok(providers);
    }

    /// <summary>
    /// Batched read of every source's enable switch. Any failure (or an
    /// empty result) degrades to "everything enabled" — the settings page
    /// must never sink over the switch display.
    /// </summary>
    private async Task<IReadOnlyDictionary<string, string>> GetEnabledMapSafeAsync()
    {
        try
        {
            // Null-guarded: a stubbed/unwired settings access may answer an
            // empty task — that reads as "every key absent" = all enabled.
            return await _settingsAccess.GetSettingsAsync(
                       _providerRegistry.All.Select(entry => SubtitleSourceSettingKeys.EnabledKey(entry.Provider.Provider)))
                   ?? new Dictionary<string, string>();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex, "Could not read the subtitle-source enable switches; rendering every source as enabled.");
            return new Dictionary<string, string>();
        }
    }

    private async Task<IReadOnlyDictionary<string, SubtitleSourceCooldown>> GetActiveCooldownsSafeAsync()
    {
        try
        {
            return await _orchestrator.GetActiveCooldownsAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex, "Could not read the subtitle source cooldown map; rendering sources as healthy.");
            return new Dictionary<string, SubtitleSourceCooldown>();
        }
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
