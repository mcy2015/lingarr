using Lingarr.Core.Configuration;
using Lingarr.Core.Data;
using Lingarr.Core.Entities;
using Lingarr.Core.Enum;
using Lingarr.Server.Models;
using Lingarr.Server.Models.Api;
using Lingarr.Server.Modules.Translation;
using Microsoft.EntityFrameworkCore;
using Lingarr.Server.Modules.Settings;

namespace Lingarr.Server.Modules.Media;

/// <summary>
/// Aggregations behind the media library matrix (A4) and its tray estimate.
/// Track coverage comes from the statistics scan's per-media snapshot; state
/// and last activity from the newest request per episode/movie. With a
/// coverage or state filter active the whole (DB-only) result set is
/// aggregated before paging so totals remain truthful.
/// </summary>
public class MediaLibraryService : IMediaLibraryService
{
    private readonly LingarrDbContext _dbContext;
    private readonly ISettingService _settingService;
    private readonly ICostService _costService;
    private readonly ILogger<MediaLibraryService> _logger;

    public MediaLibraryService(
        LingarrDbContext dbContext,
        ISettingService settingService,
        ICostService costService,
        ILogger<MediaLibraryService> logger)
    {
        _dbContext = dbContext;
        _settingService = settingService;
        _costService = costService;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<PagedResult<MediaLibraryItem>> GetLibrary(
        MediaType? mediaType,
        string? searchQuery,
        string? state,
        string? targetLanguage,
        string? orderBy,
        bool ascending,
        int pageNumber,
        int pageSize)
    {
        var wantShows = mediaType is null or MediaType.Show;
        var wantMovies = mediaType is null or MediaType.Movie;

        // Tracking query: Episodes carry a required Season navigation back,
        // which no-tracking includes reject as a cycle.
        var showQuery = _dbContext.Shows
            .Include(s => s.Seasons)
            .ThenInclude(season => season.Episodes)
            .AsQueryable();
        var movieQuery = _dbContext.Movies
            .AsNoTracking()
            .AsQueryable();

        if (!string.IsNullOrEmpty(searchQuery))
        {
            var needle = searchQuery.ToLower();
            if (wantShows) showQuery = showQuery.Where(s => s.Title.ToLower().Contains(needle));
            if (wantMovies) movieQuery = movieQuery.Where(m => m.Title.ToLower().Contains(needle));
        }

        var showList = wantShows ? await showQuery.ToListAsync() : new List<Show>();
        var movieList = wantMovies ? await movieQuery.ToListAsync() : new List<Movie>();

        // Newest request per media drives the state column and last activity.
        var episodeIds = showList
            .SelectMany(s => s.Seasons.SelectMany(season => season.Episodes.Select(e => e.Id)))
            .ToHashSet();
        var movieIds = movieList.Select(m => m.Id).ToHashSet();
        var latestByMedia = await LatestStatusByMediaAsync(episodeIds, movieIds);

        var targetLanguages = await _settingService.GetSettingAsJson<TargetLanguage>(
            SettingKeys.Translation.TargetLanguages) ?? new List<TargetLanguage>();

        var items = new List<MediaLibraryItem>();
        foreach (var show in showList)
        {
            var item = BuildShowItem(show, latestByMedia, targetLanguages);
            if (MatchesFilters(item, state, targetLanguage))
            {
                items.Add(item);
            }
        }
        foreach (var movie in movieList)
        {
            var item = BuildMovieItem(movie, latestByMedia, targetLanguages);
            if (MatchesFilters(item, state, targetLanguage))
            {
                items.Add(item);
            }
        }

        var ordered = Order(items, orderBy, ascending);
        return new PagedResult<MediaLibraryItem>
        {
            Items = Paged(ordered, pageNumber, pageSize),
            TotalCount = ordered.Count,
            PageNumber = pageNumber,
            PageSize = pageSize
        };
    }

    private static List<MediaLibraryItem> Paged(List<MediaLibraryItem> ordered, int pageNumber, int pageSize) =>
        ordered
            .Skip((pageNumber - 1) * pageSize)
            .Take(pageSize)
            .ToList();

    private MediaLibraryItem BuildShowItem(
        Show show,
        IReadOnlyDictionary<(MediaType Type, int Id), TranslationRequest> latestByMedia,
        IReadOnlyList<TargetLanguage> targetLanguages)
    {
        var episodes = show.Seasons.SelectMany(season => season.Episodes).ToList();
        var coverage = show.SubtitleCoverage ?? new Dictionary<string, MediaLanguageCoverage>();
        var (state, completed, failed, lastActivity) = AggregateState(
            episodes.Select(e => latestByMedia.GetValueOrDefault((MediaType.Show, e.Id))));

        return new MediaLibraryItem
        {
            Id = show.Id,
            MediaType = MediaType.Show,
            Title = show.Title,
            DateAdded = show.DateAdded,
            SeasonCount = show.Seasons.Count,
            EpisodeCount = episodes.Count,
            Tracks = BuildTracks(coverage, episodes.Count, targetLanguages),
            State = state,
            CompletedCount = completed,
            FailedCount = failed,
            LastActivityAt = lastActivity,
            IncludeInTranslation = show.IncludeInTranslation,
            TranslationAgeThreshold = show.TranslationAgeThreshold,
            Show = show
        };
    }

    private MediaLibraryItem BuildMovieItem(
        Movie movie,
        IReadOnlyDictionary<(MediaType Type, int Id), TranslationRequest> latestByMedia,
        IReadOnlyList<TargetLanguage> targetLanguages)
    {
        var coverage = movie.SubtitleCoverage ?? new Dictionary<string, MediaLanguageCoverage>();
        var (state, completed, failed, lastActivity) = AggregateState(
            [latestByMedia.GetValueOrDefault((MediaType.Movie, movie.Id))]);

        return new MediaLibraryItem
        {
            Id = movie.Id,
            MediaType = MediaType.Movie,
            Title = movie.Title,
            DateAdded = movie.DateAdded,
            SeasonCount = 0,
            EpisodeCount = 1,
            Tracks = BuildTracks(coverage, 1, targetLanguages),
            State = state,
            CompletedCount = completed,
            FailedCount = failed,
            LastActivityAt = lastActivity,
            IncludeInTranslation = movie.IncludeInTranslation,
            TranslationAgeThreshold = movie.TranslationAgeThreshold
        };
    }

    /// <summary>
    /// Track chips: every language found by the scan plus every configured
    /// target language (missing targets surface as "none" tracks).
    /// </summary>
    private static List<SubtitleTrackSummary> BuildTracks(
        Dictionary<string, MediaLanguageCoverage> coverage,
        int total,
        IReadOnlyList<TargetLanguage> targetLanguages)
    {
        var languages = coverage.Keys
            .Concat(targetLanguages.Select(t => t.Code))
            .Distinct(StringComparer.OrdinalIgnoreCase);

        return languages
            .Select(code =>
            {
                var have = coverage.GetValueOrDefault(code)?.Count ?? 0;
                return new SubtitleTrackSummary
                {
                    Language = code.ToLowerInvariant(),
                    Have = Math.Min(have, total),
                    Total = total,
                    State = have == 0 ? "none" : have >= total ? "complete" : "partial"
                };
            })
            .OrderBy(t => t.State == "complete" ? 0 : t.State == "partial" ? 1 : 2)
            .ThenBy(t => t.Language)
            .ToList();
    }

    private static (MediaTranslationState State, int Completed, int Failed, DateTimeOffset? Last) AggregateState(
        IEnumerable<TranslationRequest?> latestPerMedia)
    {
        var latest = latestPerMedia.Where(r => r is not null).Cast<TranslationRequest>().ToList();
        var last = latest.Count == 0 ? (DateTimeOffset?)null : latest.Max(r => r.UpdatedAt);
        var completed = latest.Count(r => r.Status == TranslationStatus.Completed);
        var failed = latest.Count(r => r.Status == TranslationStatus.Failed);
        var active = latest.Count(r => r.Status is TranslationStatus.InProgress or TranslationStatus.Pending);

        var state = latest.Count == 0
            ? MediaTranslationState.None
            : latest.Any(r => r.Status == TranslationStatus.InProgress)
                ? MediaTranslationState.InProgress
                : latest.Any(r => r.Status == TranslationStatus.Pending)
                    ? MediaTranslationState.Queued
                    : failed > 0
                        ? MediaTranslationState.Failed
                        : MediaTranslationState.Completed;

        return (state, completed, failed, last);
    }

    private static bool MatchesFilters(MediaLibraryItem item, string? state, string? targetLanguage)
    {
        if (!string.IsNullOrEmpty(state)
            && !string.Equals(item.State.ToString(), state, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.IsNullOrEmpty(targetLanguage))
        {
            // "Missing this target": the track is absent or not yet complete.
            var track = item.Tracks.FirstOrDefault(t =>
                string.Equals(t.Language, targetLanguage, StringComparison.OrdinalIgnoreCase));
            return track is not { State: "complete" };
        }

        return true;
    }

    private static List<MediaLibraryItem> Order(List<MediaLibraryItem> items, string? orderBy, bool ascending) =>
        orderBy switch
        {
            "DateAdded" => ascending
                ? items.OrderBy(i => i.DateAdded ?? DateTimeOffset.MaxValue).ToList()
                : items.OrderByDescending(i => i.DateAdded ?? DateTimeOffset.MinValue).ToList(),
            "LastActivity" => ascending
                ? items.OrderBy(i => i.LastActivityAt ?? DateTimeOffset.MaxValue).ToList()
                : items.OrderByDescending(i => i.LastActivityAt ?? DateTimeOffset.MinValue).ToList(),
            _ => ascending
                ? items.OrderBy(i => i.Title, StringComparer.OrdinalIgnoreCase).ToList()
                : items.OrderByDescending(i => i.Title, StringComparer.OrdinalIgnoreCase).ToList()
        };

    private async Task<Dictionary<(MediaType Type, int Id), TranslationRequest>> LatestStatusByMediaAsync(
        HashSet<int> episodeIds,
        HashSet<int> movieIds)
    {
        if (episodeIds.Count == 0 && movieIds.Count == 0)
        {
            return new Dictionary<(MediaType, int), TranslationRequest>();
        }

        var requests = await _dbContext.TranslationRequests
            .AsNoTracking()
            .Where(r => r.MediaId != null
                && ((r.MediaType == MediaType.Show && episodeIds.Contains(r.MediaId.Value))
                    || (r.MediaType == MediaType.Movie && movieIds.Contains(r.MediaId.Value))))
            .ToListAsync();

        return requests
            .GroupBy(r => (r.MediaType, r.MediaId!.Value))
            .ToDictionary(
                g => g.Key,
                g => g.OrderByDescending(r => r.UpdatedAt).First());
    }

    /// <inheritdoc />
    public async Task<TranslationEstimate> Estimate(List<EstimateItem> items, string targetLanguage)
    {
        const double charsPerByte = 0.5; // UTF-8 subtitle files ≈ half bytes are translatable text
        var showIds = items.Where(i => i.MediaType == MediaType.Show).Select(i => i.Id).ToHashSet();
        var movieIds = items.Where(i => i.MediaType == MediaType.Movie).Select(i => i.Id).ToHashSet();

        var shows = await _dbContext.Shows
            .Include(s => s.Seasons)
            .ThenInclude(season => season.Episodes)
            .Where(s => showIds.Contains(s.Id))
            .ToListAsync();
        var movies = await _dbContext.Movies
            .AsNoTracking()
            .Where(m => movieIds.Contains(m.Id))
            .ToListAsync();

        var sourceCodes = (await _settingService.GetSettingAsJson<Language>(
                              SettingKeys.Translation.SourceLanguages) ?? new List<Language>())
            .Select(l => l.code.ToLowerInvariant())
            .ToList();

        long sourceBytes = 0;
        foreach (var show in shows)
        {
            var episodes = show.Seasons.SelectMany(season => season.Episodes).ToList();
            sourceBytes += EstimateBytes(show.SubtitleCoverage, episodes.Count, sourceCodes, targetLanguage);
        }
        foreach (var movie in movies)
        {
            sourceBytes += EstimateBytes(movie.SubtitleCoverage, 1, sourceCodes, targetLanguage);
        }

        var characters = (long)(sourceBytes * charsPerByte);
        // The service_type setting is normalised to a JSON array by the
        // startup migration, so look the rate up for the parsed primary
        // provider — the raw string would never match a rate key.
        var defaultProvider = TranslationServices.Parse(
            await _settingService.GetSetting(SettingKeys.Translation.ServiceType))[0];
        var rates = await _costService.GetRates();
        // A missing rate means "unknown", not free — TryGetValue keeps that
        // distinct from a configured $0 rate.
        var rate = rates.TryGetValue(defaultProvider, out var configured)
            ? configured
            : (decimal?)null;

        return new TranslationEstimate
        {
            Characters = characters,
            Provider = defaultProvider,
            Cost = rate is null
                ? null
                : (double)rate * characters / 1_000_000
        };
    }

    /// <summary>
    /// Bytes of source-language subtitles for the episodes still missing the
    /// target language — scaled by the missing ratio when coverage only gives
    /// per-language totals.
    /// </summary>
    private static long EstimateBytes(
        Dictionary<string, MediaLanguageCoverage>? coverage,
        int total,
        List<string> sourceCodes,
        string targetLanguage)
    {
        if (coverage is null || total == 0)
        {
            return 0;
        }

        var target = coverage.GetValueOrDefault(targetLanguage.ToLowerInvariant());
        var missing = Math.Max(0, total - (target?.Count ?? 0));
        if (missing == 0)
        {
            return 0;
        }

        var source = sourceCodes
            .Select(code => coverage.GetValueOrDefault(code))
            .Where(c => c is not null)
            .OrderByDescending(c => c!.Count)
            .FirstOrDefault();
        if (source is null)
        {
            return 0;
        }

        var coveredEpisodes = Math.Max(1, source.Count);
        return (long)((double)source.Bytes / coveredEpisodes * missing);
    }
}
