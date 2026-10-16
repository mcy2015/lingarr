using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Lingarr.Core.Data;
using Lingarr.Core.Entities;
using Lingarr.Core.Enum;
using Lingarr.Server.Hubs;
using Lingarr.Server.Models;
using Lingarr.Server.Modules.Translation;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Moq;
using Xunit;

namespace Lingarr.Server.Tests.Services;

/// <summary>
/// Scale audit P0-10 + P2 broadcast-collapse coverage for
/// <see cref="ProgressService"/>: progress notifications are throttled per
/// request, every message reaches only the request's own SignalR group (the
/// global <c>TranslationRequests</c> group — reserved for the
/// <c>ActiveTranslations</c> summary — receives nothing), line broadcasts
/// leave as batched <c>LineTranslatedBatch</c> array messages (full batch or
/// elapsed window, terminal emit flushes the tail) and per-line persistence
/// is batched — a run of N lines performs at most ceil(N / batch) line
/// flushes, so interrupted runs keep their flushed batches and never
/// duplicate them on resume.
/// </summary>
public class ProgressServiceTests : IDisposable
{
    private readonly ManualTimeProvider _time = new();
    private readonly SaveCountInterceptor _saveCounter = new();
    private readonly DbContextOptions<LingarrDbContext> _options;
    private readonly LingarrDbContext _dbContext;
    private readonly List<(string Group, string Method, object?[] Args)> _sends = [];
    private readonly Dictionary<string, Mock<IClientProxy>> _proxies = [];

    public ProgressServiceTests()
    {
        _options = new DbContextOptionsBuilder<LingarrDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .AddInterceptors(_saveCounter)
            .Options;
        _dbContext = new LingarrDbContext(_options);

        var clients = new Mock<IHubClients>();
        clients
            .Setup(c => c.Group(It.IsAny<string>()))
            .Returns((string group) => ProxyFor(group));
        var hubContext = new Mock<IHubContext<TranslationRequestsHub>>();
        hubContext.Setup(h => h.Clients).Returns(clients.Object);
        HubContext = hubContext.Object;
    }

    private IHubContext<TranslationRequestsHub> HubContext { get; }

    public void Dispose() => _dbContext.Dispose();

    private ProgressService CreateService() => new(HubContext, _dbContext, _time);

