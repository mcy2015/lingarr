using System;
using System.Collections.Generic;
using System.Linq;
using Lingarr.Server.Modules.Infrastructure;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Xunit;

namespace Lingarr.Server.Tests.Infrastructure;

/// <summary>
/// L2 correlation enricher of the shared file sink: the pre-formatted
/// Correlation property joins only the non-empty segments in the fixed
/// job → req → sess → media → src order, an empty context yields the empty
/// string, and — the timing contract — the property is composed at WRITE
/// time from the ambient context, so two events of one logger carry their
/// own snapshots. Drives a standalone Serilog pipeline with a collecting
/// sink: the process-wide file-logger singleton stays untouched (its owning
/// test class is the only writer).
/// </summary>
public class SerilogCorrelationEnricherTests
{
    private static string? CorrelationOf(LogEvent logEvent)
        => logEvent.Properties.TryGetValue(CorrelationEnricher.PropertyName, out var value)
            && value is ScalarValue scalar
            ? scalar.Value as string
            : null;

    [Fact]
    public void Compose_FullSnapshot_JoinsAllSegmentsInOrder()
    {
        var composed = CorrelationEnricher.Compose(new CorrelationContext.Snapshot(
            JobId: "abc",
            RequestId: "42",
            SearchSessionId: 7,
            MediaKey: "movie/123",
            Provider: "assrt"));

        Assert.Equal("job:abc req:42 sess:7 media:movie/123 src:assrt", composed);
    }

    [Fact]
    public void Compose_PartialSnapshot_RendersOnlyNonEmptySegments()
    {
        var composed = CorrelationEnricher.Compose(new CorrelationContext.Snapshot(
            JobId: null,
            RequestId: null,
            SearchSessionId: 7,
            MediaKey: null,
            Provider: "opensubtitles"));

        Assert.Equal("sess:7 src:opensubtitles", composed);
    }

    [Fact]
    public void Compose_EmptySnapshot_ReturnsEmptyString()
    {
        Assert.Equal(string.Empty, CorrelationEnricher.Compose(default));
        Assert.Equal(string.Empty, CorrelationEnricher.Compose(new CorrelationContext.Snapshot(
            JobId: "  ", RequestId: "", null, null, null)));
    }

    [Fact]
    public void Enricher_ComposesAtWriteTime_FromTheAmbientContext()
    {
        var sink = new CollectingSink();
        using var logger = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .Enrich.With(new CorrelationEnricher())
            .WriteTo.Sink(sink)
            .CreateLogger();
        try
        {
            logger.Information("no correlation yet");
            using (CorrelationContext.Push(searchSessionId: 11, provider: "assrt"))
            {
                logger.Information("searching a source");
                using (CorrelationContext.Push(mediaKey: "Episode/5"))
                {
                    logger.Information("downloading");
                }
            }

            Assert.Equal(3, sink.Events.Count);
            Assert.Equal(string.Empty, CorrelationOf(sink.Events[0]));
            Assert.Equal("sess:11 src:assrt", CorrelationOf(sink.Events[1]));
            Assert.Equal("sess:11 media:Episode/5 src:assrt", CorrelationOf(sink.Events[2]));
        }
        finally
        {
            CorrelationContext.Clear();
        }
    }

    [Fact]
    public void FileSinkOutputTemplate_RendersTheCorrelationTag()
    {
        // The wiring contract between enricher and template: the property the
        // enricher publishes is the one the template prints, bracketed,
        // between the source context and the message.
        Assert.Contains("[{Correlation}]", SerilogFileLogger.OutputTemplate);
        Assert.Contains("{Message:lj}", SerilogFileLogger.OutputTemplate);
    }

    private sealed class CollectingSink : ILogEventSink
    {
        private readonly List<LogEvent> _events = [];

        public IReadOnlyList<LogEvent> Events => _events;

        public void Emit(LogEvent logEvent) => _events.Add(logEvent);
    }
}
