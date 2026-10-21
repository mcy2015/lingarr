using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Lingarr.Server.Modules.Infrastructure;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Lingarr.Server.Tests.Infrastructure;

/// <summary>
/// L2 stamping acceptance of the in-memory sink: LogEntry carries the five
/// correlation keys exactly as the ambient context read at WRITE time (set
/// after logger construction still stamps), a cleared context stamps nothing,
/// and the search → download chain accumulates keys per stage. Joins the
/// serialized sink collection because the ring is process-wide static state.
/// </summary>
[Collection("InMemoryLog")]
public class InMemoryLoggerCorrelationTests
{
    private readonly InMemoryLogger _logger = new("Lingarr.Server.Tests.Correlation");

    /// <summary>Everything the subscription channel currently holds, in order.</summary>
    private static List<LogEntry> Drain(InMemoryLogSink.LogSubscription subscription)
    {
        var entries = new List<LogEntry>();
        while (subscription.Reader.TryRead(out var entry))
        {
            entries.Add(entry);
        }

        return entries;
    }

    [Fact]
    public void Log_WithFullCorrelation_StampsAllFiveFields()
    {
        try
        {
            CorrelationContext.SetJobId("hf-31");
            CorrelationContext.SetRequestId("42");
            CorrelationContext.SetSearchSessionId(7);
            CorrelationContext.SetMediaKey("Episode/99");
            CorrelationContext.SetProvider("opensubtitles");

            var marker = Guid.NewGuid().ToString("N");
            using var subscription = InMemoryLogSink.Subscribe();
            _logger.LogWarning("{Marker}", marker);
            var entry = Drain(subscription).Single(e => e.Message == marker);

            Assert.Equal("hf-31", entry.JobId);
            Assert.Equal("42", entry.RequestId);
            Assert.Equal(7, entry.SearchSessionId);
            Assert.Equal("Episode/99", entry.MediaKey);
            Assert.Equal("opensubtitles", entry.Provider);
        }
        finally
        {
            CorrelationContext.Clear();
        }
    }

    [Fact]
    public void Log_AfterClear_StampsNoCorrelation()
    {
        try
        {
            CorrelationContext.SetJobId("stale");
            CorrelationContext.Clear();

            var marker = Guid.NewGuid().ToString("N");
            using var subscription = InMemoryLogSink.Subscribe();
            _logger.LogInformation("{Marker}", marker);
            var entry = Drain(subscription).Single(e => e.Message == marker);

            Assert.Null(entry.JobId);
            Assert.Null(entry.RequestId);
            Assert.Null(entry.SearchSessionId);
            Assert.Null(entry.MediaKey);
            Assert.Null(entry.Provider);
        }
        finally
        {
            CorrelationContext.Clear();
        }
    }

    [Fact]
    public void SearchToDownloadChain_KeysAccumulatePerStage()
    {
        // Chain-level value: one logical flow walks search round → per-source
        // iteration → download decision, each stage narrowing the grep. The
        // entries written at each stage must snapshot exactly the keys set so
        // far — no more (no leaked stale keys), no fewer.
        try
        {
            var stageSearch = Guid.NewGuid().ToString("N");
            var stageSource = Guid.NewGuid().ToString("N");
            var stageDownload = Guid.NewGuid().ToString("N");
            var stageAfter = Guid.NewGuid().ToString("N");
            using var subscription = InMemoryLogSink.Subscribe();

            // Stage 1 — the search round established its session (the shape
            // the orchestrator's per-source flow stamps after telemetry).
            using (CorrelationContext.Push(searchSessionId: 7))
            {
                _logger.LogInformation("{Marker}", stageSearch);

                // Stage 2 — iterating one source of that round.
                using (CorrelationContext.Push(provider: "assrt"))
                {
                    _logger.LogInformation("{Marker}", stageSource);

                    // Stage 3 — the download decision for one media item.
                    using (CorrelationContext.Push(mediaKey: "Movie/123"))
                    {
                        _logger.LogInformation("{Marker}", stageDownload);
                    }
                }
            }

            // Every scope disposed — the flow is back to unattributed.
            _logger.LogInformation("{Marker}", stageAfter);

            var entries = Drain(subscription);
            var search = entries.Single(e => e.Message == stageSearch);
            var source = entries.Single(e => e.Message == stageSource);
            var download = entries.Single(e => e.Message == stageDownload);
            var after = entries.Single(e => e.Message == stageAfter);

            Assert.Null(search.Provider);
            Assert.Null(search.MediaKey);
            Assert.Equal(7, search.SearchSessionId);

            Assert.Equal(7, source.SearchSessionId);
            Assert.Equal("assrt", source.Provider);
            Assert.Null(source.MediaKey);

            Assert.Equal(7, download.SearchSessionId);
            Assert.Equal("assrt", download.Provider);
            Assert.Equal("Movie/123", download.MediaKey);

            Assert.Null(after.SearchSessionId);
            Assert.Null(after.Provider);
            Assert.Null(after.MediaKey);
        }
        finally
        {
            CorrelationContext.Clear();
        }
    }

    [Fact]
    public void Serialization_DropsNullCorrelationFields_AndKeepsSetOnes()
    {
        try
        {
            CorrelationContext.SetRequestId("42");
            CorrelationContext.SetProvider("assrt");

            var marker = Guid.NewGuid().ToString("N");
            _logger.LogInformation("{Marker}", marker);
            var entry = InMemoryLogSink.GetRecentLogs(1000).Last(e => e.Message == marker);

            using var correlated = JsonDocument.Parse(JsonSerializer.Serialize(entry));
            Assert.Equal("42", correlated.RootElement.GetProperty("requestId").GetString());
            Assert.Equal("assrt", correlated.RootElement.GetProperty("provider").GetString());
            // Null keys are absent from the wire, not explicit nulls — the
            // pre-L2 SSE frame shape is preserved entry for entry.
            Assert.False(correlated.RootElement.TryGetProperty("jobId", out _));
            Assert.False(correlated.RootElement.TryGetProperty("searchSessionId", out _));
            Assert.False(correlated.RootElement.TryGetProperty("mediaKey", out _));

            CorrelationContext.Clear();
            var bareMarker = Guid.NewGuid().ToString("N");
            _logger.LogInformation("{Marker}", bareMarker);
            var bare = InMemoryLogSink.GetRecentLogs(1000).Last(e => e.Message == bareMarker);

            using var uncorrelated = JsonDocument.Parse(JsonSerializer.Serialize(bare));
            foreach (var key in new[] { "jobId", "requestId", "searchSessionId", "mediaKey", "provider" })
            {
                Assert.False(uncorrelated.RootElement.TryGetProperty(key, out _), key + " must be absent");
            }
        }
        finally
        {
            CorrelationContext.Clear();
        }
    }
}
