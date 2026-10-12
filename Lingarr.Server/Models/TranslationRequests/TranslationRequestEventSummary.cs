using Lingarr.Core.Enum;

namespace Lingarr.Server.Models.TranslationRequests;

/// <summary>
/// Recent-event row for the overview activity feed (gap-audit A3): one line per
/// status transition, joined with the owning request's title.
/// </summary>
public class TranslationRequestEventSummary
{
    public int Id { get; set; }
    public int RequestId { get; set; }
    public required string RequestTitle { get; set; }
    public required TranslationStatus Status { get; set; }
    public string? Message { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
