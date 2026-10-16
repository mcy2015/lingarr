using System.Text.Json;
using Lingarr.Core.Data;
using Lingarr.Core.Entities;
using Lingarr.Core.Enum;
using Lingarr.Server.Modules.Media;
using Lingarr.Server.Modules.Settings;
using Lingarr.Server.Modules.Subtitle;
using Microsoft.EntityFrameworkCore;

namespace Lingarr.Server.Modules.SubtitleSearch.Pipeline;

/// <summary>
/// Single-media coverage refresh after a download lands (tech design ⑦
/// step ⑦): recomputes exactly the rows the statistics scan owns for this
/// one media item, with the same rules (SubtitleNeedRule) and the same
/// coverage shape — a full-library rescan is the daily StatisticsJob's
/// business, this is the "flip the flag now so the card updates without a
/// page reload" path.
/// <list type="bullet">
/// <item>Movie: coverage dictionary from the movie directory scan, needs/
/// no-subtitle flags via the shared rule, one row update.</item>
/// <item>Episode: the episode's need flags from the subtitles that match
/// its video file base name (the same filename-match the statistics scan
/// uses to attribute season files to episodes).</item>
/// <item>Show-level aggregated coverage (season scan) is deliberately NOT
/// recomputed here — the daily scan owns it; refreshing it would scan
/// whole seasons per download.</item>
/// </list>
/// </summary>
public sealed class SubtitleCoverageRefresher
{
    private readonly LingarrDbContext _dbContext;
    private readonly ISubtitleService _subtitleService;
    private readonly ISettingService _settingService;
    private readonly ILogger<SubtitleCoverageRefresher> _logger;

    public SubtitleCoverageRefresher(
        LingarrDbContext dbContext,
        ISubtitleService subtitleService,
        ISettingService settingService,
        ILogger<SubtitleCoverageRefresher> logger)
    {
        _dbContext = dbContext;
        _subtitleService = subtitleService;
        _settingService = settingService;
        _logger = logger;
    }

    /// <summary>
    /// Refreshes the coverage-derived columns of one media item. Safe to
    /// call for media without a path (nothing to scan, rule runs on
    /// audio/override alone — the statistics scan's pathless-movie branch).
    /// </summary>
    public async Task RefreshAsync(
        MediaType mediaType,
        int mediaId,
        CancellationToken cancellationToken = default)
    {
        var targets = (await _settingService.GetSettingAsJson<Lingarr.Server.Models.TargetLanguage>(
                           Lingarr.Core.Configuration.SettingKeys.Translation.TargetLanguages)
                       ?? [])
            .Select(language => language.Code)
            .ToList();

        if (mediaType == MediaType.Movie)
        {
            await RefreshMovieAsync(mediaId, targets, cancellationToken);
        }
        else if (mediaType == MediaType.Episode)
        {
            await RefreshEpisodeAsync(mediaId, targets, cancellationToken);
        }
    }

    private async Task RefreshMovieAsync(
        int movieId,
        List<string> targets,
        CancellationToken cancellationToken)
    {
        var movie = await _dbContext.Movies
            .Where(m => m.Id == movieId)
            .Select(m => new
            {
                m.Id, m.Path, m.SubtitleCoverageJson,
                m.AudioLanguages, m.AudioOverride, m.NeedsTranslation, m.NoSubtitleNeeded
            })
            .FirstOrDefaultAsync(cancellationToken);
        if (movie is null)
        {
            return;
        }

        // Same coverage shape the statistics scan writes: one count/bytes
        // slot per language, dual-language files counting for each language
        // they carry.
        var coverage = new Dictionary<string, MediaLanguageCoverage>();
        var covered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(movie.Path))
        {
            foreach (var subtitle in await _subtitleService.GetAllSubtitles(movie.Path))
            {
                var bytes = File.Exists(subtitle.Path) ? new FileInfo(subtitle.Path).Length : 0;
                foreach (var language in LanguagesOf(subtitle))
                {
                    covered.Add(language);
                    var slot = coverage.GetValueOrDefault(language) ?? new MediaLanguageCoverage();
                    slot.Count++;
                    slot.Bytes += bytes;
                    coverage[language] = slot;
                }
            }
        }

        var coverageJson = JsonSerializer.Serialize(coverage);
        var needs = SubtitleNeedRule.NeedsTranslation(
            movie.AudioOverride, movie.AudioLanguages, covered, targets);
        var native = SubtitleNeedRule.NoSubtitleNeeded(
            movie.AudioOverride, movie.AudioLanguages, targets);
        if (coverageJson == movie.SubtitleCoverageJson
            && needs == movie.NeedsTranslation
            && native == movie.NoSubtitleNeeded)
        {
            return;
        }

        await _dbContext.Movies
            .Where(m => m.Id == movieId)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(m => m.SubtitleCoverageJson, coverageJson)
                    .SetProperty(m => m.NeedsTranslation, needs)
                    .SetProperty(m => m.NoSubtitleNeeded, native),
                cancellationToken);
        _logger.LogDebug(
            "Refreshed coverage of movie {MovieId}: needs={Needs} native={Native}.",
            movieId, needs, native);
    }

    private async Task RefreshEpisodeAsync(
        int episodeId,
        List<string> targets,
        CancellationToken cancellationToken)
    {
        var episode = await _dbContext.Episodes
            .Where(e => e.Id == episodeId)
            .Select(e => new
            {
                e.Id, e.Path, e.FileName, e.AudioLanguages, e.AudioOverride, e.NeedsTranslation, e.NoSubtitleNeeded
            })
            .FirstOrDefaultAsync(cancellationToken);
        if (episode is null)
        {
            return;
        }

        var covered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(episode.Path) && !string.IsNullOrWhiteSpace(episode.FileName))
        {
            // Path is the episode's directory (EpisodeSync write-side
            // contract); the filename match attributes exactly the files
            // the statistics scan would attribute to this episode.
            foreach (var subtitle in await _subtitleService.GetSubtitles(episode.Path, episode.FileName))
            {
                foreach (var language in LanguagesOf(subtitle))
                {
                    covered.Add(language);
                }
            }
        }

        var needs = SubtitleNeedRule.NeedsTranslation(
            episode.AudioOverride, episode.AudioLanguages, covered, targets);
        var native = SubtitleNeedRule.NoSubtitleNeeded(
            episode.AudioOverride, episode.AudioLanguages, targets);
        if (needs == episode.NeedsTranslation && native == episode.NoSubtitleNeeded)
        {
            return;
        }

        await _dbContext.Episodes
            .Where(e => e.Id == episodeId)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(e => e.NeedsTranslation, needs)
                    .SetProperty(e => e.NoSubtitleNeeded, native),
                cancellationToken);
        _logger.LogDebug(
            "Refreshed coverage flags of episode {EpisodeId}: needs={Needs} native={Native}.",
            episodeId, needs, native);
    }

    /// <summary>The language codes a subtitle file carries ("unknown" when untagged) — the StatisticsJob rule.</summary>
    private static IEnumerable<string> LanguagesOf(Lingarr.Server.Models.FileSystem.Subtitles subtitle) =>
        subtitle.Languages.Count > 0 ? subtitle.Languages : [subtitle.Language];
}
