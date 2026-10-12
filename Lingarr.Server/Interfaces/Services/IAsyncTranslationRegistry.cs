namespace Lingarr.Server.Interfaces.Services;

/// <summary>
/// Tracks in-flight async (job-less) content translations by their translation request id,
/// so they can be cancelled while running. Replaces the previous static dictionary that was
/// shared across translation request services; registered as a singleton to keep that behavior.
/// </summary>
public interface IAsyncTranslationRegistry
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
}
