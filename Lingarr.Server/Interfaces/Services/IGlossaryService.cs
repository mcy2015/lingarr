using Lingarr.Core.Entities;
using Lingarr.Server.Models.FileSystem;

namespace Lingarr.Server.Interfaces.Services;

public interface IGlossaryService
{
    Task<List<TranslationGlossary>> GetAll();
    Task<TranslationGlossary> Create(TranslationGlossary entry);
    Task<TranslationGlossary?> Update(int id, TranslationGlossary entry);
    Task<bool> Delete(int id);

    /// <summary>
    /// Enforces locked glossary entries on translated subtitles: when an
    /// item's source text contains a locked source term but none of its
    /// translated lines carry the expected target term, the term is appended
    /// to the first translated line. Returns the number of touched items.
    /// </summary>
    Task<int> ApplyLockedTerms(List<SubtitleItem> subtitles);

    /// <summary>Glossary entries whose source term appears in the text (proofread hints).</summary>
    Task<List<TranslationGlossary>> FindHits(string text);
}
