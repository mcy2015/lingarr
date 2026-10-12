using Lingarr.Core.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Lingarr.Server.Interfaces.Services;

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
