using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Lingarr.Server.Modules.Settings;
using Lingarr.Server.Modules.Subtitle;
using Lingarr.Server.Modules.Translation;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Lingarr.Server.Tests.Services.Subtitle;

/// <summary>
/// Content sniffing for files whose name carries no language tag (requirement
/// F1). Samples are full SRT cues so timing lines and digits exercise the
/// letter-only statistics. The detector is conservative by contract: anything
/// ambiguous must return null rather than guess (宁可漏判不可错判).
/// </summary>
public class SubtitleContentLanguageDetectorTests
{
    [Fact]
    public void SimplifiedChinese_SniffsZh()
    {
        const string text = """
            1
            00:00:01,000 --> 00:00:03,000
            你到底想去哪里？我们现在就出发吧。

            2
            00:00:04,000 --> 00:00:06,500
            我不知道，也许应该先回家看看情况再说。
            """;

        Assert.Equal("zh", SubtitleContentLanguageDetector.DetectLanguage(text));
    }

    [Fact]
    public void TraditionalChinese_SniffsZhTw()
    {
        const string text = """
            1
            00:00:01,000 --> 00:00:03,000
            你到底想去哪裡？我們現在就出發吧。

            2
            00:00:04,000 --> 00:00:06,500
            我不知道，也許應該先回家看看情況再說。
            """;

        Assert.Equal("zh-TW", SubtitleContentLanguageDetector.DetectLanguage(text));
    }

    [Fact]
    public void Japanese_KanaMixedWithKanji_SniffsJa()
    {
        const string text = """
            1
            00:00:01,000 --> 00:00:03,000
            これはテストです。私の名前は田中です。

            2
            00:00:04,000 --> 00:00:06,000
            何があったのか、まったく分かりません。
            """;

        Assert.Equal("ja", SubtitleContentLanguageDetector.DetectLanguage(text));
    }

    [Fact]
    public void Korean_Hangul_SniffsKo()
    {
        const string text = """
            1
            00:00:01,000 --> 00:00:03,000
            안녕하세요. 무슨 일이 있었어요?

            2
            00:00:04,000 --> 00:00:06,000
            나는 그것을 믿을 수 없어. 정말 놀라웠어.
            """;

        Assert.Equal("ko", SubtitleContentLanguageDetector.DetectLanguage(text));
    }

    [Fact]
    public void English_Stopwords_SniffsEn()
    {
        const string text = """
            1
            00:00:01,000 --> 00:00:02,500
            What do you think you're doing here?

            2
            00:00:03,000 --> 00:00:05,000
            I don't know what you want from me, but you're not getting it.

            3
            00:00:06,000 --> 00:00:07,500
            We need to talk about this right now.
            """;

        Assert.Equal("en", SubtitleContentLanguageDetector.DetectLanguage(text));
    }

    [Fact]
    public void French_Stopwords_SniffsFr()
    {
        const string text = """
            1
            00:00:01,000 --> 00:00:02,500
            Je ne sais pas ce que tu veux dire.

            2
            00:00:03,000 --> 00:00:05,000
            Je dois y aller maintenant, tu viens avec moi ?

            3
            00:00:06,000 --> 00:00:07,500
            C'est une bonne idée, mais c'est trop tard.
            """;

        Assert.Equal("fr", SubtitleContentLanguageDetector.DetectLanguage(text));
    }

    [Fact]
    public void ChineseEnglishMix_ReturnsNull()
    {
        // 中英 dual-language cues: Han is not dominant, but the Latin share is
        // too CJK-heavy for a Latin verdict — must stay undetectable.
        const string text = """
            1
            00:00:01,000 --> 00:00:03,000
            你到底想去哪里？
            Where do you want to go?

            2
            00:00:04,000 --> 00:00:06,000
            我不知道该怎么说。
            I don't know what to say.
            """;

        Assert.Null(SubtitleContentLanguageDetector.DetectLanguage(text));
    }

    [Fact]
    public void ProperNounHeavyLatin_LowDistinction_ReturnsNull()
    {
        const string text = """
            1
            00:00:01,000 --> 00:00:02,500
            Maria, Roberto, Andrea — hotel grande, villa rosa.

            2
            00:00:03,000 --> 00:00:04,500
            Luca, Marco, Elena — tempo, cinema, verde.
            """;

        Assert.Null(SubtitleContentLanguageDetector.DetectLanguage(text));
    }

    [Fact]
    public void ShortText_BelowSignalFloor_ReturnsNull()
    {
        // "Hi" alone must not be sniffed as anything (a two-letter sample is noise).
        const string text = "1\n00:00:01,000 --> 00:00:02,000\nHi";

        Assert.Null(SubtitleContentLanguageDetector.DetectLanguage(text));
    }

