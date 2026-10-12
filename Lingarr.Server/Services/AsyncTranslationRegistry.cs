using System.Collections.Concurrent;
using Lingarr.Server.Interfaces.Services;

namespace Lingarr.Server.Services;

/// <summary>
/// Singleton registry of in-flight async content translations, replacing the former
/// static ConcurrentDictionary so both the queue and content translation services
/// share one instance without static state.
/// </summary>
public class AsyncTranslationRegistry : IAsyncTranslationRegistry, IRequestPauseGate
{
    private readonly ConcurrentDictionary<int, CancellationTokenSource> _asyncTranslationJobs = new();

    /// <inheritdoc />
    public void Register(int requestId, CancellationTokenSource cts)
    {
        _asyncTranslationJobs.TryAdd(requestId, cts);
    }

    /// <inheritdoc />
    public bool TryGet(int requestId, out CancellationTokenSource cts)
    {
        return _asyncTranslationJobs.TryGetValue(requestId, out cts!);
    }

    /// <inheritdoc />
    private readonly ConcurrentDictionary<int, SemaphoreSlim> _pausedRequests = new();

    /// <inheritdoc />
    public bool TryPause(int requestId)
    {
        // job-side WaitIfPausedAsync picks the gate up on its next batch check
        return _pausedRequests.TryAdd(requestId, new SemaphoreSlim(0, 1));
    }

    /// <inheritdoc />
    public bool TryResume(int requestId)
    {
        if (_pausedRequests.TryRemove(requestId, out var gate))
        {
            gate.Release();
            return true;
        }

        return false;
    }

    /// <inheritdoc />
    public bool IsPaused(int requestId)
    {
        return _pausedRequests.ContainsKey(requestId);
    }

    /// <inheritdoc />
    public Task WaitAsync(int requestId, CancellationToken cancellationToken)
    {
        return WaitIfPausedAsync(requestId, cancellationToken);
    }

    /// <inheritdoc />
    public async Task WaitIfPausedAsync(int requestId, CancellationToken cancellationToken)
    {
        if (_pausedRequests.TryGetValue(requestId, out var gate))
        {
            await gate.WaitAsync(cancellationToken);
        }
    }

    public bool TryRemove(int requestId, out CancellationTokenSource cts)
    {
        return _asyncTranslationJobs.TryRemove(requestId, out cts!);
    }
}
