using Lingarr.Core.Entities;
using Lingarr.Core.Enum;
using Lingarr.Server.Models.TranslationRequests;

namespace Lingarr.Server.Interfaces.Services;

public interface ITranslationRequestEventService
{
    Task LogEvent(int translationRequestId, TranslationStatus status, string? message = null);
    Task<List<TranslationRequestEvent>> GetEvents(int translationRequestId);
    Task<List<TranslationRequestEventSummary>> GetRecentEvents(int take = 20);
}
