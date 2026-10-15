using Lingarr.Core.Data;
using Lingarr.Core.Entities;
using Lingarr.Server.Models.Api;
using Microsoft.EntityFrameworkCore;

namespace Lingarr.Server.Modules.Subtitle;

/// <summary>
/// Batch normalizer for existing external subtitles (F3). A preview never
/// writes; execution only renames items the client sends back from a
/// confirmed preview, re-validating every single one because the filesystem
/// may have changed since. Matching rules:
/// <list type="bullet">
/// <item>Movies: the subtitle's own directory must hold exactly one video
/// file — its base name becomes the new name's base.</item>
/// <item>Shows: the subtitle's base name must extend exactly one episode
/// file name in the same directory (the statistics job's heuristic).</item>
/// <item>The filename language tag must parse to exactly one language;
/// dual-language files are always skipped, never guessed.</item>
/// </list>
/// </summary>
public class SubtitleNormalizeService : ISubtitleNormalizeService
{
    private static readonly string[] SupportedSubtitleExtensions = [".srt", ".ass", ".ssa", ".vtt"];

    /// <summary>Same caption vocabulary as <see cref="SubtitleService"/>'s naming flow.</summary>
    private static readonly string[] SupportedCaptions = ["sdh", "cc", "forced", "hi"];

    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mkv", ".mp4", ".avi", ".mov", ".wmv", ".m2ts", ".ts", ".mpg", ".mpeg", ".flv", ".webm", ".iso"
    };

    /// <summary>Scan cap carried over from the import workbench's path scan.</summary>
    private const int MaxFiles = 500;

    private readonly LingarrDbContext _dbContext;
    private readonly ILogger<SubtitleNormalizeService> _logger;

    public SubtitleNormalizeService(LingarrDbContext dbContext, ILogger<SubtitleNormalizeService> logger)
    {
        _dbContext = dbContext;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<SubtitleNormalizePreviewResponse> Preview(string mediaType, int mediaId)
    {
        var isMovie = mediaType.Equals("Movie", StringComparison.OrdinalIgnoreCase);
        Movie? movie = null;
        List<Episode> episodes = [];
        List<string> scanRoots;

        if (isMovie)
        {
            movie = await _dbContext.Movies.FirstOrDefaultAsync(m => m.Id == mediaId)
                    ?? throw new KeyNotFoundException($"Movie {mediaId} was not found.");
            if (string.IsNullOrWhiteSpace(movie.Path))
            {
                throw new KeyNotFoundException($"Movie {movie.Title} has no stored directory.");
            }

            // MovieSync stores the movie's directory in Path (not the file).
            scanRoots = [movie.Path!];
        }
        else
        {
            var show = await _dbContext.Shows.FirstOrDefaultAsync(s => s.Id == mediaId)
                       ?? throw new KeyNotFoundException($"Show {mediaId} was not found.");

            episodes = await _dbContext.Episodes
                .Include(e => e.Season)
                .ThenInclude(s => s.Show)
                .Where(e => e.Season.ShowId == mediaId && e.Path != null && e.Path != string.Empty)
                .ToListAsync();

            // The show folder is the scan root when it exists on disk; a show
            // without one falls back to the distinct episode directories.
            scanRoots = !string.IsNullOrWhiteSpace(show.Path) && Directory.Exists(show.Path)
                ? [show.Path]
                : episodes
                    .Select(e => Path.GetDirectoryName(e.Path!))
                    .OfType<string>()
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
        }

        if (scanRoots.Count == 0 || scanRoots.All(root => !Directory.Exists(root)))
        {
            // Known media, missing directory: a distinct error state, not a
            // 404 — the UI explains it (reindex / path mapping) instead of
            // pretending the media is gone.
            return new SubtitleNormalizePreviewResponse
            {
                Writable = false,
                Error = "missingDirectory"
            };
        }

        var subtitleFiles = EnumerateSubtitleFiles(scanRoots);

        // One probe per scan root: any read-only root blocks the batch —
        // partial writability has no safe UI.
        var writable = scanRoots.Where(Directory.Exists).All(ProbeWritable);
        if (!writable)
        {
            return new SubtitleNormalizePreviewResponse
            {
                Writable = false,
                Error = "readOnly"
            };
        }

        var episodesByDirectory = episodes
            .Where(e => !string.IsNullOrEmpty(e.FileName))
            .GroupBy(e => Path.GetDirectoryName(e.Path!) ?? string.Empty)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        var videosByDirectory = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var items = new List<SubtitleNormalizePreviewItem>();
        var skipped = new List<SubtitleNormalizeSkippedItem>();

        // First pass: match and compute a candidate target per file.
        foreach (var file in subtitleFiles)
        {
            var fileName = Path.GetFileName(file);
            var nameWithoutExtension = Path.GetFileNameWithoutExtension(file);

            // 1. Media match — which video does this file sit next to?
            string baseName;
            string mediaTitle;
            var directory = Path.GetDirectoryName(file) ?? string.Empty;
            if (isMovie)
            {
                var videos = VideosInDirectory(directory, videosByDirectory);
                if (videos.Count == 0)
                {
                    skipped.Add(Skip(file, SubtitleNormalizeSkipReasons.NoVideoInDirectory));
                    continue;
                }

                if (videos.Count > 1)
                {
                    skipped.Add(Skip(file, SubtitleNormalizeSkipReasons.AmbiguousVideo));
                    continue;
                }

                baseName = Path.GetFileNameWithoutExtension(videos[0]);
                mediaTitle = movie!.Title;
            }
            else
            {
                if (!episodesByDirectory.TryGetValue(directory, out var candidates)
                    || candidates.Count == 0)
                {
                    skipped.Add(Skip(file, SubtitleNormalizeSkipReasons.NoEpisodeMatch));
                    continue;
                }

                var matches = candidates
                    .Where(ep => SubtitleMatchesEpisode(nameWithoutExtension, ep.FileName!))
                    .ToList();
                if (matches.Count == 0)
                {
                    skipped.Add(Skip(file, SubtitleNormalizeSkipReasons.NoEpisodeMatch));
                    continue;
                }

                if (matches.Count > 1)
                {
                    skipped.Add(Skip(file, SubtitleNormalizeSkipReasons.AmbiguousEpisodeMatch));
                    continue;
                }

                var episode = matches[0];
                baseName = Path.GetFileNameWithoutExtension(episode.FileName!);
                mediaTitle = $"{episode.Season.Show.Title} - " +
                             $"S{episode.Season.SeasonNumber:D2}E{episode.EpisodeNumber:D2} - " +
                             $"{episode.Title}";
            }

            // 2. Language: filename tags only (no content sniffing — a rename
            //    must never hinge on guessed content), exactly one language.
            if (!TryGetLanguageAndCaption(nameWithoutExtension, out var language, out var caption, out var languages))
            {
                skipped.Add(Skip(file, SubtitleNormalizeSkipReasons.LanguageUnknown));
                continue;
            }

            if (languages.Count > 1)
            {
                skipped.Add(Skip(file, SubtitleNormalizeSkipReasons.DualLanguage));
                continue;
            }

            // 3. New name: {base}.{language}[.{caption}].{ext}
            var newName = string.IsNullOrEmpty(caption)
                ? $"{baseName}.{language}{Path.GetExtension(file)}"
                : $"{baseName}.{language}.{caption}{Path.GetExtension(file)}";
            if (string.Equals(fileName, newName, StringComparison.Ordinal))
            {
                skipped.Add(Skip(file, SubtitleNormalizeSkipReasons.AlreadyNormalized));
                continue;
            }

            var newPath = Path.Combine(directory, newName);
            if (File.Exists(newPath))
            {
                skipped.Add(Skip(file, SubtitleNormalizeSkipReasons.TargetConflict));
                continue;
            }

            items.Add(new SubtitleNormalizePreviewItem
            {
                OldPath = file,
                NewPath = newPath,
                OldName = fileName,
                NewName = newName,
                Language = language,
                MediaTitle = mediaTitle
            });
        }

        // Second pass: two files resolving to one new name (e.g. a .chs and a
        // .sc subtitle of the same video) would overwrite each other — skip
        // every colliding entry rather than picking an arbitrary winner.
        var collisions = items
            .GroupBy(item => item.NewPath, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .SelectMany(group => group)
            .ToHashSet();
        if (collisions.Count > 0)
        {
            items = items.Where(item => !collisions.Contains(item)).ToList();
            skipped.AddRange(collisions
                .OrderBy(item => item.OldPath, StringComparer.OrdinalIgnoreCase)
                .Select(item => Skip(item.OldPath, SubtitleNormalizeSkipReasons.TargetCollisionInBatch)));
        }

        return new SubtitleNormalizePreviewResponse
        {
            Items = items,
            Skipped = skipped,
            Writable = true
        };
    }

    /// <inheritdoc />
    public Task<SubtitleNormalizeExecuteResponse> Execute(
        string mediaType,
        int mediaId,
        List<SubtitleNormalizeExecuteItem> confirmedItems)
    {
        if (confirmedItems.Count == 0)
        {
            return Task.FromResult(new SubtitleNormalizeExecuteResponse());
        }

        var result = new SubtitleNormalizeExecuteResponse();
        var failures = new List<SubtitleNormalizeFailedItem>();

        // Full-batch conflict check first: execute what is uniquely targeted,
        // fail the colliding entries before anything moves.
        var collidingTargets = confirmedItems
            .GroupBy(item => item.NewPath, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .SelectMany(group => group.Select(item => item.OldPath))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var item in confirmedItems)
        {
            if (collidingTargets.Contains(item.OldPath))
            {
                failures.Add(Fail(item.OldPath, "another item in this batch targets the same file name"));
                continue;
            }

            // Preview hands out full paths; a bare file name is accepted too
            // and resolves against the source directory.
            var directory = Path.GetDirectoryName(item.OldPath) ?? string.Empty;
            var newPath = Path.IsPathRooted(item.NewPath)
                ? item.NewPath
                : Path.Combine(directory, item.NewPath);
            if (!string.Equals(directory, Path.GetDirectoryName(newPath), StringComparison.Ordinal))
            {
                failures.Add(Fail(item.OldPath, "target must stay in the source directory"));
                continue;
            }

            if (!File.Exists(item.OldPath))
            {
                failures.Add(Fail(item.OldPath, "source file no longer exists"));
                continue;
            }

            if (File.Exists(item.NewPath))
            {
                failures.Add(Fail(item.OldPath, $"target '{Path.GetFileName(item.NewPath)}' already exists"));
                continue;
            }

            try
            {
                File.Move(item.OldPath, item.NewPath);
                result.Renamed++;
                _logger.LogInformation(
                    "Normalized subtitle '{OldPath}' → '{NewPath}' (language '{Language}', media '{MediaTitle}')",
                    item.OldPath, item.NewPath, item.Language, item.MediaTitle);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to normalize subtitle '{OldPath}' → '{NewPath}'",
                    item.OldPath, item.NewPath);
                failures.Add(Fail(item.OldPath, ex.Message));
            }
        }

        result.Failed = failures;
        return Task.FromResult(result);
    }

    /// <summary>
    /// Enumerates supported subtitle files under the roots (recursive),
    /// stable-sorted, capped at 500 — over the cap refuses the batch instead
    /// of truncating silently.
    /// </summary>
    private static List<string> EnumerateSubtitleFiles(IEnumerable<string> roots)
    {
        var files = roots
            .Where(Directory.Exists)
            .SelectMany(root => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            .Where(file => SupportedSubtitleExtensions.Contains(Path.GetExtension(file).ToLowerInvariant()))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(file => file, StringComparer.OrdinalIgnoreCase)
            .Take(MaxFiles + 1)
            .ToList();
        if (files.Count > MaxFiles)
        {
            throw new InvalidOperationException(
                $"The media directory holds more than {MaxFiles} subtitle files; cannot normalize safely.");
        }

        return files;
    }

    /// <summary>Creates-deletes a probe file: the only reliable read-only test across network mounts.</summary>
    private static bool ProbeWritable(string directory)
    {
        try
        {
            var probe = Path.Combine(directory, $".lingarr-normalize-{Guid.NewGuid():N}.tmp");
            File.WriteAllText(probe, string.Empty);
            File.Delete(probe);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Directory listing of video files, memoized per directory.</summary>
    private static List<string> VideosInDirectory(
        string directory,
        Dictionary<string, List<string>> cache)
    {
        if (cache.TryGetValue(directory, out var cached))
        {
            return cached;
        }

        var videos = Directory.Exists(directory)
            ? Directory.EnumerateFiles(directory)
                .Where(file => VideoExtensions.Contains(Path.GetExtension(file)))
                .ToList()
            : [];
        cache[directory] = videos;
        return videos;
    }

    /// <summary>
    /// The statistics job's subtitle-to-episode heuristic: the subtitle name
    /// equals the episode base name or extends it at a dot boundary.
    /// </summary>
    private static bool SubtitleMatchesEpisode(string subtitleFileName, string episodeFileName)
    {
        var baseName = Path.GetFileNameWithoutExtension(episodeFileName);
        return subtitleFileName.StartsWith(baseName + ".", StringComparison.OrdinalIgnoreCase)
               || string.Equals(subtitleFileName, baseName, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Reads the language and caption tags off a subtitle name using the same
    /// vocabulary as the scan/translation flow. Unlike the scan, no content
    /// sniffing ever fills a missing tag: an unnamed language stays unknown.
    /// </summary>
    private static bool TryGetLanguageAndCaption(
        string fileNameWithoutExtension,
        out string language,
        out string caption,
        out List<string> languages)
    {
        language = string.Empty;
        caption = string.Empty;
        languages = [];

        var parts = fileNameWithoutExtension.Split('.').Reverse().ToList();

        var captionPart = parts.FirstOrDefault(p => SupportedCaptions.Contains(p.ToLowerInvariant()));
        if (captionPart != null)
        {
            caption = captionPart.ToLowerInvariant();
            parts.Remove(captionPart);
        }

        var languagePart = parts.FirstOrDefault(p => SubtitleLanguageParser.TryParse(p, out _));
        if (languagePart != null && SubtitleLanguageParser.TryParse(languagePart, out var parsed))
        {
            languages = parsed.ToList();
        }
        else if (caption == "hi")
        {
            // "hi" alone is Hindi in this app's vocabulary (see SubtitleService).
            language = "hi";
            languages = ["hi"];
            caption = string.Empty;
            return true;
        }
        else
        {
            return false;
        }

        language = languages[0];
        return true;
    }

    private static SubtitleNormalizeSkippedItem Skip(string path, string reason) => new()
    {
        Path = path,
        Reason = reason
    };

    private static SubtitleNormalizeFailedItem Fail(string path, string error) => new()
    {
        Path = path,
        Error = error
    };
}
