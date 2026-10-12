using Lingarr.Core.Enum;

namespace Lingarr.Core.Entities;

public class TranslationRequest : BaseEntity
{
    public string? JobId  { get; set; }
    public TranslationJobType? JobType { get; set; }
    public int? MediaId  { get; set; }
    public required string Title { get; set; }
    public required string SourceLanguage { get; set; }
    public required string TargetLanguage { get; set; }
    public string? SubtitleToTranslate { get; set; }
    public string? TranslatedSubtitle { get; set; }
    /// <summary>
    /// Whether the written output subtitle is bilingual
    /// (translated lines on top, original line below).
    /// </summary>
    public bool IsBilingual { get; set; } = false;
    /// <summary>
    /// Where the request originated (library media vs manually imported file).
    /// </summary>
    public TranslationRequestSource Source { get; set; } = TranslationRequestSource.Library;
    public required MediaType MediaType { get; set; }
    public required TranslationStatus Status { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public string? ErrorMessage { get; set; }
    public string? StackTrace { get; set; }
    /// <summary>
    /// SHA-256 of the normalized subtitle content (UTF-8, LF endings) for
    /// import workbench duplicate detection; null for library requests.
    /// </summary>
    public string? ContentHash { get; set; }
}
