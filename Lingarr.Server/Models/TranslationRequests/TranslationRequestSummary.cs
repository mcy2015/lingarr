using Lingarr.Core.Enum;

namespace Lingarr.Server.Models.TranslationRequests;

/// <summary>
/// List-row projection of a translation request returned by the paged list endpoint.
/// Carries exactly the fields the SPA reads from list rows; entity internals the list
/// view never consumes (StackTrace, JobId, JobType, SubtitleToTranslate) are
/// deliberately excluded. MediaId/UpdatedAt back the media detail screen's
/// per-episode latest-state mapping. The single-item detail endpoint keeps
/// returning <see cref="TranslationRequestDetail"/>.
/// </summary>
public class TranslationRequestSummary
{
    public int Id { get; set; }
    public required string Title { get; set; }
    public required string SourceLanguage { get; set; }
    public required string TargetLanguage { get; set; }
    public string? TranslatedSubtitle { get; set; }
    public bool IsBilingual { get; set; }
    public required MediaType MediaType { get; set; }
    public required TranslationStatus Status { get; set; }
    public TranslationRequestSource Source { get; set; }
    public string? ServiceType { get; set; }
    public int Progress { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public int RetryCount { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public string? ErrorMessage { get; set; }
    public int? MediaId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
