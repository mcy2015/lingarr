using Lingarr.Core.Data;
using Lingarr.Core.Enum;
using Lingarr.Server.Modules.Infrastructure;
using Lingarr.Server.Modules.Settings;
using Lingarr.Server.Modules.SubtitleSearch.Abstractions;
using Lingarr.Server.Modules.SubtitleSearch.Scoring;
using Lingarr.Server.Modules.SubtitleSearch.Telemetry;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace Lingarr.Server.Modules.SubtitleSearch.Orchestration;

/// <summary>Why one source contributed nothing to a round.</summary>
/// <param name="Provider">Source key.</param>
/// <param name="Reason">Failure reason name ("Timeout", "RateLimited"…) or "SkippedCooldown".</param>
/// <param name="Error">Human-readable detail.</param>
public sealed record SubtitleSourceOutcome(string Provider, string Reason, string? Error);

/// <summary>
/// Aggregated result of one search round across the queried sources
/// (tech design ①/③): scored, deduped candidates ordered best-first, the
/// per-source outcomes, and the cache key under which the candidate list
/// is kept for the download endpoint (10min TTL, ②).
/// </summary>
/// <param name="CacheKey">UUID under which <see cref="Candidates"/> is cached.</param>
/// <param name="Candidates">Scored, deduped, ordered candidates.</param>
/// <param name="SourceOutcomes">Per-source failure/skip notes (empty when everything succeeded).</param>
public sealed record SubtitleSearchOutcome(
    string CacheKey,
    IReadOnlyList<SubtitleCandidate> Candidates,
    IReadOnlyList<SubtitleSourceOutcome> SourceOutcomes)
{
    public static SubtitleSearchOutcome Empty(string cacheKey) =>
        new(cacheKey, [], []);
}

/// <summary>
/// Source-parallel search orchestration (tech design ①/⑤):
/// <list type="bullet">
/// <item><see cref="Task.WhenAll"/> across the queried sources — one source
/// hanging never delays the others.</item>
/// <item>Per-source 10s budget enforced through a linked cancellation token
/// that is passed into the provider call. The token must penetrate the HTTP
/// layer itself: an HttpClient timeout wrapper does not unwrap a handler
/// that never returns (project lesson) — providers forward the token into
/// every SendAsync and the linked CTS fires it on schedule.</item>
/// <item>Cooled-down sources are skipped up front (⑧) and reported as
/// SkippedCooldown outcomes; a source failure is recorded in the cooldown
/// store when the ⑧ table prescribes one.</item>
/// <item>Candidates are scored (③), merged and deduped on
/// (language, normalized release, size) with equal scores resolved by
/// registration order (the site-priority default of ⑩).</item>
/// <item>Every source query lands a search-session row and a quota-upsert
/// (④ telemetry) — also the zero-result ones.</item>
/// </list>
/// </summary>
public sealed partial class SubtitleSearchOrchestrator
{
    /// <summary>Per-source search budget (design ②: 单源 10s).</summary>
    public static readonly TimeSpan SourceSearchTimeout = TimeSpan.FromSeconds(10);

    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(10);

    private readonly SubtitleProviderRegistry _registry;
    private readonly SubtitleMatchScorer _scorer;
    private readonly SubtitleSourceCooldownStore _cooldowns;
    private readonly SubtitleTelemetryService _telemetry;
    private readonly IMemoryCache _cache;
    private readonly ILogger<SubtitleSearchOrchestrator> _logger;
    private readonly TimeSpan _searchTimeout;
    private readonly LingarrDbContext? _dbContext;
    private readonly ISettingService? _settingService;

    /// <summary>
    /// What the 10min cache entry holds: the candidate list plus the
    /// per-candidate deciding search-session id and the media the round
    /// searched — the download endpoint runs in a different scope than the
    /// search, so the linkage must live in the singleton cache, not
    /// orchestrator instance state.
    /// </summary>
    private sealed record CachedOutcome(
        IReadOnlyList<SubtitleCandidate> Candidates,
        IReadOnlyDictionary<string, int> SessionByExternalId,
        MediaType MediaType,
        int MediaId);

