using System.Text.Json;
using Lingarr.Core.Entities;
using Lingarr.Core.Enum;
using Lingarr.Server.Models.FileSystem;
using Lingarr.Server.Modules.Media;
using Xunit;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

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
        // Card A removed -select_streams s, so video/audio/attachment streams
        // now reach the parser and must be filtered by codec_type; a report
        // without streams (or a non-array one) still parses to an empty list.
        var json = WrapStreams(
            """{ "index": 0, "codec_name": "h264", "codec_type": "video", "tags": { "language": "eng" } }""",
            """{ "index": 1, "codec_name": "eac3", "codec_type": "audio", "tags": { "language": "chi" } }""",
            """{ "index": 3, "codec_name": "ttf", "codec_type": "attachment" }""",
            """{ "index": 4, "codec_name": "subrip", "codec_type": "subtitle", "tags": { "language": "chi" } }""");

        var tracks = EmbeddedTrackService.ParseFFprobeOutput(json);

        var track = Assert.Single(tracks);
        Assert.Equal(4, track.StreamIndex);
        Assert.Equal("zh", track.Language);

        Assert.Empty(EmbeddedTrackService.ParseFFprobeOutput("""{ "streams": [] }"""));
        Assert.Empty(EmbeddedTrackService.ParseFFprobeOutput("""{}"""));
        Assert.Empty(EmbeddedTrackService.ParseFFprobeOutput("""{ "streams": null }"""));
    }

    [Fact]
    public void Parse_StreamWithoutCodecType_StillParsesAsSubtitleTrack()
    {
        // Legacy behavior: -select_streams-era reports (and their fixtures)
        // carry no codec_type — every stream in them was a subtitle stream.
        var json = WrapStreams(
            """{ "index": 7, "codec_name": "mov_text", "tags": { "language": "eng" } }""");

        var tracks = EmbeddedTrackService.ParseFFprobeOutput(json);

        var track = Assert.Single(tracks);
        Assert.Equal("srt", track.Codec);
        Assert.Equal("en", track.Language);
    }

    // ── card A: audio-language extraction from the same report ─────

    [Fact]
    public void ParseAudioLanguages_CollectsDistinctNormalizedCodes_InStreamOrder()
    {
        var json = WrapStreams(
            """{ "index": 0, "codec_name": "h264", "codec_type": "video" }""",
            """{ "index": 1, "codec_name": "eac3", "codec_type": "audio", "tags": { "language": "chi" } }""",
            """{ "index": 2, "codec_name": "ac3", "codec_type": "audio", "tags": { "language": "english" } }""",
            """{ "index": 3, "codec_name": "truehd", "codec_type": "audio", "tags": { "language": "zho" } }""",
            """{ "index": 4, "codec_name": "truehd", "codec_type": "audio" }""",
            """{ "index": 5, "codec_name": "subrip", "codec_type": "subtitle", "tags": { "language": "kor" } }""");

        var languages = EmbeddedTrackService.ParseAudioLanguages(json);

        // chi and zho fold onto zh (deduplicated); the name "english" parses
        // through the filename-parser name table; the tagless audio stream
        // and the subtitle stream contribute nothing.
        Assert.Equal(["zh", "en"], languages);
    }

    [Fact]
    public void ParseAudioLanguages_UndAndUnrecognizedTags_ContributeNothing()
    {
        // "probed but nothing recognized" must stay empty so callers keep
        // the column null instead of storing "[]".
        var json = WrapStreams(
            """{ "index": 1, "codec_type": "audio", "tags": { "language": "und" } }""",
            """{ "index": 2, "codec_type": "audio", "tags": { "language": "gibberish" } }""",
            """{ "index": 3, "codec_type": "audio" }""");

        Assert.Empty(EmbeddedTrackService.ParseAudioLanguages(json));
        Assert.Empty(EmbeddedTrackService.ParseAudioLanguages("""{ "streams": [] }"""));
        Assert.Empty(EmbeddedTrackService.ParseAudioLanguages("""{}"""));
        Assert.Empty(EmbeddedTrackService.ParseAudioLanguages("""{ "streams": null }"""));
    }

    [Fact]
    public void ParseAudioLanguageString_SplitsTheSlashJoinedArrTruthString()
    {
        Assert.Equal(["en", "zh"], EmbeddedTrackService.ParseAudioLanguageString("english / chi"));
        Assert.Equal(["zh"], EmbeddedTrackService.ParseAudioLanguageString("chi"));
        // duplicates collapse; und/unknown tokens drop out; blank input is empty
        Assert.Equal(["en", "zh"], EmbeddedTrackService.ParseAudioLanguageString("english / chi / english"));
        Assert.Empty(EmbeddedTrackService.ParseAudioLanguageString("und / gibberish"));
        Assert.Empty(EmbeddedTrackService.ParseAudioLanguageString(null));
        Assert.Empty(EmbeddedTrackService.ParseAudioLanguageString("   "));
    }

    [Fact]
    public void NormalizeLanguageNames_FoldsAggregateGuesses_AndDropsUnknownNames()
    {
        // *arr's languages aggregate carries display names and an "Unknown"
        // placeholder — unknowns drop out instead of guessing.
        var codes = EmbeddedTrackService.NormalizeLanguageNames(
            ["English", "chi", "Unknown", null, "Japanese"]);

        Assert.Equal(["en", "zh", "ja"], codes);
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

    // ── F2b: available-language union ──────────────────────────────

    /// <summary>Track row helper for the union tests (scan-marker-free).</summary>
    private static MediaSubtitleTrack Track(string? language, bool isTextBased = true) => new()
    {
        MediaType = MediaType.Movie,
        MediaId = 1,
        StreamIndex = 2,
        Language = language,
        RawLanguage = null,
        Title = null,
        Codec = isTextBased ? "srt" : "pgs",
        IsTextBased = isTextBased,
        FilePath = "/movies/movie.mkv"
    };

    private static Subtitles External(params string[] languages) => new()
    {
        Path = "/movies/movie.srt",
        FileName = "movie",
        Language = languages[0],
        Languages = languages.ToList(),
        Caption = string.Empty,
        Format = ".srt"
    };

    [Fact]
    public void BuildAvailableLanguages_UnionsExternalAndEmbedded_Lowercased()
    {
        var external = new List<Subtitles>
        {
            External("EN"),                  // single-language file, uppercase tag
            External("zh", "ja")             // dual-language file (scjp/简日)
        };
        var embedded = new List<MediaSubtitleTrack>
        {
            Track("ko"),                     // text track with a language
            Track(null),                     // unrecognized tag contributes nothing
            Track("zh-TW", isTextBased: false) // graphic tracks count for coverage too
        };

        var available = EmbeddedTrackService.BuildAvailableLanguages(external, embedded);

        string[] expected = ["en", "ja", "ko", "zh", "zh-tw"];
        Assert.Equal(expected, available.Order().ToArray());
    }

    [Fact]
    public void BuildAvailableLanguages_EmptyInputs_YieldEmptySet()
    {
        Assert.Empty(EmbeddedTrackService.BuildAvailableLanguages([], []));
    }

    [Fact]
    public void TranslationSourceFileName_UsesVideoBaseAndLowercasedLanguage()
    {
        Assert.Equal(
            "Movie.2023.1080p.ja.srt",
            EmbeddedTrackService.TranslationSourceFileName("Movie.2023.1080p", "JA"));
        // defensive: a FileName that somehow carries an extension loses it
        Assert.Equal(
            "movie.en.srt",
            EmbeddedTrackService.TranslationSourceFileName("movie.mkv", "en"));
    }
}
