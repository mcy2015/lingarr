namespace Lingarr.Server.Models.Api;

/// <summary>
/// Result of an active connection probe (PM analysis P0): one real minimal
/// translation sent to the provider and measured end to end. Provider
/// failures arrive as ok=false plus a message — the endpoint never 500s.
/// </summary>
public class TranslationTestResult
{
    public bool Ok { get; init; }

    /// <summary>HTTP-like status: 200 on success, the provider status code
    /// when the failure carried one, null otherwise.</summary>
    public int? Status { get; init; }

    public double LatencyMs { get; init; }

    public required string Message { get; init; }
}

/// <summary>
/// Request body of the single-line translation preview (PM analysis P0):
/// runs one line through the named service using its saved request
/// template, exactly like a job would.
/// </summary>
public class TranslationPreviewRequest
{
    public required string ServiceType { get; init; }
    public required string Text { get; init; }
    public string? SourceLanguage { get; init; }
    public string? TargetLanguage { get; init; }
}

/// <summary>
/// One glossary term found in the preview source text, shown as a chip
/// next to the translation (prototype screen-10).
/// </summary>
public class TranslationPreviewGlossaryHit
{
    public required string SourceTerm { get; init; }
    public required string TargetTerm { get; init; }
    public bool IsLocked { get; init; }
}

/// <summary>
/// Response of the preview endpoint. Failures keep the same shape with
/// ok=false and a message so the frontend renders a structured error state.
/// </summary>
public class TranslationPreviewResponse
{
    public bool Ok { get; init; }
    public string? Translated { get; init; }
    public double LatencyMs { get; init; }

    /// <summary>Per-call cost at the provider's configured rate;
    /// null when the provider has no rate (unknown, not free).</summary>
    public double? Cost { get; init; }

    public List<TranslationPreviewGlossaryHit> GlossaryHits { get; init; } = [];

    /// <summary>Failure description when ok is false.</summary>
    public string? Message { get; init; }
}
