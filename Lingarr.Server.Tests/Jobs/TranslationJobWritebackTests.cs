using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Lingarr.Contracts.Translation;
using Lingarr.Core.Configuration;
using Lingarr.Core.Data;
using Lingarr.Core.Entities;
using Lingarr.Core.Enum;
using Lingarr.Core.Interfaces;
using Lingarr.Server.Controllers;
using Lingarr.Server.Hubs;
using Lingarr.Server.Modules.Infrastructure;
using Lingarr.Server.Modules.Media;
using Lingarr.Server.Modules.Settings;
using Lingarr.Server.Modules.Statistics;
using Lingarr.Server.Modules.Subtitle;
using Lingarr.Server.Modules.SubtitleSearch.Pipeline;
using Lingarr.Server.Modules.Translation;
using Lingarr.Server.Models;
using Lingarr.Server.Models.Api;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Lingarr.Server.Tests.Jobs;

/// <summary>
/// Walkthrough fix B1, end to end through the real TranslationJob writeback:
/// once the translated file lands, the media's subtitle coverage snapshot
/// and need flags update immediately (movies directly, episodes through the
/// owning show's rebuilt aggregate), the media detail page's cached
/// directory scan is dropped so the Files card sees the new file within the
/// same TTL window, and the download pipeline's SubtitleDownloaded hub
/// event is broadcast. Real SQLite because the coverage refresh writes
/// through ExecuteUpdate; the subtitle service is the production one.
/// </summary>
public class TranslationJobWritebackTests : IDisposable
{
    private const string ServiceType = "stub";
    private const string SourceSrt = "1\r\n00:00:01,000 --> 00:00:02,000\r\nHello world\r\n";

