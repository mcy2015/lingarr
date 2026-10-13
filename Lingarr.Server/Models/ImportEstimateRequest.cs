namespace Lingarr.Server.Models;

/// <summary>
/// Import cost estimate request (PM analysis P0): the files queued for
/// translation with their estimated translatable character counts.
/// </summary>
public class ImportEstimateRequest
{
    public required List<ImportEstimateFile> Files { get; init; }
    public int TranslateCount { get; init; }
    public string? TargetLanguage { get; init; }
}

public class ImportEstimateFile
{
    public required string Name { get; init; }
    public long Characters { get; init; }
}
