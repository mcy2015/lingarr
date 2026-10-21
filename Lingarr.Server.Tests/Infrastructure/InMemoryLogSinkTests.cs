using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Lingarr.Server.Modules.Infrastructure;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Lingarr.Server.Tests.Infrastructure;

/// <summary>
/// The sink is one static process-wide instance shared by every test below,
/// so the classes of this collection must not run in parallel with each
/// other.
/// </summary>
[CollectionDefinition("InMemoryLog", DisableParallelization = true)]
public class InMemoryLogCollection
{
}

[Collection("InMemoryLog")]
public class InMemoryLogSinkTests
{
    private static void AddEntry(string message, LogLevel level = LogLevel.Information) =>
        InMemoryLogSink.AddLog(new LogEntry
        {
            LogLevel = level,
            Message = message,
            Timestamp = DateTimeOffset.UtcNow,
            Category = "Lingarr.Server.Tests.InMemoryLogSinkTests"
        });

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
    public void AddLog_UnderConcurrency_AssignsContiguousUniqueSeqRange()
    {
        long before = InMemoryLogSink.LastSeq;
        using var subscription = InMemoryLogSink.Subscribe();
        const int total = 200; // below the 256 channel bound, so nothing is dropped

        Parallel.For(0, total, _ => AddEntry("seq-race"));

        var delivered = Drain(subscription);

        // Every write got a unique seq from the same counter, and the counter
        // advanced by exactly the number of writes: no duplicates, no gaps.
        Assert.Equal(total, InMemoryLogSink.LastSeq - before);
        Assert.Equal(total, delivered.Count);
        var expected = Enumerable.Range((int)(before + 1), total).Select(i => (long)i).OrderBy(i => i);
        Assert.Equal(expected, delivered.Select(e => e.Seq).OrderBy(i => i));
    }

    [Fact]
    public void Subscriber_WhenChannelOverflows_CountsDropsAndSynthesizesAccurateGap()
    {
        using var subscription = InMemoryLogSink.Subscribe();

        // Capacity is 256: the first 256 land, the next 44 are dropped.
        for (int i = 0; i < 300; i++)
        {
            AddEntry("overflow-fill");
        }
        Assert.Equal(44, subscription.DroppedCount);

        var delivered = Drain(subscription);
        Assert.Equal(256, delivered.Count);

        // The next successful write is preceded by a gap marker whose message
        // reports exactly how many entries were skipped, and which reuses the
        // seq of the real entry it precedes (lossless EventSource ids).
        AddEntry("overflow-after");
        var afterGap = Drain(subscription);

        Assert.Equal(2, afterGap.Count);
        var gap = afterGap[0];
        Assert.True(gap.IsGapMarker);
        Assert.Equal(LogLevel.Warning, gap.LogLevel);
        Assert.Contains("44", gap.Message);
        var real = afterGap[1];
        Assert.Equal("overflow-after", real.Message);
        Assert.False(real.IsGapMarker);
        Assert.Equal(real.Seq, gap.Seq);
        Assert.Equal(0, subscription.DroppedCount);
    }

    [Fact]
    public void MinimumLevel_Information_WithParallelDebugFlood_KeepsRingAndChannelClean()
    {
        var logger = new InMemoryLogger("Lingarr.Server.Tests.Storm");
        InMemoryLogSettings.Configure(LogLevel.Information);
        try
        {
            long before = InMemoryLogSink.LastSeq;
            using var subscription = InMemoryLogSink.Subscribe();

            logger.Log(LogLevel.Error, default, "storm-error-anchor", null, static (s, _) => s);
            Parallel.For(0, 100_000, _ =>
                logger.Log(LogLevel.Debug, default, "storm-debug", null, static (s, _) => s));
            logger.Log(LogLevel.Warning, default, "storm-warning-anchor", null, static (s, _) => s);

            // Only the two anchors reached the sink — the Debug flood was
            // filtered before it could evict Warning/Error from the ring.
            Assert.Equal(2, InMemoryLogSink.LastSeq - before);

            var ring = InMemoryLogSink.GetRecentLogs(1000).ToList();
            Assert.Contains(ring, e => e.Message == "storm-error-anchor");
            Assert.Contains(ring, e => e.Message == "storm-warning-anchor");
            Assert.DoesNotContain(ring, e => e.LogLevel == LogLevel.Debug);

            var delivered = Drain(subscription);
            Assert.Equal(2, delivered.Count);
            Assert.All(delivered, e => Assert.NotEqual(LogLevel.Debug, e.LogLevel));
        }
        finally
        {
            InMemoryLogSettings.Configure(LogLevel.Information);
        }
    }

    [Fact]
    public void MinimumLevel_IsConfigurable_AndFiltersBelowIt()
    {
        var logger = new InMemoryLogger("Lingarr.Server.Tests.Levels");
        long before = InMemoryLogSink.LastSeq;
        InMemoryLogSettings.Configure(LogLevel.Warning);
        try
        {
            logger.LogInformation("filtered-information");
            logger.LogWarning("kept-warning");

            Assert.Equal(1, InMemoryLogSink.LastSeq - before);
            var last = InMemoryLogSink.GetRecentLogs(1).Single();
            Assert.Equal("kept-warning", last.Message);
        }
        finally
        {
            InMemoryLogSettings.Configure(LogLevel.Information);
        }
    }

    [Fact]
    public void Logger_ExceptionsGoToStackTraceField_NotIntoMessage()
    {
        var logger = new InMemoryLogger("Lingarr.Server.Tests.Exception");
        var exception = new InvalidOperationException("kaboom");

        logger.LogError(exception, "operation failed");

        var entry = InMemoryLogSink.GetRecentLogs(1).Single();
        Assert.Equal("operation failed", entry.Message);
        Assert.Contains("kaboom", entry.StackTrace);
        Assert.Contains(nameof(InvalidOperationException), entry.StackTrace);
    }

    [Fact]
    public void LogEntry_Serialization_KeepsLegacyFields_AndAddsSeqStackTrace()
    {
        var entry = new LogEntry
        {
            LogLevel = LogLevel.Warning,
            Message = "legacy-shape",
            Timestamp = new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.Zero),
            Category = "Lingarr.Server.Tests.Shape",
            StackTrace = "System.Exception: shape"
        };

        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(entry));
        var names = doc.RootElement.EnumerateObject().Select(p => p.Name).OrderBy(n => n);

        // The five legacy wire fields keep their exact names; seq and
        // stackTrace are additive, nothing else leaks onto the wire.
        Assert.Equal(
            new[] { "logLevel", "message", "timestamp", "category", "formattedSource", "seq", "stackTrace" }.OrderBy(n => n),
            names);
        Assert.Equal("Warning", doc.RootElement.GetProperty("logLevel").GetString());
        Assert.Equal("legacy-shape", doc.RootElement.GetProperty("message").GetString());
        Assert.Equal("Shape", doc.RootElement.GetProperty("formattedSource").GetString());
        Assert.Equal("System.Exception: shape", doc.RootElement.GetProperty("stackTrace").GetString());
    }
}
