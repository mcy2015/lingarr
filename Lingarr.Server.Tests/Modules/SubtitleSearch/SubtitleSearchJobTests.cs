using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Lingarr.Core.Configuration;
using Lingarr.Core.Data;
using Lingarr.Core.Entities;
using Lingarr.Core.Enum;
using Lingarr.Server.Hubs;
using Lingarr.Server.Models;
using Lingarr.Server.Modules.Infrastructure;
using Lingarr.Server.Modules.Settings;
using Lingarr.Server.Modules.Subtitle;
using Lingarr.Server.Modules.Subtitle.PostProcessing;
using Lingarr.Server.Modules.SubtitleSearch;
using Lingarr.Server.Modules.SubtitleSearch.Abstractions;
using Lingarr.Server.Modules.SubtitleSearch.Jobs;
using Lingarr.Server.Modules.SubtitleSearch.Orchestration;
using Lingarr.Server.Modules.SubtitleSearch.Pipeline;
using Lingarr.Server.Modules.SubtitleSearch.Queue;
using Lingarr.Server.Modules.SubtitleSearch.Requests;
using Lingarr.Server.Modules.SubtitleSearch.Scoring;
using Lingarr.Server.Modules.SubtitleSearch.Telemetry;
using Lingarr.Server.Modules.Translation;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Lingarr.Server.Tests.Modules.SubtitleSearch;

/// <summary>
/// One-round contracts of the automated subtitle-search batch (tech design
/// ⑤, previously zero coverage): the master-switch short-circuit (zero
/// queue writes, zero provider calls), the full happy loop (gap enqueue →
/// dequeue → search → download → coverage flip → Succeeded with the pin
/// released), the low-confidence path (best candidate below
/// subtitle_search_auto_score → history row + backoff, never a download)
/// and the already-exists skip (target-language file on disk → no
/// overwrite, no provider download).
/// Real SQLite + snake-case naming because every consumer of the round
/// (queue upserts, ExecuteUpdates, telemetry) is relational; the subtitle
/// service is the REAL production one so the naming template, the language
/// tag parsing and the coverage refresh run exactly as in production.
/// <para>
/// R14 (media-level bounded fan-out of 8): since the job resolves the whole
/// per-item graph from a DI scope, the fixture builds a real
/// ServiceProvider behind the production lifetimes (scoped DbContext /
/// orchestrator / pipeline over a singleton settings + provider double) and
/// a temp-FILE SQLite database with the production
/// <see cref="SqlitePragmaConnectionInterceptor"/> (WAL + busy timeout) —
/// per-scope contexts open their own connection, so eight concurrent items
/// hit the database the same way production's eight concurrent scopes do.
/// A shared :memory: connection would serialize/undefined the concurrency
/// the round is now supposed to exhibit.
/// </para>
/// </summary>
public class SubtitleSearchJobTests
{
    private const string VideoFileName = "Movie.2019.WEBDL-1080p.mkv";

    /// <summary>Provider double: scripted candidates + a plain SRT payload.</summary>
    private sealed class ScriptedSubtitleSource : ISubtitleProvider
    {
        private readonly Func<SubtitleSearchRequest, SubtitleCandidate[]> _candidates;
        private readonly byte[] _payload;
        private int _searchCalls;
        private int _downloadCalls;

        public string Provider => "scripted";

        /// <summary>
        /// Optional gate awaited inside SearchAsync (R14 tests): the
        /// concurrency-cap and cancellation tests block in-flight media
        /// items right here, inside the source call, on the orchestrator's
        /// linked budget token.
        /// </summary>
        public Func<CancellationToken, Task>? OnSearching { get; set; }

        /// <summary>
        /// Candidates (by external id — the pipeline forwards the external
        /// id through <see cref="SubtitleCandidateRef.CacheKey"/>) whose
        /// byte download must escape as a plain exception. The pipeline
        /// only catches OCE and SubtitleProviderException around the
        /// provider download, so a plain throw survives it and lands in the
        /// JOB's per-item catch — the crash one broken item must not take
        /// the round down with.
        /// </summary>
        public string? ExplodeDownloadForExternalId { get; set; }

        public int SearchCalls => Volatile.Read(ref _searchCalls);
        public int DownloadCalls => Volatile.Read(ref _downloadCalls);

