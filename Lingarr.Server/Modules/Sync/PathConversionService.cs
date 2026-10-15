using Lingarr.Core.Data;
using Lingarr.Core.Enum;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace Lingarr.Server.Modules.Sync;

/// <summary>Outcome of a path conversion: the rewritten path and whether any mapping applied.</summary>
public record PathConversionResult(string MappedPath, bool Matched);

public class PathConversionService
{
    /// <summary>
    /// Mapping cache TTL in seconds (audit S6: every conversion used to re-query
    /// the mapping table). PathMapping writes go through
    /// MappingController/MappingService, a different channel than
    /// SettingService/SettingChangedListener — there is no change event to hook,
    /// so a short TTL bounds staleness instead (30s: an edited mapping applies
    /// within half a minute without any invalidation wiring).
    /// </summary>
    public const int MappingCacheSeconds = 30;

    private readonly LingarrDbContext _context;
    private readonly IMemoryCache? _cache;
    private readonly TimeSpan _mappingCacheDuration;

    public PathConversionService(
        LingarrDbContext context,
        IMemoryCache? memoryCache = null,
        TimeSpan? mappingCacheDuration = null)
    {
        _context = context;
        _cache = memoryCache;
        _mappingCacheDuration = mappingCacheDuration ?? TimeSpan.FromSeconds(MappingCacheSeconds);
    }

    /// <summary>Cache key under which one media type's mappings are memoized.</summary>
    public static string MappingCacheKey(MediaType mediaType) => $"path-mappings:{mediaType}";

    /// <summary>
    /// Converts and maps a source path based on the specified media type, applying configured path mappings
    /// and normalizing directory separators.
    /// </summary>
    /// <param name="sourcePath">The original path to be converted.</param>
    /// <param name="mediaType">The type of media associated with the path.</param>
    public string ConvertAndMapPath(string sourcePath, MediaType mediaType)
    {
        if (string.IsNullOrEmpty(sourcePath))
        {
            return sourcePath;
        }

        var normalizedPath = NormalizePath(sourcePath);
        return PathReplace(normalizedPath, mediaType).MappedPath;
    }

    /// <summary>
    /// Same conversion as <see cref="ConvertAndMapPath"/> but reports whether any
    /// mapping matched — the mapping-test endpoint uses it so the verdict and the
    /// sync pipeline share one code path.
    /// </summary>
    public PathConversionResult TryConvertAndMapPath(string sourcePath, MediaType mediaType)
    {
        if (string.IsNullOrEmpty(sourcePath))
        {
            return new PathConversionResult(sourcePath, false);
        }

        var normalizedPath = NormalizePath(sourcePath);
        return PathReplace(normalizedPath, mediaType);
    }

    /// <summary>
    /// Normalizes a file system path by converting Windows-style backslashes to forward slashes
    /// and removing Windows drive letters (e.g., "C:" or "D:").
    /// </summary>
    /// <param name="path">The path to normalize. Can be either Windows or Linux style path.</param>
    public string NormalizePath(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return path;
        }

        path = path.Replace('\\', '/').TrimStart();

        if (path.Length >= 2 && path[1] == ':')
        {
            path = path.Substring(2);
        }

