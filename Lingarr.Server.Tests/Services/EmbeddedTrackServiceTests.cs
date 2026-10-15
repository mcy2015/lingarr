using System.Text.Json;
using Lingarr.Server.Modules.Media;
using Xunit;

namespace Lingarr.Server.Tests.Services;

/// <summary>
/// Pure-parser coverage for the ffprobe report — no process, no database:
/// subtitle stream extraction, codec aliasing, graphic-track flagging and
/// language normalization (filename-parser rules plus the container-only
/// chi/zho folding).
/// </summary>
public class EmbeddedTrackServiceTests
{
    private static string WrapStreams(params string[] streams) =>
        $$"""{ "streams": [ {{string.Join(", ", streams)}} ] }""";

    [Fact]
    public void Parse_MixesTextAndGraphicTracks_WithNormalizedCodecsAndLanguages()
    {
        var json = WrapStreams(
            """{ "index": 2, "codec_name": "subrip", "codec_type": "subtitle", "tags": { "language": "chi", "title": "简体中文" } }""",
            """{ "index": 3, "codec_name": "ass", "codec_type": "subtitle", "tags": { "language": "eng" } }""",
            """{ "index": 4, "codec_name": "hdmv_pgs_subtitle", "codec_type": "subtitle", "tags": { "language": "zho" } }""");

        var tracks = EmbeddedTrackService.ParseFFprobeOutput(json);

        Assert.Equal(3, tracks.Count);

        var chinese = tracks[0];
        Assert.Equal(2, chinese.StreamIndex);
        Assert.Equal("zh", chinese.Language);           // chi → zh (container folding)
        Assert.Equal("chi", chinese.RawLanguage);
        Assert.Equal("简体中文", chinese.Title);
        Assert.Equal("srt", chinese.Codec);             // subrip → srt
        Assert.True(chinese.IsTextBased);

        var english = tracks[1];
        Assert.Equal("en", english.Language);           // eng → en (fansub token)
        Assert.Equal("ass", english.Codec);
        Assert.True(english.IsTextBased);

        var pgs = tracks[2];
        Assert.Equal("zh", pgs.Language);               // zho → zh (container folding)
        Assert.Equal("pgs", pgs.Codec);                 // hdmv_pgs_subtitle → pgs
        Assert.False(pgs.IsTextBased);                  // graphic · not translatable
    }

    [Fact]
    public void Parse_MovTextIsSrt_AndUnrecognisedLanguageKeepsRawTagOnly()
    {
        var json = WrapStreams(
            """{ "index": 7, "codec_name": "mov_text", "tags": { "language": "xyzzy" } }""",
            """{ "index": 8, "codec_name": "eia_608", "tags": { "language": "und" } }""");

        var tracks = EmbeddedTrackService.ParseFFprobeOutput(json);

        Assert.Equal("srt", tracks[0].Codec);           // mov_text → srt (MP4 embedded captions)
        Assert.True(tracks[0].IsTextBased);
        Assert.Null(tracks[0].Language);                // not a known code
        Assert.Equal("xyzzy", tracks[0].RawLanguage);

        Assert.Equal("eia_608", tracks[1].Codec);       // unknown alias keeps codec_name
        Assert.False(tracks[1].IsTextBased);            // CEA-608 counts as graphic here
        Assert.Null(tracks[1].Language);                // "und" is not a language
        Assert.Equal("und", tracks[1].RawLanguage);
    }

    [Fact]
    public void Parse_StreamWithoutTags_YieldsNullLanguageAndKeepsTrack()
    {
        var json = WrapStreams(
            """{ "index": 5, "codec_name": "subrip" }""");

        var tracks = EmbeddedTrackService.ParseFFprobeOutput(json);

        var track = Assert.Single(tracks);
        Assert.Null(track.Language);
        Assert.Null(track.RawLanguage);
        Assert.Null(track.Title);
        Assert.Equal("srt", track.Codec);
    }

    [Fact]
    public void Parse_SkipsVideoStreamsReportAndNoStreamsReport()
    {
        // -select_streams s already filters, but a report without streams
        // (or a non-array one) must parse to an empty list, not throw.
        Assert.Empty(EmbeddedTrackService.ParseFFprobeOutput("""{ "streams": [] }"""));
        Assert.Empty(EmbeddedTrackService.ParseFFprobeOutput("""{}"""));
        Assert.Empty(EmbeddedTrackService.ParseFFprobeOutput("""{ "streams": null }"""));
    }

    [Fact]
    public void Parse_MalformedJson_ThrowsForCallerToWarnAbout()
    {
        Assert.ThrowsAny<JsonException>(() => EmbeddedTrackService.ParseFFprobeOutput("not json"));
    }

    [Fact]
    public void Normalize_FoldsRegionTagThroughFilenameParserRules()
    {
        // zh-CN / zh-Hans fold onto zh via SubtitleLanguageParser's table.
        Assert.Equal("zh", EmbeddedTrackService.NormalizeLanguage("zh-CN"));
        Assert.Equal("zh-TW", EmbeddedTrackService.NormalizeLanguage("zh-HK"));
        Assert.Equal("ja", EmbeddedTrackService.NormalizeLanguage("jpn"));
        Assert.Equal("ko", EmbeddedTrackService.NormalizeLanguage("kor"));
        Assert.Null(EmbeddedTrackService.NormalizeLanguage("gibberish"));
        Assert.Null(EmbeddedTrackService.NormalizeLanguage(null));
        Assert.Null(EmbeddedTrackService.NormalizeLanguage("   "));
    }
}
