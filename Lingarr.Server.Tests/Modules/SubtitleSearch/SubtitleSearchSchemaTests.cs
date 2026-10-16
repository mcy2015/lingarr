using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Lingarr.Core.Data;
using Lingarr.Core.Enum;
using Lingarr.Core.Entities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Lingarr.Server.Tests.Modules.SubtitleSearch;

/// <summary>
/// M1-legacy DbSet wiring (M2 batch): the six subtitle-search entities are
/// exposed on the context with their FluentMigrator table names pinned
/// (M0008/M0009) so EnsureCreated-built schemas match the migrated schema —
/// the same audit that pins subtitle_language_cache. The composite-PK
/// counter tables must be insertable and deduplicating.
/// </summary>
public class SubtitleSearchSchemaTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly LingarrDbContext _context;

    public SubtitleSearchSchemaTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _context = new LingarrDbContext(new DbContextOptionsBuilder<LingarrDbContext>()
            .UseSqlite(_connection)
            .Options);
        _context.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public void EnsureCreated_PinsTheMigratedTableNames()
    {
        var tables = TableNames();

        Assert.Contains("subtitle_search_state", tables);
        Assert.Contains("subtitle_downloads", tables);
        Assert.Contains("subtitle_search_blacklist", tables);
        Assert.Contains("subtitle_search_sessions", tables);
        Assert.Contains("provider_quota_usage", tables);
        Assert.Contains("coverage_daily", tables);
        // No EF-defaulted (pluralized PascalCase) names leaked for these.
        Assert.DoesNotContain("SubtitleDownloads", tables);
        Assert.DoesNotContain("SubtitleSearchStates", tables);
        Assert.DoesNotContain("ProviderQuotaUsages", tables);
        Assert.DoesNotContain("CoverageDailies", tables);
    }

    [Fact]
    public async Task NewEntities_RoundTripThroughTheContext()
    {
        var now = DateTimeOffset.UtcNow;
        var today = DateOnly.FromDateTime(now.UtcDateTime);

        _context.SubtitleSearchStates.Add(new SubtitleSearchState
        {
            MediaType = MediaType.Movie,
            MediaId = 1,
            NextAttemptAt = now,
            UpdatedAt = now
        });
        _context.SubtitleDownloads.Add(new SubtitleDownload
        {
            MediaType = MediaType.Movie,
            MediaId = 1,
            Provider = "opensubtitles",
            ExternalId = "ext-1",
            Language = "en",
            FilePath = "/movies/Movie/Movie.en.srt",
            MatchScore = 100,
            HashMatch = true,
            Source = SubtitleDownloadSource.Auto,
            Result = SubtitleDownloadResult.Succeeded,
            CreatedAt = now
        });
        _context.SubtitleSearchBlacklists.Add(new SubtitleSearchBlacklist
        {
            Provider = "assrt",
            Reason = "account suspended for scraping",
            ExpiresAt = now.AddDays(7)
        });
        _context.SubtitleSearchSessions.Add(new SubtitleSearchSession
        {
            MediaType = MediaType.Episode,
            MediaId = 5,
            Provider = "gestdown",
            QueryLanguage = "en",
            ResultCount = 2,
            HitTarget = true,
            CreatedAt = now
        });
        _context.ProviderQuotaUsages.Add(new ProviderQuotaUsage
        {
            Date = today,
            Provider = "opensubtitles",
            Searches = 3,
            Downloads = 1,
            QuotaLimit = 40
        });
        _context.CoverageDailies.Add(new CoverageDaily
        {
            Date = today,
            MediaType = MediaType.Movie,
            Language = "zh",
            Covered = 10,
            Total = 234
        });

        Assert.Equal(6, await _context.SaveChangesAsync());
        Assert.Equal(1, await _context.SubtitleSearchStates.CountAsync());
        Assert.Equal(1, await _context.SubtitleDownloads.CountAsync());
        Assert.Equal(1, await _context.SubtitleSearchBlacklists.CountAsync());
        Assert.Equal(1, await _context.SubtitleSearchSessions.CountAsync());
        Assert.Equal(1, await _context.ProviderQuotaUsages.CountAsync());
        Assert.Equal(1, await _context.CoverageDailies.CountAsync());
    }

    [Fact]
    public async Task QuotaUsage_CompositeKeyRejectsSameDayDuplicate()
    {
        var today = DateOnly.FromDateTime(DateTimeOffset.UtcNow.UtcDateTime);
        _context.ProviderQuotaUsages.Add(new ProviderQuotaUsage { Date = today, Provider = "opensubtitles" });
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        _context.ProviderQuotaUsages.Add(new ProviderQuotaUsage { Date = today, Provider = "opensubtitles" });

        // The composite PK backs the INSERT ... ON CONFLICT upsert (⑩) — a
        // same-(date,provider) insert must collide.
        await Assert.ThrowsAsync<DbUpdateException>(() => _context.SaveChangesAsync());
    }

    private HashSet<string> TableNames()
    {
        var command = _context.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table'";
        using var reader = command.ExecuteReader();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (reader.Read())
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }
}
