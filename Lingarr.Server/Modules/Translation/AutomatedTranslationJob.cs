using Hangfire;
using Lingarr.Core.Configuration;
using Lingarr.Core.Data;
using Lingarr.Core.Enum;
using Lingarr.Core.Interfaces;
using Lingarr.Server.Filters;
using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi.Extensions;
using Lingarr.Server.Modules.Infrastructure;
using Lingarr.Server.Modules.Settings;
using Lingarr.Server.Modules.Media;

namespace Lingarr.Server.Modules.Translation;

public class AutomatedTranslationJob
{
    private readonly LingarrDbContext _dbContext;
    private readonly ILogger<AutomatedTranslationJob> _logger;
    private readonly IMediaSubtitleProcessor _mediaSubtitleProcessor;
    private readonly ISettingService _settingService;
    private readonly IScheduleService _scheduleService;
    private int _maxTranslationsPerRun = 10;
    private TimeSpan _defaultMovieAgeThreshold;
    private TimeSpan _defaultShowAgeThreshold;

    // Rotation cursors persisted as settings rows (Provider == null, so the
    // plugin-orphan cleanup leaves them alone). Previously IMemoryCache:
    // process-local state that silently reset the cycle on every restart.
    private const string MovieProcessingIndexKey = "automation_movie_processing_index";
    private const string ShowProcessingIndexKey = "automation_show_processing_index";

    public AutomatedTranslationJob(
        LingarrDbContext dbContext,
        ILogger<AutomatedTranslationJob> logger,
        IMediaSubtitleProcessor mediaSubtitleProcessor,
        IScheduleService scheduleService,
        ISettingService settingService)
    {
        _dbContext = dbContext;
        _logger = logger;
        _settingService = settingService;
        _scheduleService = scheduleService;
        _mediaSubtitleProcessor = mediaSubtitleProcessor;
    }

    [DisableConcurrentExecution(timeoutInSeconds: 10 * 60)]
    [AutomaticRetry(Attempts = 0)]
    [Queue("translation")]
    public async Task Execute()
    {
        var jobName = JobContextFilter.GetCurrentJobTypeName();
        await _scheduleService.UpdateJobState(jobName, JobStatus.Processing.GetDisplayName());

        var settings = await _settingService.GetSettings([
            SettingKeys.Automation.AutomationEnabled,
            SettingKeys.Automation.TranslationCycle,
            SettingKeys.Automation.MaxTranslationsPerRun,
            SettingKeys.Automation.MovieAgeThreshold,
            SettingKeys.Automation.ShowAgeThreshold
        ]);

        if (settings[SettingKeys.Automation.AutomationEnabled] == "false")
        {
            _logger.LogInformation("Automation not enabled, skipping translation automation.");
            return;
        }

        int.TryParse(settings[SettingKeys.Automation.MaxTranslationsPerRun], out int maxTranslations);
        int.TryParse(settings[SettingKeys.Automation.MovieAgeThreshold], out int movieAgeThreshold);
        int.TryParse(settings[SettingKeys.Automation.ShowAgeThreshold], out int showAgeThreshold);

        _maxTranslationsPerRun = maxTranslations;
        _defaultMovieAgeThreshold = TimeSpan.FromHours(movieAgeThreshold);
        _defaultShowAgeThreshold = TimeSpan.FromHours(showAgeThreshold);

        var translationCycle = settings[SettingKeys.Automation.TranslationCycle] == "true" ? "movies" : "shows";
        _logger.LogInformation($"Starting translation cycle for |Green|{translationCycle}|/Green|");

        var translationsPerformed = 0;
        switch (translationCycle)
        {
            case "movies":
                await _settingService.SetSetting(SettingKeys.Automation.TranslationCycle, "false");
                translationsPerformed += await ProcessMovies(_maxTranslationsPerRun);
                if (translationsPerformed < _maxTranslationsPerRun)
                {
                    await ProcessShows(_maxTranslationsPerRun - translationsPerformed);
                }

                break;
            case "shows":
                await _settingService.SetSetting(SettingKeys.Automation.TranslationCycle, "true");
                translationsPerformed += await ProcessShows(_maxTranslationsPerRun);
                if (translationsPerformed < _maxTranslationsPerRun)
                {
                    await ProcessMovies(_maxTranslationsPerRun - translationsPerformed);
                }

                break;
        }

        await _scheduleService.UpdateJobState(jobName, JobStatus.Succeeded.GetDisplayName());
    }

