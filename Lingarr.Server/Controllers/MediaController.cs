using System.Collections.Concurrent;
using Lingarr.Core.Data;
using Lingarr.Core.Entities;
using Lingarr.Core.Enum;
using Lingarr.Server.Models;
using Lingarr.Server.Models.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Lingarr.Server.Modules.Media;
using Lingarr.Server.Modules.Subtitle;
using Lingarr.Server.Modules.SubtitleSearch.Pipeline;
using Lingarr.Server.Modules.Translation;

namespace Lingarr.Server.Controllers;

[ApiController]
[Authorize("LingarrAccess")]
[Route("api/[controller]")]
public class MediaController : ControllerBase
{
    private readonly IMediaService _mediaService;
    private readonly IMediaLibraryService _mediaLibraryService;
    private readonly ISubtitleService _subtitleService;
    private readonly AudioLanguageService _audioLanguageService;
    private readonly LingarrDbContext _dbContext;
    private readonly TargetLanguageResolver _targetLanguageResolver;
    private readonly SubtitleCoverageRefresher _coverageRefresher;
    private readonly IEmbeddedTrackService? _embeddedTrackService;

    // ── subtitle file cache (audit P1-5) ───────────────────────────
    //
    // The detail page's subtitle card re-scans every season directory on
    // every view. The Files list cannot be reconstructed from the statistics
    // snapshot (it is per-language aggregates, not per-file rows), so the
    // last scan of a directory is kept for a short TTL: repeated views
    // within the window serve the cached list, the next view after expiry
    // re-scans once. Entries are read-only once stored.

