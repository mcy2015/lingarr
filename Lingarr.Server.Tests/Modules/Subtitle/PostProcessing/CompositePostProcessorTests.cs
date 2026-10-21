using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Lingarr.Server.Modules.Subtitle.PostProcessing;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Lingarr.Server.Tests.Modules.Subtitle.PostProcessing;

/// <summary>
/// CompositePostProcessor contracts (B01): stages run in DI order, a stage
/// failure is contained (warning, no rethrow, next stage still runs) and the
/// error text carries the stage name, Changed aggregates across stages, and
/// caller cancellation is the only propagation.
/// </summary>
public sealed class CompositePostProcessorTests : IDisposable
{
    private readonly string _file;

    public CompositePostProcessorTests()
    {
        _file = Path.Combine(Path.GetTempPath(), "lingarr-composite-" + Guid.NewGuid().ToString("N") + ".srt");
        File.WriteAllText(_file, "1\n00:00:01,000 --> 00:00:02,000\nx\n");
    }

    private static Mock<ISubtitlePostProcessor> Stage(
        string name,
        PostProcessResult? result = null,
        List<string>? calls = null)
    {
        var stage = new Mock<ISubtitlePostProcessor>();
        stage.SetupGet(processor => processor.Name).Returns(name);
        stage
            .Setup(processor => processor.ProcessAsync(
                It.IsAny<string>(), It.IsAny<PostProcessContext>(), It.IsAny<CancellationToken>()))
            .Callback(() => calls?.Add(name))
            .ReturnsAsync(result ?? PostProcessResult.Unchanged("noop"));
        return stage;
    }

    [Fact]
    public async Task ProcessAsync_RunsStagesInRegistrationOrder()
    {
        var calls = new List<string>();
        var first = Stage("First", calls: calls);
        var second = Stage("Second", calls: calls);
        var composite = new CompositePostProcessor(
            [first.Object, second.Object], NullLogger<CompositePostProcessor>.Instance);

        await composite.ProcessAsync(_file, new PostProcessContext(), CancellationToken.None);

        Assert.Equal(new[] { "First", "Second" }, calls);
    }

    [Fact]
    public async Task ProcessAsync_StageFailure_DoesNotBlockAndNamesTheStage()
    {
        var calls = new List<string>();
        var failing = Stage("Boom", calls: calls);
        failing
            .Setup(processor => processor.ProcessAsync(
                It.IsAny<string>(), It.IsAny<PostProcessContext>(), It.IsAny<CancellationToken>()))
            .Callback(() => calls.Add("Boom"))
            .ThrowsAsync(new InvalidOperationException("disk on fire"));
        var after = Stage("After", calls: calls);
        var composite = new CompositePostProcessor(
            [failing.Object, after.Object], NullLogger<CompositePostProcessor>.Instance);

        // No rethrow — the landed file must never be sunk by a stage.
        var result = await composite.ProcessAsync(_file, new PostProcessContext(), CancellationToken.None);

        Assert.Equal(new[] { "Boom", "After" }, calls);
        Assert.NotNull(result.Error);
        Assert.Contains("Boom", result.Error);
        Assert.Contains("disk on fire", result.Error);
    }

    [Fact]
    public async Task ProcessAsync_AggregatesChangedAndStageReportedErrors()
    {
        var changer = Stage("Changer", new PostProcessResult(true, "converted"));
        var reporter = Stage("Reporter", new PostProcessResult(false, Error: "halfway done"));
        var composite = new CompositePostProcessor(
            [changer.Object, reporter.Object], NullLogger<CompositePostProcessor>.Instance);

        var result = await composite.ProcessAsync(_file, new PostProcessContext(), CancellationToken.None);

        Assert.True(result.Changed);
        Assert.Contains("Reporter: halfway done", result.Error);
    }

    [Fact]
    public async Task ProcessAsync_NoStages_NoChangeNoError()
    {
        var composite = new CompositePostProcessor(
            Enumerable.Empty<ISubtitlePostProcessor>(), NullLogger<CompositePostProcessor>.Instance);

        var result = await composite.ProcessAsync(_file, new PostProcessContext(), CancellationToken.None);

        Assert.False(result.Changed);
        Assert.Null(result.Error);
    }

    [Fact]
    public async Task ProcessAsync_CallerCancellation_Propagates()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var hanging = Stage("Hanging");
        hanging
            .Setup(processor => processor.ProcessAsync(
                It.IsAny<string>(), It.IsAny<PostProcessContext>(), cancelled.Token))
            .ThrowsAsync(new OperationCanceledException(cancelled.Token));
        var never = Stage("Never");
        var composite = new CompositePostProcessor(
            [hanging.Object, never.Object], NullLogger<CompositePostProcessor>.Instance);

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            composite.ProcessAsync(_file, new PostProcessContext(), cancelled.Token));

        never.Verify(processor => processor.ProcessAsync(
            It.IsAny<string>(), It.IsAny<PostProcessContext>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    public void Dispose()
    {
        try
        {
            File.Delete(_file);
        }
        catch (IOException)
        {
            // Temp cleanup is best-effort.
        }
    }
}
