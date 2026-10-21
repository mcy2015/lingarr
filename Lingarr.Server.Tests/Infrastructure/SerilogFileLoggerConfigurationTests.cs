using System;
using System.Collections.Generic;
using Lingarr.Server.Modules.Infrastructure;
using Microsoft.Extensions.Configuration;
using Serilog.Events;
using Xunit;

namespace Lingarr.Server.Tests.Infrastructure;

/// <summary>
/// Configuration binding for the shared Serilog file sink: the level and the
/// file path come from IConfiguration so the env-var route
/// (Serilog__MinimumLevel__Default) works with zero extra packages. These
/// tests exercise the pure resolvers only — the singleton file logger is
/// covered separately and stays untouched here.
/// </summary>
public class SerilogFileLoggerConfigurationTests
{
    private static IConfiguration BuildConfig(IEnumerable<KeyValuePair<string, string?>>? values = null)
        => new ConfigurationBuilder().AddInMemoryCollection(values ?? []).Build();

    // ── MinimumLevel ────────────────────────────────────────────────────

    [Fact]
    public void MinimumLevel_DefaultsToInformation_WithoutConfiguration()
    {
        Assert.Equal(LogEventLevel.Information, SerilogFileLogger.ResolveMinimumLevel(null));
        Assert.Equal(
            LogEventLevel.Information,
            SerilogFileLogger.ResolveMinimumLevel(BuildConfig()));
    }

    [Fact]
    public void MinimumLevel_ReadsOverrideFromConfiguration()
    {
        var config = BuildConfig(new Dictionary<string, string?>
        {
            ["Serilog:MinimumLevel:Default"] = "Debug"
        });

        Assert.Equal(LogEventLevel.Debug, SerilogFileLogger.ResolveMinimumLevel(config));
    }

    [Fact]
    public void MinimumLevel_IsCaseInsensitive()
    {
        var config = BuildConfig(new Dictionary<string, string?>
        {
            ["Serilog:MinimumLevel:Default"] = "warning"
        });

        Assert.Equal(LogEventLevel.Warning, SerilogFileLogger.ResolveMinimumLevel(config));
    }

    [Fact]
    public void MinimumLevel_InvalidValue_FallsBackToInformation()
    {
        var config = BuildConfig(new Dictionary<string, string?>
        {
            ["Serilog:MinimumLevel:Default"] = "Chatty"
        });

        Assert.Equal(LogEventLevel.Information, SerilogFileLogger.ResolveMinimumLevel(config));
    }

    [Fact]
    public void MinimumLevel_EnvVarDoubleUnderscore_MapsToConfigKey()
    {
        // The documented way to raise file-log verbosity in a running
        // container: Serilog__MinimumLevel__Default relies on the standard
        // __→: mapping of the environment-variables provider.
        const string variable = "Serilog__MinimumLevel__Default";
        Environment.SetEnvironmentVariable(variable, "Debug");
        try
        {
            var config = new ConfigurationBuilder().AddEnvironmentVariables().Build();

            Assert.Equal(LogEventLevel.Debug, SerilogFileLogger.ResolveMinimumLevel(config));
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
        }
    }

    // ── FilePath ────────────────────────────────────────────────────────

    [Fact]
    public void FilePath_DefaultsToContainerPath_WhenUnset()
    {
        Assert.Equal(SerilogFileLogger.DefaultFilePath, SerilogFileLogger.ResolveFilePath(null));
        Assert.Equal(
            SerilogFileLogger.DefaultFilePath,
            SerilogFileLogger.ResolveFilePath(BuildConfig()));
    }

    [Fact]
    public void FilePath_OverrideWins()
    {
        var config = BuildConfig(new Dictionary<string, string?>
        {
            ["Serilog:FilePath"] = "/tmp/elsewhere/lingarr-.log"
        });

        Assert.Equal("/tmp/elsewhere/lingarr-.log", SerilogFileLogger.ResolveFilePath(config));
    }

    [Fact]
    public void FilePath_Whitespace_FallsBackToDefault()
    {
        var config = BuildConfig(new Dictionary<string, string?>
        {
            ["Serilog:FilePath"] = "   "
        });

        Assert.Equal(SerilogFileLogger.DefaultFilePath, SerilogFileLogger.ResolveFilePath(config));
    }
}
