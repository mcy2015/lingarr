using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Lingarr.Server.Models.FileSystem;
using Lingarr.Server.Modules.Subtitle;
using Lingarr.Server.Modules.Subtitle.TimelineSync;
using Xunit;

namespace Lingarr.Server.Tests.Modules.Subtitle.TimelineSync;

/// <summary>
/// <see cref="SubtitleTimelineRewriter"/> contracts (B05): the cue-time
/// transform itself (scale + shift, negative clamping, duration repair),
/// the backup-before-write ordering, and format round-trips — the ASS
/// style layer survives byte-for-byte, VTT re-parses.
/// </summary>
public sealed class SubtitleTimelineRewriterTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "lingarr-timeline-rewriter-" + Guid.NewGuid().ToString("N"));

    private readonly SubtitleTimelineRewriter _rewriter = new(new SubtitleEncodingService());

    public SubtitleTimelineRewriterTests()
    {
        Directory.CreateDirectory(_directory);
    }

    private string WriteFile(string name, string content)
    {
        var path = Path.Combine(_directory, name);
        File.WriteAllText(path, content, new UTF8Encoding(false));
        return path;
    }

    [Fact]
    public async Task Rewrite_ShiftsEveryCue()
    {
        var path = WriteFile("movie.srt", "1\n00:00:10,000 --> 00:00:12,000\nhello\n");

        var result = await _rewriter.RewriteAsync(path, 1.0, -2.5, CancellationToken.None);

        Assert.Equal(1, result.Cues);
        Assert.Equal(0, result.ClampedCues);
        var cues = TimelineSrt(path);
        Assert.Equal(7_500, cues[0].StartTime);
        Assert.Equal(9_500, cues[0].EndTime);
    }

    [Fact]
    public async Task Rewrite_ScalesAndShifts()
    {
        var path = WriteFile("movie.srt", "1\n00:01:40,000 --> 00:01:44,000\nhello\n");

        await _rewriter.RewriteAsync(path, 25.0 / 23.976, 1.0, CancellationToken.None);

        var cues = TimelineSrt(path);
        // 25/23.976 = 1.0427100…: 100000ms × R + 1000 = 105271.0 → 105271;
        // end 104000ms × R + 1000 = 109441.8 → 109442.
        Assert.Equal(105_271, cues[0].StartTime);
        Assert.Equal(109_442, cues[0].EndTime);
    }

    [Fact]
    public async Task Rewrite_NegativeTimesClampToZeroWithRepairedDuration()
    {
        var path = WriteFile("movie.srt",
            "1\n00:00:01,000 --> 00:00:02,000\nearly\n" +
            "2\n00:00:05,000 --> 00:00:06,000\nlate\n");

        var result = await _rewriter.RewriteAsync(path, 1.0, -3.0, CancellationToken.None);

        Assert.Equal(1, result.ClampedCues);
        var cues = TimelineSrt(path);
        Assert.Equal(0, cues[0].StartTime);
        Assert.True(cues[0].EndTime > cues[0].StartTime, "clamped cue must keep a positive duration");
        Assert.Equal(2_000, cues[1].StartTime);
        Assert.Equal(3_000, cues[1].EndTime);
    }

    [Fact]
    public async Task Rewrite_CreatesBackupWithTheOriginalBytes()
    {
        var content = "1\n00:00:10,000 --> 00:00:12,000\nhello\n";
        var path = WriteFile("movie.srt", content);

        await _rewriter.RewriteAsync(path, 1.0, 1.0, CancellationToken.None);

        var backup = path + SubtitleTimelineRewriter.BackupSuffix;
        Assert.True(File.Exists(backup));
        Assert.Equal(content, File.ReadAllText(backup));
    }

    [Fact]
    public async Task Rewrite_BackupRefusesToOverwrite()
    {
        var path = WriteFile("movie.srt", "1\n00:00:10,000 --> 00:00:12,000\nhello\n");
        File.WriteAllText(path + SubtitleTimelineRewriter.BackupSuffix, "existing backup");

        // The rewrite itself may succeed up to the backup step; the copy
        // with overwrite:false must refuse rather than clobber the backup.
        await Assert.ThrowsAnyAsync<IOException>(
            () => _rewriter.RewriteAsync(path, 1.0, 1.0, CancellationToken.None));
        Assert.Equal("existing backup", File.ReadAllText(path + SubtitleTimelineRewriter.BackupSuffix));
    }

    [Fact]
    public async Task Rewrite_Ass_KeepsStyleLayerByteForByte()
    {
        var styleBlock =
            "[V4+ Styles]\n" +
            "Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding\n" +
            "Style: Default,思源黑体,48,&H00FFFFFF,&H000000FF,&H00000000,&H7F000000,0,0,0,0,100,100,0,0,1,2.0,1,2,20,20,42,1\n";
        var content =
            "[Script Info]\n" +
            "Title: Original\n" +
            "ScriptType: v4.00+\n" +
            "PlayResX: 1920\n" +
            "PlayResY: 1080\n" +
            styleBlock +
            "[Events]\n" +
            "Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text\n" +
            "Dialogue: 0,0:00:10.00,0:00:12.00,Default,,0,0,0,,{\fad(200,200)}样式保留测试\n";

        var path = WriteFile("movie.ass", content);

        var result = await _rewriter.RewriteAsync(path, 1.0, -2.5, CancellationToken.None);

        Assert.Equal(1, result.Cues);
        var rewritten = File.ReadAllText(path).Replace("\r\n", "\n");
        Assert.Contains("Style: Default,思源黑体,48,&H00FFFFFF", rewritten);
        Assert.Contains("{\fad(200,200)}样式保留测试", rewritten);
        Assert.Contains("PlayResX: 1920", rewritten);
        // 10s − 2.5s → 7.5s in h:mm:ss.cs
        Assert.Contains("0:00:07.50,0:00:09.50", rewritten);
    }

    [Fact]
    public async Task Rewrite_Vtt_RoundTrips()
    {
        var path = WriteFile("movie.vtt",
            "WEBVTT\n\n00:00:10.000 --> 00:00:12.000\nhello\n");

        await _rewriter.RewriteAsync(path, 1.0, 2.5, CancellationToken.None);

        var rewritten = File.ReadAllText(path);
        Assert.StartsWith("WEBVTT", rewritten);
        Assert.Contains("00:00:12.500 --> 00:00:14.500", rewritten);
    }

    [Fact]
    public void VideoBaseName_StripsOnlyKnownVideoExtensions()
    {
        Assert.Equal("Movie.2023.WEBDL-1080p", SubtitleTimelineRewriter.VideoBaseName("Movie.2023.WEBDL-1080p.mkv"));
        Assert.Equal("Show.S01E01", SubtitleTimelineRewriter.VideoBaseName("Show.S01E01.mp4"));
        // No known video extension: the WHOLE name is the base — scene dots
        // must never be eaten as extensions.
        Assert.Equal("Movie.2023.WEBDL-1080p", SubtitleTimelineRewriter.VideoBaseName("Movie.2023.WEBDL-1080p"));
    }

    private static List<SubtitleItem> TimelineSrt(string path)
    {
        using var input = new MemoryStream(File.ReadAllBytes(path));
        return new SrtParser().ParseStream(input, Encoding.UTF8);
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
