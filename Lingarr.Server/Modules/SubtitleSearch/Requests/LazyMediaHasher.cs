using System.Collections.Concurrent;
using Lingarr.Server.Modules.SubtitleSearch.Providers;

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
    private readonly ConcurrentDictionary<CacheKey, string> _videoHashes = new();
    private readonly ConcurrentDictionary<CacheKey, string> _shooterHashes = new();

    // Coarse overflow cap for the in-memory maps — see ComputeCached.
    private const int MaxEntries = 10_000;

    public LazyMediaHasher(
        Func<string, string>? videoHashComputer = null,
        Func<string, string>? shooterHashComputer = null)
    {
        _computeVideoHash = videoHashComputer ?? SubtitleHasher.Compute;
        _computeShooterHash = shooterHashComputer ?? ShooterFileHasher.Compute;
    }

    /// <summary>
    /// The OpenSubtitles moviehash of the file at <paramref name="filePath"/>,
    /// cached on (path, mtime). Null when the file does not exist — a missing
    /// file is a gap, not an error.
    /// </summary>
    public string? VideoHash(string? filePath)
        => ComputeCached(filePath, _videoHashes, _computeVideoHash);

    /// <summary>The shooter.cn segmented filehash, cached the same way.</summary>
    public string? ShooterHash(string? filePath)
        => ComputeCached(filePath, _shooterHashes, _computeShooterHash);

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
}
