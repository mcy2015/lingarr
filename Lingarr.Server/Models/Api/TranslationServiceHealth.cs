namespace Lingarr.Server.Models.Api;

/// <summary>
/// Rolling traffic snapshot for one provider, computed over the most recent
/// recorded attempts. Null latency means no successful attempt is in the window.
/// </summary>
public class TranslationHealthSnapshot
{
    public required string ServiceType { get; init; }
    public double? AverageLatencyMs { get; init; }
    public double SuccessRate { get; init; }
    public string? LastError { get; init; }
    public DateTimeOffset? LastRequestAt { get; init; }
    public int SampleCount { get; init; }
}

/// <summary>
/// Health row for the overview service-health card and the settings provider
/// cards. Latency/success are process-local rolling windows (since restart);
/// MonthlyCost is null until a rate is configured and MonthlyRequests counts
/// the provider's completed requests of the current month (0 when unused).
/// </summary>
public class TranslationServiceHealth
{
    public required string Provider { get; init; }
    public required string DisplayName { get; init; }
    public bool Configured { get; init; }
    public double? LatencyMs { get; init; }
    public double? SuccessRate { get; init; }
    public string? LastError { get; init; }
    public DateTimeOffset? LastRequestAt { get; init; }
    public int SampleCount { get; init; }
    public double? MonthlyCost { get; init; }

    /// <summary>
    /// Completed translation requests attributed to this provider in the
    /// current month — the same serviceType/month criteria the cost rollup
    /// prices (walkthrough fix B3: the card's number is real, not decorative).
    /// </summary>
    public int MonthlyRequests { get; init; }
}
