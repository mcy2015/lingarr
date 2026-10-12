namespace Lingarr.Core.Entities;

/// <summary>
/// One terminology entry for translation and proofreading (gap-audit A8).
/// Locked entries are enforced deterministically after machine translation:
/// when the source line contains the source term, the target term replaces
/// whatever the provider produced for it. Unlocked entries are advisory and
/// surface as hints in the proofreading workbench.
/// </summary>
public class TranslationGlossary : BaseEntity
{
    public required string SourceTerm { get; set; }
    public required string TargetTerm { get; set; }
    public string? Note { get; set; }
    public bool IsLocked { get; set; }
}
