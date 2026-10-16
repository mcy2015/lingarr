using System;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
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
/// Pin management of queue rows (design ⑤/⑥): pin creates the row on
/// demand at rank 0, revives skipped rows, unpin returns to Normal (an
/// unexpired boost keeps rank 1) — and non-Movie/Episode media types are
/// rejected. Real SQLite (SaveChanges path).
/// </summary>
public class SubtitlePinServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly PinDbContext _dbContext;
    private readonly SubtitlePinService _service;

    public SubtitlePinServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _dbContext = new PinDbContext(new DbContextOptionsBuilder<LingarrDbContext>()
            .UseSqlite(_connection)
            .Options);
        _dbContext.Database.EnsureCreated();
        _service = new SubtitlePinService(_dbContext, NullLogger<SubtitlePinService>.Instance);
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    private sealed class PinDbContext(DbContextOptions options) : LingarrDbContext(options);

    [Fact]
    public async Task Pin_CreatesTheRowOnDemandAtRankZero()
    {
        var state = await _service.SetPinnedAsync(MediaType.Movie, 12, pinned: true);

        Assert.NotNull(state);
        Assert.True(state.Pinned);
        Assert.Equal(SubtitlePriorityRank.Pinned, state.PriorityRank);
        Assert.Equal(SubtitleSearchStatus.Pending, state.Status);

        var persisted = await _dbContext.SubtitleSearchStates
            .SingleAsync(row => row.MediaType == MediaType.Movie && row.MediaId == 12);
        Assert.True(persisted.Pinned);
        Assert.Equal(SubtitlePriorityRank.Pinned, persisted.PriorityRank);
    }

    [Fact]
    public async Task Pin_RevivesASkippedRow()
    {
        _dbContext.SubtitleSearchStates.Add(new SubtitleSearchState
        {
            MediaType = MediaType.Episode,
            MediaId = 13,
            Status = SubtitleSearchStatus.Skipped,
            PriorityRank = SubtitlePriorityRank.Normal,
            AttemptCount = 8,
            LastError = "gave up",
            NextAttemptAt = DateTimeOffset.UtcNow.AddDays(30),
            UpdatedAt = DateTimeOffset.UtcNow
        });
        await _dbContext.SaveChangesAsync();

        var state = await _service.SetPinnedAsync(MediaType.Episode, 13, pinned: true);

        Assert.Equal(SubtitleSearchStatus.Pending, state!.Status);
        Assert.Null(state.LastError);
        Assert.True(state.NextAttemptAt <= DateTimeOffset.UtcNow.AddSeconds(5));
    }

    [Fact]
    public async Task Unpin_ReturnsToNormal_UnlessTheBoostIsStillActive()
    {
        var now = DateTimeOffset.UtcNow;
        var plain = new SubtitleSearchState
        {
            MediaType = MediaType.Movie, MediaId = 14, Pinned = true,
            PriorityRank = SubtitlePriorityRank.Pinned, NextAttemptAt = now, UpdatedAt = now
        };
        var boosted = new SubtitleSearchState
        {
            MediaType = MediaType.Movie, MediaId = 15, Pinned = true,
            PriorityRank = SubtitlePriorityRank.Pinned,
            WebhookBoostUntil = now.AddHours(24), NextAttemptAt = now, UpdatedAt = now
        };
        _dbContext.SubtitleSearchStates.AddRange(plain, boosted);
        await _dbContext.SaveChangesAsync();

        var unPinnedPlain = await _service.SetPinnedAsync(MediaType.Movie, 14, pinned: false);
        var unPinnedBoosted = await _service.SetPinnedAsync(MediaType.Movie, 15, pinned: false);

        Assert.Equal(SubtitlePriorityRank.Normal, unPinnedPlain!.PriorityRank);
        Assert.False(unPinnedPlain.Pinned);
        Assert.Equal(SubtitlePriorityRank.WebhookBoost, unPinnedBoosted!.PriorityRank);
    }

    [Fact]
    public async Task Unpin_NeverQueuedRow_IsAnHonestNoOp()
    {
        var state = await _service.SetPinnedAsync(MediaType.Movie, 16, pinned: false);

        Assert.NotNull(state);
        Assert.False(state.Pinned);
        // Nothing was persisted for a row that never existed.
        Assert.Equal(0, await _dbContext.SubtitleSearchStates.CountAsync());
    }

    [Fact]
    public async Task Pin_RejectsNonMovieEpisodeMediaTypes()
    {
        Assert.Null(await _service.SetPinnedAsync(MediaType.Show, 1, pinned: true));
        Assert.Null(await _service.SetPinnedAsync(MediaType.Season, 1, pinned: true));
    }
}
