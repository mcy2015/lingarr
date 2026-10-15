using Lingarr.Core.Enum;

namespace Lingarr.Server.Modules.Media;

/// <summary>
/// Scans media containers with ffprobe for embedded subtitle tracks
/// (F2a: probe + display only — translating from embedded tracks is F2b).
/// </summary>
public interface IEmbeddedTrackService
{
    /// <summary>
    /// Probes one media item. Movies probe their own file, episodes theirs;
    /// shows/seasons fan out over their episodes. Files whose mtime matches
    /// the stored scan marker are skipped unless <paramref name="force"/> is
    /// set.
    /// </summary>
    Task<EmbeddedTrackScanSummary> ScanMedia(
        MediaType mediaType,
        int id,
        bool force = false,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Probes every movie and episode in the library, logging progress every
    /// <see cref="EmbeddedTrackService.ProgressLogInterval"/> files.
    /// </summary>
    Task<EmbeddedTrackScanSummary> ScanAll(
        bool force = false,
        CancellationToken cancellationToken = default);
}

/// <summary>Outcome counters of one scan run.</summary>
public sealed record EmbeddedTrackScanSummary(
    int Total,
    int Probed,
    int Skipped,
    int Missing,
    int Failed);
