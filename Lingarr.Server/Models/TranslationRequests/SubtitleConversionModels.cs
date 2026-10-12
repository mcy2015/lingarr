namespace Lingarr.Server.Models.TranslationRequests;

/// <summary>
/// Request body for converting a single translation request output
/// to monolingual or bilingual form.
/// </summary>
public class SubtitleConversionRequest
{
    public int Id { get; set; }
    public bool Bilingual { get; set; }
}

/// <summary>
/// Request body for batch-converting all completed translation outputs.
/// </summary>
public class SubtitleBatchConversionRequest
{
    public bool Bilingual { get; set; }
}

/// <summary>
/// Outcome of a single conversion attempt.
/// </summary>
public class SubtitleConversionResult
{
    public int Id { get; set; }
    public string Title { get; set; } = string.Empty;
    /// <summary>Whether the conversion was performed successfully.</summary>
    public bool Success { get; set; }
    /// <summary>True when no conversion was needed (already in the target form).</summary>
    public bool Skipped { get; set; }
    /// <summary>Whether the output is bilingual after this call.</summary>
    public bool IsBilingual { get; set; }
    public string Message { get; set; } = string.Empty;
}
