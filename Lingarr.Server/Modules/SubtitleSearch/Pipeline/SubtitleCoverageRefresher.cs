using System.Text.Json;
using Lingarr.Core.Data;
using Lingarr.Core.Entities;
using Lingarr.Core.Enum;
using Lingarr.Server.Models;
using Lingarr.Server.Models.FileSystem;
using Lingarr.Server.Modules.Media;
using Lingarr.Server.Modules.Settings;
using Lingarr.Server.Modules.Subtitle;
using Microsoft.EntityFrameworkCore;

namespace Lingarr.Server.Modules.SubtitleSearch.Pipeline;

/// <summary>
/// Single-media coverage refresh after a file lands on disk (download or
/// translation writeback): recomputes exactly the rows the statistics scan
/// owns for this one media item, with the same rules (SubtitleNeedRule), the
/// same coverage shape and the same shared computation
/// (<see cref="SubtitleCoverageMath"/>) — so the stored snapshot never
/// waits for the daily StatisticsJob and never disagrees with it.
/// <list type="bullet">
/// <item>Movie: coverage dictionary from the movie directory scan, needs/
/// no-subtitle flags via the shared rule, one row update.</item>
/// <item>Episode: resolves the owning show and refreshes it the way the
/// daily scan does — every season directory of that show is scanned once,
/// the show-level aggregated coverage JSON is rebuilt from the scan and
/// every episode's need flags are recomputed from the same
/// filename-attributed languages.</item>
/// <item>Show: the same show-level refresh as the episode path, addressed
/// by the show id directly.</item>
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

        switch (mediaType)
        {
            case MediaType.Movie:
                await RefreshMovieAsync(mediaId, targets, cancellationToken);
                break;
            case MediaType.Episode:
            {
                // Episode requests/downloads address an episode id — the show
                // aggregate is what the library card reads, so the refresh
                // targets the owning show (whose season scans recompute this
                // episode's flags too, exactly like the daily scan).
                var showId = await _dbContext.Episodes
                    .Where(episode => episode.Id == mediaId)
                    .Select(episode => (int?)episode.Season.ShowId)
                    .FirstOrDefaultAsync(cancellationToken);
                if (showId is int id)
                {
                    await RefreshShowAsync(id, targets, cancellationToken);
                }
                break;
            }
            case MediaType.Show:
                await RefreshShowAsync(mediaId, targets, cancellationToken);
                break;
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

        // Exactly what the daily scan would write (shared computation): a
        // pathless movie carries no coverage at all (null), not an empty
        // object; a scanned one gets one count/bytes slot per language,
        // dual-language files counting for each language they carry.
        string? coverageJson = null;
        Dictionary<string, MediaLanguageCoverage>.KeyCollection? covered = null;
        if (!string.IsNullOrWhiteSpace(movie.Path))
        {
            var subtitles = await _subtitleService.GetAllSubtitles(movie.Path);
            var coverage = SubtitleCoverageMath.MovieCoverage(subtitles);
            coverageJson = SubtitleCoverageMath.SerializeCoverage(coverage);
            covered = coverage.Keys;
        }

        var needs = SubtitleNeedRule.NeedsTranslation(
            movie.AudioOverride, movie.AudioLanguages, covered, targets);
        var native = SubtitleNeedRule.NoSubtitleNeeded(
            movie.AudioOverride, movie.AudioLanguages, targets);
        if (string.Equals(coverageJson, movie.SubtitleCoverageJson, StringComparison.Ordinal)
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

    /// <summary>
    /// Show-level refresh, the single-show slice of the daily scan: every
    /// season with a path is scanned once, the aggregated coverage JSON is
    /// rebuilt fresh from those scans (a stored snapshot never accumulates)
    /// and every episode's need flags are recomputed from the languages the
    /// filename attribution gives each of them. Unchanged rows are not
    /// rewritten, matching the daily scan's idempotence.
    /// </summary>
    private async Task RefreshShowAsync(
        int showId,
        List<string> targets,
        CancellationToken cancellationToken)
    {
        var show = await _dbContext.Shows
            .Where(s => s.Id == showId)
            .Select(s => new
            {
                s.Id,
                s.SubtitleCoverageJson,
                Seasons = s.Seasons.Select(season => new
                {
                    Path = season.Path,
                    Episodes = season.Episodes.Select(episode => new
                    {
                        episode.Id,
                        episode.FileName,
                        episode.AudioLanguages,
                        episode.AudioOverride,
                        episode.NeedsTranslation,
                        episode.NoSubtitleNeeded
                    }).ToList()
                }).ToList()
            })
            .FirstOrDefaultAsync(cancellationToken);
        if (show is null)
        {
            return;
        }

        var coverage = new Dictionary<string, MediaLanguageCoverage>();
        // Per-episode subtitle languages accumulated by the season scans —
        // the coverage input of each episode's need rule.
        var episodeLanguages = new Dictionary<int, HashSet<string>>();
        var scannedAnySeason = false;
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

        // A show whose seasons all lack paths keeps its stored value (the
        // daily scan's rule): nothing was scanned, so there is nothing
        // fresher to write.
        var coverageJson = scannedAnySeason
            ? SubtitleCoverageMath.SerializeCoverage(coverage)
            : show.SubtitleCoverageJson;
        if (!string.Equals(coverageJson, show.SubtitleCoverageJson, StringComparison.Ordinal))
        {
            await _dbContext.Shows
                .Where(s => s.Id == showId)
                .ExecuteUpdateAsync(
                    setters => setters.SetProperty(s => s.SubtitleCoverageJson, coverageJson),
                    cancellationToken);
        }

        var flagUpdates = new List<(int Id, bool Needs, bool Native)>();
        foreach (var episode in show.Seasons.SelectMany(season => season.Episodes))
        {
            var needs = SubtitleNeedRule.NeedsTranslation(
                episode.AudioOverride,
                episode.AudioLanguages,
                episodeLanguages.GetValueOrDefault(episode.Id),
                targets);
            var native = SubtitleNeedRule.NoSubtitleNeeded(
                episode.AudioOverride, episode.AudioLanguages, targets);
            if (needs != episode.NeedsTranslation || native != episode.NoSubtitleNeeded)
            {
                flagUpdates.Add((episode.Id, needs, native));
            }
        }

        foreach (var (id, needs, native) in flagUpdates)
        {
            await _dbContext.Episodes
                .Where(episode => episode.Id == id)
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(episode => episode.NeedsTranslation, needs)
                        .SetProperty(episode => episode.NoSubtitleNeeded, native),
                    cancellationToken);
        }

        if (!string.Equals(coverageJson, show.SubtitleCoverageJson, StringComparison.Ordinal)
            || flagUpdates.Count > 0)
        {
            _logger.LogDebug(
                "Refreshed coverage of show {ShowId}: {FlagUpdates} episode flag updates.",
                showId, flagUpdates.Count);
        }
    }
}

