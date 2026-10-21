using Lingarr.Core.Enum;
using Lingarr.Core.Entities;
using Lingarr.Server.Models.FileSystem;

namespace Lingarr.Server.Modules.Media;

/// <summary>
/// Scans media containers with ffprobe for embedded subtitle tracks
/// (F2a: probe + display) and extracts text tracks as translation
/// sources (F2b).
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

    /// <summary>
    /// Extracts one text-based embedded track to
    /// <c>{contentRoot}/config/imports/embedded/</c> as SRT (F2b). The file
    /// name is deterministic in (mediaType, mediaId, streamIndex), so a
    /// repeat translation of the same track reuses the existing extraction
    /// instead of running ffmpeg again. Returns the extracted file path.
    /// Throws <see cref="InvalidOperationException"/> with a readable message
    /// when the track row is missing, the track is graphic, the container
    /// file is gone or ffmpeg fails; throws
    /// <see cref="Exceptions.FFprobeUnavailableException"/> when no ffmpeg
    /// binary exists.
    /// </summary>
    Task<string> ExtractTrack(
        MediaType mediaType,
        int mediaId,
        int streamIndex,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Prepares an embedded track as a translation source for the queue:
    /// extracts it (<see cref="ExtractTrack"/>, cached) and materializes the
    /// extraction as <c>{mediaPath}/{videoBase}.{language}.srt</c> next to
    /// the video, so the existing pipeline derives the usual output name and
    /// writes the translation back into the media directory (exactly how
    /// media-bound imports behave). An existing non-empty file is reused.
    /// Returns the media-directory path to use as SubtitleToTranslate.
    /// </summary>
    Task<string> PrepareTranslationSource(
        MediaType mediaType,
        int mediaId,
        int streamIndex,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// B04 user-facing extraction: materializes one text-based embedded
    /// track as an external sidecar file (<c>{videoBase}.{language}.srt</c>
    /// next to the video — the same name shape the translation flow writes)
    /// so players that prefer external subtitles pick it up. Idempotent: a
    /// non-empty target file short-circuits BEFORE any ffmpeg run, so a
    /// repeat call neither re-extracts nor touches the file's mtime.
    /// Throws <see cref="InvalidOperationException"/> with a readable
    /// message when the track row is missing, the track is graphic, the
    /// container file is gone or ffmpeg fails.
    /// </summary>
    Task<EmbeddedTrackExtractionResult> ExtractToSidecarAsync(
        MediaType mediaType,
        int mediaId,
        int streamIndex,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Outcome of a sidecar extraction (B04): the sidecar's absolute path and
/// whether THIS call created it (false = an identical non-empty file was
/// already next to the video and was left untouched).
/// </summary>
public sealed record EmbeddedTrackExtractionResult(string Path, bool Created);

/// <summary>Outcome counters of one scan run.</summary>
public sealed record EmbeddedTrackScanSummary(
    int Total,
    int Probed,
    int Skipped,
    int Missing,
    int Failed);
