using Lingarr.Core.Configuration;
using Lingarr.Core.Data;
using Lingarr.Server.Models.Api;
using Lingarr.Server.Modules.Settings;
using Microsoft.EntityFrameworkCore;

namespace Lingarr.Server.Modules.Subtitle.PostProcessing;

/// <inheritdoc />
/// <remarks>
/// Scan roots follow the sync write-side conventions: MovieSync stores the
/// movie directory in Movie.Path; a show lands under Show.Path when it exists
/// on disk, otherwise the show's distinct episode directories (Episode.Path —
/// the same fallback the normalize service applies per show). The 500-file
/// cap mirrors the normalize/import scans: over the cap the batch is refused
/// instead of silently truncated.
/// </remarks>
public sealed class SubtitleVariantBatchService : ISubtitleVariantBatchService
{
    private const int MaxFiles = 500;
    private static readonly string[] SupportedSubtitleExtensions = [".srt", ".ass", ".ssa", ".vtt"];

    private readonly LingarrDbContext _dbContext;
    private readonly ISettingService _settingService;
    private readonly CompositePostProcessor _postProcessor;
    private readonly ILogger<SubtitleVariantBatchService> _logger;

    public SubtitleVariantBatchService(
        LingarrDbContext dbContext,
        ISettingService settingService,
        CompositePostProcessor postProcessor,
        ILogger<SubtitleVariantBatchService> logger)
    {
        _dbContext = dbContext;
        _settingService = settingService;
        _postProcessor = postProcessor;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<SubtitlePostProcessPreviewResponse> PreviewAsync(CancellationToken cancellationToken)
    {
        var mode = await ResolveModeAsync(cancellationToken);
        var files = await ScanLibraryAsync(cancellationToken);

        var wouldChange = 0;
        foreach (var file in files)
        {
            var result = await _postProcessor.ProcessAsync(
                file, new PostProcessContext(mode, PreviewOnly: true), cancellationToken);
            wouldChange += result.Changed ? 1 : 0;
        }

        return new SubtitlePostProcessPreviewResponse
        {
            Mode = mode,
            Scanned = files.Count,
            WouldChange = wouldChange
        };
    }

    /// <inheritdoc />
    public async Task<SubtitlePostProcessExecuteResponse> ExecuteAsync(CancellationToken cancellationToken)
    {
        var mode = await ResolveModeAsync(cancellationToken);
        var files = await ScanLibraryAsync(cancellationToken);

        var result = new SubtitlePostProcessExecuteResponse
        {
            Mode = mode,
            Scanned = files.Count
        };

        foreach (var file in files)
        {
            try
            {
                var outcome = await _postProcessor.ProcessAsync(
                    file, new PostProcessContext(mode), cancellationToken);
                if (outcome.Error is not null)
                {
                    result.Failed.Add(new SubtitlePostProcessFailedItem
                    {
                        Path = file,
                        Error = outcome.Error
                    });
                }
                else if (outcome.Changed)
                {
                    result.Converted++;
                    _logger.LogInformation(
                        "Converted variant of '{FilePath}' to {Mode} (existing-library batch).",
                        file, mode);
                }
                else
                {
                    result.Skipped++;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // One broken file never sinks the batch.
                _logger.LogWarning(ex, "Variant batch could not process '{FilePath}'.", file);
                result.Failed.Add(new SubtitlePostProcessFailedItem
                {
                    Path = file,
                    Error = ex.Message
                });
            }
        }

        return result;
    }

    /// <summary>
    /// Reads the variant setting; "none"/unset (or an unknown value) refuses
    /// the batch — converting stored text must always be an explicit choice.
    /// </summary>
    private async Task<string> ResolveModeAsync(CancellationToken cancellationToken)
    {
        var value = (await _settingService.GetSetting(SettingKeys.SubtitleSearch.PostProcessChineseVariant))
            ?.Trim().ToLowerInvariant();
        if (value is "simplified" or "traditional")
        {
            return value;
        }

        throw new InvalidOperationException(
            $"The Chinese variant mode is '{value ?? "none"}' — pick simplified or traditional before running the batch.");
    }

    /// <summary>
    /// Every supported subtitle file under the library roots, stable-sorted,
    /// capped at 500 — over the cap the batch refuses rather than truncate.
    /// </summary>
    private async Task<List<string>> ScanLibraryAsync(CancellationToken cancellationToken)
    {
        var roots = new List<string>();
        roots.AddRange(await _dbContext.Movies
            .Where(movie => movie.Path != null && movie.Path != string.Empty)
            .Select(movie => movie.Path!)
            .ToListAsync(cancellationToken));

        var shows = await _dbContext.Shows
            .Where(show => show.Path != null && show.Path != string.Empty)
            .Select(show => new { show.Id, show.Path })
            .ToListAsync(cancellationToken);
        var showsWithPath = shows.ToDictionary(
            show => show.Id, show => show.Path!, StringComparer.OrdinalIgnoreCase);
        roots.AddRange(showsWithPath.Values);

        // Shows without a stored folder fall back to their distinct episode
        // directories (Episode.Path holds the directory — sync write side).
        var episodePaths = await _dbContext.Episodes
            .Where(episode => episode.Path != null && episode.Path != string.Empty)
            .Select(episode => new { ShowId = episode.Season.ShowId, Path = episode.Path })
            .ToListAsync(cancellationToken);
        foreach (var row in episodePaths)
        {
            // A show with its own root is scanned whole — only shows WITHOUT
            // one contribute their distinct episode directories.
            if (showsWithPath.ContainsKey(row.ShowId))
            {
                continue;
            }

            roots.Add(row.Path!);
        }

        var files = roots
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(Directory.Exists)
            .SelectMany(root => EnumerateFilesResiliently(root))
            .Where(file => SupportedSubtitleExtensions.Contains(Path.GetExtension(file).ToLowerInvariant()))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(file => file, StringComparer.OrdinalIgnoreCase)
            .Take(MaxFiles + 1)
            .ToList();

        if (files.Count > MaxFiles)
        {
            throw new InvalidOperationException(
                $"The library holds more than {MaxFiles} subtitle files; the batch cap is {MaxFiles}.");
        }

        return files;
    }

    /// <summary>
    /// One directory tree walk per root that isolates unreadable directories
    /// (SubtitleService's resilient traversal): a NAS folder that cannot be
    /// listed is logged and skipped, the rest of the library still scans.
    /// </summary>
    private IEnumerable<string> EnumerateFilesResiliently(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            string[] files;
            try
            {
                files = Directory.GetFiles(directory);
                foreach (var subdirectory in Directory.EnumerateDirectories(directory))
                {
                    pending.Push(subdirectory);
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException
                or IOException
                or DirectoryNotFoundException)
            {
                _logger.LogWarning(ex,
                    "Skipping unreadable directory {Directory} while scanning subtitles for the variant batch.",
                    directory);
                continue;
            }

            foreach (var file in files)
            {
                yield return file;
            }
        }
    }
}
