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

    [AutomaticRetry(Attempts = 0)]
    [Queue("system")]
    public async Task Execute()
    {
        var jobName = JobContextFilter.GetCurrentJobTypeName();
        var movieSubtitles = 0;
        var episodeSubtitles = 0;
        var byLanguage = new Dictionary<string, int>();
        var processedPaths = new HashSet<string>();
        await _scheduleService.UpdateJobState(jobName, JobStatus.Processing.GetDisplayName());

        var movies = await _dbContext.Movies.ToListAsync();
        foreach (var movie in movies)
        {
            try
            {
                if (movie.Path == null)
                {
                    movie.SubtitleCoverage = null;
                    continue;
                }
                var subtitles = await _subtitleService.GetAllSubtitles(movie.Path);
                movieSubtitles += subtitles.Count;
                movie.SubtitleCoverage = CoverageFromFiles(subtitles);

                // Group by language for language counts
                foreach (var subtitle in subtitles)
                {
                    byLanguage.TryAdd(subtitle.Language, 0);
                    byLanguage[subtitle.Language]++;
                }
            }
            catch (DirectoryNotFoundException)
            {
                await _scheduleService.UpdateJobState(jobName, JobStatus.Failed.GetDisplayName());
                _logger.LogWarning("Directory not found for movie: {MovieTitle} at path: {Path}",
                    movie.Title, movie.Path);
            }
        }

        var shows = await _dbContext.Shows
            .Include(s => s.Seasons)
            .ThenInclude(season => season.Episodes)
            .ToListAsync();

        foreach (var show in shows)
        {
            // Per-season coverage: episodes are attributed by matching the
            // subtitle base name against the episode file name, so every
            // season is scanned on its own (the global totals below keep the
            // nested-path skip to avoid double counting files).
            foreach (var season in show.Seasons)
            {
                if (string.IsNullOrEmpty(season.Path)) continue;

                try
                {
                    var subtitles = await _subtitleService.GetAllSubtitles(season.Path);
                    MergeCoverage(show, season, subtitles);
                }
                catch (DirectoryNotFoundException)
                {
                    _logger.LogWarning("Directory not found for season at path: {Path}", season.Path);
                }
            }

            foreach (var season in show.Seasons)
            {
                if (string.IsNullOrEmpty(season.Path)) continue;

                if (processedPaths.Any(p => season.Path.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                try
                {
                    var subtitles = await _subtitleService.GetAllSubtitles(season.Path);
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
        await _dbContext.SaveChangesAsync();
        await _scheduleService.UpdateJobState(jobName, JobStatus.Succeeded.GetDisplayName());
    }

    /// <summary>Movie coverage: every language present counts as one file.</summary>
    private static Dictionary<string, MediaLanguageCoverage> CoverageFromFiles(List<Subtitles> subtitles)
    {
        var coverage = new Dictionary<string, MediaLanguageCoverage>();
        foreach (var group in subtitles.GroupBy(s => s.Language))
        {
            coverage[group.Key] = new MediaLanguageCoverage
            {
                Count = 1,
                Bytes = SumBytes(group)
            };
        }
        return coverage;
    }

    /// <summary>
    /// Season coverage merged into the show: a subtitle belongs to an episode
    /// when its base name extends the episode file name, matching how the
    /// translation flow pairs subtitles with media. Unmatched files still
    /// contribute bytes to their language for the estimate heuristic.
    /// </summary>
    private static void MergeCoverage(Show show, Season season, List<Subtitles> subtitles)
    {
        var coverage = show.SubtitleCoverage ?? new Dictionary<string, MediaLanguageCoverage>();
        var coveredEpisodes = new HashSet<int>();
        foreach (var subtitle in subtitles)
        {
            var size = new FileInfo(subtitle.Path).Length;
            var slot = coverage.GetValueOrDefault(subtitle.Language) ?? new MediaLanguageCoverage();
            slot.Bytes += size;
            var episode = season.Episodes.FirstOrDefault(ep =>
                !string.IsNullOrEmpty(ep.FileName)
                && SubtitleMatchesEpisode(subtitle.FileName, ep.FileName));
            if (episode != null && coveredEpisodes.Add(episode.Id))
            {
                slot.Count++;
            }
            coverage[subtitle.Language] = slot;
        }
        show.SubtitleCoverage = coverage;
    }

    private static bool SubtitleMatchesEpisode(string subtitleFileName, string episodeFileName)
    {
        var baseName = Path.GetFileNameWithoutExtension(episodeFileName);
        return subtitleFileName.StartsWith(baseName + ".", StringComparison.OrdinalIgnoreCase)
               || string.Equals(subtitleFileName, baseName, StringComparison.OrdinalIgnoreCase);
    }

    private static long SumBytes(IEnumerable<Subtitles> subtitles) =>
        subtitles.Sum(s => File.Exists(s.Path) ? new FileInfo(s.Path).Length : 0);
}
