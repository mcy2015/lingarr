using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Lingarr.Core.Data;
using Lingarr.Core.Entities;
using Lingarr.Core.Enum;
using Lingarr.Server.Modules.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Lingarr.Server.Tests.Services;

/// <summary>
/// Audit S6: the cleanup job also prunes the embedded-track extraction cache
/// (config/imports/embedded). A file goes when it matches the extraction
/// naming, outlives the 7-day retention window and its scan key no longer
/// matches any current scan-marker row — referenced extractions are kept for
/// ExtractTrack reuse, fresh files get a grace window, foreign files are not
/// ours to delete.
/// </summary>
public class CleanupJobTests : IDisposable
{
    private readonly string _root;
    private readonly string _embeddedDirectory;
    private readonly SqliteConnection _connection;
    private readonly LingarrDbContext _dbContext;
    private readonly Mock<IScheduleService> _scheduleService = new();
    private readonly Mock<IHostEnvironment> _environment = new();
    private readonly CleanupJob _job;

    public CleanupJobTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "lingarr-cleanup-tests", Guid.NewGuid().ToString("N"));
        _embeddedDirectory = Path.Combine(_root, "config", "imports", "embedded");
        Directory.CreateDirectory(_embeddedDirectory);

        _environment.SetupGet(e => e.ContentRootPath).Returns(_root);
        // SQLite (not InMemory): the seeded CreatedAt values are applied with
        // ExecuteUpdate, which the InMemory provider cannot translate.
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _dbContext = new LingarrDbContext(new DbContextOptionsBuilder<LingarrDbContext>()
            .UseSqlite(_connection).Options);
        _dbContext.Database.EnsureCreated();
        _job = new CleanupJob(
            _dbContext,
            _scheduleService.Object,
            _environment.Object,
            NullLogger<CleanupJob>.Instance);
    }

    public void Dispose()
    {
        Directory.Delete(_root, recursive: true);
        _dbContext.Dispose();
        _connection.Dispose();
    }

    /// <summary>The SHA-256[..12] fold of a scan key, as the extraction file name carries it.</summary>
    private static string SafeKey(string scanKey) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(scanKey)))[..12];

    private string WriteFile(string fileName, DateTimeOffset lastWrite)
    {
        var path = Path.Combine(_embeddedDirectory, fileName);
        File.WriteAllText(path, "1\n00:00:01,000 --> 00:00:02,000\ncached extraction\n");
        File.SetLastWriteTimeUtc(path, lastWrite.UtcDateTime);
        return path;
    }

    private static string ExtractionFileName(int mediaId, int streamIndex, string safeKey) =>
        $"embedded.movie.{mediaId}.s{streamIndex}.{safeKey}.srt";

    private async Task SeedMarkerAsync(string scanKey)
    {
        _dbContext.MediaSubtitleTracks.Add(new MediaSubtitleTrack
        {
            MediaType = MediaType.Movie,
            MediaId = 1,
            StreamIndex = MediaSubtitleTrack.ScanMarkerStreamIndex,
            Codec = string.Empty,
            IsTextBased = false,
            FilePath = "/movies/dune.mkv",
            MediaHash = scanKey
        });
        await _dbContext.SaveChangesAsync();
    }

    [Fact]
    public async Task Execute_DeletesOverAgeUnreferencedExtractions_KeepsReferencedFreshAndForeign()
    {
        var old = DateTimeOffset.UtcNow.AddDays(-8);
        var fresh = DateTimeOffset.UtcNow.AddHours(-1);

        var referencedOld = WriteFile(ExtractionFileName(1, 2, SafeKey("scan-key-current")), old);
        var orphanOld = WriteFile(ExtractionFileName(1, 3, SafeKey("scan-key-replaced")), old);
        var orphanFresh = WriteFile(ExtractionFileName(1, 4, SafeKey("scan-key-pending")), fresh);
        var foreignOld = WriteFile("readme.txt", old);
        await SeedMarkerAsync("scan-key-current");

        await _job.Execute();

        // Referenced by the current marker: kept regardless of age.
        Assert.True(File.Exists(referencedOld));
        // Over age and unreferenced: deleted.
        Assert.False(File.Exists(orphanOld));
        // Unreferenced but fresh: grace window, kept.
        Assert.True(File.Exists(orphanFresh));
        // Not matching the extraction naming: not ours, kept.
        Assert.True(File.Exists(foreignOld));
    }

    [Fact]
    public async Task Execute_NoMarkers_DeletesEveryOverAgeExtraction()
    {
        // No scan markers at all (library wiped): every extraction is an orphan.
        var oldFile = WriteFile(ExtractionFileName(7, 1, SafeKey("anything")), DateTimeOffset.UtcNow.AddDays(-9));
        var freshFile = WriteFile(ExtractionFileName(8, 1, SafeKey("else")), DateTimeOffset.UtcNow);

        await _job.Execute();

        Assert.False(File.Exists(oldFile));
        Assert.True(File.Exists(freshFile));
    }

    [Fact]
    public async Task Execute_MissingEmbeddedDirectory_IsTolerated()
    {
        Directory.Delete(_embeddedDirectory, recursive: true);
        var expired = new TranslationRequest
        {
            Title = "expired", SourceLanguage = "en", TargetLanguage = "zh",
            SubtitleToTranslate = "/s.srt", MediaType = MediaType.Movie,
            Status = TranslationStatus.Completed, Source = TranslationRequestSource.Import
        };
        _dbContext.TranslationRequests.Add(expired);
        await _dbContext.SaveChangesAsync();
        await _dbContext.TranslationRequests.Where(r => r.Id == expired.Id)
            .ExecuteUpdateAsync(x => x.SetProperty(r => r.CreatedAt, DateTimeOffset.UtcNow.AddDays(-8)));

        await _job.Execute(); // must not throw

        Assert.Equal(0, await _dbContext.TranslationRequests.CountAsync());
    }

    [Fact]
    public async Task Execute_RemovesExpiredTranslationRequests_KeepsRecent()
    {
        var expired = new TranslationRequest
        {
            Title = "expired", SourceLanguage = "en", TargetLanguage = "zh",
            SubtitleToTranslate = "/old.srt", MediaType = MediaType.Movie,
            Status = TranslationStatus.Completed, Source = TranslationRequestSource.Import
        };
        var recent = new TranslationRequest
        {
            Title = "recent", SourceLanguage = "en", TargetLanguage = "zh",
            SubtitleToTranslate = "/new.srt", MediaType = MediaType.Movie,
            Status = TranslationStatus.Completed, Source = TranslationRequestSource.Import
        };
        _dbContext.TranslationRequests.AddRange(expired, recent);
        await _dbContext.SaveChangesAsync();
        // SaveChange's UpdateTimestamps overwrites CreatedAt on Added rows;
        // set-based writes bypass the change tracker.
        await _dbContext.TranslationRequests.Where(r => r.Id == expired.Id)
            .ExecuteUpdateAsync(x => x.SetProperty(r => r.CreatedAt, DateTimeOffset.UtcNow.AddDays(-8)));
        await _dbContext.TranslationRequests.Where(r => r.Id == recent.Id)
            .ExecuteUpdateAsync(x => x.SetProperty(r => r.CreatedAt, DateTimeOffset.UtcNow.AddDays(-1)));

        await _job.Execute();

        var remaining = await _dbContext.TranslationRequests.ToListAsync();
        Assert.Equal("recent", Assert.Single(remaining).Title);
        _scheduleService.Verify(
            s => s.UpdateJobState(It.IsAny<string>(), It.IsAny<string>()), Times.Exactly(2));
    }
}
