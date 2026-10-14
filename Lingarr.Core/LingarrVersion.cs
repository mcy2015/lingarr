using System.Reflection;
using System.Runtime.InteropServices;
using Lingarr.Core.Models;

namespace Lingarr.Core;

public static class LingarrVersion
{
    public const string Name = "Lingarr";

    public static readonly string Number = GetCurrentVersion();

    public static string Platform { get; } = BuildPlatform();

    private static string BuildPlatform()
    {
        var arch = RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return $"linux/{arch}";
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return $"windows/{arch}";
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return $"macos/{arch}";
        }

        return $"unknown/{arch}";
    }

    private static string GetCurrentVersion()
    {
        var attribute = typeof(LingarrVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>();
        var version = attribute?.InformationalVersion ?? "0.0.0";

        var buildMetadataStart = version.IndexOf('+');
        return buildMetadataStart < 0 ? version : version[..buildMetadataStart];
    }

    /// <summary>
    /// Builds the version info shown in the UI. This fork ships as a
    /// self-built image with no release feed to compare against, so the
    /// latest version always equals the running one.
    /// </summary>
    public static VersionInfo GetVersionInfo()
    {
        return new VersionInfo
        {
            NewVersion = false,
            IsDevelopment = !Version.TryParse(Number, out _),
            CurrentVersion = Number,
            LatestVersion = Number
        };
    }
}
