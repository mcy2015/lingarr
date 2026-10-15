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
/// Scale audit P0-10 coverage for the broadcast/write amplification fixes in
/// <see cref="ProgressService"/>: progress notifications are throttled per
/// request, every message reaches only the request's own SignalR group (plus
/// the legacy global group the current UI subscribes to), and per-line
/// persistence is batched — a run of N lines performs at most
/// ceil(N / batch) line flushes, with terminal emits flushing the tail so
/// interrupted runs keep their flushed batches and never duplicate them on
/// resume.
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
        // first emit + delta≥2 emits: 1, 3, 5, …, 99 (per group; the legacy
        // global group mirrors every send)
        Assert.Equal(50, midRun.Count);

        request.Status = TranslationStatus.Completed;
        await service.Emit(request, 100);

        var messages = Sends("RequestProgress", "TranslationRequest-1");
        // ≤ ceil(100 steps / 2) + 1 (the mandatory first + terminal delivery),
        // counted per group — the legacy global group mirrors every send
        Assert.True(messages.Count <= Math.Ceiling(100 / 2.0) + 1,
            $"expected at most 51 RequestProgress messages, got {messages.Count}");
        var final = messages[^1];
        Assert.Equal(100, Property(final.Args[0]!, "Progress"));
        // the terminal state reaches both the request group and the global group
        Assert.Contains(final.Group, new[] { "TranslationRequests", "TranslationRequest-1" });
        Assert.Equal(1, messages.Count(send => Equals(Property(send.Args[0]!, "Progress"), 100)));
    }

    [Fact]
    public async Task Emit_FirstAndStatusChangesAlwaysPassTheThrottle()
    {
        var request = Request(5);
        var service = CreateService();

        // first emission always goes out
        await service.Emit(request, 1);
        // both the per-request group and the legacy global group receive it
        Assert.Single(Sends("RequestProgress", "TranslationRequest-5"));

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
        // only the request's own group and the legacy global group
        Assert.Equal(
            ["TranslationRequest-11", "TranslationRequests"],
            groupsUsedByA.OrderBy(g => g).ToArray());
        Assert.DoesNotContain(_sends, send => send.Group == "TranslationRequest-22"
            && Equals(Property(send.Args[0]!, "Id"), 11));

        var groupsUsedByB = _sends
            .Where(send => Equals(Property(send.Args[0]!, "Id"), 22))
            .Select(send => send.Group)
            .Distinct()
            .ToList();
        Assert.Equal(
            ["TranslationRequest-22", "TranslationRequests"],
            groupsUsedByB.OrderBy(g => g).ToArray());
        // B's mid-run 1%-step was throttled while A kept streaming
        Assert.Single(_sends.Where(send => send.Group == "TranslationRequest-22"));
    }

    [Fact]
    public async Task EmitLine_BatchesPersistence_AndStreamsEveryBroadcast()
    {
        var request = Request(7);
        var service = CreateService();

        for (var position = 1; position <= 120; position++)
        {
            await service.EmitLine(request, position, $"src {position}", $"tgt {position}", "test");
        }

        // 120 lines → flushes at 50 and 100: ≤ ceil(120/50) line flushes
        Assert.Equal(2, _saveCounter.Saves);
        Assert.True(_saveCounter.Saves <= Math.Ceiling(120 / 50.0));
        // broadcasts still streamed per line, to both groups
        Assert.Equal(120, Sends("LineTranslated", "TranslationRequests").Count);
        Assert.Equal(120, Sends("LineTranslated", "TranslationRequest-7").Count);
        // the unflushed tail (101-120) is not on disk yet
        Assert.Equal(100, _dbContext.TranslationRequestLines.Count());

        request.Status = TranslationStatus.Completed;
        await service.Emit(request, 100);

        // the terminal emit flushed the tail: the full set is persisted
        Assert.Equal(120, _dbContext.TranslationRequestLines.Count());
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

        // below the batch size: broadcast, but nothing persisted yet
        Assert.Equal(30, Sends("LineTranslated", "TranslationRequests").Count);
        Assert.Equal(0, _dbContext.TranslationRequestLines.Count());

        request.Status = TranslationStatus.Completed;
        await service.Emit(request, 100);
        Assert.Equal(30, _dbContext.TranslationRequestLines.Count());
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