    /// <summary>
    /// Production constructor — the one DI resolves (every parameter is a
    /// registered service, and it outranks the testing constructors on
    /// parameter count). Adds the design-⑧-polish dependencies: the
    /// DbContext loads the blacklist read side, the settings service reads
    /// the per-source enable switches.
    /// </summary>
    public SubtitleSearchOrchestrator(
        SubtitleProviderRegistry registry,
        SubtitleMatchScorer scorer,
        SubtitleSourceCooldownStore cooldowns,
        SubtitleTelemetryService telemetry,
        IMemoryCache cache,
        ILogger<SubtitleSearchOrchestrator> logger,
        LingarrDbContext dbContext,
        ISettingService settingService)
        : this(registry, scorer, cooldowns, telemetry, cache, logger, SourceSearchTimeout, dbContext, settingService)
    {
    }

    /// <summary>
    /// Legacy six-argument constructor kept source-compatible for existing
    /// test harnesses (SubtitleDownloadPipelineTests/SubtitleSearchJobTests
    /// build the orchestrator without the blacklist/settings wiring). Runs
    /// with both read-side features off — production DI always prefers the
    /// longer constructor above.
    /// </summary>
    public SubtitleSearchOrchestrator(
        SubtitleProviderRegistry registry,
        SubtitleMatchScorer scorer,
        SubtitleSourceCooldownStore cooldowns,
        SubtitleTelemetryService telemetry,
        IMemoryCache cache,
        ILogger<SubtitleSearchOrchestrator> logger)
        : this(registry, scorer, cooldowns, telemetry, cache, logger, SourceSearchTimeout)
    {
    }

    /// <summary>
    /// Testing constructor (public — no InternalsVisibleTo): same wiring
    /// with a shortened per-source budget so timeout paths assert in
    /// milliseconds instead of wall-clock 10s. The blacklist DbContext and
    /// settings service are optional trailing arguments so pre-existing
    /// positional call sites keep compiling.
    /// </summary>
    public SubtitleSearchOrchestrator(
        SubtitleProviderRegistry registry,
        SubtitleMatchScorer scorer,
        SubtitleSourceCooldownStore cooldowns,
        SubtitleTelemetryService telemetry,
        IMemoryCache cache,
        ILogger<SubtitleSearchOrchestrator> logger,
        TimeSpan searchTimeout,
        LingarrDbContext? dbContext = null,
        ISettingService? settingService = null)
    {
        _registry = registry;
        _scorer = scorer;
        _cooldowns = cooldowns;
        _telemetry = telemetry;
        _cache = cache;
        _logger = logger;
        _searchTimeout = searchTimeout;
        _dbContext = dbContext;
        _settingService = settingService;
    }

