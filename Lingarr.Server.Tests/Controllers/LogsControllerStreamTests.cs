using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Lingarr.Server.Controllers;
using Lingarr.Server.Modules.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Lingarr.Server.Tests.Controllers;

/// <summary>
/// SSE shape of GET /api/logs/stream: replay + Last-Event-ID resume, gap
/// markers past the ring, heartbeat comment frames, and write coalescing.
/// Shares the static <see cref="InMemoryLogSink"/> with the sink tests, so it
/// joins their non-parallel collection.
/// </summary>
[Collection("InMemoryLog")]
public class LogsControllerStreamTests
{
    /// <summary>
    /// Response body substitute: records every WriteAsync/FlushAsync call and
    /// allows the test thread to snapshot what has been written so far.
    /// </summary>
    private sealed class RecordingStream : Stream
    {
        private readonly MemoryStream _buffer = new();
        public int WriteCount;
        public int FlushCount;

        public string Capture()
        {
            lock (_buffer)
            {
                return Encoding.UTF8.GetString(_buffer.ToArray());
            }
        }

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => true;
        public override long Length { get { lock (_buffer) return _buffer.Length; } }
        public override long Position
        {
            get { lock (_buffer) return _buffer.Position; }
            set { lock (_buffer) _buffer.Position = value; }
        }

        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) { lock (_buffer) return _buffer.Read(buffer, offset, count); }
        public override long Seek(long offset, SeekOrigin origin) { lock (_buffer) return _buffer.Seek(offset, origin); }
        public override void SetLength(long value) { lock (_buffer) _buffer.SetLength(value); }
        public override void Write(byte[] buffer, int offset, int count) { lock (_buffer) _buffer.Write(buffer, offset, count); }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref WriteCount);
            lock (_buffer)
            {
                _buffer.Write(buffer, offset, count);
            }
            return Task.CompletedTask;
        }

        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref FlushCount);
            return Task.CompletedTask;
        }
    }

    private static (LogsController Controller, DefaultHttpContext Context, RecordingStream Body, CancellationTokenSource Cts) Create()
    {
        var body = new RecordingStream();
        var context = new DefaultHttpContext();
        context.Response.Body = body;
        var cts = new CancellationTokenSource();
        var controller = new LogsController();
        controller.ControllerContext = new ControllerContext { HttpContext = context };
        return (controller, context, body, cts);
    }

    private static void Add(string message, LogLevel level = LogLevel.Information) =>
        InMemoryLogSink.AddLog(new LogEntry
        {
            LogLevel = level,
            Message = message,
            Timestamp = DateTimeOffset.UtcNow,
            Category = "Lingarr.Server.Tests.LogsControllerStreamTests"
        });

    private static List<(string Id, string Data)> ParseFrames(string raw)
    {
        var frames = new List<(string Id, string Data)>();
        foreach (var chunk in raw.Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
        {
            string id = "";
            string? data = null;
            foreach (var line in chunk.Split('\n'))
            {
                if (line.StartsWith("id: ", StringComparison.Ordinal))
                {
                    id = line.Substring(4);
                }
                else if (line.StartsWith("data: ", StringComparison.Ordinal))
                {
                    data = line.Substring(6);
                }
            }
            if (data != null)
            {
                frames.Add((id, data));
            }
        }
        return frames;
    }

    private static int CountOccurrences(string text, string value)
    {
        int count = 0, index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }
        return count;
    }

    private static async Task<string> WaitForAsync(RecordingStream body, Func<string, bool> ready, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var snapshot = body.Capture();
            if (ready(snapshot))
            {
                return snapshot;
            }
            await Task.Delay(25);
        }
        return body.Capture();
    }

    [Fact]
    public async Task Stream_WithoutLastEventId_ReplaysLast100_ThenDeliversLiveEntries()
    {
        var (controller, _, body, cts) = Create();
        for (int i = 0; i < 150; i++)
        {
            Add("replay-base-" + i);
        }
        var task = Task.Run(() => controller.GetLogStreamAsync(cts.Token));
        try
        {
            var replayed = await WaitForAsync(body, s => CountOccurrences(s, "data: ") >= 100, TimeSpan.FromSeconds(10));
            var beforeLive = ParseFrames(replayed);

            // Exactly the newest 100 of the 150, oldest first, every frame
            // carrying its seq as the SSE id.
            Assert.Equal(100, beforeLive.Count);
            Assert.Contains(beforeLive, f => f.Data.Contains("\"replay-base-149\""));
            for (int i = 0; i < 50; i++)
            {
                var marker = "replay-base-" + i;
                Assert.DoesNotContain(beforeLive, f => f.Data.Contains("\"" + marker + "\""));
            }
            var ids = beforeLive.Select(f => long.Parse(f.Id)).ToList();
            Assert.Equal(ids.OrderBy(i => i), ids);
            Assert.All(beforeLive, f => Assert.Contains("\"seq\":", f.Data));

            Add("replay-live");
            var withLive = await WaitForAsync(body, s => s.Contains("\"replay-live\""), TimeSpan.FromSeconds(10));
            var frames = ParseFrames(withLive);
            Assert.Equal(101, frames.Count);
            Assert.Contains(frames, f => f.Data.Contains("\"replay-live\""));
        }
        finally
        {
            cts.Cancel();
            await task;
            cts.Dispose();
        }
    }

    [Fact]
    public async Task Stream_WithLastEventId_DeliversExactlyTheEntriesMissedWhileDisconnected()
    {
        var (controller, context, body, cts) = Create();
        for (int i = 0; i < 5; i++)
        {
            Add("disconnect-" + i);
        }
        var seqs = InMemoryLogSink.GetRecentLogs(5).Select(e => e.Seq).ToList();
        // The client saw up to entry #2 (index 1) before dropping.
        context.Request.Headers["Last-Event-ID"] = seqs[1].ToString();

        var task = Task.Run(() => controller.GetLogStreamAsync(cts.Token));
        try
        {
            var raw = await WaitForAsync(body, s => s.Contains("\"disconnect-4\""), TimeSpan.FromSeconds(10));
            var frames = ParseFrames(raw);

            // Exactly the three missed entries: no duplicates of what the
            // client already had, nothing lost.
            Assert.Equal(3, frames.Count);
            Assert.Contains(frames, f => f.Data.Contains("\"disconnect-2\""));
            Assert.Contains(frames, f => f.Data.Contains("\"disconnect-3\""));
            Assert.Contains(frames, f => f.Data.Contains("\"disconnect-4\""));
            Assert.DoesNotContain(frames, f => f.Data.Contains("\"disconnect-0\""));
            Assert.DoesNotContain(frames, f => f.Data.Contains("\"disconnect-1\""));
            var ids = frames.Select(f => long.Parse(f.Id)).ToList();
            Assert.Equal(seqs[2], ids.First());
            Assert.Equal(seqs[4], ids.Last());
            Assert.Equal(ids.OrderBy(i => i), ids);
        }
        finally
        {
            cts.Cancel();
            await task;
            cts.Dispose();
        }
    }

    [Fact]
    public async Task Stream_WithLastEventId_BeyondTrimmedRing_SendsGapMarkerThenLast100()
    {
        var (controller, context, body, cts) = Create();
        long resumeFrom = InMemoryLogSink.LastSeq;
        for (int i = 0; i < 1100; i++)
        {
            Add("gap-flood-" + i);
        }
        context.Request.Headers["Last-Event-ID"] = resumeFrom.ToString();

        var task = Task.Run(() => controller.GetLogStreamAsync(cts.Token));
        try
        {
            var raw = await WaitForAsync(
                body,
                s => ParseFrames(s).Count >= 101 && s.Contains("skipped"),
                TimeSpan.FromSeconds(10));
            var frames = ParseFrames(raw);

            // One gap marker plus the 100 newest entries (flood-1000..1099).
            Assert.Equal(101, frames.Count);
            Assert.Contains("skipped", frames[0].Data);
            Assert.Contains("Warning", frames[0].Data);
            Assert.Contains("\"gap-flood-1000\"", frames[1].Data);
            Assert.Contains("\"gap-flood-1099\"", frames[100].Data);
            Assert.DoesNotContain(frames[1].Data, "\"gap-flood-999\"");
            // The gap marker's id is one below the ring's oldest surviving
            // entry (floor+101); replay starts at flood #1000, whose seq is
            // floor+1001 (i is zero-based), because only the last 100 of the
            // ring are resent.
            Assert.Equal(resumeFrom + 100, long.Parse(frames[0].Id));
            Assert.Equal(resumeFrom + 1001, long.Parse(frames[1].Id));
        }
        finally
        {
            cts.Cancel();
            await task;
            cts.Dispose();
        }
    }

    [Fact]
    public async Task Stream_DuringSilence_EmitsPingCommentFrames()
    {
        var originalInterval = LogsController.HeartbeatInterval;
        LogsController.HeartbeatInterval = TimeSpan.FromMilliseconds(80);
        var (controller, _, body, cts) = Create();
        var task = Task.Run(() => controller.GetLogStreamAsync(cts.Token));
        try
        {
            await WaitForAsync(body, s => CountOccurrences(s, ": ping") >= 2, TimeSpan.FromSeconds(10));
            var raw = body.Capture();
            // SSE comment frame, properly terminated and with no data line.
            Assert.Contains(": ping\n\n", raw);
            Assert.True(CountOccurrences(raw, ": ping") >= 2);
        }
        finally
        {
            LogsController.HeartbeatInterval = originalInterval;
            cts.Cancel();
            await task;
            cts.Dispose();
        }
    }

    [Fact]
    public async Task Stream_BurstOfEntries_IsCoalescedIntoASingleWriteAndFlush()
    {
        var originalWindow = LogsController.BatchWindow;
        var originalHeartbeat = LogsController.HeartbeatInterval;
        LogsController.BatchWindow = TimeSpan.FromSeconds(2);
        LogsController.HeartbeatInterval = TimeSpan.FromSeconds(10);
        var (controller, _, body, cts) = Create();
        Add("batch-anchor");
        var task = Task.Run(() => controller.GetLogStreamAsync(cts.Token));
        try
        {
            // Wait for the replay flush so it cannot be conflated with the burst.
            await WaitForAsync(body, _ => Volatile.Read(ref body.FlushCount) >= 1, TimeSpan.FromSeconds(10));

            for (int i = 0; i < 10; i++)
            {
                Add("burst-" + i);
            }
            await WaitForAsync(body, s => s.Contains("\"burst-9\""), TimeSpan.FromSeconds(10));
            await Task.Delay(100); // let the trailing flush land before counting

            var raw = body.Capture();
            Assert.Equal(10, ParseFrames(raw).Count(f => f.Data.Contains("burst-")));
            // One write for the replay, one for the whole burst — not one per entry.
            Assert.Equal(2, Volatile.Read(ref body.WriteCount));
            Assert.Equal(2, Volatile.Read(ref body.FlushCount));
        }
        finally
        {
            LogsController.BatchWindow = originalWindow;
            LogsController.HeartbeatInterval = originalHeartbeat;
            cts.Cancel();
            await task;
            cts.Dispose();
        }
    }
}
