using System.Text.Json;
using Hangfire;
using Lingarr.Core.Data;
using Lingarr.Core.Entities;
using Lingarr.Core.Enum;
using Lingarr.Server.Filters;
using Lingarr.Server.Models.FileSystem;
using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi.Extensions;
using Lingarr.Server.Modules.Infrastructure;
using Lingarr.Server.Modules.Subtitle;
using Lingarr.Server.Modules.Translation;

namespace Lingarr.Server.Modules.Statistics;

public class StatisticsJob
{
    private readonly LingarrDbContext _dbContext;
    private readonly ISubtitleService _subtitleService;
    private readonly IScheduleService _scheduleService;
    private readonly ILogger<StatisticsJob> _logger;

    public StatisticsJob(
        LingarrDbContext dbContext,
        ISubtitleService subtitleService,
        IScheduleService scheduleService,
        ILogger<StatisticsJob> logger)
    {
        _dbContext = dbContext;
        _subtitleService = subtitleService;
        _scheduleService = scheduleService;
        _logger = logger;
    }

    /// <summary>Season columns the aggregation reads (audit P1-11 projection).</summary>
    private sealed record SeasonProjection(int Id, string? Path, List<EpisodeProjection> Episodes);

    /// <summary>Episode columns the aggregation reads — the file-name match only.</summary>
    private sealed record EpisodeProjection(int Id, string? FileName);

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
        var movieCoverageUpdates = new List<(int Id, string? Json)>();
        var showCoverageUpdates = new List<(int Id, string Json)>();
        await _scheduleService.UpdateJobState(jobName, JobStatus.Processing.GetDisplayName());

        // Audit P1-11: projections instead of full entities. The old queries
        // materialized every column of every movie/show/season/episode into
        // the change tracker (peak 200-500MB at 100k scale); these load
        // exactly the columns the aggregation reads and track nothing.
        var movies = await _dbContext.Movies
            .Select(movie => new { movie.Id, movie.Path, movie.SubtitleCoverageJson })
            .ToListAsync();

        foreach (var movie in movies)
        {
            try
            {
                if (movie.Path == null)
                {
                    if (movie.SubtitleCoverageJson != null)
                    {
                        movieCoverageUpdates.Add((movie.Id, null));
                    }
                    continue;
                }

                var subtitles = await _subtitleService.GetAllSubtitles(movie.Path);
                movieSubtitles += subtitles.Count;
                var json = SerializeCoverage(CoverageFromFiles(subtitles));

                // Unchanged rows are not rewritten — matches the old change
                // tracker, which produced no UPDATE for untouched coverage.
                if (!string.Equals(json, movie.SubtitleCoverageJson, StringComparison.Ordinal))
                {
                    movieCoverageUpdates.Add((movie.Id, json));
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
                    season.Episodes.Select(episode => new EpisodeProjection(episode.Id, episode.FileName))
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
                    MergeCoverage(coverage, season.Episodes, subtitles);

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
        // every changed coverage value — a save per entity would mean one
        // WAL commit per media item at 100k scale.
        await using (var transaction = await _dbContext.Database.BeginTransactionAsync())
        {
            foreach (var (id, json) in movieCoverageUpdates)
            {
                await _dbContext.Movies
                    .Where(movie => movie.Id == id)
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(movie => movie.SubtitleCoverageJson, json));
            }

            foreach (var (id, json) in showCoverageUpdates)
            {
                await _dbContext.Shows
                    .Where(show => show.Id == id)
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(show => show.SubtitleCoverageJson, json));
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
    /// estimate heuristic.
    /// </summary>
    private static void MergeCoverage(
        Dictionary<string, MediaLanguageCoverage> coverage,
        List<EpisodeProjection> episodes,
        List<Subtitles> subtitles)
    {
        var coveredEpisodes = new HashSet<int>();
        foreach (var subtitle in subtitles)
        {
            var size = BytesOf(subtitle);
            var episode = episodes.FirstOrDefault(ep =>
                !string.IsNullOrEmpty(ep.FileName)
                && SubtitleMatchesEpisode(subtitle.FileName, ep.FileName));
            foreach (var language in LanguagesOf(subtitle))
            {
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
