using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Lingarr.Server.Models.FileSystem;
using Lingarr.Server.Modules.Subtitle;
using Xunit;

namespace Lingarr.Server.Tests.Services.Subtitle;

public class VttParserTests
{
    private static List<SubtitleItem> Parse(string vtt) =>
        new VttParser().ParseStream(
            new MemoryStream(Encoding.UTF8.GetBytes(vtt)), Encoding.UTF8);

    [Fact]
    public void ParsesHeaderCueIdsAndDotMilliseconds()
    {
        var items = Parse("""
            WEBVTT

            1
            00:00:01.000 --> 00:00:03.500
            Hello world

            2
            00:00:04.250 --> 00:00:06.000
            Second cue
            spanning two lines
            """);

        Assert.Equal(2, items.Count);
        Assert.Equal(1, items[0].Position);
        Assert.Equal(1000, items[0].StartTime);
        Assert.Equal(3500, items[0].EndTime);
        Assert.Equal(["Hello world"], items[0].Lines);
        Assert.Equal(2, items[1].Lines.Count);
        Assert.Equal(4250, items[1].StartTime);
    }

    [Fact]
    public void AcceptsMinuteSecondTimestampsAndCueSettings()
    {
        var items = Parse("""
            WEBVTT - metadata lines are ignored

            00:01.000 --> 00:02.000 align:start line:0%
            Short form
            """);

        Assert.Single(items);
        Assert.Equal(1000, items[0].StartTime);
        Assert.Equal(2000, items[0].EndTime);
    }

    [Fact]
    public void MissingHeaderThrows()
    {
        Assert.Throws<FormatException>(() => Parse("1\n00:00:01.000 --> 00:00:02.000\nNo header"));
    }
}

public class VttWriterTests
{
    [Fact]
    public async Task WritesHeaderDotSeparatorsAndBilingualOriginal()
    {
        var items = new List<SubtitleItem>
        {
            new()
            {
                Position = 1,
                StartTime = 1000,
                EndTime = 3500,
                Lines = ["Hello world"],
                PlaintextLines = ["Hello world"],
                TranslatedLines = ["你好世界"]
            }
        };

        using var stream = new MemoryStream();
        await new VttWriter().WriteStreamAsync(stream, items, stripSubtitleFormatting: false, bilingual: true);
        var text = Encoding.UTF8.GetString(stream.ToArray());

        Assert.StartsWith("WEBVTT", text);
        Assert.Contains("00:00:01.000 --> 00:00:03.500", text);
        Assert.Contains("你好世界", text);
        Assert.Contains("Hello world", text); // bilingual original below the translation
    }

    [Fact]
    public async Task RoundTripsThroughTheParser()
    {
        var items = new List<SubtitleItem>
        {
            new()
            {
                Position = 7,
                StartTime = 61_500,
                EndTime = 64_000,
                Lines = ["Source"],
                PlaintextLines = ["Source"],
                TranslatedLines = ["译文"]
            }
        };

        using var stream = new MemoryStream();
        await new VttWriter().WriteStreamAsync(stream, items, stripSubtitleFormatting: false);
        var parsed = new VttParser().ParseStream(
            new MemoryStream(stream.ToArray()), Encoding.UTF8);

        Assert.Single(parsed);
        Assert.Equal(7, parsed[0].Position);
        Assert.Equal(61_500, parsed[0].StartTime);
        Assert.Equal(64_000, parsed[0].EndTime);
    }
}
