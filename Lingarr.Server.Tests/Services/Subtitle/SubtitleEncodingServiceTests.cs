using System.Linq;
using System.Text;
using Lingarr.Server.Modules.Subtitle;
using Xunit;

namespace Lingarr.Server.Tests.Services.Subtitle;

/// <summary>
/// Encoding detection fixtures use real encoded bytes (GBK via cp936, Big5 via cp950)
/// so the detector is exercised end-to-end, exactly like the import workbench will.
/// </summary>
public class SubtitleEncodingServiceTests
{
    private static readonly SubtitleEncodingService Service = new();

    private static byte[] Encode(string text, string encodingName)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(encodingName).GetBytes(text);
    }

    [Fact]
    public void Detect_Utf8WithoutBom_ReturnsUtf8()
    {
        var bytes = "简体中文字幕测试"u8.ToArray();

        Assert.Equal("utf-8", Service.Detect(bytes).WebName);
    }

    [Fact]
    public void Detect_Utf8WithBom_ReturnsUtf8()
    {
        var bytes = new byte[] { 0xEF, 0xBB, 0xBF }.Concat("字幕"u8.ToArray()).ToArray();

        Assert.Equal("utf-8", Service.Detect(bytes).WebName);
    }

    [Fact]
    public void Detect_Utf16LeWithBom_ReturnsUnicode()
    {
        var preamble = Encoding.Unicode.GetPreamble();
        var bytes = preamble.Concat(Encoding.Unicode.GetBytes("字幕")).ToArray();

        Assert.Equal(Encoding.Unicode.WebName, Service.Detect(bytes).WebName);
    }

    [Theory]
    [InlineData("简体中文字幕测试第五批次")]
    [InlineData("第七集的字幕时间轴校正")]
    public void ConvertToUtf8_GbkEncodedChinese_RoundTrips(string text)
    {
        var bytes = Encode(text, "gb2312"); // gb2312 ⊂ gbk ⊂ gb18030

        var result = Service.ConvertToUtf8(bytes);

        Assert.Equal(text, Encoding.UTF8.GetString(result));
    }

    [Theory]
    [InlineData("繁體中文字幕測試視覺廳")]
    [InlineData("這是傳統中文的時間軸")]
    public void ConvertToUtf8_Big5EncodedTraditional_RoundTrips(string text)
    {
        var bytes = Encode(text, "big5");

        var result = Service.ConvertToUtf8(bytes);

        Assert.Equal(text, Encoding.UTF8.GetString(result));
    }

    [Fact]
    public void ConvertToUtf8_ShortGbkSnippet_WithMostlyAscii_StillRoundTrips()
    {
        // Regression: Ude names KOI8-R (0.41) for short ASCII-heavy GBK snippets;
        // the CJK plausibility override must rescue them.
        const string text = "1\n00:00:01,000 --> 00:00:02,000\n简体中文字幕\n";
        var bytes = Encode(text, "gb2312");

        var result = Service.ConvertToUtf8(bytes);

        Assert.Equal(text, Encoding.UTF8.GetString(result));
    }

    [Fact]
    public void ConvertToUtf8_Latin1Encoded_FallsBackAndRoundTrips()
    {
        const string text = "Café résumé naïve";
        var bytes = Encode(text, "latin1");

        var result = Service.ConvertToUtf8(bytes);

        Assert.Equal(text, Encoding.UTF8.GetString(result));
    }

    [Fact]
    public void ConvertToUtf8_Windows1252Encoded_WithEmDash_RoundTrips()
    {
        const string text = "Café résumé — naïve";
        var bytes = Encode(text, "windows-1252");

        var result = Service.ConvertToUtf8(bytes);

        Assert.Equal(text, Encoding.UTF8.GetString(result));
    }

    [Fact]
    public void ConvertToUtf8_Utf8WithBom_StripsBom()
    {
        var text = "字幕内容";
        var bytes = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes(text)).ToArray();

        var result = Service.ConvertToUtf8(bytes);

        var decoded = Encoding.UTF8.GetString(result);
        Assert.Equal(text, decoded);
        Assert.False(decoded.StartsWith('\uFEFF'));
    }

    [Fact]
    public void ConvertToUtf8_AlreadyUtf8_ReturnsEquivalentBytes()
    {
        var bytes = "纯 UTF-8 content 123"u8.ToArray();

        var result = Service.ConvertToUtf8(bytes);

        Assert.Equal(bytes, result);
    }

    [Fact]
    public void ConvertToUtf8_EmptyInput_ReturnsEmpty()
    {
        Assert.Empty(Service.ConvertToUtf8([]));
        Assert.Equal("utf-8", Service.Detect([]).WebName);
    }
}
