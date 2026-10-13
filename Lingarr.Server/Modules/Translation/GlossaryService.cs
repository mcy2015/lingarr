using Lingarr.Core.Data;
using Lingarr.Core.Entities;
using Lingarr.Server.Models.Api;
using Lingarr.Server.Models.FileSystem;
using Microsoft.EntityFrameworkCore;

namespace Lingarr.Server.Modules.Translation;

/// <summary>
/// Terminology store (A8). Locked entries are enforced after machine
/// translation with a deterministic per-line substitution — stronger than a
/// prompt hint for "must hold" terms and independent of provider prompt
/// formats. Advisory entries only feed the proofreading hints.
/// </summary>
public class GlossaryService : IGlossaryService
{
    private readonly LingarrDbContext _dbContext;

    public GlossaryService(LingarrDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    /// <inheritdoc />
    public async Task<List<TranslationGlossary>> GetAll() =>
        await _dbContext.TranslationGlossaries
            .OrderBy(g => g.SourceTerm)
            .AsNoTracking()
            .ToListAsync();

    /// <inheritdoc />
    public async Task<TranslationGlossary> Create(TranslationGlossary entry)
    {
        _dbContext.TranslationGlossaries.Add(entry);
        await _dbContext.SaveChangesAsync();
        return entry;
    }

    /// <inheritdoc />
    public async Task<GlossaryBulkResult> BulkImport(GlossaryBulkRequest request)
    {
        var result = new GlossaryBulkResult();

        // Case-insensitive source-term matching, consistent with FindHits.
        // TryAdd keeps the import robust against legacy case-variant rows.
        var stored = new Dictionary<string, TranslationGlossary>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in await _dbContext.TranslationGlossaries.ToListAsync())
        {
            stored.TryAdd(row.SourceTerm, row);
        }
        var seenInBatch = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in request.Entries)
        {
            var source = entry.SourceTerm?.Trim();
            var target = entry.TargetTerm?.Trim();

            // MatchMode mirrors the existing per-entry enum: locked /
            // context (case-insensitive); missing means advisory.
            bool? isLocked = entry.MatchMode?.Trim().ToLowerInvariant() switch
            {
                null or "" => false,
                "locked" => true,
                "context" => false,
                _ => null
            };

            if (string.IsNullOrEmpty(source) || string.IsNullOrEmpty(target) || isLocked is null)
            {
                result.Skipped++;
                continue;
            }

            if (!seenInBatch.Add(source))
            {
                // Duplicate source within the batch: first row wins so the
                // import stays deterministic; count the repeat as skipped.
                result.Skipped++;
                continue;
            }

            if (stored.TryGetValue(source, out var existing))
            {
                // Stored source term: existing update semantics — overwrite
                // target term and lock, keep the row identity.
                existing.TargetTerm = target;
                existing.IsLocked = isLocked.Value;
            }
            else
            {
                _dbContext.TranslationGlossaries.Add(new TranslationGlossary
                {
                    SourceTerm = source,
                    TargetTerm = target,
                    IsLocked = isLocked.Value
                });
            }

            result.Imported++;
        }

        await _dbContext.SaveChangesAsync();
        return result;
    }

    /// <inheritdoc />
    public async Task<TranslationGlossary?> Update(int id, TranslationGlossary entry)
    {
        var existing = await _dbContext.TranslationGlossaries.FindAsync(id);
        if (existing is null)
        {
            return null;
        }

        existing.SourceTerm = entry.SourceTerm;
        existing.TargetTerm = entry.TargetTerm;
        existing.Note = entry.Note;
        existing.IsLocked = entry.IsLocked;
        await _dbContext.SaveChangesAsync();
        return existing;
    }

    /// <inheritdoc />
    public async Task<bool> Delete(int id)
    {
        var existing = await _dbContext.TranslationGlossaries.FindAsync(id);
        if (existing is null)
        {
            return false;
        }

        _dbContext.TranslationGlossaries.Remove(existing);
        await _dbContext.SaveChangesAsync();
        return true;
    }

    /// <inheritdoc />
    public async Task<int> ApplyLockedTerms(List<SubtitleItem> subtitles)
    {
        var locked = await _dbContext.TranslationGlossaries
            .AsNoTracking()
            .Where(g => g.IsLocked)
            .ToListAsync();
        if (locked.Count == 0 || subtitles.Count == 0)
        {
            return 0;
        }

        var changed = 0;
        foreach (var item in subtitles)
        {
            if (item.TranslatedLines.Count == 0)
            {
                continue;
            }

            var source = string.Join(" ",
                item.PlaintextLines.Count > 0 ? item.PlaintextLines : item.Lines);
            var translation = string.Join("\n", item.TranslatedLines);

            var missing = locked
                .Where(entry =>
                    source.Contains(entry.SourceTerm, StringComparison.OrdinalIgnoreCase)
                    && !translation.Contains(entry.TargetTerm, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (missing.Count == 0)
                continue;

            // Without a reliable translation-side anchor the safe deterministic
            // form is appending the locked terms to the first translated line —
            // wholesale word swaps inside CJK text risk garbling unrelated spans.
            item.TranslatedLines[0] =
                item.TranslatedLines[0] + " · " + string.Join(" / ", missing.Select(m => m.TargetTerm));
            changed++;
        }

        return changed;
    }

    /// <inheritdoc />
    public async Task<List<TranslationGlossary>> FindHits(string text)
    {
        var all = await _dbContext.TranslationGlossaries.AsNoTracking().ToListAsync();
        return all
            .Where(g => text.Contains(g.SourceTerm, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

}
