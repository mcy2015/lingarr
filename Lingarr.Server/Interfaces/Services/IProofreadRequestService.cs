using Lingarr.Core.Entities;
using Lingarr.Server.Models;

namespace Lingarr.Server.Interfaces.Services;

/// <summary>
/// Proofread operations on translation requests: capability reporting, single-line
/// suggestions, full-request proofread passes and applying suggestions.
/// </summary>
public interface IProofreadRequestService
{
    /// <summary>
    /// Reports whether any configured translation service can proofread, and which one would do it.
    /// </summary>
    Task<ProofreadStatusResponse> GetProofreadStatus();

    /// <summary>
    /// Proofreads a single line against its source without persisting anything, used for the
    /// suggestion shown on the translation detail page.
    /// </summary>
    Task<string?> ProofreadLine(
        ProofreadLineRequest proofreadLineRequest,
        CancellationToken cancellationToken);

    /// <summary>
    /// Queues a proofread pass over every line of a completed translation request.
    /// </summary>
    Task<string?> ProofreadTranslationRequest(
        TranslationRequest proofreadRequest);

    /// <summary>
    /// Applies a proofread suggestion to a single line, updating the stored line and rewriting
    /// the translated subtitle file with that one line replaced.
    /// </summary>
    Task<string?> ApplyProofreadLine(
        ProofreadLineApplyRequest applyRequest);
}
