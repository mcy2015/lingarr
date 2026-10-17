namespace Lingarr.Server.Modules.SubtitleSearch.Abstractions;

/// <summary>
/// Verdict of the light connectivity probe behind
/// <c>POST /api/subtitle-search/test/{provider}</c> (tech design ⑥). Latency
/// is measured by the controller around the probe, so the provider only
/// reports the qualitative outcome.
/// </summary>
/// <param name="Ok">Whether the probe proved the source usable (reachable + credentials accepted when it needs any).</param>
/// <param name="Message">Human-readable result for the source card (PRD AC2: 429/timeout/invalid credentials must be distinguishable).</param>
public sealed record SubtitleProviderTestOutcome(bool Ok, string Message);

/// <summary>
/// Health probe of one subtitle source (M2): one real lightweight request —
/// a minimal search or a source-appropriate ping — never a fake ping.
/// Implemented by the v1 sources; registry entries that do not
/// implement it keep answering 501 on the test endpoint so the M1 skeleton
/// contract stays observable.
/// </summary>
public interface ISubtitleProviderHealth
{
    /// <summary>
    /// Runs one cheap real request against the source using the configured
    /// credentials. Failures must surface as
    /// <see cref="SubtitleProviderException"/> (the controller translates
    /// them into ok=false with the reason), not as thrown 500s.
    /// </summary>
    Task<SubtitleProviderTestOutcome> TestConnectionAsync(CancellationToken cancellationToken);
}
