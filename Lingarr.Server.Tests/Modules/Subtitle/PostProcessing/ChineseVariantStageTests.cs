using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Lingarr.Server.Modules.Subtitle;
using Lingarr.Server.Modules.Subtitle.PostProcessing;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Lingarr.Server.Tests.Modules.Subtitle.PostProcessing;

/// <summary>
/// ChineseVariantStage contracts (B01): variant conversion round-trips
/// through the existing parsers/writers, the ASS style layer survives
/// byte-for-byte, mode "none" and no-op files keep their bytes AND mtime
/// untouched, previews never write, and unparseable files are skipped
/// without throwing. Real files in a per-test temp directory.
/// </summary>
public sealed class ChineseVariantStageTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "lingarr-variant-stage-" + Guid.NewGuid().ToString("N"));

    private readonly ChineseVariantStage _stage = new(
        new SubtitleEncodingService(), NullLogger<ChineseVariantStage>.Instance);

    public ChineseVariantStageTests()
    {
        Directory.CreateDirectory(_directory);
    }

    private string WriteFile(string name, string content)
    {
        var path = Path.Combine(_directory, name);
        File.WriteAllText(path, content, new UTF8Encoding(false));
        return path;
    }

    private static string Normalize(string content) => content.Replace("\r\n", "\n");

    [Fact]
    public async Task ProcessAsync_SimplifiedMode_ConvertsSrtText()
    {
        var path = WriteFile("movie.chs.srt",
            "1\n00:00:01,000 --> 00:00:02,000\n繁體中文字幕測試\n");

        var result = await _stage.ProcessAsync(path, new PostProcessContext("simplified"), CancellationToken.None);

        Assert.True(result.Changed);
        var converted = Normalize(File.ReadAllText(path));
        Assert.Contains("繁体中文字幕测试", converted);
        Assert.DoesNotContain("繁體", converted);
        // Cue timing and numbering survive the round trip.
        Assert.Contains("00:00:01,000 --> 00:00:02,000", converted);
    }

    [Fact]
    public async Task ProcessAsync_SecondRun_IsIdempotent()
    {
        var path = WriteFile("movie.chs.srt",
            "1\n00:00:01,000 --> 00:00:02,000\n繁體中文字幕測試\n");
        await _stage.ProcessAsync(path, new PostProcessContext("simplified"), CancellationToken.None);
        var afterFirst = File.ReadAllText(path);
        var mtimeAfterFirst = File.GetLastWriteTimeUtc(path);

        var second = await _stage.ProcessAsync(path, new PostProcessContext("simplified"), CancellationToken.None);

        Assert.False(second.Changed);
        Assert.Equal(afterFirst, File.ReadAllText(path));
        Assert.Equal(mtimeAfterFirst, File.GetLastWriteTimeUtc(path));
    }

    [Fact]
    public async Task ProcessAsync_AssKeepsStyleLayerByteForByte()
    {
        var styleBlock =
            "[V4+ Styles]\n" +
            "Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding\n" +
            "Style: Default,思源黑体,48,&H00FFFFFF,&H000000FF,&H00000000,&H7F000000,0,0,0,0,100,100,0,0,1,2.0,1,2,20,20,42,1\n" +
            "Style: Sign,華康儷中黑,64,&H0000FFFF,&H000000FF,&H00000000,&H7F000000,-1,0,0,0,100,100,0,0,1,3.0,0,5,0,0,0,1\n";
        var content =
            "[Script Info]\n" +
            "Title: Original\n" +
            "ScriptType: v4.00+\n" +
            "PlayResX: 1920\n" +
            "PlayResY: 1080\n" +
            styleBlock +
            "[Events]\n" +
            "Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text\n" +
            "Dialogue: 0,0:00:01.00,0:00:02.00,Default,,0,0,0,,繁體中文字幕測試\n" +
            "Dialogue: 0,0:00:03.00,0:00:04.00,Sign,,0,0,0,,{\\fad(200,200)}頭髮很長\n";
        var path = WriteFile("movie.chs.ass", content);

        var result = await _stage.ProcessAsync(path, new PostProcessContext("simplified"), CancellationToken.None);

        Assert.True(result.Changed);
        var converted = Normalize(File.ReadAllText(path));

        // Style layer ([V4+ Styles] section through the last Style: line)
        // survives byte-for-byte — only the Dialogue text changed.
        var originalStyleBlock = Normalize(styleBlock).TrimEnd('\n');
        Assert.Contains(originalStyleBlock, converted);

        var expectedStyleBlock = ExtractSection(converted, "[V4+ Styles]", "[Events]");
        Assert.Equal(originalStyleBlock, expectedStyleBlock);

        // Dialogue text converted (phrase-level OpenCC case included); the
        // style layer's own traditional font name above proves it was not
        // touched by the conversion.
        Assert.Contains("繁体中文字幕测试", converted);
        Assert.Contains("头发很长", converted);
        Assert.Contains("華康儷中黑", converted);
    }

    [Fact]
    public async Task ProcessAsync_NoneMode_LeavesBytesAndMtimeUntouched()
    {
        var path = WriteFile("movie.chs.srt",
            "1\n00:00:01,000 --> 00:00:02,000\n繁體中文字幕測試\n");
        var before = File.ReadAllBytes(path);
        var mtime = File.GetLastWriteTimeUtc(path);

        var none = await _stage.ProcessAsync(path, new PostProcessContext("none"), CancellationToken.None);
        var missing = await _stage.ProcessAsync(path, new PostProcessContext(null), CancellationToken.None);

        Assert.False(none.Changed);
        Assert.False(missing.Changed);
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.Equal(mtime, File.GetLastWriteTimeUtc(path));
    }

    [Fact]
    public async Task ProcessAsync_ChineseFreeFile_IsNotRewritten()
    {
        var path = WriteFile("movie.en.srt",
            "1\n00:00:01,000 --> 00:00:02,000\nHello world\n");
        var before = File.ReadAllText(path);
        var mtime = File.GetLastWriteTimeUtc(path);

        var result = await _stage.ProcessAsync(path, new PostProcessContext("simplified"), CancellationToken.None);

        Assert.False(result.Changed);
        Assert.Equal(before, File.ReadAllText(path));
        Assert.Equal(mtime, File.GetLastWriteTimeUtc(path));
    }

    [Fact]
    public async Task ProcessAsync_PreviewOnly_ReportsChangeWithoutWriting()
    {
        var path = WriteFile("movie.chs.srt",
            "1\n00:00:01,000 --> 00:00:02,000\n繁體中文字幕測試\n");
        var before = File.ReadAllBytes(path);
        var mtime = File.GetLastWriteTimeUtc(path);

        var result = await _stage.ProcessAsync(
            path, new PostProcessContext("simplified", PreviewOnly: true), CancellationToken.None);

        Assert.True(result.Changed);
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.Equal(mtime, File.GetLastWriteTimeUtc(path));
    }

    [Fact]
    public async Task ProcessAsync_UnparseableFile_IsSkippedWithoutThrowing()
    {
        var path = WriteFile("broken.srt", "not a subtitle at all");
        var before = File.ReadAllBytes(path);

        var result = await _stage.ProcessAsync(path, new PostProcessContext("simplified"), CancellationToken.None);

        Assert.False(result.Changed);
        Assert.NotNull(result.Reason);
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact]
    public async Task ProcessAsync_UnsupportedExtension_IsSkipped()
    {
        var path = WriteFile("movie.chs.sub", "1\n00:00:01,000 --> 00:00:02,000\n繁體中文字幕測試\n");

        var result = await _stage.ProcessAsync(path, new PostProcessContext("simplified"), CancellationToken.None);

        Assert.False(result.Changed);
    }

    [Fact]
    public async Task ProcessAsync_VttConvertsAndStaysIdempotent()
    {
        var path = WriteFile("movie.chs.vtt",
            "WEBVTT\n\n1\n00:00:01.000 --> 00:00:02.000\n繁體中文字幕測試\n");

        var result = await _stage.ProcessAsync(path, new PostProcessContext("simplified"), CancellationToken.None);
        Assert.True(result.Changed);

        var converted = Normalize(File.ReadAllText(path));
        Assert.Contains("繁体中文字幕测试", converted);
        Assert.StartsWith("WEBVTT", converted);

        var second = await _stage.ProcessAsync(path, new PostProcessContext("simplified"), CancellationToken.None);
        Assert.False(second.Changed);
    }

    private static string ExtractSection(string content, string startMarker, string endMarker)
    {
        var start = content.IndexOf(startMarker, StringComparison.Ordinal);
        var end = content.IndexOf(endMarker, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start, $"section {startMarker} not found");
        return content[start..end].TrimEnd('\n');
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
