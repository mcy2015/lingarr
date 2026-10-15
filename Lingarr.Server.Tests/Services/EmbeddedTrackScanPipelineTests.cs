using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Lingarr.Core.Configuration;
using Lingarr.Core.Data;
using Lingarr.Core.Entities;
using Lingarr.Core.Enum;
using Lingarr.Server.Modules.Media;
using Lingarr.Server.Modules.Settings;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Lingarr.Server.Tests.Services;

/// <summary>
/// Audit P0-6 scan-pipeline coverage against a real SQLite schema and real
/// media files on disk, with ffprobe stubbed: the per-run batch limit bounds
/// the number of probed files, the next run resumes from the stored markers
/// (unchanged files cost no budget) and row writes are flushed in batches —
/// one SaveChanges per FlushBatchSize files instead of two transactions per
/// file.
/// </summary>
public class EmbeddedTrackScanPipelineTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly CountingDbContext _dbContext;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lingarr-emb-scan-" + Guid.NewGuid().ToString("N"));
    private readonly Mock<ISettingService> _settings = new();
    private readonly List<string> _probedPaths = [];

    public EmbeddedTrackScanPipelineTests()
    {
        Directory.CreateDirectory(_root);
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _dbContext = new CountingDbContext(new DbContextOptionsBuilder<LingarrDbContext>()
            .UseSqlite(_connection)
            .Options);
        _dbContext.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        _connection.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    /// <summary>Counts SaveChanges calls — the acceptance bound for batched flushes.</summary>
    private sealed class CountingDbContext : LingarrDbContext
    {
        public int SaveChangesCalls;

        public CountingDbContext(DbContextOptions options) : base(options)
        {
        }

        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            SaveChangesCalls++;
            return base.SaveChangesAsync(cancellationToken);
        }
    }

    private EmbeddedTrackService CreateService() => new(
        _dbContext,
        NullLogger<EmbeddedTrackService>.Instance,
        settingService: _settings.Object,
        ffprobeRunner: (filePath, _) =>
        {
            _probedPaths.Add(filePath);
            return Task.FromResult("""{ "streams": [ { "index": 2, "codec_name": "subrip", "tags": { "language": "chi" } } ] }""");
        });

    private void SetBatchLimit(string? value) =>
        _settings
            .Setup(s => s.GetSetting(SettingKeys.Automation.EmbeddedScanBatchLimit))
            .ReturnsAsync(value);

    private async Task<List<Movie>> SeedMoviesAsync(int count)
    {
        var movies = new List<Movie>();
        for (var i = 1; i <= count; i++)
        {
            var fileName = $"movie{i}.mkv";
            File.WriteAllBytes(Path.Combine(_root, fileName), new byte[16]);
            movies.Add(new Movie
            {
                Id = i,
                RadarrId = i,
                Title = $"movie {i}",
                Path = _root,
                FileName = fileName,
                DateAdded = DateTimeOffset.UtcNow
            });
        }

        _dbContext.Movies.AddRange(movies);
        await _dbContext.SaveChangesAsync();
        return movies;
    }

    private async Task<int> CountMarkerRowsAsync() =>
        await _dbContext.MediaSubtitleTracks
            .CountAsync(track => track.StreamIndex == MediaSubtitleTrack.ScanMarkerStreamIndex);

    // ── batch limit + resume ────────────────────────────────────────

    [Fact]
    public async Task BatchLimit_ProbesAtMostNFilesPerRun_AndResumesOnTheNext()
    {
        await SeedMoviesAsync(3);
        SetBatchLimit("2");

        var first = await CreateService().ScanAll();

        Assert.Equal(3, first.Total);
        Assert.Equal(2, first.Probed);
        Assert.Equal(2, await CountMarkerRowsAsync());
        // deterministic resume order: lowest ids first
        Assert.Equal(
            [Path.Combine(_root, "movie1.mkv"), Path.Combine(_root, "movie2.mkv")],
            _probedPaths);

        var second = await CreateService().ScanAll();

        Assert.Equal(1, second.Probed);
        Assert.Equal(2, second.Skipped); // the first run's files are free now
        Assert.Equal(3, await CountMarkerRowsAsync());
        Assert.Equal(Path.Combine(_root, "movie3.mkv"), _probedPaths[^1]);
    }

    [Fact]
    public async Task BatchLimit_MissingOrInvalidSetting_FallsBackToTheCodeDefault()
    {
        Assert.Equal(500, SettingKeys.Automation.DefaultEmbeddedScanBatchLimit);
        await SeedMoviesAsync(3);
        SetBatchLimit("not-a-number");

        var summary = await CreateService().ScanAll();

        Assert.Equal(3, summary.Probed);

        // blank behaves the same as absent
        SetBatchLimit("   ");
        _probedPaths.Clear();
        await _dbContext.MediaSubtitleTracks.ExecuteDeleteAsync();
        await CreateService().ScanAll();
        Assert.Equal(3, _probedPaths.Count);
    }

    [Fact]
    public async Task UnchangedFiles_CostNoBatchBudget()
    {
        await SeedMoviesAsync(3);
        SetBatchLimit("3");
        await CreateService().ScanAll();
        Assert.Equal(3, await CountMarkerRowsAsync());

        // a run with budget 1 must still verify all three files (marker
        // skips are free), otherwise resume would stall.
        _probedPaths.Clear();
        SetBatchLimit("1");
        var summary = await CreateService().ScanAll();

        Assert.Equal(0, summary.Probed);
        Assert.Equal(3, summary.Skipped);
        Assert.Empty(_probedPaths);
    }

    // ── batched writes ─────────────────────────────────────────────

    [Fact]
    public async Task RowWrites_AreFlushedInBatches_NotPerFile()
    {
        await SeedMoviesAsync(3);
        _dbContext.SaveChangesCalls = 0; // seeding's save is not the scan's
        SetBatchLimit(null);

        var summary = await CreateService().ScanAll();

        Assert.Equal(3, summary.Probed);
        // old pipeline: 2 write transactions per file. Now: one SaveChanges
        // per FlushBatchSize files plus the closing flush — 3 files in one
        // batch means a single SaveChanges.
        var bound = (int)Math.Ceiling(summary.Probed / (double)EmbeddedTrackService.FlushBatchSize);
        Assert.Equal(1, _dbContext.SaveChangesCalls);
        Assert.True(_dbContext.SaveChangesCalls <= bound);

        // every scanned file ends up with its track rows + marker
        Assert.Equal(6, await _dbContext.MediaSubtitleTracks.CountAsync());
    }

    [Fact]
    public async Task ForceRescan_ReplacesRowsWithoutDuplicates()
    {
        await SeedMoviesAsync(1);
        var service = CreateService();
        await service.ScanAll();
        await service.ScanMedia(MediaType.Movie, 1, force: true);

        Assert.Equal(2, _probedPaths.Count);
        Assert.Equal(2, await _dbContext.MediaSubtitleTracks.CountAsync());
    }

    [Fact]
    public async Task MissingMediaFile_RecordsMissingMarker()
    {
        _dbContext.Movies.Add(new Movie
        {
            Id = 1,
            RadarrId = 1,
            Title = "gone",
            Path = _root,
            FileName = "gone.mkv", // never written to disk
            DateAdded = DateTimeOffset.UtcNow
        });
        await _dbContext.SaveChangesAsync();

        var summary = await CreateService().ScanAll();

        Assert.Equal(1, summary.Missing);
        Assert.Empty(_probedPaths);
        var marker = await _dbContext.MediaSubtitleTracks.SingleAsync();
        Assert.Equal(MediaSubtitleTrack.ScanMarkerStreamIndex, marker.StreamIndex);
        Assert.Equal(MediaSubtitleTrack.MissingFileHash, marker.MediaHash);
    }
}
