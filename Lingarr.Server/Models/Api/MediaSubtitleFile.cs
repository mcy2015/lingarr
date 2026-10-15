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
