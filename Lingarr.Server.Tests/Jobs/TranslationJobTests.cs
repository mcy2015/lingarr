using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Hangfire;
using Lingarr.Contracts.Translation;
using Lingarr.Core.Configuration;
using Lingarr.Core.Data;
using Lingarr.Core.Entities;
using Lingarr.Core.Enum;
using Lingarr.Core.Interfaces;
using Lingarr.Server.Hubs;
using Lingarr.Server.Modules.Translation;
using Lingarr.Server.Models;
using Lingarr.Server.Models.FileSystem;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;
using Lingarr.Server.Modules.Infrastructure;
using Lingarr.Server.Modules.Settings;
using Lingarr.Server.Modules.Statistics;
using Lingarr.Server.Modules.Subtitle;
using Lingarr.Server.Modules.SubtitleSearch.Pipeline;

namespace Lingarr.Server.Tests.Jobs;

/// <summary>
/// Behaviour tests for the primary translation pipeline: status gating,
/// resume-from-persisted-lines, batch vs individual dispatch, and the
/// failure/cancellation handlers. Providers and IO are stubbed; the
/// request/line state uses EF InMemory.
/// </summary>
public class TranslationJobTests
{
    private const string ServiceType = "openai";

    private readonly Mock<ISubtitleService> _subtitleService = new();
    private readonly Mock<IProgressService> _progressService = new();
    private readonly Mock<IStatisticsService> _statisticsService = new();
    private readonly Mock<ITranslationRequestEventService> _eventService = new();
    private readonly Mock<ITranslationRequestService> _translationRequestService = new();
    private readonly FakeTranslationServiceFactory _factory = new();
    private readonly FakeSettingService _settings = new();

    private readonly StubTranslationService _provider = new();

    private LingarrDbContext _context = default!;
    private TranslationJob _job = default!;

    private static LingarrDbContext BuildContext()
    {
        var options = new DbContextOptionsBuilder<LingarrDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new LingarrDbContext(options);
    }

    private async Task<TranslationRequest> SeedRequestAsync(TranslationStatus status = TranslationStatus.Pending)
    {
        var request = new TranslationRequest
        {
            Title = "Test Movie",
            SubtitleToTranslate = "/media/test.srt",
            SourceLanguage = "en",
            TargetLanguage = "es",
            MediaType = MediaType.Movie,
            Status = status
        };
        _context.TranslationRequests.Add(request);
        await _context.SaveChangesAsync();
        return request;
    }

