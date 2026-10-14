namespace Lingarr.Server.Models;

/// <summary>
/// The time zone recurring jobs effectively run in: crons are interpreted
/// in it and schedule times are displayed in it. Warning is non-null when
/// the TZ environment variable is unset or invalid and the zone fell back
/// to UTC — surfaced so the schedule screen can flag it instead of only
/// the server log.
/// </summary>
public class ScheduleTimeZone
{
    public string IanaId { get; set; } = string.Empty;
    public string? Warning { get; set; }
}
