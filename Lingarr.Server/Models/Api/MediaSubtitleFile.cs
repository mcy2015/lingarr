namespace Lingarr.Server.Models.Api;

/// <summary>
/// One external subtitle file found next to a media item. <see cref="Languages"/>
/// carries every language of a dual-language file (scjp → zh + ja).
/// </summary>
public class MediaSubtitleFile
{
    public string FileName { get; set; } = string.Empty;
    public string Directory { get; set; } = string.Empty;
    public List<string> Languages { get; set; } = [];
    /// <summary>Container without the dot: "ass", "srt"…</summary>
    public string Format { get; set; } = string.Empty;
    public long Bytes { get; set; }
}

/// <summary>
/// GET api/media/{mediaType}/{id}/subtitles payload: the external files the
/// card always listed (shape unchanged) plus the embedded tracks of the last
/// ffprobe scan. <see cref="EmbeddedScanned"/> is false until the media's
/// files have been probed (or confirmed missing) at least once.
/// </summary>
public class MediaSubtitlesResponse
{
    public List<MediaSubtitleFile> Files { get; set; } = [];
    public List<MediaEmbeddedTrack> Embedded { get; set; } = [];
    public bool EmbeddedScanned { get; set; }

    /// <summary>
    /// Normalized audio track languages of the media (PRD card A); null while
    /// unknown. Shows aggregate over their episodes: null when any episode is
    /// unknown, else the distinct union.
    /// </summary>
    public string[]? AudioLanguages { get; set; }
}

/// <summary>
/// One embedded subtitle stream inside the media container. Graphic tracks
/// (PGS, DVB…) carry <see cref="IsTextBased"/> = false — display-only, no
/// translation entry point. <see cref="MediaId"/> names the movie/episode
/// that physically owns the stream, so show-level payloads (which aggregate
/// over episodes) stay attributable for per-track actions (F2b).
/// </summary>
public class MediaEmbeddedTrack
{
    /// <summary>Id of the owning movie / episode.</summary>
    public int MediaId { get; set; }
    public int StreamIndex { get; set; }
    /// <summary>Normalized code (zh-CN → zh); null when the tag was unrecognised.</summary>
    public string? Language { get; set; }
    /// <summary>The raw container tag as muxed ("chi", "und"…); null when absent.</summary>
    public string? RawLanguage { get; set; }
    public string? Title { get; set; }
    /// <summary>Display codec: srt / ass / pgs…</summary>
    public string Codec { get; set; } = string.Empty;
    public bool IsTextBased { get; set; }
}