        public ScriptedSubtitleSource(
            Func<SubtitleSearchRequest, SubtitleCandidate[]> candidates, byte[] payload)
        {
            _candidates = candidates;
            _payload = payload;
        }

        public async Task<SubtitleSearchResult> SearchAsync(
            SubtitleSearchRequest request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _searchCalls);
            if (OnSearching is { } hook)
            {
                await hook(cancellationToken);
            }

            return new SubtitleSearchResult(_candidates(request));
        }

        public Task<Lingarr.Server.Modules.SubtitleSearch.Abstractions.SubtitleDownload> DownloadAsync(
            SubtitleCandidateRef candidateRef, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _downloadCalls);
            if (candidateRef.CacheKey == ExplodeDownloadForExternalId)
            {
                throw new InvalidOperationException(
                    $"scripted source exploded downloading '{candidateRef.CacheKey}'");
            }

            return Task.FromResult(new Lingarr.Server.Modules.SubtitleSearch.Abstractions.SubtitleDownload(
                Provider, "Movie.2019.WEBDL-1080p.srt", "srt", _payload));
        }
    }

    private static SubtitleCandidate Candidate(
        string language = "zh",
        bool hashMatch = true,
        string externalId = "ext-job-1") => new(
        "scripted",
        ExternalId: externalId,
        language,
        MatchScore: 0,
        Forced: false,
        HearingImpaired: false,
        ReleaseInfo: "Movie.2019.WEBDL-1080p",
        Uploader: null,
        Format: "srt",
        Size: 1234,
        hashMatch);

    [Fact]
    public async Task Execute_MasterSwitchOff_ShortCircuitsBeforeAnyQueueOrProviderAction()
    {
        // The seeded default of subtitle_search_enabled is false — the round
        // must end after one log line: no gap enqueue, no dequeue, no search,
        // no history row, nothing on disk.
        using var fixture = new SubtitleSearchJobFixture(
            request => [Candidate(language: "zh", hashMatch: true)]);
        fixture.Settings.SetSetting(SettingKeys.SubtitleSearch.Enabled, "false").GetAwaiter().GetResult();

        await fixture.Job.Execute();

        Assert.Equal(0, fixture.Source.SearchCalls);
        Assert.Equal(0, fixture.Source.DownloadCalls);
        Assert.Empty(await fixture.DbContext.SubtitleSearchStates.AsNoTracking().ToListAsync());
        Assert.Empty(await fixture.DbContext.SubtitleDownloads.AsNoTracking().ToListAsync());
        Assert.Empty(await fixture.DbContext.SubtitleSearchSessions.AsNoTracking().ToListAsync());
        Assert.True((await fixture.DbContext.Movies.AsNoTracking().SingleAsync()).NeedsTranslation);
        Assert.Empty(Directory.GetFiles(fixture.MediaDirectory, "*.srt"));
        // the round itself still reports success (the rollback lever is not an error)
        fixture.ScheduleService.Verify(
            service => service.UpdateJobState(It.IsAny<string>(), "Succeeded"), Times.Once);
    }

    [Fact]
    public async Task Execute_MasterSwitchOn_DownloadsCoversAndReleasesThePin()
    {
        // Full loop: a pinned gap movie is enqueued by the round itself,
        // searched (hash hit = 100), downloaded to the temp directory,
        // coverage flips needs_translation, and the terminal Succeeded
        // releases the pin.
        using var fixture = new SubtitleSearchJobFixture(
            request => [Candidate(language: "zh", hashMatch: true)]);
        await fixture.PinService.SetPinnedAsync(
            MediaType.Movie, fixture.MovieId, pinned: true, CancellationToken.None);

        await fixture.Job.Execute();

        var state = await fixture.DbContext.SubtitleSearchStates.AsNoTracking().SingleAsync();
        Assert.Equal(SubtitleSearchStatus.Succeeded, state.Status);
        Assert.False(state.Pinned); // the pin is released on success
        Assert.Equal(SubtitlePriorityRank.Normal, state.PriorityRank);
        Assert.Equal(0, state.AttemptCount);
        Assert.Null(state.LastError);

        // the subtitle landed next to the video through the real naming
        // template ({原名}.{语言})
        var landed = Path.Combine(fixture.MediaDirectory, "Movie.2019.WEBDL-1080p.zh.srt");
        Assert.True(File.Exists(landed), $"the subtitle must land on disk ({landed})");
        Assert.Contains("你好世界", File.ReadAllText(landed));

        var history = await fixture.DbContext.SubtitleDownloads.AsNoTracking().SingleAsync();
        Assert.Equal(SubtitleDownloadResult.Succeeded, history.Result);
        Assert.Equal(SubtitleDownloadSource.Auto, history.Source);
        Assert.Equal("zh", history.Language);
        Assert.True(history.HashMatch);
        Assert.Equal(100, history.MatchScore);
        Assert.Equal(landed, history.FilePath);

        // history is linked to the deciding search session
        var session = await fixture.DbContext.SubtitleSearchSessions.AsNoTracking().SingleAsync();
        Assert.Equal(session.Id, history.SearchSessionId);

        // the real coverage refresh flipped the need flag
        var movie = await fixture.DbContext.Movies.AsNoTracking().SingleAsync();
        Assert.False(movie.NeedsTranslation);

        Assert.Equal(1, fixture.Source.SearchCalls);
        Assert.Equal(1, fixture.Source.DownloadCalls);
        // zh is a target-language direct hit — terminal, never queued for translation
        fixture.TranslationQueue.Verify(
            queue => queue.CreateImportRequest(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<int?>(), It.IsAny<MediaType>(), It.IsAny<string?>(), It.IsAny<bool>()),
            Times.Never);
    }

    [Fact]
    public async Task Execute_BestCandidateBelowTheLine_RecordsLowConfidenceAndBacksOff()
    {
        // subtitle_search_auto_score injected at 90: the best non-hash
        // candidate scores 78 (title 55 + year 15 + release tokens 8) —
        // below the line means a LowConfidence history row and a 6h backoff,
        // never a download and never Succeeded.
        using var fixture = new SubtitleSearchJobFixture(
            request => [Candidate(language: "zh", hashMatch: false)]);
        fixture.Settings.SetSetting(SettingKeys.SubtitleSearch.AutoScore, "90").GetAwaiter().GetResult();

        await fixture.Job.Execute();

        var history = await fixture.DbContext.SubtitleDownloads.AsNoTracking().SingleAsync();
        Assert.Equal(SubtitleDownloadResult.LowConfidenceSkipped, history.Result);
        Assert.Equal(78, history.MatchScore);
        Assert.Equal(string.Empty, history.FilePath);
        Assert.NotNull(history.Error);
        Assert.Contains("78", history.Error!);
        Assert.Contains("90", history.Error!);

        var state = await fixture.DbContext.SubtitleSearchStates.AsNoTracking().SingleAsync();
        Assert.Equal(SubtitleSearchStatus.Failed, state.Status); // backed off, not terminal
        Assert.Equal(1, state.AttemptCount);
        Assert.NotNull(state.NextAttemptAt);
        Assert.True(state.NextAttemptAt > DateTimeOffset.UtcNow.AddHours(5)); // 6h × 2^0
        Assert.NotNull(state.LastError);
        Assert.Contains("best candidate scored 78", state.LastError!);

        Assert.Equal(1, fixture.Source.SearchCalls);
        Assert.Equal(0, fixture.Source.DownloadCalls); // the candidate was never fetched
        Assert.Empty(Directory.GetFiles(fixture.MediaDirectory, "*.srt"));
        Assert.True((await fixture.DbContext.Movies.AsNoTracking().SingleAsync()).NeedsTranslation);
    }

    [Fact]
    public async Task Execute_TargetLanguageAlreadyOnDisk_SkipsWithoutOverwriting()
    {
        // A same-language subtitle already sits next to the video: the auto
        // round records AlreadyExistsSkipped, never calls the provider's
        // download and never overwrites the existing file.
        using var fixture = new SubtitleSearchJobFixture(
            request => [Candidate(language: "zh", hashMatch: true)]);
        // Named after the video file (the GetSubtitles base-name match):
        // "Movie.2019.WEBDL-1080p.mkv.zh.srt" carries the zh tag.
        var existingPath = Path.Combine(fixture.MediaDirectory, "Movie.2019.WEBDL-1080p.mkv.zh.srt");
        File.WriteAllText(existingPath, "ORIGINAL");

        await fixture.Job.Execute();

        var history = await fixture.DbContext.SubtitleDownloads.AsNoTracking().SingleAsync();
        Assert.Equal(SubtitleDownloadResult.AlreadyExistsSkipped, history.Result);
        Assert.Equal(existingPath, history.FilePath); // the history row points at the existing file
        Assert.Equal("ORIGINAL", File.ReadAllText(existingPath)); // untouched

        Assert.Equal(1, fixture.Source.SearchCalls);
        Assert.Equal(0, fixture.Source.DownloadCalls);

        // The row is terminal Succeeded for this round (the coverage is
        // already there). Because this branch skips the coverage refresh,
        // needs_translation stays true — the next round's revive pass may
        // legitimately re-offer the movie; that is the queue's existing
        // revive semantics, not this test's concern.
        var state = await fixture.DbContext.SubtitleSearchStates.AsNoTracking().SingleAsync();
        Assert.Equal(SubtitleSearchStatus.Succeeded, state.Status);
        Assert.True((await fixture.DbContext.Movies.AsNoTracking().SingleAsync()).NeedsTranslation);
    }

    [Fact]
    public async Task Execute_MoreMediaThanTheCap_AtMostEightItemsAreInFlight()
    {
        // R14 media-level bounded fan-out: nine gap movies against the
        // semaphore of eight. The provider gate blocks every search, so the
        // in-flight high-water mark is observable directly — exactly eight
        // media items may sit inside the pipeline at once, the ninth waits
        // for a slot and still finishes when one frees.
        using var fixture = new SubtitleSearchJobFixture(
            request => [Candidate(language: "zh", hashMatch: true)],
            movieCount: SubtitleSearchJob.MediaParallelism + 1);

        var reachedCap = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var inFlight = 0;
        var peak = 0;

        fixture.Source.OnSearching = async token =>
        {
            var current = Interlocked.Increment(ref inFlight);
            // lock-free running maximum of concurrent in-flight media items
            while (true)
            {
                var observed = Volatile.Read(ref peak);
                if (current <= observed
                    || Interlocked.CompareExchange(ref peak, current, observed) == observed)
                {
                    break;
                }
            }

            if (current == SubtitleSearchJob.MediaParallelism)
            {
                reachedCap.TrySetResult();
            }

            // hold the item inside the source call until the test releases
            await release.Task.WaitAsync(token);
            Interlocked.Decrement(ref inFlight);
        };

        var round = fixture.Job.Execute();
        try
        {
            await reachedCap.Task.WaitAsync(TimeSpan.FromSeconds(30));
        }
        catch (TimeoutException)
        {
            release.TrySetResult();
            await round;
            Assert.Fail(
                $"Only {Volatile.Read(ref peak)} of {SubtitleSearchJob.MediaParallelism} media items became concurrently in flight within 30s.");
        }

        // The cap is fully used AND not exceeded: while all eight slots are
        // held inside the source call, the ninth item cannot have started
        // (its search is only possible after a slot frees, which requires
        // the release above).
        Assert.Equal(SubtitleSearchJob.MediaParallelism, peak);
        Assert.Equal(SubtitleSearchJob.MediaParallelism, fixture.Source.SearchCalls);

        release.TrySetResult();
        await round;

        // the cap held for the whole round, the waiter ran, everyone closed
        Assert.Equal(SubtitleSearchJob.MediaParallelism, peak);
        Assert.Equal(SubtitleSearchJob.MediaParallelism + 1, fixture.Source.SearchCalls);
        Assert.Equal(SubtitleSearchJob.MediaParallelism + 1, fixture.Source.DownloadCalls);
        Assert.Equal(
            SubtitleSearchJob.MediaParallelism + 1,
            await fixture.DbContext.SubtitleSearchStates.AsNoTracking()
                .CountAsync(state => state.Status == SubtitleSearchStatus.Succeeded));
    }

    [Fact]
    public async Task Execute_OneMediaCrashesInThePipeline_OthersFinishAndTheRoundSucceeds()
    {
        // R14 exception isolation: "Movie 2"'s byte download escapes as a
        // plain exception (the pipeline lets those through on purpose) —
        // exactly that item backs off with the crash recorded, the other
        // two close the full loop, and the round itself still ends
        // Succeeded. Serial-loop semantics ("catch, log with the media id,
        // mark failed, continue") under concurrency.
        using var fixture = new SubtitleSearchJobFixture(
            request => request.Title == "Movie 2"
                ? [Candidate(language: "zh", hashMatch: true, externalId: "poison")]
                : [Candidate(language: "zh", hashMatch: true, externalId: "ok")],
            movieCount: 3);
        fixture.Source.ExplodeDownloadForExternalId = "poison";

        await fixture.Job.Execute();

        var states = await fixture.DbContext.SubtitleSearchStates.AsNoTracking().ToListAsync();
        Assert.Equal(3, states.Count);

        var crashed = states.Single(state =>
            state.LastError != null && state.LastError.Contains("scripted source exploded"));
        Assert.Equal(SubtitleSearchStatus.Failed, crashed.Status); // backed off, not a round abort
        Assert.Equal(1, crashed.AttemptCount);

        Assert.Equal(
            2,
            states.Count(state => state.Status == SubtitleSearchStatus.Succeeded));

        // the two healthy items really downloaded (their per-item scopes ran
        // the full pipeline to the terminal mark)
        Assert.Equal(3, fixture.Source.SearchCalls);
        Assert.Equal(3, fixture.Source.DownloadCalls); // the poisoned fetch was attempted, then blew up
        Assert.Equal(
            2,
            Directory.GetFiles(fixture.MediaRoot, "*.zh.srt", SearchOption.AllDirectories).Length);

        fixture.ScheduleService.Verify(
            service => service.UpdateJobState(It.IsAny<string>(), "Succeeded"), Times.Once);
    }

    [Fact]
    public async Task Execute_JobTokenCancelled_InFlightItemsUnwindWithoutFailureMarks()
    {
        // R14 cancellation: the job-level token penetrates to the in-flight
        // items (through the orchestrator's linked per-source budget), the
        // round waits for them to unwind, throws OperationCanceledException
        // — and a cancelled item is NEVER a failure: no backoff mark, no
        // last error, no terminal Succeeded state on the round.
        using var fixture = new SubtitleSearchJobFixture(
            request => [Candidate(language: "zh", hashMatch: true)],
            movieCount: 2);

        using var cts = new CancellationTokenSource();
        var bothInFlight = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var inFlight = 0;

        fixture.Source.OnSearching = async token =>
        {
            if (Interlocked.Increment(ref inFlight) == 2)
            {
                bothInFlight.TrySetResult();
            }

            // cancelled exactly by the job token (linked into this one)
            await Task.Delay(Timeout.Infinite, token);
        };

        var round = fixture.Job.Execute(cts.Token);
        try
        {
            await bothInFlight.Task.WaitAsync(TimeSpan.FromSeconds(30));
        }
        catch (TimeoutException)
        {
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => round);
            Assert.Fail("Two media items never became concurrently in flight within 30s.");
        }

        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => round);

        // both items unwound as cancelled — Pending, no attempt, no error
        var states = await fixture.DbContext.SubtitleSearchStates.AsNoTracking().ToListAsync();
        Assert.Equal(2, states.Count);
        Assert.All(states, state =>
        {
            Assert.Equal(SubtitleSearchStatus.Pending, state.Status);
            Assert.Equal(0, state.AttemptCount);
            Assert.Null(state.LastError);
        });

        Assert.Equal(2, fixture.Source.SearchCalls);
        Assert.Equal(0, fixture.Source.DownloadCalls);

        // the round never reached its completion mark
        fixture.ScheduleService.Verify(
            service => service.UpdateJobState(It.IsAny<string>(), "Succeeded"), Times.Never);
    }

    /// <summary>
    /// Full-round fixture on real SQLite: the subtitle service, the naming
    /// template, the coverage refresher and the orchestrator are the
    /// production implementations; only the subtitle source and the
    /// schedule/translation collaborators are doubles. R14: the job
    /// resolves the per-item pipeline from DI scopes, so the fixture wires
    /// a ServiceProvider with the production lifetimes — every scope gets
    /// its own DbContext on its own connection to the temp-file database
    /// (WAL + busy timeout through the production interceptor), which is
    /// what makes real eight-way media concurrency safe to exercise.
    /// </summary>
    private sealed class SubtitleSearchJobFixture : IDisposable
    {
        internal const string SrtText = "1\r\n00:00:01,000 --> 00:00:02,000\r\n你好世界\r\n";

        internal readonly string MediaRoot;
        internal readonly string MediaDirectory;
        internal readonly string DatabaseDirectory;
        internal readonly string DatabasePath;
        internal readonly LingarrDbContext DbContext;
        internal readonly InMemorySettingService Settings = new();
        internal readonly MemoryCache Cache = new(new MemoryCacheOptions());
        internal readonly Mock<IScheduleService> ScheduleService = new();
        internal readonly Mock<ITranslationRequestQueueService> TranslationQueue = new();
        internal readonly ScriptedSubtitleSource Source;
        internal readonly SubtitleSearchJob Job;
        internal readonly SubtitlePinService PinService;
        internal readonly ServiceProvider RootProvider;

        internal int MovieId { get; }

        internal SubtitleSearchJobFixture(
            Func<SubtitleSearchRequest, SubtitleCandidate[]> candidates,
            int movieCount = 1)
        {
            MediaRoot = Path.Combine(
                Path.GetTempPath(), "lingarr-subtitle-job-" + Guid.NewGuid().ToString("N"));
            MediaDirectory = Path.Combine(MediaRoot, "movie-1");
            Directory.CreateDirectory(MediaDirectory);
            DatabaseDirectory = Path.Combine(
                Path.GetTempPath(), "lingarr-subtitle-job-db-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(DatabaseDirectory);
            DatabasePath = Path.Combine(DatabaseDirectory, "lingarr.db");

            DbContext = CreateContext();
            DbContext.Database.EnsureCreated();

            // one directory per movie: concurrent items must never race on
            // the same output path (the cap test runs nine of them)
            var first = true;
            var firstMovie = default(Movie);
            for (var i = 1; i <= movieCount; i++)
            {
                var directory = Path.Combine(MediaRoot, $"movie-{i}");
                Directory.CreateDirectory(directory);
                // marker video file — never read (no hashing provider round-trips),
                // but the directory layout mirrors a real library entry
                File.WriteAllBytes(Path.Combine(directory, VideoFileName), [1, 0, 1, 0]);
                var movie = new Movie
                {
                    RadarrId = i,
                    Title = i == 1 ? "Movie" : $"Movie {i}",
                    Year = 2019,
                    Path = directory,
                    FileName = VideoFileName,
                    DateAdded = DateTimeOffset.UtcNow,
                    NeedsTranslation = true,
                    NoSubtitleNeeded = false
                };
                DbContext.Movies.Add(movie);
                if (first)
                {
                    firstMovie = movie;
                    first = false;
                }
            }

            DbContext.SaveChanges();
            MovieId = firstMovie!.Id;

            // zh is the deployment target: the direct-hit download path is
            // terminal and the coverage refresh flips needs_translation.
            Settings.SetSetting(
                SettingKeys.Translation.TargetLanguages,
                System.Text.Json.JsonSerializer.Serialize(new List<TargetLanguage>
                {
                    new() { Name = "Chinese", Code = "zh" }
                })).GetAwaiter().GetResult();
            Settings.SetSetting(SettingKeys.SubtitleSearch.Enabled, "true").GetAwaiter().GetResult();

            ScheduleService
                .Setup(service => service.UpdateJobState(It.IsAny<string>(), It.IsAny<string>()))
                .Returns(Task.CompletedTask);
            TranslationQueue
                .Setup(queue => queue.CreateImportRequest(
                    It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                    It.IsAny<int?>(), It.IsAny<MediaType>(), It.IsAny<string?>(), It.IsAny<bool>()))
                .ReturnsAsync(41);

            Source = new ScriptedSubtitleSource(candidates, Encoding.UTF8.GetBytes(SrtText));

            // The REAL subtitle service: naming template ({原名}.{语言}
            // fallback) and filename language parsing run as in production.
            var subtitleService = new SubtitleService(
                NullLogger<SubtitleService>.Instance,
                new LanguageCodeService(),
                Settings);

            // Production lifetimes (SubtitleSearchModule): the per-item
            // graph is scoped over the DbContext, singletons are the
            // stateless scorer/cache/extractor and the test doubles.
            var services = new ServiceCollection();
            services.AddLogging(); // ILogger<T> → null loggers for every collaborator
            services.AddSingleton<ISettingService>(Settings);
            services.AddSingleton<ISubtitleProvider>(Source);
            services.AddSingleton<IMemoryCache>(Cache);
            services.AddSingleton<SubtitleMatchScorer>();
            services.AddSingleton<SubtitleArchiveExtractor>();
            services.AddSingleton<ISubtitleEncodingService>(new PassthroughEncodingService());
            services.AddSingleton<ISubtitleService>(subtitleService);
            services.AddSingleton<ITranslationRequestQueueService>(TranslationQueue.Object);
            services.AddSingleton<IHubContext<JobProgressHub>>(HubContext());
            services.AddSingleton(new LazyMediaHasher());
            services.AddScoped<LingarrDbContext>(_ => CreateContext());
            services.AddScoped<SubtitleProviderRegistry>();
            services.AddScoped<SubtitleSourceCooldownStore>();
            services.AddScoped<SubtitleTelemetryService>();
            services.AddScoped<SubtitleSearchRequestBuilder>();
            services.AddScoped<SubtitleSearchQueueService>();
            services.AddScoped<SubtitleSearchOrchestrator>();
            services.AddScoped<SubtitleCoverageRefresher>();
            // B01: the pipeline's post-processing dependency, with NO stage
            // registered — the empty composite keeps these job tests at the
            // pre-B01 download behaviour.
            services.AddScoped<CompositePostProcessor>();
            services.AddScoped<SubtitleDownloadPipeline>();
            RootProvider = services.BuildServiceProvider();

            // Round-level collaborators the job keeps on its own (Hangfire)
            // scope: gap enqueue + batch dequeue + the round-start cooldown
            // snapshot — all main-thread, so they share the assertion
            // context without concurrency.
            var registry = new SubtitleProviderRegistry(
                new ISubtitleProvider[] { Source },
                Array.Empty<ISubtitleSourceManifest>(),
                NullLogger<SubtitleProviderRegistry>.Instance);
            var queueService = new SubtitleSearchQueueService(
                DbContext, NullLogger<SubtitleSearchQueueService>.Instance);
            var orchestrator = new SubtitleSearchOrchestrator(
                registry,
                new SubtitleMatchScorer(),
                new SubtitleSourceCooldownStore(Settings, NullLogger<SubtitleSourceCooldownStore>.Instance),
                new SubtitleTelemetryService(DbContext),
                Cache,
                NullLogger<SubtitleSearchOrchestrator>.Instance,
                DbContext,
                Settings);
            PinService = new SubtitlePinService(DbContext, NullLogger<SubtitlePinService>.Instance);
            Job = new SubtitleSearchJob(
                Settings,
                ScheduleService.Object,
                queueService,
                orchestrator,
                RootProvider.GetRequiredService<IServiceScopeFactory>(),
                NullLogger<SubtitleSearchJob>.Instance);
        }

        /// <summary>
        /// One context = one connection to the temp-file database, with the
        /// production pragmas (WAL + busy timeout): per-scope contexts must
        /// be independently usable, exactly like the job's eight concurrent
        /// scopes against the production database.
        /// </summary>
        private LingarrDbContext CreateContext()
        {
            return new JobDbContext(new DbContextOptionsBuilder<LingarrDbContext>()
                .UseSqlite(
                    $"Data Source={DatabasePath};Foreign Keys=True",
                    sqlite => sqlite.UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery))
                .AddInterceptors(new SqlitePragmaConnectionInterceptor())
                .UseSnakeCaseNamingConvention()
                .Options);
        }

        public void Dispose()
        {
            RootProvider.Dispose();
            DbContext.Dispose();
            Cache.Dispose();
            try
            {
                Directory.Delete(MediaRoot, recursive: true);
                Directory.Delete(DatabaseDirectory, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // temp cleanup is best-effort
            }

            GC.SuppressFinalize(this);
        }

        private static IHubContext<JobProgressHub> HubContext()
        {
            var proxy = new Mock<IClientProxy>();
            proxy
                .Setup(client => client.SendCoreAsync(
                    It.IsAny<string>(), It.IsAny<object?[]>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);
            var clients = new Mock<IHubClients>();
            clients.Setup(hubClients => hubClients.Group(It.IsAny<string>())).Returns(proxy.Object);
            var context = new Mock<IHubContext<JobProgressHub>>();
            context.Setup(hubContext => hubContext.Clients).Returns(clients.Object);
            return context.Object;
        }

        private sealed class JobDbContext(DbContextOptions options) : LingarrDbContext(options);

        /// <summary>UTF-8 in, UTF-8 out — the encoding step is not under test here.</summary>
        private sealed class PassthroughEncodingService : ISubtitleEncodingService
        {
            public Encoding Detect(byte[] content) => Encoding.UTF8;

            public byte[] ConvertToUtf8(byte[] content) => content;
        }
    }
}
