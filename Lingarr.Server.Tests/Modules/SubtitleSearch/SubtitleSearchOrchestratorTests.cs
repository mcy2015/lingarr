using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Lingarr.Core.Data;
using Lingarr.Core.Enum;
using Lingarr.Server.Modules.Settings;
using Lingarr.Server.Modules.SubtitleSearch;
using Lingarr.Server.Modules.SubtitleSearch.Abstractions;
using Lingarr.Server.Modules.SubtitleSearch.Orchestration;
using Lingarr.Server.Modules.SubtitleSearch.Scoring;
using Lingarr.Server.Modules.SubtitleSearch.Telemetry;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;
using Xunit;

namespace Lingarr.Server.Tests.Modules.SubtitleSearch;

/// <summary>
/// Stateful <see cref="ISettingService"/> double — the cooldown store
/// round-trips its map through GetSetting/SetSetting, so cooldown tests
/// need real storage, not a stateless Moq (the true cause behind the
/// previously skipped cooldown tests: RecordFailureAsync wrote into a mock
/// that never answered reads).
/// </summary>
internal sealed class InMemorySettingService : ISettingService
{
    private readonly Dictionary<string, string> _values = new();

    /// <summary>
    /// Artificial read latency used to widen read-modify-write race windows
    /// in the cooldown-serialization tests; 0 in normal use.
    /// </summary>
    internal int ReadDelayMilliseconds { get; set; }

    public event SettingChangedHandler? SettingChanged;

    public async Task<string?> GetSetting(string key)
    {
        if (ReadDelayMilliseconds > 0)
        {
            await Task.Delay(ReadDelayMilliseconds);
        }

        return _values.TryGetValue(key, out var value) ? value : null;
    }

    public Task<Dictionary<string, string>> GetSettings(IEnumerable<string> keys)
        => Task.FromResult(keys.Where(_values.ContainsKey).ToDictionary(key => key, key => _values[key]));

    public Task<bool> SetSetting(string key, string value)
    {
        _values[key] = value;
        return Task.FromResult(true);
    }

    public Task<bool> SetSettings(Dictionary<string, string> settings)
    {
        foreach (var (key, value) in settings)
        {
            _values[key] = value;
        }

        return Task.FromResult(true);
    }

    public Task<List<T>> GetSettingAsJson<T>(string key) where T : class
    {
        if (!_values.TryGetValue(key, out var json))
        {
            return Task.FromResult(new List<T>());
        }

        return Task.FromResult(System.Text.Json.JsonSerializer.Deserialize<List<T>>(json) ?? []);
    }

    public Task<bool> SetEncryptedSetting(string key, string value)
    {
        _values["secret:" + key] = value;
        return Task.FromResult(true);
    }

    public Task<string?> GetEncryptedSetting(string key)
        => Task.FromResult(_values.TryGetValue("secret:" + key, out var value) ? value : null);

    public Task<Dictionary<string, bool>> GetEncryptedSettingStates(IEnumerable<string> keys)
        => Task.FromResult(keys.Where(_values.ContainsKey)
            .ToDictionary(key => key, key => !string.IsNullOrWhiteSpace(_values["secret:" + key])));
}