    [Fact]
    public void TimingOnlySrt_ReturnsNull()
    {
        const string text = """
            1
            00:00:01,000 --> 00:00:02,000

            2
            00:00:03,000 --> 00:00:04,000

            """;

        Assert.Null(SubtitleContentLanguageDetector.DetectLanguage(text));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \n\t")]
    public void EmptyText_ReturnsNull(string? text)
    {
        Assert.Null(SubtitleContentLanguageDetector.DetectLanguage(text));
    }

    [Fact]
    public void ShortPureHanSample_DefaultsToSimplifiedInsteadOfUnknown()
    {
        // QA F1-N1: two lines of everyday dialogue, no script-unique
        // characters at all — previously unknown, now simplified.
        Assert.Equal("zh", SubtitleContentLanguageDetector.DetectLanguage(
            "我们都是好孩子。\n这条路很长。"));
    }

    [Fact]
    public void AssDialogueText_SniffsThroughStyleLayers()
    {
        var text = SubtitleService.StripAssStyleLayers(
            "[Script Info]\nTitle: x\n" +
            "Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text\n" +
            "Style: Default,Arial,20,&H00FFFFFF,...\n" +
            "Dialogue: 0,0:00:01.00,0:00:03.00,Default,,0,0,0,,我们都是好孩子，天天都在等待阳光。\n" +
            "Dialogue: 0,0:00:04.00,0:00:06.00,Default,,0,0,0,,这条路的尽头就是我们要去的地方。\n");
        Assert.Equal("zh", SubtitleContentLanguageDetector.DetectLanguage(text));
    }
}

/// <summary>
/// GetAllSubtitles end-to-end over a real directory: unknown-language files go
/// through the full pipeline (head read → encoding conversion → detection),
/// filename tags keep precedence, and the (path, mtime, length) cache prevents
/// re-reading unchanged files.
/// </summary>
public class SubtitleContentSniffScanTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lingarr-sniff-" + Guid.NewGuid().ToString("N"));
    private readonly Mock<ISettingService> _settings = new();

    private SubtitleService CreateService(Func<string, byte[]>? reader = null) => new(
        NullLogger<SubtitleService>.Instance,
        new LanguageCodeService(),
        _settings.Object,
        null,
        reader);

    private void WriteSubtitle(string fileName, string content, string? encodingName = null)
    {
        Directory.CreateDirectory(_root);
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var bytes = encodingName is null
            ? Encoding.UTF8.GetBytes(content)
            : Encoding.GetEncoding(encodingName).GetBytes(content);
        File.WriteAllBytes(Path.Combine(_root, fileName), bytes);
    }

    private async Task<Lingarr.Server.Models.FileSystem.Subtitles> ScanSingleAsync()
    {
        var subtitles = await CreateService().GetAllSubtitles(_root);
        return Assert.Single(subtitles);
    }

    private const string SimplifiedSrt = """
        1
        00:00:01,000 --> 00:00:03,000
        你到底想去哪里？我们现在就出发吧。

        2
        00:00:04,000 --> 00:00:06,500
        我不知道，也许应该先回家看看情况再说。
        """;

    private const string TraditionalSrt = """
        1
        00:00:01,000 --> 00:00:03,000
        你到底想去哪裡？我們現在就出發吧。

        2
        00:00:04,000 --> 00:00:06,500
        我不知道，也許應該先回家看看情況再說。
        """;

    private const string JapaneseSrt = """
        1
        00:00:01,000 --> 00:00:03,000
        これはテストです。私の名前は田中です。

        2
        00:00:04,000 --> 00:00:06,000
        何があったのか、まったく分かりません。
        """;

    private const string KoreanSrt = """
        1
        00:00:01,000 --> 00:00:03,000
        안녕하세요. 무슨 일이 있었어요?

        2
        00:00:04,000 --> 00:00:06,000
        나는 그것을 믿을 수 없어. 정말 놀라웠어.
        """;

    private const string EnglishSrt = """
        1
        00:00:01,000 --> 00:00:02,500
        What do you think you're doing here?

        2
        00:00:03,000 --> 00:00:05,000
        I don't know what you want from me, but you're not getting it.

        3
        00:00:06,000 --> 00:00:07,500
        We need to talk about this right now.
        """;

    private const string FrenchSrt = """
        1
        00:00:01,000 --> 00:00:02,500
        Je ne sais pas ce que tu veux dire.

        2
        00:00:03,000 --> 00:00:05,000
        Je dois y aller maintenant, tu viens avec moi ?

        3
        00:00:06,000 --> 00:00:07,500
        C'est une bonne idée, mais c'est trop tard.
        """;

