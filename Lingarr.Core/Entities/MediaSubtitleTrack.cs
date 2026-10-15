using Lingarr.Core.Enum;

namespace Lingarr.Core.Entities;

/// <summary>
/// One embedded subtitle stream inside a media container (MKV/MP4…), written
/// by the ffprobe scan (EmbeddedTrackScanJob). Rows are always owned by the
/// media item that physically carries the file: movies store
/// <see cref="MediaType"/>.Movie, episodes store Episode — a show-level scan
/// fans out over its episodes.
/// <para>
/// One sentinel row per scanned file records the scan itself: it carries
/// <see cref="StreamIndex"/> = <see cref="ScanMarkerStreamIndex"/>, no track
/// data and the incremental key in <see cref="MediaHash"/>. The marker is how
/// "probed, zero tracks" is told apart from "never probed" on the detail
/// page, and how the daily scan skips files whose mtime has not changed.
/// </para>
/// </summary>
public class MediaSubtitleTrack : BaseEntity
{
    public const int ScanMarkerStreamIndex = -1;

    /// <summary>Media hash of <see cref="MediaHash"/> for a missing source file.</summary>
    public const string MissingFileHash = "missing-file";

    /// <summary>The media type that owns the container file (Movie or Episode).</summary>
    public required MediaType MediaType { get; set; }

    /// <summary>Id of the owning movie / episode (polymorphic, no FK — as translation_requests).</summary>
    public required int MediaId { get; set; }

    /// <summary>ffprobe stream index inside the container; -1 for the scan-marker row.</summary>
    public required int StreamIndex { get; set; }

    /// <summary>Normalized code via SubtitleLanguageParser (zh-CN → zh); null when unrecognised.</summary>
    public string? Language { get; set; }

    /// <summary>The raw container language tag, kept for display ("chi", "und"…).</summary>
    public string? RawLanguage { get; set; }

    /// <summary>Container track title tag, when present ("简体中文"…).</summary>
    public string? Title { get; set; }

    /// <summary>Display codec: srt / ass / pgs … (subrip→srt, hdmv_pgs_subtitle→pgs).</summary>
    public required string Codec { get; set; }

    /// <summary>False for bitmap/graphic tracks (PGS, DVB…) — not translatable.</summary>
    public required bool IsTextBased { get; set; }

    /// <summary>Container file path the stream was probed from.</summary>
    public required string FilePath { get; set; }

    /// <summary>
    /// Incremental key of the owning scan: SHA-256 of "path|mtimeTicks"
    /// (or <see cref="MissingFileHash"/>). Deliberately not Movie/Episode
    /// .MediaHash — that column is the translation pipeline's subtitle-state
    /// hash and would be overwritten by it.
    /// </summary>
    public string? MediaHash { get; set; }
}
