namespace Lingarr.Server.Models;

/// <summary>Body of POST /subtitles/import-paths (A7).</summary>
public class ImportPathsRequest
{
    public required List<string> Paths { get; init; }
    public string SourceLanguage { get; init; } = "en";
    public string TargetLanguage { get; init; } = "nl";
    public bool Translate { get; init; }
    public string? MediaType { get; init; }
    public int? MediaId { get; init; }
    public int? Season { get; init; }
    public string? ChineseConvert { get; init; }
    public bool IsBilingual { get; init; }
}
