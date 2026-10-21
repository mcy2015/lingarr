namespace Lingarr.Server.Modules.SubtitleSearch.Abstractions;

/// <summary>
/// B04: a subtitle source whose availability depends on settings beyond the
/// per-source <c>subtitle_{source}_enabled</c> switch — whisper needs its
/// endpoint, model and key before it can answer anything, and an
/// unconfigured transcription source must neither appear in the providers
/// list nor burn a slot in a search round. The registry consults this gate
/// once per scope build; while it answers false the provider is omitted
/// entirely (registry.All, the orchestrator's round, the providers endpoint
/// and the connection test all follow), and the next scope after the last
/// missing setting is saved picks it up — no restart, matching the
/// credentials hot-reload posture of the other sources.
/// </summary>
public interface ISettingsGatedSubtitleProvider
{
    /// <summary>
    /// True when every setting the source needs carries a non-blank value.
    /// Implementations must never throw — a read failure reads as "not
    /// configured" (the conservative default), the same posture as the
    /// orchestrator's preference reads.
    /// </summary>
    Task<bool> IsConfiguredAsync(CancellationToken cancellationToken);
}
