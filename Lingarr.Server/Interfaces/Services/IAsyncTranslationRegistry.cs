namespace Lingarr.Server.Interfaces.Services;

/// <summary>
/// Tracks in-flight async (job-less) content translations by their translation request id,
/// so they can be cancelled while running. Replaces the previous static dictionary that was
/// shared across translation request services; registered as a singleton to keep that behavior.
/// </summary>
public interface IAsyncTranslationRegistry : IRequestPauseGate
{
    /// <summary>
    /// Registers the cancellation source for a running async translation.
    /// </summary>
    void Register(int requestId, CancellationTokenSource cts);

    /// <summary>
    /// Attempts to retrieve the cancellation source for a running async translation.
    /// </summary>
    bool TryGet(int requestId, out CancellationTokenSource cts);

    /// <summary>
    /// Attempts to remove the cancellation source once the async translation finished.
    /// </summary>
    bool TryRemove(int requestId, out CancellationTokenSource cts);

    /// <summary>
    /// Marks a running translation as paused; the next batch-loop check will block.
    /// </summary>
    bool TryPause(int requestId);

    /// <summary>
    /// Releases a paused translation so the running job continues.
    /// </summary>
    bool TryResume(int requestId);

    /// <summary>Whether the request currently has an active pause gate.</summary>
    bool IsPaused(int requestId);

}
