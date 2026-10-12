namespace Lingarr.Server.Models.Api;

/// <summary>
/// Overview pipeline strip payload (gap-audit A2): live counts for the five
/// pipeline nodes plus the KPI aggregates the overview shows next to them.
/// Monthly cost stays null until usage/price tracking lands (A5, batch B2).
/// </summary>
public class PipelineStatistics
{
    /// <summary>Episodes + movies known to the library (from the statistics scan).</summary>
    public int MediaCount { get; set; }

    /// <summary>
    /// Subtitle tracks still missing for the configured target languages,
    /// derived from the scan's per-language file counts: for every target
    /// language, media count minus subtitle files found for it, floored at 0.
    /// </summary>
    public long MissingSubtitles { get; set; }

    /// <summary>Sum of enqueued jobs across all Hangfire queues.</summary>
    public long QueueDepth { get; set; }

    /// <summary>Configured providers not failing their recent request window.</summary>
    public int ServicesOnline { get; set; }

    /// <summary>Requests with a written-back subtitle file this month.</summary>
    public int WrittenThisMonth { get; set; }

    /// <summary>Completed vs completed+failed over the last 30 days; null when no terminal outcomes.</summary>
    public double? SuccessRate30Days { get; set; }

    /// <summary>Reserved for A5 (usage × provider pricing); rendered as "—" until then.</summary>
    public double? MonthlyCost { get; set; }

    /// <summary>Hangfire worker count (MAX_CONCURRENT_JOBS), for the queue footer.</summary>
    public int ConcurrentJobs { get; set; }
}