    private IClientProxy ProxyFor(string group)
    {
        if (_proxies.TryGetValue(group, out var proxy))
        {
            return proxy.Object;
        }

        proxy = new Mock<IClientProxy>();
        proxy
            .Setup(p => p.SendCoreAsync(
                It.IsAny<string>(),
                It.IsAny<object?[]>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask)
            .Callback<string, object?[], CancellationToken>((method, args, _) =>
                _sends.Add((group, method, args)));
        _proxies[group] = proxy;
        return proxy.Object;
    }

    private List<(string Group, string Method, object?[] Args)> Sends(
        string method, string? group = null) =>
        _sends.Where(send => send.Method == method && (group == null || send.Group == group)).ToList();

    private static object? Property(object payload, string name) =>
        payload.GetType().GetProperty(name)?.GetValue(payload);

    private static TranslationRequest Request(
        int id,
        TranslationStatus status = TranslationStatus.InProgress) => new()
    {
        Id = id,
        Title = $"request {id}",
        SourceLanguage = "en",
        TargetLanguage = "zh",
        MediaType = MediaType.Movie,
        Status = status
    };

    [Fact]
    public async Task Emit_ThrottlesMidRunProgress_TerminalStateAlwaysArrives()
    {
        var request = Request(1);
        var service = CreateService();

        // 99 mid-run 1%-steps with a frozen clock: only the first emission and
        // every ≥2% delta pass the throttle
        for (var progress = 1; progress < 100; progress++)
        {
            await service.Emit(request, progress);
        }

        var midRun = Sends("RequestProgress", "TranslationRequest-1");
        // first emit + delta≥2 emits: 1, 3, 5, …, 99 (the request's own group
        // only — the global group carries nothing per-request any more)
        Assert.Equal(50, midRun.Count);
        Assert.Empty(Sends("RequestProgress", "TranslationRequests"));

        request.Status = TranslationStatus.Completed;
        await service.Emit(request, 100);

        var messages = Sends("RequestProgress", "TranslationRequest-1");
        // ≤ ceil(100 steps / 2) + 1 (the mandatory first + terminal delivery)
        Assert.True(messages.Count <= Math.Ceiling(100 / 2.0) + 1,
            $"expected at most 51 RequestProgress messages, got {messages.Count}");
        var final = messages[^1];
        Assert.Equal(100, Property(final.Args[0]!, "Progress"));
        // the terminal state reaches only the request's own group
        Assert.Equal("TranslationRequest-1", final.Group);
        Assert.Equal(1, messages.Count(send => Equals(Property(send.Args[0]!, "Progress"), 100)));
        Assert.Empty(Sends("RequestProgress", "TranslationRequests"));
    }

    [Fact]
    public async Task Emit_FirstAndStatusChangesAlwaysPassTheThrottle()
    {
        var request = Request(5);
        var service = CreateService();

        // first emission always goes out
        await service.Emit(request, 1);
        // the request's own group receives it; the global group stays silent
        Assert.Single(Sends("RequestProgress", "TranslationRequest-5"));
        Assert.Empty(Sends("RequestProgress", "TranslationRequests"));

        // a status flip (pause) is announced even without any progress delta
        request.Status = TranslationStatus.Paused;
        await service.Emit(request, 1);
        var messages = Sends("RequestProgress");
        Assert.Equal(2, Sends("RequestProgress", "TranslationRequest-5").Count);
        var paused = messages[^1];
        Assert.Equal("Paused", Property(paused.Args[0]!, "Status"));
    }

    [Fact]
    public async Task Emit_PassesTheThrottleAfterTheIntervalElapses()
    {
        var request = Request(6);
        var service = CreateService();

        await service.Emit(request, 10);
        var savesAfterFirst = _saveCounter.Saves;

        // frozen clock + delta < 2 → throttled: no save, no broadcast
        await service.Emit(request, 11);
        Assert.Equal(savesAfterFirst, _saveCounter.Saves);
        Assert.Single(Sends("RequestProgress", "TranslationRequest-6"));

        // 600ms later the same small delta passes via the interval rule alone
        _time.Advance(TimeSpan.FromMilliseconds(600));
        await service.Emit(request, 11);
        Assert.Equal(2, Sends("RequestProgress", "TranslationRequest-6").Count);
        Assert.Equal(savesAfterFirst + 1, _saveCounter.Saves);
    }

    [Fact]
    public async Task Emit_GroupsByRequestId_RequestsNeverCrossGroups()
    {
        var service = CreateService();
        var requestA = Request(11);
        var requestB = Request(22);

        await service.Emit(requestA, 1);
        await service.Emit(requestA, 3);
        await service.Emit(requestB, 1);
        await service.Emit(requestB, 2); // frozen clock, delta 1 → throttled
        await service.Emit(requestA, 5);
        await service.EmitLine(requestA, 1, "hello", "hola", "test");

        var groupsUsedByA = _sends
            .Where(send => Equals(Property(send.Args[0]!, "Id"), 11))
            .Select(send => send.Group)
            .Distinct()
            .ToList();
        // only the request's own group — never the global summary group
        Assert.Equal(["TranslationRequest-11"], groupsUsedByA);
        Assert.DoesNotContain(_sends, send => send.Group == "TranslationRequest-22"
            && Equals(Property(send.Args[0]!, "Id"), 11));

        var groupsUsedByB = _sends
            .Where(send => Equals(Property(send.Args[0]!, "Id"), 22))
            .Select(send => send.Group)
            .Distinct()
            .ToList();
        Assert.Equal(["TranslationRequest-22"], groupsUsedByB);
        // the global group received nothing from this service at all
        Assert.DoesNotContain(_sends, send => send.Group == "TranslationRequests");
        // B's mid-run 1%-step was throttled while A kept streaming
        Assert.Single(_sends.Where(send => send.Group == "TranslationRequest-22"));
    }

    [Fact]
    public async Task EmitLine_BatchesPersistence_AndBatchesBroadcasts()
    {
        var request = Request(7);
        var service = CreateService();

        for (var position = 1; position <= 120; position++)
        {
            await service.EmitLine(request, position, $"src {position}", $"tgt {position}", "test");
        }

        // 120 lines → persistence flushes at 50 and 100: ≤ ceil(120/50) saves
        Assert.Equal(2, _saveCounter.Saves);
        Assert.True(_saveCounter.Saves <= Math.Ceiling(120 / 50.0));
        // broadcasts left as batches of 50 (frozen clock → count rule only),
        // to the request's group alone — the retired per-line event is gone
        // and the global group stays silent
        var batches = Sends("LineTranslatedBatch", "TranslationRequest-7");
        Assert.Equal(2, batches.Count);
        Assert.All(batches, batch => Assert.Equal(50, ((object[])batch.Args[0]!).Length));
        Assert.Empty(Sends("LineTranslated", "TranslationRequest-7"));
        Assert.Empty(Sends("LineTranslatedBatch", "TranslationRequests"));
        // the unflushed tail (101-120) is not on disk yet
        Assert.Equal(100, _dbContext.TranslationRequestLines.Count());

        request.Status = TranslationStatus.Completed;
        await service.Emit(request, 100);

        // the terminal emit flushed the broadcast tail and the persistence
        // tail: the full set is persisted, every line reached the wire
        Assert.Equal(120, _dbContext.TranslationRequestLines.Count());
        var allBatches = Sends("LineTranslatedBatch", "TranslationRequest-7");
        Assert.Equal(3, allBatches.Count);
        Assert.Equal(120, allBatches.Sum(batch => ((object[])batch.Args[0]!).Length));
    }

    [Fact]
    public async Task EmitLine_FlushesTheBroadcastBatchWhenTheWindowElapses()
    {
        var request = Request(12);
        var service = CreateService();

        // three lines inside the window (frozen clock): buffered, no send
        for (var position = 1; position <= 3; position++)
        {
            await service.EmitLine(request, position, $"src {position}", $"tgt {position}", "test");
        }
        Assert.Empty(Sends("LineTranslatedBatch", "TranslationRequest-12"));

        // the next line after the 50ms window flushes the whole buffer
        _time.Advance(TimeSpan.FromMilliseconds(60));
        await service.EmitLine(request, 4, "src 4", "tgt 4", "test");
        var first = Sends("LineTranslatedBatch", "TranslationRequest-12");
        Assert.Single(first);
        var items = (object[])first[0].Args[0]!;
        Assert.Equal(4, items.Length);
        Assert.Equal(
            new[] { 1, 2, 3, 4 },
            items.Select(item => (int)Property(item, "Position")!).ToArray());
        Assert.Equal(12, Property(items[0], "Id"));

        // a fresh window buffers again until the terminal emit flushes
        for (var position = 5; position <= 6; position++)
        {
            await service.EmitLine(request, position, $"src {position}", $"tgt {position}", "test");
        }
        request.Status = TranslationStatus.Completed;
        await service.Emit(request, 100);
        var all = Sends("LineTranslatedBatch", "TranslationRequest-12");
        Assert.Equal(2, all.Count);
        Assert.Equal(6, all.Sum(batch => ((object[])batch.Args[0]!).Length));
    }

    [Fact]
    public async Task NoMethodOfThisService_ReachesTheGlobalGroup()
    {
        var request = Request(13);
        var service = CreateService();

        await service.Emit(request, 1);
        await service.EmitLine(request, 1, "hello", "hola", "test");
        _time.Advance(TimeSpan.FromMilliseconds(60));
        await service.EmitLine(request, 2, "bye", "adiós", "test");
        await service.EmitProofreadLine(request, 1, "hello", "hallo", "proofread");

        // progress, batched lines and the interactive proofread path all
        // target the request's group — the global group is reserved for the
        // ActiveTranslations summary another service owns
        Assert.DoesNotContain(_sends, send => send.Group == "TranslationRequests");
        Assert.All(_sends, send => Assert.Equal("TranslationRequest-13", send.Group));
        Assert.Contains(_sends, send => send.Method == "LineProofread");
        Assert.Contains(_sends, send => send.Method == "LineTranslatedBatch");
    }

    [Fact]
    public async Task InterruptedRun_KeepsFlushedBatches_ResumeDoesNotDuplicate()
    {
        var request = Request(8);
        var service = CreateService();

        // 60 lines translated, then the process dies before any terminal emit:
        // the first 50 are flushed, the tail of 10 only exists in memory
        for (var position = 1; position <= 60; position++)
        {
            await service.EmitLine(request, position, $"src {position}", $"tgt {position}", "test");
        }
        Assert.Equal(50, _dbContext.TranslationRequestLines.Count());

        // a resumed run re-translates only the unflushed tail (the job's
        // checkpoint reads persisted rows by position), then completes
        var resumedContext = new LingarrDbContext(_options);
        await using (resumedContext)
        {
            var resumedService = new ProgressService(HubContext, resumedContext, _time);
            for (var position = 51; position <= 60; position++)
            {
                await resumedService.EmitLine(request, position, $"src {position}", $"re-tgt {position}", "test");
            }
            request.Status = TranslationStatus.Completed;
            await resumedService.Emit(request, 100);
        }

        var lines = _dbContext.TranslationRequestLines.ToList();
        Assert.Equal(60, lines.Count);
        // positions are unique — already-batched lines never re-persist
        Assert.Equal(Enumerable.Range(1, 60).ToList(), lines.Select(l => l.Position).OrderBy(p => p).ToList());
    }

    [Fact]
    public async Task EmitLines_BuffersUnderTheBatchSize_FlushesOnTerminal()
    {
        var request = Request(9);
        var service = CreateService();

        await service.EmitLines(request, Enumerable.Range(1, 30).Select(position => new TranslatedLineData
        {
            Position = position,
            Source = $"src {position}",
            Target = $"tgt {position}",
            Service = "test"
        }).ToList());

        // below the batch size with a frozen clock: buffered, nothing on the
        // wire yet and nothing persisted yet
        Assert.Empty(Sends("LineTranslatedBatch", "TranslationRequest-9"));
        Assert.Equal(0, _dbContext.TranslationRequestLines.Count());

        request.Status = TranslationStatus.Completed;
        await service.Emit(request, 100);
        Assert.Equal(30, _dbContext.TranslationRequestLines.Count());
        // the terminal emit flushed the buffered broadcasts as one batch
        var batches = Sends("LineTranslatedBatch", "TranslationRequest-9");
        Assert.Single(batches);
        Assert.Equal(30, ((object[])batches[0].Args[0]!).Length);
    }

    /// <summary>Deterministic clock the throttle tests advance by hand.</summary>
    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _utcNow = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan delta) => _utcNow = _utcNow.Add(delta);
    }

    private sealed class SaveCountInterceptor : SaveChangesInterceptor
    {
        public int Saves { get; private set; }

        public override InterceptionResult<int> SavingChanges(
            DbContextEventData eventData,
            InterceptionResult<int> result)
        {
            Saves++;
            return base.SavingChanges(eventData, result);
        }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            Saves++;
            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }
}
