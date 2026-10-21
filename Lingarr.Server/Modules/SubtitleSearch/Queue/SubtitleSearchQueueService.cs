using Lingarr.Core.Data;
using Lingarr.Core.Entities;
using Lingarr.Core.Enum;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Lingarr.Server.Modules.SubtitleSearch.Queue;

/// <summary>
/// Backlog snapshot for the batch card (tech design ⑥ <c>/backlog</c>):
/// how much is queued, how much is pinned/boosted, how much the next round
/// will actually pick up.
/// </summary>
/// <param name="Backlog">Non-terminal rows (pending + failed backoff).</param>
/// <param name="PinnedCount">Rows pinned through the API.</param>
/// <param name="WebhookBoostedCount">Pending rows with an unexpired webhook boost.</param>
/// <param name="NextRoundCount">Pending rows due right now (next_attempt_at &lt;= now).</param>
public sealed record SubtitleSearchBacklogSnapshot(
    int Backlog,
    int PinnedCount,
    int WebhookBoostedCount,
    int NextRoundCount);

/// <summary>
/// Priority queue over <c>subtitle_search_state</c> (tech design ⑤).
/// <see cref="SubtitleSearchState.PriorityRank"/> is the write-time-frozen
/// ordering scalar (0 Pinned / 1 WebhookBoost / 2 Normal) so the dequeue is
/// a single indexed query:
/// <code>
/// WHERE status=0 AND next_attempt_at&lt;=@now
/// ORDER BY priority_rank, next_attempt_at, id LIMIT @batchLimit
/// </code>
/// backed by <c>ix_subtitle_search_state_dequeue</c>. Enqueue dedup rides
/// the UNIQUE(media_type, media_id) index with SQLite upserts that preserve
/// the attempt history (webhook re-boost never resets the backoff clock,
/// ⑤). Failure backoff: 6h × 2^(attempt-1) capped at 7d, attempt ≥ 8 →
/// status Skipped.
/// </summary>
public sealed class SubtitleSearchQueueService
{
    /// <summary>Backoff base (⑤): first failure retries after 6 hours.</summary>
    public static readonly TimeSpan BaseBackoff = TimeSpan.FromHours(6);

    /// <summary>Backoff cap (⑤): never wait more than 7 days.</summary>
    public static readonly TimeSpan MaxBackoff = TimeSpan.FromDays(7);

    /// <summary>Give-up threshold (⑤): attempt_count ≥ 8 → status Skipped.</summary>
    public const int MaxAttempts = 8;

    /// <summary>Webhook boost window on enqueue (⑤: now + 48h).</summary>
    public static readonly TimeSpan WebhookBoostWindow = TimeSpan.FromHours(48);

    /// <summary>
    /// Translation statuses that mean "a translation of this media is still
    /// expected to land" (QA3 P1-1): Pending/InProgress are actively running,
    /// Paused/Interrupted may be resumed. Completed/Failed/Cancelled are
    /// definitive outcomes — a Failed translation must NOT block the queue
    /// revive, or the media stays target-language-less forever (the exact
    /// bug the revive closes).
    /// </summary>
    private static readonly int[] ActiveTranslationStatuses =
    [
        (int)TranslationStatus.Pending,
        (int)TranslationStatus.InProgress,
        (int)TranslationStatus.Interrupted,
        (int)TranslationStatus.Paused
    ];

    private readonly LingarrDbContext _dbContext;
    private readonly ILogger<SubtitleSearchQueueService> _logger;

    public SubtitleSearchQueueService(
        LingarrDbContext dbContext,
        ILogger<SubtitleSearchQueueService> logger)
    {
        _dbContext = dbContext;
        _logger = logger;
    }

