using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Lingarr.Core.Enum;
using Lingarr.Server.Modules.Infrastructure;
using Lingarr.Server.Modules.Media;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Lingarr.Server.Tests.Services;

/// <summary>
/// Audit P0-6 mutex semantics: the daily trigger no longer fails when the
/// previous run is still processing (the old DisableConcurrentExecution(5min)
/// + AutomaticRetry(0) combination made every later trigger fail during a
/// 6-14h first run). A still-running previous instance makes the new trigger
/// log, report Succeeded and skip the scan; the guard is an in-process flag
/// (single-container deployment) released even when the scan fails.
/// </summary>
public class EmbeddedTrackScanJobTests
{
    private readonly Mock<IEmbeddedTrackService> _embeddedTrackService = new();
    private readonly Mock<IScheduleService> _scheduleService = new();
    private readonly RecordingLogger _logger = new();

    private EmbeddedTrackScanJob CreateJob() => new(
        _embeddedTrackService.Object,
        _scheduleService.Object,
        _logger);

    private void SetupSuccessfulScan() =>
        _embeddedTrackService
            .Setup(service => service.ScanAll(It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EmbeddedTrackScanSummary(0, 0, 0, 0, 0));

    private sealed class RecordingLogger : ILogger<EmbeddedTrackScanJob>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Messages.Add(formatter(state, exception));
    }

    [Fact]
    public async Task PreviousInstanceStillRunning_TriggersSkipWithSucceededState()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _embeddedTrackService
            .Setup(service => service.ScanAll(It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns(async (bool force, CancellationToken ct) =>
            {
                entered.SetResult();
                await release.Task;
                return new EmbeddedTrackScanSummary(0, 0, 0, 0, 0);
            });

        var first = Task.Run(() => CreateJob().Execute());
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // second trigger while the first is inside ScanAll: skips the scan
        await CreateJob().Execute();

        _embeddedTrackService.Verify(service => service.ScanAll(It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Once);
        _scheduleService.Verify(service => service.UpdateJobState(It.IsAny<string>(), "Succeeded"), Times.Once);
        Assert.Contains(_logger.Messages, message => message.Contains("skipped"));

        release.SetResult();
        await first;
    }

    [Fact]
    public async Task NoOtherInstanceRunning_RunsTheScan()
    {
        SetupSuccessfulScan();

        await CreateJob().Execute();

        _embeddedTrackService.Verify(service => service.ScanAll(It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Once);
        _scheduleService.Verify(service => service.UpdateJobState(It.IsAny<string>(), "Succeeded"), Times.Once);
    }

    [Fact]
    public async Task ScanInProgress_ReleasedAfterFailure_AllowsTheNextRun()
    {
        _embeddedTrackService
            .SetupSequence(service => service.ScanAll(It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("probe blew up"))
            .ReturnsAsync(new EmbeddedTrackScanSummary(0, 0, 0, 0, 0));

        // L3: the failed run rethrows after recording (AutomaticRetry(0) —
        // no retry storm); the finally still releases the in-progress flag.
        await Assert.ThrowsAsync<InvalidOperationException>(() => CreateJob().Execute());

        _scheduleService.Verify(service => service.UpdateJobState(It.IsAny<string>(), "Failed"), Times.Once);

        await CreateJob().Execute(); // runs again — the flag did not stick

        _embeddedTrackService.Verify(service => service.ScanAll(It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
        _scheduleService.Verify(service => service.UpdateJobState(It.IsAny<string>(), "Succeeded"), Times.Once);
    }
}
