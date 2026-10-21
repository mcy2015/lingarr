using Hangfire;
using Lingarr.Core.Configuration;
using Lingarr.Core.Data;
using Lingarr.Core.Entities;
using Lingarr.Core.Enum;
using Lingarr.Server.Filters;
using Lingarr.Server.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi.Extensions;
using Lingarr.Server.Modules.Infrastructure;
using Lingarr.Server.Modules.Media;
using Lingarr.Server.Modules.Settings;
using Lingarr.Server.Modules.Subtitle;
using Lingarr.Server.Modules.SubtitleSearch.Pipeline;
using Lingarr.Server.Modules.Translation;

namespace Lingarr.Server.Modules.Statistics;

public class StatisticsJob
{
    /// <summary>
    /// QA4 P2-2: number of closing coverage/need UPDATE statements per
    /// commit. The old single closing transaction could hold ~140k
    /// statements in one WAL commit; chunks of this size keep every commit
    /// bounded while the run stays "a handful of commits", not one per
    /// media item.
    /// </summary>
    public const int FinalizeChunkSize = 5000;

    /// <summary>
    /// R13: per-run incremental scan budget per media type — movies, and
    /// episodes (whose owning shows are then scanned whole). Steady rounds
    /// walk at most this many items of each type however large the library
    /// is, which is what makes the round O(budget) instead of O(library).
    /// Never-checked media (null watermark) is prioritized, then the oldest
    /// watermarks fill the remainder.
    /// </summary>
    public const int IncrementalScanBatchSize = 5000;

    /// <summary>
    /// R13: a media item whose watermark is younger than this stays skipped;
    /// older (or never-checked) items re-enter the round's budget, so a
    /// directory that quietly changed on disk is re-verified roughly weekly
    /// even without a download event to trigger the refresher.
    /// </summary>
    public static readonly TimeSpan CoverageRescanInterval = TimeSpan.FromDays(7);

    /// <summary>
    /// R13: ids per watermark-stamp statement — headroom under SQLite's
    /// parameter limit (EmbeddedTrackService's batched-delete precedent).
    /// </summary>
    private const int WatermarkIdChunkSize = 500;

    private readonly LingarrDbContext _dbContext;
    private readonly ISubtitleService _subtitleService;
    private readonly IScheduleService _scheduleService;
    private readonly ISettingService _settingService;
    private readonly ILogger<StatisticsJob> _logger;
    private readonly int _finalizeChunkSize;
    private readonly int _incrementalScanBatchSize;

    public StatisticsJob(
        LingarrDbContext dbContext,
        ISubtitleService subtitleService,
        IScheduleService scheduleService,
        ISettingService settingService,
        ILogger<StatisticsJob> logger,
        int? finalizeChunkSize = null,
        int? incrementalScanBatchSize = null)
    {
        _dbContext = dbContext;
        _subtitleService = subtitleService;
        _scheduleService = scheduleService;
        _settingService = settingService;
        _logger = logger;
        _finalizeChunkSize = finalizeChunkSize ?? FinalizeChunkSize;
        _incrementalScanBatchSize = incrementalScanBatchSize ?? IncrementalScanBatchSize;
    }

    /// <summary>Season columns the aggregation reads (audit P1-11 projection).</summary>
    private sealed record SeasonProjection(int Id, string? Path, List<EpisodeProjection> Episodes);

    /// <summary>
    /// Episode columns the aggregation reads — the file-name match plus the
    /// subtitle-need inputs (PRD card A / audit P0-5).
    /// </summary>
    private sealed record EpisodeProjection(
        int Id,
        string? FileName,
        string[]? AudioLanguages,
        bool? AudioOverride,
        bool NeedsTranslation,
        bool NoSubtitleNeeded);

    [AutomaticRetry(Attempts = 0)]
    [Queue("system")]
    public async Task Execute()
    {
        var jobName = JobContextFilter.GetCurrentJobTypeName();
        try
        {
            await RunAsync(jobName);
        }
        catch (Exception ex)
        {
            // QA4 P1-2: the whole run is guarded the SyncShowJob way — an
            // exception anywhere below (mid-scan IO on the media tree, a
            // database failure) fails the job state instead of leaving it
            // stuck in Processing while hours of scan results are lost.
            // AutomaticRetry(Attempts = 0) means the next scheduled run
            // starts over; everything committed in earlier finalize chunks
            // stays, like the sync jobs' partial batches. R13: because a
            // failed round never reaches the closing statements, neither
            // the coverage values nor their watermarks land — the items it
            // had selected stay eligible and are retried next round.
            await _scheduleService.UpdateJobState(jobName, JobStatus.Failed.GetDisplayName());
            _logger.LogError(ex, "An error occurred while computing statistics.");
            // Attempts=0 means no retry storm: rethrowing only marks the
            // job Failed in Hangfire storage (persisted on the schedule
            // screen) and feeds the server exception filter's file log.
            // The R13 semantics above are unaffected — items selected by a
            // failed round stay eligible for the next one either way.
            throw;
        }
    }

