using System.Collections.Concurrent;
using System.Text.Json.Serialization;
using System.Threading.Channels;

namespace Lingarr.Server.Modules.Infrastructure
{
    public class LogEntry
    {
        private LogLevel _logLevel;

        // Wire format carries LogLevel as the lowercase string below; the
        // raw enum must not leak a second PascalCase field into the JSON.
        [JsonIgnore]
        public LogLevel LogLevel
        {
            get => _logLevel;
            set => _logLevel = value;
        }

        [JsonPropertyName("logLevel")]
        public string LogLevelString => LogLevel.ToString();

        [JsonPropertyName("message")]
        public string? Message { get; set; }

        [JsonPropertyName("timestamp")]
        public DateTimeOffset Timestamp { get; set; }

        [JsonPropertyName("category")]
        public string? Category { get; set; }

        [JsonPropertyName("formattedSource")]
        public string FormattedSource => Category?.Split('.').LastOrDefault() ?? Category ?? string.Empty;

        /// <summary>
        /// Monotonic sequence assigned by <see cref="InMemoryLogSink.AddLog"/>, starting at 1.
        /// The SSE stream exposes it as the event id so clients can resume with
        /// Last-Event-ID without the Count-diff races of the old polling loop.
        /// </summary>
        [JsonPropertyName("seq")]
        public long Seq { get; set; }

        /// <summary>
        /// Full exception dump. Since L1b the exception is no longer appended
        /// to <see cref="Message"/> — it lives here as its own field.
        /// </summary>
        [JsonPropertyName("stackTrace")]
        public string? StackTrace { get; set; }

        // ── L2 correlation keys ─────────────────────────────────────────
        //
        // Stamped by InMemoryLogger.Log from CorrelationContext at write
        // time (never at logger construction), one field per key, all
        // nullable and null-dropped on the wire: an entry without
        // correlation serializes exactly like its pre-L2 shape, so the SSE
        // stream and every existing consumer are unaffected. Key semantics
        // and DB alignment are documented on CorrelationContext.

