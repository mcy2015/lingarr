using System.ComponentModel.DataAnnotations;
using Lingarr.Core.Enum;

namespace Lingarr.Core.Entities;

/// <summary>
/// Priority-queue row per searchable media item (tech design ⑤).
/// <see cref="PriorityRank"/> is a write-time-frozen scalar so the dequeue
/// stays a single indexed ORDER BY; the UNIQUE(media_type, media_id) index
/// (ux_subtitle_search_state_media) is the webhook/manual enqueue UPSERT
/// target. The table is created by migration M0008 and written from the M3
/// automation batch — the entity ships in M1 so later batches only wire the
/// DbSet.
/// </summary>
public class SubtitleSearchState
{
    [Key]
    public int Id { get; set; }

    /// <summary>Owner media type — Movie or Episode (polymorphic, no FK).</summary>
    public required MediaType MediaType { get; set; }

    /// <summary>Id of the owning movie / episode.</summary>
    public required int MediaId { get; set; }

    /// <summary>Lifecycle of the queue entry; Pending is the only dequeued state.</summary>
    public SubtitleSearchStatus Status { get; set; } = SubtitleSearchStatus.Pending;

    /// <summary>Dequeue ordering scalar; Pinned entries always win.</summary>
    public SubtitlePriorityRank PriorityRank { get; set; } = SubtitlePriorityRank.Normal;

    /// <summary>API-set pin (POST /pin); pinned keeps rank 0 until unpinned.</summary>
    public bool Pinned { get; set; }

    /// <summary>Webhook boost window (now+48h on enqueue). Expired boosts are demoted inside the dequeue batch.</summary>
    public DateTimeOffset? WebhookBoostUntil { get; set; }

    /// <summary>Earliest next dequeue (failure backoff: 6h × 2^attempt_count, capped 7d).</summary>
    public required DateTimeOffset NextAttemptAt { get; set; }

    /// <summary>Consecutive failures; ≥8 transitions Status to Skipped.</summary>
    public int AttemptCount { get; set; }

    /// <summary>Last failure reason for the backlog card.</summary>
    public string? LastError { get; set; }

    /// <summary>When a search last ran for this item (any outcome).</summary>
    public DateTimeOffset? LastSearchedAt { get; set; }

    /// <summary>Row maintenance timestamp — the table deliberately has no created_at.</summary>
    public required DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>Lifecycle of a subtitle_search_state row (M0008 int codes).</summary>
public enum SubtitleSearchStatus
{
    /// <summary>Eligible for dequeue.</summary>
    Pending = 0,

    /// <summary>All target languages covered.</summary>
    Succeeded = 1,

    /// <summary>Last round failed; subject to backoff.</summary>
    Failed = 2,

    /// <summary>Gave up (attempt_count ≥ 8) or administratively skipped.</summary>
    Skipped = 3
}

/// <summary>Write-time-frozen dequeue priority (M0008 int codes).</summary>
public enum SubtitlePriorityRank
{
    /// <summary>API pin.</summary>
    Pinned = 0,

    /// <summary>Webhook boost, valid until WebhookBoostUntil.</summary>
    WebhookBoost = 1,

    /// <summary>Ordinary backlog.</summary>
    Normal = 2
}