    private async Task RunAsync(string jobName)
    {
        // (id, json) pairs whose stored coverage differs from the freshly
        // computed one — written in chunked transactions at the end.
        var movieCoverageUpdates = new List<(int Id, string? Json, bool Needs, bool Native)>();
        var showCoverageUpdates = new List<(int Id, string Json)>();
        // episodes whose stored needs_translation flag flips this run
        // (audit P0-5 / PRD card A).
        var episodeNeedsUpdates = new List<(int Id, bool Needs, bool Native)>();
        // R13: items whose scan found everything unchanged — they still get
        // their watermark stamped (the coverage IS verified), in batched
        // id-chunk statements instead of one per row.
        var movieWatermarkOnlyIds = new List<int>();
        var episodeWatermarkOnlyIds = new List<int>();
        // R13: unscanned movies whose need flags drift because the target
        // languages changed — recomputed from the persisted snapshot without
        // touching the disk, and without claiming a fresh watermark.
        var movieFlagOnlyUpdates = new List<(int Id, bool Needs, bool Native)>();
        await _scheduleService.UpdateJobState(jobName, JobStatus.Processing.GetDisplayName());

        // Subtitle-need inputs: the configured target languages drive both
        // the audio exemption and the coverage gap (contract: audio hits any
        // target → exempt; coverage lacks any target → needs translation).
        var targetLanguages = (await _settingService.GetSettingAsJson<TargetLanguage>(
                                  SettingKeys.Translation.TargetLanguages)
                              ?? new List<TargetLanguage>())
            .Select(language => language.Code)
            .ToList();

        // One clock for the whole round: every watermark stamped below
        // carries the same instant, and the staleness cutoff derives from it.
        var utcNow = DateTimeOffset.UtcNow;
        var staleCutoff = utcNow - CoverageRescanInterval;

        // R13 aggregation inputs: per-type per-language incidence counts
        // ("media items carrying a subtitle in the language"). Scanned items
        // contribute their freshly computed coverage; everything else
        // contributes its persisted snapshot — the aggregate therefore never
        // depends on "this round scanned the whole library".
        var movieLanguageCounts = new Dictionary<string, int>();
        var episodeLanguageCounts = new Dictionary<string, int>();

        // Audit P1-11: projections instead of full entities. The old queries
        // materialized every column of every movie/show/season/episode into
        // the change tracker (peak 200-500MB at 100k scale); these load
        // exactly the columns the aggregation reads and track nothing.
        var movies = await _dbContext.Movies
            .Select(movie => new
            {
                movie.Id,
                movie.Path,
                movie.SubtitleCoverageJson,
                movie.AudioLanguages,
                movie.AudioOverride,
                movie.NeedsTranslation,
                movie.NoSubtitleNeeded,
                movie.CoverageCheckedAt
            })
            .ToListAsync();

        // R13 selection — computed from the same projection, so the round
        // adds no second movies query. Bootstrap rule: while no movie of the
        // library has ever been checked, the round scans everything (the
        // migration/first-scan full path); afterwards null watermarks come
        // first, the oldest watermarks fill the rest of the budget.
        var movieBootstrap = movies.All(movie => movie.CoverageCheckedAt == null);
        var scanMovieIds = (movieBootstrap
                ? movies.Select(movie => movie.Id)
                : movies
                    .Where(movie => movie.CoverageCheckedAt == null
                                    || movie.CoverageCheckedAt <= staleCutoff)
                    .OrderByDescending(movie => movie.CoverageCheckedAt == null)
                    .ThenBy(movie => movie.CoverageCheckedAt)
                    .ThenBy(movie => movie.Id)
                    .Take(_incrementalScanBatchSize)
                    .Select(movie => movie.Id))
            .ToHashSet();

        foreach (var movie in movies)
        {
            if (!scanMovieIds.Contains(movie.Id))
            {
                // Fresh watermark: the persisted snapshot vouches for this
                // movie's coverage, so it contributes to the aggregate and
                // its flags without a scan. The flags are still recomputed —
                // target languages change in settings, and the snapshot is
                // all the rule needs for a movie.
                var stored = SubtitleCoverageMath.DeserializeCoverage(movie.SubtitleCoverageJson);
                AddLanguageCounts(movieLanguageCounts, stored);
                var needsFromSnapshot = SubtitleNeedRule.NeedsTranslation(
                    movie.AudioOverride, movie.AudioLanguages, stored?.Keys, targetLanguages);
                var nativeFromSnapshot = SubtitleNeedRule.NoSubtitleNeeded(
                    movie.AudioOverride, movie.AudioLanguages, targetLanguages);
                if (needsFromSnapshot != movie.NeedsTranslation
                    || nativeFromSnapshot != movie.NoSubtitleNeeded)
                {
                    movieFlagOnlyUpdates.Add((movie.Id, needsFromSnapshot, nativeFromSnapshot));
                }
                continue;
            }

            if (movie.Path == null)
            {
                // no files to scan: coverage clears and the need rule
                // runs on audio/override alone (nothing is covered)
                var needsWithoutPath = SubtitleNeedRule.NeedsTranslation(
                    movie.AudioOverride, movie.AudioLanguages, null, targetLanguages);
                var nativeWithoutPath = SubtitleNeedRule.NoSubtitleNeeded(
                    movie.AudioOverride, movie.AudioLanguages, targetLanguages);
                if (movie.SubtitleCoverageJson != null
                    || needsWithoutPath != movie.NeedsTranslation
                    || nativeWithoutPath != movie.NoSubtitleNeeded)
                {
                    movieCoverageUpdates.Add((movie.Id, null, needsWithoutPath, nativeWithoutPath));
                }
                else
                {
                    movieWatermarkOnlyIds.Add(movie.Id);
                }
                continue;
            }

            // QA4 P1-2: the unreachable catch(DirectoryNotFoundException) is
            // gone — GetAllSubtitles returns an empty list for a missing
            // directory (pinned by its tests) and its hardened traversal
            // skips unreadable subdirectories instead of throwing, so the
            // per-item guard had nothing left to catch. Anything else
            // bubbles to the top-level handler and fails the job.
            var subtitles = await _subtitleService.GetAllSubtitles(movie.Path);
            var coverage = SubtitleCoverageMath.MovieCoverage(subtitles);
            var json = SubtitleCoverageMath.SerializeCoverage(coverage);
            var needs = SubtitleNeedRule.NeedsTranslation(
                movie.AudioOverride, movie.AudioLanguages, coverage.Keys, targetLanguages);
            var native = SubtitleNeedRule.NoSubtitleNeeded(
                movie.AudioOverride, movie.AudioLanguages, targetLanguages);

            // Unchanged rows are not rewritten — matches the old change
            // tracker, which produced no UPDATE for untouched coverage.
            if (!string.Equals(json, movie.SubtitleCoverageJson, StringComparison.Ordinal)
                || needs != movie.NeedsTranslation
                || native != movie.NoSubtitleNeeded)
            {
                movieCoverageUpdates.Add((movie.Id, json, needs, native));
            }
            else
            {
                movieWatermarkOnlyIds.Add(movie.Id);
            }

            // The movie's incidence contribution — identical to what the
            // persisted snapshot will carry after the finalize commit.
            AddLanguageCounts(movieLanguageCounts, coverage);
        }

        // R13 episode-side selection. The watermark lives on episodes while
        // the scan unit is the show (one season scan feeds the show coverage
        // merge and every episode's flags), so eligible episodes select
        // their owning shows and each selected show is scanned whole — the
        // per-show rebuild semantics stay exactly the full scan's.
        var episodeWatermarks = await _dbContext.Episodes
            .Select(episode => new { episode.Id, ShowId = episode.Season.ShowId, episode.CoverageCheckedAt })
            .ToListAsync();
        var episodeBootstrap = episodeWatermarks.All(episode => episode.CoverageCheckedAt == null);
        var scanShowIds = (episodeBootstrap
                ? episodeWatermarks.Select(episode => episode.ShowId).Distinct()
                : episodeWatermarks
                    .Where(episode => episode.CoverageCheckedAt == null
                                      || episode.CoverageCheckedAt <= staleCutoff)
                    .OrderByDescending(episode => episode.CoverageCheckedAt == null)
                    .ThenBy(episode => episode.CoverageCheckedAt)
                    .ThenBy(episode => episode.Id)
                    .Take(_incrementalScanBatchSize)
                    .Select(episode => episode.ShowId))
            .Distinct()
            .ToHashSet();

        if (scanMovieIds.Count == 0 && scanShowIds.Count == 0)
        {
            _logger.LogInformation(
                "Statistics scan: every media item carries a fresh coverage watermark — zero disk scans this round.");
        }
        else
        {
            _logger.LogInformation(
                "Statistics scan: {Movies} of {TotalMovies} movies and {Shows} shows selected this round ({Mode}).",
                scanMovieIds.Count,
                movies.Count,
                scanShowIds.Count,
                movieBootstrap && episodeBootstrap ? "bootstrap full scan" : "incremental");
        }

        // Unscanned shows contribute their persisted snapshot to the
        // aggregate — a light two-column query, never the season tree.
        if (!episodeBootstrap)
        {
            var unscannedShowCoverage = await _dbContext.Shows
                .Where(show => !scanShowIds.Contains(show.Id))
                .Select(show => show.SubtitleCoverageJson)
                .ToListAsync();
            foreach (var storedJson in unscannedShowCoverage)
            {
                AddLanguageCounts(episodeLanguageCounts, SubtitleCoverageMath.DeserializeCoverage(storedJson));
            }
        }

        // Bootstrap scans every show — including shows without episodes,
        // whose seasons carry no watermark id to select them by.
        var showsQuery = episodeBootstrap
            ? _dbContext.Shows
            : _dbContext.Shows.Where(show => scanShowIds.Contains(show.Id));
        var shows = await showsQuery
            .Select(show => new
            {
                show.Id,
                show.SubtitleCoverageJson,
                Seasons = show.Seasons.Select(season => new SeasonProjection(
                        season.Id,
                        season.Path,
                        season.Episodes.Select(episode => new EpisodeProjection(
                                episode.Id,
                                episode.FileName,
                                episode.AudioLanguages,
                                episode.AudioOverride,
                                episode.NeedsTranslation,
                                episode.NoSubtitleNeeded))
                            .ToList()))
                    .ToList()
            })
            .ToListAsync();

        foreach (var show in shows)
        {
            // Coverage is recomputed fresh from every season that has a path.
            // The tracked-entity implementation seeded MergeCoverage from the
            // persisted snapshot, so each daily run re-added the same files
            // and the counts accumulated forever (S3 follow-up: computed
            // fresh, second run is now idempotent). A show whose seasons all
            // lack paths keeps its stored value.
            var coverage = new Dictionary<string, MediaLanguageCoverage>();
            var scannedAnySeason = false;
            // Per-episode subtitle languages accumulated by the season scans
            // — the coverage input of each episode's need rule.
            var episodeLanguages = new Dictionary<int, HashSet<string>>();

            // Audit issue #2: ONE scan per season, shared by the coverage
            // merge and the episode attribution. R13: the season scan feeds
            // the persisted show snapshot only — the library aggregate reads
            // those snapshots, so the old nested-path file-count guard has
            // no file totals left to protect (unmatched files carry bytes
            // only, and MergeSeasonCoverage's (episode, language) set keeps
            // a file scanned under nested season paths from counting twice).
            foreach (var season in show.Seasons)
            {
                if (string.IsNullOrEmpty(season.Path)) continue;

                var subtitles = await _subtitleService.GetAllSubtitles(season.Path);
                scannedAnySeason = true;
                SubtitleCoverageMath.MergeSeasonCoverage(
                    coverage,
                    season.Episodes
                        .Select(episode => new EpisodeFileRef(episode.Id, episode.FileName))
                        .ToList(),
                    subtitles,
                    episodeLanguages);
            }

            var json = scannedAnySeason ? SubtitleCoverageMath.SerializeCoverage(coverage) : show.SubtitleCoverageJson;
            if (!string.Equals(json, show.SubtitleCoverageJson, StringComparison.Ordinal))
            {
                showCoverageUpdates.Add((show.Id, json!));
            }

            // Per-episode subtitle-need flags (needs is judged per episode;
            // show-level display aggregates over these). Episodes of seasons
            // without a path have no scanned coverage and default to the
            // audio/override inputs only, matching the pathless-movie rule.
            // Every episode of a scanned show was checked — changed ones
            // carry their flag update, unchanged ones just the watermark.
            foreach (var episode in show.Seasons.SelectMany(season => season.Episodes))
            {
                var needs = SubtitleNeedRule.NeedsTranslation(
                    episode.AudioOverride,
                    episode.AudioLanguages,
                    episodeLanguages.GetValueOrDefault(episode.Id),
                    targetLanguages);
                var native = SubtitleNeedRule.NoSubtitleNeeded(
                    episode.AudioOverride, episode.AudioLanguages, targetLanguages);
                if (needs != episode.NeedsTranslation || native != episode.NoSubtitleNeeded)
                {
                    episodeNeedsUpdates.Add((episode.Id, needs, native));
                }
                else
                {
                    episodeWatermarkOnlyIds.Add(episode.Id);
                }
            }

            AddLanguageCounts(
                episodeLanguageCounts,
                scannedAnySeason ? coverage : SubtitleCoverageMath.DeserializeCoverage(show.SubtitleCoverageJson));
        }

        // ── R13 aggregate: snapshots + table counts ─────────────────────
        // SubtitlesByLanguage becomes "media items carrying a subtitle in
        // the language" (movies plus attributed episodes — the exact input
        // the pipeline KPI subtracts from the media count) and
        // TotalSubtitles the sum of those incidences. The same dictionary
        // is recomputed identically whether this round scanned everything,
        // a slice or nothing, which is what pins the zero-drift guarantee.
        var byLanguage = new Dictionary<string, int>(movieLanguageCounts);
        foreach (var (language, count) in episodeLanguageCounts)
        {
            byLanguage[language] = byLanguage.GetValueOrDefault(language) + count;
        }
        var totalEpisodes = await _dbContext.Episodes.CountAsync();

        // Update statistics
        var stats = await _dbContext.Statistics.SingleOrDefaultAsync();
        if (stats == null)
        {
            stats = new Lingarr.Core.Entities.Statistics();
            _dbContext.Statistics.Add(stats);
        }
        stats.TotalEpisodes = totalEpisodes;
        stats.TotalMovies = movies.Count;
        stats.TotalSubtitles = byLanguage.Values.Sum();
        stats.SubtitlesByLanguage = byLanguage;

        // R13: the daily coverage snapshot (M0009). One row per (UTC day,
        // media type, language) — Covered straight out of the incidence
        // counts above, Total the table counts. Target languages always get
        // rows (a zero-covered target is precisely the interesting state);
        // languages only present in snapshots get theirs too. Same-day runs
        // upsert: the composite PK makes the second write an update.
        var dailyLanguages = new HashSet<string>(targetLanguages, StringComparer.OrdinalIgnoreCase);
        foreach (var language in byLanguage.Keys)
        {
            dailyLanguages.Add(language);
        }
        if (dailyLanguages.Count > 0)
        {
            var today = DateOnly.FromDateTime(utcNow.UtcDateTime);
            var dailyByKey = (await _dbContext.CoverageDailies
                    .Where(row => row.Date == today)
                    .ToListAsync())
                .ToDictionary(row => (row.MediaType, row.Language));
            foreach (var language in dailyLanguages)
            {
                UpsertCoverageDaily(dailyByKey, today, MediaType.Movie,
                    language, movieLanguageCounts.GetValueOrDefault(language), movies.Count);
                UpsertCoverageDaily(dailyByKey, today, MediaType.Episode,
                    language, episodeLanguageCounts.GetValueOrDefault(language), totalEpisodes);
            }
        }

        // QA4 P2-2: the closing backfills are committed in chunks of
        // _finalizeChunkSize statements instead of one giant transaction —
        // a first full run at 100k scale could hold ~140k UPDATE statements
        // in a single WAL commit. The tracked statistics/coverage_daily rows
        // commit with the first chunk and are a no-op for the rest (their
        // values are final before the loop); a mid-way failure keeps earlier
        // chunks, and the next run recomputes idempotently. R13: a changed
        // row's statement stamps its watermark in the same commit as the
        // coverage it vouches for; a failed round stamps none of them.
        var finalizeStatements = new List<Func<Task>>(
            movieCoverageUpdates.Count + showCoverageUpdates.Count + episodeNeedsUpdates.Count
            + movieFlagOnlyUpdates.Count + movieWatermarkOnlyIds.Count + episodeWatermarkOnlyIds.Count);
        foreach (var (id, json, needs, native) in movieCoverageUpdates)
        {
            finalizeStatements.Add(() => _dbContext.Movies
                .Where(movie => movie.Id == id)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(movie => movie.SubtitleCoverageJson, json)
                    .SetProperty(movie => movie.NeedsTranslation, needs)
                    .SetProperty(movie => movie.NoSubtitleNeeded, native)
                    .SetProperty(movie => movie.CoverageCheckedAt, utcNow)));
        }

