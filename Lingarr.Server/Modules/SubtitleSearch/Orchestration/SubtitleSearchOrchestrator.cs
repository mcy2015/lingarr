using Lingarr.Core.Enum;
using Lingarr.Server.Modules.SubtitleSearch.Abstractions;
using Lingarr.Server.Modules.SubtitleSearch.Scoring;
using Lingarr.Server.Modules.SubtitleSearch.Telemetry;
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

    /// <summary>
    /// What the 10min cache entry holds: the candidate list plus the
    /// per-candidate deciding search-session id — the download endpoint runs
    /// in a different scope than the search, so the linkage must live in the
    /// singleton cache, not orchestrator instance state.
    /// </summary>
    private sealed record CachedOutcome(
        IReadOnlyList<SubtitleCandidate> Candidates,
        IReadOnlyDictionary<string, int> SessionByExternalId);

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
    /// milliseconds instead of wall-clock 10s.
    /// </summary>
    public SubtitleSearchOrchestrator(
        SubtitleProviderRegistry registry,
        SubtitleMatchScorer scorer,
        SubtitleSourceCooldownStore cooldowns,
        SubtitleTelemetryService telemetry,
        IMemoryCache cache,
        ILogger<SubtitleSearchOrchestrator> logger,
        TimeSpan searchTimeout)
    {
        _registry = registry;
        _scorer = scorer;
        _cooldowns = cooldowns;
        _telemetry = telemetry;
        _cache = cache;
        _logger = logger;
        _searchTimeout = searchTimeout;
    }

    /// <summary>
    /// Runs one aggregate search. <paramref name="singleProvider"/> limits
    /// the round to one source (the manual per-source endpoint, design ⑥);
    /// null queries every registered source that is not cooling down.
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

        var targets = (single is not null ? [single] : _registry.All)
            .Where(entry => !activeCooldowns.ContainsKey(entry.Provider.Provider))
            .ToList();
        foreach (var skipped in (single is not null ? [single] : _registry.All)
                     .Where(entry => activeCooldowns.ContainsKey(entry.Provider.Provider)))
        {
            var cooldown = activeCooldowns[skipped.Provider.Provider];
            outcomes.Add(new SubtitleSourceOutcome(
                skipped.Provider.Provider,
                "SkippedCooldown",
                $"cooling down until {cooldown.Until:u} ({cooldown.Reason})."));
            _logger.LogInformation(
                "Skipping subtitle source {Provider}: cooldown until {Until}.",
                skipped.Provider.Provider, cooldown.Until);
        }

        var merged = new List<SubtitleCandidate>();
        var sessions = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (targets.Count > 0)
        {
            // One linked CTS per source so a timeout cancels exactly that
            // source's call; the caller's token stays linked to all of them.
            var searches = targets.Select(entry => SearchOneAsync(
                    entry.Provider, request, mediaType, mediaId, outcomes, sessions, cancellationToken))
                .ToArray();
            var perSource = await Task.WhenAll(searches);
            foreach (var candidates in perSource)
            {
                merged.AddRange(candidates);
            }
        }

        var ordered = Order(Dedupe(merged));
        var cacheKey = Guid.NewGuid().ToString("N");
        _cache.Set(
            CacheKeyPrefix + cacheKey,
            new CachedOutcome(ordered, sessions),
            CacheTtl);

        return new SubtitleSearchOutcome(cacheKey, ordered, outcomes);
    }

    /// <summary>
    /// Resolves the download endpoint's cache key back to the candidate
    /// list; expired/unknown keys return null (the caller answers 404/410).
    /// </summary>
    public IReadOnlyList<SubtitleCandidate>? ResolveCache(string cacheKey)
        => _cache.TryGetValue(CacheKeyPrefix + cacheKey, out CachedOutcome? outcome)
            ? outcome.Candidates
            : null;

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
        CancellationToken callerToken)
    {
        // The budget must reach the HTTP calls themselves: providers forward
        // this token into SendAsync, and a hanging handler unwinds the moment
        // it fires. Relying on HttpClient.Timeout alone does NOT unwrap a
        // handler whose SendAsync task never completes (project lesson).
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(callerToken);
        budget.CancelAfter(_searchTimeout);

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
            }
            catch (Exception telemetryError)
            {
                _logger.LogWarning(telemetryError,
                    "Subtitle telemetry recording failed for {Provider}; search continues.",
                    provider.Provider);
            }

            var scored = _scorer.ScoreAll(request, result.Candidates).ToList();
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
            // The 10s budget fired (caller cancellation rethrows below).
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
    /// Final ordering: score descending, then hash hits, then registration
    /// order (stable). Registration order equals the default site priority
    /// of ⑩ (OpenSubtitles first … shooter last).
    /// </summary>
    private List<SubtitleCandidate> Order(List<SubtitleCandidate> deduped)
        => deduped
            .Select((candidate, index) => (candidate, index))
            .OrderByDescending(entry => entry.candidate.MatchScore)
            .ThenByDescending(entry => entry.candidate.HashMatch)
            .ThenBy(entry => entry.index)
            .Select(entry => entry.candidate)
            .ToList();

    private static string NormalizeRelease(string? release)
    {
        var value = (release ?? string.Empty).Trim().ToLowerInvariant();
        return value.Length == 0 ? string.Empty : WhitespaceRunRegex().Replace(value, " ");
    }

    [System.Text.RegularExpressions.GeneratedRegex("\\s+")]
    private static partial System.Text.RegularExpressions.Regex WhitespaceRunRegex();
}