    private bool ShouldProcessMedia(IMedia media, MediaType mediaType, TimeSpan? customAgeThreshold = null)
    {
        if (media.Path == null)
        {
            return false;
        }

        var fileInfo = new FileInfo(media.Path);
        var fileAge = TimeSpan.Zero;
        if (!fileInfo.Exists)
        {
            if (!media.DateAdded.HasValue)
            {
                return false;
            }

            fileAge = DateTimeOffset.UtcNow - media.DateAdded.Value;
        }
        else
        {
            fileAge = DateTimeOffset.UtcNow - new DateTimeOffset(fileInfo.LastWriteTimeUtc);
        }

        var threshold = customAgeThreshold ??
                        (mediaType == MediaType.Movie ? _defaultMovieAgeThreshold : _defaultShowAgeThreshold);

        var fileAgeHours = fileAge.TotalHours;
        var thresholdHours = threshold.TotalHours;
        if (fileAgeHours >= thresholdHours)
        {
            return true;
        }

        _logger.LogInformation(
            "Media {FileName} does not meet age threshold. Age: {Age} hours, Required: {Threshold} hours",
            media.FileName,
            fileAgeHours.ToString("F2"),
            thresholdHours.ToString("F2"));
        return false;
    }

    private async Task<int> ProcessMovies(int limit)
    {
        _logger.LogInformation("Movie Translation job initiated");

        // Audit P0-5: candidates come from the needs_translation flag the
        // statistics scan computes (audio/override/coverage rule), selected
        // with an indexed SQL predicate instead of loading every movie and
        // walking the filesystem per item to discover nothing is missing.
        // Media that needs no translation is never selected; the per-media
        // processing path (hash guard, request creation) is unchanged.
        var movies = await _dbContext.Movies
            .Where(movie => movie.IncludeInTranslation && movie.NeedsTranslation)
            .OrderBy(movie => movie.Id)
            .ToListAsync();

        if (!movies.Any())
        {
            _logger.LogInformation("No translatable movies found.");
            return 0;
        }
        
        // Instead of a random selection based on updatedAt, we will use a cycle so that all shows are processed.
        // Hopefully, this will prevent some shows from not being processed at all.
        var currentIndex = await GetProcessingIndex(MovieProcessingIndexKey);
        if (currentIndex >= movies.Count)
        {
            currentIndex = 0;
            _logger.LogInformation("Movie processing cycle completed. Starting new cycle from the beginning.");
        }
        // Scan movies starting from `currentIndex` until we reach the maximum
        // number of translations or we processed all movies once; this ensures
        // we evaluate past the initial window when few items are eligible.
        _logger.LogInformation(
            "Processing up to {MaxTranslations} movies starting at {StartIndex} out of {TotalCount}",
            limit,
            currentIndex,
            movies.Count);

        var translationsInitiated = 0;
        var scannedMovies = 0;
        var index = currentIndex;

        while (translationsInitiated < limit && scannedMovies < movies.Count)
        {
            var movie = movies[index % movies.Count];
            try
            {
                TimeSpan? threshold = movie.TranslationAgeThreshold.HasValue
                    ? TimeSpan.FromHours(movie.TranslationAgeThreshold.Value)
                    : null;

                if (!ShouldProcessMedia(movie, MediaType.Movie, threshold))
                {
                    continue;
                }

                var isProcessed = await _mediaSubtitleProcessor.ProcessMedia(movie, MediaType.Movie);
                if (isProcessed)
                {
                    translationsInitiated++;
                }
            }
            catch (DirectoryNotFoundException)
            {
                _logger.LogWarning("Directory not found at path: |Red|{Path}|/Red|, skipping subtitle", movie.Path);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Error processing subtitles for movie at path: |Red|{Path}|/Red|, skipping subtitle",
                    movie.Path);
            }
            finally
            {
                index++;
                scannedMovies++;
            }
        }

