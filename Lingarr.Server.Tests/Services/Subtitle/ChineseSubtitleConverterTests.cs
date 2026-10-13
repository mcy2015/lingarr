using Lingarr.Server.Modules.Subtitle;
using Xunit;

namespace Lingarr.Server.Tests.Services.Subtitle;

public class ChineseSubtitleConverterTests
{
    [Theory]
    [InlineData("繁體中文字幕測試", "simplified", "繁体中文字幕测试")]
    [InlineData("繁體中文字幕測試", "none", "繁體中文字幕測試")]
    [InlineData("繁體中文字幕測試", null, "繁體中文字幕測試")]
    [InlineData("简体中文字幕测试", "traditional", "簡體中文字幕測試")]
    public void Convert_BetweenVariants(string input, string? mode, string expected)
    {
        Assert.Equal(expected, ChineseSubtitleConverter.Convert(input, mode));
    }

    [Fact]
    public void Convert_TraditionalToSimplified_RoundTrips()
    {
        var traditional = ChineseSubtitleConverter.Convert("简体中文字幕测试", "traditional");

        Assert.Equal("简体中文字幕测试", ChineseSubtitleConverter.Convert(traditional, "simplified"));
    }

    [Fact]
    public void Convert_PhraseLevel_HandlesHeadwordAmbiguity()
    {
        // 头发(fa4, traditional 頭髮) vs 发展(zhan3, traditional 發展) — the classic
        // OpenCC phrase-level case that char-by-char mapping gets wrong.
        var simplified = ChineseSubtitleConverter.Convert("我的頭髮很長", "simplified");

        Assert.Equal("我的头发很长", simplified);
    }
}
