using System;
using System.Collections.Generic;
using System.Data.Common;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Lingarr.Core.Configuration;
using Lingarr.Core.Data;
using Lingarr.Core.Entities;
using Lingarr.Core.Enum;
using Lingarr.Server.Modules.SubtitleSearch.Queue;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Lingarr.Server.Tests.Modules.SubtitleSearch;

/// <summary>
/// Priority-queue contracts of the subtitle-search automation (tech design
/// ⑤, M3 acceptance): dequeue ordering (Pinned &gt; webhook boost &gt; due
/// normal), in-batch boost expiry downgrade, upserts that preserve the
/// attempt history, the B12 backoff curve (attempts 1-8 on 6h×2^n capped
/// 7d, 9-12 at a fixed 14d, give-up past the budget or past the 90-day
/// long-tail window), the gap fill, the backlog aggregate — and the dequeue
/// query plan walking ix_subtitle_search_state_dequeue over a seeded table.
/// Real SQLite schema because the upserts/ExecuteUpdate paths need a
/// relational provider.
/// </summary>
public class SubtitleSearchQueueServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly QueueDbContext _dbContext;
    private readonly InMemorySettingService _settings;
    private readonly SubtitleSearchQueueService _service;

    public SubtitleSearchQueueServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        // Snake-case naming convention: raw SQL (INSERT ON CONFLICT,
        // EXPLAIN QUERY PLAN) addresses the migration column names, so the
        // EnsureCreated schema must match production naming exactly.
        _dbContext = new QueueDbContext(new DbContextOptionsBuilder<LingarrDbContext>()
            .UseSqlite(_connection)
            .UseSnakeCaseNamingConvention()
            .Options);
        _dbContext.Database.EnsureCreated();
        // Empty by default → MarkFailedAsync falls back to the seeded B12
        // defaults (max attempts 12, long-tail window 90d).
        _settings = new InMemorySettingService();
        _service = new SubtitleSearchQueueService(
            _dbContext, _settings, NullLogger<SubtitleSearchQueueService>.Instance);
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    private sealed class QueueDbContext(DbContextOptions options) : LingarrDbContext(options);

    private static SubtitleSearchState Row(
        MediaType mediaType,
        int mediaId,
        SubtitlePriorityRank rank = SubtitlePriorityRank.Normal,
        SubtitleSearchStatus status = SubtitleSearchStatus.Pending,
        int attemptCount = 0,
        DateTimeOffset? nextAttemptAt = null,
        bool pinned = false,
        DateTimeOffset? boostUntil = null,
        DateTimeOffset? lastSearchedAt = null) => new()
    {
        MediaType = mediaType,
        MediaId = mediaId,
        Status = status,
        PriorityRank = rank,
        Pinned = pinned,
        WebhookBoostUntil = boostUntil,
        NextAttemptAt = nextAttemptAt ?? DateTimeOffset.UtcNow.AddHours(-1),
        AttemptCount = attemptCount,
        LastSearchedAt = lastSearchedAt,
        UpdatedAt = DateTimeOffset.UtcNow
    };

    [Fact]
    public async Task Dequeue_OrdersPinnedThenBoostThenNormal_AndSkipsFutureAttempts()
    {
        var now = DateTimeOffset.UtcNow;
        _dbContext.SubtitleSearchStates.AddRange(
            Row(MediaType.Movie, 1, SubtitlePriorityRank.Normal),                          // due normal
            Row(MediaType.Movie, 2, SubtitlePriorityRank.WebhookBoost, boostUntil: now.AddHours(24)),
            Row(MediaType.Movie, 3, SubtitlePriorityRank.Pinned, pinned: true),
            Row(MediaType.Movie, 4, nextAttemptAt: now.AddHours(6)),                        // still backing off
            Row(MediaType.Movie, 5, status: SubtitleSearchStatus.Succeeded),                // terminal
            Row(MediaType.Movie, 6, status: SubtitleSearchStatus.Skipped));                 // gave up
        await _dbContext.SaveChangesAsync();

        var batch = await _service.DequeueBatchAsync(10, now);

        // Priority order: pinned (rank 0) first, boost second, due normal
        // last; the backoff row and both terminal rows never appear.
        Assert.Equal(new[] { 3, 2, 1 }, batch.Select(state => state.MediaId).ToArray());
    }

    [Fact]
    public async Task Dequeue_DowngradesExpiredWebhookBoostsWithinTheBatch()
    {
        var now = DateTimeOffset.UtcNow;
        _dbContext.SubtitleSearchStates.AddRange(
            Row(MediaType.Episode, 10, SubtitlePriorityRank.WebhookBoost, boostUntil: now.AddHours(-1)),
            Row(MediaType.Episode, 11, SubtitlePriorityRank.WebhookBoost, boostUntil: now.AddHours(1)));
        await _dbContext.SaveChangesAsync();

        var batch = await _service.DequeueBatchAsync(10, now);

        Assert.Equal(2, batch.Count);
        var expired = await _service.FindByMediaAsync(MediaType.Episode, 10, CancellationToken.None);
        var active = await _service.FindByMediaAsync(MediaType.Episode, 11, CancellationToken.None);
        Assert.Equal(SubtitlePriorityRank.Normal, expired!.PriorityRank);
        Assert.Equal(SubtitlePriorityRank.WebhookBoost, active!.PriorityRank);
    }

    [Fact]
    public async Task Dequeue_OnceBackoffExpires_PicksUpFailedRowsAgain()
    {
        // The backoff rule (⑤) requires failed rows to re-enter the queue
        // once next_attempt_at passes — a Failed row stuck outside the
        // dequeue would never retry.
        var now = DateTimeOffset.UtcNow;
        _dbContext.SubtitleSearchStates.AddRange(
            Row(MediaType.Movie, 7, status: SubtitleSearchStatus.Failed, attemptCount: 1,
                nextAttemptAt: now.AddMinutes(-1)),                                     // backoff expired → due
            Row(MediaType.Movie, 8, status: SubtitleSearchStatus.Failed, attemptCount: 1,
                nextAttemptAt: now.AddHours(5)));                                       // still backing off
        await _dbContext.SaveChangesAsync();

        var batch = await _service.DequeueBatchAsync(10, now);

        Assert.Equal(new[] { 7 }, batch.Select(state => state.MediaId).ToArray());
    }

    [Fact]
    public async Task Dequeue_LimitsTheBatchSize()
    {
        var now = DateTimeOffset.UtcNow;
        for (var i = 0; i < 5; i++)
        {
            _dbContext.SubtitleSearchStates.Add(Row(MediaType.Movie, 100 + i));
        }

        await _dbContext.SaveChangesAsync();

        Assert.Equal(3, (await _service.DequeueBatchAsync(3, now)).Count);
    }

    [Fact]
    public async Task EnqueueBoosted_PreservesAttemptHistoryAndImprovesRank()
    {
        // A row that already failed twice (backing off a day out) gets
        // webhook-boosted: rank improves to 1, next attempt jumps to now,
        // but the attempt count — the backoff history — survives (⑤).
        var failed = Row(MediaType.Movie, 20, SubtitlePriorityRank.Normal,
            status: SubtitleSearchStatus.Failed, attemptCount: 2,
            nextAttemptAt: DateTimeOffset.UtcNow.AddDays(1));
        failed.LastError = "no candidates";
        _dbContext.SubtitleSearchStates.Add(failed);
        await _dbContext.SaveChangesAsync();

        await _service.EnqueueBoostedAsync(MediaType.Movie, 20);

        var row = await _service.FindByMediaAsync(MediaType.Movie, 20, CancellationToken.None);
        Assert.Equal(2, row!.AttemptCount);
        Assert.Equal(SubtitlePriorityRank.WebhookBoost, row.PriorityRank);
        Assert.NotNull(row.WebhookBoostUntil);
        Assert.True(row.WebhookBoostUntil > DateTimeOffset.UtcNow.AddHours(47));
        Assert.True(row.NextAttemptAt <= DateTimeOffset.UtcNow.AddSeconds(5));
        Assert.Equal(SubtitleSearchStatus.Failed, row.Status); // terminal flip happens on dequeue outcome
        Assert.Equal("no candidates", row.LastError);
    }

    [Fact]
    public async Task EnqueueBoosted_RevivesSkippedRows()
    {
        var skipped = Row(MediaType.Movie, 21, SubtitlePriorityRank.Normal,
            status: SubtitleSearchStatus.Skipped, attemptCount: 8);
        _dbContext.SubtitleSearchStates.Add(skipped);
        await _dbContext.SaveChangesAsync();

        await _service.EnqueueBoostedAsync(MediaType.Movie, 21);

        var row = await _service.FindByMediaAsync(MediaType.Movie, 21, CancellationToken.None);
        Assert.Equal(SubtitleSearchStatus.Pending, row!.Status);
        // The attempt history survives the revive — the boost puts the row
        // back into the dequeued set immediately (B12 acceptance 3: the
        // boost stays a revive path; the backoff curve change never
        // touches it).
        Assert.Equal(8, row.AttemptCount);
    }

    [Fact]
    public async Task EnqueueGapMedia_InsertsOnlyUnqueuedGapItems_AndIsIdempotent()
    {
        _dbContext.Movies.Add(new Movie
        {
            RadarrId = 1,
            Title = "Gap Movie",
            Path = "/media/gap",
            FileName = "gap.mkv",
            DateAdded = DateTimeOffset.UtcNow,
            NeedsTranslation = true,
            NoSubtitleNeeded = false
        });
        _dbContext.Movies.Add(new Movie
        {
            RadarrId = 2,
            Title = "Covered Movie",
            Path = "/media/covered",
            FileName = "covered.mkv",
            DateAdded = DateTimeOffset.UtcNow,
            NeedsTranslation = false,
            NoSubtitleNeeded = false
        });
        _dbContext.Movies.Add(new Movie
        {
            RadarrId = 3,
            Title = "Exempt Movie",
            Path = "/media/exempt",
            FileName = "exempt.mkv",
            DateAdded = DateTimeOffset.UtcNow,
            NeedsTranslation = true,
            NoSubtitleNeeded = true
        });
        // QA3 P3-9: no file location (path NULL / empty) → never queued.
        _dbContext.Movies.Add(new Movie
        {
            RadarrId = 5,
            Title = "Pathless Movie",
            Path = null,
            FileName = "pathless.mkv",
            DateAdded = DateTimeOffset.UtcNow,
            NeedsTranslation = true,
            NoSubtitleNeeded = false
        });
        var queued = new Movie
        {
            RadarrId = 4,
            Title = "Already Queued",
            Path = "/media/queued",
            FileName = "queued.mkv",
            DateAdded = DateTimeOffset.UtcNow,
            NeedsTranslation = true,
            NoSubtitleNeeded = false
        };
        _dbContext.Movies.Add(queued);
        await _dbContext.SaveChangesAsync();
        _dbContext.SubtitleSearchStates.Add(Row(MediaType.Movie, queued.Id));
        var show = new Show
        {
            SonarrId = 1,
            Title = "Gap Show",
            Path = "/media/show",
            DateAdded = DateTimeOffset.UtcNow
        };
        _dbContext.Episodes.Add(new Episode
        {
            SonarrId = 1,
            EpisodeNumber = 1,
            Title = "Gap Episode",
            Path = "/media/show/s01",
            FileName = "e01.mkv",
            DateAdded = DateTimeOffset.UtcNow,
            SeasonId = 1,
            Season = new Season
            {
                SeasonNumber = 1,
                ShowId = 1,
                Show = show
            },
            NeedsTranslation = true,
            NoSubtitleNeeded = false
        });
        // Episode-side path guard: an empty path is "no location" too
        // (Episode.Path defaults to the empty string).
        _dbContext.Episodes.Add(new Episode
        {
            SonarrId = 2,
            EpisodeNumber = 2,
            Title = "Pathless Episode",
            Path = "",
            FileName = "e02.mkv",
            DateAdded = DateTimeOffset.UtcNow,
            SeasonId = 1,
            Season = new Season
            {
                SeasonNumber = 1,
                ShowId = 1,
                Show = show
            },
            NeedsTranslation = true,
            NoSubtitleNeeded = false
        });
        await _dbContext.SaveChangesAsync();

        var first = await _service.EnqueueGapMediaAsync();
        var second = await _service.EnqueueGapMediaAsync();

        Assert.Equal(2, first);  // the gap movie + the gap episode
        Assert.Equal(0, second); // idempotent: nothing new the second time
        // The pre-existing queued row + the two inserted gap rows.
        Assert.Equal(3, await _dbContext.SubtitleSearchStates.CountAsync());
        // The already-queued movie keeps its row (looked up by its real id —
        // the old test's hardcoded MediaId=1 pointed at whatever got id 1).
        var inserted = await _service.FindByMediaAsync(MediaType.Movie, queued.Id, CancellationToken.None);
        Assert.NotNull(inserted);
        Assert.Equal(SubtitlePriorityRank.Normal, inserted.PriorityRank);
    }

    [Fact]
    public async Task EnqueueGapMedia_RevivesSucceededRowsStillNeedingTranslation()
    {
        // QA3 P1-1: the en subtitle downloaded (row → Succeeded) but the
        // target language never landed — the media still needs translation,
        // so the next gap round must put the row back into the queue.
        var movie = new Movie
        {
            RadarrId = 11,
            Title = "Half Covered",
            Path = "/media/half",
            FileName = "half.mkv",
            DateAdded = DateTimeOffset.UtcNow,
            NeedsTranslation = true,
            NoSubtitleNeeded = false
        };
        _dbContext.Movies.Add(movie);
        await _dbContext.SaveChangesAsync();
        var state = Row(
            MediaType.Movie, movie.Id,
            status: SubtitleSearchStatus.Succeeded,
            attemptCount: 4,
            nextAttemptAt: DateTimeOffset.UtcNow.AddDays(-3));
        state.LastError = "old";
        state.LastSearchedAt = DateTimeOffset.UtcNow.AddDays(-3);
        _dbContext.SubtitleSearchStates.Add(state);
        await _dbContext.SaveChangesAsync();

        var first = await _service.EnqueueGapMediaAsync();
        var second = await _service.EnqueueGapMediaAsync();

        Assert.Equal(1, first);  // one revived row, nothing inserted
        Assert.Equal(0, second); // now Pending — no longer Succeeded, no repeat
        var row = await _service.FindByMediaAsync(MediaType.Movie, movie.Id, CancellationToken.None);
        Assert.Equal(SubtitleSearchStatus.Pending, row!.Status);
        Assert.Equal(0, row.AttemptCount); // fresh attempt history
        Assert.Null(row.LastError);
        Assert.True(row.NextAttemptAt <= DateTimeOffset.UtcNow.AddSeconds(5));
    }

    [Fact]
    public async Task EnqueueGapMedia_ReviveKeepsPinnedRowsPinned()
    {
        var movie = new Movie
        {
            RadarrId = 12,
            Title = "Pinned Half Covered",
            Path = "/media/pinned",
            FileName = "pinned.mkv",
            DateAdded = DateTimeOffset.UtcNow,
            NeedsTranslation = true,
            NoSubtitleNeeded = false
        };
        _dbContext.Movies.Add(movie);
        await _dbContext.SaveChangesAsync();
        _dbContext.SubtitleSearchStates.Add(Row(
            MediaType.Movie, movie.Id,
            rank: SubtitlePriorityRank.Pinned,
            status: SubtitleSearchStatus.Succeeded,
            pinned: true));
        await _dbContext.SaveChangesAsync();

        await _service.EnqueueGapMediaAsync();

        var row = await _service.FindByMediaAsync(MediaType.Movie, movie.Id, CancellationToken.None);
        Assert.Equal(SubtitleSearchStatus.Pending, row!.Status);
        Assert.True(row.Pinned); // the pin survives the revive
        Assert.Equal(SubtitlePriorityRank.Pinned, row.PriorityRank);
    }

    [Fact]
    public async Task EnqueueGapMedia_ActiveTranslationRequest_BlocksTheRevive()
    {
        // A translation still expected to land (Pending/InProgress/Paused/
        // Interrupted) must not be re-searched under it — that loops the
        // queue into an in-flight translation.
        var movie = new Movie
        {
            RadarrId = 13,
            Title = "Translating",
            Path = "/media/translating",
            FileName = "translating.mkv",
            DateAdded = DateTimeOffset.UtcNow,
            NeedsTranslation = true,
            NoSubtitleNeeded = false
        };
        _dbContext.Movies.Add(movie);
        await _dbContext.SaveChangesAsync();
        var state = Row(MediaType.Movie, movie.Id, status: SubtitleSearchStatus.Succeeded);
        _dbContext.SubtitleSearchStates.Add(state);
        var episode = new Episode
        {
            SonarrId = 3,
            EpisodeNumber = 4,
            Title = "Translating Episode",
            Path = "/media/show/s01",
            FileName = "e04.mkv",
            DateAdded = DateTimeOffset.UtcNow,
            SeasonId = 1,
            Season = new Season
            {
                SeasonNumber = 1,
                ShowId = 1,
                Show = new Show
                {
                    SonarrId = 2,
                    Title = "Translating Show",
                    Path = "/media/show2",
                    DateAdded = DateTimeOffset.UtcNow
                }
            },
            NeedsTranslation = true,
            NoSubtitleNeeded = false
        };
        _dbContext.Episodes.Add(episode);
        await _dbContext.SaveChangesAsync();
        var episodeState = Row(MediaType.Episode, episode.Id, status: SubtitleSearchStatus.Succeeded);
        _dbContext.SubtitleSearchStates.Add(episodeState);
        await _dbContext.SaveChangesAsync();
        _dbContext.TranslationRequests.AddRange(
            new TranslationRequest
            {
                MediaId = movie.Id,
                MediaType = MediaType.Movie,
                Title = movie.Title,
                SourceLanguage = "en",
                TargetLanguage = "zh",
                Status = TranslationStatus.InProgress
            },
            new TranslationRequest
            {
                MediaId = episode.Id,
                MediaType = MediaType.Episode,
                Title = episode.Title,
                SourceLanguage = "en",
                TargetLanguage = "zh",
                Status = TranslationStatus.Paused // paused counts as still expected
            });
        await _dbContext.SaveChangesAsync();

        var result = await _service.EnqueueGapMediaAsync();

        Assert.Equal(0, result); // nothing inserted, nothing revived
        var movieRow = await _service.FindByMediaAsync(MediaType.Movie, movie.Id, CancellationToken.None);
        var episodeRow = await _service.FindByMediaAsync(MediaType.Episode, episode.Id, CancellationToken.None);
        Assert.Equal(SubtitleSearchStatus.Succeeded, movieRow!.Status);
        Assert.Equal(SubtitleSearchStatus.Succeeded, episodeRow!.Status);
    }

    [Fact]
    public async Task EnqueueGapMedia_FinishedTranslationRequest_DoesNotBlockTheRevive()
    {
        // A FAILED translation is exactly the broken chain the revive exists
        // for (en downloaded, translation died): re-searching must happen.
        var movie = new Movie
        {
            RadarrId = 14,
            Title = "Failed Translation",
            Path = "/media/failed",
            FileName = "failed.mkv",
            DateAdded = DateTimeOffset.UtcNow,
            NeedsTranslation = true,
            NoSubtitleNeeded = false
        };
        _dbContext.Movies.Add(movie);
        await _dbContext.SaveChangesAsync();
        _dbContext.SubtitleSearchStates.Add(
            Row(MediaType.Movie, movie.Id, status: SubtitleSearchStatus.Succeeded));
        _dbContext.TranslationRequests.AddRange(
            new TranslationRequest
            {
                MediaId = movie.Id,
                MediaType = MediaType.Movie,
                Title = movie.Title,
                SourceLanguage = "en",
                TargetLanguage = "zh",
                Status = TranslationStatus.Completed
            },
            new TranslationRequest
            {
                MediaId = movie.Id,
                MediaType = MediaType.Movie,
                Title = movie.Title,
                SourceLanguage = "en",
                TargetLanguage = "zh",
                Status = TranslationStatus.Failed
            });
        await _dbContext.SaveChangesAsync();

        var result = await _service.EnqueueGapMediaAsync();

        Assert.Equal(1, result);
        var row = await _service.FindByMediaAsync(MediaType.Movie, movie.Id, CancellationToken.None);
        Assert.Equal(SubtitleSearchStatus.Pending, row!.Status);
    }

    [Fact]
    public async Task EnqueueGapMedia_CoveredMediaIsNotRevived()
    {
        // needs_translation flipped back to 0 (statistics scan) — a
        // Succeeded row stays terminal.
        var movie = new Movie
        {
            RadarrId = 15,
            Title = "Covered After All",
            Path = "/media/covered2",
            FileName = "covered2.mkv",
            DateAdded = DateTimeOffset.UtcNow,
            NeedsTranslation = false,
            NoSubtitleNeeded = false
        };
        _dbContext.Movies.Add(movie);
        await _dbContext.SaveChangesAsync();
        _dbContext.SubtitleSearchStates.Add(
            Row(MediaType.Movie, movie.Id, status: SubtitleSearchStatus.Succeeded));
        await _dbContext.SaveChangesAsync();

        var result = await _service.EnqueueGapMediaAsync();

        Assert.Equal(0, result);
        var row = await _service.FindByMediaAsync(MediaType.Movie, movie.Id, CancellationToken.None);
        Assert.Equal(SubtitleSearchStatus.Succeeded, row!.Status);
    }

    [Fact]
    public async Task MarkFailed_BackoffDoublesFromSixHoursAndCapsAtSevenDays()
    {
        var expected = new[]
        {
            TimeSpan.FromHours(6),       // attempt 1
            TimeSpan.FromHours(12),      // attempt 2
            TimeSpan.FromHours(24),      // attempt 3
            TimeSpan.FromHours(48),      // attempt 4
            TimeSpan.FromHours(96),      // attempt 5
            TimeSpan.FromDays(7),        // attempt 6 — 192h capped
            TimeSpan.FromDays(7)         // attempt 7
        };
        for (var attempt = 1; attempt <= 7; attempt++)
        {
            var state = Row(MediaType.Movie, 30 + attempt, attemptCount: attempt - 1);
            _dbContext.SubtitleSearchStates.Add(state);
            await _dbContext.SaveChangesAsync();

            var before = DateTimeOffset.UtcNow;
            await _service.MarkFailedAsync(state.Id, "boom");
            var after = DateTimeOffset.UtcNow;

            var row = await _dbContext.SubtitleSearchStates.AsNoTracking().SingleAsync(s => s.Id == state.Id);
            Assert.Equal(attempt, row.AttemptCount);
            Assert.Equal(SubtitleSearchStatus.Failed, row.Status);
            var actualBackoff = row.NextAttemptAt - before;
            var ceiling = row.NextAttemptAt - after;
            Assert.True(actualBackoff >= expected[attempt - 1] - TimeSpan.FromSeconds(1),
                $"attempt {attempt}: backoff {actualBackoff} below {expected[attempt - 1]}");
            Assert.True(ceiling <= expected[attempt - 1] + TimeSpan.FromSeconds(1),
                $"attempt {attempt}: backoff {ceiling} above {expected[attempt - 1]}");
        }
    }

    [Fact]
    public async Task MarkFailed_AttemptEight_StaysRetryableOnTheSevenDayCap()
    {
        // B12 acceptance 1, the behavior-change point: the default budget
        // (12) keeps the 8th failure INSIDE the dense zone — status stays
        // retryable (Failed, never Skipped) and next_attempt rides the
        // capped doubling curve (+7d). The pre-B12 rule skipped here.
        var state = Row(MediaType.Movie, 40, attemptCount: 7);
        _dbContext.SubtitleSearchStates.Add(state);
        await _dbContext.SaveChangesAsync();

        var before = DateTimeOffset.UtcNow;
        await _service.MarkFailedAsync(state.Id, "still nothing");
        var after = DateTimeOffset.UtcNow;

        var row = await _dbContext.SubtitleSearchStates.AsNoTracking().SingleAsync(s => s.Id == state.Id);
        Assert.Equal(8, row.AttemptCount);
        Assert.Equal(SubtitleSearchStatus.Failed, row.Status);
        Assert.Equal("still nothing", row.LastError);
        // BackoffFor(8) = 192h capped at 7d.
        Assert.True(row.NextAttemptAt - before >= TimeSpan.FromDays(7) - TimeSpan.FromSeconds(1),
            $"attempt 8: backoff {row.NextAttemptAt - before} below the 7d cap");
        Assert.True(row.NextAttemptAt - after <= TimeSpan.FromDays(7) + TimeSpan.FromSeconds(1),
            $"attempt 8: backoff {row.NextAttemptAt - after} above the 7d cap");
    }

    [Fact]
    public async Task MarkFailed_LongTailAttemptsNineToTwelve_WaitFourteenDays()
    {
        // B12: the tail zone (attempts 9-12 under the default budget)
        // spaces retries at a fixed 14 days instead of the capped curve.
        foreach (var attemptCount in new[] { 8, 9, 10, 11 })
        {
            var state = Row(MediaType.Movie, 45 + attemptCount,
                status: SubtitleSearchStatus.Failed,
                attemptCount: attemptCount,
                lastSearchedAt: DateTimeOffset.UtcNow.AddDays(-14));
            _dbContext.SubtitleSearchStates.Add(state);
            await _dbContext.SaveChangesAsync();

            var before = DateTimeOffset.UtcNow;
            await _service.MarkFailedAsync(state.Id, "tail miss");
            var after = DateTimeOffset.UtcNow;

            var row = await _dbContext.SubtitleSearchStates.AsNoTracking().SingleAsync(s => s.Id == state.Id);
            Assert.Equal(attemptCount + 1, row.AttemptCount);
            Assert.Equal(SubtitleSearchStatus.Failed, row.Status); // 12 still retryable
            Assert.True(row.NextAttemptAt - before >= TimeSpan.FromDays(14) - TimeSpan.FromSeconds(1),
                $"attemptCount {attemptCount}: backoff {row.NextAttemptAt - before} below 14d");
            Assert.True(row.NextAttemptAt - after <= TimeSpan.FromDays(14) + TimeSpan.FromSeconds(1),
                $"attemptCount {attemptCount}: backoff {row.NextAttemptAt - after} above 14d");
        }
    }

    [Fact]
    public async Task MarkFailed_AttemptThirteen_TransitionsToSkipped()
    {
        // B12 acceptance 1: the first attempt past the default budget (12)
        // gives up — Skipped stays terminal for the dequeue; only a pin or
        // a new webhook boost revives the row.
        var state = Row(MediaType.Movie, 60, status: SubtitleSearchStatus.Failed,
            attemptCount: 12, lastSearchedAt: DateTimeOffset.UtcNow.AddDays(-14));
        _dbContext.SubtitleSearchStates.Add(state);
        await _dbContext.SaveChangesAsync();

        await _service.MarkFailedAsync(state.Id, "budget exhausted");

        var row = await _dbContext.SubtitleSearchStates.AsNoTracking().SingleAsync(s => s.Id == state.Id);
        Assert.Equal(13, row.AttemptCount);
        Assert.Equal(SubtitleSearchStatus.Skipped, row.Status);
        Assert.Equal("budget exhausted", row.LastError);
        Assert.NotNull(row.LastSearchedAt);
    }

    [Fact]
    public async Task MarkFailed_MaxAttemptsEight_RestoresThePreB12RuleFieldByField()
    {
        // B12 acceptance 2 (regression gate): subtitle_search_max_attempts=8
        // must behave exactly like the pre-B12 compile-time constant — the
        // 8th failure transitions to Skipped (with the capped 7d backoff
        // written, as the old code did) and the doubling curve covers every
        // retryable attempt. The long-tail window must not fire here.
        _settings.SetSetting(SettingKeys.SubtitleSearch.MaxAttempts, "8").GetAwaiter().GetResult();

        var state = Row(MediaType.Movie, 61, attemptCount: 7,
            lastSearchedAt: DateTimeOffset.UtcNow.AddDays(-400)); // window long exceeded — ignored in legacy mode
        _dbContext.SubtitleSearchStates.Add(state);
        await _dbContext.SaveChangesAsync();

        var before = DateTimeOffset.UtcNow;
        await _service.MarkFailedAsync(state.Id, "legacy give-up");
        var after = DateTimeOffset.UtcNow;

        var row = await _dbContext.SubtitleSearchStates.AsNoTracking().SingleAsync(s => s.Id == state.Id);
        Assert.Equal(8, row.AttemptCount);
        Assert.Equal(SubtitleSearchStatus.Skipped, row.Status);
        Assert.Equal("legacy give-up", row.LastError);
        // The old code wrote next_attempt = now + BackoffFor(8) even on the
        // skipped row; the legacy mode keeps that shape.
        Assert.True(row.NextAttemptAt - before >= TimeSpan.FromDays(7) - TimeSpan.FromSeconds(1));
        Assert.True(row.NextAttemptAt - after <= TimeSpan.FromDays(7) + TimeSpan.FromSeconds(1));

        // Post-boost retries past the line keep skipping (old >= rule).
        var boosted = Row(MediaType.Movie, 62, status: SubtitleSearchStatus.Skipped,
            attemptCount: 9, lastSearchedAt: DateTimeOffset.UtcNow.AddDays(-1));
        _dbContext.SubtitleSearchStates.Add(boosted);
        await _dbContext.SaveChangesAsync();
        await _service.MarkFailedAsync(boosted.Id, "boost retry");
        var boostedRow = await _dbContext.SubtitleSearchStates.AsNoTracking()
            .SingleAsync(s => s.Id == boosted.Id);
        Assert.Equal(10, boostedRow.AttemptCount);
        Assert.Equal(SubtitleSearchStatus.Skipped, boostedRow.Status);
    }

    [Fact]
    public async Task MarkFailed_LongTailWindowExhausted_GivesUpBeforeTheBudget()
    {
        // B12's 90-day backstop: a tail-zone row whose previous attempt
        // (the stored last_searched_at) is older than the long-tail window
        // gives up on the next failure even though the attempt budget is
        // not exhausted — the starved-queue shape (job downtime, batch
        // limits) must not stretch the catch-up window indefinitely.
        var starved = Row(MediaType.Movie, 63, status: SubtitleSearchStatus.Failed,
            attemptCount: 10, lastSearchedAt: DateTimeOffset.UtcNow.AddDays(-91));
        _dbContext.SubtitleSearchStates.Add(starved);
        await _dbContext.SaveChangesAsync();

        await _service.MarkFailedAsync(starved.Id, "window exhausted");

        var row = await _dbContext.SubtitleSearchStates.AsNoTracking().SingleAsync(s => s.Id == starved.Id);
        Assert.Equal(11, row.AttemptCount);
        Assert.Equal(SubtitleSearchStatus.Skipped, row.Status);
        Assert.Equal("window exhausted", row.LastError);

        // Control: one day short of the window the tail retry continues.
        var fresh = Row(MediaType.Movie, 64, status: SubtitleSearchStatus.Failed,
            attemptCount: 10, lastSearchedAt: DateTimeOffset.UtcNow.AddDays(-89));
        _dbContext.SubtitleSearchStates.Add(fresh);
        await _dbContext.SaveChangesAsync();

        await _service.MarkFailedAsync(fresh.Id, "window still open");

        var freshRow = await _dbContext.SubtitleSearchStates.AsNoTracking().SingleAsync(s => s.Id == fresh.Id);
        Assert.Equal(11, freshRow.AttemptCount);
        Assert.Equal(SubtitleSearchStatus.Failed, freshRow.Status);
    }

    [Fact]
    public async Task MarkFailed_LongTailWindow_DoesNotArmInsideTheDenseZone()
    {
        // The backstop bounds the LONG TAIL only: a dense-zone row (here a
        // first failure after months of queue starvation) still gets its
        // full budget — new uploads appear over time, and the window is
        // not a global age limit on the queue row.
        var stale = Row(MediaType.Movie, 65, attemptCount: 0,
            lastSearchedAt: DateTimeOffset.UtcNow.AddDays(-365));
        _dbContext.SubtitleSearchStates.Add(stale);
        await _dbContext.SaveChangesAsync();

        await _service.MarkFailedAsync(stale.Id, "first try after downtime");

        var row = await _dbContext.SubtitleSearchStates.AsNoTracking().SingleAsync(s => s.Id == stale.Id);
        Assert.Equal(1, row.AttemptCount);
        Assert.Equal(SubtitleSearchStatus.Failed, row.Status);
    }

    [Fact]
    public async Task MarkSucceeded_ClearsTheAttemptHistory()
    {
        var state = Row(MediaType.Movie, 41, status: SubtitleSearchStatus.Failed, attemptCount: 3);
        state.LastError = "old error";
        _dbContext.SubtitleSearchStates.Add(state);
        await _dbContext.SaveChangesAsync();

        await _service.MarkSucceededAsync(state.Id);

        var row = await _dbContext.SubtitleSearchStates.AsNoTracking().SingleAsync(s => s.Id == state.Id);
        Assert.Equal(SubtitleSearchStatus.Succeeded, row.Status);
        Assert.Equal(0, row.AttemptCount);
        Assert.Null(row.LastError);
        Assert.NotNull(row.LastSearchedAt);
    }

    [Fact]
    public async Task MarkSucceeded_ReleasesThePinAndTheRank()
    {
        // QA3 P3-10: a permanently pinned Succeeded row only inflated the
        // backlog card's PinnedCount — success ends the pin.
        var state = Row(
            MediaType.Movie, 42,
            rank: SubtitlePriorityRank.Pinned,
            status: SubtitleSearchStatus.Pending,
            pinned: true);
        _dbContext.SubtitleSearchStates.Add(state);
        await _dbContext.SaveChangesAsync();

        await _service.MarkSucceededAsync(state.Id);

        var row = await _dbContext.SubtitleSearchStates.AsNoTracking().SingleAsync(s => s.Id == state.Id);
        Assert.Equal(SubtitleSearchStatus.Succeeded, row.Status);
        Assert.False(row.Pinned);
        Assert.Equal(SubtitlePriorityRank.Normal, row.PriorityRank);
    }

    [Fact]
    public void BackoffFor_NeverOverflows_AndStaysCappedAtSevenDays()
    {
        // QA2 P3-2: the old `Ticks << shifts` wrapped negative for large
        // shift counts and a negative span bypassed the cap. Any attempt
        // count must answer a positive span no larger than 7d.
        Assert.Equal(TimeSpan.FromHours(6), SubtitleSearchQueueService.BackoffFor(1));
        Assert.Equal(TimeSpan.FromHours(96), SubtitleSearchQueueService.BackoffFor(5));
        Assert.Equal(TimeSpan.FromDays(7), SubtitleSearchQueueService.BackoffFor(6));
        for (var attempt = 7; attempt <= 1000; attempt++)
        {
            var backoff = SubtitleSearchQueueService.BackoffFor(attempt);
            Assert.True(backoff > TimeSpan.Zero, $"attempt {attempt}: backoff must stay positive");
            Assert.True(backoff <= SubtitleSearchQueueService.MaxBackoff,
                $"attempt {attempt}: backoff {backoff} exceeded the 7d cap");
        }
    }

    [Fact]
    public async Task Dequeue_BreaksRankAndTimeTiesById()
    {
        // QA3 P3-12: equal (rank, next_attempt_at) needs a deterministic
        // final order — insertion order is not guaranteed by the store.
        var now = DateTimeOffset.UtcNow;
        var due = now.AddHours(-1);
        var first = new SubtitleSearchState
        {
            MediaType = MediaType.Movie, MediaId = 60, Status = SubtitleSearchStatus.Pending,
            PriorityRank = SubtitlePriorityRank.Normal, NextAttemptAt = due, UpdatedAt = due
        };
        var second = new SubtitleSearchState
        {
            MediaType = MediaType.Movie, MediaId = 61, Status = SubtitleSearchStatus.Pending,
            PriorityRank = SubtitlePriorityRank.Normal, NextAttemptAt = due, UpdatedAt = due
        };
        _dbContext.SubtitleSearchStates.AddRange(second, first); // tracked out of id order
        await _dbContext.SaveChangesAsync();

        var batch = await _service.DequeueBatchAsync(10, now);

        // The contract: ties resolve by ascending row id, whatever the
        // tracking order was.
        var expectedIds = new[] { first.Id, second.Id }.OrderBy(id => id).ToArray();
        Assert.Equal(2, batch.Count);
        Assert.Equal(expectedIds, batch.Select(state => state.Id).ToArray());
    }

    [Fact]
    public async Task GetBacklogSnapshot_CountsTheFourRealFigures()
    {
        var now = DateTimeOffset.UtcNow;
        _dbContext.SubtitleSearchStates.AddRange(
            Row(MediaType.Movie, 50, SubtitlePriorityRank.Pinned, pinned: true),
            Row(MediaType.Movie, 51, SubtitlePriorityRank.WebhookBoost, boostUntil: now.AddHours(24)),
            Row(MediaType.Movie, 52, SubtitlePriorityRank.WebhookBoost, boostUntil: now.AddHours(-1)),
            Row(MediaType.Movie, 53, nextAttemptAt: now.AddHours(6)),   // pending but not due
            Row(MediaType.Movie, 54, status: SubtitleSearchStatus.Skipped));
        await _dbContext.SaveChangesAsync();

        var snapshot = await _service.GetBacklogSnapshotAsync();

        // backlog = pending + failed rows (4: 50,51,52,53); pinned = 1;
        // active boosts = 1 (51); next round pickup = 3 (50,51,52).
        Assert.Equal(4, snapshot.Backlog);
        Assert.Equal(1, snapshot.PinnedCount);
        Assert.Equal(1, snapshot.WebhookBoostedCount);
        Assert.Equal(3, snapshot.NextRoundCount);
    }

    [Fact]
    public async Task DequeueQueryPlan_WalksTheDequeueCoveringIndex()
    {
        // Seed 2000 pending rows (the acceptance's test-scale seed for the
        // 10w-row budget) so SQLite's planner has a reason to prefer the
        // covering index over a scan.
        var now = DateTimeOffset.UtcNow;
        var seed = now.AddHours(-1);
        for (var i = 0; i < 2000; i += 250)
        {
            _dbContext.SubtitleSearchStates.AddRange(
                Enumerable.Range(i, 250).Select(id => new SubtitleSearchState
                {
                    MediaType = MediaType.Movie,
                    MediaId = id,
                    Status = SubtitleSearchStatus.Pending,
                    PriorityRank = (SubtitlePriorityRank)(id % 3),
                    NextAttemptAt = seed,
                    UpdatedAt = seed
                }));
            await _dbContext.SaveChangesAsync();
        }

        // The exact eligibility shape DequeueBatchAsync translates to
        // (Pending or Failed, due, rank order, id tiebreak, limited).
        var plan = await ExplainQueryPlanAsync(
            """
            SELECT id FROM subtitle_search_state
            WHERE status IN (0, 2) AND next_attempt_at <= @now
            ORDER BY priority_rank, next_attempt_at, id
            LIMIT 200
            """,
            new Dictionary<string, object> { ["@now"] = now.UtcDateTime });

        Assert.Contains("ix_subtitle_search_state_dequeue", plan);
        Assert.DoesNotContain("SCAN subtitle_search_state", plan);
    }

    /// <summary>
    /// EXPLAIN QUERY PLAN through the same connection so the seeded data and
    /// the planner see one database.
    /// </summary>
    private async Task<string> ExplainQueryPlanAsync(string sql, Dictionary<string, object> parameters)
    {
        var lines = new List<string>();
        await using var command = _connection.CreateCommand();
        command.CommandText = "EXPLAIN QUERY PLAN " + sql;
        foreach (var (name, value) in parameters)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value;
            command.Parameters.Add(parameter);
        }

        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            lines.Add(reader.GetString(3));
        }

        return string.Join('\n', lines);
    }
}
