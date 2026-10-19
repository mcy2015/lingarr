using System.Text;
using Lingarr.Core.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Lingarr.Server.Models.Api;
using Lingarr.Server.Modules.Translation;

namespace Lingarr.Server.Controllers;

[ApiController]
[Authorize("LingarrAccess")]
[Route("api/[controller]")]
public class GlossaryController : ControllerBase
{
    private readonly IGlossaryService _glossaryService;

    public GlossaryController(IGlossaryService glossaryService)
    {
        _glossaryService = glossaryService;
    }

    /// <summary>Full glossary, alphabetical by source term (A8).</summary>
    [HttpGet]
    public async Task<ActionResult<IEnumerable<TranslationGlossary>>> GetAll()
    {
        return Ok(await _glossaryService.GetAll());
    }

    /// <summary>Glossary entries whose source term appears in the text — proofread hints.</summary>
    [HttpGet("hits")]
    public async Task<ActionResult<IEnumerable<TranslationGlossary>>> FindHits([FromQuery] string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return Ok(Array.Empty<TranslationGlossary>());
        }

        return Ok(await _glossaryService.FindHits(text));
    }

    /// <summary>
    /// CSV glossary import (prototype screen-06 C「导入 CSV」): each row is
    /// validated; valid rows are upserted (duplicate source terms follow the
    /// existing update semantics), invalid rows and in-batch duplicates are
    /// skipped and reported in the counts.
    /// </summary>
    [HttpPost("bulk")]
    public async Task<ActionResult<GlossaryBulkResult>> BulkImport([FromBody] GlossaryBulkRequest request)
    {
        return Ok(await _glossaryService.BulkImport(request));
    }

    /// <summary>
    /// CSV glossary export (R9a) — the symmetric counterpart of the CSV
    /// import: the same source,target,mode columns the import parser
    /// accepts verbatim (mode ∈ locked/context), the same standard quote
    /// escaping, and UTF-8 with BOM so Excel opens CJK terms directly. An
    /// empty glossary still exports the header row, and re-importing the
    /// file restores the exported entries (round-trip symmetry).
    /// </summary>
    [HttpGet("export")]
    public async Task<FileResult> ExportCsv()
    {
        var entries = await _glossaryService.GetAll();

        var builder = new StringBuilder();
        builder.Append("source,target,mode\r\n");
        foreach (var entry in entries)
        {
            builder.Append(EscapeCsvField(entry.SourceTerm)).Append(',');
            builder.Append(EscapeCsvField(entry.TargetTerm)).Append(',');
            builder.Append(entry.IsLocked ? "locked" : "context");
            builder.Append("\r\n");
        }

        // Prepend the UTF-8 BOM explicitly: Excel detects the encoding and
        // opens CJK terms directly instead of mangling them.
        var preamble = Encoding.UTF8.GetPreamble();
        var body = Encoding.UTF8.GetBytes(builder.ToString());
        var payload = new byte[preamble.Length + body.Length];
        Buffer.BlockCopy(preamble, 0, payload, 0, preamble.Length);
        Buffer.BlockCopy(body, 0, payload, preamble.Length, body.Length);

        return File(payload, "text/csv", "lingarr-glossary.csv");
    }

    /// <summary>
    /// Escapes one CSV field under the same rules the import parser
    /// accepts: a field containing a comma, quote or line break is wrapped
    /// in double quotes, with inner quotes doubled.
    /// </summary>
    private static string EscapeCsvField(string field)
    {
        if (field.IndexOf(',') < 0 && field.IndexOf('"') < 0
            && field.IndexOf('\r') < 0 && field.IndexOf('\n') < 0)
        {
            return field;
        }

        return $"\"{field.Replace("\"", "\"\"")}\"";
    }

    [HttpPost]
    public async Task<ActionResult<TranslationGlossary>> Create([FromBody] TranslationGlossary entry)
    {
        var created = await _glossaryService.Create(entry);
        return CreatedAtAction(nameof(GetAll), new { id = created.Id }, created);
    }

    [HttpPut("{id}")]
    public async Task<ActionResult<TranslationGlossary>> Update(int id, [FromBody] TranslationGlossary entry)
    {
        var updated = await _glossaryService.Update(id, entry);
        return updated is null ? NotFound() : Ok(updated);
    }

    [HttpDelete("{id}")]
    public async Task<ActionResult> Delete(int id)
    {
        return await _glossaryService.Delete(id) ? Ok() : NotFound();
    }
}
