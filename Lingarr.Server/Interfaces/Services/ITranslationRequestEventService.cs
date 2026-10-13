using Lingarr.Core.Entities;
using Lingarr.Core.Enum;
using Lingarr.Server.Models.TranslationRequests;

namespace Lingarr.Server.Interfaces.Services;

public interface ITranslationRequestEventService
{
    Task LogEvent(int translationRequestId, TranslationStatus status, string? message = null,
        TranslationEventType? eventType = null, string? provider = null);

    /// <summary>
    /// Always persists a typed timeline event (P1-2) — no consecutive-status
    /// dedup, because batch/fallback events legitimately repeat.
    /// </summary>
    Task LogTimelineEvent(int translationRequestId, TranslationEventType eventType,
        TranslationStatus status, string? provider = null, string? message = null);

    Task<bool> RequestExists(int translationRequestId);
    Task<List<TranslationRequestEvent>> GetEvents(int translationRequestId);
    Task<List<TranslationRequestEventSummary>> GetRecentEvents(int take = 20);
}
