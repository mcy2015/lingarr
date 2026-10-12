using Lingarr.Server.Interfaces.Services;
using Lingarr.Server.Interfaces.Services.Subtitle;
using Lingarr.Server.Models.Api;

namespace Lingarr.Server.Services;

/// <summary>
/// Import workbench storage. Files land under config/imports, converted to UTF-8 so
/// the parsing/translation pipeline (which assumes decodable text) treats them like
/// any other subtitle. Queuing reuses the regular request pipeline via
/// <see cref="ITranslationRequestQueueService.CreateImportRequest"/> — the job derives
/// its output path from the stored file at run time, no media binding needed.
/// </summary>
public class SubtitleImportService : ISubtitleImportService
{
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".srt", ".ass", ".ssa"
    };

    private readonly IHostEnvironment _environment;
    private readonly ISubtitleEncodingService _encodingService;
    private readonly ITranslationRequestQueueService _queueService;
    private readonly ILogger<SubtitleImportService> _logger;

    public SubtitleImportService(
        IHostEnvironment environment,
        ISubtitleEncodingService encodingService,
        ITranslationRequestQueueService queueService,
        ILogger<SubtitleImportService> logger)
    {
        _environment = environment;
        _encodingService = encodingService;
        _queueService = queueService;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<List<ImportedSubtitleResult>> ImportFiles(
        List<IFormFile> files,
        string sourceLanguage,
        string targetLanguage,
        bool translate)
    {
        var importsDirectory = Path.Combine(_environment.ContentRootPath, "config", "imports");
        Directory.CreateDirectory(importsDirectory);

        var results = new List<ImportedSubtitleResult>();
        foreach (var file in files)
        {
            results.Add(await ImportOne(file, importsDirectory, sourceLanguage, targetLanguage, translate));
        }

        return results;
    }

    private async Task<ImportedSubtitleResult> ImportOne(
        IFormFile file,
        string importsDirectory,
        string sourceLanguage,
        string targetLanguage,
        bool translate)
    {
        // Browsers send bare file names, but strip directories defensively.
        var fileName = Path.GetFileName(file.FileName);
        var result = new ImportedSubtitleResult { FileName = fileName };

        try
        {
            var extension = Path.GetExtension(fileName);
            if (!SupportedExtensions.Contains(extension))
            {
                result.Error = $"Unsupported subtitle format '{extension}'";
                return result;
            }

            await using var stream = file.OpenReadStream();
            using var buffered = new MemoryStream();
            await stream.CopyToAsync(buffered);
            var utf8Bytes = _encodingService.ConvertToUtf8(buffered.ToArray());

            var savedPath = UniquePath(importsDirectory, SanitizeFileName(fileName));
            await File.WriteAllBytesAsync(savedPath, utf8Bytes);
            result.SavedPath = savedPath;

            if (translate)
            {
                result.TranslationRequestId = await _queueService.CreateImportRequest(
                    savedPath,
                    Path.GetFileNameWithoutExtension(savedPath),
                    sourceLanguage,
                    targetLanguage);
            }

            result.Success = true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to import subtitle {FileName}", fileName);
            result.Error = ex.Message;
        }

        return result;
    }

    private static string SanitizeFileName(string fileName)
    {
        var invalidChars = Path.GetInvalidFileNameChars();
        return string.Join("_", fileName.Split(invalidChars, StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>Keeps same-named imports side by side instead of overwriting: name(1).ext, name(2).ext …</summary>
    private static string UniquePath(string directory, string fileName)
    {
        var candidate = Path.Combine(directory, fileName);
        if (!File.Exists(candidate))
        {
            return candidate;
        }

        var stem = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        for (var counter = 1; ; counter++)
        {
            candidate = Path.Combine(directory, $"{stem}({counter}){extension}");
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }
    }
}