        /// <summary>Hangfire background job id (translation_requests.JobId).</summary>
        [JsonPropertyName("jobId")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? JobId { get; set; }

        /// <summary>Translation request id (translation_requests.Id).</summary>
        [JsonPropertyName("requestId")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? RequestId { get; set; }

        /// <summary>Deciding subtitle search session (subtitle_search_sessions.Id).</summary>
        [JsonPropertyName("searchSessionId")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public int? SearchSessionId { get; set; }

        /// <summary>Media key "{mediaType}/{mediaId}" (subtitle_downloads.MediaType + MediaId).</summary>
        [JsonPropertyName("mediaKey")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? MediaKey { get; set; }

        /// <summary>Subtitle source key (cooldown store / telemetry spelling).</summary>
        [JsonPropertyName("provider")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Provider { get; set; }

        /// <summary>
        /// In-process only (never serialized): marks the synthetic
        /// "... skipped N entries ..." entries emitted after a subscriber's
        /// channel overflowed, or the replay-gap marker of the SSE endpoint.
        /// </summary>
        [JsonIgnore]
        public bool IsGapMarker { get; set; }
    }

    /// <summary>
    /// Independent minimum level for the in-memory sink, decoupled from the
    /// global LoggerFactory filters: raising the global level to Debug for the
    /// console must not flood the 1000-entry ring (which would evict the
    /// Warning/Error entries the log page exists for). The provider is
    /// constructed without IConfiguration (see InfrastructureModule), so the
    /// key is read from the environment variable form
    /// <c>InMemoryLog__MinimumLevel</c> (standard double-underscore mapping
    /// of the <c>InMemoryLog:MinimumLevel</c> key) once at first use.
    /// </summary>
    public static class InMemoryLogSettings
    {
        private const string MinimumLevelEnvironmentKey = "InMemoryLog__MinimumLevel";

        private static int _minimumLevel = LoadDefaultMinimumLevel();

        public static LogLevel MinimumLevel => (LogLevel)Volatile.Read(ref _minimumLevel);

        /// <summary>Overrides the minimum level at runtime (also the test seam).</summary>
        public static void Configure(LogLevel minimumLevel) => Volatile.Write(ref _minimumLevel, (int)minimumLevel);

        private static int LoadDefaultMinimumLevel()
        {
            var raw = Environment.GetEnvironmentVariable(MinimumLevelEnvironmentKey);
            return Enum.TryParse(raw, ignoreCase: true, out LogLevel parsed)
                ? (int)parsed
                : (int)LogLevel.Information;
        }
    }

    public static class InMemoryLogSink
    {
        private static readonly ConcurrentQueue<LogEntry> Logs = new();
        private static readonly int MaxLogCount = 1000;
        private static readonly ConcurrentDictionary<Guid, Subscriber> Subscribers = new();
        private static long _seqCounter;

        // Internal (not private) so the containing sink and its public
        // LogSubscription ctor (which takes it as a parameter) stay within
        // one accessibility domain.
        internal sealed class Subscriber
        {
            public Channel<LogEntry> Channel { get; } = System.Threading.Channels.Channel.CreateBounded<LogEntry>(
                new BoundedChannelOptions(256) { SingleReader = true });

            public long Dropped;
        }

        public sealed class LogSubscription : IDisposable
        {
            private readonly Guid _id;
            private readonly Subscriber _subscriber;

            internal LogSubscription(Guid id, Subscriber subscriber)
            {
                _id = id;
                _subscriber = subscriber;
            }

            public ChannelReader<LogEntry> Reader => _subscriber.Channel.Reader;

            /// <summary>Entries dropped for this subscriber because its channel was full.</summary>
            public long DroppedCount => Volatile.Read(ref _subscriber.Dropped);

            public void Dispose() => Subscribers.TryRemove(_id, out _);
        }

        /// <summary>Last sequence handed out by <see cref="AddLog"/>; starts at 0.</summary>
        public static long LastSeq => Volatile.Read(ref _seqCounter);

        public static LogSubscription Subscribe()
        {
            var id = Guid.NewGuid();
            var subscriber = new Subscriber();
            Subscribers[id] = subscriber;
            return new LogSubscription(id, subscriber);
        }

        public static void AddLog(LogEntry logEntry)
        {
            logEntry.Seq = Interlocked.Increment(ref _seqCounter);

            Logs.Enqueue(logEntry);

            // Trim logs if we exceed the maximum count and only keep logs for 24h
            while (Logs.Count > MaxLogCount && Logs.TryDequeue(out _))
            {
            }
            var cutoffTime = DateTimeOffset.UtcNow.AddHours(-24);
            while (Logs.TryPeek(out var oldestLog) && oldestLog.Timestamp < cutoffTime && Logs.TryDequeue(out _))
            {
            }

            if (Subscribers.IsEmpty)
            {
                return;
            }

            // Fan out to every subscriber without ever blocking the logging
            // path and without per-subscriber locks: TryWrite on a full
            // bounded channel returns false immediately, the dropped entry is
            // counted, and the next successful write is preceded by a
            // synthetic gap entry reusing that entry's seq (so EventSource
            // ids stay lossless across the gap).
            foreach (var subscriber in Subscribers.Values)
            {
                long dropped = Interlocked.Exchange(ref subscriber.Dropped, 0);
                if (dropped > 0)
                {
                    var gap = new LogEntry
                    {
                        Seq = logEntry.Seq,
                        LogLevel = LogLevel.Warning,
                        Message = $"... skipped {dropped} entries ...",
                        Timestamp = logEntry.Timestamp,
                        Category = "Lingarr.InMemoryLogSink",
                        IsGapMarker = true
                    };
                    if (!subscriber.Channel.Writer.TryWrite(gap))
                    {
                        // Still full — restore the count; the real entry write
                        // below fails too and adds itself to it.
                        Interlocked.Add(ref subscriber.Dropped, dropped);
                    }
                }

                if (!subscriber.Channel.Writer.TryWrite(logEntry))
                {
                    Interlocked.Increment(ref subscriber.Dropped);
                }
            }
        }

        public static IEnumerable<LogEntry> GetRecentLogs(int count)
        {
            return Logs.TakeLast(count);
        }

        public static ConcurrentQueue<LogEntry> LogQueue => Logs;
    }

    public class InMemoryLogger : ILogger
    {
        private readonly string _categoryName;

        public InMemoryLogger(string categoryName)
        {
            _categoryName = categoryName;
        }

        IDisposable? ILogger.BeginScope<TState>(TState state) => null;

        public bool IsEnabled(LogLevel logLevel) =>
            logLevel != LogLevel.None && logLevel >= InMemoryLogSettings.MinimumLevel;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
                return;

            var message = formatter(state, exception);
            // L2: stamp the ambient correlation keys at write time — the
            // snapshot is taken here, not when the logger was created, so a
            // key set anywhere earlier in the same flow reaches this entry.
            var correlation = CorrelationContext.Capture();
            var logEntry = new LogEntry
            {
                LogLevel = logLevel,
                Message = message,
                StackTrace = exception?.ToString(),
                Timestamp = DateTimeOffset.UtcNow,
                Category = _categoryName,
                JobId = correlation.JobId,
                RequestId = correlation.RequestId,
                SearchSessionId = correlation.SearchSessionId,
                MediaKey = correlation.MediaKey,
                Provider = correlation.Provider
            };

            InMemoryLogSink.AddLog(logEntry);
        }
    }

    public class InMemoryLoggerProvider : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName)
        {
            return new InMemoryLogger(categoryName);
        }

        public void Dispose()
        {
        }
    }
}