/// <summary>
/// Episode reference of the shared season-coverage attribution: the id the
/// need flags persist on and the file name a subtitle is matched against.
/// </summary>
internal sealed record EpisodeFileRef(int Id, string? FileName);

/// <summary>
/// The subtitle-coverage computation shared by the daily statistics scan
/// (StatisticsJob) and the single-media <see cref="SubtitleCoverageRefresher"/>
/// — extracted so the two writers of SubtitleCoverageJson can never drift:
/// whatever landed on disk produces byte-identical snapshots from either
/// path. Statistics semantics (see <see cref="MediaLanguageCoverage"/>):
/// movie coverage is 0/1 per language, show coverage counts episodes per
/// language with dual-language files counting for each language they carry.
/// </summary>
internal static class SubtitleCoverageMath
{
    /// <summary>
    /// Movie coverage: every language present counts as one file. Dual-language
    /// files (scjp/简日…) contribute to each language they carry. Audit P2:
    /// each file is stat'ed once and the size reused per language — the old
    /// Where().Sum(BytesOf) re-statted every file for every language.
    /// </summary>
    internal static Dictionary<string, MediaLanguageCoverage> MovieCoverage(List<Subtitles> subtitles)
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
    internal static void MergeSeasonCoverage(
        Dictionary<string, MediaLanguageCoverage> coverage,
        List<EpisodeFileRef> episodes,
        List<Subtitles> subtitles,
        Dictionary<int, HashSet<string>> episodeLanguages)
    {
        // QA4 P3: matchable episodes with extension-safe base names,
        // computed once per season. Entity file names carry no (or a known
        // video) extension while release base names contain dots
        // ("Show.S01E01"), so Path.GetFileNameWithoutExtension would eat
        // ".S01E01"-style segments — only a known video extension is ever
        // stripped (EmbeddedTrackService precedent).
        var matchable = new List<(EpisodeFileRef Episode, string BaseName)>();
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
            (EpisodeFileRef Episode, string BaseName)? match = null;
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
    internal static string EpisodeBaseName(string fileName)
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

    /// <summary>Counterpart of the entities' SubtitleCoverage getter.</summary>
    internal static Dictionary<string, MediaLanguageCoverage>? DeserializeCoverage(string? json) =>
        json == null
            ? null
            : JsonSerializer.Deserialize<Dictionary<string, MediaLanguageCoverage>>(json);

    /// <summary>The language codes a subtitle file carries ("unknown" when untagged).</summary>
    internal static IEnumerable<string> LanguagesOf(Subtitles subtitle) =>
        subtitle.Languages.Count > 0 ? subtitle.Languages : [subtitle.Language];

    /// <summary>File size guarded for existence — a vanished file counts as zero bytes (audit P2).</summary>
    internal static long BytesOf(Subtitles subtitle) =>
        File.Exists(subtitle.Path) ? new FileInfo(subtitle.Path).Length : 0;

    /// <summary>Serializes exactly like the entities' SubtitleCoverage setter, so stored JSON stays byte-identical to the old writer.</summary>
    internal static string? SerializeCoverage(Dictionary<string, MediaLanguageCoverage>? coverage) =>
        coverage == null ? null : JsonSerializer.Serialize(coverage);

    internal static bool SubtitleMatchesEpisode(string subtitleFileName, string episodeBaseName) =>
        subtitleFileName.StartsWith(episodeBaseName + ".", StringComparison.OrdinalIgnoreCase)
        || string.Equals(subtitleFileName, episodeBaseName, StringComparison.OrdinalIgnoreCase);
}
