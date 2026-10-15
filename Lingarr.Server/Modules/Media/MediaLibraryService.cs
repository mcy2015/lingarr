using System.Text.Json;
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
/// Track coverage comes from the statistics scan's per-media JSON snapshot;
/// state and last activity from the newest request per episode/movie, rolled
/// up with SQL GROUP BY so neither episode entities nor request payloads
/// (StackTrace/subtitle columns) are ever materialized (scale audit
/// P0-7/P0-8). Coverage/state filters run over the small aggregate row set
/// (DB only, no filesystem) before paging so totals remain truthful.
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
        var needle = string.IsNullOrEmpty(searchQuery) ? null : searchQuery.ToLower();

        var targetLanguages = await _settingService.GetSettingAsJson<TargetLanguage>(
            SettingKeys.Translation.TargetLanguages) ?? new List<TargetLanguage>();

        // Phase 1 — aggregate rows straight from SQL: scalars, coverage read
        // directly off the JSON column and season/episode counts from grouped
        // subqueries. One small row per media item, no entity graphs.
        var rows = new List<MediaRow>();
        if (wantShows)
        {
            var showQuery = _dbContext.Shows
                .AsNoTracking()
                .AsQueryable();
            if (needle is not null)
            {
                showQuery = showQuery.Where(s => s.Title.ToLower().Contains(needle));
            }

            var showRows = await showQuery.Select(s => new
            {
                s.Id,
                s.Title,
                s.DateAdded,
                s.IncludeInTranslation,
                s.TranslationAgeThreshold,
                s.SubtitleCoverageJson,
                SeasonCount = s.Seasons.Count(),
                EpisodeCount = s.Seasons.SelectMany(season => season.Episodes).Count()
            }).ToListAsync();

            var statusById = await ShowStatusCountsAsync();
            rows.AddRange(showRows.Select(r => new MediaRow
            {
                Id = r.Id,
                MediaType = MediaType.Show,
                Title = r.Title,
                DateAdded = r.DateAdded,
                IncludeInTranslation = r.IncludeInTranslation,
                TranslationAgeThreshold = r.TranslationAgeThreshold,
                SubtitleCoverageJson = r.SubtitleCoverageJson,
                SeasonCount = r.SeasonCount,
                EpisodeCount = r.EpisodeCount,
                Status = statusById.GetValueOrDefault(r.Id)
            }));
        }
        if (wantMovies)
        {
            var movieQuery = _dbContext.Movies
                .AsNoTracking()
                .AsQueryable();
            if (needle is not null)
            {
                movieQuery = movieQuery.Where(m => m.Title.ToLower().Contains(needle));
            }

            var movieRows = await movieQuery.Select(m => new
            {
                m.Id,
                m.Title,
                m.DateAdded,
                m.IncludeInTranslation,
                m.TranslationAgeThreshold,
                m.SubtitleCoverageJson
            }).ToListAsync();

            var statusById = await MovieStatusCountsAsync();
            rows.AddRange(movieRows.Select(r => new MediaRow
            {
                Id = r.Id,
                MediaType = MediaType.Movie,
                Title = r.Title,
                DateAdded = r.DateAdded,
                IncludeInTranslation = r.IncludeInTranslation,
                TranslationAgeThreshold = r.TranslationAgeThreshold,
                SubtitleCoverageJson = r.SubtitleCoverageJson,
                SeasonCount = 0,
                EpisodeCount = 1,
                Status = statusById.GetValueOrDefault(r.Id)
            }));
        }

        foreach (var row in rows)
        {
            row.Tracks = BuildTracks(
                DeserializeCoverage(row.SubtitleCoverageJson) ?? new Dictionary<string, MediaLanguageCoverage>(),
                row.EpisodeCount,
                targetLanguages);
        }

        // Phase 2 — coverage/state filters, ordering and paging run over the
        // aggregate rows (small: one row per media, not per episode).
        var filtered = rows.Where(row => MatchesFilters(row, state, targetLanguage)).ToList();
        var ordered = Order(filtered, orderBy, ascending);
        var page = ordered
            .Skip((pageNumber - 1) * pageSize)
            .Take(pageSize)
            .ToList();

        // Phase 3 — the full show graph hydrates for the page's rows only
        // (inline season detail). Tracking stays on because Episodes' required
        // Season navigation cycles no-tracking includes.
        Dictionary<int, Show>? showsById = null;
        var pageShowIds = page
            .Where(row => row.MediaType == MediaType.Show)
            .Select(row => row.Id)
            .Distinct()
            .ToList();
        if (pageShowIds.Count > 0)
        {
            showsById = await _dbContext.Shows
                .Include(s => s.Seasons)
                .ThenInclude(season => season.Episodes)
                .Where(s => pageShowIds.Contains(s.Id))
                .ToDictionaryAsync(s => s.Id);
        }

        return new PagedResult<MediaLibraryItem>
        {
            Items = page.Select(row => BuildItem(row, showsById)).ToList(),
            TotalCount = ordered.Count,
            PageNumber = pageNumber,
            PageSize = pageSize
        };
    }

    /// <inheritdoc />
    public async Task<MediaLibraryItem?> GetLibraryItem(MediaType? mediaType, int id)
    {
        var targetLanguages = await _settingService.GetSettingAsJson<TargetLanguage>(
            SettingKeys.Translation.TargetLanguages) ?? new List<TargetLanguage>();

        if (mediaType is null or MediaType.Show)
        {
            var show = await _dbContext.Shows
                .Include(s => s.Seasons)
                .ThenInclude(season => season.Episodes)
                .FirstOrDefaultAsync(s => s.Id == id);
            if (show is not null)
            {
                var summary = (await ShowStatusCountsAsync(show.Id)).GetValueOrDefault(show.Id);
                var episodeCount = show.Seasons.Sum(season => season.Episodes.Count);
                return new MediaLibraryItem
                {
                    Id = show.Id,
                    MediaType = MediaType.Show,
                    Title = show.Title,
                    DateAdded = show.DateAdded,
                    SeasonCount = show.Seasons.Count,
                    EpisodeCount = episodeCount,
                    Tracks = BuildTracks(
                        show.SubtitleCoverage ?? new Dictionary<string, MediaLanguageCoverage>(),
                        episodeCount,
                        targetLanguages),
                    State = DeriveState(summary),
                    CompletedCount = summary?.Completed ?? 0,
                    FailedCount = summary?.Failed ?? 0,
                    LastActivityAt = summary?.LastActivity,
                    IncludeInTranslation = show.IncludeInTranslation,
                    TranslationAgeThreshold = show.TranslationAgeThreshold,
                    Show = show
                };
            }
        }

        if (mediaType is null or MediaType.Movie)
        {
            var movie = await _dbContext.Movies
                .AsNoTracking()
                .FirstOrDefaultAsync(m => m.Id == id);
            if (movie is not null)
            {
                var summary = (await MovieStatusCountsAsync(movie.Id)).GetValueOrDefault(movie.Id);
                return new MediaLibraryItem
                {
                    Id = movie.Id,
                    MediaType = MediaType.Movie,
                    Title = movie.Title,
                    DateAdded = movie.DateAdded,
                    SeasonCount = 0,
                    EpisodeCount = 1,
                    Tracks = BuildTracks(
                        movie.SubtitleCoverage ?? new Dictionary<string, MediaLanguageCoverage>(),
                        1,
                        targetLanguages),
                    State = DeriveState(summary),
                    CompletedCount = summary?.Completed ?? 0,
                    FailedCount = summary?.Failed ?? 0,
                    LastActivityAt = summary?.LastActivity,
                    IncludeInTranslation = movie.IncludeInTranslation,
                    TranslationAgeThreshold = movie.TranslationAgeThreshold
                };
            }
        }

        return null;
    }

    /// <summary>
    /// Newest request per episode rolled up to counts per show (scale audit
    /// P0-8). A NOT EXISTS anti-join keeps only the newest row per media (Id
    /// breaks UpdatedAt ties deterministically) and the grouping stays in
    /// SQL, so neither request payloads nor episode entities enter memory.
    /// When <paramref name="showId"/> is given the aggregation narrows to a
    /// single show (media detail page direct path).
    /// </summary>
    private async Task<Dictionary<int, MediaStatusSummary>> ShowStatusCountsAsync(int? showId = null)
    {
        var episodes = _dbContext.Episodes
            .AsNoTracking()
            .AsQueryable();
        if (showId is not null)
        {
            var id = showId.Value;
            episodes = episodes.Where(e => e.Season.ShowId == id);
        }

        var rows = await episodes
            .Join(
                _dbContext.TranslationRequests.AsNoTracking()
                    .Where(r => r.MediaType == MediaType.Show),
                e => (int?)e.Id,
                r => r.MediaId,
                (e, r) => new { ShowId = e.Season.ShowId, RequestId = r.Id, r.MediaId, r.Status, r.UpdatedAt })
            .Where(x => !_dbContext.TranslationRequests.Any(later =>
                later.MediaType == MediaType.Show
                && later.MediaId == x.MediaId
                && (later.UpdatedAt > x.UpdatedAt
                    || (later.UpdatedAt == x.UpdatedAt && later.Id > x.RequestId))))
            .GroupBy(x => x.ShowId)
            .Select(g => new
            {
                ShowId = g.Key,
                Total = g.Count(),
                Completed = g.Count(x => x.Status == TranslationStatus.Completed),
                Failed = g.Count(x => x.Status == TranslationStatus.Failed),
                InProgress = g.Count(x => x.Status == TranslationStatus.InProgress),
                Pending = g.Count(x => x.Status == TranslationStatus.Pending),
                LastActivity = g.Max(x => x.UpdatedAt)
            })
            .ToListAsync();

        return rows.ToDictionary(
            r => r.ShowId,
            r => new MediaStatusSummary
            {
                Total = r.Total,
                Completed = r.Completed,
                Failed = r.Failed,
                InProgress = r.InProgress,
                Pending = r.Pending,
                LastActivity = r.LastActivity
            });
    }

    /// <summary>
    /// Newest request per movie rolled up to counts (movie MediaId is the
    /// movie id, no join needed). Same anti-join shape as
    /// <see cref="ShowStatusCountsAsync"/>; <paramref name="movieId"/> narrows
    /// the aggregation to one movie for the detail-page direct path.
    /// </summary>
    private async Task<Dictionary<int, MediaStatusSummary>> MovieStatusCountsAsync(int? movieId = null)
    {
        var query = _dbContext.TranslationRequests
            .AsNoTracking()
            .Where(r => r.MediaType == MediaType.Movie && r.MediaId != null);
        if (movieId is not null)
        {
            var id = movieId.Value;
            query = query.Where(r => r.MediaId == id);
        }

        var rows = await query
            .Where(r => !_dbContext.TranslationRequests.Any(later =>
                later.MediaType == MediaType.Movie
                && later.MediaId == r.MediaId
                && (later.UpdatedAt > r.UpdatedAt
                    || (later.UpdatedAt == r.UpdatedAt && later.Id > r.Id))))
            .GroupBy(r => r.MediaId!.Value)
            .Select(g => new
            {
                MediaId = g.Key,
                Total = g.Count(),
                Completed = g.Count(x => x.Status == TranslationStatus.Completed),
                Failed = g.Count(x => x.Status == TranslationStatus.Failed),
                InProgress = g.Count(x => x.Status == TranslationStatus.InProgress),
                Pending = g.Count(x => x.Status == TranslationStatus.Pending),
                LastActivity = g.Max(x => x.UpdatedAt)
            })
            .ToListAsync();

        return rows.ToDictionary(
            r => r.MediaId,
            r => new MediaStatusSummary
            {
                Total = r.Total,
                Completed = r.Completed,
                Failed = r.Failed,
                InProgress = r.InProgress,
                Pending = r.Pending,
                LastActivity = r.LastActivity
            });
    }

    /// <summary>
    /// State column derivation from the per-media latest-request counts —
    /// identical precedence to the previous in-memory aggregation: any
    /// InProgress, else any Pending, else any Failed, else Completed.
    /// </summary>
    private static MediaTranslationState DeriveState(MediaStatusSummary? summary)
    {
        if (summary is null || summary.Total == 0)
        {
            return MediaTranslationState.None;
        }
        if (summary.InProgress > 0)
        {
            return MediaTranslationState.InProgress;
        }
        if (summary.Pending > 0)
        {
            return MediaTranslationState.Queued;
        }
        if (summary.Failed > 0)
        {
            return MediaTranslationState.Failed;
        }
        return MediaTranslationState.Completed;
    }

    private static MediaLibraryItem BuildItem(MediaRow row, IReadOnlyDictionary<int, Show>? shows)
    {
        return new MediaLibraryItem
        {
            Id = row.Id,
            MediaType = row.MediaType,
            Title = row.Title,
            DateAdded = row.DateAdded,
            SeasonCount = row.SeasonCount,
            EpisodeCount = row.EpisodeCount,
            Tracks = row.Tracks,
            State = DeriveState(row.Status),
            CompletedCount = row.Status?.Completed ?? 0,
            FailedCount = row.Status?.Failed ?? 0,
            LastActivityAt = row.Status?.LastActivity,
            IncludeInTranslation = row.IncludeInTranslation,
            TranslationAgeThreshold = row.TranslationAgeThreshold,
            Show = row.MediaType == MediaType.Show ? shows?.GetValueOrDefault(row.Id) : null
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

    private static bool MatchesFilters(MediaRow row, string? state, string? targetLanguage)
    {
        if (!string.IsNullOrEmpty(state)
            && !string.Equals(DeriveState(row.Status).ToString(), state, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.IsNullOrEmpty(targetLanguage))
        {
            // "Missing this target": the track is absent or not yet complete.
            var track = row.Tracks.FirstOrDefault(t =>
                string.Equals(t.Language, targetLanguage, StringComparison.OrdinalIgnoreCase));
            return track is not { State: "complete" };
        }

        return true;
    }

    private static List<MediaRow> Order(List<MediaRow> items, string? orderBy, bool ascending) =>
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

    private static Dictionary<string, MediaLanguageCoverage>? DeserializeCoverage(string? json) =>
        string.IsNullOrEmpty(json)
            ? null
            : JsonSerializer.Deserialize<Dictionary<string, MediaLanguageCoverage>>(json);

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

    /// <summary>
    /// Per-media aggregate row: scalar columns plus the grouped request
    /// counts — one small row per media item instead of full entity graphs.
    /// </summary>
    private sealed class MediaRow
    {
        public int Id { get; init; }
        public MediaType MediaType { get; init; }
        public string Title { get; init; } = string.Empty;
        public DateTimeOffset? DateAdded { get; init; }
        public bool IncludeInTranslation { get; init; }
        public int? TranslationAgeThreshold { get; init; }
        public string? SubtitleCoverageJson { get; init; }
        public int SeasonCount { get; init; }
        public int EpisodeCount { get; init; }
        public MediaStatusSummary? Status { get; set; }
        public List<SubtitleTrackSummary> Tracks { get; set; } = [];
        public DateTimeOffset? LastActivityAt => Status?.LastActivity;
    }

    /// <summary>
    /// Counts over the newest request per episode/movie — the inputs of the
    /// state column, computed by SQL GROUP BY and never from materialized
    /// request entities.
    /// </summary>
    private sealed class MediaStatusSummary
    {
        public int Total { get; set; }
        public int Completed { get; set; }
        public int Failed { get; set; }
        public int InProgress { get; set; }
        public int Pending { get; set; }
        public DateTimeOffset? LastActivity { get; set; }
    }
}
