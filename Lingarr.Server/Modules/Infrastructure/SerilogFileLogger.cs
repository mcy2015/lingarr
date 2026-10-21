using System.Text;
using Microsoft.Extensions.Configuration;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using SerilogLogger = Serilog.ILogger;

namespace Lingarr.Server.Modules.Infrastructure;

/// <summary>
/// L2 correlation enricher of the shared file sink: reads the ambient
/// <see cref="CorrelationContext"/> at write time and synthesizes ONE
/// pre-formatted <c>Correlation</c> string property —
/// <c>job:abc req:42 sess:7 media:movie/123 src:assrt</c> (only the non-empty
/// segments, space-separated) — which the file template renders as
/// <c>[{Correlation}]</c>. A single pre-composed property keeps the template
/// and every future parser free of five optional placeholders; an event with
/// no correlation renders an empty pair of brackets.
/// </summary>
public sealed class CorrelationEnricher : ILogEventEnricher
{
    /// <summary>Serilog property name the output template references.</summary>
    public const string PropertyName = "Correlation";

    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        logEvent.AddOrUpdateProperty(
            propertyFactory.CreateProperty(PropertyName, Compose(CorrelationContext.Capture())));
    }

    /// <summary>
    /// Pure formatting seam (tests assert the exact segment order/spelling):
    /// job → req → sess → media → src, non-empty segments only, empty string
    /// when nothing is set.
    /// </summary>
    public static string Compose(CorrelationContext.Snapshot snapshot)
    {
        var builder = new StringBuilder();
        Append(builder, "job:", snapshot.JobId);
        Append(builder, "req:", snapshot.RequestId);
        if (snapshot.SearchSessionId is { } sessionId)
        {
            Append(builder, "sess:", sessionId.ToString());
        }

        Append(builder, "media:", snapshot.MediaKey);
        Append(builder, "src:", snapshot.Provider);
        return builder.ToString();
    }

    private static void Append(StringBuilder builder, string prefix, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        if (builder.Length > 0)
        {
            builder.Append(' ');
        }

        builder.Append(prefix).Append(value);
    }
}

/// <summary>
/// The process-wide Serilog file logger backing every logger factory in the
/// app. The main factory (built in <see cref="InfrastructureModule"/>) and the
/// standalone plugin-loader factory (TranslationModule) both route through
/// <see cref="GetOrCreate"/>: the first call initializes the sink, every later
/// call — with or without configuration — reuses the same instance, so plugin
/// logs and application logs append to one file instead of forking or
/// duplicating entries. All factories hold the logger with dispose:false;
/// <see cref="Shutdown"/> (wired to ApplicationStopping in Program.cs) is the
/// single flush-and-close point.
/// </summary>
public static class SerilogFileLogger
{
    /// <summary>
    /// Daily-rolling file inside the mounted /app/config volume; overridden to
    /// a relative path by appsettings.Development.json for bare-metal runs.
    /// </summary>
    public const string DefaultFilePath = "/app/config/logs/lingarr-.log";

    private const string FilePathKey = "Serilog:FilePath";
    private const string MinimumLevelKey = "Serilog:MinimumLevel:Default";

    /// <summary>
    /// File line shape (L2): the pre-formatted correlation tag rides between
    /// the source context and the message as <c>[job:a req:42 …]</c>. Public
    /// const so tests can pin that the template keeps referencing the
    /// enricher's property.
    /// </summary>
    public const string OutputTemplate =
        "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext} [{Correlation}] {Message:lj}{NewLine}{Exception}";

    private static readonly object Gate = new();
    private static SerilogLogger? _logger;

    /// <summary>
    /// Returns the shared file logger, creating it on first use. Thread-safe;
    /// once initialized the <paramref name="configuration"/> argument is
    /// ignored, which is what lets the configuration-less plugin-loader
    /// factory join the main factory's sink.
    /// </summary>
    public static SerilogLogger GetOrCreate(IConfiguration? configuration = null)
    {
        if (Volatile.Read(ref _logger) is { } initialized)
        {
            return initialized;
        }

        lock (Gate)
        {
            if (_logger is { } cached)
            {
                return cached;
            }

            var created = CreateLogger(configuration);
            Volatile.Write(ref _logger, created);
            return created;
        }
    }

    /// <summary>
    /// Flushes and closes the shared file logger. Idempotent; a subsequent
    /// <see cref="GetOrCreate"/> builds a fresh instance (the test seam).
    /// </summary>
    public static void Shutdown()
    {
        lock (Gate)
        {
            // Serilog's ILogger interface does not declare Dispose; the
            // concrete Logger implements it.
            (_logger as IDisposable)?.Dispose();
            _logger = null;
        }
    }

    /// <summary>
    /// Log file path from <c>Serilog:FilePath</c>, falling back to the
    /// container default.
    /// </summary>
    public static string ResolveFilePath(IConfiguration? configuration)
        => configuration?[FilePathKey] is { } configured && !string.IsNullOrWhiteSpace(configured)
            ? configured
            : DefaultFilePath;

    /// <summary>
    /// File-sink threshold from <c>Serilog:MinimumLevel:Default</c>. The env
    /// form <c>Serilog__MinimumLevel__Default=Debug</c> raises verbosity via
    /// the standard <c>__</c>→<c>:</c> env-var mapping, no extra package.
    /// Anything missing or unparseable falls back to Information.
    /// </summary>
    public static LogEventLevel ResolveMinimumLevel(IConfiguration? configuration)
        => Enum.TryParse(configuration?[MinimumLevelKey], ignoreCase: true, out LogEventLevel level)
            ? level
            : LogEventLevel.Information;

    private static SerilogLogger CreateLogger(IConfiguration? configuration)
    {
        var filePath = ResolveFilePath(configuration);
        try
        {
            return new LoggerConfiguration()
                .MinimumLevel.Is(ResolveMinimumLevel(configuration))
                // L2: stamps the ambient correlation keys onto every event as
                // the pre-formatted Correlation property the template renders.
                .Enrich.With(new CorrelationEnricher())
                // L5: console output replaces the retired ColoredConsoleLogger
                // (the pipe-marker color scheme is gone; the sink colors by
                // level). Same template as the file so docker logs and the
                // file agree line for line.
                .WriteTo.Console(outputTemplate: OutputTemplate)
                .WriteTo.File(
                    filePath,
                    rollingInterval: RollingInterval.Day,
                    fileSizeLimitBytes: 52_428_800, // 50 MB per file before rolling over
                    rollOnFileSizeLimit: true,
                    retainedFileCountLimit: 7,
                    flushToDiskInterval: TimeSpan.FromSeconds(2),
                    shared: false,
                    outputTemplate: OutputTemplate)
                .CreateLogger();
        }
        catch (Exception ex)
        {
            // File logging is an observability add-on: an unusable path must
            // not prevent boot. Console and in-memory providers stay active;
            // the reason surfaces on stderr for whoever owns the deployment.
            Console.Error.WriteLine(
                $"Serilog file sink disabled: could not initialize '{filePath}'.");
            Console.Error.WriteLine(ex);
            return new LoggerConfiguration().CreateLogger();
        }
    }
}
