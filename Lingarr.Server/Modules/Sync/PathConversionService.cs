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
    /// Cache key under which the resolved media roots are memoized. Nothing
    /// invalidates this entry when libraries are synced or mappings edited
    /// (those channels have no change event wired here), so a root change —
    /// a newly synced library, a rewritten mapping destination — takes
    /// effect at most <see cref="MappingCacheSeconds"/> seconds (30s) late.
    /// That bound is accepted because computing the roots fans out over the
    /// Path columns of movies, shows and episodes (library-scale row counts)
    /// and used to run on every whitelist validation (audit QA5 P2-4).
    /// </summary>
    public static string MediaRootsCacheKey => "path-media-roots";

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
        return NormalizePathCore(path);
    }

    /// <summary>Pure form of <see cref="NormalizePath"/> usable from static contexts.</summary>
    private static string NormalizePathCore(string path)
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
    /// pathological library at "/" cannot re-open the whole disk. Roots come
    /// back in resolved canonical form (see the whitelist validator) and are
    /// memoized for a short TTL — see <see cref="MediaRootsCacheKey"/>.
    /// </summary>
    public async Task<List<string>> GetMediaRootsAsync(CancellationToken cancellationToken = default)
    {
        // QA5 P2-4: the roots used to be recomputed (three library-wide Path
        // queries) on every whitelist validation. Memoize the resolved list
        // for the same short TTL as the mapping cache; root changes apply at
        // most 30s late — see <see cref="MediaRootsCacheKey"/>.
        if (_cache is not null
            && _cache.TryGetValue(MediaRootsCacheKey, out List<string>? cached)
            && cached is not null)
        {
            return cached;
        }

        var roots = await ComputeMediaRootsAsync(cancellationToken);

        if (_cache is not null)
        {
            _cache.Set(MediaRootsCacheKey, roots, _mappingCacheDuration);
        }

        return roots;
    }

    /// <summary>Uncached roots computation; the callers see the cached form.</summary>
    private async Task<List<string>> ComputeMediaRootsAsync(CancellationToken cancellationToken)
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // One link-answer memo per computation: resolving a root now reads
        // the filesystem (one readlink per path segment), and the roots of a
        // library share most of their prefixes.
        var linkMemo = new Dictionary<string, string?>();
        var mappingRoots = await _context.PathMappings
            .Where(m => m.DestinationPath != string.Empty)
            .Select(m => m.DestinationPath)
            .ToListAsync(cancellationToken);
        foreach (var root in mappingRoots)
        {
            AddRoot(roots, root, linkMemo);
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

        // R15: roots resolve through symlinks now, which costs filesystem
        // reads. Collapse the parents lexically first — thousands of episode
        // rows share a handful of parents — so a cache miss stays
        // proportional to the library's directory breadth, not row count,
        // and the shared memo deduplicates the prefixes those parents have
        // in common ("/media", "/media/tv", …).
        var lexicalParents = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var libraryPath in moviePaths.Concat(showPaths).Concat(episodePaths))
        {
            lexicalParents.Add(Path.GetDirectoryName(NormalizeRoot(libraryPath)));
        }
        foreach (var parent in lexicalParents)
        {
            AddRoot(roots, parent, linkMemo);
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

        // P0 fix (path traversal): the old check compared the raw string
        // against the roots, so "/root/../.." passed the prefix test and the
        // OS then resolved the ".." during enumeration, escaping the
        // whitelist. Resolving via GetFullPath first folds ".", ".." and
        // duplicate separators exactly the way the filesystem would, and the
        // comparison runs on that resolved form (the roots are cached
        // resolved too). Paths GetFullPath rejects — invalid characters,
        // malformed input — are treated as outside so they never reach the
        // filesystem.
        //
        // R15 fix (symlink escape): GetFullPath is purely lexical, so a
        // symlink inside a whitelisted root but pointing elsewhere still
        // passed the prefix test. Both the candidate and the roots now
        // resolve through symbolic links as well (see
        // <see cref="ResolvePathWithLinks"/>); within one validation the
        // per-segment link answers are memoized, so the hot path costs one
        // readlink per path segment, once.
        string resolved;
        try
        {
            resolved = ResolvePathWithLinks(NormalizeRoot(path), new Dictionary<string, string?>());
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return $"Path '{path}' is not a valid path.";
        }

        if (roots.Any(root => IsSameOrUnder(resolved, root)))
        {
            return null;
        }

        var sample = string.Join(", ", roots.Take(3));
        var ellipsis = roots.Count > 3 ? ", …" : string.Empty;
        return $"Path '{path}' is outside the library media roots ({sample}{ellipsis}). Server path scanning is limited to library directories.";
    }

    private void AddRoot(HashSet<string> roots, string? root, Dictionary<string, string?> linkMemo)
    {
        // Roots are stored resolved: candidates are only ever compared in
        // resolved form (see ValidatePathInsideMediaRootsAsync), so a root
        // kept lexical would never match its own resolved candidates. Since
        // R15 "resolved" includes symbolic links — a root that is itself a
        // symlink counts as its real target, so a request spelled through
        // the link and one spelled through the real directory both match
        // the same stored root. Runs only on roots-cache misses (30s TTL).
        try
        {
            var resolved = ResolvePathWithLinks(NormalizeRoot(root), linkMemo);
            if (resolved.Length > 0)
            {
                roots.Add(resolved);
            }
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            // A stored path GetFullPath cannot resolve can never match a
            // resolvable candidate; dropping the root fails closed.
        }
    }

    /// <summary>
    /// The canonical form used for whitelist comparisons:
    /// <see cref="Path.GetFullPath"/> folds ".", ".." and duplicate
    /// separators the way the OS later would when the path is enumerated,
    /// and <see cref="NormalizeRoot"/> maps the platform-specific result
    /// (drive letters, backslashes) back to the drive-less "/"-separated
    /// shape shared by candidates and roots on every OS. Throws whatever
    /// GetFullPath throws for unusable input.
    /// </summary>
    private static string ResolveFullPath(string path)
    {
        // NUL can never appear in a real path component; reject it here so
        // the verdict never depends on runtime-specific GetFullPath
        // validation having (or lacking) the same check.
        if (path.Contains('\0'))
        {
            throw new ArgumentException("Path contains a NUL character.", nameof(path));
        }

        return NormalizeRoot(Path.GetFullPath(path));
    }

    // ── symlink resolution (R15) ───────────────────────────────────
    // FileSystemInfo.ResolveLinkTarget only answers for the final path
    // component, so a link hiding in a *middle* segment stayed invisible to
    // the whitelist: the prefix check compared lexical strings and waved
    // "<root>/linked-season/episode.srt" through even when the link pointed
    // outside the library. Resolving every accumulated prefix from the
    // filesystem root down closes that hole on both sides of the comparison
    // — the request path and the whitelist roots themselves (a root that is
    // a link counts as its real target).

    /// <summary>
    /// Maximum symlink hops followed per path, matching the customary
    /// SYMLOOP_MAX (40). Beyond it the OS itself refuses the path (ELOOP),
    /// so the walk falls back to the lexical form instead of looping.
    /// </summary>
    private const int MaxLinkHops = 40;

    /// <summary>
    /// <see cref="ResolveFullPath"/> plus symbolic-link resolution: every
    /// accumulated path prefix from the root down is checked for being a
    /// link and replaced by its target, so the result names the location the
    /// OS would actually open. Symlink resolution and ".." folding must
    /// interleave — "<root>/link/../x" with link→"/a/b" is "/a/x", not
    /// "<root>/x" — which is why the segments are walked with a stack
    /// instead of pre-folding ".." lexically. Each followed link re-seeds
    /// the target's segments through the same loop — absolute targets
    /// replace the whole accumulated path, relative targets are anchored at
    /// the link's directory first — so chains and ".." inside target
    /// strings keep OS semantics at one hop per link. When resolution
    /// cannot complete — missing segments, permission errors, exhausted
    /// hops — the walk keeps the segment it has, so a path that does not
    /// exist gets exactly the pre-R15 lexical verdict (no extra grants, no
    /// extra rejections). TODO(R15): Windows mount points/junctions beyond
    /// what ResolveLinkTarget covers are untested; deployment is Linux.
    /// </summary>
    /// <param name="path">
    /// Normalized ("/"-separated) path; may be relative, which anchors to
    /// the current directory like GetFullPath always did.
    /// </param>
    /// <param name="linkMemo">
    /// Per-request memo of accumulated prefix → link target (null = not a
    /// link), so shared prefixes cost one readlink per batch, not per path.
    /// </param>
    private static string ResolvePathWithLinks(string path, Dictionary<string, string?> linkMemo)
    {
        // Lexical form first: it anchors relative inputs (as before) and is
        // the fallback when link resolution bails out (missing paths keep
        // their old verdicts).
        var lexical = ResolveFullPath(path);
        if (lexical.Length == 0)
        {
            return lexical; // "/" and "" are never usable entries here
        }

        var pending = new Stack<string>();
        var source = path.StartsWith('/') ? path : lexical;
        foreach (var segment in source.Split('/', StringSplitOptions.RemoveEmptyEntries).Reverse())
        {
            pending.Push(segment);
        }

        var resolvedSegments = new List<string>();
        var hops = 0;
        while (pending.Count > 0)
        {
            var segment = pending.Pop();
            if (segment == ".")
            {
                continue;
            }
            if (segment == "..")
            {
                if (resolvedSegments.Count > 0)
                {
                    resolvedSegments.RemoveAt(resolvedSegments.Count - 1);
                }
                continue;
            }

            resolvedSegments.Add(segment);
            var candidate = "/" + string.Join("/", resolvedSegments);
            var target = TryGetLinkTarget(candidate, linkMemo);
            if (target is null)
            {
                continue;
            }

            if (++hops > MaxLinkHops)
            {
                // ELOOP territory: the OS cannot open this path either; keep
                // the verdict the lexical form would have produced.
                return lexical;
            }

            // Follow the hop by re-seeding the target's segments through
            // the same loop, which keeps ".." in later segments interleaved
            // with resolution exactly like the OS does it. An absolute
            // target ("" is the normalized filesystem root "/") replaces
            // the whole accumulated path — the OS resolves a link to "/x"
            // as "/x", never "<dir>/x" — while a relative target is
            // anchored at the link's directory.
            resolvedSegments.RemoveAt(resolvedSegments.Count - 1);
            if (target.Length == 0 || target[0] == '/')
            {
                resolvedSegments.Clear();
            }
            else
            {
                target = "/" + string.Join("/", resolvedSegments) + "/" + target;
            }

            if (target.Length > 0)
            {
                foreach (var targetSegment in target.Split('/', StringSplitOptions.RemoveEmptyEntries).Reverse())
                {
                    pending.Push(targetSegment);
                }
            }
        }

        return NormalizeRoot("/" + string.Join("/", resolvedSegments));
    }

    /// <summary>
    /// Single-hop link target of an accumulated prefix, in canonical "/"
    /// form, or null when the prefix is not a link or cannot be inspected
    /// (missing prefix, permission error) — the caller then keeps the
    /// segment as-is, matching the behaviour for non-existent paths. The
    /// final-target-chasing overload is deliberately not used: hop-by-hop
    /// re-seeding keeps ".." folding under the walk's control and bounds
    /// runaway chains itself.
    /// </summary>
    private static string? TryGetLinkTarget(string candidate, Dictionary<string, string?> linkMemo)
    {
        if (linkMemo.TryGetValue(candidate, out var memoized))
        {
            return memoized;
        }

        string? target = null;
        try
        {
            // DirectoryInfo works for directory and file links alike (the
            // underlying check is a readlink / reparse-point open, both
            // type-agnostic).
            var resolved = new DirectoryInfo(candidate).ResolveLinkTarget(returnFinalTarget: false);
            target = resolved is null ? null : NormalizeRoot(resolved.FullName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            target = null;
        }

        linkMemo[candidate] = target;
        return target;
    }

    /// <summary>NormalizePath + trailing-slash trim; "/" and "" yield "" (not a usable root).</summary>
    private static string NormalizeRoot(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        return NormalizePathCore(path).TrimEnd('/');
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