    /// <summary>
    /// Runs one aggregate search. <paramref name="singleProvider"/> limits
    /// the round to one source (the manual per-source endpoint, design ⑥);
    /// null queries every registered source that is not cooling down and
    /// not disabled through its <c>subtitle_{source}_enabled</c> switch
    /// (design ⑧ polish). A source explicitly named through
    /// <paramref name="singleProvider"/> is queried even when its switch is
    /// off — the user picked it by name, that outranks the switch (only the
    /// cooldown still guards it).
    /// </summary>
    public async Task<SubtitleSearchOutcome> SearchAsync(
        SubtitleSearchRequest request,
        MediaType mediaType,
        int mediaId,
        string? singleProvider,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var activeCooldowns = await _cooldowns.GetActiveAsync(cancellationToken);
        var outcomes = new List<SubtitleSourceOutcome>();

        RegisteredSubtitleProvider? single = null;
        if (singleProvider is not null)
        {
            single = _registry.Find(singleProvider);
            if (single is null)
            {
                throw new KeyNotFoundException($"Unknown subtitle provider '{singleProvider}'.");
            }
        }

        // Enable switches (⑧ polish): one batched settings read per round;
        // absent keys (and the unwired legacy constructors) mean enabled.
        var disabled = single is null
            ? await GetDisabledProvidersAsync(cancellationToken)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var targets = (single is not null ? [single] : _registry.All)
            .Where(entry => !activeCooldowns.ContainsKey(entry.Provider.Provider))
            .Where(entry => single is not null || !disabled.Contains(entry.Provider.Provider))
            .ToList();
        foreach (var skipped in (single is not null ? [single] : _registry.All)
                     .Where(entry => activeCooldowns.ContainsKey(entry.Provider.Provider)))
        {
            var cooldown = activeCooldowns[skipped.Provider.Provider];
            outcomes.Add(new SubtitleSourceOutcome(
                skipped.Provider.Provider,
                "SkippedCooldown",
                $"cooling down until {cooldown.Until:u} ({cooldown.Reason})."));
            // L4b: per-media repetition of a round-level fact — Debug. The
            // auto round announces the cooling set once per round at
            // Information; manual single-source searches are unaffected.
            _logger.LogDebug(
                "Skipping subtitle source {Provider}: cooldown until {Until}.",
                skipped.Provider.Provider, cooldown.Until);
        }

        // Blacklist read side (locked contract with the pipeline's write
        // side): exact (provider, external_id) pairs inside their expiry
        // window. Loaded once per round, before the dedupe, so a banned
        // upload never even competes. Read failure degrades to "no
        // blacklist" — a monitoring table must not sink the search round.
        var blacklist = await LoadBlacklistAsync(cancellationToken);

        // B07 hearing-impaired preference: read once per round, before the
        // searches, so the per-source ordering and the final chain apply the
        // same tiebreak. Absent key / read failure / the unwired legacy
        // constructors all mean false — the pre-B07 ordering.
        var preferHearingImpaired = await GetPreferHearingImpairedAsync(cancellationToken);

        var merged = new List<SubtitleCandidate>();
        var sessions = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (targets.Count > 0)
        {
            // One linked CTS per source so a timeout cancels exactly that
            // source's call; the caller's token stays linked to all of them.
            var searches = targets.Select(entry => SearchOneAsync(
                    entry.Provider, request, mediaType, mediaId, outcomes, sessions,
                    cancellationToken, preferHearingImpaired))
                .ToArray();
            var perSource = await Task.WhenAll(searches);
            foreach (var candidates in perSource)
            {
                merged.AddRange(candidates);
            }
        }

        var filtered = blacklist.Count == 0
            ? merged
            : merged.Where(candidate => !blacklist.Contains((candidate.Provider, candidate.ExternalId))).ToList();
        var ordered = Order(Dedupe(filtered), preferHearingImpaired);
        var cacheKey = Guid.NewGuid().ToString("N");
        _cache.Set(
            CacheKeyPrefix + cacheKey,
            new CachedOutcome(ordered, sessions, mediaType, mediaId),
            CacheTtl);

        // L4b: one Debug line per media search; the auto round aggregates
        // outcomes into its round-end Information summary instead.
        _logger.LogDebug(
            "Searched {MediaType} {MediaId} across {Sources} source(s): {Candidates} candidate(s). Source outcomes: {Outcomes}",
            mediaType, mediaId, targets.Count, ordered.Count,
            outcomes.Count == 0
                ? "none"
                : string.Join(", ", outcomes
                    .GroupBy(outcome => outcome.Reason)
                    .Select(group => $"{group.Key}×{group.Count()}")));

        return new SubtitleSearchOutcome(cacheKey, ordered, outcomes);
    }

    /// <summary>
    /// Source keys whose enable switch reads "false" this round. A settings
    /// read failure (or the unwired legacy constructors) yields an empty
    /// set — the round then queries everything rather than nothing.
    /// </summary>
    private async Task<HashSet<string>> GetDisabledProvidersAsync(CancellationToken cancellationToken)
    {
        if (_settingService is null)
        {
            return [];
        }

        try
        {
            var keys = _registry.All
                .Select(entry => SubtitleSourceSettingKeys.EnabledKey(entry.Provider.Provider))
                .ToList();
            var stored = await _settingService.GetSettings(keys);
            return _registry.All
                .Where(entry => stored.TryGetValue(
                    SubtitleSourceSettingKeys.EnabledKey(entry.Provider.Provider),
                    out var value) && !SubtitleSourceSettingKeys.IsEnabled(value))
                .Select(entry => entry.Provider.Provider)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not read the subtitle-source enable switches; querying every source.");
            return [];
        }
    }

