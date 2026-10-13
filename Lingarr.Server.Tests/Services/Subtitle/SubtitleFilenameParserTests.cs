using Lingarr.Server.Modules.Subtitle;
using Xunit;

namespace Lingarr.Server.Tests.Services.Subtitle;

/// <summary>
/// Contract tests for the import workbench filename parser.
/// Pattern set mirrors the Chinese fan-sub naming space (ChineseSubFinder reference):
/// SxxEyy / NxxNN / Eyy / 第N集(话/季), plus ASCII/CJK language markers.
/// </summary>
public class SubtitleFilenameParserTests
{
    [Theory]
    [InlineData("Shōgun.S01E05.1080p.chs.ass", 1, 5, SubtitleMatchTier.Exact)]
    [InlineData("severance.s02.e03.mkv", 2, 3, SubtitleMatchTier.Exact)]
    [InlineData("Show S01-E05.srt", 1, 5, SubtitleMatchTier.Exact)]
    [InlineData("S1E5.mkv", 1, 5, SubtitleMatchTier.Exact)]
    [InlineData("Series.S01EP05.720p.srt", 1, 5, SubtitleMatchTier.Exact)]
    [InlineData("s01e05e06.srt", 1, 5, SubtitleMatchTier.Exact)]   // multi-episode: first wins
    [InlineData("Show.S01E05.1920x1080.srt", 1, 5, SubtitleMatchTier.Exact)] // resolution must not interfere
    [InlineData("Show.1x02.720p.mkv", 1, 2, SubtitleMatchTier.Guess)]
    [InlineData("幕府将军.第8集.简体.srt", null, 8, SubtitleMatchTier.Exact)]
    [InlineData("Anime.第12话.ass", null, 12, SubtitleMatchTier.Exact)]
    [InlineData("Anime.第12話.srt", null, 12, SubtitleMatchTier.Exact)]
    [InlineData("第2季第5话.srt", 2, 5, SubtitleMatchTier.Exact)]
    [InlineData("Season.Pack.E05.srt", null, 5, SubtitleMatchTier.Exact)]
    [InlineData("Show.episode.12.srt", null, 12, SubtitleMatchTier.Exact)]
    [InlineData("Show.S01.complete.ass", 1, null, SubtitleMatchTier.Guess)] // season-only pack file
    [InlineData("Show.Season.2.Complete.cht.ass", 2, null, SubtitleMatchTier.Guess)]
    [InlineData("剧集.第2季.繁体.ass", 2, null, SubtitleMatchTier.Guess)]
    public void Parse_KnownNamingPatterns_ExtractsSeasonEpisodeAndTier(
        string fileName, int? expectedSeason, int? expectedEpisode, SubtitleMatchTier expectedTier)
    {
        var result = SubtitleFilenameParser.Parse(fileName);

        Assert.Equal(expectedSeason, result.Season);
        Assert.Equal(expectedEpisode, result.Episode);
        Assert.Equal(expectedTier, result.MatchTier);
    }

    [Theory]
    [InlineData("Dune.Part.Two.2021.vtt")]
    [InlineData("Blade.Runner.2049.chs.ass")] // year must not read as episode
    [InlineData("Movie.1920x1080.srt")]       // resolution must not read as NxxNN
    [InlineData("Movie.720x480.srt")]
    [InlineData("Rurouni.Kenshin.OVA.srt")]
    [InlineData("random-file_without_markers")]
    public void Parse_NoSeasonEpisodeSignal_ReturnsNone(string fileName)
    {
        var result = SubtitleFilenameParser.Parse(fileName);

        Assert.Null(result.Season);
        Assert.Null(result.Episode);
        Assert.Equal(SubtitleMatchTier.None, result.MatchTier);
    }

    [Theory]
    [InlineData("Movie.chs.srt", new[] { "zh-Hans" })]
    [InlineData("Movie.GB.ass", new[] { "zh-Hans" })]
    [InlineData("Movie.cht.ass", new[] { "zh-Hant" })]
    [InlineData("Movie.Big5.ass", new[] { "zh-Hant" })]
    [InlineData("Movie.简体.srt", new[] { "zh-Hans" })]
    [InlineData("Movie.繁體.ass", new[] { "zh-Hant" })]
    [InlineData("Movie.简英.srt", new[] { "zh-Hans", "en" })]
    [InlineData("Movie.chs&eng.ass", new[] { "zh-Hans", "en" })]
    [InlineData("Severance.S02E03.en.srt", new[] { "en" })]
    [InlineData("Movie.jpn.ass", new[] { "ja" })]
    [InlineData("Movie.kor.srt", new[] { "ko" })]
    [InlineData("Movie.srt", new string[0])]
    public void Parse_LanguageMarkers_TagsVariants(string fileName, string[] expectedTags)
    {
        var result = SubtitleFilenameParser.Parse(fileName);

        Assert.Equal(expectedTags, result.LanguageTags);
    }

    [Fact]
    public void Parse_ChassisLikeWord_DoesNotReadScAsSimplifiedChinese()
    {
        // "sc" must require delimiters; "disc"/"chassis" must not turn into zh-Hans
        var result = SubtitleFilenameParser.Parse("disc.image.srt");

        Assert.DoesNotContain("zh-Hans", result.LanguageTags);
    }

    [Fact]
    public void Parse_NullOrWhitespace_ReturnsNone()
    {
        foreach (var input in new[] { null, "", "   " })
        {
            var result = SubtitleFilenameParser.Parse(input!);

            Assert.Null(result.Season);
            Assert.Null(result.Episode);
            Assert.Equal(SubtitleMatchTier.None, result.MatchTier);
            Assert.Empty(result.LanguageTags);
        }
    }

    [Fact]
    public void Parse_SxxEyyBeatsNxNN_WhenBothPresent()
    {
        var result = SubtitleFilenameParser.Parse("Show.S01E05.crap.1x02.srt");

        Assert.Equal(1, result.Season);
        Assert.Equal(5, result.Episode);
        Assert.Equal(SubtitleMatchTier.Exact, result.MatchTier);
    }
}
