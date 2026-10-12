using Lingarr.Core.Entities;
using Lingarr.Server.Models;
using Lingarr.Server.Models.TranslationRequests;

namespace Lingarr.Server.Interfaces.Services;

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
    Task<PagedResult<TranslationRequestSummary>> GetTranslationRequests(
        string? searchQuery,
        string? orderBy,
        bool ascending,
        int pageNumber,
        int pageSize);
}
