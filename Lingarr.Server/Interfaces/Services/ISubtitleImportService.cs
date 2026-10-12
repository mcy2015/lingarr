using Lingarr.Server.Models.Api;

namespace Lingarr.Server.Interfaces.Services;

/// <summary>
/// Import workbench: stores manually provided subtitle files (converted to UTF-8)
/// and optionally queues them for translation like any library subtitle.
/// </summary>
public interface ISubtitleImportService
{
    /// <summary>
    /// Stores the uploaded subtitle files, converting each to UTF-8, and optionally
    /// enqueues a translation request per file. With a media binding (mediaType
    /// "Movie", or "Show" + season) files are stored next to the matched media's
    /// video and requests are bound to that media; unmatched files fall back to
    /// the imports directory. Unsupported extensions and failures are reported
    /// per file; one bad file never blocks the rest of the batch.
    /// </summary>
    Task<List<ImportedSubtitleResult>> ImportFiles(
        List<IFormFile> files,
        string sourceLanguage,
        string targetLanguage,
        bool translate,
        string? mediaType = null,
        int? mediaId = null,
        int? season = null,
        string? chineseConvert = null,
        bool isBilingual = false);

    /// <summary>
    /// Lists supported subtitle files under a server directory (recursive),
    /// for the import workbench's path scan (A7). Non-existent or
    /// non-directory paths throw; the result is capped to keep responses sane.
    /// </summary>
    Task<List<ScannedSubtitleFile>> ScanPath(string path);

    /// <summary>
    /// Imports subtitle files already on the server (from ScanPath) with the
    /// same storage/binding/dedup semantics as <see cref="ImportFiles"/>.
    /// </summary>
    Task<List<ImportedSubtitleResult>> ImportPaths(
        List<string> paths,
        string sourceLanguage,
        string targetLanguage,
        bool translate,
        string? mediaType = null,
        int? mediaId = null,
        int? season = null,
        string? chineseConvert = null,
        bool isBilingual = false);
}
