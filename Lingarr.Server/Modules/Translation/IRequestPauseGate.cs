namespace Lingarr.Server.Modules.Translation;

/// <summary>
/// Cooperative pause gate for a running translation: the translation loop calls
/// <see cref="WaitAsync"/> at batch boundaries and blocks there until the request
/// is resumed. Backed by the singleton async translation registry so the API
/// and the Hangfire job share the state.
/// </summary>
public interface IRequestPauseGate
{
    /// <summary>Blocks while the request is paused; returns immediately otherwise.</summary>
    Task WaitAsync(int requestId, CancellationToken cancellationToken);
}
