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
    private readonly LingarrDbContext _dbContext;
    private readonly ISubtitleService _subtitleService;
    private readonly IScheduleService _scheduleService;
    private readonly ISettingService _settingService;
    private readonly ILogger<StatisticsJob> _logger;

    public StatisticsJob(
        LingarrDbContext dbContext,
        ISubtitleService subtitleService,
        IScheduleService scheduleService,
        ISettingService settingService,
        ILogger<StatisticsJob> logger)
    {
        _dbContext = dbContext;
        _subtitleService = subtitleService;
        _scheduleService = scheduleService;
        _settingService = settingService;
        _logger = logger;
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
        var movieSubtitles = 0;
        var episodeSubtitles = 0;
        var byLanguage = new Dictionary<string, int>();
        var processedPaths = new HashSet<string>();
        // (id, json) pairs whose stored coverage differs from the freshly
        // computed one — written in one transaction at the end.
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
            try
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
            catch (DirectoryNotFoundException)
            {
                await _scheduleService.UpdateJobState(jobName, JobStatus.Failed.GetDisplayName());
                _logger.LogWarning("Directory not found for movie {MovieId} at path: {Path}",
                    movie.Id, movie.Path);
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

            // Audit issue #2: ONE scan per season, shared by the coverage
            // merge and the episode/language counting. The old code scanned
            // every season twice (once per loop); with the single-pass
            // GetAllSubtitles this is now one traversal per season instead
            // of eight. The nested-path skip only guards the global totals
            // (files under an already-counted season path must not count
            // twice) — every season still merges its own coverage.
            foreach (var season in show.Seasons)
            {
                if (string.IsNullOrEmpty(season.Path)) continue;

                try
                {
                    var subtitles = await _subtitleService.GetAllSubtitles(season.Path);
                    scannedAnySeason = true;
                    MergeCoverage(coverage, season.Episodes, subtitles, episodeLanguages);

                    if (processedPaths.Any(p => season.Path.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
                    {
                        continue;
                    }

                    episodeSubtitles += subtitles.Count;
                    processedPaths.Add(season.Path);

                    foreach (var subtitle in subtitles)
                    {
                        byLanguage.TryAdd(subtitle.Language, 0);
                        byLanguage[subtitle.Language]++;
                    }
                }
                catch (DirectoryNotFoundException)
                {
                    await _scheduleService.UpdateJobState(jobName, JobStatus.Failed.GetDisplayName());
                    _logger.LogWarning("Directory not found for season at path: {Path}", season.Path);
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

        // Audit P0-6-adjacent: one transaction covers the statistics row and
        // every changed coverage/need value — a save per entity would mean
        // one WAL commit per media item at 100k scale.
        await using (var transaction = await _dbContext.Database.BeginTransactionAsync())
        {
            foreach (var (id, json, needs, native) in movieCoverageUpdates)
            {
                await _dbContext.Movies
                    .Where(movie => movie.Id == id)
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(movie => movie.SubtitleCoverageJson, json)
                        .SetProperty(movie => movie.NeedsTranslation, needs)
                        .SetProperty(movie => movie.NoSubtitleNeeded, native));
            }

            foreach (var (id, json) in showCoverageUpdates)
            {
                await _dbContext.Shows
                    .Where(show => show.Id == id)
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(show => show.SubtitleCoverageJson, json));
            }

            foreach (var (id, needs, native) in episodeNeedsUpdates)
            {
                await _dbContext.Episodes
                    .Where(episode => episode.Id == id)
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(episode => episode.NeedsTranslation, needs)
                        .SetProperty(episode => episode.NoSubtitleNeeded, native));
            }

            await _dbContext.SaveChangesAsync();
            await transaction.CommitAsync();
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
        var coveredEpisodes = new HashSet<int>();
        foreach (var subtitle in subtitles)
        {
            var size = BytesOf(subtitle);
            var episode = episodes.FirstOrDefault(ep =>
                !string.IsNullOrEmpty(ep.FileName)
                && SubtitleMatchesEpisode(subtitle.FileName, ep.FileName));
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
                if (episode != null && coveredEpisodes.Add(episode.Id * 31 + language.GetHashCode()))
                {
                    slot.Count++;
                }
                coverage[language] = slot;
            }
        }
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

    private static bool SubtitleMatchesEpisode(string subtitleFileName, string episodeFileName)
    {
        var baseName = Path.GetFileNameWithoutExtension(episodeFileName);
        return subtitleFileName.StartsWith(baseName + ".", StringComparison.OrdinalIgnoreCase)
               || string.Equals(subtitleFileName, baseName, StringComparison.OrdinalIgnoreCase);
    }
}
