namespace Lingarr.Server.Modules.Auth;

/// <summary>
/// Sliding-window limiter for failed logins, keyed on username + remote IP.
/// Deliberately in-memory and per-instance: Lingarr targets single-host SQLite
/// deployments, so there is no shared state to coordinate.
/// </summary>
public interface ILoginRateLimiter
{
    /// <summary>
    /// Whether the username/IP pair has already exhausted the allowed failed
    /// attempts inside the current window.
    /// </summary>
    bool IsBlocked(string username, string remoteIp);

    /// <summary>
    /// Records one failed login attempt for the username/IP pair.
    /// </summary>
    void RecordFailure(string username, string remoteIp);
}

/// <summary>
/// Keeps a bounded FIFO of failure timestamps per username + remote IP. A pair
    /// is blocked once <see cref="MaxFailures"/> failures fall inside the
    /// trailing <see cref="Window"/>; timestamps older than the window are
    /// pruned on every touch so access recovers as the window slides.
/// </summary>
public class LoginRateLimiter : ILoginRateLimiter
{
    /// <summary>Failed attempts allowed per username + IP inside the window.</summary>
    public const int MaxFailures = 5;

    /// <summary>Length of the sliding window.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    /// <summary>Trim the key dictionary once it grows beyond this size.</summary>
    private const int SweepThreshold = 1000;

    private readonly TimeProvider _timeProvider;
    private readonly object _sync = new();
    private readonly Dictionary<string, Queue<DateTimeOffset>> _failures = new();

    public LoginRateLimiter(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider;
    }

    /// <inheritdoc />
    public bool IsBlocked(string username, string remoteIp)
    {
        lock (_sync)
        {
            if (!_failures.TryGetValue(Key(username, remoteIp), out var queue))
            {
                return false;
            }

            Prune(queue, _timeProvider.GetUtcNow());
            return queue.Count >= MaxFailures;
        }
    }

    /// <inheritdoc />
    public void RecordFailure(string username, string remoteIp)
    {
        var now = _timeProvider.GetUtcNow();

        lock (_sync)
        {
            // Random usernames (enumeration attempts) would otherwise grow the
            // dictionary forever; sweep idle entries once it gets large.
            if (_failures.Count >= SweepThreshold)
            {
                Sweep(now);
            }

            if (!_failures.TryGetValue(Key(username, remoteIp), out var queue))
            {
                queue = new Queue<DateTimeOffset>();
                _failures[Key(username, remoteIp)] = queue;
            }

            Prune(queue, now);
            queue.Enqueue(now);
        }
    }

    private void Prune(Queue<DateTimeOffset> queue, DateTimeOffset now)
    {
        while (queue.Count > 0 && now - queue.Peek() >= Window)
        {
            queue.Dequeue();
        }
    }

    private void Sweep(DateTimeOffset now)
    {
        var expiredKeys = _failures
            .Where(pair =>
            {
                Prune(pair.Value, now);
                return pair.Value.Count == 0;
            })
            .Select(pair => pair.Key)
            .ToList();

        foreach (var key in expiredKeys)
        {
            _failures.Remove(key);
        }
    }

    private static string Key(string username, string remoteIp) => $"{username}|{remoteIp}";
}
