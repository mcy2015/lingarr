using Lingarr.Server.Models.Api;

namespace Lingarr.Server.Interfaces.Services;

/// <summary>
/// Tracks per-provider request outcomes (latency / success / last error) from the
/// shared retry loop and aggregates them with configuration status from the plugin
/// registry into the overview and settings health view (gap-audit A1).
/// </summary>
public interface ITranslationHealthService
{
    /// <summary>
    /// Records the outcome of a single provider request attempt. Callers pass the
    /// canonical service type key (e.g. "openai"), not the display name.
    /// </summary>
    void Record(string serviceType, bool success, TimeSpan latency, string? error = null);

    /// <summary>
    /// Returns the rolling snapshot for a service type, or null when no traffic
    /// has been observed since startup.
    /// </summary>
    TranslationHealthSnapshot? GetSnapshot(string serviceType);

    /// <summary>
    /// Aggregates every registered provider: configuration status from required
    /// manifest fields plus the in-memory traffic snapshot when present.
    /// </summary>
    Task<List<TranslationServiceHealth>> GetServiceHealthAsync();

    /// <summary>
    /// Number of providers that are configured and not failing their recent window.
    /// Best available "online" proxy until sustained heartbeats exist.
    /// </summary>
    Task<int> GetOnlineServiceCountAsync();
}
