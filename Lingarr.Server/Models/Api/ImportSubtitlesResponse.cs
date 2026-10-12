namespace Lingarr.Server.Models.Api;

/// <summary>Per-file outcome of an import workbench upload.</summary>
public class ImportedSubtitleResult
{
    public required string FileName { get; set; }
    public bool Success { get; set; }

    /// <summary>Saved UTF-8 path inside the imports directory (when the file was stored).</summary>
    public string? SavedPath { get; set; }

    /// <summary>Created translation request id (when the file was queued for translation).</summary>
    public int? TranslationRequestId { get; set; }

    public string? Error { get; set; }
}

/// <summary>Response wrapper for the import endpoint.</summary>
public class ImportSubtitlesResponse
{
    public List<ImportedSubtitleResult> Results { get; set; } = [];
}
