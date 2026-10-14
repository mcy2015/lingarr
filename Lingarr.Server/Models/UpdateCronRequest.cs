using System.ComponentModel.DataAnnotations;

namespace Lingarr.Server.Models;

/// <summary>
/// Body of PUT /api/schedule/job/{jobId}/cron — the single edit entry for a
/// recurring job's schedule.
/// </summary>
public class UpdateCronRequest
{
    /// <summary>5-field cron expression validated before persisting.</summary>
    [Required]
    public string Cron { get; set; } = string.Empty;
}
