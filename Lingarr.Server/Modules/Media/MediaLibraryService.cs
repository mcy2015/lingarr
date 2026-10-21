using System.Text.Json;
using Lingarr.Core.Configuration;
using Lingarr.Core.Data;
using Lingarr.Core.Entities;
using Lingarr.Core.Enum;
using Lingarr.Server.Models;
using Lingarr.Server.Models.Api;
using Lingarr.Server.Modules.SubtitleSearch.Pipeline;
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
/// The audio-aware states (PRD card A) — "native" (needs no subtitles) and
/// "audio-unknown" — are plain column predicates and push all the way down
/// into the media queries.
/// </summary>
public class MediaLibraryService : IMediaLibraryService
{
    /// <summary>Library state filter value: the subtitle-need rule judged the media as not needing subtitles.</summary>
    public const string NativeState = "native";

    /// <summary>Library state filter value: the audio track languages are unknown (null).</summary>
    public const string AudioUnknownState = "audio-unknown";

    private readonly LingarrDbContext _dbContext;
    private readonly ISettingService _settingService;
    private readonly ICostService _costService;
    private readonly TargetLanguageResolver _targetLanguageResolver;
    private readonly ILogger<MediaLibraryService> _logger;

    public MediaLibraryService(
        LingarrDbContext dbContext,
        ISettingService settingService,
        ICostService costService,
        TargetLanguageResolver targetLanguageResolver,
        ILogger<MediaLibraryService> logger)
    {
        _dbContext = dbContext;
        _settingService = settingService;
        _costService = costService;
        _targetLanguageResolver = targetLanguageResolver;
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
        // page arithmetic guard (audit P2): pageNumber 0/negative would make
        // Skip((pageNumber-1)*pageSize) negative and throw out of List.Skip —
        // clamp at the service entry so every caller is covered
        pageNumber = Math.Max(1, pageNumber);

        var wantShows = mediaType is null or MediaType.Show;
        var wantMovies = mediaType is null or MediaType.Movie;
        var needle = string.IsNullOrEmpty(searchQuery) ? null : searchQuery.ToLower();

        // Audit P1-4: ASCII search terms resolve through the FTS5 title index
        // (media_title_fts) — lower(title) LIKE '%…%' can use no B-tree and
        // scans the whole table per search at the 100k-rows scale. The FTS
        // semantics are word-prefix ("star" matches "Star Trek" and
        // "Starship Troopers", not a title merely containing "star"
        // mid-word). Non-ASCII (CJK) terms keep the LIKE path below: the
        // unicode61 tokenizer does not segment hanzi runs — a whole run
        // folds into one token — so prefix matching would silently miss
        // most real queries. TrySearchAsync also returns null (→ LIKE) when
        // the index is unavailable (non-SQLite provider, table absent,
        // SQLite error), keeping the predicate as the safety net.
        // B2: whichever branch serves the term, the title predicate is a
        // union with a Path substring match — a media row titled 狗阵 whose
        // path is "/movies/Black Dog" must surface for "black dog", which
        // neither the title FTS nor a title-only LIKE could ever find.
        Dictionary<MediaType, List<int>>? ftsIds = null;
        if (needle is not null && needle.All(c => c <= '\u007F'))
        {
            ftsIds = await TitleFts.TrySearchAsync(_dbContext, needle);
        }

        var ftsShowIds = ftsIds?.GetValueOrDefault(MediaType.Show);
        var ftsMovieIds = ftsIds?.GetValueOrDefault(MediaType.Movie);

        var targetLanguages = await _settingService.GetSettingAsJson<TargetLanguage>(
            SettingKeys.Translation.TargetLanguages) ?? new List<TargetLanguage>();

        // The two audio-aware states are plain column predicates, so unlike
        // the request-state filter (in-memory over the aggregate rows) they
        // push into the media queries themselves (PRD card A first batch).
        var wantNative = string.Equals(state, NativeState, StringComparison.OrdinalIgnoreCase);
        var wantAudioUnknown = string.Equals(state, AudioUnknownState, StringComparison.OrdinalIgnoreCase);

        // Two-phase default path (P0-7 closeout): without a target-language
        // filter the browsing shapes — no request-state filter, column-backed
        // ordering — page inside SQL (filter/sort/Skip/Take over id+scalar
        // columns) and the coverage JSON deserializes for the page's rows
        // only, instead of parsing every media row of the library. The other
        // shapes keep the full in-memory aggregation below: a target-language
        // filter ranks membership on per-language tracks that live inside the
        // coverage JSON (no SQL representation), and request-state filters /
        // LastActivity ordering need the newest-request rollup per media,
        // which likewise has no pushable form.
        if (string.IsNullOrEmpty(targetLanguage)
            && !IsRequestStateFilter(state)
            && !string.Equals(orderBy, "LastActivity", StringComparison.OrdinalIgnoreCase))
        {
            return await GetLibraryPagedAsync(
                wantShows, wantMovies, needle, ftsShowIds, ftsMovieIds, wantNative, wantAudioUnknown,
                orderBy, ascending, pageNumber, pageSize, targetLanguages);
        }

        // Phase 1 — aggregate rows straight from SQL: scalars, coverage read
        // directly off the JSON column and season/episode counts from grouped
        // subqueries. One small row per media item, no entity graphs.
        var rows = new List<MediaRow>();
        if (wantShows)
        {
            var showQuery = ApplyShowFilters(
                _dbContext.Shows.AsNoTracking().AsQueryable(), needle, ftsShowIds, wantNative, wantAudioUnknown);

            var showRows = await showQuery.Select(s => new
            {
                s.Id,
                s.Title,
                s.DateAdded,
                s.IncludeInTranslation,
                s.TranslationAgeThreshold,
                s.SubtitleCoverageJson,
                s.SuggestedLanguage,
                s.Year,
                s.ImdbId,
                s.ImdbRating,
                SeasonCount = s.Seasons.Count(),
                EpisodeCount = s.Seasons.SelectMany(season => season.Episodes).Count(),
                HasEpisodes = s.Seasons.SelectMany(season => season.Episodes).Any(),
                AllEpisodesNative = s.Seasons.SelectMany(season => season.Episodes).All(e => e.NoSubtitleNeeded)
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
                SuggestedLanguage = r.SuggestedLanguage,
                Year = r.Year,
                ImdbId = r.ImdbId,
                ImdbRating = r.ImdbRating,
                SeasonCount = r.SeasonCount,
                EpisodeCount = r.EpisodeCount,
                NeedsNoSubtitles = r.HasEpisodes && r.AllEpisodesNative,
                Status = statusById.GetValueOrDefault(r.Id)
            }));
        }
        if (wantMovies)
        {
            var movieQuery = ApplyMovieFilters(
                _dbContext.Movies.AsNoTracking().AsQueryable(), needle, ftsMovieIds, wantNative, wantAudioUnknown);

            var movieRows = await movieQuery.Select(m => new
            {
                m.Id,
                m.Title,
                m.DateAdded,
                m.IncludeInTranslation,
                m.TranslationAgeThreshold,
                m.SubtitleCoverageJson,
                m.SuggestedLanguage,
                m.AudioLanguages,
                m.NeedsTranslation,
                m.NoSubtitleNeeded,
                m.Year,
                m.ImdbId,
                m.Quality,
                m.SizeBytes,
                m.ImdbRating
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
                SuggestedLanguage = r.SuggestedLanguage,
                AudioLanguages = r.AudioLanguages,
                Year = r.Year,
                ImdbId = r.ImdbId,
                Quality = r.Quality,
                SizeBytes = r.SizeBytes,
                ImdbRating = r.ImdbRating,
                SeasonCount = 0,
                EpisodeCount = 1,
                NeedsNoSubtitles = r.NoSubtitleNeeded,
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

    /// <summary>
    /// Two-phase default path (P0-7 closeout): filtering, ordering and the
    /// paging window happen in SQL over id+scalar columns (phase 1), and the
    /// coverage JSON plus request rollups hydrate for the page's rows only
    /// (phase 2). The default browsing flow therefore parses at most a page
    /// worth of coverage snapshots per request instead of the whole
    /// library's.
    /// Mixed-type listings cannot Skip in SQL (how many shows precede a
    /// movie globally is unknown upfront), so each type fetches its first
    /// skip+take window — a union page element is preceded by fewer than
    /// skip+take rows of either type, so the windows provably contain the
    /// page — and the windows merge in memory under the same comparator
    /// <see cref="Order"/> uses before the final Skip/Take.
    /// </summary>
    private async Task<PagedResult<MediaLibraryItem>> GetLibraryPagedAsync(
        bool wantShows,
        bool wantMovies,
        string? needle,
        List<int>? ftsShowIds,
        List<int>? ftsMovieIds,
        bool wantNative,
        bool wantAudioUnknown,
        string? orderBy,
        bool ascending,
        int pageNumber,
        int pageSize,
        IReadOnlyList<TargetLanguage> targetLanguages)
    {
        var skip = (pageNumber - 1) * pageSize;
        var take = skip + pageSize;
        var rows = new List<MediaRow>();
        var totalCount = 0;

        if (wantShows)
        {
            var showQuery = ApplyShowFilters(
                _dbContext.Shows.AsNoTracking().AsQueryable(), needle, ftsShowIds, wantNative, wantAudioUnknown);
            var scan = showQuery.Select(s => new ShowScanRow
            {
                Id = s.Id,
                SuggestedLanguage = s.SuggestedLanguage,
                Title = s.Title,
                DateAdded = s.DateAdded,
                IncludeInTranslation = s.IncludeInTranslation,
                TranslationAgeThreshold = s.TranslationAgeThreshold,
                Year = s.Year,
                ImdbId = s.ImdbId,
                ImdbRating = s.ImdbRating,
                SeasonCount = s.Seasons.Count(),
                EpisodeCount = s.Seasons.SelectMany(season => season.Episodes).Count(),
                HasEpisodes = s.Seasons.SelectMany(season => season.Episodes).Any(),
                AllEpisodesNative = s.Seasons.SelectMany(season => season.Episodes).All(e => e.NoSubtitleNeeded)
            });
            scan = OrderScan(scan, orderBy, ascending);
            var slice = await scan.Take(take).ToListAsync();
            totalCount += await showQuery.CountAsync();
            rows.AddRange(slice.Select(r => new MediaRow
            {
                Id = r.Id,
                MediaType = MediaType.Show,
                SuggestedLanguage = r.SuggestedLanguage,
                Title = r.Title,
                DateAdded = r.DateAdded,
                IncludeInTranslation = r.IncludeInTranslation,
                TranslationAgeThreshold = r.TranslationAgeThreshold,
                Year = r.Year,
                ImdbId = r.ImdbId,
                ImdbRating = r.ImdbRating,
                SeasonCount = r.SeasonCount,
                EpisodeCount = r.EpisodeCount,
                NeedsNoSubtitles = r.HasEpisodes && r.AllEpisodesNative
            }));
        }
        if (wantMovies)
        {
            var movieQuery = ApplyMovieFilters(
                _dbContext.Movies.AsNoTracking().AsQueryable(), needle, ftsMovieIds, wantNative, wantAudioUnknown);
            var scan = movieQuery.Select(m => new MovieScanRow
            {
                Id = m.Id,
                SuggestedLanguage = m.SuggestedLanguage,
                Title = m.Title,
                DateAdded = m.DateAdded,
                IncludeInTranslation = m.IncludeInTranslation,
                TranslationAgeThreshold = m.TranslationAgeThreshold,
                AudioLanguages = m.AudioLanguages,
                NoSubtitleNeeded = m.NoSubtitleNeeded,
                Year = m.Year,
                ImdbId = m.ImdbId,
                Quality = m.Quality,
                SizeBytes = m.SizeBytes,
                ImdbRating = m.ImdbRating
            });
            scan = OrderScan(scan, orderBy, ascending);
            var slice = await scan.Take(take).ToListAsync();
            totalCount += await movieQuery.CountAsync();
            rows.AddRange(slice.Select(r => new MediaRow
            {
                Id = r.Id,
                MediaType = MediaType.Movie,
                SuggestedLanguage = r.SuggestedLanguage,
                Title = r.Title,
                DateAdded = r.DateAdded,
                IncludeInTranslation = r.IncludeInTranslation,
                TranslationAgeThreshold = r.TranslationAgeThreshold,
                AudioLanguages = r.AudioLanguages,
                Year = r.Year,
                ImdbId = r.ImdbId,
                Quality = r.Quality,
                SizeBytes = r.SizeBytes,
                ImdbRating = r.ImdbRating,
                SeasonCount = 0,
                EpisodeCount = 1,
                NeedsNoSubtitles = r.NoSubtitleNeeded
            }));
        }

        // merge the typed windows with the legacy comparator (stable shows-
        // before-movies tie order, matching the legacy insertion order), then
        // slice the page out of the merged window
        var page = Order(rows, orderBy, ascending)
            .Skip(skip)
            .Take(pageSize)
            .ToList();

        // Phase 2 — coverage JSON hydrates for the page's rows only. This is
        // the whole point of the two-phase split: the coverage column is
        // never part of the phase-1 scan, so paging never deserializes the
        // full library's snapshots.
        var pageShowIds = page.Where(r => r.MediaType == MediaType.Show).Select(r => r.Id).ToList();
        var pageMovieIds = page.Where(r => r.MediaType == MediaType.Movie).Select(r => r.Id).ToList();
        var coverageByMediaId = new Dictionary<(MediaType Type, int Id), string?>();
        if (pageShowIds.Count > 0)
        {
            var coverageRows = await _dbContext.Shows
                .AsNoTracking()
                .Where(s => pageShowIds.Contains(s.Id))
                .Select(s => new { s.Id, s.SubtitleCoverageJson })
                .ToListAsync();
            foreach (var row in coverageRows)
            {
                coverageByMediaId[(MediaType.Show, row.Id)] = row.SubtitleCoverageJson;
            }
        }
        if (pageMovieIds.Count > 0)
        {
            var coverageRows = await _dbContext.Movies
                .AsNoTracking()
                .Where(m => pageMovieIds.Contains(m.Id))
                .Select(m => new { m.Id, m.SubtitleCoverageJson })
                .ToListAsync();
            foreach (var row in coverageRows)
            {
                coverageByMediaId[(MediaType.Movie, row.Id)] = row.SubtitleCoverageJson;
            }
        }
        foreach (var row in page)
        {
            row.Tracks = BuildTracks(
                DeserializeCoverage(coverageByMediaId.GetValueOrDefault((row.MediaType, row.Id)))
                    ?? new Dictionary<string, MediaLanguageCoverage>(),
                row.EpisodeCount,
                targetLanguages);
        }

        // request rollups narrow to the page's ids as well
        Dictionary<int, MediaStatusSummary>? showStatus = null;
        Dictionary<int, MediaStatusSummary>? movieStatus = null;
        if (pageShowIds.Count > 0)
        {
            showStatus = await ShowStatusCountsAsync(pageShowIds);
        }
        if (pageMovieIds.Count > 0)
        {
            movieStatus = await MovieStatusCountsAsync(pageMovieIds);
        }
        foreach (var row in page)
        {
            row.Status = row.MediaType == MediaType.Show
                ? showStatus?.GetValueOrDefault(row.Id)
                : movieStatus?.GetValueOrDefault(row.Id);
        }

        // Phase 3 — the full show graph hydrates for the page's rows only
        // (inline season detail), exactly like the legacy path.
        Dictionary<int, Show>? showsById = null;
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
            TotalCount = totalCount,
            PageNumber = pageNumber,
            PageSize = pageSize
        };
    }

    /// <summary>Request-derived state filter (queued/in-progress/…): cannot
    /// push into the media queries, it ranks on the newest-request rollup.</summary>
    private static bool IsRequestStateFilter(string? state) =>
        !string.IsNullOrEmpty(state)
        && !string.Equals(state, NativeState, StringComparison.OrdinalIgnoreCase)
        && !string.Equals(state, AudioUnknownState, StringComparison.OrdinalIgnoreCase);

    /// <summary>Shared show predicates of both paths: search, native, audio-unknown.
    /// Search arrives either as the FTS-resolved id set (ASCII queries — the
    /// index already decided the word-prefix matches, so the id membership
    /// replaces the title predicate: no lower(title) scan over the table)
    /// or as the LIKE substring fallback (non-ASCII queries or an
    /// unavailable index). Either way the predicate is a union: title match
    /// OR path substring match — a CJK-titled media row often carries the
    /// romanised release name only in its on-disk path (B2), so the path is
    /// searched alongside the title.</summary>
    private static IQueryable<Show> ApplyShowFilters(
        IQueryable<Show> query,
        string? needle,
        List<int>? ftsShowIds,
        bool wantNative,
        bool wantAudioUnknown)
    {
        if (ftsShowIds is not null)
        {
            query = query.Where(s => ftsShowIds.Contains(s.Id)
                                     || s.Path.ToLower().Contains(needle!));
        }
        else if (needle is not null)
        {
            query = query.Where(s => s.Title.ToLower().Contains(needle)
                                     || s.Path.ToLower().Contains(needle));
        }
        if (wantNative)
        {
            // a show counts as native when it has episodes and every one
            // carries the persisted exemption flag (scan-maintained)
            query = query.Where(s =>
                s.Seasons.SelectMany(season => season.Episodes).Any()
                && s.Seasons.SelectMany(season => season.Episodes).All(e => e.NoSubtitleNeeded));
        }
        if (wantAudioUnknown)
        {
            query = query.Where(s =>
                s.Seasons.SelectMany(season => season.Episodes).Any(e => e.AudioLanguages == null));
        }
        return query;
    }

    /// <summary>Shared movie predicates of both paths: search, native,
    /// audio-unknown. See <see cref="ApplyShowFilters"/> for the FTS/LIKE
    /// split and the title-OR-path union of the search predicate.</summary>
    private static IQueryable<Movie> ApplyMovieFilters(
        IQueryable<Movie> query,
        string? needle,
        List<int>? ftsMovieIds,
        bool wantNative,
        bool wantAudioUnknown)
    {
        if (ftsMovieIds is not null)
        {
            query = query.Where(m => ftsMovieIds.Contains(m.Id)
                                     || (m.Path != null && m.Path.ToLower().Contains(needle!)));
        }
        else if (needle is not null)
        {
            query = query.Where(m => m.Title.ToLower().Contains(needle)
                                     || (m.Path != null && m.Path.ToLower().Contains(needle)));
        }
        if (wantNative)
        {
            query = query.Where(m => m.NoSubtitleNeeded);
        }
        if (wantAudioUnknown)
        {
            query = query.Where(m => m.AudioLanguages == null);
        }
        return query;
    }

    /// <summary>
    /// Phase-1 ordering, pushed into SQL. Title (the default) orders on
    /// lower(Title), which folds ASCII case the same way the legacy
    /// in-memory OrdinalIgnoreCase comparator does; DateAdded keeps nulls
    /// last in both directions exactly like the legacy ?? MaxValue/MinValue
    /// fallbacks; Id is the deterministic tie-break the legacy stable sort
    /// got from insertion order. LastActivity never reaches here — it needs
    /// the request rollup and stays on the legacy path.
    /// </summary>
    private static IQueryable<ShowScanRow> OrderScan(
        IQueryable<ShowScanRow> scan, string? orderBy, bool ascending)
    {
        if (string.Equals(orderBy, "DateAdded", StringComparison.OrdinalIgnoreCase))
        {
            return ascending
                ? scan.OrderBy(r => r.DateAdded == null ? 1 : 0)
                    .ThenBy(r => r.DateAdded).ThenBy(r => r.Id)
                : scan.OrderBy(r => r.DateAdded == null ? 1 : 0)
                    .ThenByDescending(r => r.DateAdded).ThenBy(r => r.Id);
        }
        return ascending
            ? scan.OrderBy(r => r.Title.ToLower()).ThenBy(r => r.Id)
            : scan.OrderByDescending(r => r.Title.ToLower()).ThenBy(r => r.Id);
    }

    /// <summary>Movie counterpart of <see cref="OrderScan"/> (same semantics).</summary>
    private static IQueryable<MovieScanRow> OrderScan(
        IQueryable<MovieScanRow> scan, string? orderBy, bool ascending)
    {
        if (string.Equals(orderBy, "DateAdded", StringComparison.OrdinalIgnoreCase))
        {
            return ascending
                ? scan.OrderBy(r => r.DateAdded == null ? 1 : 0)
                    .ThenBy(r => r.DateAdded).ThenBy(r => r.Id)
                : scan.OrderBy(r => r.DateAdded == null ? 1 : 0)
                    .ThenByDescending(r => r.DateAdded).ThenBy(r => r.Id);
        }
        return ascending
            ? scan.OrderBy(r => r.Title.ToLower()).ThenBy(r => r.Id)
            : scan.OrderByDescending(r => r.Title.ToLower()).ThenBy(r => r.Id);
    }

    /// <inheritdoc />
    public async Task<MediaLibraryItem?> GetLibraryItem(MediaType? mediaType, int id)
    {
        // B08: the detail projection resolves the media's EFFECTIVE target
        // set (per-media override > global) with the same shared resolver
        // the need-flag writers use — the coverage chips never show a
        // global target an override removed, and never hide one it added.
        var globalTargetCodes = await _targetLanguageResolver.GetGlobalAsync();

        if (mediaType is null or MediaType.Show)
        {
            var show = await _dbContext.Shows
                .Include(s => s.Seasons)
                .ThenInclude(season => season.Episodes)
                .Include(s => s.Images)
                .FirstOrDefaultAsync(s => s.Id == id);
            if (show is not null)
            {
                var summary = (await ShowStatusCountsAsync([show.Id])).GetValueOrDefault(show.Id);
                var episodes = show.Seasons.SelectMany(season => season.Episodes).ToList();
                var episodeCount = episodes.Count;
                var showTargets = TargetLanguageCodes(show.TargetLanguagesOverride, globalTargetCodes);
                return new MediaLibraryItem
                {
                    Id = show.Id,
                    MediaType = MediaType.Show,
                    Title = show.Title,
                    DateAdded = show.DateAdded,
                    Year = show.Year,
                    ImdbId = show.ImdbId,
                    ImdbRating = show.ImdbRating,
                    Overview = show.Overview,
                    PosterPath = show.Images.FirstOrDefault(image => image.Type == "poster")?.Path,
                    SeasonCount = show.Seasons.Count,
                    EpisodeCount = episodeCount,
                    Tracks = BuildTracks(
                        show.SubtitleCoverage ?? new Dictionary<string, MediaLanguageCoverage>(),
                        episodeCount,
                        showTargets),
                    State = DeriveState(summary),
                    CompletedCount = summary?.Completed ?? 0,
                    FailedCount = summary?.Failed ?? 0,
                    LastActivityAt = summary?.LastActivity,
                    IncludeInTranslation = show.IncludeInTranslation,
                    TranslationAgeThreshold = show.TranslationAgeThreshold,
                    AudioLanguages = AggregateAudioLanguages(episodes.Select(e => e.AudioLanguages)),
                    SuggestedAudioLanguage = show.SuggestedLanguage,
                    NeedsNoSubtitles = episodes.Count > 0 && episodes.All(e => e.NoSubtitleNeeded),
                    // show-level mark: uniform across episodes → that value,
                    // mixed/unmarked → null (automatic), matching the toggle
                    // which writes every episode at once
                    AudioOverride = episodeCount == 0
                        ? null
                        : episodes.All(e => e.AudioOverride == true)
                            ? true
                            : episodes.All(e => e.AudioOverride == false)
                                ? false
                                : null,
                    TargetLanguagesOverride = OverrideCodesOnWire(show.TargetLanguagesOverride),
                    Show = show
                };
            }
        }

        if (mediaType is null or MediaType.Movie)
            {
                var movie = await _dbContext.Movies
                    .AsNoTracking()
                    .Include(m => m.Images)
                    .FirstOrDefaultAsync(m => m.Id == id);
                if (movie is not null)
                {
                    var summary = (await MovieStatusCountsAsync([movie.Id])).GetValueOrDefault(movie.Id);
                    return new MediaLibraryItem
                    {
                        Id = movie.Id,
                        MediaType = MediaType.Movie,
                        Title = movie.Title,
                        DateAdded = movie.DateAdded,
                        Year = movie.Year,
                        ImdbId = movie.ImdbId,
                        Quality = movie.Quality,
                        SizeBytes = movie.SizeBytes,
                        ImdbRating = movie.ImdbRating,
                        Overview = movie.Overview,
                        PosterPath = movie.Images.FirstOrDefault(image => image.Type == "poster")?.Path,
                        FileName = movie.FileName,
                        Path = movie.Path,
                        VideoCodec = movie.VideoCodec,
                        AudioCodec = movie.AudioCodec,
                        AudioChannels = movie.AudioChannels,
                        Resolution = movie.Resolution,
                        RuntimeMinutes = movie.RuntimeMinutes,
                        SeasonCount = 0,
                        EpisodeCount = 1,
                    Tracks = BuildTracks(
                        movie.SubtitleCoverage ?? new Dictionary<string, MediaLanguageCoverage>(),
                        1,
                        TargetLanguageCodes(movie.TargetLanguagesOverride, globalTargetCodes)),
                    State = DeriveState(summary),
                    CompletedCount = summary?.Completed ?? 0,
                    FailedCount = summary?.Failed ?? 0,
                    LastActivityAt = summary?.LastActivity,
                    IncludeInTranslation = movie.IncludeInTranslation,
                    TranslationAgeThreshold = movie.TranslationAgeThreshold,
                    AudioLanguages = movie.AudioLanguages,
                    SuggestedAudioLanguage = movie.SuggestedLanguage,
                    NeedsNoSubtitles = movie.NoSubtitleNeeded,
                    AudioOverride = movie.AudioOverride,
                    TargetLanguagesOverride = OverrideCodesOnWire(movie.TargetLanguagesOverride)
                };
            }
        }

        return null;
    }

    /// <summary>
    /// B08: the stored override column → the effective code list the detail
    /// projection builds its tracks against (override &gt; global, empty
    /// stored string = explicit no-target), then wrapped into the
    /// TargetLanguage shape BuildTracks consumes.
    /// </summary>
    private static List<TargetLanguage> TargetLanguageCodes(
        string? storedOverride,
        List<string> globalTargetCodes) =>
        TargetLanguageResolver.Resolve(storedOverride, globalTargetCodes)
            .Select(code => new TargetLanguage { Name = code, Code = code })
            .ToList();

    /// <summary>
    /// B08 wire shape of the override: null = follow global, [] = explicit
    /// no-target, otherwise the parsed codes. Only the stored-value parse —
    /// never a global fallback, so the client can tell the two states apart.
    /// </summary>
    private static string[]? OverrideCodesOnWire(string? storedOverride) =>
        storedOverride is null
            ? (string[]?)null
            : [.. TargetLanguageResolver.ParseStored(storedOverride)];

    /// <summary>
    /// Newest request per episode rolled up to counts per show (scale audit
    /// P0-8). A NOT EXISTS anti-join keeps only the newest row per media (Id
    /// breaks UpdatedAt ties deterministically) and the grouping stays in
    /// SQL, so neither request payloads nor episode entities enter memory.
    /// When <paramref name="showIds"/> is given the aggregation narrows to
    /// those shows (media detail direct path / two-phase page hydration);
    /// null aggregates the whole library (legacy full-aggregation path).
    /// </summary>
    private async Task<Dictionary<int, MediaStatusSummary>> ShowStatusCountsAsync(
        IReadOnlyCollection<int>? showIds = null)
    {
        var episodes = _dbContext.Episodes
            .AsNoTracking()
            .AsQueryable();
        if (showIds is not null)
        {
            episodes = episodes.Where(e => showIds.Contains(e.Season.ShowId));
        }

        var rows = await episodes
            .Join(
                // Episode requests exist under both MediaType conventions:
                // request creation writes MediaType.Episode
                // (AutomatedTranslationJob → MediaSubtitleProcessor), the
                // legacy rows carry MediaType.Show. Both belong to the show
                // rollup — the same gate the mediaParentId filter applies
                // (scale audit P1-6); movie rows never do.
                _dbContext.TranslationRequests.AsNoTracking()
                    .Where(r => r.MediaType == MediaType.Show || r.MediaType == MediaType.Episode),
                e => (int?)e.Id,
                r => r.MediaId,
                (e, r) => new { ShowId = e.Season.ShowId, RequestId = r.Id, r.MediaId, r.Status, r.UpdatedAt })
            .Where(x => !_dbContext.TranslationRequests.Any(later =>
                // both conventions compete for "newest request per episode"
                (later.MediaType == MediaType.Show || later.MediaType == MediaType.Episode)
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
    /// <see cref="ShowStatusCountsAsync"/>; <paramref name="movieIds"/> narrows
    /// the aggregation to those movies (detail direct path / two-phase page
    /// hydration), null aggregates the whole library.
    /// </summary>
    private async Task<Dictionary<int, MediaStatusSummary>> MovieStatusCountsAsync(
        IReadOnlyCollection<int>? movieIds = null)
    {
        var query = _dbContext.TranslationRequests
            .AsNoTracking()
            .Where(r => r.MediaType == MediaType.Movie && r.MediaId != null);
        if (movieIds is not null)
        {
            query = query.Where(r => movieIds.Contains(r.MediaId!.Value));
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
        var show = row.MediaType == MediaType.Show ? shows?.GetValueOrDefault(row.Id) : null;
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
            Year = row.Year,
            ImdbId = row.ImdbId,
            // quality/size are movie-file concepts: show rows never carry
            // them, so their projections surface null by construction
            Quality = row.Quality,
            SizeBytes = row.SizeBytes,
            ImdbRating = row.ImdbRating,
            // shows union their episodes' audio (null when any episode is
            // unknown); movies take the column value straight from the row
            AudioLanguages = show is not null
                ? AggregateAudioLanguages(show.Seasons.SelectMany(season => season.Episodes)
                    .Select(episode => episode.AudioLanguages))
                : row.AudioLanguages,
            NeedsNoSubtitles = row.NeedsNoSubtitles,
            SuggestedAudioLanguage = row.SuggestedLanguage,
            Show = show
        };
    }

    /// <summary>
    /// Show-level audio aggregation over its episodes' audio lists: null when
    /// any episode's audio is unknown, otherwise the distinct union of the
    /// known codes. Public so the media endpoints aggregate the same way.
    /// </summary>
    public static string[]? AggregateAudioLanguages(IEnumerable<string[]?> episodeAudioLanguages)
    {
        var union = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var audio in episodeAudioLanguages)
        {
            if (audio == null)
            {
                return null;
            }

            foreach (var code in audio)
            {
                union.Add(code);
            }
        }

        return union.OrderBy(code => code, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    /// <summary>
    /// Track chips: every language found by the scan plus every configured
    /// target language (missing targets surface as "none" tracks). B07: the
    /// forced dimension rides the same per-language chip —
    /// <see cref="SubtitleTrackSummary.ForcedHave"/> carries the forced-only
    /// count while <see cref="SubtitleTrackSummary.Have"/>/State stay the
    /// FULL coverage verdict (a forced-only language reads "none").
    /// B04: <see cref="SubtitleTrackSummary.EmbeddedHave"/> rides the same
    /// chip the same way (9b0180f same-concept-badge-once ruling) — the
    /// snapshot's embedded dimension surfaces as its own mark while Have/
    /// State keep presenting the external-file verdict. Zero unless the
    /// embedded_track_counts_as_coverage setting was on when the snapshot
    /// was written.
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
                var slot = coverage.GetValueOrDefault(code);
                var have = slot?.Count ?? 0;
                return new SubtitleTrackSummary
                {
                    Language = code.ToLowerInvariant(),
                    Have = Math.Min(have, total),
                    Total = total,
                    ForcedHave = Math.Min(slot?.ForcedCount ?? 0, total),
                    EmbeddedHave = Math.Min(slot?.EmbeddedCount ?? 0, total),
                    State = have == 0 ? "none" : have >= total ? "complete" : "partial"
                };
            })
            .OrderBy(t => t.State == "complete" ? 0 : t.State == "partial" ? 1 : 2)
            .ThenBy(t => t.Language)
            .ToList();
    }

    private static bool MatchesFilters(MediaRow row, string? state, string? targetLanguage)
    {
        // The audio-aware states were already pushed into the media queries
        // (plain column predicates) — only request states filter here.
        var statePushedToSql = string.Equals(state, NativeState, StringComparison.OrdinalIgnoreCase)
                               || string.Equals(state, AudioUnknownState, StringComparison.OrdinalIgnoreCase);
        if (!string.IsNullOrEmpty(state) && !statePushedToSql
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
    /// Coverage JSON is deliberately absent on the two-phase path's rows:
    /// it hydrates for the page only.
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
        public int? Year { get; init; }
        public string? ImdbId { get; init; }
        public string? Quality { get; init; }
        public long? SizeBytes { get; init; }
        public double? ImdbRating { get; init; }
        public int SeasonCount { get; init; }
        public int EpisodeCount { get; init; }
        public string[]? AudioLanguages { get; init; }
        public string? SuggestedLanguage { get; init; }
        public bool NeedsNoSubtitles { get; init; }
        public MediaStatusSummary? Status { get; set; }
        public List<SubtitleTrackSummary> Tracks { get; set; } = [];
        public DateTimeOffset? LastActivityAt => Status?.LastActivity;
    }

    /// <summary>
    /// Phase-1 scan row of the two-phase default path: id + scalar columns +
    /// grouped season/episode counts. Never carries the coverage JSON — that
    /// is exactly what keeps default paging off the whole-library parse.
    /// </summary>
    private sealed class ShowScanRow
    {
        public string? SuggestedLanguage { get; set; }
        public int Id { get; set; }
        public string Title { get; set; } = string.Empty;
        public DateTimeOffset? DateAdded { get; set; }
        public bool IncludeInTranslation { get; set; }
        public int? TranslationAgeThreshold { get; set; }
        public int? Year { get; set; }
        public string? ImdbId { get; set; }
        public double? ImdbRating { get; set; }
        public int SeasonCount { get; set; }
        public int EpisodeCount { get; set; }
        public bool HasEpisodes { get; set; }
        public bool AllEpisodesNative { get; set; }
    }

    /// <summary>Movie counterpart of <see cref="ShowScanRow"/>.</summary>
    private sealed class MovieScanRow
    {
        public string? SuggestedLanguage { get; set; }
        public int Id { get; set; }
        public string Title { get; set; } = string.Empty;
        public DateTimeOffset? DateAdded { get; set; }
        public bool IncludeInTranslation { get; set; }
        public int? TranslationAgeThreshold { get; set; }
        public string[]? AudioLanguages { get; set; }
        public bool NoSubtitleNeeded { get; set; }
        public int? Year { get; set; }
        public string? ImdbId { get; set; }
        public string? Quality { get; set; }
        public long? SizeBytes { get; set; }
        public double? ImdbRating { get; set; }
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

/// <summary>
/// The shared subtitle-need rule (PRD card A, merged into audit S4): the
/// single source of truth used by the statistics scan (persists
/// needs_translation per movie/episode) and the display states. The manual
/// override endpoint short-circuits on the override branch of the same rule.
/// </summary>
public static class SubtitleNeedRule
{
    /// <summary>
    /// Persisted exemption: a manual mark wins (marking "not needed" exempts,
    /// marking "needed" un-exempts); otherwise known audio covering any
    /// target language exempts. Unknown audio (null) never exempts and with
    /// no configured targets nothing is exempt (conservative on both).
    /// </summary>
    /// <param name="audioOverride">Manual reversible mark; null = automatic.</param>
    /// <param name="audioLanguages">Normalized audio codes; null = unknown.</param>
    /// <param name="targetLanguages">Configured translation target codes.</param>
    public static bool NoSubtitleNeeded(
        bool? audioOverride,
        string[]? audioLanguages,
        IReadOnlyCollection<string> targetLanguages)
    {
        if (audioOverride.HasValue)
        {
            return !audioOverride.Value;
        }
        return audioLanguages is not null && audioLanguages.Any(audio =>
            targetLanguages.Contains(audio, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>
    /// needs = override.HasValue ? override.Value
    ///       : audio hits any target language ? false
    ///       : coverage lacks any target language.
    /// Unknown audio (null) never exempts — conservative, so "und" content
    /// keeps surfacing as a gap (PRD card A boundary ①). With no configured
    /// target languages nothing can be missing.
    /// </summary>
    /// <param name="audioOverride">Manual reversible mark; null = automatic.</param>
    /// <param name="audioLanguages">Normalized audio codes; null = unknown.</param>
    /// <param name="coveredLanguages">Subtitle languages the media already carries.</param>
    /// <param name="targetLanguages">Configured translation target codes.</param>
    public static bool NeedsTranslation(
        bool? audioOverride,
        string[]? audioLanguages,
        IEnumerable<string>? coveredLanguages,
        IReadOnlyCollection<string> targetLanguages)
    {
        if (audioOverride.HasValue)
        {
            return audioOverride.Value;
        }

        if (targetLanguages.Count == 0)
        {
            return false;
        }

        if (audioLanguages is not null && audioLanguages.Any(audio =>
                targetLanguages.Contains(audio, StringComparer.OrdinalIgnoreCase)))
        {
            return false;
        }

        return targetLanguages.Any(target =>
            coveredLanguages?.Contains(target, StringComparer.OrdinalIgnoreCase) != true);
    }
}