        var newIndex = index % movies.Count;
        await SetProcessingIndex(MovieProcessingIndexKey, newIndex);

        return translationsInitiated;
    }

    private async Task<int> ProcessShows(int limit)
    {
        _logger.LogInformation("Show Translation job initiated");

        var shows = await _dbContext.Shows
            .Where(show => show.IncludeInTranslation)
            .ToListAsync();

        var seasons = await _dbContext.Seasons
            .Where(season => shows.Select(s => s.Id).Contains(season.ShowId) && season.IncludeInTranslation)
            .ToListAsync();

        var episodes = await _dbContext.Episodes
            .Where(episode =>
                seasons.Select(s => s.Id).Contains(episode.SeasonId)
                && episode.IncludeInTranslation
                // Audit P0-5: per-episode need flag from the statistics scan
                // (shows select through their episodes) — episodes that need
                // no translation never enter the rotation.
                && episode.NeedsTranslation)
            .OrderBy(e => e.Id)
            .ToListAsync();

        if (!episodes.Any())
        {
            _logger.LogInformation("No translatable shows found.");
            return 0;
        }

        // Instead of a random selection based on updatedAt, we will use a cycle so that all shows are processed.
        // Hopefully, this will prevent some shows from not being processed at all.
        var currentIndex = await GetProcessingIndex(ShowProcessingIndexKey);
        if (currentIndex >= episodes.Count)
        {
            currentIndex = 0;
            _logger.LogInformation("Show processing cycle completed. Starting new cycle from the beginning.");
        }
        _logger.LogInformation(
            "Processing up to {MaxTranslations} episodes starting at {StartIndex} out of {TotalCount}",
            limit,
            currentIndex,
            episodes.Count);

        var translationsInitiated = 0;
        var scannedEpisodes = 0;
        var episodeIndex = currentIndex;

        while (translationsInitiated < limit && scannedEpisodes < episodes.Count)
        {
            var episode = episodes[episodeIndex % episodes.Count];

            try
            {
                var season = seasons.FirstOrDefault(s => s.Id == episode.SeasonId);
                var show = shows.FirstOrDefault(s => s.Id == season?.ShowId);

                TimeSpan? threshold = null;
                if (show != null && show.TranslationAgeThreshold.HasValue)
                {
                    threshold = TimeSpan.FromHours(show.TranslationAgeThreshold.Value);
                }

                if (!ShouldProcessMedia(episode, MediaType.Episode, threshold))
                {
                    continue;
                }

                var isProcessed = await _mediaSubtitleProcessor.ProcessMedia(episode, MediaType.Episode);
                if (isProcessed)
                {
                    translationsInitiated++;
                }
            }
            catch (DirectoryNotFoundException)
            {
                _logger.LogWarning("Directory not found for show at path: |Red|{Path}|/Red|, skipping episode",
                    episode.Path);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Error processing subtitles for episode at path: |Red|{Path}|/Red|, skipping episode",
                    episode.Path);
            }
            finally
            {
                episodeIndex++;
                scannedEpisodes++;
            }
        }

        var newIndex = episodeIndex % episodes.Count;
        await SetProcessingIndex(ShowProcessingIndexKey, newIndex);

        return translationsInitiated;
    }

    private async Task<int> GetProcessingIndex(string key)
    {
        var raw = await _settingService.GetSetting(key);
        return int.TryParse(raw, out var index) ? index : 0;
    }

    private Task SetProcessingIndex(string key, int value)
    {
        return _settingService.SetSetting(key, value.ToString());
    }
}