    /// <summary>
    /// Fills the queue with gap media: every movie/episode the statistics
    /// scan marked <c>needs_translation=1 AND no_subtitle_needed=0</c> and
    /// that has no state row yet. Two set-based INSERT…SELECT statements
    /// (one per media type) — no per-media round trips, and the NOT EXISTS
    /// guard makes it idempotent across rounds.
    /// <para>
    /// B08 target-language contract: the gap verdict is NOT re-derived here
    /// — the flags this selection reads are written exclusively by writers
    /// that resolve through the shared
    /// <see cref="Lingarr.Server.Modules.SubtitleSearch.Pipeline.TargetLanguageResolver"/>
    /// (SubtitleCoverageRefresher, StatisticsJob, the subtitle-need
    /// endpoint), so a per-media override (or the explicit empty「无目标」
    /// override, which judges "nothing missing" and keeps the media out of
    /// this queue) is inherited through the flags with zero drift. The
    /// override endpoint refreshes a media's flags at write time, so the
    /// next round enqueues against fresh verdicts.
    /// </para>
    /// <para>
    /// Media without a file location (<c>path</c> NULL or empty) never enter
    /// the selection set (QA3 P3-9): there is nothing to search for or burn
    /// a subtitle next to, so queuing them only burns retries.
    /// </para>
    /// <para>
    /// QA3 P1-1 (the Succeeded break in the chain): a download used to end
    /// in a terminal <c>Succeeded</c> row even when the target language was
    /// never covered (en downloaded, translation failed or was never
    /// enqueued) — the NOT EXISTS guard then skipped that media forever.
    /// The revive pass below re-queues <c>Succeeded</c> rows whose media
    /// STILL needs translation (attempt history cleared, next attempt now;
    /// pin/boost state untouched) — unless a translation request for that
    /// media is still expected to land (Pending/InProgress/Paused/Interrupted),
    /// which would loop re-searches into an in-flight translation.
    /// </para>
    /// </summary>
    /// <returns>Number of rows inserted or revived.</returns>
    public async Task<int> EnqueueGapMediaAsync(CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var inserted = 0;
        inserted += await _dbContext.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO subtitle_search_state
                (media_type, media_id, status, priority_rank, pinned, webhook_boost_until,
                 next_attempt_at, attempt_count, last_error, last_searched_at, updated_at)
            SELECT 0, m.id, 0, 2, 0, NULL, @now, 0, NULL, NULL, @now
            FROM movies m
            WHERE m.needs_translation = 1 AND m.no_subtitle_needed = 0
              AND m.path IS NOT NULL AND m.path <> ''
              AND NOT EXISTS (SELECT 1 FROM subtitle_search_state s
                              WHERE s.media_type = 0 AND s.media_id = m.id)
            """,
            new object[] { NowParameter(now) },
            cancellationToken);
        inserted += await _dbContext.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO subtitle_search_state
                (media_type, media_id, status, priority_rank, pinned, webhook_boost_until,
                 next_attempt_at, attempt_count, last_error, last_searched_at, updated_at)
            SELECT 3, e.id, 0, 2, 0, NULL, @now, 0, NULL, NULL, @now
            FROM episodes e
            WHERE e.needs_translation = 1 AND e.no_subtitle_needed = 0
              AND e.path IS NOT NULL AND e.path <> ''
              AND NOT EXISTS (SELECT 1 FROM subtitle_search_state s
                              WHERE s.media_type = 3 AND s.media_id = e.id)
            """,
            new object[] { NowParameter(now) },
            cancellationToken);

