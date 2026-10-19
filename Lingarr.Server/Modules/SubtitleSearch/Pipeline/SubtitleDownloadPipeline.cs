using System.Security.Cryptography;
using System.Text;
using Lingarr.Core.Configuration;
using Lingarr.Core.Data;
using Lingarr.Core.Entities;
using Lingarr.Core.Enum;
using Lingarr.Server.Controllers;
using Lingarr.Server.Hubs;
using Lingarr.Server.Models;
using Lingarr.Server.Models.FileSystem;
using Lingarr.Server.Modules.Settings;
using Lingarr.Server.Modules.Subtitle;
using Lingarr.Server.Modules.SubtitleSearch.Abstractions;
using Lingarr.Server.Modules.SubtitleSearch.Orchestration;
using Lingarr.Server.Modules.SubtitleSearch.Telemetry;
using Lingarr.Server.Modules.Translation;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace Lingarr.Server.Modules.SubtitleSearch.Pipeline;

/// <summary>
/// Verdict of one pipeline run — every outcome lands a history row (⑦
/// step ⑤), successes and failures alike, so the history screen counts
/// from real attempts only.
/// </summary>
/// <param name="HistoryId">Id of the written subtitle_downloads row.</param>
/// <param name="FilePath">Where the file landed; null when nothing was written.</param>
/// <param name="Result">Outcome classification of the attempt.</param>
/// <param name="Error">Readable failure reason when <see cref="Result"/> is Failed.</param>
/// <param name="TranslationRequestId">First translation request created (⑦ step ⑦, one per target language); null when none.</param>
/// <param name="TranslationDuplicate">True when at least one target language was skipped because the same content/target pair is already tracked.</param>
/// <param name="FilesLanded">How many files this attempt wrote: 1 for an ordinary candidate, N when a whole-season archive fanned out one entry per episode, 0 when nothing landed.</param>
public sealed record SubtitleDownloadOutcome(
    int HistoryId,
    string? FilePath,
    SubtitleDownloadResult Result,
    string? Error = null,
    int? TranslationRequestId = null,
    bool TranslationDuplicate = false,
    int FilesLanded = 0);

/// <summary>
/// Download post-processing pipeline (tech design ⑦): fetch → magic-number
/// archive extraction → encoding conversion (GBK/GB18030 handled by the
/// existing <see cref="ISubtitleEncodingService"/>) → optional SRT
/// conversion (default on) → naming-template path via
/// <see cref="ISubtitleService.CreateFilePath"/> (the same template
/// translation outputs use) → history + telemetry rows → translation
/// enqueue for source-language hits → single-media coverage refresh →
/// JobProgressHub broadcast.
/// <para>
/// zh direct hits are terminal (dual-path ruling: a target-language
/// subtitle needs no translation); only non-target languages with
/// auto_translate on enter the translation queue, idempotent on the
/// content hash (the import-service dedup rule).
/// </para>
/// </summary>
public sealed class SubtitleDownloadPipeline
{
    /// <summary>
    /// Known video extensions for the naming template input — a media file
    /// name like "Movie.2023.WEBDL-1080p" must never lose ".WEBDL-1080p"
    /// to an extension guess (known trap), so only these are stripped.
    /// </summary>
    private static readonly string[] VideoExtensions =
        [".mkv", ".mp4", ".avi", ".mov", ".m4v", ".ts", ".wmv", ".flv", ".webm"];

    /// <summary>
    /// How long an automatic blacklist observation stays in effect (locked
    /// read/write contract with the search filter side, which matches exactly
    /// on (provider, external_id) inside this window). Long enough that a
    /// permanently broken upload stops being re-attempted every round, short
    /// enough that a source fixing its data recovers within a quarter.
    /// </summary>
    private const int BlacklistWindowDays = 90;

    /// <summary>Reason summary cap — the row is an observation, not a log.</summary>
    private const int BlacklistReasonMaxLength = 300;

    private readonly LingarrDbContext _dbContext;
    private readonly SubtitleProviderRegistry _registry;
    private readonly SubtitleArchiveExtractor _extractor;
    private readonly ISubtitleEncodingService _encodingService;
    private readonly ISubtitleService _subtitleService;
    private readonly ITranslationRequestQueueService _translationQueue;
    private readonly ISettingService _settingService;
    private readonly SubtitleSourceCooldownStore _cooldowns;
    private readonly SubtitleTelemetryService _telemetry;
    private readonly SubtitleCoverageRefresher _coverageRefresher;
    private readonly IHubContext<JobProgressHub> _hubContext;
    private readonly ILogger<SubtitleDownloadPipeline> _logger;

    public SubtitleDownloadPipeline(
        LingarrDbContext dbContext,
        SubtitleProviderRegistry registry,
        SubtitleArchiveExtractor extractor,
        ISubtitleEncodingService encodingService,
        ISubtitleService subtitleService,
        ITranslationRequestQueueService translationQueue,
        ISettingService settingService,
        SubtitleSourceCooldownStore cooldowns,
        SubtitleTelemetryService telemetry,
        SubtitleCoverageRefresher coverageRefresher,
        IHubContext<JobProgressHub> hubContext,
        ILogger<SubtitleDownloadPipeline> logger)
    {
        _dbContext = dbContext;
        _registry = registry;
        _extractor = extractor;
        _encodingService = encodingService;
        _subtitleService = subtitleService;
        _translationQueue = translationQueue;
        _settingService = settingService;
        _cooldowns = cooldowns;
        _telemetry = telemetry;
        _coverageRefresher = coverageRefresher;
        _hubContext = hubContext;
        _logger = logger;
    }

