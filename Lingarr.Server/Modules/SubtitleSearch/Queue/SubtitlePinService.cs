using Lingarr.Core.Data;
using Lingarr.Core.Entities;
using Lingarr.Core.Enum;
using Microsoft.EntityFrameworkCore;

namespace Lingarr.Server.Modules.SubtitleSearch.Queue;

/// <summary>
/// Pin management of queue rows (tech design ⑤/⑥ <c>/pin</c>): a pin moves
/// the row to rank 0 (always dequeued first) and revives a skipped row —
/// the user's explicit "try this again" wins over the give-up rule. Unpin
/// returns the row to Normal; an unexpired webhook boost keeps rank 1.
/// The resulting row (after the change) is returned so the UI can render
/// the effective rank/status without a second request.
/// </summary>
public sealed class SubtitlePinService
{
    private readonly LingarrDbContext _dbContext;
    private readonly ILogger<SubtitlePinService> _logger;

    public SubtitlePinService(LingarrDbContext dbContext, ILogger<SubtitlePinService> logger)
    {
        _dbContext = dbContext;
        _logger = logger;
    }

    /// <summary>
    /// Pins or unpins one media item. Creates the row on demand (pinning a
    /// media item that was never queued is legal — it enqueues it at once).
    /// </summary>
    /// <returns>The state row after the change; null for a non-Movie/Episode
    /// media type.</returns>
    public async Task<SubtitleSearchState?> SetPinnedAsync(
        MediaType mediaType,
        int mediaId,
        bool pinned,
        CancellationToken cancellationToken = default)
    {
        if (mediaType is not (MediaType.Movie or MediaType.Episode))
        {
            return null;
        }

        var now = DateTimeOffset.UtcNow;
        var state = await _dbContext.SubtitleSearchStates.FirstOrDefaultAsync(
            row => row.MediaType == mediaType && row.MediaId == mediaId,
            cancellationToken);

        if (state is null)
        {
            if (!pinned)
            {
                // Unpinning something never queued: report the absent row as
                // a fresh unpinned one — honest no-op answer.
                return new SubtitleSearchState
                {
                    MediaType = mediaType,
                    MediaId = mediaId,
                    NextAttemptAt = now,
                    UpdatedAt = now
                };
            }

            state = new SubtitleSearchState
            {
                MediaType = mediaType,
                MediaId = mediaId,
                Status = SubtitleSearchStatus.Pending,
                PriorityRank = SubtitlePriorityRank.Pinned,
                Pinned = true,
                NextAttemptAt = now,
                UpdatedAt = now
            };
            _dbContext.SubtitleSearchStates.Add(state);
        }
        else
        {
            state.Pinned = pinned;
            state.UpdatedAt = now;
            if (pinned)
            {
                state.PriorityRank = SubtitlePriorityRank.Pinned;
                // Revive a given-up row: the pin is an explicit retry order.
                if (state.Status is SubtitleSearchStatus.Skipped)
                {
                    state.Status = SubtitleSearchStatus.Pending;
                    state.LastError = null;
                }
                state.NextAttemptAt = now;
            }
            else
            {
                // Rank back by boost presence; attempt history is untouched.
                // Boundary rule unified with the dequeue downgrade (QA3
                // P3-12 follow-up): a boost counts as active while
                // webhook_boost_until >= now — expired strictly when
                // webhook_boost_until < now — so both reads agree.
                state.PriorityRank = state.WebhookBoostUntil is { } until && until >= now
                    ? SubtitlePriorityRank.WebhookBoost
                    : SubtitlePriorityRank.Normal;
            }
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
        _logger.LogInformation(
            "Subtitle-search queue row for {MediaType} {MediaId} {Action}.",
            mediaType, mediaId, pinned ? "pinned" : "unpinned");
        return state;
    }
}
