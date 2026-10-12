using System.Collections.Concurrent;
using Lingarr.Server.Interfaces.Services;

namespace Lingarr.Server.Services;

/// <summary>
/// Singleton registry of in-flight async content translations, replacing the former
/// static ConcurrentDictionary so both the queue and content translation services
/// share one instance without static state.
/// </summary>
public class AsyncTranslationRegistry : IAsyncTranslationRegistry
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
    public bool TryRemove(int requestId, out CancellationTokenSource cts)
    {
        return _asyncTranslationJobs.TryRemove(requestId, out cts!);
    }
}