    /// <summary>
    /// Runs the pipeline for one candidate. Never throws for source/format
    /// problems — failures land as history rows with readable errors; only
    /// caller cancellation propagates.
    /// </summary>
    /// <param name="candidate">The picked candidate (from the orchestrator cache).</param>
    /// <param name="mediaType">Owning media type (Movie/Episode).</param>
    /// <param name="mediaId">Owning media id.</param>
    /// <param name="source">How the download was initiated.</param>
    /// <param name="searchSessionId">Deciding search session, when known (history linkage).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <param name="skipTranslation">
    /// Manual-download override (locked API contract): true keeps the
    /// target-language direct hit on disk but never enqueues a translation
    /// request. Default false preserves the automated dual-path ruling.
    /// </param>
    public async Task<SubtitleDownloadOutcome> DownloadAsync(
        SubtitleCandidate candidate,
        MediaType mediaType,
        int mediaId,
        SubtitleDownloadSource source,
        int? searchSessionId,
        CancellationToken cancellationToken,
        bool skipTranslation = false)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        var media = await LoadMediaAsync(mediaType, mediaId, cancellationToken);
        if (media is null)
        {
            return await RecordAsync(
                candidate, mediaType, mediaId, source, searchSessionId,
                SubtitleDownloadResult.Failed, filePath: null,
                error: $"{mediaType} {mediaId} does not exist in the library.");
        }

        // Auto rounds must not duplicate coverage the library already has
        // (the queue was filled from the needs-translation gap scan, but a
        // file may have landed since). Manual picks always proceed — the
        // user explicitly chose this candidate.
        if (source == SubtitleDownloadSource.Auto
            && media.DirectoryPath is not null
            && media.VideoFileName is not null)
        {
            var existing = await _subtitleService.GetSubtitles(media.DirectoryPath, media.VideoFileName);
            if (existing.Any(subtitle => Carries(subtitle, candidate.Language)))
            {
                return await RecordAsync(
                    candidate, mediaType, mediaId, source, searchSessionId,
                    SubtitleDownloadResult.AlreadyExistsSkipped,
                    filePath: existing.First().Path,
                    error: null);
            }
        }

        // ① fetch the raw payload (often an archive) from the source, then
        // pick the entry by magic number. Provider/format failures land as
        // history rows with the full media context.
        var registered = _registry.Find(candidate.Provider);
        if (registered is null)
        {
            return await RecordAsync(
                candidate, mediaType, mediaId, source, searchSessionId,
                SubtitleDownloadResult.Failed, filePath: null,
                error: $"Unknown subtitle provider '{candidate.Provider}'.");
        }

