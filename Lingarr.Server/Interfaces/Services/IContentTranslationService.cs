using Lingarr.Core.Enum;
using Lingarr.Contracts.Translation;
using Lingarr.Contracts.Models.Batch;
using Lingarr.Server.Models.FileSystem;

namespace Lingarr.Server.Interfaces.Services;

/// <summary>
/// Job-less ("async content") translations used by external API integrations (ex. Bazarr),
/// which run inline and are tracked via the async translation registry.
/// </summary>
public interface IContentTranslationService
{
    /// <summary>
    /// Translate subtitle content without using jobs, Used for other Apps API Intergration (ex. Bazarr).
    /// </summary>
    Task<BatchSubtitleLine[]> TranslateContentAsync(
        TranslateAbleSubtitleContent translateAbleContent,
        CancellationToken parentCancellationToken);

    /// <summary>
    /// Formats the media title based on the media type and ID.
    /// Shared with request creation, which titles new translation requests the same way.
    /// </summary>
    /// <param name="mediaId">The internal media ID (Movie or Episode primary key)</param>
    /// <param name="mediaType">The type of media (Movie or Episode)</param>
    Task<string> FormatMediaTitle(int mediaId, MediaType mediaType);
}
