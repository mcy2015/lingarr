using Lingarr.Core.Enum;

namespace Lingarr.Core.Entities;

public class TranslationRequestEvent : BaseEntity
{
    public int TranslationRequestId { get; set; }
    public TranslationRequest TranslationRequest { get; set; } = null!;
    public required TranslationStatus Status { get; set; }

    /// <summary>
    /// Timeline event kind (P1-2). Null on status-transition rows written
    /// before the typed timeline existed.
    /// </summary>
    public TranslationEventType? EventType { get; set; }

    /// <summary>Provider the event is about (failure target, degradation destination).</summary>
    public string? Provider { get; set; }

    public string? Message { get; set; }
}