        return path;
    }

    /// <summary>
    /// Applies configured path mappings from the database to the source path for the specified media type.
    /// </summary>
    /// <param name="sourcePath">The original path to apply mappings to.</param>
    /// <param name="mediaType">The type of media to filter relevant path mappings.</param>
    /// <returns>The rewritten path and whether any mapping applied.</returns>
    private PathConversionResult PathReplace(string sourcePath, MediaType mediaType)
    {
        var mappings = GetMappings(mediaType);
        var matched = false;
        foreach (var (source, destination) in mappings)
        {
            if (sourcePath.Contains(source))
            {
                sourcePath = sourcePath.Replace(source, destination);
                matched = true;
            }
        }

        return new PathConversionResult(sourcePath, matched);
    }

    /// <summary>
    /// The mappings of one media type, memoized in the shared memory cache for
    /// <see cref="MappingCacheSeconds"/> seconds. The cached value is a
    /// (source, destination) projection — no entity instances, so the cache
    /// never holds rows tracked by a disposed scope. The sync pipeline converts
    /// every movie/episode path, which used to mean one mapping query per row.
    /// </summary>
    private List<(string SourcePath, string DestinationPath)> GetMappings(MediaType mediaType)
    {
        var cacheKey = MappingCacheKey(mediaType);
        if (_cache is not null
            && _cache.TryGetValue(cacheKey, out List<(string SourcePath, string DestinationPath)>? cached)
            && cached is not null)
        {
            return cached;
        }

        var mappings = _context.PathMappings
            .Where(m => m.MediaType == mediaType)
            .Select(m => new ValueTuple<string, string>(m.SourcePath, m.DestinationPath))
            .ToList();

        if (_cache is not null)
        {
            _cache.Set(cacheKey, mappings, _mappingCacheDuration);
        }

        return mappings;
    }

    // ── media roots (audit S6/P1-12) ───────────────────────────────
    // The recursive subtitle endpoints used to accept any server path.
    // They now only accept paths at or inside a library media root.

    /// <summary>
    /// The library media roots a user-supplied path may point into: the
    /// configured path-mapping destinations (the post-mapping mount points),
    /// or — when no mapping exists at all (identity mapping, same-host *arr) —
    /// the parent directories of every stored library media path, deduplicated.
    /// Computing a common ancestor of all library paths would be expensive;
    /// per-path parents are one directory level and cover every root layout
    /// (movie dirs sit under the movies root, show dirs under the shows root,
    /// season dirs under their show). Filesystem roots are never returned so a
    /// pathological library at "/" cannot re-open the whole disk.
    /// </summary>
    public async Task<List<string>> GetMediaRootsAsync(CancellationToken cancellationToken = default)
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var mappingRoots = await _context.PathMappings
            .Where(m => m.DestinationPath != string.Empty)
            .Select(m => m.DestinationPath)
            .ToListAsync(cancellationToken);
        foreach (var root in mappingRoots)
        {
            AddRoot(roots, root);
        }

        // Always union the library-derived roots with the mapping targets: a
        // partial mapping install (movies mapped, shows direct) must not get
        // its unmapped side rejected by the path whitelist.
        var moviePaths = await _context.Movies
            .Where(m => m.Path != null && m.Path != string.Empty)
            .Select(m => m.Path!)
            .ToListAsync(cancellationToken);
        var showPaths = await _context.Shows
            .Where(s => s.Path != null && s.Path != string.Empty)
            .Select(s => s.Path!)
            .ToListAsync(cancellationToken);
        var episodePaths = await _context.Episodes
            .Where(e => e.Path != null && e.Path != string.Empty)
            .Select(e => e.Path!)
            .ToListAsync(cancellationToken);

        foreach (var libraryPath in moviePaths.Concat(showPaths).Concat(episodePaths))
        {
            AddRoot(roots, Path.GetDirectoryName(NormalizeRoot(libraryPath)));
        }

        return roots.ToList();
    }

    /// <summary>
    /// Validates that a user-supplied path points at or inside a library media
    /// root (see <see cref="GetMediaRootsAsync"/>). Returns null when the path
    /// is allowed; otherwise a human-readable rejection reason for the 400
    /// response. Runs before any filesystem access so rejected paths can
    /// neither enumerate directories nor reveal that they exist.
    /// </summary>
    public async Task<string?> ValidatePathInsideMediaRootsAsync(
        string? path,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "Path is required.";
        }

        var roots = await GetMediaRootsAsync(cancellationToken);
        if (roots.Count == 0)
        {
            return "No media roots are configured. Sync a library or set up path mappings before scanning server paths.";
        }

        var normalized = NormalizeRoot(path);
        if (roots.Any(root => IsSameOrUnder(normalized, root)))
        {
            return null;
        }

        var sample = string.Join(", ", roots.Take(3));
        var ellipsis = roots.Count > 3 ? ", …" : string.Empty;
        return $"Path '{path}' is outside the library media roots ({sample}{ellipsis}). Server path scanning is limited to library directories.";
    }

    private void AddRoot(HashSet<string> roots, string? root)
    {
        var normalized = NormalizeRoot(root);
        if (normalized.Length > 0)
        {
            roots.Add(normalized);
        }
    }

    /// <summary>NormalizePath + trailing-slash trim; "/" and "" yield "" (not a usable root).</summary>
    private string NormalizeRoot(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        return NormalizePath(path).TrimEnd('/');
    }

    /// <summary>True when <paramref name="path"/> equals or lies under <paramref name="root"/>, segment-aware.</summary>
    private static bool IsSameOrUnder(string path, string root)
    {
        if (root.Length == 0 || path.Length < root.Length)
        {
            return false;
        }

        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return path.Length == root.Length || path[root.Length] == '/';
    }
}