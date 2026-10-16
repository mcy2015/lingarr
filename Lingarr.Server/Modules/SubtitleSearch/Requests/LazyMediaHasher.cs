using System.Collections.Concurrent;
using Lingarr.Core.Data;
using Lingarr.Server.Modules.SubtitleSearch.Providers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lingarr.Server.Modules.SubtitleSearch.Requests;

/// <summary>
/// Lazily computed per-file subtitle hashes (tech design ⑨: "hash computation
/// lazy-computed — computed only when entering candidate scoring, never batch
/// pre-computed"). Both hash algorithms need real file IO over the head and
/// tail of the media file, so the results are cached in memory keyed on
/// (path, mtime) — a file edit invalidates its entry, an untouched file never
/// hashes twice. Singleton: the cache must survive the per-request scopes the
/// builder lives in.
/// <para>
/// The compute delegates are injectable so tests can count calls ("second time zero IO") and inject deterministic values without touching the disk.
/// </para>
/// <para>
/// Persistent-cache write-back (design ⑧ polish): a freshly computed
/// OpenSubtitles moviehash is also written into the owning library row's
/// dedicated <c>video_hash</c> column (M0011) through the injectable
/// <c>videoHashWriteBack</c> delegate — fire-and-forget, failures only log,
/// and never overwriting an existing value (<c>video_hash IS NULL</c> guard,
/// see <see cref="WriteBackVideoHashAsync"/>). The module wires the delegate
/// to a scoped DbContext; this class itself stays IO-pure so tests inject a
/// recorder.
/// </para>
/// </summary>
public sealed class LazyMediaHasher
{
    private readonly struct CacheKey(string path, long lastWriteUtcTicks)
    {
        public string Path { get; } = path;
        public long LastWriteUtcTicks { get; } = lastWriteUtcTicks;
    }

    private readonly Func<string, string> _computeVideoHash;
    private readonly Func<string, string> _computeShooterHash;
    private readonly Func<string, string, Task>? _videoHashWriteBack;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<CacheKey, string> _videoHashes = new();
    private readonly ConcurrentDictionary<CacheKey, string> _shooterHashes = new();

    // Coarse overflow cap for the in-memory maps — see ComputeCached.
    private const int MaxEntries = 10_000;

    public LazyMediaHasher(
        Func<string, string>? videoHashComputer = null,
        Func<string, string>? shooterHashComputer = null,
        Func<string, string, Task>? videoHashWriteBack = null,
        ILogger<LazyMediaHasher>? logger = null)
    {
        _computeVideoHash = videoHashComputer ?? SubtitleHasher.Compute;
        _computeShooterHash = shooterHashComputer ?? ShooterFileHasher.Compute;
        _videoHashWriteBack = videoHashWriteBack;
        _logger = (ILogger?)logger ?? NullLogger<LazyMediaHasher>.Instance;
    }

    /// <summary>
    /// The OpenSubtitles moviehash of the file at <paramref name="filePath"/>,
    /// cached on (path, mtime). Null when the file does not exist — a missing
    /// file is a gap, not an error. A successful value additionally triggers
    /// the fire-and-forget persistent write-back when the delegate is wired.
    /// </summary>
    public string? VideoHash(string? filePath)
    {
        var hash = ComputeCached(filePath, _videoHashes, _computeVideoHash);
        if (hash is not null && _videoHashWriteBack is not null && !string.IsNullOrWhiteSpace(filePath))
        {
            _ = WriteBackAsync(filePath, hash);
        }

        return hash;
    }

    /// <summary>The shooter.cn segmented filehash, cached the same way (no persistent write-back — the column holds the moviehash).</summary>
    public string? ShooterHash(string? filePath)
        => ComputeCached(filePath, _shooterHashes, _computeShooterHash);

    /// <summary>
    /// Fire-and-forget wrapper: the write-back must never delay or fail the
    /// search round — a failure logs at warning and is forgotten (the in-memory
    /// cache still serves the value for this process lifetime).
    /// </summary>
    private async Task WriteBackAsync(string filePath, string hash)
    {
        try
        {
            await _videoHashWriteBack!(filePath, hash);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Persisting the media hash of '{FilePath}' failed; continuing with the in-memory cache.", filePath);
        }
    }

    private static string? ComputeCached(
        string? filePath,
        ConcurrentDictionary<CacheKey, string> cache,
        Func<string, string> compute)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return null;
        }

        var key = new CacheKey(filePath, File.GetLastWriteTimeUtc(filePath).Ticks);
        var hash = cache.GetOrAdd(key, static (k, c) => c(k.Path), compute);

        // Coarse overflow guard: rounds process ≤ batch-limit files, so the
        // cap is only ever crossed by many distinct rounds; dropping the map
        // then costs one recompute per hot file. Bounded beats precise here.
        if (cache.Count > MaxEntries)
        {
            cache.Clear();
        }

        return hash;
    }

    /// <summary>Test/diagnostic hook: entries currently cached.</summary>
    public int CachedEntryCount => _videoHashes.Count + _shooterHashes.Count;

    /// <summary>
    /// Persists a computed moviehash into the owning movie/episode row's
    /// dedicated <c>video_hash</c> column (M0011) — only where the column is
    /// still NULL, never overwriting an existing value. The row is matched on
    /// the library's stored (Path, FileName) pair, which is exactly the pair
    /// the request builder combined into the hashed file path
    /// (EpisodeSync/MovieSync write-side contract). Pure LINQ ExecuteUpdate —
    /// no raw SQL, no DbContext mirror assumptions.
    /// <para>
    /// The column has exactly one writer (this write-back) — the ownership
    /// conflict that used to pit it against the translation pipeline on
    /// <c>media_hash</c> (subtitle-set hash + clear-on-change events) is what
    /// the dedicated column resolves; <c>media_hash</c> is fully returned to
    /// the translation pipeline and is never touched here. The IS NULL guard
    /// survives for one reason only: two lazy computes racing on the same
    /// file must not double-write (both compute the same value, so the guard
    /// is just idempotence insurance). There is no empty-string sentinel on
    /// this column — NULL is the only "not computed yet" state.
    /// </para>
    /// </summary>
    /// <returns>Number of rows the hash was written into (0 or 1 per table).</returns>
    public static async Task<int> WriteBackVideoHashAsync(
        LingarrDbContext dbContext,
        string filePath,
        string hash,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        if (string.IsNullOrWhiteSpace(filePath) || string.IsNullOrWhiteSpace(hash))
        {
            return 0;
        }

        var directory = Path.GetDirectoryName(filePath);
        var fileName = Path.GetFileName(filePath);
        if (string.IsNullOrEmpty(directory) || string.IsNullOrEmpty(fileName))
        {
            return 0;
        }

        var movies = await dbContext.Movies
            .Where(movie => movie.VideoHash == null && movie.Path == directory && movie.FileName == fileName)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(movie => movie.VideoHash, hash),
                cancellationToken);
        var episodes = await dbContext.Episodes
            .Where(episode => episode.VideoHash == null && episode.Path == directory && episode.FileName == fileName)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(episode => episode.VideoHash, hash),
                cancellationToken);
        return movies + episodes;
    }
}
