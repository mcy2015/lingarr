using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Lingarr.Server.Modules.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Lingarr.Server.Tests.Infrastructure;

/// <summary>
/// End-to-end tests for the shared Serilog file sink, driven through the
/// real provider-registration path (<see cref="InfrastructureModule.AddLogProviders"/>).
/// The shared logger is process-wide static state, so every test resets it in
/// its constructor and disposes it in teardown — the class must stay the only
/// place that touches it.
/// </summary>
public class SerilogFileLoggerFileSinkTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "lingarr-serilog-" + Guid.NewGuid().ToString("N"));

    public SerilogFileLoggerFileSinkTests()
    {
        Directory.CreateDirectory(_root);
        SerilogFileLogger.Shutdown();
    }

    public void Dispose()
    {
        SerilogFileLogger.Shutdown();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // best-effort temp cleanup
        }
    }

    private IConfiguration BuildConfig(string minimumLevel) =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Serilog:MinimumLevel:Default"] = minimumLevel,
            ["Serilog:FilePath"] = Path.Combine(_root, "lingarr-.log")
        }).Build();

    /// <summary>Flushes the shared sink (dispose:false everywhere else) and reads the single rolled file.</summary>
    private string FlushAndReadSingleLogFile()
    {
        SerilogFileLogger.Shutdown();
        var file = Assert.Single(Directory.GetFiles(_root, "lingarr-*.log"));
        return File.ReadAllText(file);
    }

    private static int CountOccurrences(string content, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = content.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }

    [Fact]
    public void FileSink_WritesMessagesWithLevelAndCategory()
    {
        using var factory = LoggerFactory.Create(
            logging => InfrastructureModule.AddLogProviders(logging, BuildConfig("Information")));

        var logger = factory.CreateLogger("FileSinkTestCategory");
        logger.LogInformation("file sink acceptance {Value}", 42);

        var content = FlushAndReadSingleLogFile();

        Assert.Contains("file sink acceptance 42", content);
        // the output template carries the level and the ILogger category
        Assert.Contains("INF", content);
        Assert.Contains("FileSinkTestCategory", content);
    }

    [Fact]
    public void FileSink_WritesFullException_IndependentlyOfTheMessageLine()
    {
        using var factory = LoggerFactory.Create(
            logging => InfrastructureModule.AddLogProviders(logging, BuildConfig("Information")));

        factory.CreateLogger("FileSinkTestCategory")
            .LogError(new InvalidOperationException("boom for the file sink"), "failure recorded {Code}", 7);

        var content = FlushAndReadSingleLogFile();

        Assert.Contains("failure recorded 7", content);
        Assert.Contains("InvalidOperationException", content);
        Assert.Contains("boom for the file sink", content);

        // the exception is rendered on its own lines after the message,
        // not folded into it
        var lines = content.Split('\n');
        var messageLineIndex = Array.FindIndex(lines, l => l.Contains("failure recorded 7"));
        Assert.True(messageLineIndex >= 0, "message line missing from: " + content);
        Assert.DoesNotContain("InvalidOperationException", lines[messageLineIndex]);
        Assert.Contains(lines.Skip(messageLineIndex + 1),
            line => line.Contains("InvalidOperationException"));
    }

    [Fact]
    public void FileSink_MinimumLevel_FiltersEventsBelowTheConfiguredLevel()
    {
        using var factory = LoggerFactory.Create(
            logging => InfrastructureModule.AddLogProviders(logging, BuildConfig("Warning")));

        var logger = factory.CreateLogger("FilterTestCategory");
        logger.LogInformation("information event must be filtered");
        logger.LogWarning("warning event must survive");

        var content = FlushAndReadSingleLogFile();

        Assert.DoesNotContain("information event must be filtered", content);
        Assert.Contains("warning event must survive", content);
    }

    [Fact]
    public void FileSink_MinimumLevel_Debug_LetsDebugEventsThrough()
    {
        using var factory = LoggerFactory.Create(
            logging => InfrastructureModule.AddLogProviders(logging, BuildConfig("Debug")));

        factory.CreateLogger("FilterTestCategory").LogDebug("debug event must survive");

        var content = FlushAndReadSingleLogFile();

        Assert.Contains("debug event must survive", content);
    }

    [Fact]
    public void SharedLogger_BothFactoriesAppendToTheSameFile_WithoutDuplication()
    {
        // production order: the main factory initializes the sink from
        // configuration first; the plugin-loader factory (TranslationModule)
        // then registers without configuration and must reuse that instance.
        using var mainFactory = LoggerFactory.Create(
            logging => InfrastructureModule.AddLogProviders(logging, BuildConfig("Information")));
        using var pluginFactory = LoggerFactory.Create(
            logging => InfrastructureModule.AddLogProviders(logging));

        var first = SerilogFileLogger.GetOrCreate();
        Assert.Same(first, SerilogFileLogger.GetOrCreate());

        mainFactory.CreateLogger("MainFactory").LogInformation("message from the main factory");
        pluginFactory.CreateLogger("PluginFactory").LogInformation("message from the plugin factory");

        var content = FlushAndReadSingleLogFile();

        Assert.Equal(1, CountOccurrences(content, "message from the main factory"));
        Assert.Equal(1, CountOccurrences(content, "message from the plugin factory"));
    }
}
