using Lingarr.Core.Data;
using Lingarr.Core.Entities;
using Lingarr.Core.Enum;
using Lingarr.Server.Interfaces.Services;
using Lingarr.Server.Models.TranslationRequests;
using Microsoft.EntityFrameworkCore;

namespace Lingarr.Server.Services;

public class TranslationRequestEventService : ITranslationRequestEventService
{
    private readonly LingarrDbContext _dbContext;

    public TranslationRequestEventService(LingarrDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task LogEvent(int translationRequestId, TranslationStatus status, string? message = null,
        TranslationEventType? eventType = null, string? provider = null)
    {
        // Dedup only against other status/lifecycle events: granular timeline
        // rows (batch progress, provider failures, degradations) sit between
        // lifecycle transitions and must not suppress them.
        var lastEvent = await _dbContext.TranslationRequestEvents
            .Where(e => e.TranslationRequestId == translationRequestId
                        && (e.EventType == null
                            || (e.EventType != TranslationEventType.ProviderFailed
                                && e.EventType != TranslationEventType.Degraded
                                && e.EventType != TranslationEventType.BatchCompleted)))
            .OrderByDescending(e => e.CreatedAt)
            .ThenByDescending(e => e.Id)
            .FirstOrDefaultAsync();

        if (lastEvent != null && lastEvent.Status == status)
        {
            return;
        }

        var translationEvent = new TranslationRequestEvent
        {
            TranslationRequestId = translationRequestId,
            Status = status,
            EventType = eventType,
            Provider = provider,
            Message = message
        };

        _dbContext.TranslationRequestEvents.Add(translationEvent);
        await _dbContext.SaveChangesAsync();
    }

    public async Task LogTimelineEvent(int translationRequestId, TranslationEventType eventType,
        TranslationStatus status, string? provider = null, string? message = null)
    {
        _dbContext.TranslationRequestEvents.Add(new TranslationRequestEvent
        {
            TranslationRequestId = translationRequestId,
            Status = status,
            EventType = eventType,
            Provider = provider,
            Message = message
        });
        await _dbContext.SaveChangesAsync();
    }

    public Task<bool> RequestExists(int translationRequestId) =>
        _dbContext.TranslationRequests.AnyAsync(r => r.Id == translationRequestId);

    public async Task<List<TranslationRequestEvent>> GetEvents(int translationRequestId)
    {
        return await _dbContext.TranslationRequestEvents
            .Where(e => e.TranslationRequestId == translationRequestId)
            .OrderBy(e => e.CreatedAt)
            .ThenBy(e => e.Id)
            .ToListAsync();
    }

    public async Task<List<TranslationRequestEventSummary>> GetRecentEvents(int take = 20)
    {
        return await _dbContext.TranslationRequestEvents
            // per-batch progress rows would drown the feed; provider failures
            // and degradations are exactly what the feed wants to surface
            .Where(e => e.EventType != TranslationEventType.BatchCompleted)
            .Join(_dbContext.TranslationRequests,
                e => e.TranslationRequestId,
                r => r.Id,
                (e, r) => new TranslationRequestEventSummary
                {
                    Id = e.Id,
                    RequestId = e.TranslationRequestId,
                    RequestTitle = r.Title,
                    Status = e.Status,
                    EventType = e.EventType,
                    Provider = e.Provider,
                    Message = e.Message,
                    CreatedAt = e.CreatedAt
                })
            .OrderByDescending(e => e.Id)
            .Take(take)
            .ToListAsync();
    }
}