    /// <summary>
    /// Active blacklist observations as an exact-match set of
    /// (provider, external_id) — ordinal comparison on both parts, mirroring
    /// the write side's equality predicate. LINQ over the DbSet, no raw SQL:
    /// the read must stay translatable whatever the DbContext mirror looks
    /// like. Operator rows without <c>expires_at</c> (null = keep showing)
    /// are included; their empty external id never matches a real candidate.
    /// </summary>
    private async Task<HashSet<(string Provider, string ExternalId)>> LoadBlacklistAsync(
        CancellationToken cancellationToken)
    {
        if (_dbContext is null)
        {
            return [];
        }

        try
        {
            var now = DateTimeOffset.UtcNow;
            var entries = await _dbContext.SubtitleSearchBlacklists
                .AsNoTracking()
                .Where(row => row.ExpiresAt == null || row.ExpiresAt > now)
                .Select(row => new { row.Provider, row.ExternalId })
                .ToListAsync(cancellationToken);
            return entries
                .Select(row => (row.Provider, row.ExternalId))
                .ToHashSet();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not load the subtitle search blacklist; filtering nothing this round.");
            return [];
        }
    }

    /// <summary>
    /// Resolves the download endpoint's cache key back to the candidate
    /// list; expired/unknown keys return null (the caller answers 404/410).
    /// Ownership-blind — prefer <see cref="TryResolveCache"/>.
    /// </summary>
    public IReadOnlyList<SubtitleCandidate>? ResolveCache(string cacheKey)
        => _cache.TryGetValue(CacheKeyPrefix + cacheKey, out CachedOutcome? outcome)
            ? outcome.Candidates
            : null;

    /// <summary>
    /// Verdict of a download-endpoint cache lookup (QA3 P3-2): the media
    /// binding of the search outcome decides what a replayed cache key may
    /// download — a key searched for one media item must not fetch into
    /// another (stale dialog against a different library row).
    /// </summary>
    public enum SubtitleCacheResolution
    {
        /// <summary>Key live and bound to the requested media.</summary>
        Resolved,

        /// <summary>Key unknown or past its TTL — the caller answers 410.</summary>
        Expired,

        /// <summary>Key live but bound to a different mediaType/mediaId — the caller answers 400.</summary>
        MediaMismatch
    }

    /// <summary>
    /// Ownership-checked variant of <see cref="ResolveCache"/> for the
    /// download endpoint: resolves the key AND verifies the requested
    /// (mediaType, mediaId) matches the media the search round queried.
    /// </summary>
    public SubtitleCacheResolution TryResolveCache(
        string cacheKey,
        MediaType mediaType,
        int mediaId,
        out IReadOnlyList<SubtitleCandidate>? candidates)
    {
        if (!_cache.TryGetValue(CacheKeyPrefix + cacheKey, out CachedOutcome? outcome))
        {
            candidates = null;
            return SubtitleCacheResolution.Expired;
        }

        if (outcome.MediaType != mediaType || outcome.MediaId != mediaId)
        {
            candidates = null;
            return SubtitleCacheResolution.MediaMismatch;
        }

        candidates = outcome.Candidates;
        return SubtitleCacheResolution.Resolved;
    }

    /// <summary>
    /// The deciding search-session id of one cached candidate — the session
    /// row of the source round that returned it; null when the candidate
    /// predates the linkage.
    /// </summary>
    public int? SessionIdOf(string cacheKey, SubtitleCandidate candidate)
        => _cache.TryGetValue(CacheKeyPrefix + cacheKey, out CachedOutcome? outcome)
           && outcome.SessionByExternalId.TryGetValue(candidate.ExternalId, out var sessionId)
            ? sessionId
            : null;

    internal const string CacheKeyPrefix = "subtitle-search-outcome:";

    /// <summary>
    /// Active cooldown map passthrough — the automated job reads it once per
    /// round to decide the queried-source set (it shares this orchestrator's
    /// scoped store).
    /// </summary>
    public Task<IReadOnlyDictionary<string, SubtitleSourceCooldown>> GetActiveCooldownsAsync(
        CancellationToken cancellationToken = default)
        => _cooldowns.GetActiveAsync(cancellationToken);

    /// <summary>The registered source keys in registration order (site-priority default, ⑩).</summary>
    public IReadOnlyList<string> RegisteredProviderKeys()
        => _registry.All.Select(entry => entry.Provider.Provider).ToList();

