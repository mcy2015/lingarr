using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Lingarr.Core.Configuration;
using Lingarr.Core.Data;
using Lingarr.Core.Entities;
using Lingarr.Server.Modules.Subtitle;
using Lingarr.Server.Modules.Subtitle.PostProcessing;
using Lingarr.Server.Tests.Modules.SubtitleSearch;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Lingarr.Server.Tests.Modules.Subtitle.PostProcessing;

/// <summary>
/// Existing-library variant batch contracts (B01): the preview never writes
/// (bytes and mtimes untouched) while counting exactly the files the mode
/// would rewrite; execution converts only files whose parsed text actually
/// changes, is idempotent across runs, reports per-file failures without
/// sinking the batch, scans show roots (falling back to episode directories
/// for shows without one) and refuses over the 500-file cap. Real SQLite
/// because the scan roots come from the library tables; real files in a
/// per-test temp tree.
/// </summary>
public sealed class SubtitleVariantBatchServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly LingarrDbContext _dbContext;
    private readonly InMemorySettingService _settings = new();
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "lingarr-variant-batch-" + Guid.NewGuid().ToString("N"));

    private readonly List<ISubtitlePostProcessor> _stages = [];

    public SubtitleVariantBatchServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        // Snake-case naming: the library tables are queried exactly as the
        // production schema names them.
        _dbContext = new BatchDbContext(new DbContextOptionsBuilder<LingarrDbContext>()
            .UseSqlite(_connection)
            .UseSnakeCaseNamingConvention()
            .Options);
        _dbContext.Database.EnsureCreated();
        Directory.CreateDirectory(_root);

        _stages.Add(new ChineseVariantStage(
            new SubtitleEncodingService(), NullLogger<ChineseVariantStage>.Instance));
    }

    private SubtitleVariantBatchService CreateService() => new(
        _dbContext,
        _settings,
        new CompositePostProcessor(_stages, NullLogger<CompositePostProcessor>.Instance),
        NullLogger<SubtitleVariantBatchService>.Instance);

    /// <summary>
    /// Seeds one movie directory (row + folder) holding the named subtitle
    /// files, the way MovieSync writes it (Movie.Path = directory).
    /// </summary>
    private string SeedMovie(params (string Name, string Content)[] files)
    {
        var directory = Path.Combine(_root, "movie-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(directory);
        foreach (var (name, content) in files)
        {
            File.WriteAllText(Path.Combine(directory, name), content);
        }

        _dbContext.Movies.Add(new Movie
        {
            RadarrId = Random.Shared.Next(1000, 9999),
            Title = "Movie " + directory[^6..],
            Path = directory,
            FileName = "movie.mkv",
            DateAdded = DateTimeOffset.UtcNow
        });
        _dbContext.SaveChanges();
        return directory;
    }

    private const string TraditionalSrt =
        "1\n00:00:01,000 --> 00:00:02,000\n繁體中文字幕測試\n";
    private const string EnglishSrt =
        "1\n00:00:01,000 --> 00:00:02,000\nHello world\n";
    private const string SimplifiedSrt =
        "1\n00:00:01,000 --> 00:00:02,000\n简体中文字幕测试\n";

    private static Dictionary<string, (byte[] Bytes, DateTime Mtime)> Snapshot(string directory) =>
        Directory.GetFiles(directory).ToDictionary(
            file => file,
            file => (File.ReadAllBytes(file), File.GetLastWriteTimeUtc(file)));

    private static void AssertUnchanged(Dictionary<string, (byte[] Bytes, DateTime Mtime)> before, string directory)
    {
        var after = Snapshot(directory);
        foreach (var (path, snapshot) in before)
        {
            Assert.Equal(snapshot.Bytes, after[path].Bytes);
            Assert.Equal(snapshot.Mtime, after[path].Mtime);
        }
    }

    [Fact]
    public async Task Preview_ModeNoneOrMissing_Refuses()
    {
        var service = CreateService();
        SeedMovie(("movie.chs.srt", TraditionalSrt));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.PreviewAsync(CancellationToken.None));

        await _settings.SetSetting(SettingKeys.SubtitleSearch.PostProcessChineseVariant, "none");
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.PreviewAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Preview_CountsWouldChange_WithoutTouchingDisk()
    {
        await _settings.SetSetting(SettingKeys.SubtitleSearch.PostProcessChineseVariant, "simplified");
        var directory = SeedMovie(
            ("movie.chs.srt", TraditionalSrt),
            ("movie.en.srt", EnglishSrt),
            ("movie.zh.srt", SimplifiedSrt));
        var before = Snapshot(directory);

        var preview = await CreateService().PreviewAsync(CancellationToken.None);

        Assert.Equal("simplified", preview.Mode);
        Assert.Equal(3, preview.Scanned);
        Assert.Equal(1, preview.WouldChange);
        AssertUnchanged(before, directory);
    }

    [Fact]
    public async Task Execute_ConvertsOnlyFilesThatActuallyChange()
    {
        await _settings.SetSetting(SettingKeys.SubtitleSearch.PostProcessChineseVariant, "simplified");
        var directory = SeedMovie(
            ("movie.chs.srt", TraditionalSrt),
            ("movie.en.srt", EnglishSrt),
            ("movie.zh.srt", SimplifiedSrt));
        var englishBefore = File.ReadAllBytes(Path.Combine(directory, "movie.en.srt"));
        var englishMtime = File.GetLastWriteTimeUtc(Path.Combine(directory, "movie.en.srt"));
        var simplifiedBefore = File.ReadAllBytes(Path.Combine(directory, "movie.zh.srt"));
        var simplifiedMtime = File.GetLastWriteTimeUtc(Path.Combine(directory, "movie.zh.srt"));

        var result = await CreateService().ExecuteAsync(CancellationToken.None);

        Assert.Equal(3, result.Scanned);
        Assert.Equal(1, result.Converted);
        Assert.Equal(2, result.Skipped);
        Assert.Empty(result.Failed);

        Assert.Contains("繁体中文字幕测试", File.ReadAllText(Path.Combine(directory, "movie.chs.srt")));
        // Files with nothing to convert keep their bytes AND mtime.
        Assert.Equal(englishBefore, File.ReadAllBytes(Path.Combine(directory, "movie.en.srt")));
        Assert.Equal(englishMtime, File.GetLastWriteTimeUtc(Path.Combine(directory, "movie.en.srt")));
        Assert.Equal(simplifiedBefore, File.ReadAllBytes(Path.Combine(directory, "movie.zh.srt")));
        Assert.Equal(simplifiedMtime, File.GetLastWriteTimeUtc(Path.Combine(directory, "movie.zh.srt")));
    }

    [Fact]
    public async Task Execute_IsIdempotentAcrossRuns()
    {
        await _settings.SetSetting(SettingKeys.SubtitleSearch.PostProcessChineseVariant, "simplified");
        SeedMovie(("movie.chs.srt", TraditionalSrt));

        var first = await CreateService().ExecuteAsync(CancellationToken.None);
        var second = await CreateService().ExecuteAsync(CancellationToken.None);

        Assert.Equal(1, first.Converted);
        Assert.Equal(0, second.Converted);
        Assert.Equal(1, second.Skipped);
    }

    [Fact]
    public async Task Scan_ShowWithoutPath_FallsBackToEpisodeDirectories()
    {
        await _settings.SetSetting(SettingKeys.SubtitleSearch.PostProcessChineseVariant, "simplified");
        // EpisodeSync write side: Show.Path may be empty while Episode.Path
        // holds the episode's directory — the scan must reach it.
        var episodeDirectory = Path.Combine(_root, "show-no-path");
        Directory.CreateDirectory(episodeDirectory);
        File.WriteAllText(Path.Combine(episodeDirectory, "show.s01e01.chs.srt"), TraditionalSrt);

        var show = new Show
        {
            SonarrId = 91,
            Title = "Pathless Show",
            Path = null,
            DateAdded = DateTimeOffset.UtcNow
        };
        var season = new Season { SeasonNumber = 1, Path = null, Show = show };
        _dbContext.Episodes.Add(new Episode
        {
            SonarrId = 9101,
            EpisodeNumber = 1,
            Title = "Episode 1",
            FileName = "show.s01e01.mkv",
            Path = episodeDirectory,
            Season = season,
            DateAdded = DateTimeOffset.UtcNow
        });
        _dbContext.SaveChanges();

        var preview = await CreateService().PreviewAsync(CancellationToken.None);

        Assert.Equal(1, preview.Scanned);
        Assert.Equal(1, preview.WouldChange);
    }

    [Fact]
    public async Task Scan_OverCap_RefusesInsteadOfTruncating()
    {
        await _settings.SetSetting(SettingKeys.SubtitleSearch.PostProcessChineseVariant, "simplified");
        var directory = Path.Combine(_root, "big-library");
        Directory.CreateDirectory(directory);
        for (var index = 0; index < 501; index++)
        {
            File.WriteAllText(Path.Combine(directory, $"ep{index:D3}.srt"), EnglishSrt);
        }

        _dbContext.Movies.Add(new Movie
        {
            RadarrId = 99,
            Title = "Big Library",
            Path = directory,
            FileName = "big.mkv",
            DateAdded = DateTimeOffset.UtcNow
        });
        _dbContext.SaveChanges();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreateService().PreviewAsync(CancellationToken.None));
        Assert.Contains("500", ex.Message);
    }

    /// <summary>A stage that fails for exactly one path — the per-file failure case.</summary>
    private sealed class ThrowingStage(string targetPath) : ISubtitlePostProcessor
    {
        public string Name => "Thrower";

        public Task<PostProcessResult> ProcessAsync(
            string filePath, PostProcessContext context, CancellationToken cancellationToken)
        {
            if (filePath == targetPath)
            {
                throw new IOException("file locked by another process");
            }

            return Task.FromResult(PostProcessResult.Unchanged("not my file"));
        }
    }

    [Fact]
    public async Task Execute_PerFileFailure_ReportsStageNameWithoutSinkingTheBatch()
    {
        await _settings.SetSetting(SettingKeys.SubtitleSearch.PostProcessChineseVariant, "simplified");
        var directory = SeedMovie(
            ("movie.chs.srt", TraditionalSrt),
            ("movie.en.srt", EnglishSrt));

        _stages.Insert(0, new ThrowingStage(Path.Combine(directory, "movie.chs.srt")));
        var result = await CreateService().ExecuteAsync(CancellationToken.None);

        Assert.Equal(2, result.Scanned);
        Assert.Equal(1, result.Skipped);
        Assert.Single(result.Failed);
        Assert.Equal(Path.Combine(directory, "movie.chs.srt"), result.Failed[0].Path);
        Assert.Contains("Thrower", result.Failed[0].Error);
        // The composite contract: one stage's failure never blocks the next —
        // the variant stage behind the thrower still converted the file.
        Assert.Contains("繁体中文字幕测试", File.ReadAllText(Path.Combine(directory, "movie.chs.srt")));
    }

    private sealed class BatchDbContext(DbContextOptions options) : LingarrDbContext(options);

    public void Dispose()
    {
        _dbContext.Dispose();
        _connection.Dispose();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Temp cleanup is best-effort.
        }
    }
}
