namespace Lingarr.Server.Models.Api;

/// <summary>One file found by the server-path scan (A7).</summary>
public class ScannedSubtitleFile
{
    /// <summary>Absolute path on the server, to pass back to import-paths.</summary>
    public required string Path { get; init; }

    /// <summary>File name alone, for the workbench table.</summary>
    public required string FileName { get; init; }

    public long SizeBytes { get; init; }
}
