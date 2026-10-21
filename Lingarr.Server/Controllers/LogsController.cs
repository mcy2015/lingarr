using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Lingarr.Server.Modules.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Lingarr.Server.Controllers;

[ApiController]
[Authorize("LingarrAccess")]
[Route("api/[controller]")]
public class LogsController : ControllerBase
{
    /// <summary>Entries replayed to a fresh (or gap-recovering) client.</summary>
    private const int ReplayCount = 100;

    // Real-time intervals, exposed as static seams (no InternalsVisibleTo) so
    // the test suite can shrink them instead of waiting 15s per heartbeat.
    public static TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(15);
    public static TimeSpan BatchWindow { get; set; } = TimeSpan.FromMilliseconds(50);
    public static int MaxBatchSize { get; set; } = 32;

    [HttpGet("stream")]
    public async Task GetLogStreamAsync(CancellationToken cancellationToken)
    {
        Response.ContentType = "text/event-stream";
        Response.Headers.Append("Cache-Control", "no-cache");
        Response.Headers.Append("Connection", "keep-alive");

        // Subscribe BEFORE snapshotting the ring: every entry logged after
        // this point is fanned out to our channel, so replay + channel cannot
        // lose entries (the old Count-diff polling raced both sides).
        using var subscription = InMemoryLogSink.Subscribe();
        try
        {
            var reader = subscription.Reader;
            var snapshot = InMemoryLogSink.GetRecentLogs(1000).ToList();
            // Seqs assigned up to this moment. Channel entries above this
            // line were assigned after the snapshot and can never be
            // duplicates of the replay — the dedup set stays bounded.
            long replayCeiling = InMemoryLogSink.LastSeq;

            long floor = 0;
            if (Request.Headers.TryGetValue("Last-Event-ID", out var lastEventIdValues) &&
                long.TryParse(lastEventIdValues.ToString(), out var lastEventId))
            {
                floor = lastEventId;
            }

            var sentSeqs = new HashSet<long>();
            var replay = BuildReplay(snapshot, floor, sentSeqs);
            if (replay.Count > 0)
            {
                await WriteFramesAsync(replay, cancellationToken);
            }
            else
            {
                // Send the headers even when there is nothing to replay.
                await Response.Body.FlushAsync(cancellationToken);
            }

            var lastFlushTimestamp = Stopwatch.GetTimestamp();
            while (!cancellationToken.IsCancellationRequested)
            {
                var untilHeartbeat = HeartbeatInterval - Stopwatch.GetElapsedTime(lastFlushTimestamp);
                if (untilHeartbeat < TimeSpan.Zero)
                {
                    untilHeartbeat = TimeSpan.Zero;
                }

                bool dataAvailable;
                using (var heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    heartbeatCts.CancelAfter(untilHeartbeat);
                    try
                    {
                        dataAvailable = await reader.WaitToReadAsync(heartbeatCts.Token);
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        // Silent period expired — SSE comment frame. EventSource
                        // ignores it, but proxies keep the connection alive.
                        await WritePingAsync(cancellationToken);
                        lastFlushTimestamp = Stopwatch.GetTimestamp();
                        continue;
                    }
                }

                if (!dataAvailable)
                {
                    break;
                }

                var batch = await FillBatchAsync(reader, cancellationToken);
                if (batch.Count == 0)
                {
                    continue;
                }

                // Stable order by seq: a gap marker keeps its place ahead of
                // the real entry that carries the same seq.
                var outgoing = new List<LogEntry>(batch.Count);
                foreach (var entry in batch.OrderBy(e => e.Seq))
                {
                    if (entry.IsGapMarker)
                    {
                        outgoing.Add(entry);
                        continue;
                    }
                    if (entry.Seq <= floor)
                    {
                        // Client reported already having it via Last-Event-ID.
                        continue;
                    }
                    if (entry.Seq <= replayCeiling && !sentSeqs.Add(entry.Seq))
                    {
                        // Already replayed on this connection.
                        continue;
                    }
                    outgoing.Add(entry);
                }

                if (outgoing.Count > 0)
                {
                    await WriteFramesAsync(outgoing, cancellationToken);
                    lastFlushTimestamp = Stopwatch.GetTimestamp();
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Client disconnected — normal SSE teardown. The subscription is
            // disposed by the using above so the sink's subscriber list
            // cannot leak.
        }
    }

    /// <summary>
    /// Builds the initial backlog: the last <see cref="ReplayCount"/> entries
    /// for a fresh connection; the entries strictly above a Last-Event-ID
    /// resume point; or a gap marker plus the last 100 when the ring has been
    /// trimmed past the resume point.
    /// </summary>
    private static List<LogEntry> BuildReplay(List<LogEntry> snapshot, long floor, HashSet<long> sentSeqs)
    {
        List<LogEntry> replay;
        if (floor > 0 && snapshot.Count > 0 && snapshot.Min(e => e.Seq) > floor + 1)
        {
            // The ring no longer reaches back to the resume point — say so
            // instead of silently skipping what was trimmed.
            var oldestSeq = snapshot.Min(e => e.Seq);
            var gap = new LogEntry
            {
                Seq = oldestSeq - 1,
                LogLevel = LogLevel.Warning,
                Message = $"... skipped {oldestSeq - floor - 1} entries ...",
                Timestamp = DateTimeOffset.UtcNow,
                Category = "Lingarr.InMemoryLogSink",
                IsGapMarker = true
            };
            replay = new List<LogEntry>(ReplayCount + 1) { gap };
            replay.AddRange(snapshot.TakeLast(ReplayCount));
        }
        else if (floor > 0)
        {
            replay = snapshot.Where(e => e.Seq > floor).ToList();
        }
        else
        {
            replay = snapshot.TakeLast(ReplayCount).ToList();
        }

        foreach (var entry in replay)
        {
            if (!entry.IsGapMarker)
            {
                sentSeqs.Add(entry.Seq);
            }
        }

        return replay;
    }

    /// <summary>
    /// Collects up to <see cref="MaxBatchSize"/> entries, coalescing a burst
    /// within <see cref="BatchWindow"/> into a single write/flush pair.
    /// </summary>
    private static async Task<List<LogEntry>> FillBatchAsync(
        ChannelReader<LogEntry> reader, CancellationToken cancellationToken)
    {
        var capacity = Math.Max(1, MaxBatchSize);
        var batch = new List<LogEntry>(capacity);
        var windowStart = Stopwatch.GetTimestamp();
        using var windowCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        while (batch.Count < capacity)
        {
            if (reader.TryRead(out var entry))
            {
                batch.Add(entry);
                continue;
            }

            var remaining = BatchWindow - Stopwatch.GetElapsedTime(windowStart);
            if (remaining <= TimeSpan.Zero)
            {
                break;
            }

            windowCts.CancelAfter(remaining);
            bool more;
            try
            {
                more = await reader.WaitToReadAsync(windowCts.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                break; // batch window elapsed with what we have
            }
            if (!more)
            {
                break;
            }
        }

        return batch;
    }

    private async Task WriteFramesAsync(IReadOnlyList<LogEntry> entries, CancellationToken cancellationToken)
    {
        var builder = new StringBuilder();
        foreach (var entry in entries)
        {
            builder.Append("id: ").Append(entry.Seq).Append('\n')
                .Append("data: ").Append(JsonSerializer.Serialize(entry)).Append('\n').Append('\n');
        }

        var bytes = Encoding.UTF8.GetBytes(builder.ToString());
        await Response.Body.WriteAsync(bytes, cancellationToken);
        await Response.Body.FlushAsync(cancellationToken);
    }

    private async Task WritePingAsync(CancellationToken cancellationToken)
    {
        await Response.Body.WriteAsync(Encoding.UTF8.GetBytes(": ping\n\n"), cancellationToken);
        await Response.Body.FlushAsync(cancellationToken);
    }
}
