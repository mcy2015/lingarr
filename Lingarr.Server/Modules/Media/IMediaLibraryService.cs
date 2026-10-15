using Lingarr.Core.Entities;
using Lingarr.Core.Enum;
using Lingarr.Server.Models;
using Lingarr.Server.Models.Api;

namespace Lingarr.Server.Modules.Media;

public interface IMediaLibraryService
{
    /// <summary>
    /// Matrix rows for the library screen (A4): subtitle track coverage from
    /// the statistics scan, translation state and last activity from the
    /// request table. Coverage-dependent filters run over the full result set
    /// (DB only, no filesystem) so page totals stay correct.
    /// </summary>
    Task<PagedResult<Models.Api.MediaLibraryItem>> GetLibrary(
        MediaType? mediaType,
        string? searchQuery,
        string? state,
        string? targetLanguage,
        string? orderBy,
        bool ascending,
        int pageNumber,
        int pageSize);

    /// <summary>
    /// Single library row for the media detail page: the same shape as a
    /// <see cref="GetLibrary"/> item but built from one media graph and its
    /// newest requests only — never the full-library aggregation (scale
    /// audit P0-8). Null when no media of the requested type carries the id;
    /// a null mediaType tries shows first, then movies.
    /// </summary>
    Task<MediaLibraryItem?> GetLibraryItem(MediaType? mediaType, int id);

    /// <summary>
    /// Rough cost estimate for translating selected items into a target
    /// language: uncovered episodes' source-language subtitle bytes from the
    /// coverage scan, priced at the default provider's rate (A5).
    /// </summary>
    Task<TranslationEstimate> Estimate(List<EstimateItem> items, string targetLanguage);
}

public class EstimateItem
{
    public required MediaType MediaType { get; init; }
    public required int Id { get; init; }
}
