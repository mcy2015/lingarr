namespace Lingarr.Server.Models.Api;

/// <summary>
/// POST /api/subtitle/post-process/preview response: how many library
/// subtitle files the configured variant mode would rewrite. Pure count —
/// the preview itself never writes.
/// </summary>
public class SubtitlePostProcessPreviewResponse
{
    /// <summary>Effective variant mode ("simplified"/"traditional").</summary>
    public string Mode { get; set; } = string.Empty;

    /// <summary>Subtitle files the scan examined (library-wide, cap 500).</summary>
    public int Scanned { get; set; }

    /// <summary>Files whose content would actually change (idempotent skip excluded).</summary>
    public int WouldChange { get; set; }
}

/// <summary>One file the batch execution could not process.</summary>
public class SubtitlePostProcessFailedItem
{
    public string Path { get; set; } = string.Empty;
    public string Error { get; set; } = string.Empty;
}

/// <summary>
/// POST /api/subtitle/post-process/execute response. Conversions are
/// independent: one failure never rolls back or blocks the others.
/// </summary>
public class SubtitlePostProcessExecuteResponse
{
    /// <summary>Effective variant mode the batch ran with.</summary>
    public string Mode { get; set; } = string.Empty;

    /// <summary>Subtitle files the scan examined.</summary>
    public int Scanned { get; set; }

    /// <summary>Files rewritten (their parsed lines actually changed).</summary>
    public int Converted { get; set; }

    /// <summary>Files left untouched — already in the target variant / no Chinese text.</summary>
    public int Skipped { get; set; }

    public List<SubtitlePostProcessFailedItem> Failed { get; set; } = [];
}