        // Revive pass (QA3 P1-1): status 1 = Succeeded, 0 = Pending.
        var revived = 0;
        revived += await _dbContext.Database.ExecuteSqlRawAsync(
            $"""
            UPDATE subtitle_search_state
            SET status = 0, attempt_count = 0, next_attempt_at = @now, last_error = NULL, updated_at = @now
            WHERE media_type = 0 AND status = 1
              AND EXISTS (SELECT 1 FROM movies m
                          WHERE m.id = subtitle_search_state.media_id
                            AND m.needs_translation = 1 AND m.no_subtitle_needed = 0
                            AND m.path IS NOT NULL AND m.path <> '')
              AND NOT EXISTS (SELECT 1 FROM translation_requests tr
                              WHERE tr.media_type = 0 AND tr.media_id = subtitle_search_state.media_id
                                AND tr.status IN ({string.Join(", ", ActiveTranslationStatuses)}))
            """,
            new object[] { NowParameter(now) },
            cancellationToken);
        revived += await _dbContext.Database.ExecuteSqlRawAsync(
            $"""
            UPDATE subtitle_search_state
            SET status = 0, attempt_count = 0, next_attempt_at = @now, last_error = NULL, updated_at = @now
            WHERE media_type = 3 AND status = 1
              AND EXISTS (SELECT 1 FROM episodes e
                          WHERE e.id = subtitle_search_state.media_id
                            AND e.needs_translation = 1 AND e.no_subtitle_needed = 0
                            AND e.path IS NOT NULL AND e.path <> '')
              AND NOT EXISTS (SELECT 1 FROM translation_requests tr
                              WHERE tr.media_type = 3 AND tr.media_id = subtitle_search_state.media_id
                                AND tr.status IN ({string.Join(", ", ActiveTranslationStatuses)}))
            """,
            new object[] { NowParameter(now) },
            cancellationToken);

        var total = inserted + revived;
        if (total > 0)
        {
            _logger.LogInformation(
                "Enqueued {Count} media items into the subtitle-search queue ({Inserted} new, {Revived} revived).",
                total, inserted, revived);
        }

