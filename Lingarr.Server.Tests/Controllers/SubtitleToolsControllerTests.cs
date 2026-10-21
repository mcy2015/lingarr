using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Lingarr.Core.Data;
using Lingarr.Core.Entities;
using Lingarr.Server.Controllers;
using Lingarr.Server.Models.FileSystem;
using Lingarr.Server.Modules.Subtitle;
using Lingarr.Server.Modules.Subtitle.PostProcessing;
using Lingarr.Server.Modules.Subtitle.TimelineSync;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Lingarr.Server.Tests.Controllers;

/// <summary>
/// <see cref="SubtitleToolsController"/> contracts (B10): both endpoints
/// validate ownership of the subtitle path against the named media
/// (BilingualMerge precedent) before touching anything. The shift endpoint
/// moves every cue by exactly the requested offset through the B05 rewriter
/// (style layer untouched, backup gate honored); the variant endpoint runs
/// the B01 conversion core with the batch's no-change-is-200 semantics.
/// Real files in a per-test temp directory, real rewriter/stage instances.
/// </summary>
public sealed class SubtitleToolsControllerTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "lingarr-subtitle-tools-" + Guid.NewGuid().ToString("N"));

    private readonly SubtitleToolsController _controller;

    public SubtitleToolsControllerTests()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Path.Combine(_directory, "Movie.2023.mkv"), "video");
        File.WriteAllText(Path.Combine(_directory, "Movie.2023.zh.srt"),
            "1\n00:00:10,000 --> 00:00:12,000\n繁體中文字幕測試\n" +
            "2\n00:01:10,500 --> 00:01:12,750\n第二條\n", new UTF8Encoding(false));

        var options = new DbContextOptionsBuilder<LingarrDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var dbContext = new LingarrDbContext(options);
        dbContext.Movies.Add(new Movie
        {
            RadarrId = 1,
            Title = "Movie",
            FileName = "Movie.2023.mkv",
            Path = _directory,
            DateAdded = DateTimeOffset.UtcNow
        });
        dbContext.SaveChanges();

        _controller = new SubtitleToolsController(
            dbContext,
            new SubtitleTimelineRewriter(new SubtitleEncodingService()),
            new ChineseVariantStage(new SubtitleEncodingService(), NullLogger<ChineseVariantStage>.Instance));
    }

    private string SubtitlePath => Path.Combine(_directory, "Movie.2023.zh.srt");

    private static List<SubtitleItem> ParseSrt(string path)
    {
        using var input = new MemoryStream(File.ReadAllBytes(path));
        return new SrtParser().ParseStream(input, Encoding.UTF8);
    }

    // ── shift ────────────────────────────────────────────────────────

    [Fact]
    public async Task Shift_Plus500Ms_MovesEveryCueExactly()
    {
        var response = await _controller.Shift(
            new SubtitleShiftRequestBody("Movie", 1, SubtitlePath, 500), CancellationToken.None);

        var result = Assert.IsType<OkObjectResult>(response.Result);
        var body = Assert.IsType<SubtitleShiftResponse>(result.Value!);
        Assert.Equal(500, body.OffsetMs);
        Assert.Equal(2, body.Cues);
        Assert.Equal(0, body.ClampedCues);

        // Every cue moved by exactly +500ms — start AND end, cue text intact.
        var cues = ParseSrt(SubtitlePath);
        Assert.Equal(10_500, cues[0].StartTime);
        Assert.Equal(12_500, cues[0].EndTime);
        Assert.Equal(71_000, cues[1].StartTime);
        Assert.Equal(73_250, cues[1].EndTime);
        Assert.Contains("繁體中文字幕測試", cues[0].Lines);

        // The backup (the one-shift gate) landed with the original bytes.
        Assert.True(File.Exists(SubtitlePath + SubtitleTimelineRewriter.BackupSuffix));
    }

    [Fact]
    public async Task Shift_Minus500Ms_AssStyleLayerStaysByteForByte()
    {
        var styleBlock =
            "[V4+ Styles]\n" +
            "Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding\n" +
            "Style: Default,思源黑体,48,&H00FFFFFF,&H000000FF,&H00000000,&H7F000000,0,0,0,0,100,100,0,0,1,2.0,1,2,20,20,42,1\n";
        var content =
            "[Script Info]\n" +
            "Title: Original\n" +
            "ScriptType: v4.00+\n" +
            styleBlock +
            "[Events]\n" +
            "Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text\n" +
            "Dialogue: 0,0:00:10.00,0:00:12.00,Default,,0,0,0,,{\\fad(200,200)}樣式保留測試\n";
        var path = Path.Combine(_directory, "Movie.2023.zh.ass");
        File.WriteAllText(path, content, new UTF8Encoding(false));

        var response = await _controller.Shift(
            new SubtitleShiftRequestBody("Movie", 1, path, -500), CancellationToken.None);

        Assert.IsType<OkObjectResult>(response.Result);
        var rewritten = File.ReadAllText(path).Replace("\r\n", "\n");

        // Style layer and Dialogue text survive byte-for-byte; only the
        // times moved (10s − 0.5s → 9.5s in h:mm:ss.cs).
        Assert.Contains("Style: Default,思源黑体,48,&H00FFFFFF", rewritten);
        Assert.Contains("{\\fad(200,200)}樣式保留測試", rewritten);
        Assert.Contains("0:00:09.50,0:00:11.50", rewritten);
        Assert.DoesNotContain("0:00:10.00,0:00:12.00", rewritten);
    }

    [Fact]
    public async Task Shift_ZeroOffset_Returns400AndTouchesNothing()
    {
        var before = File.ReadAllBytes(SubtitlePath);

        var response = await _controller.Shift(
            new SubtitleShiftRequestBody("Movie", 1, SubtitlePath, 0), CancellationToken.None);

        var result = Assert.IsType<BadRequestObjectResult>(response.Result);
        Assert.Contains("non-zero", result.Value!.ToString());
        Assert.Equal(before, File.ReadAllBytes(SubtitlePath));
        Assert.False(File.Exists(SubtitlePath + SubtitleTimelineRewriter.BackupSuffix));
    }

    [Fact]
    public async Task Shift_ExistingBackup_Returns400WithRestoreHint()
    {
        File.WriteAllText(SubtitlePath + SubtitleTimelineRewriter.BackupSuffix, "old backup");
        var before = File.ReadAllText(SubtitlePath);

        var response = await _controller.Shift(
            new SubtitleShiftRequestBody("Movie", 1, SubtitlePath, 500), CancellationToken.None);

        var result = Assert.IsType<BadRequestObjectResult>(response.Result);
        Assert.Contains("restore", result.Value!.ToString());
        Assert.Equal(before, File.ReadAllText(SubtitlePath));
    }

    [Fact]
    public async Task Shift_UnsupportedFormat_Returns400()
    {
        var subPath = Path.Combine(_directory, "Movie.2023.zh.sub");
        File.WriteAllText(subPath, "1\n00:00:10,000 --> 00:00:12,000\nx\n");

        var response = await _controller.Shift(
            new SubtitleShiftRequestBody("Movie", 1, subPath, 500), CancellationToken.None);

        var result = Assert.IsType<BadRequestObjectResult>(response.Result);
        Assert.Contains("unsupported subtitle format", result.Value!.ToString());
    }

    [Fact]
    public async Task Shift_ForeignPath_Returns400()
    {
        var foreign = Path.Combine(Path.GetTempPath(), "elsewhere-" + Guid.NewGuid().ToString("N"), "Movie.2023.zh.srt");
        Directory.CreateDirectory(Path.GetDirectoryName(foreign)!);
        File.WriteAllText(foreign, "x");

        var response = await _controller.Shift(
            new SubtitleShiftRequestBody("Movie", 1, foreign, 500), CancellationToken.None);

        var result = Assert.IsType<BadRequestObjectResult>(response.Result);
        Assert.Contains("does not belong", result.Value!.ToString());
    }

    [Fact]
    public async Task Shift_UnparseableFile_Returns400AndLeavesBytesUntouched()
    {
        var broken = Path.Combine(_directory, "Movie.2023.zh2.srt");
        File.WriteAllText(broken, "not a subtitle at all");

        var response = await _controller.Shift(
            new SubtitleShiftRequestBody("Movie", 1, broken, 500), CancellationToken.None);

        var result = Assert.IsType<BadRequestObjectResult>(response.Result);
        Assert.Contains("could not shift", result.Value!.ToString());
        Assert.Equal("not a subtitle at all", File.ReadAllText(broken));
    }

    // ── variant ──────────────────────────────────────────────────────

    [Fact]
    public async Task Variant_SimplifiedMode_ConvertsTheFile()
    {
        // The mode names the TARGET variant; the fixture text is traditional.
        var response = await _controller.ConvertVariant(
            new SubtitleVariantRequestBody("Movie", 1, SubtitlePath, "simplified"), CancellationToken.None);

        var result = Assert.IsType<OkObjectResult>(response.Result);
        var body = Assert.IsType<SubtitleVariantResponse>(result.Value!);
        Assert.True(body.Changed);
        Assert.Contains("繁体中文字幕测试", File.ReadAllText(SubtitlePath));
    }

    [Fact]
    public async Task Variant_NoChangeContent_Is200NoChangeAndIdempotent()
    {
        // First run converts to simplified…
        var first = await _controller.ConvertVariant(
            new SubtitleVariantRequestBody("Movie", 1, SubtitlePath, "simplified"), CancellationToken.None);
        var firstBody = Assert.IsType<SubtitleVariantResponse>(Assert.IsType<OkObjectResult>(first.Result).Value!);
        Assert.True(firstBody.Changed);
        var converted = File.ReadAllBytes(SubtitlePath);

        // …the second run over converted text answers 200 changed=false and
        // leaves the bytes untouched (the batch's idempotence semantics).
        var second = await _controller.ConvertVariant(
            new SubtitleVariantRequestBody("Movie", 1, SubtitlePath, "simplified"), CancellationToken.None);
        var secondBody = Assert.IsType<SubtitleVariantResponse>(Assert.IsType<OkObjectResult>(second.Result).Value!);
        Assert.False(secondBody.Changed);
        Assert.NotNull(secondBody.Reason);
        Assert.Equal(converted, File.ReadAllBytes(SubtitlePath));
    }

    [Fact]
    public async Task Variant_NoChineseContent_Is200NoChange()
    {
        var english = Path.Combine(_directory, "Movie.2023.en.srt");
        File.WriteAllText(english, "1\n00:00:10,000 --> 00:00:12,000\nhello world\n");
        var before = File.ReadAllBytes(english);

        var response = await _controller.ConvertVariant(
            new SubtitleVariantRequestBody("Movie", 1, english, "simplified"), CancellationToken.None);

        var body = Assert.IsType<SubtitleVariantResponse>(Assert.IsType<OkObjectResult>(response.Result).Value!);
        Assert.False(body.Changed);
        Assert.Equal(before, File.ReadAllBytes(english));
    }

    [Fact]
    public async Task Variant_InvalidMode_Returns400()
    {
        var response = await _controller.ConvertVariant(
            new SubtitleVariantRequestBody("Movie", 1, SubtitlePath, "none"), CancellationToken.None);

        var result = Assert.IsType<BadRequestObjectResult>(response.Result);
        Assert.Contains("mode", result.Value!.ToString());
    }

    [Fact]
    public async Task Variant_PathOutsideTheMediaDirectory_Returns400()
    {
        var escape = Path.Combine(_directory, "..", "escape.zh.srt");

        var response = await _controller.ConvertVariant(
            new SubtitleVariantRequestBody("Movie", 1, escape, "simplified"), CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(response.Result);
    }

    [Fact]
    public async Task Variant_EpisodeMediaType_ResolvesTheEpisodeDirectory()
    {
        var options = new DbContextOptionsBuilder<LingarrDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var dbContext = new LingarrDbContext(options);
        var show = new Show
        {
            SonarrId = 1,
            Title = "Show",
            Path = _directory,
            DateAdded = DateTimeOffset.UtcNow
        };
        dbContext.Episodes.Add(new Episode
        {
            SonarrId = 1,
            EpisodeNumber = 1,
            Title = "Episode",
            FileName = "Movie.2023.mkv", // reuse the on-disk fixtures
            Path = _directory,
            Season = new Season { SeasonNumber = 1, Show = show },
            DateAdded = DateTimeOffset.UtcNow
        });
        dbContext.SaveChanges();
        var controller = new SubtitleToolsController(
            dbContext,
            new SubtitleTimelineRewriter(new SubtitleEncodingService()),
            new ChineseVariantStage(new SubtitleEncodingService(), NullLogger<ChineseVariantStage>.Instance));

        var response = await controller.ConvertVariant(
            new SubtitleVariantRequestBody("Episode", 1, SubtitlePath, "simplified"), CancellationToken.None);

        var body = Assert.IsType<SubtitleVariantResponse>(Assert.IsType<OkObjectResult>(response.Result).Value!);
        Assert.True(body.Changed);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Temp cleanup is best-effort.
        }
    }
}
