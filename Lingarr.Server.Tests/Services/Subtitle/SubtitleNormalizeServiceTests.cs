using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Lingarr.Core.Data;
using Lingarr.Core.Entities;
using Lingarr.Server.Models.Api;
using Lingarr.Server.Modules.Subtitle;
using Moq;
using Xunit;

namespace Lingarr.Server.Tests.Services.Subtitle;

/// <summary>
/// F3 batch normalize: real files in a temp directory, in-memory database.
/// Every preview assertion doubles as the safety proof — a skipped file is
/// never renamed, a planned file only after Execute.
/// </summary>
public class SubtitleNormalizeServiceTests : IDisposable
{
    private readonly string _root;
    private readonly LingarrDbContext _dbContext;
    private readonly Mock<ILogger<SubtitleNormalizeService>> _logger;
    private readonly SubtitleNormalizeService _service;

    public SubtitleNormalizeServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "lingarr-normalize-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _dbContext = new LingarrDbContext(new DbContextOptionsBuilder<LingarrDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        _logger = new Mock<ILogger<SubtitleNormalizeService>>();
        _service = new SubtitleNormalizeService(_dbContext, _logger.Object);
    }

    public void Dispose()
    {
        _dbContext.Database.EnsureDeleted();
        if (Directory.Exists(_root))
        {
            // A read-only-directory test may leave the tree non-writable.
            MakeWritable(_root);
            Directory.Delete(_root, recursive: true);
        }
    }

    private static void MakeWritable(string path)
    {
        foreach (var directory in Directory.EnumerateDirectories(path))
        {
            MakeWritable(directory);
        }

        try
        {
            var info = new DirectoryInfo(path);
            info.Attributes &= ~FileAttributes.ReadOnly;
        }
        catch
        {
            // best effort — Dispose must not throw
        }
    }

    private static void Chmod(string path, string mode)
    {
        var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "chmod",
            Arguments = $"{mode} \"{path}\"",
            RedirectStandardError = true,
            UseShellExecute = false
        });
        process?.WaitForExit(5000);
    }

    private static bool DirectoryIsEffectivelyWritable(string directory)
    {
        try
        {
            var probe = Path.Combine(directory, $".lingarr-writetest-{Guid.NewGuid():N}");
            File.WriteAllText(probe, string.Empty);
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private string Dir(params string[] segments) => Path.Combine([_root, .. segments]);

    private static void WriteFile(string path, string content = "1\n00:00:01,000 --> 00:00:02,000\nline\n")
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private async Task<(Movie movie, string directory)> SeedMovie(string? path = null)
    {
        var directory = Dir("movies", "dune");
        var moviePath = path ?? Path.Combine(directory, "Dune.2021.mkv");
        Directory.CreateDirectory(directory);
        // Production convention: Movie.Path is the directory holding the
        // video; the default seed also creates the video itself.
        if (path == null && !File.Exists(moviePath))
        {
            WriteFile(moviePath, "video-bytes");
        }
        var movie = new Movie
        {
            RadarrId = 1, Title = "Dune", Path = Path.GetDirectoryName(moviePath)!,
            FileName = Path.GetFileNameWithoutExtension(moviePath), DateAdded = DateTimeOffset.Now
        };
        _dbContext.Movies.Add(movie);
        await _dbContext.SaveChangesAsync();
        return (movie, directory);
    }

    private async Task<Show> SeedShow()
    {
        var showDirectory = Dir("tv", "shogun");
        var show = new Show
        {
            SonarrId = 1, Title = "Shōgun", Path = showDirectory, DateAdded = DateTimeOffset.Now
        };
        var season = new Season { SeasonNumber = 1, Show = show };
        show.Seasons.Add(season);
        season.Episodes.Add(new Episode
        {
            SonarrId = 11, EpisodeNumber = 1, Title = "Anjin",
            Path = Path.Combine(showDirectory, "Shogun.S01E01.mkv"),
            FileName = "Shogun.S01E01.mkv", Season = season
        });
        _dbContext.Shows.Add(show);
        await _dbContext.SaveChangesAsync();
        return show;
    }

    // ── preview · movies ──────────────────────────────────────────────

    [Fact]
    public async Task Preview_Movie_PlansBaseLanguageRename()
    {
        var (movie, directory) = await SeedMovie();
        WriteFile(Path.Combine(directory, "Dune.2021.cht.srt"));

        var preview = await _service.Preview("Movie", movie.Id);

        Assert.True(preview.Writable);
        Assert.Null(preview.Error);
        var item = Assert.Single(preview.Items);
        Assert.Equal("Dune.2021.cht.srt", item.OldName);
        Assert.Equal("Dune.2021.zh-TW.srt", item.NewName);
        Assert.Equal("zh-TW", item.Language);
        Assert.Equal("Dune", item.MediaTitle);
        Assert.Equal(item.OldPath, Path.Combine(directory, "Dune.2021.cht.srt"));
        Assert.Equal(item.NewPath, Path.Combine(directory, "Dune.2021.zh-TW.srt"));
        Assert.Empty(preview.Skipped);
        // Pure preview: nothing moved on disk.
        Assert.True(File.Exists(item.OldPath));
        Assert.False(File.Exists(item.NewPath));
    }

    [Fact]
    public async Task Preview_Movie_KeepsCaptionSegment()
    {
        var (movie, directory) = await SeedMovie();
        WriteFile(Path.Combine(directory, "Dune.2021.chs.sdh.ass"));

        var preview = await _service.Preview("Movie", movie.Id);

        var item = Assert.Single(preview.Items);
        Assert.Equal("Dune.2021.zh.sdh.ass", item.NewName);
        Assert.Equal("zh", item.Language);
    }

    [Fact]
    public async Task Preview_Movie_TwoVideosInDirectory_SkipsAmbiguous()
    {
        var (movie, directory) = await SeedMovie();
        WriteFile(Path.Combine(directory, "Dune.2021.part2.mkv"));
        WriteFile(Path.Combine(directory, "Dune.2021.chs.srt"));

        var preview = await _service.Preview("Movie", movie.Id);

        Assert.Empty(preview.Items);
        var skipped = Assert.Single(preview.Skipped);
        Assert.Equal(SubtitleNormalizeSkipReasons.AmbiguousVideo, skipped.Reason);
    }

    [Fact]
    public async Task Preview_Movie_SubtitleWithoutVideoInItsDirectory_Skips()
    {
        var (movie, directory) = await SeedMovie();
        WriteFile(Path.Combine(directory, "extras", "featurette.zh.ass"));

        var preview = await _service.Preview("Movie", movie.Id);

        Assert.Empty(preview.Items);
        var skipped = Assert.Single(preview.Skipped);
        Assert.Equal(SubtitleNormalizeSkipReasons.NoVideoInDirectory, skipped.Reason);
    }

    [Fact]
    public async Task Preview_Movie_DualLanguageTag_IsSkipped()
    {
        var (movie, directory) = await SeedMovie();
        WriteFile(Path.Combine(directory, "Dune.2021.scjp.srt"));
        WriteFile(Path.Combine(directory, "Dune.2021.简日.srt"));

        var preview = await _service.Preview("Movie", movie.Id);

        Assert.Empty(preview.Items);
        Assert.Equal(2, preview.Skipped.Count);
        Assert.All(preview.Skipped, skipped =>
            Assert.Equal(SubtitleNormalizeSkipReasons.DualLanguage, skipped.Reason));
    }

    [Fact]
    public async Task Preview_Movie_NoRecognizedLanguage_IsSkipped()
    {
        var (movie, directory) = await SeedMovie();
        WriteFile(Path.Combine(directory, "Dune.2021.random-name.srt"));

        var preview = await _service.Preview("Movie", movie.Id);

        Assert.Empty(preview.Items);
        var skipped = Assert.Single(preview.Skipped);
        Assert.Equal(SubtitleNormalizeSkipReasons.LanguageUnknown, skipped.Reason);
    }

    [Fact]
    public async Task Preview_Movie_TargetNameAlreadyOnDisk_IsSkipped()
    {
        var (movie, directory) = await SeedMovie();
        WriteFile(Path.Combine(directory, "Dune.2021.chs.srt"));
        WriteFile(Path.Combine(directory, "Dune.2021.zh.srt"));

        var preview = await _service.Preview("Movie", movie.Id);

        Assert.Empty(preview.Items);
        // The non-normalized source hits a target conflict; the existing
        // target itself is already normalized — both truths are reported.
        Assert.Equal(2, preview.Skipped.Count);
        Assert.Contains(preview.Skipped, s => s.Reason == SubtitleNormalizeSkipReasons.TargetConflict);
        Assert.Contains(preview.Skipped, s => s.Reason == SubtitleNormalizeSkipReasons.AlreadyNormalized);
        // The pre-existing target is untouched either way.
        Assert.True(File.Exists(Path.Combine(directory, "Dune.2021.zh.srt")));
    }

    [Fact]
    public async Task Preview_Movie_AlreadyNormalized_IsSkipped()
    {
        var (movie, directory) = await SeedMovie();
        WriteFile(Path.Combine(directory, "Dune.2021.zh.srt"));

        var preview = await _service.Preview("Movie", movie.Id);

        Assert.Empty(preview.Items);
        var skipped = Assert.Single(preview.Skipped);
        Assert.Equal(SubtitleNormalizeSkipReasons.AlreadyNormalized, skipped.Reason);
    }

    [Fact]
    public async Task Preview_Movie_TwoFilesClaimingSameNewName_BothSkipped()
    {
        var (movie, directory) = await SeedMovie();
        WriteFile(Path.Combine(directory, "Dune.2021.chs.srt"));
        WriteFile(Path.Combine(directory, "Dune.2021.sc.srt"));

        var preview = await _service.Preview("Movie", movie.Id);

        // chs and sc both canonicalise to zh — neither may win arbitrarily.
        Assert.Empty(preview.Items);
        Assert.Equal(2, preview.Skipped.Count);
        Assert.All(preview.Skipped, skipped =>
            Assert.Equal(SubtitleNormalizeSkipReasons.TargetCollisionInBatch, skipped.Reason));
    }

    [Fact]
    public async Task Preview_Movie_MissingDirectory_ReportsErrorState()
    {
        var (movie, _) = await SeedMovie(path: Dir("nowhere", "Dune.2021.mkv"));

        var preview = await _service.Preview("Movie", movie.Id);

        Assert.False(preview.Writable);
        Assert.Equal("missingDirectory", preview.Error);
        Assert.Empty(preview.Items);
        Assert.Empty(preview.Skipped);
    }

    [Fact]
    public async Task Preview_Movie_ReadOnlyDirectory_ReportsErrorState()
    {
        var (movie, directory) = await SeedMovie();
        WriteFile(Path.Combine(directory, "Dune.2021.chs.srt"));
        Chmod(directory, "555");
        try
        {
            // chmod cannot restrict root (tests run as root in containers) —
            // probe first and skip when the platform ignores the mode.
            if (DirectoryIsEffectivelyWritable(directory))
            {
                return;
            }

            var preview = await _service.Preview("Movie", movie.Id);

            Assert.False(preview.Writable);
            Assert.Equal("readOnly", preview.Error);
            Assert.Empty(preview.Items);
        }
        finally
        {
            Chmod(directory, "755");
        }
    }

    [Fact]
    public async Task Preview_UnknownMedia_ThrowsKeyNotFound()
    {
        await Assert.ThrowsAsync<KeyNotFoundException>(() => _service.Preview("Movie", 999));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => _service.Preview("Show", 999));
    }

    // ── preview · shows ───────────────────────────────────────────────

    [Fact]
    public async Task Preview_Show_EpisodeBaseNameMatch_PlansRename()
    {
        var show = await SeedShow();
        var showDirectory = Dir("tv", "shogun");
        WriteFile(Path.Combine(showDirectory, "Shogun.S01E01.chs.srt"));
        WriteFile(Path.Combine(showDirectory, "Omake.05.zh.srt"));

        var preview = await _service.Preview("Show", show.Id);

        var item = Assert.Single(preview.Items);
        Assert.Equal("Shogun.S01E01.chs.srt", item.OldName);
        Assert.Equal("Shogun.S01E01.zh.srt", item.NewName);
        Assert.Equal("Shōgun - S01E01 - Anjin", item.MediaTitle);

        var skipped = Assert.Single(preview.Skipped);
        Assert.Equal(SubtitleNormalizeSkipReasons.NoEpisodeMatch, skipped.Reason);
    }

    [Fact]
    public async Task Preview_Show_MultipleEpisodesMatch_IsAmbiguous()
    {
        var show = await SeedShow();
        var season = show.Seasons.Single();
        season.Episodes.Add(new Episode
        {
            SonarrId = 12, EpisodeNumber = 1, Title = "Anjin (duplicate)",
            Path = Path.Combine(Dir("tv", "shogun"), "Shogun.S01E01.mkv"),
            FileName = "Shogun.S01E01.mkv", Season = season
        });
        await _dbContext.SaveChangesAsync();
        WriteFile(Path.Combine(Dir("tv", "shogun"), "Shogun.S01E01.chs.srt"));

        var preview = await _service.Preview("Show", show.Id);

        Assert.Empty(preview.Items);
        var skipped = Assert.Single(preview.Skipped);
        Assert.Equal(SubtitleNormalizeSkipReasons.AmbiguousEpisodeMatch, skipped.Reason);
    }

    // ── execute ───────────────────────────────────────────────────────

    [Fact]
    public async Task Execute_ConfirmedItems_RenamesFilesAndLogsEach()
    {
        var (movie, directory) = await SeedMovie();
        WriteFile(Path.Combine(directory, "Dune.2021.chs.srt"));
        WriteFile(Path.Combine(directory, "Dune.2021.cht.ass"));
        var preview = await _service.Preview("Movie", movie.Id);
        Assert.Equal(2, preview.Items.Count);

        var result = await _service.Execute("Movie", movie.Id,
            preview.Items.Select(item => new SubtitleNormalizeExecuteItem
            {
                OldPath = item.OldPath,
                NewPath = item.NewPath,
                Language = item.Language,
                MediaTitle = item.MediaTitle
            }).ToList());

        Assert.Equal(2, result.Renamed);
        Assert.Empty(result.Failed);
        Assert.False(File.Exists(Path.Combine(directory, "Dune.2021.chs.srt")));
        Assert.True(File.Exists(Path.Combine(directory, "Dune.2021.zh.srt")));
        Assert.True(File.Exists(Path.Combine(directory, "Dune.2021.zh-TW.ass")));

        // One information log line per rename, visible on the logs page.
        _logger.Verify(
            logger => logger.Log(
                LogLevel.Information,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((state, _) =>
                    state.ToString()!.Contains("Dune.2021.chs.srt")
                    && state.ToString()!.Contains("Dune.2021.zh.srt")
                    && state.ToString()!.Contains("'zh'")
                    && state.ToString()!.Contains("'Dune'")),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
        _logger.Verify(
            logger => logger.Log(
                LogLevel.Information,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((state, _) =>
                    state.ToString()!.Contains("Dune.2021.cht.ass")
                    && state.ToString()!.Contains("Dune.2021.zh-TW.ass")),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    public async Task Execute_SourceDeletedBetweenPreviewAndExecute_FailsThatItemOnly()
    {
        var (movie, directory) = await SeedMovie();
        WriteFile(Path.Combine(directory, "Dune.2021.chs.srt"));
        WriteFile(Path.Combine(directory, "Dune.2021.eng.srt"));
        var preview = await _service.Preview("Movie", movie.Id);
        Assert.Equal(2, preview.Items.Count);

        // The first planned file vanishes before execution.
        File.Delete(preview.Items[0].OldPath);

        var result = await _service.Execute("Movie", movie.Id,
            preview.Items.Select(item => new SubtitleNormalizeExecuteItem
            {
                OldPath = item.OldPath,
                NewPath = item.NewPath,
                Language = item.Language,
                MediaTitle = item.MediaTitle
            }).ToList());

        Assert.Equal(1, result.Renamed);
        var failure = Assert.Single(result.Failed);
        Assert.Equal(preview.Items[0].OldPath, failure.Path);
        Assert.Contains("no longer exists", failure.Error);
    }

    [Fact]
    public async Task Execute_TargetAppearedBetweenPreviewAndExecute_FailsWithoutOverwriting()
    {
        var (movie, directory) = await SeedMovie();
        WriteFile(Path.Combine(directory, "Dune.2021.chs.srt"));
        var preview = await _service.Preview("Movie", movie.Id);
        WriteFile(Path.Combine(directory, "Dune.2021.zh.srt"), "occupied");

        var result = await _service.Execute("Movie", movie.Id,
        [
            new SubtitleNormalizeExecuteItem
            {
                OldPath = preview.Items[0].OldPath,
                NewPath = preview.Items[0].NewPath,
                Language = preview.Items[0].Language,
                MediaTitle = preview.Items[0].MediaTitle
            }
        ]);

        Assert.Equal(0, result.Renamed);
        var failure = Assert.Single(result.Failed);
        Assert.Contains("already exists", failure.Error);
        Assert.Equal("occupied", await File.ReadAllTextAsync(Path.Combine(directory, "Dune.2021.zh.srt")));
        Assert.True(File.Exists(Path.Combine(directory, "Dune.2021.chs.srt")));
    }

    [Fact]
    public async Task Execute_DuplicateTargetsInBatch_FailBeforeAnythingMoves()
    {
        var (movie, directory) = await SeedMovie();
        WriteFile(Path.Combine(directory, "Dune.2021.chs.srt"));
        WriteFile(Path.Combine(directory, "Dune.2021.sc.srt"));

        var result = await _service.Execute("Movie", movie.Id,
        [
            new SubtitleNormalizeExecuteItem
            {
                OldPath = Path.Combine(directory, "Dune.2021.chs.srt"),
                NewPath = Path.Combine(directory, "Dune.2021.zh.srt"),
                Language = "zh",
                MediaTitle = "Dune"
            },
            new SubtitleNormalizeExecuteItem
            {
                OldPath = Path.Combine(directory, "Dune.2021.sc.srt"),
                NewPath = Path.Combine(directory, "Dune.2021.zh.srt"),
                Language = "zh",
                MediaTitle = "Dune"
            }
        ]);

        Assert.Equal(0, result.Renamed);
        Assert.Equal(2, result.Failed.Count);
        Assert.All(result.Failed, failure =>
            Assert.Contains("same file name", failure.Error));
        Assert.True(File.Exists(Path.Combine(directory, "Dune.2021.chs.srt")));
        Assert.True(File.Exists(Path.Combine(directory, "Dune.2021.sc.srt")));
    }

    [Fact]
    public async Task Execute_TargetEscapingSourceDirectory_IsRejected()
    {
        var (movie, directory) = await SeedMovie();
        WriteFile(Path.Combine(directory, "Dune.2021.chs.srt"));

        // A target outside the source directory is caught by the
        // same-directory guard; File.Move would relocate the file.
        var result = await _service.Execute("Movie", movie.Id,
        [
            new SubtitleNormalizeExecuteItem
            {
                OldPath = Path.Combine(directory, "Dune.2021.chs.srt"),
                NewPath = Path.Combine(Dir(), "escaped.srt"),
                Language = "zh",
                MediaTitle = "Dune"
            }
        ]);

        Assert.Equal(0, result.Renamed);
        var failure = Assert.Single(result.Failed);
        Assert.Contains("source directory", failure.Error);
        Assert.False(File.Exists(Path.Combine(Dir(), "escaped.srt")));
        Assert.True(File.Exists(Path.Combine(directory, "Dune.2021.chs.srt")));
    }

    [Fact]
    public async Task Execute_EmptyItemList_IsANoOp()
    {
        var result = await _service.Execute("Movie", 1, []);

        Assert.Equal(0, result.Renamed);
        Assert.Empty(result.Failed);
    }
}
