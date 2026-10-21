using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Threading;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Lingarr.Core.Data;
using Lingarr.Core.Entities;
using Lingarr.Core.Enum;
using Lingarr.Server.Models;
using Lingarr.Server.Models.FileSystem;
using Lingarr.Server.Modules.Infrastructure;
using Lingarr.Server.Modules.Settings;
using Lingarr.Server.Modules.Statistics;
using Lingarr.Server.Modules.Subtitle;
using Lingarr.Server.Modules.Translation;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Lingarr.Server.Tests.Modules.Statistics;

/// <summary>
/// Audit S3 statistics-chain coverage: the season scan runs once per season
/// (audit issue #2), the aggregation loads projections instead of full
/// entities (P1-11) and writes only changed coverage values, committed in
/// chunked closing transactions (QA4 P2-2), with result values equivalent
/// to the tracked-entity era. Runs on a real SQLite schema because
/// ExecuteUpdate needs a relational provider.
/// </summary>
public class StatisticsJobTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly List<string> _sqlLog = [];
    private readonly CommitCountingInterceptor _commitCounter = new();
    private readonly StatisticsDbContext _dbContext;
    private readonly Mock<IScheduleService> _scheduleService = new();
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lingarr-stats-" + Guid.NewGuid().ToString("N"));

    public StatisticsJobTests()
    {
        Directory.CreateDirectory(_root);
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _dbContext = new StatisticsDbContext(new DbContextOptionsBuilder<LingarrDbContext>()
            .UseSqlite(_connection)
            .AddInterceptors(new RecordingInterceptor(_sqlLog), _commitCounter)
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

    private sealed class StatisticsDbContext(DbContextOptions options) : LingarrDbContext(options);

    /// <summary>
    /// QA4 P2-2: counts committed transactions — the observable proof that
    /// the closing backfill statements are committed in chunks instead of
    /// one giant transaction.
    /// </summary>
    private sealed class CommitCountingInterceptor : DbTransactionInterceptor
    {
        public int Commits;

        public override void TransactionCommitted(
            DbTransaction transaction,
            TransactionEndEventData eventData)
        {
            Commits++;
            base.TransactionCommitted(transaction, eventData);
        }

        public override Task TransactionCommittedAsync(
            DbTransaction transaction,
            TransactionEndEventData eventData,
            CancellationToken cancellationToken = default)
        {
            Commits++;
            return base.TransactionCommittedAsync(transaction, eventData, cancellationToken);
        }
    }

    private sealed class RecordingInterceptor(List<string> log) : DbCommandInterceptor
    {
        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result)
        {
            log.Add(command.CommandText);
            return base.ReaderExecuting(command, eventData, result);
        }

        // The async dispatch path (ToListAsync/ExecuteUpdateAsync) never calls
        // the sync hooks — DbCommandInterceptor's async defaults do not
        // forward — so both must log.
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            log.Add(command.CommandText);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override InterceptionResult<int> NonQueryExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result)
        {
            log.Add(command.CommandText);
            return base.NonQueryExecuting(command, eventData, result);
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            log.Add(command.CommandText);
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    private StatisticsJob CreateJob(
        ISubtitleService? subtitleService = null,
        ISettingService? settingService = null,
        int? finalizeChunkSize = null,
        int? incrementalScanBatchSize = null) => new(
        _dbContext,
        subtitleService ?? new SubtitleService(
            NullLogger<SubtitleService>.Instance,
            new LanguageCodeService(),
            new Mock<ISettingService>().Object),
        _scheduleService.Object,
        settingService ?? new Mock<ISettingService>().Object,
        NullLogger<StatisticsJob>.Instance,
        finalizeChunkSize,
        incrementalScanBatchSize);

    /// <summary>
    /// Setting service whose target languages are exactly the given codes —
    /// the target set of the subtitle-need rule.
    /// </summary>
    private static ISettingService TargetsService(params string[] codes)
    {
        var mock = new Mock<ISettingService>();
        mock
            .Setup(service => service.GetSettingAsJson<TargetLanguage>(It.IsAny<string>()))
            .ReturnsAsync(codes
                .Select(code => new TargetLanguage { Name = code, Code = code })
                .ToList());
        return mock.Object;
    }

    private string WriteSubtitleFile(string directory, string fileName, int bytes)
    {
        var path = Path.Combine(_root, directory, fileName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[bytes]);
        return path;
    }

    private async Task<(Movie movie, string dir)> SeedMovieAsync(
        int id,
        string[]? audioLanguages = null,
        bool? audioOverride = null)
    {
        var dir = $"movie{id}";
        Directory.CreateDirectory(Path.Combine(_root, dir));
        var movie = new Movie
        {
            Id = id,
            RadarrId = id,
            Title = $"movie {id}",
            Path = Path.Combine(_root, dir),
            FileName = $"movie{id}.mkv",
            DateAdded = DateTimeOffset.UtcNow,
            AudioLanguages = audioLanguages,
            AudioOverride = audioOverride
        };
        _dbContext.Movies.Add(movie);
        await _dbContext.SaveChangesAsync();
        return (movie, dir);
    }

    private async Task<Show> SeedShowAsync(int showId, params string[] seasonDirs)
    {
        var show = new Show
        {
            Id = showId,
            SonarrId = showId,
            Title = $"show {showId}",
            Path = Path.Combine(_root, $"show{showId}"),
            DateAdded = DateTimeOffset.UtcNow
        };
        var episodeId = showId * 100;
        foreach (var (index, dir) in seasonDirs.Select((d, i) => (i, d)))
        {
            var season = new Season
            {
                Id = showId * 10 + index,
                SeasonNumber = index + 1,
                Path = Path.Combine(_root, dir),
                ShowId = show.Id,
                Show = show
            };
            for (var ep = 1; ep <= 2; ep++)
            {
                var number = ep;
                season.Episodes.Add(new Episode
                {
                    Id = ++episodeId,
                    SonarrId = episodeId,
                    EpisodeNumber = number,
                    Title = $"s{index + 1}e{number}",
                    Path = season.Path,
                    FileName = $"show{showId}.s{index + 1:00}e{number:00}.mkv",
                    SeasonId = season.Id,
                    Season = season
                });
            }
            show.Seasons.Add(season);
        }

        _dbContext.Shows.Add(show);
        await _dbContext.SaveChangesAsync();
        return show;
    }

    /// <summary>
    /// Seeds a one-season show whose episodes carry the exact given file
    /// names — the QA4 P3 matching tests need versioned/duplicated names the
    /// standard seeding cannot express.
    /// </summary>
    private async Task<Show> SeedShowWithEpisodeFileNamesAsync(int showId, string seasonDir, params string[] fileNames)
    {
        var show = new Show
        {
            Id = showId,
            SonarrId = showId,
            Title = $"show {showId}",
            Path = Path.Combine(_root, $"show{showId}"),
            DateAdded = DateTimeOffset.UtcNow
        };
        var season = new Season
        {
            Id = showId * 10,
            SeasonNumber = 1,
            Path = Path.Combine(_root, seasonDir),
            ShowId = show.Id,
            Show = show
        };
        var episodeId = showId * 100;
        foreach (var fileName in fileNames)
        {
            season.Episodes.Add(new Episode
            {
                Id = ++episodeId,
                SonarrId = episodeId,
                EpisodeNumber = 1,
                Title = fileName,
                Path = season.Path,
                FileName = fileName,
                SeasonId = season.Id,
                Season = season
            });
        }

        show.Seasons.Add(season);
        _dbContext.Shows.Add(show);
        await _dbContext.SaveChangesAsync();
        return show;
    }

    private static bool IsCoverageUpdate(string sql) =>
        sql.StartsWith("UPDATE \"movies\"", StringComparison.OrdinalIgnoreCase)
        || sql.StartsWith("UPDATE \"shows\"", StringComparison.OrdinalIgnoreCase);

    // ── end-to-end values (equivalence with the tracked-entity era) ──

    [Fact]
    public async Task Execute_ComputesTotalsAndCoverage_FromRealFiles()
    {
        var (movie, movieDir) = await SeedMovieAsync(1);
        var zhBytes = 100;
        var enBytes = 40;
        WriteSubtitleFile(movieDir, "movie1.zh.srt", zhBytes);
        WriteSubtitleFile(movieDir, "movie1.en.srt", enBytes);

        var show = await SeedShowAsync(2, "show2/s01");
        var seasonDir = show.Seasons[0].Path!;
        var epBytes = 60;
        // matches episode 1's file name (base name prefix), not episode 2
        WriteSubtitleFile("show2/s01", "show2.s01e01.zh.srt", epBytes);

        await CreateJob().Execute();

        var stats = await _dbContext.Statistics.AsNoTracking().SingleAsync();
        Assert.Equal(1, stats.TotalMovies);
        Assert.Equal(2, stats.TotalEpisodes);
        Assert.Equal(3, stats.TotalSubtitles); // 2 movie files + 1 season file
        Assert.Equal(new Dictionary<string, int> { ["zh"] = 2, ["en"] = 1 }, stats.SubtitlesByLanguage);

        var storedMovie = await _dbContext.Movies.AsNoTracking().SingleAsync(m => m.Id == movie.Id);
        Assert.NotNull(storedMovie.SubtitleCoverage);
        Assert.Equal(1, storedMovie.SubtitleCoverage!["zh"].Count);
        Assert.Equal(zhBytes, storedMovie.SubtitleCoverage!["zh"].Bytes);
        Assert.Equal(1, storedMovie.SubtitleCoverage!["en"].Count);
        Assert.Equal(enBytes, storedMovie.SubtitleCoverage!["en"].Bytes);

        // Show coverage: one episode covered in zh, bytes of that file only.
        var storedShow = await _dbContext.Shows.AsNoTracking().SingleAsync(s => s.Id == show.Id);
        Assert.Equal(1, storedShow.SubtitleCoverage!["zh"].Count);
        Assert.Equal(epBytes, storedShow.SubtitleCoverage!["zh"].Bytes);
    }

    [Fact]
    public async Task Execute_SeasonWithoutPath_AndMovieWithoutPath_AreSkippedWithoutScan()
    {
        _dbContext.Movies.Add(new Movie
        {
            Id = 9,
            RadarrId = 9,
            Title = "no path",
            Path = null,
            FileName = null,
            DateAdded = DateTimeOffset.UtcNow,
            SubtitleCoverageJson = "{\"zh\":{\"count\":1,\"bytes\":1}}"
        });
        _dbContext.Shows.Add(new Show
        {
            Id = 8,
            SonarrId = 8,
            Title = "empty",
            Path = "/tv/empty",
            DateAdded = DateTimeOffset.UtcNow,
            Seasons =
            [
                new Season
                {
                    Id = 80,
                    SeasonNumber = 1,
                    Path = null,
                    ShowId = 8,
                    Show = null!,
                    Episodes = []
                }
            ]
        });
        await _dbContext.SaveChangesAsync();

        var subtitleService = new Mock<ISubtitleService>();
        subtitleService
            .Setup(s => s.GetAllSubtitles(It.IsAny<string>()))
            .ReturnsAsync(new List<Subtitles>());
        await CreateJob(subtitleService.Object).Execute();

        // a movie without a path has its stale coverage cleared
        var storedMovie = await _dbContext.Movies.AsNoTracking().SingleAsync(m => m.Id == 9);
        Assert.Null(storedMovie.SubtitleCoverageJson);
        // seasons without a path are never scanned
        subtitleService.Verify(s => s.GetAllSubtitles(It.IsAny<string>()), Times.Never);
    }

    // ── audit issue #2: one scan per season ─────────────────────────

    [Fact]
    public async Task Execute_ScansEachSeasonDirectory_ExactlyOnce()
    {
        var subtitleService = new Mock<ISubtitleService>();
        subtitleService
            .Setup(s => s.GetAllSubtitles(It.IsAny<string>()))
            .ReturnsAsync(new List<Subtitles>());
        await SeedShowAsync(3, "show3/s01", "show3/s02");

        await CreateJob(subtitleService.Object).Execute();

        subtitleService.Verify(s => s.GetAllSubtitles(It.IsAny<string>()), Times.Exactly(2));
        subtitleService.Verify(s => s.GetAllSubtitles(Path.Combine(_root, "show3/s01")), Times.Once);
        subtitleService.Verify(s => s.GetAllSubtitles(Path.Combine(_root, "show3/s02")), Times.Once);
    }

    [Fact]
    public async Task Execute_NestedSeasonPaths_CountFilesOnceButMergeEverySeason()
    {
        var subtitleService = new Mock<ISubtitleService>();
        // R13: the file must be attributable (episode-matching name) to be
        // counted — the aggregate reads the persisted snapshots, whose
        // per-language counts are attributed episodes.
        subtitleService
            .Setup(s => s.GetAllSubtitles(It.IsAny<string>()))
            .ReturnsAsync(
            [
                new Subtitles { Path = "/x/show4.s01e01.zh.srt", FileName = "show4.s01e01.zh", Language = "zh", Languages = ["zh"], Format = ".srt" }
            ]);

        // season 2's path starts with season 1's path → nested duplicate
        await SeedShowAsync(4, "show4", "show4/specials");

        await CreateJob(subtitleService.Object).Execute();

        var stats = await _dbContext.Statistics.AsNoTracking().SingleAsync();
        // two seasons scanned (coverage), but the file counted once globally
        Assert.Equal(1, stats.TotalSubtitles);
        Assert.Equal(1, stats.SubtitlesByLanguage["zh"]);
    }

    // ── P1-11: projections, not tracked entities ────────────────────

    [Fact]
    public async Task Execute_LoadsNoMediaEntitiesIntoTheChangeTracker()
    {
        await SeedMovieAsync(5);
        await SeedShowAsync(6, "show6/s01");
        _dbContext.ChangeTracker.Clear();

        await CreateJob().Execute();

        var trackedTypes = _dbContext.ChangeTracker.Entries()
            .Select(entry => entry.Entity.GetType())
            .ToList();
        Assert.DoesNotContain(typeof(Movie), trackedTypes);
        Assert.DoesNotContain(typeof(Show), trackedTypes);
        Assert.DoesNotContain(typeof(Season), trackedTypes);
        Assert.DoesNotContain(typeof(Episode), trackedTypes);
    }

    [Fact]
    public async Task Execute_MovieQuery_SelectsOnlyTheProjectedColumns()
    {
        await SeedMovieAsync(7);
        _sqlLog.Clear();

        await CreateJob().Execute();

        var movieSelect = _sqlLog.Single(sql =>
            sql.Contains("FROM \"movies\"", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("\"path\"", movieSelect, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"title\"", movieSelect, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"media_hash\"", movieSelect, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"date_added\"", movieSelect, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Execute_SecondRun_WritesNoCoverageUpdates()
    {
        var (movie, _) = await SeedMovieAsync(10);
        WriteSubtitleFile("movie10", "movie10.zh.srt", 20);
        var show = await SeedShowAsync(11, "show11/s01");
        WriteSubtitleFile("show11/s01", "show11.s01e01.zh.srt", 30);

        await CreateJob().Execute();
        var firstRunCoverageUpdates = _sqlLog.Count(IsCoverageUpdate);
        Assert.Equal(2, firstRunCoverageUpdates); // one movie + one show

        _sqlLog.Clear();
        await CreateJob().Execute();
        Assert.Equal(0, _sqlLog.Count(IsCoverageUpdate));
    }

    // ── historical semantics pinned ─────────────────────────────────

    [Fact]
    public async Task Execute_ShowCoverage_RecomputesFreshFromScannedSeasons()
    {
        // Coverage is recomputed from the scanned seasons only: stale stored
        // values never accumulate into the fresh scan (the tracked-entity
        // implementation merged into the persisted snapshot and grew the
        // counts on every run).
        var show = await SeedShowAsync(12, "show12/s01");
        show.SubtitleCoverageJson = "{\"zh\":{\"count\":5,\"bytes\":500}}";
        await _dbContext.SaveChangesAsync();

        WriteSubtitleFile("show12/s01", "show12.s01e01.zh.srt", 30);

        await CreateJob().Execute();

        var storedShow = await _dbContext.Shows.AsNoTracking().SingleAsync(s => s.Id == show.Id);
        Assert.Equal(1, storedShow.SubtitleCoverage!["zh"].Count);
        Assert.Equal(30, storedShow.SubtitleCoverage!["zh"].Bytes);
    }

    // ── subtitle-need rule (PRD card A / audit P0-5) ────────────────

    [Fact]
    public async Task Execute_MovieNeedsTranslation_CoversTheFourContractStates()
    {
        // none of these movies carry any subtitle file — the rule alone decides
        await SeedMovieAsync(20, audioLanguages: ["zh"]);                    // audio hits the target → exempt
        await SeedMovieAsync(21, audioLanguages: null);                     // audio unknown + missing zh → needs
        await SeedMovieAsync(22, audioLanguages: ["zh"], audioOverride: true);  // override=true beats zh audio
        await SeedMovieAsync(23, audioLanguages: null, audioOverride: false);  // override=false beats the gap

        await CreateJob(settingService: TargetsService("zh")).Execute();

        var flags = await _dbContext.Movies.AsNoTracking().ToDictionaryAsync(m => m.Id, m => m.NeedsTranslation);
        Assert.False(flags[20]);
        Assert.True(flags[21]);
        Assert.True(flags[22]);
        Assert.False(flags[23]);

        // idempotent: a second run with the same inputs flips nothing
        _sqlLog.Clear();
        await CreateJob(settingService: TargetsService("zh")).Execute();
        Assert.Equal(0, _sqlLog.Count(IsNeedsUpdate));
    }

    [Fact]
    public async Task Execute_EpisodeNeedsTranslation_IsJudgedPerEpisode()
    {
        var show = await SeedShowAsync(30, "show30/s01");
        var episodes = await _dbContext.Episodes
            .Where(e => e.Season.ShowId == show.Id)
            .OrderBy(e => e.EpisodeNumber)
            .ToListAsync();
        // episode 1 carries zh audio (exempt), episode 2's audio is unknown;
        // the one zh subtitle on disk matches episode 1 only
        episodes[0].AudioLanguages = ["zh"];
        episodes[1].AudioLanguages = null;
        await _dbContext.SaveChangesAsync();
        WriteSubtitleFile("show30/s01", "show30.s01e01.zh.srt", 10);

        await CreateJob(settingService: TargetsService("zh")).Execute();

        var flags = await _dbContext.Episodes.AsNoTracking()
            .Where(e => e.Season.ShowId == show.Id)
            .ToDictionaryAsync(e => e.EpisodeNumber, e => e.NeedsTranslation);
        Assert.False(flags[1]); // zh audio exempts even without subtitles
        Assert.True(flags[2]);  // unknown audio never exempts (conservative)
    }

    [Fact]
    public async Task Execute_FullCoverage_NeedsNothing_UnlessOverrideForcesIt()
    {
        // covered in the target → no need; covered but override forces it back
        await SeedMovieAsync(24);
        WriteSubtitleFile("movie24", "movie24.zh.srt", 12);
        await SeedMovieAsync(25, audioOverride: true);
        WriteSubtitleFile("movie25", "movie25.zh.srt", 12);

        await CreateJob(settingService: TargetsService("zh")).Execute();

        var flags = await _dbContext.Movies.AsNoTracking().ToDictionaryAsync(m => m.Id, m => m.NeedsTranslation);
        Assert.False(flags[24]);
        Assert.True(flags[25]);
    }

    private static bool IsNeedsUpdate(string sql) =>
        sql.StartsWith("UPDATE \"movies\"", StringComparison.OrdinalIgnoreCase)
        || sql.StartsWith("UPDATE \"episodes\"", StringComparison.OrdinalIgnoreCase);

    // ── QA4 P1-2: top-level exception handling ─────────────────────

    [Fact]
    public async Task Execute_UnhandledScanException_MarksFailedAndRethrows()
    {
        // The classic failure shape: a mid-scan IO exception surfacing out of
        // the subtitle collection. Before the guard this escaped Execute,
        // lost the whole run and left the job state stuck in Processing.
        // L3: the catch records the failure state and rethrows (Automatic-
        // Retry(0) — no retry storm) so Hangfire persists the Failed state
        // and the server exception filter logs the stack.
        var subtitleService = new Mock<ISubtitleService>();
        subtitleService
            .Setup(s => s.GetAllSubtitles(It.IsAny<string>()))
            .ThrowsAsync(new IOException("NAS dropped the share mid-enumeration"));
        await SeedMovieAsync(50);

        await Assert.ThrowsAsync<IOException>(() => CreateJob(subtitleService.Object).Execute());

        _scheduleService.Verify(s => s.UpdateJobState(It.IsAny<string>(), "Processing"), Times.Once);
        _scheduleService.Verify(s => s.UpdateJobState(It.IsAny<string>(), "Failed"), Times.Once);
        _scheduleService.Verify(s => s.UpdateJobState(It.IsAny<string>(), "Succeeded"), Times.Never);
        Assert.False(await _dbContext.Statistics.AnyAsync());
    }

    [Fact]
    public async Task Execute_SeasonScanFailure_MarksFailedAndRethrows()
    {
        var subtitleService = new Mock<ISubtitleService>();
        subtitleService
            .Setup(s => s.GetAllSubtitles(It.IsAny<string>()))
            .ThrowsAsync(new IOException("season directory vanished mid-scan"));
        await SeedShowAsync(51, "show51/s01");

        await Assert.ThrowsAsync<IOException>(() => CreateJob(subtitleService.Object).Execute());

        _scheduleService.Verify(s => s.UpdateJobState(It.IsAny<string>(), "Failed"), Times.Once);
        _scheduleService.Verify(s => s.UpdateJobState(It.IsAny<string>(), "Succeeded"), Times.Never);
    }

    // ── QA4 P2-2: chunked finalize transactions ────────────────────

    [Fact]
    public async Task FinalizeWrites_AreCommittedInChunks_BehaviorStaysEquivalent()
    {
        for (var id = 60; id <= 64; id++)
        {
            await SeedMovieAsync(id);
            WriteSubtitleFile($"movie{id}", $"movie{id}.zh.srt", 10 + id);
        }

        _commitCounter.Commits = 0; // seeding's implicit commits are not the job's
        await CreateJob(finalizeChunkSize: 2).Execute();

        // five backfill statements in chunks of two → three commits, not one
        // giant transaction (the old behavior) and not one per statement.
        Assert.Equal(3, _commitCounter.Commits);
        _scheduleService.Verify(s => s.UpdateJobState(It.IsAny<string>(), "Succeeded"), Times.Once);

        // the chunked outcome equals the old single-transaction outcome
        var stored = await _dbContext.Movies
            .AsNoTracking()
            .Where(movie => movie.Id >= 60 && movie.Id <= 64)
            .ToDictionaryAsync(movie => movie.Id);
        Assert.Equal(5, stored.Count);
        foreach (var movie in stored.Values)
        {
            Assert.NotNull(movie.SubtitleCoverage);
            Assert.Equal(1, movie.SubtitleCoverage!["zh"].Count);
        }

        var stats = await _dbContext.Statistics.AsNoTracking().SingleAsync();
        Assert.Equal(5, stats.TotalMovies);
        Assert.Equal(5, stats.TotalSubtitles);
    }

    [Fact]
    public async Task FinalizeWrites_WithoutBackfill_StillCommitTheStatisticsRow()
    {
        await SeedMovieAsync(65);
        await CreateJob().Execute();
        // force the zero-backfill branch: the media side is settled, and the
        // statistics row is the only thing left to write
        await _dbContext.Statistics.ExecuteDeleteAsync();
        _dbContext.ChangeTracker.Clear();
        _commitCounter.Commits = 0;

        await CreateJob().Execute();

        // zero backfill statements → no chunk transactions at all, but the
        // recreated statistics row is still persisted by the closing
        // SaveChanges of the zero-chunk branch
        var stats = await _dbContext.Statistics.AsNoTracking().SingleAsync();
        Assert.Equal(1, stats.TotalMovies);
        Assert.Equal(0, stats.TotalEpisodes);
    }

    // ── QA4 P3: episode file-name matching ─────────────────────────

    [Fact]
    public async Task MergeCoverage_VersionedEpisodeFile_AttributesToTheMostSpecificEpisode()
    {
        // a versioned release shares its base name with the plain episode;
        // the old FirstOrDefault always credited the plain one
        var show = await SeedShowWithEpisodeFileNamesAsync(
            80, "show80/s01", "show80.s01e01.mkv", "show80.s01e01.v2.mkv");
        WriteSubtitleFile("show80/s01", "show80.s01e01.v2.zh.srt", 25);

        await CreateJob(settingService: TargetsService("zh")).Execute();

        var episodes = await _dbContext.Episodes.AsNoTracking()
            .Where(episode => episode.Season.ShowId == show.Id)
            .OrderBy(episode => episode.Id)
            .ToListAsync();
        Assert.Equal(2, episodes.Count);
        // episodes[0] is the plain release (added first, lower id), episodes[1]
        // the versioned one: the subtitle belongs to the versioned file, so
        // that episode is covered and needs nothing while the plain one stays
        // uncovered — the old FirstOrDefault credited episodes[0] instead.
        Assert.Equal("show80.s01e01.mkv", episodes[0].FileName);
        Assert.True(episodes[0].NeedsTranslation);
        Assert.Equal("show80.s01e01.v2.mkv", episodes[1].FileName);
        Assert.False(episodes[1].NeedsTranslation);

        // coverage itself counts the file once, in zh
        var storedShow = await _dbContext.Shows.AsNoTracking().SingleAsync(s => s.Id == show.Id);
        Assert.Equal(1, storedShow.SubtitleCoverage!["zh"].Count);
        Assert.Equal(25, storedShow.SubtitleCoverage!["zh"].Bytes);
    }

    [Fact]
    public async Task MergeCoverage_AmbiguousDuplicateFileNames_StayAttributedToTheFirstEpisode()
    {
        // two episode entities sharing one file name (multi-version across
        // directories): names cannot separate them, the first stays the
        // attributed one — pinned as the deliberate behavior.
        var show = await SeedShowWithEpisodeFileNamesAsync(
            81, "show81/s01", "show81.s01e01.mkv", "show81.s01e01.mkv");
        WriteSubtitleFile("show81/s01", "show81.s01e01.zh.srt", 20);

        await CreateJob(settingService: TargetsService("zh")).Execute();

        var episodes = await _dbContext.Episodes.AsNoTracking()
            .Where(episode => episode.Season.ShowId == show.Id)
            .OrderBy(episode => episode.Id)
            .ToListAsync();
        Assert.False(episodes[0].NeedsTranslation); // first episode is credited
        Assert.True(episodes[1].NeedsTranslation);  // the duplicate is not

        var storedShow = await _dbContext.Shows.AsNoTracking().SingleAsync(s => s.Id == show.Id);
        Assert.Equal(1, storedShow.SubtitleCoverage!["zh"].Count);
    }

    // ── R13: watermark-driven incremental scanning ─────────────────

    [Fact]
    public async Task Execute_FreshWatermarks_ScanNothingAndAggregateFromSnapshots()
    {
        await SeedMovieAsync(90);
        WriteSubtitleFile("movie90", "movie90.zh.srt", 15);
        await SeedShowAsync(91, "show91/s01");
        WriteSubtitleFile("show91/s01", "show91.s01e01.zh.srt", 25);

        // bootstrap run: scans the library once and stamps the watermarks
        await CreateJob().Execute();

        var subtitleService = new Mock<ISubtitleService>();
        subtitleService
            .Setup(s => s.GetAllSubtitles(It.IsAny<string>()))
            .ReturnsAsync(new List<Subtitles>());
        _sqlLog.Clear();
        await CreateJob(subtitleService.Object).Execute();

        // steady state: fresh watermarks → zero disk scans, no media row
        // rewritten — the round's cost no longer depends on the library size
        subtitleService.Verify(s => s.GetAllSubtitles(It.IsAny<string>()), Times.Never);
        Assert.Equal(0, _sqlLog.Count(sql => IsCoverageUpdate(sql) || IsNeedsUpdate(sql)));

        // ... and the aggregate still comes out of the persisted snapshots
        var stats = await _dbContext.Statistics.AsNoTracking().SingleAsync();
        Assert.Equal(1, stats.TotalMovies);
        Assert.Equal(2, stats.TotalEpisodes);
        Assert.Equal(2, stats.TotalSubtitles); // movie zh incidence + episode zh incidence
        Assert.Equal(2, stats.SubtitlesByLanguage["zh"]);
    }

    [Fact]
    public async Task Execute_NewMediaFirst_ThenOldestWatermarks_WithinTheBatchLimit()
    {
        // one new movie (null watermark), four stale ones (30 days old)
        await SeedMovieAsync(100);
        WriteSubtitleFile("movie100", "movie100.zh.srt", 10);
        for (var id = 101; id <= 104; id++)
        {
            var (stale, _) = await SeedMovieAsync(id);
            stale.CoverageCheckedAt = DateTimeOffset.UtcNow.AddDays(-30);
        }
        await _dbContext.SaveChangesAsync();
        _dbContext.ChangeTracker.Clear();

        var subtitleService = new Mock<ISubtitleService>();
        subtitleService
            .Setup(s => s.GetAllSubtitles(It.IsAny<string>()))
            .ReturnsAsync(new List<Subtitles>());

        await CreateJob(subtitleService.Object, incrementalScanBatchSize: 2).Execute();

        // the new movie (null watermark first) plus the single oldest stale
        // one fill the budget of two; the rest stay untouched
        subtitleService.Verify(s => s.GetAllSubtitles(Path.Combine(_root, "movie100")), Times.Once);
        subtitleService.Verify(s => s.GetAllSubtitles(Path.Combine(_root, "movie101")), Times.Once);
        subtitleService.Verify(s => s.GetAllSubtitles(Path.Combine(_root, "movie102")), Times.Never);
        subtitleService.Verify(s => s.GetAllSubtitles(Path.Combine(_root, "movie104")), Times.Never);

        var watermarks = await _dbContext.Movies.AsNoTracking()
            .ToDictionaryAsync(movie => movie.Id, movie => movie.CoverageCheckedAt);
        Assert.NotNull(watermarks[100]);
        Assert.NotNull(watermarks[101]);
        // 102/104 were seeded with 30-day-old watermarks and stay untouched
        // this round — still stale, not rewritten, not nulled
        Assert.True(watermarks[102] < DateTimeOffset.UtcNow.AddDays(-29));
        Assert.True(watermarks[104] < DateTimeOffset.UtcNow.AddDays(-29));

        // the next round resumes exactly where this one stopped
        subtitleService.Invocations.Clear();
        await CreateJob(subtitleService.Object, incrementalScanBatchSize: 2).Execute();
        subtitleService.Verify(s => s.GetAllSubtitles(Path.Combine(_root, "movie102")), Times.Once);
        subtitleService.Verify(s => s.GetAllSubtitles(Path.Combine(_root, "movie103")), Times.Once);
        // movie100's watermark is fresh now — it is not rescanned
        subtitleService.Verify(s => s.GetAllSubtitles(Path.Combine(_root, "movie100")), Times.Never);
    }

    [Fact]
    public async Task Execute_FailedScan_WritesNoWatermark_MediaStaysEligible()
    {
        var (movie, _) = await SeedMovieAsync(110);
        WriteSubtitleFile("movie110", "movie110.zh.srt", 10);
        var (fresh, _) = await SeedMovieAsync(111);
        fresh.CoverageCheckedAt = DateTimeOffset.UtcNow;
        await _dbContext.SaveChangesAsync();
        _dbContext.ChangeTracker.Clear();

        var subtitleService = new Mock<ISubtitleService>();
        subtitleService
            .Setup(s => s.GetAllSubtitles(It.IsAny<string>()))
            .ThrowsAsync(new IOException("share dropped mid-scan"));
        // L3: failed rounds rethrow after recording (the R13 semantics —
        // items stay eligible — hold either way; see the comment below).
        await Assert.ThrowsAsync<IOException>(() => CreateJob(subtitleService.Object).Execute());

        _scheduleService.Verify(s => s.UpdateJobState(It.IsAny<string>(), "Failed"), Times.Once);
        // the failed round never reaches its closing statements: neither the
        // coverage values nor the watermark landed
        var afterFailure = await _dbContext.Movies.AsNoTracking().SingleAsync(m => m.Id == movie.Id);
        Assert.Null(afterFailure.CoverageCheckedAt);
        Assert.Null(afterFailure.SubtitleCoverageJson);

        // ... so the next round selects the same media again and succeeds
        await CreateJob().Execute();
        var afterRetry = await _dbContext.Movies.AsNoTracking().SingleAsync(m => m.Id == movie.Id);
        Assert.NotNull(afterRetry.CoverageCheckedAt);
        Assert.NotNull(afterRetry.SubtitleCoverageJson);
    }

    [Fact]
    public async Task Execute_TwoIncrementalRounds_MatchOneFullScan()
    {
        // a mixed library: two covered movies, an uncovered one, a show with
        // two attributed episode subtitles and one uncovered episode
        await SeedMovieAsync(120);
        WriteSubtitleFile("movie120", "movie120.zh.srt", 11);
        await SeedMovieAsync(121);
        WriteSubtitleFile("movie121", "movie121.en.srt", 12);
        await SeedMovieAsync(122);
        await SeedShowAsync(123, "show123/s01");
        WriteSubtitleFile("show123/s01", "show123.s01e01.zh.srt", 13);
        WriteSubtitleFile("show123/s01", "show123.s01e02.en.srt", 14);

        // two incremental rounds with a per-type budget of two
        await CreateJob(incrementalScanBatchSize: 2).Execute();
        await CreateJob(incrementalScanBatchSize: 2).Execute();

        var incrementalStats = await _dbContext.Statistics.AsNoTracking().SingleAsync();
        var incrementalMovieJson = await MovieCoverageJsonAsync(120, 122);
        var incrementalShowJson = await _dbContext.Shows.AsNoTracking()
            .Where(show => show.Id == 123)
            .Select(show => show.SubtitleCoverageJson)
            .SingleAsync();
        var incrementalDaily = await DailyCoverageRowsAsync();

        // wipe the watermarks → the next run takes the bootstrap full-scan path
        await _dbContext.Movies.ExecuteUpdateAsync(setters => setters
            .SetProperty(movie => movie.CoverageCheckedAt, (DateTimeOffset?)null));
        await _dbContext.Episodes.ExecuteUpdateAsync(setters => setters
            .SetProperty(episode => episode.CoverageCheckedAt, (DateTimeOffset?)null));
        _dbContext.ChangeTracker.Clear();

        await CreateJob(incrementalScanBatchSize: 2).Execute(); // budget ignored on the bootstrap path

        // R13 zero-drift pin: the incremental aggregate, per-media snapshots
        // and daily coverage rows equal the full scan's
        var fullStats = await _dbContext.Statistics.AsNoTracking().SingleAsync();
        Assert.Equal(fullStats.TotalMovies, incrementalStats.TotalMovies);
        Assert.Equal(fullStats.TotalEpisodes, incrementalStats.TotalEpisodes);
        Assert.Equal(fullStats.TotalSubtitles, incrementalStats.TotalSubtitles);
        Assert.Equal(fullStats.SubtitlesByLanguageJson, incrementalStats.SubtitlesByLanguageJson);
        Assert.Equal(await MovieCoverageJsonAsync(120, 122), incrementalMovieJson);
        Assert.Equal(
            await _dbContext.Shows.AsNoTracking()
                .Where(show => show.Id == 123)
                .Select(show => show.SubtitleCoverageJson)
                .SingleAsync(),
            incrementalShowJson);
        Assert.Equal(await DailyCoverageRowsAsync(), incrementalDaily);
    }

    [Fact]
    public async Task Execute_AllNullWatermarks_BootstrapScansEverythingDespiteTheLimit()
    {
        for (var id = 130; id <= 133; id++)
        {
            await SeedMovieAsync(id);
        }

        var subtitleService = new Mock<ISubtitleService>();
        subtitleService
            .Setup(s => s.GetAllSubtitles(It.IsAny<string>()))
            .ReturnsAsync(new List<Subtitles>());

        await CreateJob(subtitleService.Object, incrementalScanBatchSize: 1).Execute();

        // first-scan/migration path: every movie scanned in one run and stamped
        for (var id = 130; id <= 133; id++)
        {
            subtitleService.Verify(s => s.GetAllSubtitles(Path.Combine(_root, $"movie{id}")), Times.Once);
        }
        var watermarks = await _dbContext.Movies.AsNoTracking()
            .Where(movie => movie.Id >= 130 && movie.Id <= 133)
            .ToListAsync();
        Assert.True(watermarks.TrueForAll(movie => movie.CoverageCheckedAt != null));
    }

    [Fact]
    public async Task Execute_WritesCoverageDailyRows_AndUpsertsSameDayRuns()
    {
        await SeedMovieAsync(140);
        WriteSubtitleFile("movie140", "movie140.zh.srt", 10);
        await SeedShowAsync(141, "show141/s01");
        WriteSubtitleFile("show141/s01", "show141.s01e01.zh.srt", 20);

        await CreateJob(settingService: TargetsService("zh", "en")).Execute();
        // same UTC day → upsert, not a duplicate row
        await CreateJob(settingService: TargetsService("zh", "en")).Execute();

        var today = DateOnly.FromDateTime(DateTimeOffset.UtcNow.UtcDateTime);
        var rows = await _dbContext.CoverageDailies.AsNoTracking()
            .Where(row => row.Date == today)
            .ToListAsync();
        // one row per (media type, language); zero-covered targets are the
        // interesting state and always recorded
        Assert.Equal(4, rows.Count);
        Assert.Contains(rows, row => row.MediaType == MediaType.Movie && row.Language == "zh"
                                     && row.Covered == 1 && row.Total == 1);
        Assert.Contains(rows, row => row.MediaType == MediaType.Movie && row.Language == "en"
                                     && row.Covered == 0 && row.Total == 1);
        Assert.Contains(rows, row => row.MediaType == MediaType.Episode && row.Language == "zh"
                                     && row.Covered == 1 && row.Total == 2);
        Assert.Contains(rows, row => row.MediaType == MediaType.Episode && row.Language == "en"
                                     && row.Covered == 0 && row.Total == 2);
    }

    [Fact]
    public async Task Execute_UnmatchedSubtitleFile_CountsNoIncidenceButKeepsBytes()
    {
        var show = await SeedShowAsync(150, "show150/s01");
        WriteSubtitleFile("show150/s01", "stray.zh.srt", 30); // matches no episode file name

        await CreateJob().Execute();

        var stats = await _dbContext.Statistics.AsNoTracking().SingleAsync();
        // the incidence aggregate answers "which media items carry the
        // language" — a stray file carries bytes only, no coverage slot
        Assert.Equal(0, stats.TotalSubtitles);
        Assert.Empty(stats.SubtitlesByLanguage);

        var storedShow = await _dbContext.Shows.AsNoTracking().SingleAsync(s => s.Id == show.Id);
        Assert.Equal(30, storedShow.SubtitleCoverage!["zh"].Bytes);
        Assert.Equal(0, storedShow.SubtitleCoverage!["zh"].Count);
    }

    [Fact]
    public async Task Execute_FreshWatermarkMovie_RecomputesFlagsFromThePersistedSnapshot()
    {
        await SeedMovieAsync(160);
        WriteSubtitleFile("movie160", "movie160.zh.srt", 10);
        // bootstrap with no targets: needs = false, watermark stamped
        await CreateJob().Execute();
        var firstWatermark = (await _dbContext.Movies.AsNoTracking().SingleAsync(m => m.Id == 160))
            .CoverageCheckedAt;

        // target languages appear later; the movie's watermark is fresh so
        // the disk is not rescanned, but the flag follows the snapshot
        await CreateJob(settingService: TargetsService("ja")).Execute();

        var stored = await _dbContext.Movies.AsNoTracking().SingleAsync(m => m.Id == 160);
        Assert.True(stored.NeedsTranslation); // zh on disk, ja targeted → gap
        // the flag fix never claims a scan: the watermark keeps its age
        Assert.Equal(firstWatermark, stored.CoverageCheckedAt);
    }

    private async Task<List<string?>> MovieCoverageJsonAsync(int fromId, int toId) =>
        await _dbContext.Movies.AsNoTracking()
            .Where(movie => movie.Id >= fromId && movie.Id <= toId)
            .OrderBy(movie => movie.Id)
            .Select(movie => movie.SubtitleCoverageJson)
            .ToListAsync();

    private async Task<List<(DateOnly Date, MediaType MediaType, string Language, int Covered, int Total)>> DailyCoverageRowsAsync() =>
        (await _dbContext.CoverageDailies.AsNoTracking()
            .OrderBy(row => row.MediaType)
            .ThenBy(row => row.Language)
            .ToListAsync())
        .Select(row => (row.Date, row.MediaType, row.Language, row.Covered, row.Total))
        .ToList();
}