    private async Task<List<SubtitleCandidate>> SearchOneAsync(
        ISubtitleProvider provider,
        SubtitleSearchRequest request,
        MediaType mediaType,
        int mediaId,
        List<SubtitleSourceOutcome> outcomes,
        Dictionary<string, int> sessions,
        CancellationToken callerToken,
        bool preferHearingImpaired)
    {
        // The budget must reach the HTTP calls themselves: providers forward
        // this token into SendAsync, and a hanging handler unwinds the moment
        // it fires. Relying on HttpClient.Timeout alone does NOT unwrap a
        // handler whose SendAsync task never completes (project lesson).
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(callerToken);
        budget.CancelAfter(_searchTimeout);

        // L2 correlation: this source's slice of the round — every log line
        // from here down (provider internals, the HTTP gate, scoring, the
        // failure outcomes) carries the source key. The scope is per source
        // iteration and restores on every exit path; because CorrelationContext
        // writes copy-on-write, the parallel sibling tasks of the round's
        // Task.WhenAll each keep their own source key without stomping each
        // other, and the round flow that joins them stays unattributed (a
        // round spans one session per queried source — there is no single
        // round-level session id to stamp).
        using var correlation = CorrelationContext.Push(provider: provider.Provider);

        try
        {
            var result = await provider.SearchAsync(request, budget.Token);

            // Telemetry is monitoring, not business logic: a quota/session
            // write failure must never swallow the search round. The
            // session link is skipped when recording fails (null id).
            int? sessionId = null;
            try
            {
                await _telemetry.RecordSearchQuotaAsync(
                    provider.Provider, SubtitleTelemetryService.PublishedQuotaLimit(provider.Provider));
                sessionId = await _telemetry.RecordSearchSessionAsync(
                    mediaType,
                    mediaId,
                    provider.Provider,
                    string.Join(",", request.Languages),
                    result.Candidates.Count,
                    hitTarget: result.Candidates.Count > 0);
                // L2: the session row exists with a real id — stamp it so the
                // remaining logs of this source's flow (scoring, outcome
                // recording below, cooldown writes) grep together with the
                // subtitle_search_sessions row the history screen links to.
                CorrelationContext.SetSearchSessionId(sessionId);
            }
            catch (Exception telemetryError)
            {
                _logger.LogWarning(telemetryError,
                    "Subtitle telemetry recording failed for {Provider}; search continues.",
                    provider.Provider);
            }

            var scored = _scorer.ScoreAll(request, result.Candidates, preferHearingImpaired).ToList();
            foreach (var candidate in scored)
            {
                // First session wins — the dedupe keeps one candidate per
                // distinct hit, so collisions are the same file found twice.
                if (sessionId is { } id)
                {
                    sessions.TryAdd(candidate.ExternalId, id);
                }
            }

            return scored;
        }
        catch (OperationCanceledException) when (!callerToken.IsCancellationRequested)
        {
            // The 10s budget fired; caller cancellation is distinguished by
            // the filter and lands in the rethrow-only catch below.
            var error = $"{provider.Provider} exceeded the {_searchTimeout.TotalSeconds:0.#}s search budget.";
            outcomes.Add(new SubtitleSourceOutcome(provider.Provider, SubtitleProviderFailureReason.Timeout.ToString(), error));
            await RecordCooldownSafeAsync(provider.Provider,
                SubtitleProviderException.ForTimeout(provider.Provider));
            return [];
        }
        catch (SubtitleProviderException ex)
        {
            outcomes.Add(new SubtitleSourceOutcome(provider.Provider, ex.Reason.ToString(), ex.Message));
            await RecordCooldownSafeAsync(provider.Provider, ex);
            return [];
        }
        catch (OperationCanceledException)
        {
            // The caller gave up: propagate untouched — never an outcome
            // row, never a cooldown, never "Unexpected" (the pipeline's
            // DownloadAsync precedent; QA2 P2-1 found this missing).
            throw;
        }
        catch (Exception ex)
        {
            // Adapter crash: record, no cooldown (code problem, not source
            // health — ⑧'s InvalidResponse bucket).
            _logger.LogWarning(ex, "Subtitle source {Provider} crashed during search.", provider.Provider);
            outcomes.Add(new SubtitleSourceOutcome(provider.Provider, "Unexpected", ex.Message));
            return [];
        }
    }

