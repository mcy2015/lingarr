using System.ComponentModel.DataAnnotations;

namespace Lingarr.Server.Models;

/// <summary>
/// Body of POST /api/schedule/job — creates a new recurring job from one
/// of the registered job types with a user-chosen id and cron expression.
/// </summary>
public class CreateRecurringJobRequest
{
    /// <summary>Hangfire recurring job id; must be unique, non-empty, no whitespace.</summary>
    [Required]
    public string JobId { get; set; } = string.Empty;

    /// <summary>Registered job type name; arbitrary types are rejected.</summary>
    [Required]
    public string JobType { get; set; } = string.Empty;

    /// <summary>5-field cron expression validated before registration.</summary>
    [Required]
    public string Cron { get; set; } = string.Empty;
}
