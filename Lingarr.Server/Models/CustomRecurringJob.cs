namespace Lingarr.Server.Models;

/// <summary>
/// A user-created recurring job definition persisted in the
/// "custom_recurring_jobs" setting. JobType is restricted to the known
/// job classes — the schedule screen never registers arbitrary types.
/// </summary>
public class CustomRecurringJob
{
    /// <summary>Hangfire recurring job id chosen by the user; unique per instance.</summary>
    public string JobId { get; set; } = string.Empty;

    /// <summary>One of the registered job type names (e.g. "StatisticsJob").</summary>
    public string JobType { get; set; } = string.Empty;

    /// <summary>Cron expression (5-field standard syntax).</summary>
    public string Cron { get; set; } = string.Empty;

    /// <summary>False removes the recurring registration until re-enabled.</summary>
    public bool Enabled { get; set; } = true;
}
