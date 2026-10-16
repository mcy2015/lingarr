using System.Text.Json;
using Hangfire;
using Lingarr.Core.Configuration;
using Lingarr.Core.Data;
using Lingarr.Core.Entities;
using Lingarr.Core.Enum;
using Lingarr.Server.Filters;
using Lingarr.Server.Models;
using Lingarr.Server.Models.FileSystem;
using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi.Extensions;
using Lingarr.Server.Modules.Infrastructure;
using Lingarr.Server.Modules.Media;
using Lingarr.Server.Modules.Settings;
using Lingarr.Server.Modules.Subtitle;
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

    private readonly LingarrDbContext _dbContext;
    private readonly ISubtitleService _subtitleService;
    private readonly IScheduleService _scheduleService;
    private readonly ISettingService _settingService;
    private readonly ILogger<StatisticsJob> _logger;
    private readonly int _finalizeChunkSize;

    public StatisticsJob(
        LingarrDbContext dbContext,
        ISubtitleService subtitleService,
        IScheduleService scheduleService,
        ISettingService settingService,
        ILogger<StatisticsJob> logger,
        int? finalizeChunkSize = null)
    {
        _dbContext = dbContext;
        _subtitleService = subtitleService;
        _scheduleService = scheduleService;
        _settingService = settingService;
        _logger = logger;
        _finalizeChunkSize = finalizeChunkSize ?? FinalizeChunkSize;
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
            // stays, like the sync jobs' partial batches.
            await _scheduleService.UpdateJobState(jobName, JobStatus.Failed.GetDisplayName());
            _logger.LogError(ex,
                "An error occurred while computing statistics. Exception details: {ExceptionMessage}, Stack Trace: {StackTrace}",
                ex.Message, ex.StackTrace);
        }
    }

    private async Task RunAsync(string jobName)
    {
        var movieSubtitles = 0;
        var episodeSubtitles = 0;
        var byLanguage = new Dictionary<string, int>();
        // (id, json) pairs whose stored coverage differs from the freshly
        // computed one — written in chunked transactions at the end.
        var movieCoverageUpdates = new List<(int Id, string? Json, bool Needs, bool Native)>();
        var showCoverageUpdates = new List<(int Id, string Json)>();
        // episodes whose stored needs_translation flag flips this run
        // (audit P0-5 / PRD card A).
        var episodeNeedsUpdates = new List<(int Id, bool Needs, bool Native)>();
        await _scheduleService.UpdateJobState(jobName, JobStatus.Processing.GetDisplayName());

        // Subtitle-need inputs: the configured target languages drive both
        // the audio exemption and the coverage gap (contract: audio hits any
        // target → exempt; coverage lacks any target → needs translation).
        var targetLanguages = (await _settingService.GetSettingAsJson<TargetLanguage>(
                                  SettingKeys.Translation.TargetLanguages)
                              ?? new List<TargetLanguage>())
            .Select(language => language.Code)
            .ToList();

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
                movie.NoSubtitleNeeded
            })
            .ToListAsync();

        foreach (var movie in movies)
        {
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
                continue;
            }

            // QA4 P1-2: the unreachable catch(DirectoryNotFoundException) is
            // gone — GetAllSubtitles returns an empty list for a missing
            // directory (pinned by its tests) and its hardened traversal
            // skips unreadable subdirectories instead of throwing, so the
            // per-item guard had nothing left to catch. Anything else
            // bubbles to the top-level handler and fails the job.
            var subtitles = await _subtitleService.GetAllSubtitles(movie.Path);
            movieSubtitles += subtitles.Count;
            var coverage = CoverageFromFiles(subtitles);
            var json = SerializeCoverage(coverage);
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

            // Group by language for language counts — dual-language files
            // (scjp/简日…) count once per language they carry.
            foreach (var language in subtitles.SelectMany(LanguagesOf))
            {
                byLanguage.TryAdd(language, 0);
                byLanguage[language]++;
            }
        }

        var shows = await _dbContext.Shows
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
            // QA4 P3: the nested-path guard only ever needs this show's own
            // already-counted season paths — a single global set made the
            // check O(total seasons) per season across the whole library.
            var processedSeasonPaths = new HashSet<string>();

            // Audit issue #2: ONE scan per season, shared by the coverage
            // merge and the episode/language counting. The old code scanned
            // every season twice (once per loop); with the single-pass
            // GetAllSubtitles this is now one traversal per season instead
            // of eight. The nested-path skip only guards the episode/subtitle
            // totals (files under an already-counted season path must not
            // count twice) — every season still merges its own coverage.
            foreach (var season in show.Seasons)
            {
                if (string.IsNullOrEmpty(season.Path)) continue;

                var subtitles = await _subtitleService.GetAllSubtitles(season.Path);
                scannedAnySeason = true;
                MergeCoverage(coverage, season.Episodes, subtitles, episodeLanguages);

                if (processedSeasonPaths.Any(p => season.Path.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                episodeSubtitles += subtitles.Count;
                processedSeasonPaths.Add(season.Path);

                foreach (var subtitle in subtitles)
                {
                    byLanguage.TryAdd(subtitle.Language, 0);
                    byLanguage[subtitle.Language]++;
                }
            }

            var json = scannedAnySeason ? SerializeCoverage(coverage) : show.SubtitleCoverageJson;
            if (!string.Equals(json, show.SubtitleCoverageJson, StringComparison.Ordinal))
            {
                showCoverageUpdates.Add((show.Id, json!));
            }

            // Per-episode subtitle-need flags (needs is judged per episode;
            // show-level display aggregates over these). Episodes of seasons
            // without a path have no scanned coverage and default to the
            // audio/override inputs only, matching the pathless-movie rule.
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
            }
        }

        // Update statistics
        var stats = await _dbContext.Statistics.SingleOrDefaultAsync();
        if (stats == null)
        {
            stats = new Lingarr.Core.Entities.Statistics();
            _dbContext.Statistics.Add(stats);
        }
        stats.TotalEpisodes = await _dbContext.Episodes.CountAsync();
        stats.TotalMovies = movies.Count;
        stats.TotalSubtitles = movieSubtitles + episodeSubtitles;
        stats.SubtitlesByLanguage = byLanguage;

        // QA4 P2-2: the closing backfills are committed in chunks of
        // _finalizeChunkSize statements instead of one giant transaction —
        // a first full run at 100k scale could hold ~140k UPDATE statements
        // in a single WAL commit. The tracked statistics row commits with
        // the first chunk and is a no-op for the rest (its values are final
        // before the loop); a mid-way failure keeps earlier chunks, and the
        // next run recomputes idempotently.
        var finalizeStatements = new List<Func<Task>>(
            movieCoverageUpdates.Count + showCoverageUpdates.Count + episodeNeedsUpdates.Count);
        foreach (var (id, json, needs, native) in movieCoverageUpdates)
        {
            finalizeStatements.Add(() => _dbContext.Movies
                .Where(movie => movie.Id == id)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(movie => movie.SubtitleCoverageJson, json)
                    .SetProperty(movie => movie.NeedsTranslation, needs)
                    .SetProperty(movie => movie.NoSubtitleNeeded, native)));
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
                    .SetProperty(episode => episode.NoSubtitleNeeded, native)));
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
    /// Movie coverage: every language present counts as one file. Dual-language
    /// files (scjp/简日…) contribute to each language they carry. Audit P2:
    /// each file is stat'ed once and the size reused per language — the old
    /// Where().Sum(BytesOf) re-statted every file for every language.
    /// </summary>
    private static Dictionary<string, MediaLanguageCoverage> CoverageFromFiles(List<Subtitles> subtitles)
    {
        var sizes = subtitles
            .Select(subtitle => (Subtitle: subtitle, Size: BytesOf(subtitle)))
            .ToList();

        var coverage = new Dictionary<string, MediaLanguageCoverage>();
        foreach (var language in sizes.SelectMany(entry => LanguagesOf(entry.Subtitle)).Distinct())
        {
            var slot = coverage.GetValueOrDefault(language) ?? new MediaLanguageCoverage();
            slot.Count++;
            slot.Bytes += sizes
                .Where(entry => LanguagesOf(entry.Subtitle).Contains(language))
                .Sum(entry => entry.Size);
            coverage[language] = slot;
        }

        return coverage;
    }

    /// <summary>
    /// Season coverage merged into the show's running dictionary: a subtitle
    /// belongs to an episode when its base name extends the episode file
    /// name, matching how the translation flow pairs subtitles with media.
    /// Unmatched files still contribute bytes to their language for the
    /// estimate heuristic. <paramref name="episodeLanguages"/> collects the
    /// languages each episode carries — the coverage input of the per-episode
    /// subtitle-need rule.
    /// </summary>
    private static void MergeCoverage(
        Dictionary<string, MediaLanguageCoverage> coverage,
        List<EpisodeProjection> episodes,
        List<Subtitles> subtitles,
        Dictionary<int, HashSet<string>> episodeLanguages)
    {
        // QA4 P3: matchable episodes with extension-safe base names,
        // computed once per season. Entity file names carry no (or a known
        // video) extension while release base names contain dots
        // ("Show.S01E01"), so Path.GetFileNameWithoutExtension would eat
        // ".S01E01"-style segments — only a known video extension is ever
        // stripped (EmbeddedTrackService precedent).
        var matchable = new List<(EpisodeProjection Episode, string BaseName)>();
        foreach (var episode in episodes)
        {
            if (string.IsNullOrEmpty(episode.FileName)) continue;
            matchable.Add((episode, EpisodeBaseName(episode.FileName!)));
        }

        // QA4 P3: the composite int hash key ("id * 31 + language hash") is
        // replaced by a value-tuple key — the hash of a string is randomized
        // per process, so distinct (episode, language) pairs could collide
        // and silently drop coverage counts.
        var coveredEpisodes = new HashSet<(int EpisodeId, string Language)>();
        foreach (var subtitle in subtitles)
        {
            var size = BytesOf(subtitle);
            // QA4 P3: the most specific (longest) matching base name wins, so
            // a subtitle of a versioned release ("…e01.v2.zh.srt") is
            // attributed to the versioned episode instead of the plain one
            // the old FirstOrDefault picked. Genuinely ambiguous duplicates —
            // two episode entities sharing one file name across version
            // directories — cannot be separated by name and stay attributed
            // to the first, as before.
            (EpisodeProjection Episode, string BaseName)? match = null;
            foreach (var candidate in matchable)
            {
                if (!SubtitleMatchesEpisode(subtitle.FileName, candidate.BaseName)) continue;
                if (match == null || candidate.BaseName.Length > match.Value.BaseName.Length)
                {
                    match = candidate;
                }
            }

            var episode = match?.Episode;
            HashSet<string>? episodeSet = null;
            if (episode != null && !episodeLanguages.TryGetValue(episode.Id, out episodeSet))
            {
                episodeSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                episodeLanguages[episode.Id] = episodeSet;
            }
            foreach (var language in LanguagesOf(subtitle))
            {
                episodeSet?.Add(language);
                var slot = coverage.GetValueOrDefault(language) ?? new MediaLanguageCoverage();
                slot.Bytes += size;
                if (episode != null && coveredEpisodes.Add((episode.Id, language)))
                {
                    slot.Count++;
                }
                coverage[language] = slot;
            }
        }
    }

    /// <summary>
    /// Known video container extensions — the only suffixes ever stripped
    /// from an episode file name. Anything after the last dot of a release
    /// base name ("Show.S01E01.WEBDL-1080p") must never be treated as an
    /// extension.
    /// </summary>
    private static readonly string[] VideoNameExtensions =
        [".mkv", ".mp4", ".m4v", ".avi", ".mov", ".wmv", ".mpg", ".mpeg", ".ts", ".m2ts"];

    /// <summary>The episode file name without its known video extension.</summary>
    private static string EpisodeBaseName(string fileName)
    {
        foreach (var extension in VideoNameExtensions)
        {
            if (fileName.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            {
                return fileName[..^extension.Length];
            }
        }

        return fileName;
    }

    /// <summary>The language codes a subtitle file carries ("unknown" when untagged).</summary>
    private static IEnumerable<string> LanguagesOf(Subtitles subtitle) =>
        subtitle.Languages.Count > 0 ? subtitle.Languages : [subtitle.Language];

    /// <summary>File size guarded for existence — a vanished file counts as zero bytes (audit P2).</summary>
    private static long BytesOf(Subtitles subtitle) =>
        File.Exists(subtitle.Path) ? new FileInfo(subtitle.Path).Length : 0;

    /// <summary>Serializes exactly like the entities' SubtitleCoverage setter, so stored JSON stays byte-identical to the old writer.</summary>
    private static string? SerializeCoverage(Dictionary<string, MediaLanguageCoverage>? coverage) =>
        coverage == null ? null : JsonSerializer.Serialize(coverage);

    /// <summary>Counterpart of the entities' SubtitleCoverage getter.</summary>
    private static Dictionary<string, MediaLanguageCoverage>? DeserializeCoverage(string? json) =>
        json == null
            ? null
            : JsonSerializer.Deserialize<Dictionary<string, MediaLanguageCoverage>>(json);

    private static bool SubtitleMatchesEpisode(string subtitleFileName, string episodeBaseName) =>
        subtitleFileName.StartsWith(episodeBaseName + ".", StringComparison.OrdinalIgnoreCase)
        || string.Equals(subtitleFileName, episodeBaseName, StringComparison.OrdinalIgnoreCase);
}