        foreach (var (id, json) in showCoverageUpdates)
        {
            finalizeStatements.Add(() => _dbContext.Shows
                .Where(show => show.Id == id)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(show => show.SubtitleCoverageJson, json)));
        }

        foreach (var (id, needs, native) in episodeNeedsUpdates)
        {
            finalizeStatements.Add(() => _dbContext.Episodes
                .Where(episode => episode.Id == id)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(episode => episode.NeedsTranslation, needs)
                    .SetProperty(episode => episode.NoSubtitleNeeded, native)
                    .SetProperty(episode => episode.CoverageCheckedAt, utcNow)));
        }

        // Unscanned movies whose flags drifted (target languages changed):
        // values only — the watermark keeps its age, the disk was not read.
        foreach (var (id, needs, native) in movieFlagOnlyUpdates)
        {
            finalizeStatements.Add(() => _dbContext.Movies
                .Where(movie => movie.Id == id)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(movie => movie.NeedsTranslation, needs)
                    .SetProperty(movie => movie.NoSubtitleNeeded, native)));
        }

        // Scanned-but-unchanged items: one watermark statement per id chunk.
        foreach (var ids in movieWatermarkOnlyIds.Chunk(WatermarkIdChunkSize))
        {
            finalizeStatements.Add(() => _dbContext.Movies
                .Where(movie => ids.Contains(movie.Id))
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(movie => movie.CoverageCheckedAt, utcNow)));
        }

        foreach (var ids in episodeWatermarkOnlyIds.Chunk(WatermarkIdChunkSize))
        {
            finalizeStatements.Add(() => _dbContext.Episodes
                .Where(episode => ids.Contains(episode.Id))
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(episode => episode.CoverageCheckedAt, utcNow)));
        }

        var chunks = finalizeStatements.Chunk(_finalizeChunkSize).ToList();
        if (chunks.Count == 0)
        {
            // nothing to backfill — only the statistics row changed
            await _dbContext.SaveChangesAsync();
        }
        else
        {
            foreach (var chunk in chunks)
            {
                await using var transaction = await _dbContext.Database.BeginTransactionAsync();
                foreach (var statement in chunk)
                {
                    await statement();
                }

                await _dbContext.SaveChangesAsync();
                await transaction.CommitAsync();
            }
        }

        await _scheduleService.UpdateJobState(jobName, JobStatus.Succeeded.GetDisplayName());
    }

    /// <summary>
    /// Adds a snapshot's per-language incidence counts to the running
    /// aggregate. Zero-count slots (a language carried only by files the
    /// attribution could not match to an episode) add nothing — the
    /// aggregate answers "which media items carry the language".
    /// </summary>
    private static void AddLanguageCounts(
        Dictionary<string, int> byLanguage,
        Dictionary<string, MediaLanguageCoverage>? coverage)
    {
        if (coverage == null) return;
        foreach (var (language, slot) in coverage)
        {
            if (slot.Count <= 0) continue;
            byLanguage.TryAdd(language, 0);
            byLanguage[language] += slot.Count;
        }
    }

    /// <summary>
    /// R13: creates or refreshes today's (media type, language) coverage
    /// row. Existing rows are tracked entities — they ride the same
    /// SaveChanges as the statistics row instead of needing their own
    /// statement machinery.
    /// </summary>
    private void UpsertCoverageDaily(
        Dictionary<(MediaType MediaType, string Language), CoverageDaily> dailyByKey,
        DateOnly today,
        MediaType mediaType,
        string language,
        int covered,
        int total)
    {
        if (dailyByKey.TryGetValue((mediaType, language), out var row))
        {
            row.Covered = covered;
            row.Total = total;
            return;
        }

        var added = new CoverageDaily
        {
            Date = today,
            MediaType = mediaType,
            Language = language,
            Covered = covered,
            Total = total
        };
        _dbContext.CoverageDailies.Add(added);
        dailyByKey[(mediaType, language)] = added;
    }

    // The coverage computations this scan runs (movie coverage from a
    // directory scan, season merge with episode attribution, serialization)
    // live in the shared SubtitleCoverageMath — the same implementation the
    // single-media SubtitleCoverageRefresher writes with, so the daily scan
    // and the "flip the flag now" path can never produce different
    // snapshots.
}
