namespace Lingarr.Server.Modules.Infrastructure;

/// <summary>
/// L2 correlation keys of the log pipeline: one ambient five-key context that
/// both log sinks (the in-memory ring and the Serilog file logger) read at
/// write time, so a "search → download → translate" chain becomes greppable
/// end to end. The keys map one-to-one onto existing database columns — no
/// new identifiers are invented here:
/// <list type="bullet">
/// <item><see cref="JobId"/> — Hangfire's background job id, the same value
/// <c>translation_requests.JobId</c> stores (written by
/// <c>Filters.JobContextFilter</c> around every job performance).</item>
/// <item><see cref="RequestId"/> — <c>translation_requests.Id</c>, set at the
/// entry of the translation jobs.</item>
/// <item><see cref="SearchSessionId"/> — <c>subtitle_search_sessions.Id</c>,
/// the session row a source round recorded (the same id
/// <c>subtitle_downloads.SearchSessionId</c> links to).</item>
/// <item><see cref="MediaKey"/> — "<c>{mediaType}/{mediaId}</c>", the shape of
/// <c>subtitle_downloads.MediaType</c> + <c>MediaId</c>.</item>
/// <item><see cref="Provider"/> — the subtitle source key, the same spelling
/// the cooldown store and telemetry use.</item>
/// </list>
/// <para>
/// Storage is a single <see cref="AsyncLocal{T}"/> holding an immutable state
/// record with copy-on-write setters (the pattern of
/// <c>Filters.JobContextFilter</c>'s two AsyncLocals, generalized). Because
/// every mutation installs a fresh record, a write performed inside a forked
/// flow — the orchestrator's parallel per-source tasks, a fire-and-forget
/// continuation — is visible only to that flow and its children; siblings and
/// the joining parent keep their own snapshot. Reading is cheap (one ambient
/// lookup) so both sinks can stamp on every entry without measurable cost.
/// </para>
/// <para>
/// Clearing discipline: every injection point must scope its writes with
/// <see cref="Push"/> (an <c>IDisposable</c> that restores the previous state)
/// or reset the key it owns in a <c>finally</c>. An uncleared AsyncLocal does
/// not corrupt other requests (it is ExecutionContext-scoped, not
/// thread-static), but it does keep stamping stale keys onto later log lines
/// of the same flow — which is exactly the misattribution this batch removes.
/// </para>
/// </summary>
public static class CorrelationContext
{
    private static readonly AsyncLocal<CorrelationState?> State = new();

    // Internal (not private) so the nested public CorrelationScope's ctor
    // (which takes it as a parameter) stays within one accessibility domain.
    internal sealed record CorrelationState(
        string? JobId = null,
        string? RequestId = null,
        int? SearchSessionId = null,
        string? MediaKey = null,
        string? Provider = null);

    /// <summary>Immutable point-in-time view of all five keys — what a logger stamps.</summary>
    public readonly record struct Snapshot(
        string? JobId,
        string? RequestId,
        int? SearchSessionId,
        string? MediaKey,
        string? Provider);

    // ── Readers ─────────────────────────────────────────────────────────

    public static string? JobId => State.Value?.JobId;

    public static string? RequestId => State.Value?.RequestId;

    public static int? SearchSessionId => State.Value?.SearchSessionId;

    public static string? MediaKey => State.Value?.MediaKey;

    public static string? Provider => State.Value?.Provider;

    /// <summary>
    /// Consistent snapshot of all five keys for log stamping. Capture once per
    /// log entry — the individual getters could straddle a concurrent
    /// mutation in the same flow.
    /// </summary>
    public static Snapshot Capture() => State.Value is { } state
        ? new Snapshot(state.JobId, state.RequestId, state.SearchSessionId, state.MediaKey, state.Provider)
        : default;

    // ── Writers ─────────────────────────────────────────────────────────

    /// <summary>Sets the Hangfire job id. Null/blank normalizes to null (no stamp).</summary>
    public static void SetJobId(string? value) => Mutate(state => state with { JobId = Normalize(value) });

    /// <summary>Sets the translation request id. Null/blank normalizes to null.</summary>
    public static void SetRequestId(string? value) => Mutate(state => state with { RequestId = Normalize(value) });

    /// <summary>Sets the deciding subtitle search session id.</summary>
    public static void SetSearchSessionId(int? value) => Mutate(state => state with { SearchSessionId = value });

    /// <summary>Sets the "{mediaType}/{mediaId}" media key. Null/blank normalizes to null.</summary>
    public static void SetMediaKey(string? value) => Mutate(state => state with { MediaKey = Normalize(value) });

    /// <summary>Sets the subtitle source key. Null/blank normalizes to null.</summary>
    public static void SetProvider(string? value) => Mutate(state => state with { Provider = Normalize(value) });

    /// <summary>Resets every key — the "no correlation" state.</summary>
    public static void Clear() => State.Value = null;

    /// <summary>
    /// Scoped write: applies the provided (non-null) keys and returns a scope
    /// whose <see cref="IDisposable.Dispose"/> restores the exact previous
    /// state — the try/finally discipline in one construct:
    /// <code>
    /// using var scope = CorrelationContext.Push(provider: "assrt");
    /// </code>
    /// Arguments left null leave the ambient value of that key untouched
    /// (pushing never clears); use the <c>Set*</c> methods to erase a key.
    /// </summary>
    public static CorrelationScope Push(
        string? jobId = null,
        string? requestId = null,
        int? searchSessionId = null,
        string? mediaKey = null,
        string? provider = null)
    {
        var previous = State.Value;
        var next = previous ?? new CorrelationState();
        if (jobId is not null)
        {
            next = next with { JobId = Normalize(jobId) };
        }

        if (requestId is not null)
        {
            next = next with { RequestId = Normalize(requestId) };
        }

        if (searchSessionId is not null)
        {
            next = next with { SearchSessionId = searchSessionId };
        }

        if (mediaKey is not null)
        {
            next = next with { MediaKey = Normalize(mediaKey) };
        }

        if (provider is not null)
        {
            next = next with { Provider = Normalize(provider) };
        }

        State.Value = next;
        return new CorrelationScope(previous);
    }

    private static void Mutate(Func<CorrelationState, CorrelationState> mutate)
        => State.Value = mutate(State.Value ?? new CorrelationState());

    private static string? Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>
    /// The restore handle of <see cref="Push"/>. Disposing sets the ambient
    /// state back to the pre-push snapshot; the restore runs exactly once so
    /// a defensive second dispose can never wipe the ambient state to null.
    /// </summary>
    public sealed class CorrelationScope : IDisposable
    {
        private CorrelationState? _previous;
        private bool _restored;

        internal CorrelationScope(CorrelationState? previous) => _previous = previous;

        public void Dispose()
        {
            if (_restored)
            {
                return;
            }

            _restored = true;
            State.Value = _previous;
        }
    }
}
