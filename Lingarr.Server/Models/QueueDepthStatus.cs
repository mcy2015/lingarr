namespace Lingarr.Server.Models;

/// <summary>
/// One Hangfire queue's live depth for the schedule screen queue strip:
/// jobs waiting to be fetched (<see cref="Enqueued"/>) plus jobs a worker
/// is currently executing (<see cref="Processing"/>).
/// </summary>
public class QueueDepthStatus
{
    /// <summary>Hangfire queue name as configured on the server (e.g. "translation").</summary>
    public string Queue { get; set; } = string.Empty;

    /// <summary>Jobs waiting in the queue; 0 when nothing is enqueued.</summary>
    public long Enqueued { get; set; }

    /// <summary>Jobs from this queue a worker is currently executing.</summary>
    public long Processing { get; set; }
}