        byte[] payloadContent;
        string? payloadFileName;
        string? payloadFormat;
        IReadOnlyList<ExtractedSubtitle> entries;
        try
        {
            var payload = await registered.Provider.DownloadAsync(
                new SubtitleCandidateRef(candidate.ExternalId), cancellationToken);
            // ④ Quota telemetry is monitoring, not business logic — an
            // upsert failure must never discard an already-fetched payload
            // (QA2 P2-3: sharing the download try-block turned SQL hiccups
            // into lost downloads without a Failed history row). The
            // orchestrator's non-fatal telemetry ruling, applied here too.
            try
            {
                await _telemetry.RecordDownloadQuotaAsync(
                    candidate.Provider, SubtitleTelemetryService.PublishedQuotaLimit(candidate.Provider),
                    payload.QuotaRemaining);
            }
            catch (Exception telemetryError)
            {
                _logger.LogWarning(telemetryError,
                    "Download quota upsert failed for {Provider}; the download continues.",
                    candidate.Provider);
            }

            payloadContent = payload.Content;
            payloadFileName = payload.FileName;
            payloadFormat = payload.Format;

            // ① every subtitle entry of the payload (a single passthrough
            // entry for plain files) — the season-pack fan-out below decides
            // from the full list whether one entry per episode should land;
            // every failure contract of the single pick applies unchanged.
            entries = _extractor.ExtractAll(
                payloadContent, payloadFileName, payloadFormat, candidate.Provider);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (SubtitleProviderException ex)
        {
            return await HandleExtractionFailureAsync(
                candidate, mediaType, mediaId, source, searchSessionId, ex, cancellationToken);
        }

        // ①b season-pack fan-out (episode downloads only): an archive whose
        // entries carry episode markers of the initiating season lands one
        // file per sibling episode instead of one picked file — the whole-
        // season packs subtitle sources serve would otherwise mis-file every
        // episode but one. Returns null whenever the pack does not qualify,
        // and the single-entry path below runs byte-identically to before.
        if (mediaType == MediaType.Episode && media.SeasonId is int initiatingSeasonId)
        {
            var seasonPack = await TryDownloadSeasonPackAsync(
                candidate, mediaId, initiatingSeasonId, media, entries,
                payloadContent, payloadFileName, payloadFormat,
                source, searchSessionId, skipTranslation, cancellationToken);
            if (seasonPack is not null)
            {
                return seasonPack;
            }
        }

        // ① (single-entry path) the one picked entry per the ⑦ rule.
        ExtractedSubtitle entry;
        try
        {
            entry = _extractor.Extract(
                payloadContent, payloadFileName, payloadFormat, candidate.Language, candidate.Provider);
        }
        catch (SubtitleProviderException ex)
        {
            return await HandleExtractionFailureAsync(
                candidate, mediaType, mediaId, source, searchSessionId, ex, cancellationToken);
        }

        // ② encoding → UTF-8 (GBK/GB18030 detection lives in the shared service).
        byte[] utf8;
        try
        {
            utf8 = _encodingService.ConvertToUtf8(entry.Content);
        }
        catch (Exception ex)
        {
            return await RecordAsync(
                candidate, mediaType, mediaId, source, searchSessionId,
                SubtitleDownloadResult.Failed, filePath: null,
                error: $"encoding conversion failed: {ex.Message}");
        }

        // A zero-byte entry or whitespace-only text is a content defect of
        // this candidate, not a transient failure — writing an empty .srt
        // would silently count as coverage. Failed + blacklist, same family
        // as the unparseable-archive case above.
        if (utf8.Length == 0 || string.IsNullOrWhiteSpace(Encoding.UTF8.GetString(utf8)))
        {
            await BlacklistCandidateAsync(candidate, "the downloaded subtitle is empty.", cancellationToken);
            return await RecordAsync(
                candidate, mediaType, mediaId, source, searchSessionId,
                SubtitleDownloadResult.Failed, filePath: null,
                error: "the downloaded subtitle is empty.");
        }

        // ③ optional SRT conversion (default on) with the existing
        // parsers/writers — ass→srt rides the same SsaParser.
        var settings = await _settingService.GetSettings([
            SettingKeys.SubtitleSearch.ConvertToSrt,
            SettingKeys.SubtitleSearch.AutoTranslate
        ]);
        var convertToSrt = settings.GetValueOrDefault(SettingKeys.SubtitleSearch.ConvertToSrt) != "false";
        var autoTranslate = settings.GetValueOrDefault(SettingKeys.SubtitleSearch.AutoTranslate) != "false";

        string outputExtension;
        byte[] outputBytes;
        if (convertToSrt)
        {
            try
            {
                outputBytes = ConvertToSrt(entry.FileName, utf8);
                outputExtension = ".srt";
            }
            catch (Exception ex)
            {
                // The payload arrived intact but no parser can read it —
                // same permanently-unusable-content family as the extractor
                // failures; retrying this external id re-fetches the same
                // bytes.
                await BlacklistCandidateAsync(
                    candidate, $"could not parse '{entry.FileName}' as a subtitle: {ex.Message}", cancellationToken);
                return await RecordAsync(
                    candidate, mediaType, mediaId, source, searchSessionId,
                    SubtitleDownloadResult.Failed, filePath: null,
                    error: $"could not parse '{entry.FileName}' as a subtitle: {ex.Message}");
            }
        }
        else
        {
            outputBytes = utf8;
            outputExtension = ExtensionOf(entry.FileName) is { } extension and not ""
                ? extension
                : ".srt";
        }

        // ④ naming: the same template the translation outputs use, applied
        // to the media video name with the output extension.
        if (media.DirectoryPath is null || media.VideoFileName is null)
        {
            return await RecordAsync(
                candidate, mediaType, mediaId, source, searchSessionId,
                SubtitleDownloadResult.Failed, filePath: null,
                error: "the media item has no file path; nowhere to write the subtitle.");
        }

        string finalPath;
        try
        {
            finalPath = await ComputeOutputPathAsync(
                media.DirectoryPath, media.VideoFileName, candidate.Language, outputExtension);
            await WriteAtomicAsync(finalPath, outputBytes, cancellationToken);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or NotSupportedException)
        {
            return await RecordAsync(
                candidate, mediaType, mediaId, source, searchSessionId,
                SubtitleDownloadResult.Failed, filePath: null,
                error: $"cannot write the subtitle file: {ex.Message}");
        }

        var outcome = await RecordAsync(
            candidate, mediaType, mediaId, source, searchSessionId,
            SubtitleDownloadResult.Succeeded, filePath: finalPath, error: null, filesLanded: 1);

        // ⑥ translation hook: target-language hits are terminal; non-target
        // languages enter the queue when auto_translate is on (content-hash
        // idempotent). skipTranslation (manual download contract) keeps the
        // file but bypasses the queue entirely.
        var translationRequestId = await EnqueueTranslationAsync(
            candidate, mediaType, mediaId, media.Title, finalPath, utf8, autoTranslate, cancellationToken,
            skipTranslation);

        // ⑦ single-media coverage refresh + ⑧ hub broadcast.
        try
        {
            await _coverageRefresher.RefreshAsync(mediaType, mediaId, cancellationToken);
        }
        catch (Exception ex)
        {
            // The file is on disk; the daily scan re-syncs the flags anyway.
            _logger.LogWarning(ex, "Coverage refresh failed for {MediaType} {MediaId}.", mediaType, mediaId);
        }

        // The Files card caches directory scans for a short TTL — drop the
        // directory that just received the file, or the detail page's
        // immediate refetch (the search modal's downloaded event) keeps
        // serving the pre-download list (TranslationJob writeback
        // precedent).
        MediaController.InvalidateSubtitleFileCache(Path.GetDirectoryName(finalPath));

        try
        {
            await _hubContext.Clients.Group("JobProgress").SendAsync(
                "SubtitleDownloaded",
                new
                {
                    mediaType = mediaType.ToString(),
                    mediaId,
                    language = candidate.Language,
                    provider = candidate.Provider,
                    path = finalPath
                },
                cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Subtitle download broadcast failed for {MediaType} {MediaId}.", mediaType, mediaId);
        }

        return outcome with
        {
            TranslationRequestId = translationRequestId.requestId,
            TranslationDuplicate = translationRequestId.duplicate
        };
    }

    private async Task<SubtitleDownloadOutcome> RecordAsync(
        SubtitleCandidate candidate,
        MediaType mediaType,
        int mediaId,
        SubtitleDownloadSource source,
        int? searchSessionId,
        SubtitleDownloadResult result,
        string? filePath,
        string? error,
        int filesLanded = 0)
    {
        var row = new Lingarr.Core.Entities.SubtitleDownload
        {
            MediaType = mediaType,
            MediaId = mediaId,
            Provider = candidate.Provider,
            ExternalId = candidate.ExternalId,
            Language = candidate.Language,
            FilePath = filePath ?? string.Empty,
            MatchScore = candidate.MatchScore,
            HashMatch = candidate.HashMatch,
            Source = source,
            SearchSessionId = searchSessionId,
            Result = result,
            Error = error,
            CreatedAt = DateTimeOffset.UtcNow
        };
        _dbContext.SubtitleDownloads.Add(row);
        await _dbContext.SaveChangesAsync();
        return new SubtitleDownloadOutcome(row.Id, filePath, result, error, FilesLanded: filesLanded);
    }

    /// <summary>
    /// Failure handling shared by the extraction passes (the fan-out's
    /// ExtractAll and the single pick's Extract): content-invalid failures
    /// (unparseable archive/body — raised by the extractor or the adapter's
    /// payload checks, never carrying an HTTP status) are permanent
    /// properties of this candidate — the same external id would serve the
    /// same garbage forever — and blacklist it (locked contract: key =
    /// (provider, external_id), 90-day window). Transport/auth/limit
    /// failures never blacklist. A daily-quota-exhaustion verdict
    /// (OpenSubtitles 406 with remaining:0 — the DailyLimit bucket) cools
    /// the source down until its own reset moment (R3 circuit-breaker; best
    /// effort — a cooldown write must never mask the history row). Every
    /// attempt lands its Failed history row.
    /// </summary>
    private async Task<SubtitleDownloadOutcome> HandleExtractionFailureAsync(
        SubtitleCandidate candidate,
        MediaType mediaType,
        int mediaId,
        SubtitleDownloadSource source,
        int? searchSessionId,
        SubtitleProviderException exception,
        CancellationToken cancellationToken)
    {
        if (exception.Reason == SubtitleProviderFailureReason.InvalidResponse && exception.HttpStatus is null)
        {
            await BlacklistCandidateAsync(candidate, exception.Message, cancellationToken);
        }

        if (exception.Reason == SubtitleProviderFailureReason.DailyLimit)
        {
            await RecordQuotaCooldownSafeAsync(candidate.Provider, exception, cancellationToken);
        }

        return await RecordAsync(
            candidate, mediaType, mediaId, source, searchSessionId,
            SubtitleDownloadResult.Failed, filePath: null,
            error: exception.Message);
    }

    /// <summary>One sibling episode of the initiating season — a fan-out landing target.</summary>
    private sealed record SeasonSibling(
        int Id,
        int EpisodeNumber,
        string? Path,
        string? FileName,
        string Title,
        int SeasonNumber,
        string ShowTitle);

    /// <summary>One fan-out landing attempt: the written path (null = the entry was skipped) plus what the pipeline needs next.</summary>
    private sealed record LandingAttempt(string? Path, byte[]? Utf8, string? Error, bool ContentInvalid);

    /// <summary>One file the season-pack fan-out wrote next to its episode.</summary>
    private sealed record LandedFile(SeasonSibling Episode, string Path, byte[] Utf8);

    /// <summary>
    /// Season-pack fan-out (episode downloads): when at least two extracted
    /// entries parse to an episode number — season-tagged entries counting
    /// only when the tag matches the initiating season — every such entry
    /// lands next to its own sibling episode, each through the full
    /// single-entry treatment (encoding → optional SRT conversion →
    /// naming template for that episode's directory and video name →
    /// unique path → write). Cross-season and unparseable entries are
    /// ignored; the language always stays the candidate's requested one
    /// (entry homing looks at the episode number only).
    /// <para>
    /// The initiating episode never walks away empty-handed: when no entry
    /// homed in on it, the original single-pick <see cref="SubtitleArchiveExtractor.Extract"/>
    /// selection lands there (identical selection rules as before the
    /// fan-out existed).
    /// </para>
    /// <para>
    /// Returns null when the fan-out condition is not met — the caller then
    /// runs the single-entry pipeline, byte-identical to the pre-fan-out
    /// behaviour. On a fan-out run: one history row (FilePath = the
    /// initiating episode's file), one translation enqueue per landed file
    /// (content-hash idempotent, target-language hits terminal,
    /// skipTranslation bypasses the queue), one coverage refresh of the
    /// initiating episode (the refresher maps it to its show), one cache
    /// invalidation per written directory and one progress broadcast.
    /// </para>
    /// </summary>
    private async Task<SubtitleDownloadOutcome?> TryDownloadSeasonPackAsync(
        SubtitleCandidate candidate,
        int mediaId,
        int initiatingSeasonId,
        MediaContext media,
        IReadOnlyList<ExtractedSubtitle> entries,
        byte[] payloadContent,
        string? payloadFileName,
        string? payloadFormat,
        SubtitleDownloadSource source,
        int? searchSessionId,
        bool skipTranslation,
        CancellationToken cancellationToken)
    {
        // Trigger: ≥2 entries parse to an episode number, with every
        // season-tagged one of them belonging to the initiating season.
        // Anything less keeps the single-pick pipeline (null) — untagged
        // and ambiguous packs behave exactly as before.
        var assignable = new List<(ExtractedSubtitle Entry, int EpisodeNumber)>();
        foreach (var entry in entries)
        {
            var parsed = SubtitleFilenameParser.Parse(entry.FileName);
            if (parsed.Episode is int episodeNumber
                && (parsed.Season is null || parsed.Season == media.SeasonNumber))
            {
                assignable.Add((entry, episodeNumber));
            }
        }

        if (assignable.Count < 2)
        {
            return null;
        }

        // Same-season siblings (the initiating episode's SeasonId): episode
        // numbers → landing targets. Projection stays anonymous for EF,
        // mapped client-side to the sibling record (StatisticsJob precedent).
        var siblingRows = await _dbContext.Episodes
            .Where(episode => episode.SeasonId == initiatingSeasonId)
            .Select(episode => new
            {
                episode.Id,
                episode.EpisodeNumber,
                episode.Path,
                episode.FileName,
                episode.Title,
                SeasonNumber = episode.Season.SeasonNumber,
                ShowTitle = episode.Season.Show.Title
            })
            .ToListAsync(cancellationToken);
        var siblingByNumber = new Dictionary<int, SeasonSibling>();
        foreach (var row in siblingRows)
        {
            var sibling = new SeasonSibling(
                row.Id, row.EpisodeNumber, row.Path, row.FileName, row.Title, row.SeasonNumber, row.ShowTitle);
            siblingByNumber.TryAdd(row.EpisodeNumber, sibling);
        }

        // Archive order, first entry wins per episode number (the same
        // first-wins tie rule the single pick applies): duplicate entries
        // for one episode and numbers without a library sibling are skipped.
        var claims = new List<(SeasonSibling Target, ExtractedSubtitle Entry)>();
        var claimedNumbers = new HashSet<int>();
        foreach (var (entry, episodeNumber) in assignable)
        {
            if (claimedNumbers.Contains(episodeNumber)
                || !siblingByNumber.TryGetValue(episodeNumber, out var sibling))
            {
                continue;
            }

            claimedNumbers.Add(episodeNumber);
            claims.Add((sibling, entry));
        }

        // Same conversion settings as the single-entry path.
        var settings = await _settingService.GetSettings([
            SettingKeys.SubtitleSearch.ConvertToSrt,
            SettingKeys.SubtitleSearch.AutoTranslate
        ]);
        var convertToSrt = settings.GetValueOrDefault(SettingKeys.SubtitleSearch.ConvertToSrt) != "false";
        var autoTranslate = settings.GetValueOrDefault(SettingKeys.SubtitleSearch.AutoTranslate) != "false";

        // One landing attempt per claim: a defective entry (empty payload,
        // unparseable format, unwritable directory) is skipped with a log
        // line — one bad file must not sink the episodes whose entries are
        // fine. Content-invalid defects are remembered for the nothing-
        // landed-at-all verdict below.
        var landed = new List<LandedFile>();
        string? lastError = null;
        var contentInvalid = false;
        foreach (var (target, entry) in claims)
        {
            var attempt = await TryLandEntryAsync(
                candidate.Language, target, entry, convertToSrt, cancellationToken);
            if (attempt.Path is null)
            {
                lastError = attempt.Error ?? lastError;
                contentInvalid |= attempt.ContentInvalid;
                _logger.LogWarning(
                    "Season pack entry '{EntryFileName}' could not land on episode {EpisodeId}: {Reason}.",
                    entry.FileName, target.Id, attempt.Error);
                continue;
            }

            landed.Add(new LandedFile(target, attempt.Path, attempt.Utf8!));
        }

        // Initiating-episode fallback: no entry homed in on the episode the
        // download started from (an E01+E03 pack downloaded from E02) — the
        // original single-pick selection lands there instead, so the
        // initiating episode keeps the never-empty-handed contract.
        if (siblingByNumber.Values.FirstOrDefault(sibling => sibling.Id == mediaId) is { } initiating
            && landed.All(file => file.Episode.Id != mediaId))
        {
            try
            {
                var picked = _extractor.Extract(
                    payloadContent, payloadFileName, payloadFormat, candidate.Language, candidate.Provider);
                var attempt = await TryLandEntryAsync(
                    candidate.Language, initiating, picked, convertToSrt, cancellationToken);
                if (attempt.Path is null)
                {
                    lastError = attempt.Error ?? lastError;
                    contentInvalid |= attempt.ContentInvalid;
                    _logger.LogWarning(
                        "Season pack fallback pick could not land on episode {EpisodeId}: {Reason}.",
                        mediaId, attempt.Error);
                }
                else
                {
                    landed.Add(new LandedFile(initiating, attempt.Path, attempt.Utf8!));
                }
            }
            catch (SubtitleProviderException ex)
            {
                lastError = ex.Message;
                contentInvalid |= ex.Reason == SubtitleProviderFailureReason.InvalidResponse
                                  && ex.HttpStatus is null;
            }
        }

        if (landed.Count == 0)
        {
            if (contentInvalid)
            {
                await BlacklistCandidateAsync(
                    candidate, lastError ?? "the downloaded subtitle is empty.", cancellationToken);
            }

            return await RecordAsync(
                candidate, MediaType.Episode, mediaId, source, searchSessionId,
                SubtitleDownloadResult.Failed, filePath: null,
                error: lastError ?? "none of the archive entries could be written.");
        }

        // History row and broadcast stay single: FilePath (and the broadcast
        // path) point at the initiating episode's file when it landed,
        // honestly at the first landed file otherwise.
        var initiatingFile = landed.FirstOrDefault(file => file.Episode.Id == mediaId)?.Path
                             ?? landed[0].Path;
        var outcome = await RecordAsync(
            candidate, MediaType.Episode, mediaId, source, searchSessionId,
            SubtitleDownloadResult.Succeeded, filePath: initiatingFile, error: null,
            filesLanded: landed.Count);

        // Translation enqueue per landed file — the same adjudication the
        // single-entry path runs (content-hash idempotent, target-language
        // hits terminal, skipTranslation bypasses the queue entirely): a
        // non-target-language whole-season pack becomes one import request
        // per episode.
        int? firstRequestId = null;
        var anyDuplicate = false;
        foreach (var file in landed)
        {
            var enqueued = await EnqueueTranslationAsync(
                candidate, MediaType.Episode, file.Episode.Id, TitleOf(file.Episode), file.Path, file.Utf8,
                autoTranslate, cancellationToken, skipTranslation);
            firstRequestId ??= enqueued.requestId;
            anyDuplicate |= enqueued.duplicate;
        }

        // One coverage refresh of the initiating episode (episode → its
        // show is the refresher's own mapping, which rescans every season
        // directory of that show).
        try
        {
            await _coverageRefresher.RefreshAsync(MediaType.Episode, mediaId, cancellationToken);
        }
        catch (Exception ex)
        {
            // The files are on disk; the daily scan re-syncs the flags anyway.
            _logger.LogWarning(ex, "Coverage refresh failed for Episode {MediaId}.", mediaId);
        }

        // The Files card caches directory scans for a short TTL — drop
        // EVERY directory that received a file: a multi-episode pack spans
        // directories, and missing one leaves that detail page serving the
        // pre-download list until the TTL expires (TranslationJob
        // writeback precedent).
        foreach (var directory in landed
                     .Select(file => Path.GetDirectoryName(file.Path))
                     .Where(directory => directory is not null)
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            MediaController.InvalidateSubtitleFileCache(directory);
        }

        // One broadcast (path = the initiating episode's file) — per-episode
        // events would storm the progress hub.
        try
        {
            await _hubContext.Clients.Group("JobProgress").SendAsync(
                "SubtitleDownloaded",
                new
                {
                    mediaType = MediaType.Episode.ToString(),
                    mediaId,
                    language = candidate.Language,
                    provider = candidate.Provider,
                    path = initiatingFile
                },
                cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Subtitle download broadcast failed for Episode {MediaId}.", mediaId);
        }

        return outcome with
        {
            TranslationRequestId = firstRequestId,
            TranslationDuplicate = anyDuplicate
        };
    }

    /// <summary>
    /// Lands one archive entry next to one episode through the single-entry
    /// treatment: encoding → UTF-8, empty-payload guard, optional SRT
    /// conversion, naming template over the episode's directory and video
    /// name, unique path, write. Failures come back as a skipped attempt
    /// (never an exception) with the readable reason;
    /// <see cref="LandingAttempt.ContentInvalid"/> marks the permanently-
    /// unusable-content family (empty/unparseable) for blacklisting when
    /// nothing lands at all.
    /// </summary>
    private async Task<LandingAttempt> TryLandEntryAsync(
        string language,
        SeasonSibling target,
        ExtractedSubtitle entry,
        bool convertToSrt,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(target.Path) || string.IsNullOrEmpty(target.FileName))
        {
            return new LandingAttempt(
                null, null, $"episode {target.Id} has no file path; nowhere to write the subtitle.", false);
        }

        byte[] utf8;
        try
        {
            utf8 = _encodingService.ConvertToUtf8(entry.Content);
        }
        catch (Exception ex)
        {
            return new LandingAttempt(null, null, $"encoding conversion failed: {ex.Message}", false);
        }

        // An empty entry would silently count as coverage — same guard as
        // the single-entry path.
        if (utf8.Length == 0 || string.IsNullOrWhiteSpace(Encoding.UTF8.GetString(utf8)))
        {
            return new LandingAttempt(null, null, "the downloaded subtitle is empty.", true);
        }

        string outputExtension;
        byte[] outputBytes;
        if (convertToSrt)
        {
            try
            {
                outputBytes = ConvertToSrt(entry.FileName, utf8);
                outputExtension = ".srt";
            }
            catch (Exception ex)
            {
                return new LandingAttempt(
                    null, null, $"could not parse '{entry.FileName}' as a subtitle: {ex.Message}", true);
            }
        }
        else
        {
            outputBytes = utf8;
            outputExtension = ExtensionOf(entry.FileName) is { } extension and not ""
                ? extension
                : ".srt";
        }

        try
        {
            var finalPath = await ComputeOutputPathAsync(
                target.Path, target.FileName, language, outputExtension);
            await WriteAtomicAsync(finalPath, outputBytes, cancellationToken);
            return new LandingAttempt(finalPath, utf8, null, false);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or NotSupportedException)
        {
            return new LandingAttempt(null, null, $"cannot write the subtitle file: {ex.Message}", false);
        }
    }

    /// <summary>
    /// The translation-request title of one episode — the same
    /// "Show - S01E02 - Title" shape <see cref="LoadMediaAsync"/> builds.
    /// </summary>
    private static string TitleOf(SeasonSibling episode) =>
        $"{episode.ShowTitle} - S{episode.SeasonNumber:D2}E{episode.EpisodeNumber:D2} - {episode.Title}";

    /// <summary>
    /// Writes or refreshes the blacklist observation for a content-invalid
    /// failure. Locked contract with the search read side (implemented in a
    /// later batch): rows are keyed on (provider, external_id) exact match,
    /// filtered inside a 90-day window — so every auto row carries
    /// <c>expires_at = now + 90d</c> and a repeat failure refreshes the same
    /// row instead of piling up duplicates. Best effort by design: a failed
    /// blacklist write logs and moves on, never masking the history row of
    /// the attempt itself.
    /// </summary>
    private async Task BlacklistCandidateAsync(
        SubtitleCandidate candidate, string reason, CancellationToken cancellationToken)
    {
        try
        {
            var expiresAt = DateTimeOffset.UtcNow.AddDays(BlacklistWindowDays);
            var summary = reason.Length <= BlacklistReasonMaxLength ? reason : reason[..BlacklistReasonMaxLength];

            var existing = await _dbContext.SubtitleSearchBlacklists
                .FirstOrDefaultAsync(blacklist =>
                    blacklist.Provider == candidate.Provider && blacklist.ExternalId == candidate.ExternalId,
                    cancellationToken);
            if (existing is null)
            {
                _dbContext.SubtitleSearchBlacklists.Add(new Lingarr.Core.Entities.SubtitleSearchBlacklist
                {
                    Provider = candidate.Provider,
                    ExternalId = candidate.ExternalId,
                    Reason = summary,
                    ExpiresAt = expiresAt
                });
            }
            else
            {
                existing.Reason = summary;
                existing.ExpiresAt = expiresAt;
            }

            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Could not blacklist {Provider}/{ExternalId} after a content-invalid failure.",
                candidate.Provider, candidate.ExternalId);
        }
    }