    private static readonly ConcurrentDictionary<string, SubtitleFileCacheEntry> SubtitleFileCache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Lifetime of a cached directory scan. Settable so tests can expire entries deterministically.</summary>
    public static TimeSpan SubtitleFileCacheTtl { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Drops one directory's cached scan (every directory when null).
    /// Production callers: the translation writeback path (TranslationJob)
    /// drops the directory a freshly translated subtitle landed in, so the
    /// Files card reflects it on the next view instead of after the TTL.
    /// </summary>
    public static void InvalidateSubtitleFileCache(string? directory = null)
    {
        if (directory == null)
        {
            SubtitleFileCache.Clear();
            return;
        }

        SubtitleFileCache.TryRemove(directory, out _);
    }

    private sealed record SubtitleFileCacheEntry(DateTimeOffset ExpiresAt, List<MediaSubtitleFile> Files);

    public MediaController(
        IMediaService mediaService,
        IMediaLibraryService mediaLibraryService,
        ISubtitleService subtitleService,
        AudioLanguageService audioLanguageService,
        LingarrDbContext dbContext,
        TargetLanguageResolver targetLanguageResolver,
        SubtitleCoverageRefresher coverageRefresher,
        IEmbeddedTrackService? embeddedTrackService = null)
    {
        _mediaService = mediaService;
        _mediaLibraryService = mediaLibraryService;
        _subtitleService = subtitleService;
        _audioLanguageService = audioLanguageService;
        _dbContext = dbContext;
        _targetLanguageResolver = targetLanguageResolver;
        _coverageRefresher = coverageRefresher;
        // B04: optional so the pre-B04 test constructors keep compiling;
        // production DI always resolves the scoped implementation.
        _embeddedTrackService = embeddedTrackService;
    }

    /// <summary>
    /// Every external subtitle file that lives next to one media item — the
    /// ground truth behind the coverage chips — plus the embedded subtitle
    /// tracks of the last ffprobe scan. Movies scan their directory, shows
    /// scan every season directory; embedded tracks aggregate over a show's
    /// episodes. Dual-language filenames (scjp, 简日…) report every language
    /// they carry.
    /// </summary>
    /// <response code="200">External files with detected languages, format and size; embedded tracks with normalized language, codec and text-based flag; whether the media has been probed at all.</response>
    /// <response code="404">Unknown media id.</response>
    [HttpGet("{mediaType}/{id}/subtitles")]
    public async Task<ActionResult<MediaSubtitlesResponse>> GetMediaSubtitles(
        MediaType mediaType,
        int id)
    {
        List<string> directories;
        // Media ids whose container files the embedded scan would probe —
        // drives the embeddedScanned flag below.
        List<int> probeableIds;
        // Audio track languages of the media (PRD card A): the movie column
        // directly, shows aggregated over their episodes.
        string[]? audioLanguages;
        if (mediaType == MediaType.Movie)
        {
            var movie = await _dbContext.Movies.FirstOrDefaultAsync(m => m.Id == id);
            if (movie == null) return NotFound();
            directories = string.IsNullOrEmpty(movie.Path) ? [] : [movie.Path];
            probeableIds = string.IsNullOrEmpty(movie.Path) || string.IsNullOrEmpty(movie.FileName)
                ? []
                : [movie.Id];
            audioLanguages = movie.AudioLanguages;
        }
        else
        {
            var show = await _dbContext.Shows
                .Include(s => s.Seasons)
                .FirstOrDefaultAsync(s => s.Id == id);
            if (show == null) return NotFound();
            directories = show.Seasons
                .Where(season => !string.IsNullOrEmpty(season.Path))
                .Select(season => season.Path!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            probeableIds = await _dbContext.Seasons
                .Where(season => season.ShowId == id)
                .SelectMany(season => season.Episodes)
                .Where(episode => !string.IsNullOrEmpty(episode.Path) && !string.IsNullOrEmpty(episode.FileName))
                .Select(episode => episode.Id)
                .ToListAsync();
            audioLanguages = MediaLibraryService.AggregateAudioLanguages(
                await _dbContext.Seasons
                    .Where(season => season.ShowId == id)
                    .SelectMany(season => season.Episodes)
                    .Select(episode => episode.AudioLanguages)
                    .ToListAsync());
        }

        var files = new List<MediaSubtitleFile>();
        foreach (var directory in directories)
        {
            // Audit P1-5: serve the cached scan while it is fresh; only an
            // expired/absent entry hits the disk (the now single-pass
            // GetAllSubtitles from audit P0-4).
            var now = DateTimeOffset.UtcNow;
            if (SubtitleFileCache.TryGetValue(directory, out var cached) && cached.ExpiresAt > now)
            {
                files.AddRange(cached.Files);
                continue;
            }

            var subtitles = await _subtitleService.GetAllSubtitles(directory);
            var scanned = subtitles.Select(subtitle => new MediaSubtitleFile
            {
                FileName = Path.GetFileName(subtitle.Path),
                Directory = Path.GetDirectoryName(subtitle.Path) ?? string.Empty,
                Languages = subtitle.Languages.Count > 0 ? subtitle.Languages : [subtitle.Language],
                Format = subtitle.Format.TrimStart('.'),
                Bytes = System.IO.File.Exists(subtitle.Path) ? new FileInfo(subtitle.Path).Length : 0
            }).ToList();
            SubtitleFileCache[directory] = new SubtitleFileCacheEntry(now + SubtitleFileCacheTtl, scanned);
            files.AddRange(scanned);
        }

        // Embedded tracks of the last scan: movies own their rows directly,
        // shows aggregate over their episodes' rows. The scan-marker rows
        // (StreamIndex -1) never surface — they only answer "was this probed".
        List<MediaSubtitleTrack> trackRows;
        List<int> scannedIds;
        if (mediaType == MediaType.Movie)
        {
            trackRows = await _dbContext.MediaSubtitleTracks
                .Where(track => track.MediaType == MediaType.Movie
                                && track.MediaId == id
                                && track.StreamIndex >= 0)
                .OrderBy(track => track.StreamIndex)
                .ToListAsync();
            scannedIds = await _dbContext.MediaSubtitleTracks
                .Where(track => track.MediaType == MediaType.Movie
                                && track.MediaId == id
                                && track.StreamIndex == MediaSubtitleTrack.ScanMarkerStreamIndex)
                .Select(track => track.MediaId)
                .ToListAsync();
        }
        else
        {
            trackRows = await _dbContext.MediaSubtitleTracks
                .Where(track => track.MediaType == MediaType.Episode
                                && probeableIds.Contains(track.MediaId)
                                && track.StreamIndex >= 0)
                .OrderBy(track => track.MediaId)
                .ThenBy(track => track.StreamIndex)
                .ToListAsync();
            scannedIds = await _dbContext.MediaSubtitleTracks
                .Where(track => track.MediaType == MediaType.Episode
                                && probeableIds.Contains(track.MediaId)
                                && track.StreamIndex == MediaSubtitleTrack.ScanMarkerStreamIndex)
                .Select(track => track.MediaId)
                .Distinct()
                .ToListAsync();
        }

        return Ok(new MediaSubtitlesResponse
        {
            Files = files.OrderBy(f => f.FileName, StringComparer.OrdinalIgnoreCase).ToList(),
            Embedded = trackRows.Select(track => new MediaEmbeddedTrack
            {
                MediaId = track.MediaId,
                StreamIndex = track.StreamIndex,
                Language = track.Language,
                RawLanguage = track.RawLanguage,
                Title = track.Title,
                Codec = track.Codec,
                IsTextBased = track.IsTextBased
            }).ToList(),
            // scanned only once every probeable file carries a scan marker
            EmbeddedScanned = probeableIds.Count > 0
                              && probeableIds.All(probeable => scannedIds.Contains(probeable)),
            AudioLanguages = audioLanguages
        });
    }

    /// <summary>
    /// Manually marks whether a media item needs subtitles (PRD card A):
    /// writes the reversible audio_override and recomputes the effective
    /// flags through the shared <see cref="SubtitleNeedRule"/> so every state
    /// (marked needed / marked not needed / mark cleared back to automatic)
    /// lands exactly where the nightly statistics scan would put it.
    /// </summary>
    /// <param name="mediaType">Movie, Episode or Show (shows cascade to episodes).</param>
    /// <param name="id">Movie/episode id, or the show id for shows.</param>
    /// <param name="request">The needed flag to persist; null clears the mark.</param>
    /// <response code="200">The media's new subtitle-need state.</response>
    /// <response code="404">Unknown media id (or unsupported media type).</response>
    [HttpPost("{mediaType}/{id}/subtitle-need")]
    public async Task<ActionResult<SubtitleNeedResponse>> SetSubtitleNeed(
        MediaType mediaType,
        int id,
        [FromBody] SubtitleNeedRequest request)
    {
        // B08: the need verdict runs on the media's EFFECTIVE target set
        // (per-media override > global) through the shared resolver — the
        // same one the refresher and the daily scan use, so a manual mark
        // still lands exactly where the nightly writers would put it.
        var globalTargets = await _targetLanguageResolver.GetGlobalAsync();

        switch (mediaType)
        {
            case MediaType.Movie:
            {
                var movie = await _dbContext.Movies.FirstOrDefaultAsync(m => m.Id == id);
                if (movie == null) return NotFound();
                var targets = TargetLanguageResolver.Resolve(movie.TargetLanguagesOverride, globalTargets);
                movie.AudioOverride = request.Needed;
                movie.NeedsTranslation = SubtitleNeedRule.NeedsTranslation(
                    movie.AudioOverride, movie.AudioLanguages, CoveredLanguages(movie.SubtitleCoverageJson), targets);
                movie.NoSubtitleNeeded = SubtitleNeedRule.NoSubtitleNeeded(
                    movie.AudioOverride, movie.AudioLanguages, targets);
                await _dbContext.SaveChangesAsync();
                return Ok(new SubtitleNeedResponse
                {
                    NeedsNoSubtitles = !movie.NeedsTranslation,
                    NeedsTranslation = movie.NeedsTranslation,
                    AudioLanguages = movie.AudioLanguages,
                    AudioOverride = movie.AudioOverride
                });
            }

            case MediaType.Episode:
            {
                var episode = await _dbContext.Episodes.FirstOrDefaultAsync(e => e.Id == id);
                if (episode == null) return NotFound();
                // episodes carry no override of their own — they inherit the
                // owning show's column (B08), and no per-episode coverage
                // snapshot either: the rule runs on audio/override alone
                // (conservative) and the next statistics scan refines needs
                // from the real subtitle files
                var showOverride = await _dbContext.Episodes
                    .Where(e => e.Id == id)
                    .Select(e => e.Season.Show.TargetLanguagesOverride)
                    .FirstAsync();
                var targets = TargetLanguageResolver.Resolve(showOverride, globalTargets);
                episode.AudioOverride = request.Needed;
                episode.NeedsTranslation = SubtitleNeedRule.NeedsTranslation(
                    episode.AudioOverride, episode.AudioLanguages, null, targets);
                episode.NoSubtitleNeeded = SubtitleNeedRule.NoSubtitleNeeded(
                    episode.AudioOverride, episode.AudioLanguages, targets);
                await _dbContext.SaveChangesAsync();
                return Ok(new SubtitleNeedResponse
                {
                    NeedsNoSubtitles = !episode.NeedsTranslation,
                    NeedsTranslation = episode.NeedsTranslation,
                    AudioLanguages = episode.AudioLanguages,
                    AudioOverride = episode.AudioOverride
                });
            }

            case MediaType.Show:
            {
                // the show must exist even when it has no episodes yet;
                // the override column rides the same existence check (a
                // present show with NULL override and a missing show both
                // read null here, hence the id-shaped projection)
                var showRow = await _dbContext.Shows
                    .Where(s => s.Id == id)
                    .Select(s => new { s.Id, s.TargetLanguagesOverride })
                    .FirstOrDefaultAsync();
                if (showRow == null) return NotFound();
                var targets = TargetLanguageResolver.Resolve(showRow.TargetLanguagesOverride, globalTargets);
                var episodes = await _dbContext.Episodes
                    .Where(e => e.Season.ShowId == id)
                    .ToListAsync();
                foreach (var episode in episodes)
                {
                    episode.AudioOverride = request.Needed;
                    episode.NeedsTranslation = SubtitleNeedRule.NeedsTranslation(
                        episode.AudioOverride, episode.AudioLanguages, null, targets);
                    episode.NoSubtitleNeeded = SubtitleNeedRule.NoSubtitleNeeded(
                        episode.AudioOverride, episode.AudioLanguages, targets);
                }
                await _dbContext.SaveChangesAsync();
                return Ok(new SubtitleNeedResponse
                {
                    // every episode now carries the override, so the show
                    // needs subtitles exactly when the mark says so; an
                    // episode-less show has neither need nor exemption (its
                    // vacuous aggregate is not "native", matching the list)
                    NeedsNoSubtitles = episodes.Count > 0 && episodes.All(e => !e.NeedsTranslation),
                    NeedsTranslation = episodes.Count > 0 && episodes.Any(e => e.NeedsTranslation),
                    AudioLanguages = MediaLibraryService.AggregateAudioLanguages(
                        episodes.Select(e => e.AudioLanguages)),
                    // an episode-less show has no row to persist the mark on
                    AudioOverride = episodes.Count > 0 ? request.Needed : null
                });
            }

            default:
                return NotFound();
        }
    }

    /// <summary>
    /// Identifies the spoken language of the media's first audio track via
    /// the configured remote ASR (walk-through option C): a 30-second clip
    /// is extracted locally, sent to the service, and the detected language
    /// stored with the manual provenance flag (sync payloads never overwrite
    /// it). With the write-back setting on, the tag is also written into the
    /// MKV container (option D).
    /// </summary>
    /// <response code="200">The identified languages and scope.</response>
    /// <response code="404">Unknown media id.</response>
    /// <response code="409">Identification not configured or the file is missing.</response>
    [HttpPost("{mediaType}/{id}/identify-audio")]
    public async Task<IActionResult> IdentifyAudio(MediaType mediaType, int id)
    {
        try
        {
            var result = await _audioLanguageService.IdentifyAsync(mediaType, id);
            return Ok(new
            {
                languages = result.Languages,
                source = result.Source,
                target = result.TargetDescription,
                writeBack = result.WriteBack
            });
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("not configured"))
        {
            return Conflict(new { message = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(new { message = ex.Message });
        }
    }

    /// <summary>
    /// Manually sets the audio languages of one media item (walk-through
    /// option E); shows cascade to every episode. An empty list resets the
    /// values (and the manual flag) back to automatic/unknown.
    /// </summary>
    [HttpPut("{mediaType}/{id}/audio-languages")]
    public async Task<IActionResult> SetAudioLanguages(
        MediaType mediaType,
        int id,
        [FromBody] AudioLanguagesRequest request)
    {
        try
        {
            var result = await _audioLanguageService.SetManualAsync(mediaType, id, request.Languages ?? []);
            return Ok(new { languages = result.Languages, source = result.Source, target = result.TargetDescription });
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(new { message = ex.Message });
        }
    }

    /// <summary>
    /// Extracts one embedded subtitle track as an external sidecar file
    /// (B04): <c>{videoBase}.{language}.srt</c> next to the video, the same
    /// name shape the translation flow writes. Idempotent — when the target
    /// already exists (non-empty) the call answers 200 with created=false
    /// and leaves the file's mtime untouched. After a fresh extraction the
    /// subtitle-file cache of the media directory is dropped and the
    /// coverage-derived flags are refreshed, so the detail page's file card
    /// and coverage chips reflect the new file on the next read.
    /// </summary>
    /// <param name="mediaType">Movie or Episode — embedded track rows are owned by the container's own media item.</param>
    /// <param name="id">Movie or episode id.</param>
    /// <param name="streamIndex">ffprobe stream index of the track row.</param>
    /// <response code="200">The sidecar path and whether this call created it.</response>
    /// <response code="400">Unsupported media type or the extraction wiring is unavailable.</response>
    /// <response code="404">Unknown media id.</response>
    /// <response code="409">The track cannot be extracted (missing row, graphic track, container gone, ffmpeg failure) — the body carries the readable reason.</response>
    [HttpPost("{mediaType}/{id}/embedded-tracks/{streamIndex}/extract")]
    public async Task<IActionResult> ExtractEmbeddedTrack(MediaType mediaType, int id, int streamIndex)
    {
        if (_embeddedTrackService is null)
        {
            return BadRequest(new { message = "Embedded track extraction is not available." });
        }

        if (mediaType is not (MediaType.Movie or MediaType.Episode))
        {
            return NotFound(new { message = $"Embedded tracks are owned by movies and episodes, not {mediaType}." });
        }

        var exists = mediaType == MediaType.Movie
            ? await _dbContext.Movies.AnyAsync(movie => movie.Id == id)
            : await _dbContext.Episodes.AnyAsync(episode => episode.Id == id);
        if (!exists)
        {
            return NotFound();
        }

        try
        {
            var result = await _embeddedTrackService.ExtractToSidecarAsync(mediaType, id, streamIndex);
            if (result.Created)
            {
                // The file card serves a cached directory scan — drop it so
                // the new sidecar is visible immediately, then let the
                // shared refresher flip coverage/need flags the same way the
                // daily scan would.
                InvalidateSubtitleFileCache(Path.GetDirectoryName(result.Path));
                await _coverageRefresher.RefreshAsync(mediaType, id);
            }

            return Ok(new
            {
                path = result.Path,
                fileName = Path.GetFileName(result.Path),
                created = result.Created
            });
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { message = ex.Message });
        }
        catch (Lingarr.Server.Exceptions.FFprobeUnavailableException ex)
        {
            // No ffmpeg binary in the environment — an actionable 409, not a 500.
            return Conflict(new { message = ex.Message });
        }
    }

    /// <summary>
    /// Sets the per-media target-language override (B08): the code set the
    /// subtitle-need rules resolve against instead of the global
    /// target_languages list. A show write covers every episode (episodes
    /// carry no column of their own); movie writes address the movie.
    /// After the write the media's coverage-derived flags are refreshed
    /// through the shared refresher, so the gap queue and the coverage
    /// badges react on the next read without waiting for the nightly scan.
    /// </summary>
    /// <param name="mediaType">Movie or Show (episodes inherit their show's override).</param>
    /// <param name="id">Movie or show id.</param>
    /// <param name="request">
    /// null languages = clear the override (follow the global list again);
    /// an empty array = the explicit「无目标」state (the media wants no
    /// subtitles — reversible any time, distinct from the manual
    /// not-needed mark); otherwise the exact code set (stored trimmed,
    /// lower-cased, deduplicated, comma-separated).
    /// </param>
    /// <response code="200">The stored override (null = following global) and the media scope it applied to.</response>
    /// <response code="404">Unknown media id or unsupported media type.</response>
    [HttpPut("{mediaType}/{id}/target-languages")]
    public async Task<IActionResult> SetTargetLanguages(
        MediaType mediaType,
        int id,
        [FromBody] TargetLanguagesRequest request)
    {
        string? stored;
        string target;
        switch (mediaType)
        {
            case MediaType.Movie:
            {
                var movie = await _dbContext.Movies.FirstOrDefaultAsync(m => m.Id == id);
                if (movie == null) return NotFound();
                movie.TargetLanguagesOverride = StoreOverride(request);
                stored = movie.TargetLanguagesOverride;
                target = "movie";
                await _dbContext.SaveChangesAsync();
                await _coverageRefresher.RefreshAsync(MediaType.Movie, id);
                break;
            }
            case MediaType.Show:
            {
                var show = await _dbContext.Shows.FirstOrDefaultAsync(s => s.Id == id);
                if (show == null) return NotFound();
                show.TargetLanguagesOverride = StoreOverride(request);
                stored = show.TargetLanguagesOverride;
                target = "show";
                await _dbContext.SaveChangesAsync();
                // the show-level refresh recomputes every episode's flags
                // under the new effective target set
                await _coverageRefresher.RefreshAsync(MediaType.Show, id);
                break;
            }
            default:
                return NotFound();
        }

        return Ok(new
        {
            // null = following the global list; [] = the explicit「无目标」state
            languages = stored is null
                ? (string[]?)null
                : [.. TargetLanguageResolver.ParseStored(stored)],
            source = stored is null ? "global" : "override",
            target
        });
    }

    /// <summary>
    /// Request → stored column value: null request clears to NULL (follow
    /// global); a code list serializes comma-separated, where the EMPTY list
    /// stores "" — the explicit「无目标」state, never NULL.
    /// </summary>
    private static string? StoreOverride(TargetLanguagesRequest request) =>
        request.Languages is null
            ? null
            : TargetLanguageResolver.SerializeOverride(request.Languages);

    /// <summary>
    /// Languages the media already carries subtitle files in, read from the
    /// statistics snapshot's per-language keys. Empty when nothing is stored
    /// — the rule treats that as "nothing covered".
    /// </summary>
    private static List<string> CoveredLanguages(string? subtitleCoverageJson)
    {
        if (string.IsNullOrWhiteSpace(subtitleCoverageJson))
        {
            return [];
        }

        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(subtitleCoverageJson);
            return document.RootElement.EnumerateObject()
                .Select(property => property.Name)
                .ToList();
        }
        catch (System.Text.Json.JsonException)
        {
            return [];
        }
    }

    /// <summary>
    /// Matrix rows for the library screen (A4): subtitle track coverage,
    /// translation state and last activity per media item, paged. All params
    /// optional; a null mediaType mixes shows and movies in one list.
    /// </summary>
    [HttpGet("library")]
    public async Task<ActionResult<PagedResult<MediaLibraryItem>>?> GetLibrary(
        MediaType? mediaType,
        int? id,
        string? searchQuery,
        string? state,
        string? targetLanguage,
        string? orderBy,
        bool ascending = true,
        int pageSize = 20,
        int pageNumber = 1)
    {
        // page size cap: a hostile/huge value must not turn one request into
        // a full-library payload (audit P2)
        pageSize = Math.Clamp(pageSize, 1, 100);

        if (id is not null)
        {
            // media detail page: single-row direct lookup that skips the
            // library aggregation entirely (audit P0-8)
            var item = await _mediaLibraryService.GetLibraryItem(mediaType, id.Value);
            return item is null
                ? NotFound()
                : Ok(new PagedResult<MediaLibraryItem>
                {
                    Items = [item],
                    TotalCount = 1,
                    PageNumber = 1,
                    PageSize = 1
                });
        }

        var value = await _mediaLibraryService.GetLibrary(
            mediaType,
            searchQuery,
            state,
            targetLanguage,
            orderBy,
            ascending,
            pageNumber,
            pageSize);
        return Ok(value);
    }

    /// <summary>
    /// Rough cost estimate for translating the selected media into a target
    /// language (A5): uncovered episodes' source subtitle bytes priced at the
    /// default provider's rate.
    /// </summary>
    [HttpPost("estimate")]
    public async Task<ActionResult<TranslationEstimate>> Estimate([FromBody] LibraryEstimateRequest request)
    {
        var value = await _mediaLibraryService.Estimate(request.Items, request.TargetLanguage);
        return Ok(value);
    }
    
    /// <summary>
    /// Retrieves a paginated list of movies based on optional search criteria and sorting parameters.
    /// </summary>
    /// <param name="searchQuery">An optional search query to filter movies by title or other attributes.</param>
    /// <param name="orderBy">An optional parameter specifying the field to sort by (e.g., "Title", "DateAdded").</param>
    /// <param name="ascending">A boolean indicating whether to sort in ascending order (default is true).</param>
    /// <param name="pageSize">The number of movies to return per page (default is 20).</param>
    /// <param name="pageNumber">The page number to retrieve (default is 1).</param>
    /// <returns>Returns an HTTP 200 OK response with a paginated list of movies.</returns>
    [HttpGet("movies")]
    public async Task<ActionResult<PagedResult<MovieResponse>>> GetMovies(
        string? searchQuery,
        string? orderBy,
        bool ascending = true,
        int pageSize = 20,
        int pageNumber = 1)
    {
        // same host-input clamp as /library (audit P2): a huge page must not
        // turn one request into a full-library payload, and a non-positive
        // page number must not reach the service's Skip arithmetic
        pageSize = Math.Clamp(pageSize, 1, 100);
        pageNumber = Math.Max(1, pageNumber);

        var value = await _mediaService.GetMovies(
            searchQuery,
            orderBy,
            ascending,
            pageNumber,
            pageSize);

        return Ok(value);
    }
    
    /// <summary>
    /// Retrieves a paginated list of shows based on optional search criteria and sorting parameters.
    /// </summary>
    /// <param name="searchQuery">An optional search query to filter shows by title or other attributes.</param>
    /// <param name="orderBy">An optional parameter specifying the field to sort by (e.g., "Title", "DateAdded").</param>
    /// <param name="ascending">A boolean indicating whether to sort in ascending order (default is true).</param>
    /// <param name="pageSize">The number of shows to return per page (default is 20).</param>
    /// <param name="pageNumber">The page number to retrieve (default is 1).</param>
    /// <returns>Returns an HTTP 200 OK response with a paginated list of shows.</returns>
    [HttpGet("shows")]
    public async Task<ActionResult<PagedResult<Show>>> GetShows(
        string? searchQuery,
        string? orderBy,
        bool ascending = true,
        int pageSize = 20,
        int pageNumber = 1)
    {
        // same host-input clamp as /library (audit P2)
        pageSize = Math.Clamp(pageSize, 1, 100);
        pageNumber = Math.Max(1, pageNumber);

        var value = await _mediaService.GetShows(
            searchQuery,
            orderBy,
            ascending,
            pageNumber,
            pageSize);
        return Ok(value);
    }
    
    /// <summary>
    /// Sets the include status of a specified media item for translation.
    /// When include is true, the item will be eligible for translation.
    /// When include is false, the item will be excluded from translation.
    /// </summary>
    /// <param name="request">The request object containing the media type, id, and include status.</param>
    [HttpPost("include")]
    public async Task<ActionResult> SetInclude([FromBody] IncludeRequest request)
    {
        var value = await _mediaService.SetInclude(request.MediaType, request.Id, request.Include);
        return Ok(value);
    }
    
    /// <summary>
    /// Sets the include status of all media items of a given type for translation.
    /// For shows, this cascades to all seasons and episodes.
    /// </summary>
    /// <param name="request">The request object containing the media type and include status.</param>
    [HttpPost("include/all")]
    public async Task<ActionResult> SetIncludeAll([FromBody] IncludeAllRequest request)
    {
        var value = await _mediaService.SetIncludeAll(request.MediaType, request.Include);
        return Ok(value);
    }
    
    /// <summary>
    /// Gets a summary of how many items of a given type are included for translation.
    /// </summary>
    /// <param name="mediaType">The type of media (Movie or Show).</param>
    [HttpGet("include/summary")]
    public async Task<ActionResult<IncludeSummary>> GetIncludeSummary([FromQuery] MediaType mediaType)
    {
        var value = await _mediaService.GetIncludeSummary(mediaType);
        return Ok(value);
    }
    
    /// <summary>
    /// Sets the amount of hours a media file needs to exist before translation is initiated.
    /// </summary>
    /// <param name="request">The request object containing the media type, id, and amount of hours to be set.</param>
    [HttpPost("threshold")]
    public async Task<ActionResult<PagedResult<Show>>> Threshold([FromBody] ThresholdRequest request)
    {
        var value = await _mediaService.Threshold(request.MediaType, request.Id, request.Hours);
        return Ok(value);
    }
}

/// <summary>Body of PUT api/media/{mediaType}/{id}/audio-languages (option E).</summary>
public class AudioLanguagesRequest
{
    /// <summary>Language codes to store ("zh", "en"…); empty resets to unknown.</summary>
    public string[]? Languages { get; set; }
}

/// <summary>
/// Body of PUT api/media/{mediaType}/{id}/target-languages (B08). The null
/// vs empty distinction is the contract: null clears the override (follow
/// the global list again), empty is the explicit「无目标」state.
/// </summary>
public class TargetLanguagesRequest
{
    /// <summary>
    /// Target language codes to pin this media to ("zh", "en"…); null
    /// (or absent) clears the override; an empty array means "no target
    /// languages at all".
    /// </summary>
    public string[]? Languages { get; set; }
}

/// <summary>Body of POST api/media/{mediaType}/{id}/subtitle-need (PRD card A).</summary>
public class SubtitleNeedRequest
{
    /// <summary>
    /// True marks the media as needing subtitles; false as not needing
    /// them; null clears the manual mark so the automatic audio rule
    /// applies again (the three-state audio_override column).
    /// </summary>
    public bool? Needed { get; set; }
}

/// <summary>
/// The media's subtitle-need state after a manual mark: the effective
///「无需字幕」state (the frontend contract field), the needs_translation
/// flag now in force, the audio track languages (null = unknown, shows
/// aggregate over their episodes) and the override just written.
/// </summary>
public class SubtitleNeedResponse
{
    public bool NeedsNoSubtitles { get; set; }
    public bool NeedsTranslation { get; set; }
    public string[]? AudioLanguages { get; set; }
    public bool? AudioOverride { get; set; }
}