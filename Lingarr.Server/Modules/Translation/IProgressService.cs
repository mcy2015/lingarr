using Lingarr.Contracts.Models;
using Lingarr.Core.Entities;
using Lingarr.Server.Models;

namespace Lingarr.Server.Modules.Translation;

public interface IProgressService
{
    /// <summary>
    /// Emits a progress update for a specific job and persists it. Mid-run calls
    /// are throttled (≥2% delta or 500ms); the first emission, status changes
    /// and terminal states always go through (scale audit P0-10). Every
    /// message reaches only the request's own SignalR group.
    /// </summary>
    /// <param name="translationRequest"></param>
    /// <param name="progress">The current progress of the job (0-100).</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    Task Emit(TranslationRequest translationRequest, int progress);

    /// <summary>
    /// Buffers a translated line for batched persistence and batched
    /// broadcasting: pending lines leave as <c>LineTranslatedBatch</c> array
    /// messages to the request's group, flushed every 50 lines or 50ms
    /// (checked lazily on the next buffered line). A terminal
    /// <see cref="Emit"/> flushes both buffers.
    /// </summary>
    Task EmitLine(
        TranslationRequest request,
        int position,
        string source,
        string target,
        string? service = null,
        LanguagePair? pair = null);

    /// <summary>
    /// Buffers multiple translated lines for batched persistence and batched
    /// broadcasting (same <c>LineTranslatedBatch</c> rhythm as
    /// <see cref="EmitLine"/>).
    /// </summary>
    Task EmitLines(TranslationRequest request, List<TranslatedLineData> lines);

    /// <summary>
    /// Applies a proofread revision to the stored line for a position and broadcasts it via SignalR
    /// (single line, interactive path — never batched). The service that
    /// originally translated the line is preserved.
    /// </summary>
    Task EmitProofreadLine(TranslationRequest request, int position, string source, string target, string? service);
}