    /// <summary>
    /// Quota-exhaustion cooldown recording must never fail the attempt's
    /// history row — the store goes through the settings service; a settings
    /// hiccup logs and moves on (the orchestrator's
    /// RecordCooldownSafeAsync precedent, applied to the download side).
    /// </summary>
    private async Task RecordQuotaCooldownSafeAsync(
        string provider, SubtitleProviderException exception, CancellationToken cancellationToken)
    {
        try
        {
            await _cooldowns.RecordQuotaExhaustedAsync(
                provider, exception.QuotaResetUtc, exception.Message, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not persist the quota cooldown of {Provider}.", provider);
        }
    }

    /// <summary>Media facts the pipeline needs; built client-side so the EF
    /// projections stay anonymous (translatable) — StatisticsJob precedent.
    /// The season coordinates (episode media only) drive the season-pack
    /// fan-out: SeasonId scopes the sibling lookup, SeasonNumber gates the
    /// season-tagged archive entries.</summary>
    private sealed record MediaContext(
        string? DirectoryPath,
        string? VideoFileName,
        string Title,
        int? SeasonId = null,
        int? SeasonNumber = null);

    private async Task<MediaContext?> LoadMediaAsync(
        MediaType mediaType, int mediaId, CancellationToken cancellationToken)
    {
        if (mediaType == MediaType.Movie)
        {
            var movie = await _dbContext.Movies
                .Where(m => m.Id == mediaId)
                .Select(m => new { m.Path, m.FileName, m.Title })
                .FirstOrDefaultAsync(cancellationToken);
            return movie is null ? null : new MediaContext(movie.Path, movie.FileName, movie.Title);
        }

        if (mediaType == MediaType.Episode)
        {
            var episode = await _dbContext.Episodes
                .Where(e => e.Id == mediaId)
                .Select(e => new
                {
                    e.Path,
                    e.FileName,
                    e.Title,
                    e.EpisodeNumber,
                    e.SeasonId,
                    SeasonNumber = e.Season.SeasonNumber,
                    ShowTitle = e.Season.Show.Title
                })
                .FirstOrDefaultAsync(cancellationToken);
            return episode is null
                ? null
                : new MediaContext(
                    episode.Path,
                    episode.FileName,
                    $"{episode.ShowTitle} - S{episode.SeasonNumber:D2}E{episode.EpisodeNumber:D2} - {episode.Title}",
                    episode.SeasonId,
                    episode.SeasonNumber);
        }

        return null;
    }

    /// <summary>
    /// Target-language hits are terminal (dual-path ruling); anything else
    /// (en in the zh-target deployment) enters the translation queue when
    /// auto_translate is on — one request per configured target language,
    /// not just the first (QA3 P3-1). Idempotent per (content hash, target
    /// language) — the import-service dedup rule. With
    /// <paramref name="skipTranslation"/> the file stays on disk but no
    /// request is ever created (manual-download contract).
    /// </summary>
    /// <returns>
    /// The id of the first created request (null when none was created) and
    /// whether at least one target was skipped as an existing duplicate.
    /// </returns>
    private async Task<(int? requestId, bool duplicate)> EnqueueTranslationAsync(
        SubtitleCandidate candidate,
        MediaType mediaType,
        int mediaId,
        string title,
        string filePath,
        byte[] utf8Content,
        bool autoTranslate,
        CancellationToken cancellationToken,
        bool skipTranslation)
    {
        if (skipTranslation)
        {
            return (null, false); // manual override: landed, but no queue
        }

        var targets = (await _settingService.GetSettingAsJson<TargetLanguage>(
                           SettingKeys.Translation.TargetLanguages)
                       ?? [])
            .Select(language => language.Code)
            .ToList();

        if (targets.Any(t => string.Equals(t, candidate.Language, StringComparison.OrdinalIgnoreCase)))
        {
            return (null, false); // zh direct hit: terminal, no translation
        }

        if (!autoTranslate)
        {
            return (null, false);
        }

        int? firstRequestId = null;
        var anyDuplicate = false;
        var contentHash = ComputeContentHash(utf8Content);
        foreach (var target in targets
                     .Where(code => !string.Equals(code, candidate.Language, StringComparison.OrdinalIgnoreCase))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var duplicateId = await _dbContext.TranslationRequests
                .Where(request => request.ContentHash == contentHash && request.TargetLanguage == target)
                .OrderBy(request => request.Id)
                .Select(request => (int?)request.Id)
                .FirstOrDefaultAsync(cancellationToken);
            if (duplicateId is not null)
            {
                anyDuplicate = true;
                continue;
            }

            var requestId = await _translationQueue.CreateImportRequest(
                filePath,
                title,
                candidate.Language,
                target,
                mediaId,
                mediaType,
                contentHash);
            firstRequestId ??= requestId;
        }

        return (firstRequestId, anyDuplicate);
    }

    /// <summary>SHA-256 over LF-normalized text — the import-service hash rule.</summary>
    private static string ComputeContentHash(byte[] utf8Content)
    {
        var text = Encoding.UTF8.GetString(utf8Content);
        var normalized = text.Replace("\r\n", "\n").Replace("\r", "\n");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))
            .ToLowerInvariant();
    }

    private async Task<string> ComputeOutputPathAsync(
        string directoryPath,
        string videoFileName,
        string language,
        string outputExtension)
    {
        // Template input = video name with the OUTPUT extension, so
        // CreateFilePath's language/template expansion runs exactly like it
        // does for translation outputs (which pass the source subtitle
        // path). Only known video extensions are stripped — scene names
        // must survive intact.
        var templateName = ReplaceVideoExtension(videoFileName, outputExtension);
        var templatePath = Path.Combine(directoryPath, templateName);
        return await _subtitleService.CreateFilePath(templatePath, language, string.Empty);
    }

    /// <summary>
    /// "Movie.2023.WEBDL-1080p.mkv" + ".srt" → "Movie.2023.WEBDL-1080p.srt";
    /// a name without a known video extension keeps every dot and simply
    /// gains the extension ("clip.part" → "clip.part.srt").
    /// </summary>
    internal static string ReplaceVideoExtension(string fileName, string newExtension)
    {
        foreach (var extension in VideoExtensions)
        {
            if (fileName.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            {
                return fileName[..^extension.Length] + newExtension;
            }
        }

        return fileName + newExtension;
    }

    /// <summary>
    /// Known subtitle extension of a file name, lowercase with dot; empty
    /// when none matches (never guesses on scene-name dots).
    /// </summary>
    private static string ExtensionOf(string fileName)
    {
        foreach (var extension in new[] { ".srt", ".ass", ".ssa", ".vtt" })
        {
            if (fileName.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            {
                return extension;
            }
        }

        return string.Empty;
    }

    /// <summary>
    /// Converts one subtitle payload to UTF-8 SRT bytes with the existing
    /// parsers/writers (SrtParser/SsaParser/VttParser in, SrtWriter out).
    /// </summary>
    private static byte[] ConvertToSrt(string fileName, byte[] utf8)
    {
        var extension = ExtensionOf(fileName);
        ISubtitleParser parser = extension switch
        {
            ".ass" or ".ssa" => new SsaParser(),
            ".vtt" => new VttParser(),
            _ => new SrtParser()
        };

        using var input = new MemoryStream(utf8);
        var items = parser.ParseStream(input, Encoding.UTF8);

        using var output = new MemoryStream();
        using var writer = new StreamWriter(output, new UTF8Encoding(false));
        var index = 1;
        foreach (var item in items)
        {
            writer.Write(index++);
            writer.Write('\n');
            writer.Write(FormatTimeCode(item.StartTime));
            writer.Write(" --> ");
            writer.Write(FormatTimeCode(item.EndTime));
            writer.Write('\n');
            foreach (var line in item.PlaintextLines.Count > 0 ? item.PlaintextLines : item.Lines)
            {
                writer.Write(line);
                writer.Write('\n');
            }

            writer.Write('\n');
        }

        writer.Flush();
        return output.ToArray();
    }

    private static string FormatTimeCode(int milliseconds) =>
        TimeSpan.FromMilliseconds(milliseconds).ToString(@"hh\:mm\:ss\,fff");

    /// <summary>
    /// Same-name collisions overwrite (user ruling 2026-10-19): re-downloading
    /// the same episode+language replaces the file — the old "(1)" suffix
    /// stacking left players holding stale subtitles while duplicates piled
    /// up. The write goes to a temp sibling first (an extension the scanner
    /// never picks up) and moves over the target atomically, so a crash
    /// mid-write can never leave a half-written subtitle in place of a good
    /// one.
    /// </summary>
    private static async Task WriteAtomicAsync(string path, byte[] bytes, CancellationToken cancellationToken)
    {
        var temporary = path + ".lingarr-partial";
        try
        {
            await File.WriteAllBytesAsync(temporary, bytes, cancellationToken);
            File.Move(temporary, path, overwrite: true);
        }
        catch
        {
            try
            {
                File.Delete(temporary);
            }
            catch (IOException)
            {
                // best-effort cleanup of the abandoned temp file
            }

            throw;
        }
    }

    private static bool Carries(Subtitles subtitle, string language) =>
        (subtitle.Languages.Count > 0 ? subtitle.Languages : [subtitle.Language])
        .Any(l => string.Equals(l, language, StringComparison.OrdinalIgnoreCase));
}
