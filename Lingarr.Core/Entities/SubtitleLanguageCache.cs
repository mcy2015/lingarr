using System.ComponentModel.DataAnnotations;

namespace Lingarr.Core.Entities;

/// <summary>
/// Persisted content-sniff result for one subtitle file (audit P1-7): the
/// in-memory cache caps at 500 entries, so a 100k-file library re-read every
/// untagged file head on each scan. Rows are keyed by path and validated
/// against (mtime, length) — an edit invalidates the entry exactly like the
/// in-memory key.
/// <para>
/// <see cref="Language"/> null is a valid "sniffed, still unknown" result and
/// is persisted like any other, so unknown files are not re-read either.
/// </para>
/// </summary>
public class SubtitleLanguageCache
{
    /// <summary>Absolute path of the subtitle file — the cache key.</summary>
    [Key]
    public required string Path { get; set; }

    /// <summary>FileInfo.LastWriteTimeUtc.Ticks when the row was written.</summary>
    public required long MtimeTicks { get; set; }

    /// <summary>File length in bytes when the row was written.</summary>
    public required long Length { get; set; }

    /// <summary>Normalized language code; null = sniffed and still unknown.</summary>
    public string? Language { get; set; }

    /// <summary>When the row was written; deliberately not BaseEntity — path-keyed upsert.</summary>
    public required DateTimeOffset UpdatedAt { get; set; }
}
