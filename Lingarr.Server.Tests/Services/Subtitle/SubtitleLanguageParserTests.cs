using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Lingarr.Server.Modules.Settings;
using Lingarr.Server.Modules.Subtitle;
using Lingarr.Server.Modules.Translation;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Lingarr.Server.Tests.Services.Subtitle;

/// <summary>
/// Fansub language tags and dual-language combos: the filename parser must
/// recognise the tags subtitle groups actually use (chs/sc/tc/jp, scjp, 简日…)
/// and canonicalise every Chinese variant onto zh / zh-TW so coverage matches
/// the configured target codes.
/// </summary>
public class SubtitleLanguageParserTests
{
    [Theory]
    [InlineData("chs", "zh")]
    [InlineData("sc", "zh")]
    [InlineData("cht", "zh-TW")]
    [InlineData("tc", "zh-TW")]
    [InlineData("jp", "ja")]
    [InlineData("jpn", "ja")]
    [InlineData("kor", "ko")]
    [InlineData("en", "en")]
    [InlineData("eng", "en")]
    [InlineData("english", "en")]
    [InlineData("chinese", "zh")]
    [InlineData("japanese", "ja")]
    [InlineData("zh-CN", "zh")]
    [InlineData("zh-SG", "zh")]
    [InlineData("zh-Hans", "zh")]
    [InlineData("zh-TW", "zh-TW")]
    [InlineData("zh-HK", "zh-TW")]
    [InlineData("简体中文", "zh")]
    [InlineData("简中", "zh")]
    [InlineData("繁中", "zh-TW")]
    [InlineData("中字", "zh")]
    public void SingleTags_CanonicaliseOntoCoverageCodes(string tag, string expected)
    {
        Assert.True(SubtitleLanguageParser.TryParse(tag, out var languages));
        var language = Assert.Single(languages);
        Assert.Equal(expected, language);
    }

    [Theory]
    [InlineData("scjp", "zh", "ja")]
    [InlineData("tcjp", "zh-TW", "ja")]
    [InlineData("tceng", "zh-TW", "en")]
    [InlineData("chseng", "zh", "en")]
    [InlineData("简日", "zh", "ja")]
    [InlineData("繁英", "zh-TW", "en")]
    [InlineData("chs&eng", "zh", "en")]
    [InlineData("sc+en", "zh", "en")]
    public void DualLanguageTags_CarryBothLanguages(string tag, string first, string second)
    {
        Assert.True(SubtitleLanguageParser.TryParse(tag, out var languages));
        Assert.Equal(2, languages.Count);
        Assert.Equal(first, languages[0]);
        Assert.Equal(second, languages[1]);
    }

    [Theory]
    [InlineData("1080p")]
    [InlineData("HEVC-10bit")]
    [InlineData("FLAC")]
    [InlineData("DBD-Raws")]
    [InlineData("tmdb-1052433")]
    [InlineData("2023")]
    [InlineData("web")]
    [InlineData("")]
    public void NonLanguageParts_AreRejected(string part)
    {
        Assert.False(SubtitleLanguageParser.TryParse(part, out _));
    }
}

/// <summary>
/// GetAllSubtitles over a real directory with fansub-style filenames — the
/// language set (dual files carry two codes) is what coverage and source
/// selection consume.
/// </summary>
public class SubtitleServiceFansubScanTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lingarr-fansub-" + Guid.NewGuid().ToString("N"));
    private readonly Mock<ISettingService> _settings = new();

    private SubtitleService CreateService() => new(
        NullLogger<SubtitleService>.Instance,
        new LanguageCodeService(),
        _settings.Object);

    [Fact]
    public async Task DualLanguageFiles_ReportEveryLanguageAndKeepThePrimary()
    {
        Directory.CreateDirectory(_root);
        var name = "[DBD-Raws][Movie][1080P][BDRip][HEVC-10bit][FLAC]";
        await File.WriteAllTextAsync(Path.Combine(_root, $"{name}.scjp.ass"), "[Script Info]");
        await File.WriteAllTextAsync(Path.Combine(_root, $"{name}.tcjp.ass"), "[Script Info]");

        var subtitles = await CreateService().GetAllSubtitles(_root);

        Assert.Equal(2, subtitles.Count);
        var scjp = subtitles.Single(s => s.FileName.EndsWith("scjp"));
        Assert.Equal("zh", scjp.Language);
        Assert.Equal(["zh", "ja"], scjp.Languages);
        var tcjp = subtitles.Single(s => s.FileName.EndsWith("tcjp"));
        Assert.Equal("zh-TW", tcjp.Language);
        Assert.Equal(["zh-TW", "ja"], tcjp.Languages);
    }

    [Fact]
    public async Task UntaggedFiles_StayUnknownButKeepScanning()
    {
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(Path.Combine(_root, "Movie.srt"), "1\n00:00:01,000 --> 00:00:02,000\nHi");

        var subtitles = await CreateService().GetAllSubtitles(_root);

        var file = Assert.Single(subtitles);
        Assert.Equal("unknown", file.Language);
        Assert.Equal(["unknown"], file.Languages);
    }

    [Fact]
    public void SelectSourceSubtitle_AcceptsAnyLanguageADualFileCarries()
    {
        var dual = new Lingarr.Server.Models.FileSystem.Subtitles
        {
            FileName = "Movie.scjp",
            Language = "zh",
            Languages = ["zh", "ja"]
        };
        var english = new Lingarr.Server.Models.FileSystem.Subtitles
        {
            FileName = "Movie.en",
            Language = "en",
            Languages = ["en"]
        };

        // A ja source picks the dual file; an en source picks the english file.
        var jaSource = CreateService().SelectSourceSubtitle([dual, english], ["ja"], "false");
        Assert.NotNull(jaSource);
        Assert.Equal("ja", jaSource.SourceLanguage);

        var enSource = CreateService().SelectSourceSubtitle([dual, english], ["en"], "false");
        Assert.NotNull(enSource);
        Assert.Equal("Movie.en", enSource.Subtitle.FileName);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