/// <summary>
/// Orchestrator contracts (design ①/⑤/⑧, M3 acceptance): source-parallel
/// search with the 10s budget penetrating into the provider call (a hanging
/// provider unwinds via the cancellation token — never via HttpClient's
/// timeout wrapper), cooldown skips, cross-source dedup on
/// (language, release, size), per-source failure outcomes with cooldown
/// persistence, and the candidate cache that the download endpoint
/// resolves. Runs on real SQLite (telemetry writes).
/// </summary>
public class SubtitleSearchOrchestratorTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly OrchestratorDbContext _dbContext;
    private readonly InMemorySettingService _settings = new();
    private readonly MemoryCache _cache = new(new MemoryCacheOptions());
    private readonly SubtitleTelemetryService _telemetry;

    public SubtitleSearchOrchestratorTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _dbContext = new OrchestratorDbContext(new DbContextOptionsBuilder<LingarrDbContext>()
            .UseSqlite(_connection)
            .UseSnakeCaseNamingConvention()
            .Options);
        _dbContext.Database.EnsureCreated();
        _telemetry = new SubtitleTelemetryService(_dbContext);
    }

    public void Dispose()
    {
        _cache.Dispose();
        _dbContext.Dispose();
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    private sealed class OrchestratorDbContext(DbContextOptions options) : LingarrDbContext(options);

    /// <summary>
    /// SaveChanges flake double for the telemetry-detach contract: fails the
    /// next save once, then behaves normally.
    /// </summary>
    private sealed class FlakyDbContext(DbContextOptions options) : LingarrDbContext(options)
    {
        public bool ThrowOnNextSave;

        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            if (ThrowOnNextSave)
            {
                ThrowOnNextSave = false;
                throw new InvalidOperationException("simulated telemetry save failure");
            }

            return base.SaveChangesAsync(cancellationToken);
        }
    }

    /// <summary>
    /// Provider double: search answers through a scriptable handler, so the
    /// hanging case is a handler that never completes before cancellation —
    /// the StubHttpMessageHandler trick, inlined for orchestrator scope.
    /// </summary>
    private sealed class ScriptedProvider : ISubtitleProvider
    {
        private readonly Func<CancellationToken, Task<SubtitleSearchResult>> _search;

        public int SearchCalls { get; private set; }

        public ScriptedProvider(Func<CancellationToken, Task<SubtitleSearchResult>> search)
        {
            _search = search;
        }

        public string Provider { get; set; } = "scripted";

        public async Task<SubtitleSearchResult> SearchAsync(
            SubtitleSearchRequest request, CancellationToken cancellationToken)
        {
            SearchCalls++;
            var result = await _search(cancellationToken);
            // Stamp candidates with this provider's key so dedupe and
            // provider assertions match the scripted identity.
            return new SubtitleSearchResult(
                result.Candidates.Select(c => c with { Provider = Provider }).ToList());
        }

        public Task<SubtitleDownload> DownloadAsync(
            SubtitleCandidateRef candidateRef, CancellationToken cancellationToken)
            => throw new NotSupportedException("search-only double");
    }

    private SubtitleSearchOrchestrator Create(
        params ISubtitleProvider[] providers) => Create(TimeSpan.FromSeconds(10), providers);

    private SubtitleSearchOrchestrator Create(
        TimeSpan timeout,
        params ISubtitleProvider[] providers)
    {
        var registry = new SubtitleProviderRegistry(
            providers, Array.Empty<ISubtitleSourceManifest>(), NullLogger<SubtitleProviderRegistry>.Instance);
        var cooldowns = new SubtitleSourceCooldownStore(
            _settings, NullLogger<SubtitleSourceCooldownStore>.Instance);
        return new SubtitleSearchOrchestrator(
            registry,
            new SubtitleMatchScorer(),
            cooldowns,
            _telemetry,
            _cache,
            _orchestratorLogger,
            timeout,
            _dbContext,
            _settings);
    }

    /// <summary>
    /// Console-backed logger: the orchestrator's telemetry catch only warns,
    /// and a NullLogger there turned "why did the session row not persist"
    /// into a guessing game during the R-fix gate. Visible in `dotnet test`
    /// output without xUnit capture.
    /// </summary>
    private static readonly ILogger<SubtitleSearchOrchestrator> _orchestratorLogger =
        LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Warning).AddConsole())
            .CreateLogger<SubtitleSearchOrchestrator>();

    private static SubtitleCandidate Candidate(
        string language = "en",
        string release = "Movie.2019.WEBDL-1080p",
        bool hashMatch = false,
        string? provider = null) => new(
        provider ?? "scripted",
        ExternalId: "ext-" + Guid.NewGuid().ToString("N"),
        language,
        MatchScore: 0,
        Forced: false,
        HearingImpaired: false,
        release,
        Uploader: null,
        Format: "srt",
        Size: 1234,
        hashMatch);

    private static SubtitleSearchRequest Request() => new(
        "Movie", 2019, Season: null, Episode: null,
        ReleaseName: "Movie.2019.WEBDL-1080p", VideoHash: null, Languages: ["en"], ImdbId: null);

    [Fact]
    public async Task Search_MergesScoresAndOrdersBestFirst()
    {
        var high = new ScriptedProvider(_ => Task.FromResult(new SubtitleSearchResult(
            [Candidate(release: "Movie.2019.WEBDL-1080p")]))) { Provider = "alpha" };
        var exact = new ScriptedProvider(_ => Task.FromResult(new SubtitleSearchResult(
            [Candidate(release: "Something.Else.2019", hashMatch: true)]))) { Provider = "beta" };
        var orchestrator = Create(high, exact);

        var outcome = await orchestrator.SearchAsync(Request(), MediaType.Movie, 1, null, CancellationToken.None);

        // Hash hit short-circuits at 100 and leads the order; the release
        // match scores below and stays second.
        Assert.Equal(2, outcome.Candidates.Count);
        Assert.Equal(100, outcome.Candidates[0].MatchScore);
        Assert.True(outcome.Candidates[0].HashMatch);
        Assert.True(outcome.Candidates[1].MatchScore >= 55);
        Assert.Empty(outcome.SourceOutcomes);
    }

    [Fact]
    public async Task Search_DedupesAcrossSourcesOnLanguageReleaseSize()
    {
        var first = new ScriptedProvider(_ => Task.FromResult(new SubtitleSearchResult(
            new[] { Candidate(release: "Movie.2019.WEBDL-1080p", hashMatch: true) }))) { Provider = "alpha" };
        var second = new ScriptedProvider(_ => Task.FromResult(new SubtitleSearchResult(
            new[]
            {
                Candidate(release: "movie 2019 webdl 1080p"),          // same after normalization → dup
                Candidate(release: "Movie.2019.BluRay-720p", language: "zh") // distinct → kept
            })))
        { Provider = "beta" };
        var orchestrator = Create(first, second);

        var outcome = await orchestrator.SearchAsync(Request(), MediaType.Movie, 1, null, CancellationToken.None);

        Assert.Equal(2, outcome.Candidates.Count);
        // The deduped survivor is the hash hit (higher score wins the key).
        Assert.Equal(100, outcome.Candidates[0].MatchScore);
        Assert.Contains(outcome.Candidates, candidate => candidate.Language == "zh");
    }

    [Fact]
    public async Task Search_HangingSourceUnwindsThroughTheBudget_NotTheHttpClientTimeout()
    {
        // The provider hangs until its token fires — the acceptance shape
        // of "HttpClient's timeout wrapper never unwraps a handler that
        // never returns": only the orchestrator's linked CTS ends it.
        var hanging = new ScriptedProvider(async token =>
        {
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            await using (token.Register(() => tcs.TrySetResult(true)))
            {
                await tcs.Task;
            }

            throw new OperationCanceledException(token);
        })
        { Provider = "hanging" };
        var quick = new ScriptedProvider(_ => Task.FromResult(new SubtitleSearchResult(
            [Candidate()]))) { Provider = "quick" };
        var orchestrator = Create(TimeSpan.FromMilliseconds(100), hanging, quick);

        var outcome = await orchestrator.SearchAsync(Request(), MediaType.Movie, 1, null, CancellationToken.None);

        // The healthy source's result survived; the hanging one is a
        // Timeout outcome with a persisted cooldown.
        Assert.Single(outcome.Candidates);
        var timeout = Assert.Single(outcome.SourceOutcomes);
        Assert.Equal("hanging", timeout.Provider);
        Assert.Equal(SubtitleProviderFailureReason.Timeout.ToString(), timeout.Reason);

        var cooldowns = await orchestrator.GetActiveCooldownsAsync();
        Assert.True(cooldowns.ContainsKey("hanging"));
    }

    [Fact]
    public async Task Search_CooledDownSourcesAreSkippedAndReported()
    {
        var cooled = new ScriptedProvider(_ => Task.FromResult(SubtitleSearchResult.Empty)) { Provider = "cooled" };
        var live = new ScriptedProvider(_ => Task.FromResult(new SubtitleSearchResult([Candidate()])))
        { Provider = "live" };
        var orchestrator = Create(cooled, live);

        // The cooldown persists through the real settings round-trip — the
        // stateful stub is what the earlier skip hid (a stateless Moq never
        // answered the round-start read).
        await new SubtitleSourceCooldownStore(
                _settings, NullLogger<SubtitleSourceCooldownStore>.Instance)
            .RecordFailureAsync("cooled", SubtitleProviderException.ForHttpStatus(
                "cooled", System.Net.HttpStatusCode.TooManyRequests));

        var outcome = await orchestrator.SearchAsync(Request(), MediaType.Movie, 1, null, CancellationToken.None);

        Assert.Equal(0, cooled.SearchCalls);
        var skipped = Assert.Single(outcome.SourceOutcomes);
        Assert.Equal("cooled", skipped.Provider);
        Assert.Equal("SkippedCooldown", skipped.Reason);
        Assert.Single(outcome.Candidates);
    }

    [Fact]
    public async Task Search_SourceFailureBecomesAnOutcomeWithoutKillingTheRound()
    {
        var failing = new ScriptedProvider(_ => throw SubtitleProviderException.ForMissingCredentials(
            "failing", "API key")) { Provider = "failing" };
        var orchestrator = Create(failing);

        var outcome = await orchestrator.SearchAsync(Request(), MediaType.Movie, 1, null, CancellationToken.None);

        Assert.Empty(outcome.Candidates);
        var failure = Assert.Single(outcome.SourceOutcomes);
        Assert.Equal(SubtitleProviderFailureReason.MissingCredentials.ToString(), failure.Reason);

        // MissingCredentials carries no cooldown (⑧) — nothing persisted.
        var cooldowns = await orchestrator.GetActiveCooldownsAsync();
        Assert.Empty(cooldowns);
    }

    [Fact]
    public async Task Search_SingleProviderModeQueriesOnlyThatSource()
    {
        var alpha = new ScriptedProvider(_ => Task.FromResult(new SubtitleSearchResult([Candidate()])))
        { Provider = "alpha" };
        var beta = new ScriptedProvider(_ => Task.FromResult(new SubtitleSearchResult([Candidate()])))
        { Provider = "beta" };
        var orchestrator = Create(alpha, beta);

        var outcome = await orchestrator.SearchAsync(Request(), MediaType.Movie, 1, "beta", CancellationToken.None);
        Assert.Equal(0, alpha.SearchCalls);
        Assert.Equal(1, beta.SearchCalls);
        Assert.Equal("beta", outcome.Candidates.Single().Provider);
    }

    // --- blacklist read side (locked contract with the pipeline write side,
    // key = (provider, external_id) exact match inside the 90-day window).

    private static SubtitleCandidate CandidateWithId(
        string externalId,
        string language = "en",
        string release = "Movie.2019.WEBDL-1080p",
        string format = "srt",
        long size = 1234) => new(
        "scripted",
        externalId,
        language,
        MatchScore: 0,
        Forced: false,
        HearingImpaired: false,
        release,
        Uploader: null,
        format,
        size,
        HashMatch: false);

    [Fact]
    public async Task Search_BlacklistedCandidatesAreFilteredOut()
    {
        // The write side (SubtitleDownloadPipeline) stamps the provider of
        // the failing candidate and the cache-opaque external id — the read
        // side must filter on exactly that pair.
        _dbContext.SubtitleSearchBlacklists.Add(new Lingarr.Core.Entities.SubtitleSearchBlacklist
        {
            Provider = "alpha",
            ExternalId = "banned-id",
            Reason = "unparseable archive",
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(90)
        });
        await _dbContext.SaveChangesAsync();

        var alpha = new ScriptedProvider(_ => Task.FromResult(new SubtitleSearchResult(
        [
            CandidateWithId("banned-id"),
            CandidateWithId("clean-id")
        ])))
        { Provider = "alpha" };
        var orchestrator = Create(alpha);

        var outcome = await orchestrator.SearchAsync(Request(), MediaType.Movie, 1, null, CancellationToken.None);

        Assert.Single(outcome.Candidates);
        Assert.Equal("clean-id", outcome.Candidates[0].ExternalId);
    }

    [Fact]
    public async Task Search_ExpiredBlacklistEntryDoesNotFilter()
    {
        _dbContext.SubtitleSearchBlacklists.Add(new Lingarr.Core.Entities.SubtitleSearchBlacklist
        {
            Provider = "alpha",
            ExternalId = "banned-id",
            Reason = "unparseable archive",
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(-1) // outside the 90-day window
        });
        await _dbContext.SaveChangesAsync();

        var alpha = new ScriptedProvider(_ => Task.FromResult(new SubtitleSearchResult(
            [CandidateWithId("banned-id")])))
        { Provider = "alpha" };
        var orchestrator = Create(alpha);

        var outcome = await orchestrator.SearchAsync(Request(), MediaType.Movie, 1, null, CancellationToken.None);

        Assert.Single(outcome.Candidates);
    }

    [Fact]
    public async Task Search_BlacklistOfAnotherProviderDoesNotFilterSameExternalId()
    {
        // Exact pair matching: beta's row about "banned-id" must not punish
        // alpha's candidate with the same opaque id.
        _dbContext.SubtitleSearchBlacklists.Add(new Lingarr.Core.Entities.SubtitleSearchBlacklist
        {
            Provider = "beta",
            ExternalId = "banned-id",
            Reason = "empty payload",
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(90)
        });
        await _dbContext.SaveChangesAsync();

        var alpha = new ScriptedProvider(_ => Task.FromResult(new SubtitleSearchResult(
            [CandidateWithId("banned-id")])))
        { Provider = "alpha" };
        var orchestrator = Create(alpha);

        var outcome = await orchestrator.SearchAsync(Request(), MediaType.Movie, 1, null, CancellationToken.None);

        Assert.Single(outcome.Candidates);
    }

    // --- per-source enable switches (design ⑧ polish: absent key = enabled,
    // aggregate rounds skip disabled sources, an explicitly named source is
    // always queried).

    [Fact]
    public async Task Search_DisabledSourceIsSkippedInAggregateRounds()
    {
        var alpha = new ScriptedProvider(_ => Task.FromResult(new SubtitleSearchResult([Candidate()])))
        { Provider = "alpha" };
        var beta = new ScriptedProvider(_ => Task.FromResult(new SubtitleSearchResult([Candidate()])))
        { Provider = "beta" };
        await _settings.SetSetting("subtitle_alpha_enabled", "false");
        var orchestrator = Create(alpha, beta);

        var outcome = await orchestrator.SearchAsync(Request(), MediaType.Movie, 1, null, CancellationToken.None);

        Assert.Equal(0, alpha.SearchCalls);
        Assert.Equal(1, beta.SearchCalls);
        Assert.Equal("beta", outcome.Candidates.Single().Provider);
        // A disabled source is not a failure — no outcome row for it.
        Assert.Empty(outcome.SourceOutcomes);
    }

    [Fact]
    public async Task Search_DisabledSourceIsStillQueriedWhenNamedExplicitly()
    {
        var alpha = new ScriptedProvider(_ => Task.FromResult(new SubtitleSearchResult([Candidate()])))
        { Provider = "alpha" };
        await _settings.SetSetting("subtitle_alpha_enabled", "false");
        var orchestrator = Create(alpha);

        var outcome = await orchestrator.SearchAsync(Request(), MediaType.Movie, 1, "alpha", CancellationToken.None);

        Assert.Equal(1, alpha.SearchCalls);
        Assert.Single(outcome.Candidates);
    }

    [Fact]
    public async Task Search_EnabledDefaultsToTrueWhenTheKeyIsAbsent()
    {
        // No key stored at all — the source must stay in the round (the
        // rollout needs no migration).
        var alpha = new ScriptedProvider(_ => Task.FromResult(new SubtitleSearchResult([Candidate()])))
        { Provider = "alpha" };
        var orchestrator = Create(alpha);

        var outcome = await orchestrator.SearchAsync(Request(), MediaType.Movie, 1, null, CancellationToken.None);

        Assert.Equal(1, alpha.SearchCalls);
        Assert.Single(outcome.Candidates);
    }

    // --- bilingual / format tiebreaks (design ⑧ polish, applied after
    // score + hash, before registration order).

    [Fact]
    public async Task Search_AtEqualScoreABilingualCandidateRanksBeforeAMonolingualOne()
    {
        // Monolingual registers FIRST — registration order alone would keep
        // it ahead, so the assertion proves the bilingual tiebreak moved it.
        var mono = new ScriptedProvider(_ => Task.FromResult(new SubtitleSearchResult(
            [CandidateWithId("mono", language: "zh")])))
        { Provider = "alpha" };
        var bilingual = new ScriptedProvider(_ => Task.FromResult(new SubtitleSearchResult(
            [CandidateWithId("bi", language: "zh+en")])))
        { Provider = "beta" };
        var orchestrator = Create(mono, bilingual);

        var outcome = await orchestrator.SearchAsync(Request(), MediaType.Movie, 1, null, CancellationToken.None);

        Assert.Equal(2, outcome.Candidates.Count);
        Assert.Equal(outcome.Candidates[0].MatchScore, outcome.Candidates[1].MatchScore);
        Assert.Equal("bi", outcome.Candidates[0].ExternalId);
        Assert.Equal("mono", outcome.Candidates[1].ExternalId);
    }

    [Fact]
    public async Task Search_AtEqualScoreAndBilingualSrtRanksBeforeAss()
    {
        // Same language and release, distinct sizes so both survive the
        // dedupe; ass registers first so only the format tiebreak reorders.
        var ass = new ScriptedProvider(_ => Task.FromResult(new SubtitleSearchResult(
            [CandidateWithId("ass", language: "zh+en", format: "ass", size: 2222)])))
        { Provider = "alpha" };
        var srt = new ScriptedProvider(_ => Task.FromResult(new SubtitleSearchResult(
            [CandidateWithId("srt", language: "zh+en", format: "srt", size: 3333)])))
        { Provider = "beta" };
        var orchestrator = Create(ass, srt);

        var outcome = await orchestrator.SearchAsync(Request(), MediaType.Movie, 1, null, CancellationToken.None);

        Assert.Equal(2, outcome.Candidates.Count);
        Assert.Equal(outcome.Candidates[0].MatchScore, outcome.Candidates[1].MatchScore);
        Assert.Equal("srt", outcome.Candidates[0].ExternalId);
        Assert.Equal("ass", outcome.Candidates[1].ExternalId);
    }

    // --- daily-limit cooldown (⑧ same-day cooldown until UTC midnight).

    [Fact]
    public async Task CooldownStore_DailyLimitCoolsDownUntilTheNextUtcMidnight()
    {
        var store = new SubtitleSourceCooldownStore(
            _settings, NullLogger<SubtitleSourceCooldownStore>.Instance);
        var before = DateTimeOffset.UtcNow;

        await store.RecordFailureAsync("opensubtitles", SubtitleProviderException.ForDailyLimit("opensubtitles"));

        var active = await store.GetActiveAsync();
        var cooldown = active["opensubtitles"];
        Assert.Equal(SubtitleProviderFailureReason.DailyLimit.ToString(), cooldown.Reason);
        // Naive-UTC midnight arithmetic: some instant of the next UTC day,
        // exactly on the day boundary.
        Assert.Equal(TimeSpan.Zero, cooldown.Until.TimeOfDay);
        var midnight = new DateTimeOffset(cooldown.Until.UtcDateTime.Date, TimeSpan.Zero);
        Assert.Equal(cooldown.Until, midnight);
        Assert.InRange(cooldown.Until, before, before.AddHours(25));
    }

    [Fact]
    public async Task Search_DailyLimitFailureBecomesAnOutcomeAndAMidnightCooldown()
    {
        var exhausted = new ScriptedProvider(_ => throw SubtitleProviderException.ForDailyLimit(
            "daily", resetEpochSeconds: DateTimeOffset.UtcNow.AddDays(1).ToUnixTimeSeconds()))
        { Provider = "daily" };
        var orchestrator = Create(exhausted);

        var outcome = await orchestrator.SearchAsync(Request(), MediaType.Movie, 1, null, CancellationToken.None);

        var failure = Assert.Single(outcome.SourceOutcomes);
        Assert.Equal(SubtitleProviderFailureReason.DailyLimit.ToString(), failure.Reason);

        var cooldowns = await orchestrator.GetActiveCooldownsAsync();
        Assert.Equal(TimeSpan.Zero, cooldowns["daily"].Until.TimeOfDay);
    }

    // --- R3 quota circuit-breaker: a DailyLimit verdict that carries the
    // source's own reset moment (the OpenSubtitles 406 reset_time_utc shape)
    // cools down until exactly that moment under the quota_exhausted bucket;
    // the header-less 429 shape above keeps its enum-name reason and the
    // midnight fallback untouched.

    [Fact]
    public async Task CooldownStore_QuotaExhaustedWithACapturedResetCoolsDownUntilIt()
    {
        var store = new SubtitleSourceCooldownStore(
            _settings, NullLogger<SubtitleSourceCooldownStore>.Instance);
        var resetUtc = DateTimeOffset.UtcNow.AddHours(6);

        await store.RecordFailureAsync("opensubtitles", SubtitleProviderException.ForDailyLimit(
            "opensubtitles", httpStatus: 406, resetUtc: resetUtc));

        var cooldown = (await store.GetActiveAsync())["opensubtitles"];
        Assert.Equal(resetUtc, cooldown.Until);
        Assert.Equal(SubtitleSourceCooldownStore.QuotaExhaustedReason, cooldown.Reason);
        Assert.Contains("daily quota", cooldown.Error);
    }

    [Fact]
    public async Task CooldownStore_QuotaExhaustedWithAStaleResetFallsBackToUtcMidnight()
    {
        // A reset moment already in the past (clock skew, stale header) must
        // not un-block the source: the conservative midnight fallback applies.
        var store = new SubtitleSourceCooldownStore(
            _settings, NullLogger<SubtitleSourceCooldownStore>.Instance);

        await store.RecordQuotaExhaustedAsync("opensubtitles", DateTimeOffset.UtcNow.AddMinutes(-5));

        var cooldown = (await store.GetActiveAsync())["opensubtitles"];
        Assert.Equal(SubtitleSourceCooldownStore.QuotaExhaustedReason, cooldown.Reason);
        Assert.Equal(TimeSpan.Zero, cooldown.Until.TimeOfDay);
        Assert.Equal(cooldown.Until, new DateTimeOffset(cooldown.Until.UtcDateTime.Date, TimeSpan.Zero));
    }

    [Fact]
    public async Task Search_QuotaExhaustedVerdictCoolsDownUntilTheReportedReset()
    {
        // The search-side mirror of the R3 406 rule: the orchestrator's
        // generic failure recording honors the exception-carried reset.
        var resetUtc = DateTimeOffset.UtcNow.AddHours(6);
        var exhausted = new ScriptedProvider(_ => throw SubtitleProviderException.ForDailyLimit(
            "daily", httpStatus: 406, resetUtc: resetUtc))
        { Provider = "daily" };
        var orchestrator = Create(exhausted);

        var outcome = await orchestrator.SearchAsync(Request(), MediaType.Movie, 1, null, CancellationToken.None);

        var failure = Assert.Single(outcome.SourceOutcomes);
        Assert.Equal(SubtitleProviderFailureReason.DailyLimit.ToString(), failure.Reason);

        var cooldowns = await orchestrator.GetActiveCooldownsAsync();
        Assert.Equal(resetUtc, cooldowns["daily"].Until);
        Assert.Equal(SubtitleSourceCooldownStore.QuotaExhaustedReason, cooldowns["daily"].Reason);
    }

    /// <summary>
    /// Direct telemetry regression pin: a null quota-remaining parameter
    /// used to throw "Value must be set" (raw null instead of DBNull.Value),
    /// which — because the orchestrator calls quota before the session save
    /// inside one try — silently killed every telemetry write.
    /// </summary>
    [Fact]
    public async Task Telemetry_DirectWrites_SessionAndQuotaRows()
    {
        var id = await _telemetry.RecordSearchSessionAsync(
            MediaType.Movie, 1, "alpha", "en", 0, hitTarget: false);
        await _telemetry.RecordSearchQuotaAsync("alpha");

        Assert.True(id > 0);
        Assert.Equal("alpha", (await _dbContext.SubtitleSearchSessions.SingleAsync()).Provider);
        Assert.Equal(1, (await _dbContext.ProviderQuotaUsages.SingleAsync()).Searches);
    }

    [Fact]
    public async Task Search_RecordsSessionsAndQuota_EvenForZeroResults()
    {
        var empty = new ScriptedProvider(_ => Task.FromResult(SubtitleSearchResult.Empty)) { Provider = "alpha" };
        var orchestrator = Create(empty);

        await orchestrator.SearchAsync(Request(), MediaType.Movie, 42, null, CancellationToken.None);

        var session = await _dbContext.SubtitleSearchSessions.SingleAsync();
        Assert.Equal("alpha", session.Provider);
        Assert.Equal(42, session.MediaId);
        Assert.Equal(0, session.ResultCount);
        Assert.False(session.HitTarget);

        var quota = await _dbContext.ProviderQuotaUsages.SingleAsync();
        Assert.Equal("alpha", quota.Provider);
        Assert.Equal(1, quota.Searches);
        Assert.Equal(0, quota.Downloads);
    }

    [Fact]
    public async Task Search_CachesTheOutcomeForTheDownloadEndpoint()
    {
        var provider = new ScriptedProvider(_ => Task.FromResult(new SubtitleSearchResult([Candidate()])))
        { Provider = "alpha" };
        var orchestrator = Create(provider);

        var outcome = await orchestrator.SearchAsync(Request(), MediaType.Movie, 1, null, CancellationToken.None);

        var resolved = orchestrator.ResolveCache(outcome.CacheKey);
        Assert.NotNull(resolved);
        Assert.Same(outcome.Candidates, resolved);
        Assert.NotNull(orchestrator.SessionIdOf(outcome.CacheKey, outcome.Candidates[0]));
        Assert.Null(orchestrator.ResolveCache("no-such-key"));
    }

    [Fact]
    public async Task Search_OpenSubtitlesSearchesCarryTheQuotaWaterline()
    {
        var os = new ScriptedProvider(_ => Task.FromResult(new SubtitleSearchResult([Candidate()])))
        { Provider = "opensubtitles" };
        var orchestrator = Create(os);

        await orchestrator.SearchAsync(Request(), MediaType.Movie, 1, null, CancellationToken.None);

        var quota = await _dbContext.ProviderQuotaUsages.SingleAsync();
        Assert.Equal(40, quota.QuotaLimit); // OpenSubtitlesProvider.DailyDownloadQuota
    }

    [Fact]
    public async Task Search_CallerCancellationPropagates()
    {
        var slow = new ScriptedProvider(async token =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), token);
            return SubtitleSearchResult.Empty;
        })
        { Provider = "slow" };
        var orchestrator = Create(slow);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            orchestrator.SearchAsync(Request(), MediaType.Movie, 1, null, cts.Token));
    }

    [Fact]
    public async Task CooldownStore_ConcurrentFailuresSerialize_BothSourcesPersist()
    {
        // QA1 P2-2: the persisted map is one read-modify-write blob — two
        // sources failing in the same parallel round must not overwrite each
        // other. The artificial read latency widens the unsynchronized window
        // so the bug (had it stayed) would reliably drop one entry.
        var settings = new InMemorySettingService { ReadDelayMilliseconds = 25 };
        var store = new SubtitleSourceCooldownStore(settings, NullLogger<SubtitleSourceCooldownStore>.Instance);

        await Task.WhenAll(
            store.RecordFailureAsync("alpha", SubtitleProviderException.ForHttpStatus(
                "alpha", System.Net.HttpStatusCode.TooManyRequests)),
            store.RecordFailureAsync("beta", SubtitleProviderException.ForHttpStatus(
                "beta", System.Net.HttpStatusCode.BadGateway)));

        var active = await store.GetActiveAsync();
        Assert.True(active.ContainsKey("alpha"), "alpha's cooldown was lost to the concurrent beta write");
        Assert.True(active.ContainsKey("beta"), "beta's cooldown was lost to the concurrent alpha write");
    }

    [Fact]
    public async Task Telemetry_FailedSessionSaveDetaches_DoesNotPoisonTheNextRound()
    {
        // QA2 P2-2: a failed telemetry save used to leave the session in
        // Added state, so every later SaveChanges of the scoped context
        // retried the same broken row. After the detach fix the next
        // telemetry write succeeds on the same context.
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        using var context = new FlakyDbContext(new DbContextOptionsBuilder<LingarrDbContext>()
            .UseSqlite(connection)
            .UseSnakeCaseNamingConvention()
            .Options);
        context.Database.EnsureCreated();
        var telemetry = new SubtitleTelemetryService(context);

        context.ThrowOnNextSave = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            telemetry.RecordSearchSessionAsync(MediaType.Movie, 1, "alpha", "en", 0, hitTarget: false));

        var id = await telemetry.RecordSearchSessionAsync(MediaType.Movie, 1, "alpha", "en", 1, hitTarget: true);
        Assert.True(id > 0);
        // The failed first save left nothing behind (detached, not retried):
        // exactly the second, successful session row persists.
        var survivor = await context.SubtitleSearchSessions.SingleAsync();
        Assert.Equal(id, survivor.Id);
        Assert.True(survivor.HitTarget);
        // No half-added row survives the failed save — the tracked entries
        // after the successful second write are saved rows, never Added.
        Assert.All(context.ChangeTracker.Entries(), entry =>
            Assert.NotEqual(EntityState.Added, entry.State));
    }

    [Fact]
    public async Task SearchOutcomeCache_IsBoundToItsMedia_AndMismatchedLookupsAreRejected()
    {
        // QA3 P3-2: the download endpoint's cache key belongs to exactly the
        // media the search ran for — another mediaId/mediaType must not
        // resolve it.
        var provider = new ScriptedProvider(_ => Task.FromResult(new SubtitleSearchResult([Candidate()])))
        { Provider = "alpha" };
        var orchestrator = Create(provider);

        var outcome = await orchestrator.SearchAsync(Request(), MediaType.Movie, 7, null, CancellationToken.None);

        Assert.Equal(
            SubtitleSearchOrchestrator.SubtitleCacheResolution.Resolved,
            orchestrator.TryResolveCache(outcome.CacheKey, MediaType.Movie, 7, out var candidates));
        Assert.Single(candidates!);

        Assert.Equal(
            SubtitleSearchOrchestrator.SubtitleCacheResolution.MediaMismatch,
            orchestrator.TryResolveCache(outcome.CacheKey, MediaType.Movie, 8, out var otherId));
        Assert.Null(otherId);
        Assert.Equal(
            SubtitleSearchOrchestrator.SubtitleCacheResolution.MediaMismatch,
            orchestrator.TryResolveCache(outcome.CacheKey, MediaType.Episode, 7, out var otherType));
        Assert.Null(otherType);
        Assert.Equal(
            SubtitleSearchOrchestrator.SubtitleCacheResolution.Expired,
            orchestrator.TryResolveCache("no-such-key", MediaType.Movie, 7, out var expired));
        Assert.Null(expired);
    }
}