    /// <summary>
    /// Cooldown recording must never fail the round — the store goes through
    /// the settings service; a settings hiccup logs and moves on.
    /// </summary>
    private async Task RecordCooldownSafeAsync(string provider, SubtitleProviderException exception)
    {
        try
        {
            await _cooldowns.RecordFailureAsync(provider, exception);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not persist the cooldown of {Provider}.", provider);
        }
    }

    /// <summary>
    /// Cross-source dedup (⑩): candidates equal on
    /// (language, normalized release, size) are the same subtitle; keep the
    /// best-scored one, ties resolved by input order — which within one
    /// source is the scorer's stable order, and across sources the
    /// registration order (the site-priority default of ⑩).
    /// </summary>
    internal List<SubtitleCandidate> Dedupe(IEnumerable<SubtitleCandidate> candidates)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var kept = new List<SubtitleCandidate>();
        foreach (var candidate in candidates.OrderByDescending(candidate => candidate.MatchScore))
        {
            var key = $"{candidate.Language}|{NormalizeRelease(candidate.ReleaseInfo)}|{candidate.Size}";
            if (seen.Add(key))
            {
                kept.Add(candidate);
            }
        }

        return kept;
    }

    /// <summary>
    /// Final ordering: score descending, then hash hits, then (B07, only
    /// with the prefer_hearing_impaired setting on) hearing-impaired over
    /// non-hi, then the two design-⑧-polish tiebreaks — bilingual over
    /// monolingual, then format preference (srt &gt; ass &gt; vtt, see
    /// <see cref="SubtitleMatchScorer.FormatPreference"/>) — and finally
    /// registration order (stable). Registration order equals the default
    /// site priority of ⑩ (OpenSubtitles first … shooter last). The hash
    /// verdict stays above the preference/polish tiebreaks: an exact-file
    /// match outranks any language/format preference. With the setting off
    /// the B07 key is a constant false and the stable sort keeps the
    /// pre-B07 order byte-identical (回归门).
    /// </summary>
    private List<SubtitleCandidate> Order(List<SubtitleCandidate> deduped, bool preferHearingImpaired)
        => deduped
            .Select((candidate, index) => (candidate, index))
            .OrderByDescending(entry => entry.candidate.MatchScore)
            .ThenByDescending(entry => entry.candidate.HashMatch)
            .ThenByDescending(entry => preferHearingImpaired && entry.candidate.HearingImpaired)
            .ThenByDescending(entry => SubtitleMatchScorer.IsBilingual(entry.candidate))
            .ThenBy(entry => SubtitleMatchScorer.FormatPreference(entry.candidate.Format))
            .ThenBy(entry => entry.index)
            .Select(entry => entry.candidate)
            .ToList();

    /// <summary>
    /// The B07 hearing-impaired preference flag
    /// (<see cref="Lingarr.Core.Configuration.SettingKeys.SubtitleSearch.PreferHearingImpaired"/>),
    /// one read per round. Absent key, settings hiccup or the unwired legacy
    /// constructors all degrade to false — a monitoring read must never sink
    /// the search round.
    /// </summary>
    private async Task<bool> GetPreferHearingImpairedAsync(CancellationToken cancellationToken)
    {
        if (_settingService is null)
        {
            return false;
        }

        try
        {
            return await _settingService.GetSetting(
                Lingarr.Core.Configuration.SettingKeys.SubtitleSearch.PreferHearingImpaired) == "true";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex,
                "Could not read the hearing-impaired preference; the candidate order is unchanged.");
            return false;
        }
    }

    /// <summary>
    /// Release normalization for the dedupe key. Must fold punctuation
    /// separators the same way the scorer does — "Movie.2019.WEBDL-1080p"
    /// and "movie 2019 webdl 1080p" are the same subtitle (⑩). An earlier
    /// version only lowercased and collapsed whitespace, so dotted and
    /// spaced variants of one release survived dedup as duplicates; the
    /// scorer's Normalize is the single normalization authority.
    /// </summary>
    private static string NormalizeRelease(string? release)
    {
        var value = (release ?? string.Empty).Trim();
        return value.Length == 0 ? string.Empty : SubtitleMatchScorer.Normalize(value);
    }
}