    private TranslationJob CreateJob()
    {
        _context = BuildContext();
        _settings.Values[SettingKeys.Translation.ServiceType] = ServiceType;

        _translationRequestService
            .Setup(s => s.UpdateTranslationRequest(
                It.IsAny<TranslationRequest>(), It.IsAny<TranslationStatus>(), It.IsAny<string?>()))
            .Callback((TranslationRequest request, TranslationStatus status, string? _) => request.Status = status)
            .Returns((TranslationRequest request, TranslationStatus _, string? _) => Task.FromResult(request));

        _subtitleService
            .Setup(s => s.ReadSubtitles(It.IsAny<string>()))
            .ReturnsAsync(() => new List<SubtitleItem>(_provider.SourceItems));

        _subtitleService
            .Setup(s => s.CreateFilePath(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync((string path, string _, string _) => path.Replace(".srt", ".es.srt"));

        _subtitleService
            .Setup(s => s.ValidateSubtitle(It.IsAny<string>(), It.IsAny<Lingarr.Server.Models.SubtitleValidationOptions>()))
            .Returns(true);

        _factory.Entries.Add(new TranslationServiceEntry(ServiceType, _provider, _provider));

        return new TranslationJob(
            NullLogger<TranslationJob>.Instance,
            _settings,
            _context,
            _progressService.Object,
            _subtitleService.Object,
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
            // Real refresher over the InMemory context: the requests seeded
            // below carry no MediaId, so the post-writeback refresh is a
            // no-op and never reaches the relational ExecuteUpdate path.
            new SubtitleCoverageRefresher(
                _context, _subtitleService.Object, _settings, NullLogger<SubtitleCoverageRefresher>.Instance),
            HubContext(),
            new Mock<IBackgroundJobClient>().Object);
    }

    /// <summary>No-op hub double — the writeback broadcast never reaches a client here.</summary>
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

    // ===== Status gating =====

    [Fact]
    public async Task Execute_SkipsWhenRequestAlreadyCompleted()
    {
        _job = CreateJob();
        var request = await SeedRequestAsync(TranslationStatus.Completed);
        _provider.SourceItems = [Item(1, "hello")];

        await _job.Execute(request, CancellationToken.None);

        Assert.Empty(_provider.TranslateCalls);
        _translationRequestService.Verify(
            s => s.UpdateTranslationRequest(
                It.IsAny<TranslationRequest>(), It.IsAny<TranslationStatus>(), It.IsAny<string?>()),
            Times.Never,
            "a completed request must not be re-run");
    }

    [Fact]
    public async Task Execute_SkipsWhenRequestMissingFromDatabase()
    {
        _job = CreateJob();
        var ghost = new TranslationRequest
        {
            Id = 4242,
            Title = "Gone",
            SubtitleToTranslate = "/gone.srt",
            SourceLanguage = "en",
            TargetLanguage = "es",
            MediaType = MediaType.Movie,
            Status = TranslationStatus.Pending
        };

        await _job.Execute(ghost, CancellationToken.None);

        Assert.Empty(_provider.TranslateCalls);
        _translationRequestService.Verify(
            s => s.UpdateTranslationRequest(
                It.IsAny<TranslationRequest>(), It.IsAny<TranslationStatus>(), It.IsAny<string?>()),
            Times.Never);
    }

    // ===== Happy path =====

    [Fact]
    public async Task Execute_CompletesRequestAndCountsAllLinesAsNew()
    {
        _job = CreateJob();
        var request = await SeedRequestAsync();
        _provider.SourceItems = [Item(1, "hello"), Item(2, "world")];

        await _job.Execute(request, CancellationToken.None);

        Assert.Equal(2, _provider.TranslateCalls.Count);
        Assert.Equal(TranslationStatus.Completed, request.Status);
        Assert.NotNull(request.TranslatedSubtitle);

        _subtitleService.Verify(
            s => s.WriteSubtitles(
                It.IsAny<string>(), It.IsAny<List<SubtitleItem>>(), It.IsAny<bool>(), It.IsAny<bool>()),
            Times.Once);

        _statisticsService.Verify(
            s => s.UpdateTranslationStatisticsFromSubtitles(
                It.IsAny<TranslationRequest>(), ServiceType, It.IsAny<string?>(),
                It.Is<List<SubtitleItem>>(items => items.Count == 2)),
            Times.Once,
            "without persisted lines every translated subtitle counts as new");

        _progressService.Verify(p => p.Emit(request, 100), Times.AtLeastOnce);
    }

    // ===== Resume =====

    [Fact]
    public async Task Execute_ResumesAndOnlyTranslatesMissingLines()
    {
        _job = CreateJob();
        var request = await SeedRequestAsync();
        _provider.SourceItems = [Item(1, "hello"), Item(2, "world"), Item(3, "again")];

        _context.TranslationRequestLines.AddRange(
            new TranslationRequestLine { TranslationRequestId = request.Id, Position = 1, Source = "hello", Target = "hola" },
            new TranslationRequestLine { TranslationRequestId = request.Id, Position = 2, Source = "world", Target = "mundo" });
        await _context.SaveChangesAsync();

        await _job.Execute(request, CancellationToken.None);

        Assert.Single(_provider.TranslateCalls);
        Assert.Equal("again", _provider.TranslateCalls[0]);

        _statisticsService.Verify(
            s => s.UpdateTranslationStatisticsFromSubtitles(
                It.IsAny<TranslationRequest>(), It.IsAny<string>(), It.IsAny<string?>(),
                It.Is<List<SubtitleItem>>(items => items.Count == 1)),
            Times.Once,
            "statistics must only count the newly translated line");
    }

    // ===== Batch dispatch =====

    [Fact]
    public async Task Execute_UsesBatchTranslationWhenEnabled()
    {
        _job = CreateJob();
        _settings.Values[SettingKeys.Translation.UseBatchTranslation] = "true";
        var request = await SeedRequestAsync();
        _provider.SourceItems = [Item(1, "hello"), Item(2, "world")];

        await _job.Execute(request, CancellationToken.None);

        Assert.Single(_provider.BatchCalls);
        Assert.Equal(2, _provider.BatchCalls[0].Count);
        Assert.Empty(_provider.TranslateCalls);
        Assert.Equal(TranslationStatus.Completed, request.Status);
    }

    // ===== Degraded completion (P1 fix: producing-service attribution) =====

    [Fact]
    public async Task Execute_DegradedCompletion_PersistsTakeoverServiceOnRequest()
    {
        // Arrange - chain gemini → openai where the primary fails on every
        // line and the fallback completes the run
        _job = CreateJob();
        var request = await SeedRequestAsync();
        _provider.SourceItems = [Item(1, "hello"), Item(2, "world")];
        var failing = new StubTranslationService
        {
            ThrowOnTranslate = new Contracts.Exceptions.TranslationException("gemini exploded")
        };
        _settings.Values[SettingKeys.Translation.ServiceType] = "[\"gemini\",\"openai\"]";
        _factory.Entries.Insert(0, new TranslationServiceEntry("gemini", failing, failing));

        // Act
        await _job.Execute(request, CancellationToken.None);

        // Assert - the request records the provider that actually produced the
        // output, not the configured primary (which drives the queue badge and
        // the per-provider cost rollups)
        var stored = await _context.TranslationRequests.AsNoTracking().SingleAsync();
        Assert.Equal(TranslationStatus.Completed, stored.Status);
        Assert.Equal("openai", stored.ServiceType);

        // statistics and cost attribution follow the takeover service
        _statisticsService.Verify(
            s => s.UpdateTranslationStatisticsFromSubtitles(
                It.IsAny<TranslationRequest>(), "openai", It.IsAny<string?>(),
                It.IsAny<List<SubtitleItem>>()),
            Times.Once);

        // the failure is visible on the timeline for the execution chain
        _eventService.Verify(
            e => e.LogTimelineEvent(
                request.Id, TranslationEventType.ProviderFailed, TranslationStatus.InProgress,
                "gemini", It.IsAny<string?>()),
            Times.Once);
        _eventService.Verify(
            e => e.LogTimelineEvent(
                request.Id, TranslationEventType.Degraded, TranslationStatus.InProgress,
                "openai", "gemini -> openai"),
            Times.Once);
    }

    [Fact]
    public async Task Execute_PrimaryCompletion_PersistsPrimaryService()
    {
        // Arrange - single-service chain: the producing service is the primary
        _job = CreateJob();
        var request = await SeedRequestAsync();
        _provider.SourceItems = [Item(1, "hello")];

        // Act
        await _job.Execute(request, CancellationToken.None);

        // Assert
        var stored = await _context.TranslationRequests.AsNoTracking().SingleAsync();
        Assert.Equal(TranslationStatus.Completed, stored.Status);
        Assert.Equal(ServiceType, stored.ServiceType);
    }

    // ===== Failure handling =====

    [Fact]
    public async Task Execute_MarksRequestFailedAndRethrowsWhenProviderFails()
    {
        _job = CreateJob();
        var request = await SeedRequestAsync();
        _provider.SourceItems = [Item(1, "hello")];
        _provider.ThrowOnTranslate = new Contracts.Exceptions.TranslationException("provider exploded");

        await Assert.ThrowsAsync<Contracts.Exceptions.TranslationException>(
            () => _job.Execute(request, CancellationToken.None));

        var stored = await _context.TranslationRequests.AsNoTracking().SingleAsync();
        Assert.Equal(TranslationStatus.Failed, stored.Status);
        Assert.NotNull(stored.ErrorMessage);
        Assert.NotNull(stored.StackTrace);

        _translationRequestService.Verify(s => s.ClearMediaHash(request), Times.Once);
    }

    // ===== Cancellation via validation failure =====

    [Fact]
    public async Task Execute_MarksRequestCancelledWhenSubtitleValidationFails()
    {
        _job = CreateJob();
        var request = await SeedRequestAsync();
        _provider.SourceItems = [Item(1, "hello")];
        _subtitleService
            .Setup(s => s.ValidateSubtitle(It.IsAny<string>(), It.IsAny<Lingarr.Server.Models.SubtitleValidationOptions>()))
            .Returns(false);

        await _job.Execute(request, CancellationToken.None);

        var stored = await _context.TranslationRequests.AsNoTracking().SingleAsync();
        Assert.Equal(TranslationStatus.Cancelled, stored.Status);
        Assert.Empty(_provider.TranslateCalls);
        _translationRequestService.Verify(s => s.ClearMediaHash(It.IsAny<TranslationRequest>()), Times.Once);
    }

    private static SubtitleItem Item(int position, params string[] lines) => new()
    {
        Position = position,
        Lines = lines.ToList(),
        PlaintextLines = lines.ToList()
    };

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

    private sealed class FakeSettingService : ISettingService
    {
        public Dictionary<string, string> Values { get; } = new();

        public event SettingChangedHandler? SettingChanged
        {
            add { }
            remove { }
        }

        public Task<string?> GetSetting(string key) =>
            Task.FromResult(Values.GetValueOrDefault(key));

        public Task<Dictionary<string, string>> GetSettings(IEnumerable<string> keys) =>
            Task.FromResult(keys.ToDictionary(key => key, key => Values.GetValueOrDefault(key, string.Empty)));

        public Task<bool> SetSetting(string key, string value) => Task.FromResult(true);
        public Task<bool> SetSettings(Dictionary<string, string> settings) => Task.FromResult(true);
        public Task<List<T>> GetSettingAsJson<T>(string key) where T : class => Task.FromResult(new List<T>());
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
        public List<SubtitleItem> SourceItems { get; set; } = [];
        public List<string> TranslateCalls { get; } = [];
        public List<List<Contracts.Models.Batch.BatchSubtitleItem>> BatchCalls { get; } = [];
        public Exception? ThrowOnTranslate { get; set; }

        public string? ModelName => "stub-model";

        public Task<string> TranslateAsync(
            string text, string sourceLanguage, string targetLanguage,
            List<string>? contextLinesBefore, List<string>? contextLinesAfter,
            CancellationToken cancellationToken)
        {
            if (ThrowOnTranslate is not null) throw ThrowOnTranslate;
            TranslateCalls.Add(text);
            return Task.FromResult($"translated:{text}");
        }

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
            if (ThrowOnTranslate is not null) throw ThrowOnTranslate;
            BatchCalls.Add(new List<Contracts.Models.Batch.BatchSubtitleItem>(subtitleBatch));
            var result = subtitleBatch.ToDictionary(item => item.Position, item => $"translated:{item.Line}");
            return Task.FromResult(result);
        }
    }
}
