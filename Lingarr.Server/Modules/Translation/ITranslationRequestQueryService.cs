using System.Collections.Generic;
using Lingarr.Core.Entities;
using Lingarr.Core.Enum;
using Lingarr.Server.Models;
using Lingarr.Server.Models.TranslationRequests;

namespace Lingarr.Server.Modules.Translation;

/// <summary>
/// Read-oriented queries over translation requests plus the SignalR active-count broadcast.
/// </summary>
public interface ITranslationRequestQueryService
{
    /// <summary>
    /// Retrieves a single translation request with its event timeline.
    /// </summary>
    Task<TranslationRequestDetail?> GetTranslationRequest(int id);

    /// <summary>
    /// Retrieves the collection of currently active (Pending or InProgress) translation requests.
    /// </summary>
    Task<List<ActiveTranslation>> GetActiveTranslations();

    /// <summary>
    /// Broadcasts the current active translation requests over SignalR.
    /// </summary>
    Task<List<ActiveTranslation>> UpdateActiveCount();

    /// <summary>
    /// Retrieves a paginated list of translation requests with optional filtering and sorting.
    /// Returns list-row summaries; entity internals are not exposed.
    /// </summary>
    /// <param name="mediaIds">
    /// Optional comma-separated media ids to filter by (legacy per-media filter,
    /// still used by movie detail histories).
    /// </param>
    /// <param name="mediaParentId">
    /// Optional parent show id: when set, only requests whose media is an
    /// episode of that show are returned (SQL pushdown over episode→season→show,
    /// scale audit P1-6 — replaces thousand-id URL lists).
    /// </param>
    Task<PagedResult<TranslationRequestSummary>> GetTranslationRequests(
        string? searchQuery,
        string? orderBy,
        bool ascending,
        int pageNumber,
        int pageSize,
        TranslationStatus? status = null,
        string? mediaIds = null,
        int? mediaParentId = null);

    /// <summary>
    /// Request count per translation status, for the queue filter tabs.
    /// </summary>
    Task<Dictionary<TranslationStatus, int>> GetStatusCounts();
}
