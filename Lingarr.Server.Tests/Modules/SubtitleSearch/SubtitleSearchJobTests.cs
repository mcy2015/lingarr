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
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
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
/// </summary>
public class SubtitleSearchJobTests
{
    private const string VideoFileName = "Movie.2019.WEBDL-1080p.mkv";

    /// <summary>Provider double: scripted candidates + a plain SRT payload.</summary>
    private sealed class ScriptedSubtitleSource : ISubtitleProvider
    {
        private readonly Func<SubtitleCandidate[]> _candidates;
        private readonly byte[] _payload;

        public string Provider => "scripted";

        public int SearchCalls { get; private set; }

        public int DownloadCalls { get; private set; }

        public ScriptedSubtitleSource(Func<SubtitleCandidate[]> candidates, byte[] payload)
        {
            _candidates = candidates;
            _payload = payload;
        }

        public Task<SubtitleSearchResult> SearchAsync(
            SubtitleSearchRequest request, CancellationToken cancellationToken)
        {
            SearchCalls++;
            return Task.FromResult(new SubtitleSearchResult(_candidates()));
        }

        public Task<Lingarr.Server.Modules.SubtitleSearch.Abstractions.SubtitleDownload> DownloadAsync(
            SubtitleCandidateRef candidateRef, CancellationToken cancellationToken)
        {
            DownloadCalls++;
            return Task.FromResult(new Lingarr.Server.Modules.SubtitleSearch.Abstractions.SubtitleDownload(
                Provider, "Movie.2019.WEBDL-1080p.srt", "srt", _payload));
        }
    }

    private static SubtitleCandidate Candidate(string language = "zh", bool hashMatch = true) => new(
        "scripted",
        ExternalId: "ext-job-1",
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
            () => [Candidate(language: "zh", hashMatch: true)]);
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
            () => [Candidate(language: "zh", hashMatch: true)]);
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
            () => [Candidate(language: "zh", hashMatch: false)]);
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
            () => [Candidate(language: "zh", hashMatch: true)]);
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

    /// <summary>
    /// Full-round fixture on real SQLite: the subtitle service, the naming
    /// template, the coverage refresher and the orchestrator are the
    /// production implementations; only the subtitle source and the
    /// schedule/translation collaborators are doubles.
    /// </summary>
    private sealed class SubtitleSearchJobFixture : IDisposable
    {
        internal const string SrtText = "1\r\n00:00:01,000 --> 00:00:02,000\r\n你好世界\r\n";

        internal readonly SqliteConnection Connection;
        internal readonly LingarrDbContext DbContext;
        internal readonly InMemorySettingService Settings = new();
        internal readonly MemoryCache Cache = new(new MemoryCacheOptions());
        internal readonly string MediaDirectory;
        internal readonly Mock<IScheduleService> ScheduleService = new();
        internal readonly Mock<ITranslationRequestQueueService> TranslationQueue = new();
        internal readonly ScriptedSubtitleSource Source;
        internal readonly SubtitleSearchJob Job;
        internal readonly SubtitlePinService PinService;

        internal int MovieId { get; }

        internal SubtitleSearchJobFixture(Func<SubtitleCandidate[]> candidates)
        {
            Connection = new SqliteConnection("DataSource=:memory:");
            Connection.Open();
            // Snake-case naming: the queue writes raw SQL against migration
            // column names, so the EnsureCreated schema must match production.
            DbContext = new JobDbContext(new DbContextOptionsBuilder<LingarrDbContext>()
                .UseSqlite(Connection)
                .UseSnakeCaseNamingConvention()
                .Options);
            DbContext.Database.EnsureCreated();

            MediaDirectory = Path.Combine(
                Path.GetTempPath(), "lingarr-subtitle-job-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(MediaDirectory);
            // marker video file — never read (no hashing provider round-trips),
            // but the directory layout mirrors a real library entry
            File.WriteAllBytes(Path.Combine(MediaDirectory, VideoFileName), [1, 0, 1, 0]);

            var movie = new Movie
            {
                RadarrId = 1,
                Title = "Movie",
                Year = 2019,
                Path = MediaDirectory,
                FileName = VideoFileName,
                DateAdded = DateTimeOffset.UtcNow,
                NeedsTranslation = true,
                NoSubtitleNeeded = false
            };
            DbContext.Movies.Add(movie);
            DbContext.SaveChanges();
            MovieId = movie.Id;

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
            var registry = new SubtitleProviderRegistry(
                new ISubtitleProvider[] { Source },
                Array.Empty<ISubtitleSourceManifest>(),
                NullLogger<SubtitleProviderRegistry>.Instance);

            // The REAL subtitle service: naming template ({原名}.{语言}
            // fallback) and filename language parsing run as in production.
            var subtitleService = new SubtitleService(
                NullLogger<SubtitleService>.Instance,
                new LanguageCodeService(),
                Settings);

            var pipeline = new SubtitleDownloadPipeline(
                DbContext,
                registry,
                new SubtitleArchiveExtractor(),
                new PassthroughEncodingService(),
                subtitleService,
                TranslationQueue.Object,
                Settings,
                new SubtitleTelemetryService(DbContext),
                new SubtitleCoverageRefresher(
                    DbContext, subtitleService, Settings, NullLogger<SubtitleCoverageRefresher>.Instance),
                HubContext(),
                NullLogger<SubtitleDownloadPipeline>.Instance);
            var orchestrator = new SubtitleSearchOrchestrator(
                registry,
                new SubtitleMatchScorer(),
                new SubtitleSourceCooldownStore(Settings, NullLogger<SubtitleSourceCooldownStore>.Instance),
                new SubtitleTelemetryService(DbContext),
                Cache,
                NullLogger<SubtitleSearchOrchestrator>.Instance);
            var queueService = new SubtitleSearchQueueService(
                DbContext, NullLogger<SubtitleSearchQueueService>.Instance);
            PinService = new SubtitlePinService(DbContext, NullLogger<SubtitlePinService>.Instance);
            Job = new SubtitleSearchJob(
                DbContext,
                Settings,
                ScheduleService.Object,
                queueService,
                new SubtitleSearchRequestBuilder(DbContext, Settings, new LazyMediaHasher()),
                orchestrator,
                pipeline,
                NullLogger<SubtitleSearchJob>.Instance);
        }

        public void Dispose()
        {
            DbContext.Dispose();
            Connection.Dispose();
            Cache.Dispose();
            try
            {
                Directory.Delete(MediaDirectory, recursive: true);
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
