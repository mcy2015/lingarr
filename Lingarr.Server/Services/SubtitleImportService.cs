using Lingarr.Core.Enum;
using Lingarr.Server.Interfaces.Services;
using Lingarr.Server.Interfaces.Services.Subtitle;
using Lingarr.Server.Models.Api;
using Lingarr.Server.Services.Subtitle;
using Microsoft.EntityFrameworkCore;

namespace Lingarr.Server.Services;

/// <summary>
/// Import workbench storage. Files are converted to UTF-8 so the parsing/translation
/// pipeline (which assumes decodable text) treats them like any other subtitle.
/// With a media binding, each file is stored next to its matched media video and the
/// created request carries that media id — output naming then works exactly like
/// library media (the job derives it from the stored path). Unmatched files fall
/// back to config/imports, so a partially matched season pack still imports.
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
    private readonly Lingarr.Core.Data.LingarrDbContext _dbContext;
    private readonly ILogger<SubtitleImportService> _logger;

    public SubtitleImportService(
        IHostEnvironment environment,
        ISubtitleEncodingService encodingService,
        ITranslationRequestQueueService queueService,
        Lingarr.Core.Data.LingarrDbContext dbContext,
        ILogger<SubtitleImportService> logger)
    {
        _environment = environment;
        _encodingService = encodingService;
        _queueService = queueService;
        _dbContext = dbContext;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<List<ImportedSubtitleResult>> ImportFiles(
        List<IFormFile> files,
        string sourceLanguage,
        string targetLanguage,
        bool translate,
        string? mediaType = null,
        int? mediaId = null,
        int? season = null)
    {
        var importsDirectory = Path.Combine(_environment.ContentRootPath, "config", "imports");
        Directory.CreateDirectory(importsDirectory);

        var results = new List<ImportedSubtitleResult>();
        foreach (var file in files)
        {
            results.Add(await ImportOne(file, importsDirectory, sourceLanguage, targetLanguage, translate,
                mediaType, mediaId, season));
        }

        return results;
    }

    private async Task<ImportedSubtitleResult> ImportOne(
        IFormFile file,
        string importsDirectory,
        string sourceLanguage,
        string targetLanguage,
        bool translate,
        string? mediaType,
        int? mediaId,
        int? season)
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

            // Directed import: resolve the media video whose side this file belongs on.
            var mediaTarget = await ResolveMediaTarget(fileName, mediaType, mediaId, season);
            var targetDirectory = mediaTarget?.Directory ?? importsDirectory;
            result.MediaBound = mediaTarget is not null;

            Directory.CreateDirectory(targetDirectory);
            var savedPath = UniquePath(targetDirectory, SanitizeFileName(fileName));
            await File.WriteAllBytesAsync(savedPath, utf8Bytes);
            result.SavedPath = savedPath;

            if (translate)
            {
                var title = mediaTarget?.Title ?? Path.GetFileNameWithoutExtension(savedPath);
                result.TranslationRequestId = await _queueService.CreateImportRequest(
                    savedPath,
                    title,
                    sourceLanguage,
                    targetLanguage,
                    mediaTarget?.MediaId,
                    mediaTarget?.MediaType ?? MediaType.Movie);
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

    /// <summary>
    /// Resolves the directory to store a file in for a directed import.
    /// Movies: next to the movie video. Shows: next to the episode video matched
    /// by the parsed episode number within the bound season. Null when the binding
    /// cannot be resolved (unknown media, unparseable name, missing video path) —
    /// the caller then falls back to the imports directory.
    /// </summary>
    private async Task<ImportMediaTarget?> ResolveMediaTarget(
        string fileName,
        string? mediaType,
        int? mediaId,
        int? season)
    {
        if (mediaId is null || string.IsNullOrEmpty(mediaType))
        {
            return null;
        }

        if (mediaType.Equals("Movie", StringComparison.OrdinalIgnoreCase))
        {
            var movie = await _dbContext.Movies.FirstOrDefaultAsync(m => m.Id == mediaId);
            if (movie is null || string.IsNullOrWhiteSpace(movie.Path))
            {
                return null;
            }

            return new ImportMediaTarget(
                Path.GetDirectoryName(movie.Path), movie.Id, MediaType.Movie, movie.Title);
        }

        if (mediaType.Equals("Show", StringComparison.OrdinalIgnoreCase))
        {
            if (season is null)
            {
                return null;
            }

            var info = SubtitleFilenameParser.Parse(fileName);
            if (info.Episode is null)
            {
                return null;
            }

            var episode = await _dbContext.Episodes
                .Include(e => e.Season)
                .ThenInclude(s => s.Show)
                .FirstOrDefaultAsync(e =>
                    e.Season.ShowId == mediaId &&
                    e.Season.SeasonNumber == season &&
                    e.EpisodeNumber == info.Episode);

            if (episode is null || string.IsNullOrWhiteSpace(episode.Path))
            {
                return null;
            }

            var title = $"{episode.Season.Show.Title} - " +
                        $"S{episode.Season.SeasonNumber:D2}E{episode.EpisodeNumber:D2} - " +
                        $"{episode.Title}";

            return new ImportMediaTarget(
                Path.GetDirectoryName(episode.Path), episode.Id, MediaType.Episode, title);
        }

        return null;
    }

    private sealed record ImportMediaTarget(string Directory, int MediaId, MediaType MediaType, string Title);

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
