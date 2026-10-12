using Lingarr.Core.Data;
using Lingarr.Core.Entities;
using Lingarr.Core.Enum;
using Lingarr.Server.Hubs;
using Lingarr.Server.Interfaces.Services;
using Lingarr.Server.Models;
using Lingarr.Server.Models.TranslationRequests;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace Lingarr.Server.Services;

public class TranslationRequestQueryService : ITranslationRequestQueryService
{
    private readonly LingarrDbContext _dbContext;
    private readonly IHubContext<TranslationRequestsHub> _hubContext;
    private readonly ITranslationRequestEventService _eventService;

    public TranslationRequestQueryService(
        LingarrDbContext dbContext,
        IHubContext<TranslationRequestsHub> hubContext,
        ITranslationRequestEventService eventService)
    {
        _dbContext = dbContext;
        _hubContext = hubContext;
        _eventService = eventService;
    }

    /// <inheritdoc />
    public async Task<TranslationRequestDetail?> GetTranslationRequest(int id)
    {
        var request = await _dbContext.TranslationRequests.FindAsync(id);
        if (request == null)
        {
            return null;
        }

        var events = await _eventService.GetEvents(id);

        var translationRequestLines = await _dbContext.TranslationRequestLines
            .Where(translationRequestLine => translationRequestLine.TranslationRequestId == id)
            .OrderBy(translationRequestLine => translationRequestLine.Position)
            .Select(translationRequestLine => new TranslationRequestSubtitleLines
            {
                Position = translationRequestLine.Position,
                Source = translationRequestLine.Source,
                Target = translationRequestLine.Target,
                Service = translationRequestLine.Service
            })
            .ToListAsync();

        return new TranslationRequestDetail
        {
            Id = request.Id,
            JobId = request.JobId,
            MediaId = request.MediaId,
            Title = request.Title,
            SourceLanguage = request.SourceLanguage,
            TargetLanguage = request.TargetLanguage,
            SubtitleToTranslate = request.SubtitleToTranslate,
            TranslatedSubtitle = request.TranslatedSubtitle,
            IsBilingual = request.IsBilingual,
            MediaType = request.MediaType,
            Status = request.Status,
            CompletedAt = request.CompletedAt,
            ErrorMessage = request.ErrorMessage,
            StackTrace = request.StackTrace,
            Progress = request.Status == TranslationStatus.Completed ? 100 : 0,
            CreatedAt = request.CreatedAt,
            UpdatedAt = request.UpdatedAt,
            Events = events.Select(translationRequestEvent => new TranslationRequestEventDetail
            {
                Id = translationRequestEvent.Id,
                Status = translationRequestEvent.Status,
                Message = translationRequestEvent.Message,
                CreatedAt = translationRequestEvent.CreatedAt
            }).ToList(),
            Lines = translationRequestLines.Count > 0 ? translationRequestLines : []
        };
    }

    /// <inheritdoc />
    public Task<List<ActiveTranslation>> GetActiveTranslations()
    {
        return _dbContext.TranslationRequests
            .Where(translationRequest =>
                translationRequest.Status == TranslationStatus.Pending ||
                translationRequest.Status == TranslationStatus.InProgress)
            .Select(translationRequest => new ActiveTranslation
            {
                MediaId = translationRequest.MediaId,
                MediaType = translationRequest.MediaType,
                Status = translationRequest.Status
            })
            .ToListAsync();
    }

    /// <inheritdoc />
    public async Task<List<ActiveTranslation>> UpdateActiveCount()
    {
        var activeTranslations = await GetActiveTranslations();
        await _hubContext.Clients.Group("TranslationRequests")
            .SendAsync("ActiveTranslations", activeTranslations);

        return activeTranslations;
    }

    /// <inheritdoc />
    public async Task<PagedResult<TranslationRequestSummary>> GetTranslationRequests(
        string? searchQuery,
        string? orderBy,
        bool ascending,
        int pageNumber,
        int pageSize)
    {
        var query = _dbContext.TranslationRequests
            .AsSplitQuery()
            .AsQueryable();

        if (!string.IsNullOrEmpty(searchQuery))
        {
            query = query.Where(translationRequest => translationRequest.Title.ToLower().Contains(searchQuery.ToLower()));
        }

        query = orderBy switch
        {
            "Title" => ascending
                ? query.OrderBy(m => m.Title)
                : query.OrderByDescending(m => m.Title),
            "CreatedAt" => ascending
                ? query.OrderByDescending(tr => tr.CreatedAt)
                : query.OrderBy(tr => tr.CreatedAt),
            "CompletedAt" => ascending
                ? query.OrderByDescending(tr => tr.CompletedAt)
                : query.OrderBy(tr => tr.CompletedAt),
            _ => ascending
                ? query.OrderByDescending(tr => tr.CreatedAt)
                : query.OrderBy(tr => tr.CreatedAt)
        };

        var totalCount = await query.CountAsync();
        var requests = await query
            .Skip((pageNumber - 1) * pageSize)
            .Take(pageSize)
            .Select(translationRequest => new TranslationRequestSummary
            {
                Id = translationRequest.Id,
                Title = translationRequest.Title,
                SourceLanguage = translationRequest.SourceLanguage,
                TargetLanguage = translationRequest.TargetLanguage,
                TranslatedSubtitle = translationRequest.TranslatedSubtitle,
                IsBilingual = translationRequest.IsBilingual,
                MediaType = translationRequest.MediaType,
                Status = translationRequest.Status,
                CompletedAt = translationRequest.CompletedAt,
                ErrorMessage = translationRequest.ErrorMessage,
                CreatedAt = translationRequest.CreatedAt
            })
            .ToListAsync();

        return new PagedResult<TranslationRequestSummary>
        {
            Items = requests,
            TotalCount = totalCount,
            PageNumber = pageNumber,
            PageSize = pageSize
        };
    }
}