        return total;
    }

    /// <summary>
    /// Webhook/manual enqueue with the ⑤ upsert semantics: rank improves to
    /// the better of stored and requested, the boost window is set,
    /// next_attempt_at jumps to now — attempt_count is preserved (never
    /// reset the backoff history).
    /// </summary>
    public Task EnqueueBoostedAsync(
        MediaType mediaType,
        int mediaId,
        CancellationToken cancellationToken = default)
    {
        return ExecuteUpsertAsync(
            mediaType,
            mediaId,
            """
            ON CONFLICT(media_type, media_id) DO UPDATE SET
                priority_rank = MIN(priority_rank, 1),
                webhook_boost_until = @boost,
                status = CASE WHEN status = 3 THEN 0 ELSE status END,
                next_attempt_at = @now,
                updated_at = @now
            """,
            extraParameter: new SqliteParameter("@boost", DateTimeOffset.UtcNow.Add(WebhookBoostWindow).UtcDateTime),
            insertRank: (int)SubtitlePriorityRank.WebhookBoost,
            insertBoostUntil: DateTimeOffset.UtcNow.Add(WebhookBoostWindow),
            cancellationToken);
    }

    /// <summary>
    /// Dequeues one batch (single indexed SELECT, ⑤) and downgrades expired
    /// webhook boosts inside the same batch — the partial index
    /// ix_subtitle_search_state_boost keeps that UPDATE cheap.
    /// <para>
    /// Eligible states are Pending <em>and</em> Failed (backoff rows): the
    /// design's dequeue SQL writes <c>status=0</c>, but its own backoff rule
    /// — "failed rows retry after 6h×2^n" — requires failed rows to re-enter
    /// the queue once next_attempt_at passes. The covering index serves the
    /// widened predicate the same way (status remains the leading column);
    /// Skipped and Succeeded stay terminal.
    /// </para>
    /// </summary>
    public async Task<List<SubtitleSearchState>> DequeueBatchAsync(
        int batchLimit,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        if (batchLimit <= 0)
        {
            return [];
        }

        var batch = await _dbContext.SubtitleSearchStates
            .AsNoTracking()
            .Where(state => (state.Status == SubtitleSearchStatus.Pending
                             || state.Status == SubtitleSearchStatus.Failed)
                            && state.NextAttemptAt <= now)
            .OrderBy(state => state.PriorityRank)
            .ThenBy(state => state.NextAttemptAt)
            .ThenBy(state => state.Id) // deterministic final tiebreak (QA3 P3-12)
            .Take(batchLimit)
            .ToListAsync(cancellationToken);

        // In-batch boost expiry downgrade (⑤): rows whose boost window
        // lapsed go back to Normal. One set-based statement; the current
        // batch keeps its already-computed order, the next round sees rank 2.
        if (batch.Count > 0)
        {
            var ids = batch.Select(state => state.Id).ToArray();
            await _dbContext.SubtitleSearchStates
                .Where(state => ids.Contains(state.Id)
                                && state.PriorityRank == SubtitlePriorityRank.WebhookBoost
                                && state.WebhookBoostUntil != null
                                && state.WebhookBoostUntil < now)
                .ExecuteUpdateAsync(
                    setters => setters.SetProperty(
                        state => state.PriorityRank,
                        SubtitlePriorityRank.Normal),
                    cancellationToken);
        }

        return batch;
    }

    /// <summary>
    /// Round finished with all target languages covered: status Succeeded,
    /// attempt history cleared (a later re-enqueue starts fresh). The pin
    /// and the priority rank are released too (QA3 P3-10): a permanently
    /// pinned Succeeded row only inflated the backlog card's PinnedCount.
    /// An unexpired webhook boost keeps its rank — expiry alone demotes it.
    /// </summary>
    public Task MarkSucceededAsync(int stateId, CancellationToken cancellationToken = default)
        => _dbContext.SubtitleSearchStates
            .Where(state => state.Id == stateId)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(state => state.Status, SubtitleSearchStatus.Succeeded)
                    .SetProperty(state => state.AttemptCount, 0)
                    .SetProperty(state => state.Pinned, false)
                    .SetProperty(state => state.PriorityRank, state => state.PriorityRank == SubtitlePriorityRank.Pinned
                        ? SubtitlePriorityRank.Normal
                        : state.PriorityRank)
                    .SetProperty(state => state.LastError, (string?)null)
                    .SetProperty(state => state.LastSearchedAt, DateTimeOffset.UtcNow)
                    .SetProperty(state => state.UpdatedAt, DateTimeOffset.UtcNow),
                cancellationToken);

    /// <summary>
    /// Round failed: attempt_count+1, backoff = min(6h × 2^(attempt-1), 7d);
    /// attempt ≥ 8 transitions to Skipped (gave up — only a pin or a new
    /// webhook boost revives the row).
    /// </summary>
    public async Task MarkFailedAsync(
        int stateId,
        string error,
        CancellationToken cancellationToken = default)
    {
        var state = await _dbContext.SubtitleSearchStates
            .Where(s => s.Id == stateId)
            .Select(s => new { s.Id, s.AttemptCount })
            .FirstOrDefaultAsync(cancellationToken);
        if (state is null)
        {
            return;
        }

        var attempt = state.AttemptCount + 1;
        var backoff = BackoffFor(attempt);
        var skipped = attempt >= MaxAttempts;
        var now = DateTimeOffset.UtcNow;

        await _dbContext.SubtitleSearchStates
            .Where(s => s.Id == stateId)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(s => s.AttemptCount, attempt)
                    .SetProperty(s => s.Status,
                        skipped ? SubtitleSearchStatus.Skipped : SubtitleSearchStatus.Failed)
                    .SetProperty(s => s.NextAttemptAt, now + backoff)
                    .SetProperty(s => s.LastError, error)
                    .SetProperty(s => s.LastSearchedAt, now)
                    .SetProperty(s => s.UpdatedAt, now),
                cancellationToken);
    }

    /// <summary>Backoff span after the n-th consecutive failure: 6h, 12h, 24h… capped at 7d.</summary>
    public static TimeSpan BackoffFor(int attemptCount)
    {
        // Doubling by repeated addition, never by a bit shift: a
        // `Ticks << shifts` overflows to a NEGATIVE TimeSpan once shifts
        // grows past ~31, and a negative span sails straight past the 7d
        // cap (QA2 P3-2 — unreachable today only because attempt ≥ 8 goes
        // Skipped; raising MaxAttempts re-arms the landmine). The loop also
        // stops as soon as the cap is reached, so the ticks stay bounded.
        var span = BaseBackoff;
        for (var i = 1; i < attemptCount && span < MaxBackoff; i++)
        {
            span += span;
        }

        return span > MaxBackoff ? MaxBackoff : span;
    }

    /// <summary>
    /// The state row of one media item, if any — the pin endpoint answers
    /// with it so the UI can show the resulting rank/status.
    /// </summary>
    public Task<SubtitleSearchState?> FindByMediaAsync(
        MediaType mediaType,
        int mediaId,
        CancellationToken cancellationToken = default)
        => _dbContext.SubtitleSearchStates
            .AsNoTracking()
            .FirstOrDefaultAsync(
                state => state.MediaType == mediaType && state.MediaId == mediaId,
                cancellationToken);

    /// <summary>
    /// Backlog counts for the batch card — one grouped aggregate over the
    /// table (no per-status queries). Backlog and the next-round count use
    /// the same eligible set as the dequeue (Pending + Failed).
    /// </summary>
    public async Task<SubtitleSearchBacklogSnapshot> GetBacklogSnapshotAsync(
        CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var row = await _dbContext.SubtitleSearchStates
            .GroupBy(_ => 1)
            .Select(group => new
            {
                Backlog = group.Count(state => state.Status == SubtitleSearchStatus.Pending
                                               || state.Status == SubtitleSearchStatus.Failed),
                Pinned = group.Count(state => state.Pinned),
                Boosted = group.Count(state => state.PriorityRank == SubtitlePriorityRank.WebhookBoost
                                               && state.WebhookBoostUntil != null
                                               && state.WebhookBoostUntil >= now),
                Due = group.Count(state => (state.Status == SubtitleSearchStatus.Pending
                                            || state.Status == SubtitleSearchStatus.Failed)
                                           && state.NextAttemptAt <= now)
            })
            .FirstOrDefaultAsync(cancellationToken);

        return row is null
            ? new SubtitleSearchBacklogSnapshot(0, 0, 0, 0)
            : new SubtitleSearchBacklogSnapshot(row.Backlog, row.Pinned, row.Boosted, row.Due);
    }

    private async Task ExecuteUpsertAsync(
        MediaType mediaType,
        int mediaId,
        string conflictClause,
        SqliteParameter? extraParameter = null,
        int insertRank = (int)SubtitlePriorityRank.Normal,
        DateTimeOffset? insertBoostUntil = null,
        CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var parameters = new List<object>
        {
            new SqliteParameter("@mediaType", (int)mediaType),
            new SqliteParameter("@mediaId", mediaId),
            NowParameter(now),
            // The insert side must honor the caller's intent: the conflict
            // clause only repairs EXISTING rows, so a hardcoded Normal/NULL
            // insert meant a webhook's first enqueue (the common case — a
            // freshly downloaded movie has no row yet) never carried the
            // boost at all.
            new SqliteParameter("@rank", insertRank),
            new SqliteParameter("@boostUntil", (object?)insertBoostUntil?.UtcDateTime ?? DBNull.Value)
        };
        if (extraParameter is not null)
        {
            parameters.Add(extraParameter);
        }

        await _dbContext.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO subtitle_search_state
                (media_type, media_id, status, priority_rank, pinned, webhook_boost_until,
                 next_attempt_at, attempt_count, last_error, last_searched_at, updated_at)
            VALUES (@mediaType, @mediaId, 0, @rank, 0, @boostUntil, @now, 0, NULL, NULL, @now)
            """ + conflictClause,
            parameters,
            cancellationToken);
    }

    /// <summary>
    /// Raw-SQL datetime parameter: the SQLite schema stores naive UTC
    /// datetimes and the EF DateTimeOffset converter writes
    /// <c>yyyy-MM-dd HH:mm:ss.fffffff</c> — the parameter must match that
    /// shape or the index comparisons against EF-written rows go wrong.
    /// </summary>
    private static SqliteParameter NowParameter(DateTimeOffset now)
        => new("@now", now.UtcDateTime);
}
