namespace Lingarr.Server.Models.FileSystem;

public class TranslateAbleSubtitleLine
{
    public required string SubtitleLine { get; set; }
    public required string SourceLanguage { get; set; }
    public required string TargetLanguage { get; set; }
    public List<string>? ContextLinesBefore { get; set; }
    public List<string>? ContextLinesAfter { get; set; }

    /// <summary>
    /// Owning translation request, set by the per-line subtitle walker so
    /// fallback events land on the right timeline (P1-2). Null for ad-hoc
    /// API calls that are not tied to a request.
    /// </summary>
    public int? RequestId { get; set; }
}
