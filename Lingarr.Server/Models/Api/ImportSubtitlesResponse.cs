namespace Lingarr.Server.Models.Api;

/// <summary>Per-file outcome of an import workbench upload.</summary>
public class ImportedSubtitleResult
{
    public required string FileName { get; set; }
    public bool Success { get; set; }

    /// <summary>True when the file was stored next to its matched media video (directed import).</summary>
    public bool MediaBound { get; set; }

    /// <summary>Id of an existing translation request with identical content + target language.</summary>
    public int? DuplicateOfRequestId { get; set; }

    /// <summary>Non-fatal notice (e.g. duplicate detected - enqueue skipped).</summary>
    public string? Warning { get; set; }

    /// <summary>Saved UTF-8 path (imports directory or next to media) when the file was stored.</summary>
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
