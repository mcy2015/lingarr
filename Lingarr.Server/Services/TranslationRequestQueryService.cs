using Lingarr.Core.Data;
using Lingarr.Core.Entities;
using Lingarr.Core.Enum;
using Lingarr.Server.Hubs;
using Lingarr.Server.Interfaces.Services;
using Lingarr.Server.Models;
using Lingarr.Server.Models.TranslationRequests;
using Microsoft.AspNetCore.SignalR;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore;

namespace Lingarr.Server.Services;

public class TranslationRequestQueryService : ITranslationRequestQueryService
{
    private readonly LingarrDbContext _dbContext;
    private readonly IHubContext<TranslationRequestsHub> _hubContext;
    private readonly ITranslationRequestEventService _eventService;
    private readonly ISubtitleService _subtitleService;

    public TranslationRequestQueryService(
        LingarrDbContext dbContext,
        IHubContext<TranslationRequestsHub> hubContext,
        ITranslationRequestEventService eventService,
        ISubtitleService subtitleService)
    {
        _dbContext = dbContext;
        _hubContext = hubContext;
        _eventService = eventService;
        _subtitleService = subtitleService;
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

        var detail = new TranslationRequestDetail
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

        await AttachTimecodes(detail, request);
        return detail;
    }

    /// <summary>
    /// The proofreading workbench renders a timecode gutter; the timings live
    /// only in the source subtitle file, so parse it and join by position.
    /// A missing/unreadable file just leaves the timecodes null.
    /// </summary>
    private async Task AttachTimecodes(TranslationRequestDetail detail, TranslationRequest request)
    {
        if (string.IsNullOrEmpty(request.SubtitleToTranslate) || !File.Exists(request.SubtitleToTranslate))
        {
            return;
        }

        try
        {
            var subtitles = await _subtitleService.ReadSubtitles(request.SubtitleToTranslate);
            var byPosition = subtitles.ToDictionary(s => s.Position);
            foreach (var line in detail.Lines)
            {
                if (byPosition.TryGetValue(line.Position, out var item))
                {
                    line.StartTimeMs = item.StartTime;
                    line.EndTimeMs = item.EndTime;
                }
            }
        }
        catch
        {
            // timecodes are a rendering nicety, never a failure
        }
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
        int pageSize,
        TranslationStatus? status = null)
    {
        var query = _dbContext.TranslationRequests
            .AsSplitQuery()
            .AsQueryable();

        if (!string.IsNullOrEmpty(searchQuery))
        {
            query = query.Where(translationRequest => translationRequest.Title.ToLower().Contains(searchQuery.ToLower()));
        }

        if (status is not null)
        {
            query = query.Where(translationRequest => translationRequest.Status == status);
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
                CreatedAt = translationRequest.CreatedAt,
                Source = translationRequest.Source,
                ServiceType = translationRequest.ServiceType,
                Progress = translationRequest.Progress,
                StartedAt = translationRequest.StartedAt,
                RetryCount = translationRequest.RetryCount
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

    /// <inheritdoc />
    public async Task<Dictionary<TranslationStatus, int>> GetStatusCounts()
    {
        return await _dbContext.TranslationRequests
            .GroupBy(translationRequest => translationRequest.Status)
            .Select(group => new { Status = group.Key, Count = group.Count() })
            .ToDictionaryAsync(entry => entry.Status, entry => entry.Count);
    }
}