namespace Lingarr.Server.Models.Api;

/// <summary>
/// POST /api/subtitle/normalize/preview and /execute request scope: one movie
/// or one show (F3). The media id resolves the on-disk directory whose
/// subtitle files the normalize pass covers.
/// </summary>
public class SubtitleNormalizeRequest
{
    public string MediaType { get; set; } = string.Empty;
    public int MediaId { get; set; }
}

/// <summary>One planned rename shown in the preview diff.</summary>
public class SubtitleNormalizePreviewItem
{
    public string OldPath { get; set; } = string.Empty;
    public string NewPath { get; set; } = string.Empty;
    public string OldName { get; set; } = string.Empty;
    public string NewName { get; set; } = string.Empty;

    /// <summary>Canonical language code the new name carries (zh, zh-TW, en…).</summary>
    public string Language { get; set; } = string.Empty;

    /// <summary>Display title of the matched media (movie title or
    /// "Show - S01E05 - Episode").</summary>
    public string MediaTitle { get; set; } = string.Empty;
}

/// <summary>
/// A subtitle file the preview refuses to touch, with a machine-readable
/// reason the client maps onto a localized explanation.
/// </summary>
public class SubtitleNormalizeSkippedItem
{
    public string Path { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
}

/// <summary>
/// POST /api/subtitle/normalize/preview response. Pure preview — the service
/// never writes during this call. <see cref="Writable"/> is false (with
/// <see cref="Error"/> set) when the media directory is missing or rejects
/// a write probe.
/// </summary>
public class SubtitleNormalizePreviewResponse
{
    public List<SubtitleNormalizePreviewItem> Items { get; set; } = [];
    public List<SubtitleNormalizeSkippedItem> Skipped { get; set; } = [];
    public bool Writable { get; set; }
    public string? Error { get; set; }
}

/// <summary>One user-confirmed rename sent back for execution.</summary>
public class SubtitleNormalizeExecuteItem
{
    public string OldPath { get; set; } = string.Empty;
    public string NewPath { get; set; } = string.Empty;

    /// <summary>Carried from the preview purely for the audit log line.</summary>
    public string Language { get; set; } = string.Empty;

    /// <summary>Display title of the matched media, carried from the preview
    /// for the audit log line.</summary>
    public string MediaTitle { get; set; } = string.Empty;
}

public class SubtitleNormalizeExecuteRequest
{
    public string MediaType { get; set; } = string.Empty;
    public int MediaId { get; set; }
    public List<SubtitleNormalizeExecuteItem> Items { get; set; } = [];
}

/// <summary>
/// POST /api/subtitle/normalize/execute response. Renames are independent:
/// one failure never rolls back or blocks the others.
/// </summary>
public class SubtitleNormalizeExecuteResponse
{
    public int Renamed { get; set; }
    public List<SubtitleNormalizeFailedItem> Failed { get; set; } = [];
}

public class SubtitleNormalizeFailedItem
{
    public string Path { get; set; } = string.Empty;
    public string Error { get; set; } = string.Empty;
}
