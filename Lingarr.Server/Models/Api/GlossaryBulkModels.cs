namespace Lingarr.Server.Models.Api;

/// <summary>
/// One row of a glossary CSV import (prototype screen-06 C「导入 CSV」).
/// MatchMode mirrors the existing per-entry enum semantics:
/// "locked" → locked term (enforced after machine translation),
/// "context" → advisory term (proofreading hint). Null defaults to
/// "context"; any other value makes the row invalid and it is skipped.
/// </summary>
public class GlossaryBulkEntry
{
    public string? SourceTerm { get; set; }
    public string? TargetTerm { get; set; }
    public string? MatchMode { get; set; }
}

public class GlossaryBulkRequest
{
    public List<GlossaryBulkEntry> Entries { get; set; } = [];
}

/// <summary>
/// Outcome of a bulk import: rows created or updated (duplicate source
/// terms follow the existing update semantics and overwrite the stored
/// target/lock), and rows skipped as invalid or as in-batch duplicates.
/// </summary>
public class GlossaryBulkResult
{
    public int Imported { get; set; }
    public int Skipped { get; set; }
}
