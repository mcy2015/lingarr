using Lingarr.Server.Models.Api;

namespace Lingarr.Server.Interfaces.Services;

/// <summary>
/// Import workbench: stores manually provided subtitle files (converted to UTF-8)
/// and optionally queues them for translation like any library subtitle.
/// </summary>
public interface ISubtitleImportService
{
    /// <summary>
    /// Stores the uploaded subtitle files under the imports directory, converting
    /// each to UTF-8, and optionally enqueues a translation request per file.
    /// Unsupported extensions and failures are reported per file; one bad file
    /// never blocks the rest of the batch.
    /// </summary>
    Task<List<ImportedSubtitleResult>> ImportFiles(
        List<IFormFile> files,
        string sourceLanguage,
        string targetLanguage,
        bool translate);
}