    private readonly SqliteConnection _connection;
    private readonly WritebackDbContext _dbContext;
    private readonly RecordingSettingService _settings = new();
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "lingarr-writeback-" + Guid.NewGuid().ToString("N"));

    private readonly Mock<IProgressService> _progressService = new();
    private readonly Mock<IStatisticsService> _statisticsService = new();
    private readonly Mock<ITranslationRequestEventService> _eventService = new();
    private readonly Mock<ITranslationRequestService> _translationRequestService = new();
    private readonly FakeTranslationServiceFactory _factory = new();
    private readonly StubTranslationService _provider = new();
    private readonly CapturingHubContext _hub = new();

    private readonly SubtitleService _subtitleService;
    private readonly TranslationJob _job;

    public TranslationJobWritebackTests()
    {
        Directory.CreateDirectory(_root);
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _dbContext = new WritebackDbContext(new DbContextOptionsBuilder<LingarrDbContext>()
            .UseSqlite(_connection)
            .UseSnakeCaseNamingConvention()
            .Options);
        _dbContext.Database.EnsureCreated();

        // zh target; the translation stub chain answers every line.
        _settings.Values[SettingKeys.Translation.TargetLanguages] = System.Text.Json.JsonSerializer.Serialize(
            new List<TargetLanguage> { new() { Name = "Chinese", Code = "zh" } });
        _settings.Values[SettingKeys.Translation.ServiceType] = ServiceType;
        // keep the output name language-tagged ({原名}.{语言}) and skip the
        // source validation — this fixture pins the writeback side effects
        _settings.Values[SettingKeys.SubtitleValidation.ValidateSubtitles] = "false";
        _settings.Values[SettingKeys.Translation.RemoveLanguageTag] = "false";

        _translationRequestService
            .Setup(s => s.UpdateTranslationRequest(
                It.IsAny<TranslationRequest>(), It.IsAny<TranslationStatus>(), It.IsAny<string?>()))
            .Callback((TranslationRequest request, TranslationStatus status, string? _) => request.Status = status)
            .Returns((TranslationRequest request, TranslationStatus _, string? _) => Task.FromResult(request));

        _factory.Entries.Add(new TranslationServiceEntry(ServiceType, _provider, _provider));

        _subtitleService = new SubtitleService(
            NullLogger<SubtitleService>.Instance,
            new LanguageCodeService(),
            _settings);

        _job = new TranslationJob(
            NullLogger<TranslationJob>.Instance,
            _settings,
            _dbContext,
            _progressService.Object,
            _subtitleService,
            new NoOpScheduleService(),
            _statisticsService.Object,
            new TranslationPipeline(
                _settings,
                _factory,
                _progressService.Object,
                new Mock<IAsyncTranslationRegistry>().Object,
                _eventService.Object,
                NullLogger<TranslationPipeline>.Instance),
            _translationRequestService.Object,
            _eventService.Object,
            new Mock<IGlossaryService>().Object,
            new SubtitleCoverageRefresher(
                _dbContext, _subtitleService, _settings, NullLogger<SubtitleCoverageRefresher>.Instance),
            _hub.Object);
    }

    public void Dispose()
    {
        MediaController.InvalidateSubtitleFileCache();
        _dbContext.Dispose();
        _connection.Dispose();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // temp cleanup is best-effort
        }

        GC.SuppressFinalize(this);
    }

    private string MediaDirectory(string name)
    {
        var directory = Path.Combine(_root, name);
        Directory.CreateDirectory(directory);
        return directory;
    }

    private void WriteSourceSubtitle(string directory, string fileName) =>
        File.WriteAllText(Path.Combine(directory, fileName), SourceSrt);

    private MediaController CreateMediaController() => new(
        new Mock<IMediaService>().Object,
        new Mock<IMediaLibraryService>().Object,
        _subtitleService,
        new Mock<ISettingService>().Object,
        new AudioLanguageService(
            _dbContext,
            new Mock<ISettingService>().Object,
            NullHttpClientFactory.Instance,
            NullLogger<AudioLanguageService>.Instance),
        _dbContext);

    private static MediaSubtitlesResponse FilesOf(ActionResult<MediaSubtitlesResponse> action) =>
        Assert.IsType<MediaSubtitlesResponse>(Assert.IsType<OkObjectResult>(action.Result).Value!);

    // ── movie writeback ────────────────────────────────────────────────

    [Fact]
    public async Task Writeback_Movie_RefreshesCoverageFlagsCacheAndBroadcasts()
    {
        // A TTL far beyond the test's lifetime (the static is process-wide,
        // so the manipulation is scoped to this test with try/finally like
        // MediaControllerTests): only the writeback's cache invalidation can
        // surface the new file on the second read.
        var originalTtl = MediaController.SubtitleFileCacheTtl;
        MediaController.SubtitleFileCacheTtl = TimeSpan.FromMinutes(10);
        try
        {
            var directory = MediaDirectory("movie");
            const string videoName = "Movie.2019.WEBDL-1080p";
            WriteSourceSubtitle(directory, videoName + ".en.srt");
            var movie = new Movie
            {
                RadarrId = 1,
                Title = "Movie",
                Year = 2019,
                Path = directory,
                FileName = videoName + ".mkv",
                DateAdded = DateTimeOffset.UtcNow,
                AudioLanguages = ["en"],
                NeedsTranslation = true // the last daily scan left the gap
            };
            _dbContext.Movies.Add(movie);
            await _dbContext.SaveChangesAsync();
            var request = new TranslationRequest
            {
                Title = "Movie",
                SubtitleToTranslate = Path.Combine(directory, videoName + ".en.srt"),
                SourceLanguage = "en",
                TargetLanguage = "zh",
                MediaType = MediaType.Movie,
                MediaId = movie.Id,
                Status = TranslationStatus.Pending
            };
            _dbContext.TranslationRequests.Add(request);
            await _dbContext.SaveChangesAsync();

            // Prime the detail page's directory-scan cache: only the en file
            // exists, and the TTL will not expire during this test.
            var controller = CreateMediaController();
            var before = FilesOf(await controller.GetMediaSubtitles(MediaType.Movie, movie.Id));
            Assert.Single(before.Files);
            Assert.All(before.Files, file => Assert.Contains("en", file.Languages));

            await _job.Execute(request, CancellationToken.None);

            // the request completed and the file landed next to the source
            Assert.Equal(TranslationStatus.Completed, request.Status);
            Assert.NotNull(request.TranslatedSubtitle);
            Assert.True(File.Exists(request.TranslatedSubtitle!));
            Assert.Contains(".zh.", Path.GetFileName(request.TranslatedSubtitle!));

            // coverage + need flag updated without waiting for the daily scan
            var stored = await _dbContext.Movies.AsNoTracking().SingleAsync(m => m.Id == movie.Id);
            Assert.NotNull(stored.SubtitleCoverage);
            Assert.Equal(1, stored.SubtitleCoverage!["zh"].Count);
            Assert.True(stored.SubtitleCoverage!["zh"].Bytes > 0);
            Assert.False(stored.NeedsTranslation);

            // the cached Files card refreshed within the same TTL window —
            // proof the writeback dropped the directory's cached scan
            var after = FilesOf(await controller.GetMediaSubtitles(MediaType.Movie, movie.Id));
            Assert.Equal(2, after.Files.Count);
            Assert.Contains(after.Files, file => file.Languages.Contains("zh"));

            // the download pipeline's "a subtitle landed" broadcast went out
            var broadcast = _hub.Sends.Single(s => s.Event == "SubtitleDownloaded");
            Assert.Equal("Movie", broadcast.PayloadProperty("mediaType"));
            Assert.Equal(movie.Id, broadcast.PayloadProperty<int>("mediaId"));
            Assert.Equal("zh", broadcast.PayloadProperty("language"));
        }
        finally
        {
            MediaController.SubtitleFileCacheTtl = originalTtl;
        }
    }

    // ── episode writeback ──────────────────────────────────────────────

    [Fact]
    public async Task Writeback_Episode_RefreshesShowAggregateAndEpisodeFlags()
    {
        var directory = MediaDirectory("show");
        WriteSourceSubtitle(directory, "Show.S01E01.en.srt");
        var show = new Show
        {
            SonarrId = 1,
            Title = "Show",
            Path = Path.Combine(_root, "show-root"),
            DateAdded = DateTimeOffset.UtcNow
        };
        var season = new Season
        {
            SeasonNumber = 1,
            Path = directory,
            Show = show
        };
        var episode = new Episode
        {
            SonarrId = 1,
            EpisodeNumber = 1,
            Title = "S01E01",
            Path = directory,
            FileName = "Show.S01E01.mkv",
            Season = season,
            NeedsTranslation = true
        };
        season.Episodes.Add(episode);
        show.Seasons.Add(season);
        _dbContext.Shows.Add(show);
        await _dbContext.SaveChangesAsync();
        var request = new TranslationRequest
        {
            Title = "Show S01E01",
            SubtitleToTranslate = Path.Combine(directory, "Show.S01E01.en.srt"),
            SourceLanguage = "en",
            TargetLanguage = "zh",
            MediaType = MediaType.Episode,
            MediaId = episode.Id,
            Status = TranslationStatus.Pending
        };
        _dbContext.TranslationRequests.Add(request);
        await _dbContext.SaveChangesAsync();

        await _job.Execute(request, CancellationToken.None);

        Assert.Equal(TranslationStatus.Completed, request.Status);

        // the owning show's aggregated snapshot rebuilt from the season scan
        var storedShow = await _dbContext.Shows.AsNoTracking()
            .SingleAsync(s => s.Id == show.Id);
        Assert.NotNull(storedShow.SubtitleCoverage);
        // en (the source) and zh (the writeback) both attribute to episode 1
        Assert.Equal(1, storedShow.SubtitleCoverage!["zh"].Count);
        Assert.Equal(1, storedShow.SubtitleCoverage!["en"].Count);

        var storedEpisode = await _dbContext.Episodes.AsNoTracking()
            .SingleAsync(e => e.Id == episode.Id);
        Assert.False(storedEpisode.NeedsTranslation);

        // the show directory's cached scan dropped too, and the broadcast
        // addresses the episode the way the download pipeline does
        var files = FilesOf(await CreateMediaController().GetMediaSubtitles(MediaType.Show, show.Id));
        Assert.Equal(2, files.Files.Count);
        var broadcast = _hub.Sends.Single(s => s.Event == "SubtitleDownloaded");
        Assert.Equal("Episode", broadcast.PayloadProperty("mediaType"));
        Assert.Equal(episode.Id, broadcast.PayloadProperty<int>("mediaId"));
    }

    // ── no media link: nothing to refresh, no broadcast ────────────────

    [Fact]
    public async Task Writeback_RequestWithoutMediaId_SkipsTheRefreshEntirely()
    {
        var directory = MediaDirectory("orphan");
        WriteSourceSubtitle(directory, "Orphan.en.srt");
        var request = new TranslationRequest
        {
            Title = "Orphan",
            SubtitleToTranslate = Path.Combine(directory, "Orphan.en.srt"),
            SourceLanguage = "en",
            TargetLanguage = "zh",
            MediaType = MediaType.Movie,
            MediaId = null,
            Status = TranslationStatus.Pending
        };
        _dbContext.TranslationRequests.Add(request);
        await _dbContext.SaveChangesAsync();

        await _job.Execute(request, CancellationToken.None);

        Assert.Equal(TranslationStatus.Completed, request.Status);
        Assert.DoesNotContain(_hub.Sends, s => s.Event == "SubtitleDownloaded");
    }

    // ── doubles ────────────────────────────────────────────────────────

    /// <summary>
    /// Setting service with plain value storage plus JSON storage for the
    /// target languages — GetSettings answers every requested key (the job
    /// indexes the result directly), missing keys defaulting to "".
    /// </summary>
    private sealed class RecordingSettingService : ISettingService
    {
        public Dictionary<string, string> Values { get; } = new();

        public event SettingChangedHandler? SettingChanged
        {
            add { }
            remove { }
        }

        public Task<string?> GetSetting(string key) =>
            Task.FromResult<string?>(Values.GetValueOrDefault(key));

        public Task<Dictionary<string, string>> GetSettings(IEnumerable<string> keys) =>
            Task.FromResult(keys.ToDictionary(key => key, key => Values.GetValueOrDefault(key, string.Empty)));

        public Task<bool> SetSetting(string key, string value) => Task.FromResult(true);

        public Task<bool> SetSettings(Dictionary<string, string> settings) => Task.FromResult(true);

        public Task<List<T>> GetSettingAsJson<T>(string key) where T : class =>
            Task.FromResult(Values.TryGetValue(key, out var json)
                ? System.Text.Json.JsonSerializer.Deserialize<List<T>>(json) ?? []
                : []);

        public Task<bool> SetEncryptedSetting(string key, string value) => Task.FromResult(true);

        public Task<string?> GetEncryptedSetting(string key) => Task.FromResult<string?>(null);

        public Task<Dictionary<string, bool>> GetEncryptedSettingStates(IEnumerable<string> keys) =>
            Task.FromResult(new Dictionary<string, bool>());
    }

    private sealed class FakeTranslationServiceFactory : ITranslationServiceFactory
    {
        public List<TranslationServiceEntry> Entries { get; } = [];

        public IReadOnlyList<TranslationServiceEntry> CreateTranslationServices(IReadOnlyList<string> serviceTypes) =>
            Entries;

        public Contracts.Translation.ITranslationService? CreateTranslationService(string serviceType) =>
            Entries.FirstOrDefault(e => e.Name == serviceType).Service;
    }

    private sealed class StubTranslationService : ITranslationService, IBatchTranslationService
    {
        public string? ModelName => "stub-model";

        public Task<string> TranslateAsync(
            string text, string sourceLanguage, string targetLanguage,
            List<string>? contextLinesBefore, List<string>? contextLinesAfter,
            CancellationToken cancellationToken) =>
            Task.FromResult($"translated:{text}");

        public Task<List<Contracts.Models.SourceLanguage>> GetLanguages() =>
            Task.FromResult(new List<Contracts.Models.SourceLanguage>());

        public Task<Contracts.Models.ModelsResponse> GetModels() =>
            Task.FromResult(new Contracts.Models.ModelsResponse());

        public Task<Contracts.Models.LanguagePair?> GetLanguagePair(
            string requestedSource, string requestedTarget, CancellationToken cancellationToken) =>
            Task.FromResult<Contracts.Models.LanguagePair?>(new Contracts.Models.LanguagePair
            {
                Source = requestedSource,
                Target = requestedTarget,
                Tier = Contracts.Models.MatchTier.Exact
            });

        public Task<Dictionary<int, string>> TranslateBatchAsync(
            List<Contracts.Models.Batch.BatchSubtitleItem> subtitleBatch,
            string sourceLanguage, string targetLanguage, CancellationToken cancellationToken)
        {
            var result = subtitleBatch.ToDictionary(item => item.Position, item => $"translated:{item.Line}");
            return Task.FromResult(result);
        }
    }

    private sealed class NoOpScheduleService : IScheduleService
    {
        public Task Initialize() => Task.CompletedTask;
        public Task<List<RecurringJobStatus>> GetRecurringJobs() =>
            Task.FromResult<List<RecurringJobStatus>>([]);
        public List<QueueDepthStatus> GetQueueDepths() => [];
        public Task<bool> SetJobEnabled(string jobId, bool enabled) => Task.FromResult(false);
        public Task<UpdateCronResult> UpdateJobCron(string jobId, string cron) =>
            Task.FromResult(UpdateCronResult.NotFound);
        public string GetJobState(string jobId) => string.Empty;
        public Task UpdateJobState(string jobId, string state) => Task.CompletedTask;
    }

    /// <summary>
    /// Hub double that records every group broadcast — the writeback's
    /// SubtitleDownloaded payload is an anonymous object, so the assertions
    /// read it through reflection.
    /// </summary>
    private sealed class CapturingHubContext
    {
        private readonly Mock<IHubContext<JobProgressHub>> _context = new();
        private readonly Mock<IHubClients> _clients = new();
        private readonly Mock<IClientProxy> _proxy = new();
        private readonly List<string> _groups = [];

        internal sealed record HubSend(string Group, string Event, object? Payload)
        {
            public object? PayloadProperty(string name) =>
                Payload?.GetType().GetProperty(name)?.GetValue(Payload);

            public T PayloadProperty<T>(string name) =>
                (T)(Payload?.GetType().GetProperty(name)?.GetValue(Payload)
                    ?? throw new InvalidOperationException($"payload has no {name}"))!;
        }

        internal List<HubSend> Sends { get; } = [];

        internal IHubContext<JobProgressHub> Object
        {
            get
            {
                _proxy
                    .Setup(client => client.SendCoreAsync(
                        It.IsAny<string>(), It.IsAny<object?[]>(), It.IsAny<CancellationToken>()))
                    .Callback((string @event, object?[] args, CancellationToken _) =>
                    {
                        var group = _groups.LastOrDefault();
                        Sends.Add(new HubSend(group ?? string.Empty, @event, args.FirstOrDefault()));
                    })
                    .Returns(Task.CompletedTask);
                _clients.Setup(hubClients => hubClients.Group(It.IsAny<string>()))
                    .Returns((string group) =>
                    {
                        _groups.Add(group);
                        return _proxy.Object;
                    });
                _context.Setup(hubContext => hubContext.Clients).Returns(_clients.Object);
                return _context.Object;
            }
        }
    }

    private sealed class WritebackDbContext(DbContextOptions options) : LingarrDbContext(options);
}