    [Fact]
    public async Task UntaggedGb18030SimplifiedChinese_SniffsZh()
    {
        // GB18030-encoded file exercises the encoding conversion inside the
        // sniff pipeline — without it the hanzi arrive as mojibake.
        WriteSubtitle("Movie.srt", SimplifiedSrt, "GB18030");

        var subtitle = await ScanSingleAsync();

        Assert.Equal("zh", subtitle.Language);
        Assert.Equal(["zh"], subtitle.Languages);
    }

    [Fact]
    public async Task UntaggedTraditionalChinese_SniffsZhTw()
    {
        WriteSubtitle("Movie.srt", TraditionalSrt);

        var subtitle = await ScanSingleAsync();

        Assert.Equal("zh-TW", subtitle.Language);
        Assert.Equal(["zh-TW"], subtitle.Languages);
    }

    [Fact]
    public async Task UntaggedJapanese_SniffsJa()
    {
        // Real ASS shape: the kana only ever appears in the Dialogue text
        // column, everything else is style noise (QA F1-N2).
        WriteSubtitle("Movie.ass", "[Script Info]\n" +
            "Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text\n" +
            "Style: Default,Arial,20,&H00FFFFFF,...\n" +
            "Dialogue: 0,0:00:01.00,0:00:03.00,Default,,0,0,0,,こんにちは、世界。私たちの旅はここから始まる。\n" +
            "Dialogue: 0,0:00:04.00,0:00:06.00,Default,,0,0,0,,また明日ね、元気でね。\n");

        var subtitle = await ScanSingleAsync();

        Assert.Equal("ja", subtitle.Language);
    }

    [Fact]
    public async Task UntaggedKorean_SniffsKo()
    {
        WriteSubtitle("Movie.srt", KoreanSrt);

        var subtitle = await ScanSingleAsync();

        Assert.Equal("ko", subtitle.Language);
    }

    [Fact]
    public async Task UntaggedEnglish_SniffsEn()
    {
        WriteSubtitle("Movie.srt", EnglishSrt);

        var subtitle = await ScanSingleAsync();

        Assert.Equal("en", subtitle.Language);
        Assert.Equal(["en"], subtitle.Languages);
    }

    [Fact]
    public async Task UntaggedFrench_SniffsFr()
    {
        WriteSubtitle("Show.S01E01.vtt", FrenchSrt);

        var subtitle = await ScanSingleAsync();

        Assert.Equal("fr", subtitle.Language);
    }

    [Fact]
    public async Task UntaggedEmptyFile_StaysUnknown()
    {
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(Path.Combine(_root, "Movie.srt"), string.Empty);

        var subtitle = await ScanSingleAsync();

        Assert.Equal("unknown", subtitle.Language);
        Assert.Equal(["unknown"], subtitle.Languages);
    }

    [Fact]
    public async Task UntaggedChineseEnglishMix_StaysUnknown()
    {
        const string mixed = """
            1
            00:00:01,000 --> 00:00:03,000
            你到底想去哪里？
            Where do you want to go?

            2
            00:00:04,000 --> 00:00:06,000
            我不知道该怎么说。
            I don't know what to say.
            """;
        WriteSubtitle("Movie.srt", mixed);

        var subtitle = await ScanSingleAsync();

        Assert.Equal("unknown", subtitle.Language);
    }

    [Fact]
    public async Task FilenameTag_WinsOverEnglishContent()
    {
        WriteSubtitle("Movie.chs.srt", EnglishSrt);

        var reads = 0;
        Func<string, byte[]> reader = _ =>
        {
            reads++;
            return Encoding.UTF8.GetBytes(EnglishSrt);
        };

        var subtitles = await CreateService(reader).GetAllSubtitles(_root);
        var subtitle = Assert.Single(subtitles);

        // The filename says zh and the content is never even read.
        Assert.Equal("zh", subtitle.Language);
        Assert.Equal(["zh"], subtitle.Languages);
        Assert.Equal(0, reads);
    }

    [Fact]
    public async Task Cache_SecondScanSkipsTheRead_EditsInvalidate()
    {
        WriteSubtitle("Movie.srt", EnglishSrt);
        var reads = 0;
        Func<string, byte[]> reader = path =>
        {
            reads++;
            return File.ReadAllBytes(path);
        };
        var service = CreateService(reader);

        var first = Assert.Single(await service.GetAllSubtitles(_root));
        Assert.Equal("en", first.Language);
        Assert.Equal(1, reads);

        // Same file, unchanged mtime/length: served from the sniff cache.
        var second = Assert.Single(await service.GetAllSubtitles(_root));
        Assert.Equal("en", second.Language);
        Assert.Equal(1, reads);

        // Rewriting the file (new length and mtime) must invalidate the entry.
        WriteSubtitle("Movie.srt", FrenchSrt);
        var third = Assert.Single(await service.GetAllSubtitles(_root));
        Assert.Equal("fr", third.Language);
        Assert.Equal(2, reads);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
