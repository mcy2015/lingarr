using System.Text.RegularExpressions;

namespace Lingarr.Server.Modules.Subtitle;

/// <summary>
/// How confidently a parsed season/episode pair maps to a real media episode.
/// The import workbench uses this to badge rows (exact / guess / unmatched)
/// and to decide whether a file can be auto-assigned or needs manual re-assign.
/// </summary>
public enum SubtitleMatchTier
{
    /// <summary>Unambiguous episode markers: SxxEyy, 第N集/话, Eyy.</summary>
    Exact,

    /// <summary>Weaker signals: NxxNN and season-only pack files.</summary>
    Guess,

    /// <summary>No season/episode signal found; file needs manual assignment.</summary>
    None
}

/// <summary>Season/episode/language signal extracted from a subtitle file name.</summary>
public sealed record SubtitleFilenameInfo(
    int? Season,
    int? Episode,
    SubtitleMatchTier MatchTier,
    IReadOnlyList<string> LanguageTags);

/// <summary>
/// Extracts season/episode/language hints from a subtitle file name (a full path is tolerated).
/// Deliberately regex-only against the Chinese fan-sub naming space — SxxEyy, NxxNN, Eyy,
/// 第N集/话/季 plus ASCII/CJK language markers — mirroring ChineseSubFinder's coverage without
/// pulling in guessit: import previews need a bounded, testable contract, not media metadata.
/// Stateless by design; safe to call from any thread.
/// </summary>
public static class SubtitleFilenameParser
{
    // S01E05 / s1.e5 / S01EP05 — the primary (and only Exact-tier) combined marker.
    // Delimiters around the block prevent word-internal hits (e.g. "SE05" never matches).
    private static readonly Regex SeasonEpisode =
        new(@"(?:^|[\s\.\-_])S(\d{1,2})[\s\.\-_]?E(?:P)?(\d{1,3})", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // 1x02 — legacy scene style; kept at Guess tier because bare "NxNN" collides with
    // title text more easily than SxxEyy. Word-adjacent delimiters make resolutions
    // (1280x720, 1920x1080) impossible to match: digit runs contain no boundary to start from.
    private static readonly Regex SeasonEpisodeAlt =
        new(@"(?:^|[\s\.\-_])(\d{1,2})x(\d{2})(?:[\s\.\-_]|$)", RegexOptions.Compiled);

    // 第8集 / 第12话 / 第12話 — unambiguous episode, season unknown unless 第N季 also present.
    private static readonly Regex ChineseEpisode =
        new(@"第\s*(\d{1,4})\s*[集话話]", RegexOptions.Compiled);

    // E05 / ep5 / Episode 12 — delimiter-guarded so "HE05" or "THE2" never match.
    private static readonly Regex PlainEpisode =
        new(@"(?:^|[\s\.\-_])(?:Episode|EP?)[\s\.]?(\d{1,3})(?:[\s\.\-_]|$)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // 第2季 — Chinese season marker.
    private static readonly Regex ChineseSeason =
        new(@"第\s*(\d{1,2})\s*季", RegexOptions.Compiled);

    // S01 / Season 2 — season-only (pack files). Guess tier: no episode to assign.
    private static readonly Regex SeasonOnly =
        new(@"(?:^|[\s\.\-_])(?:S(\d{1,2})|Season[\s\._]?(\d{1,2}))(?:[\s\.\-_]|$)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Filename language markers → BCP-47-ish tags. ASCII tokens are delimiter-checked
    // ("chs" must not fire inside "chassis"/"disc"); CJK tokens are substring-checked
    // because .NET word boundaries have no useful meaning between Han characters.
    private static readonly (string Token, string Tag, bool Ascii)[] LanguageTokens =
    [
        ("chs", "zh-Hans", true), ("gb", "zh-Hans", true), ("sc", "zh-Hans", true),
        ("简体", "zh-Hans", false), ("简", "zh-Hans", false),
        ("cht", "zh-Hant", true), ("big5", "zh-Hant", true), ("tc", "zh-Hant", true),
        ("繁体", "zh-Hant", false), ("繁", "zh-Hant", false),
        ("english", "en", true), ("eng", "en", true), ("en", "en", true), ("英", "en", false),
        ("jpn", "ja", true), ("日", "ja", false),
        ("kor", "ko", true), ("韩", "ko", false),
    ];

    /// <summary>Parses a subtitle file name; never throws — unparseable input yields <see cref="SubtitleMatchTier.None"/>.</summary>
    public static SubtitleFilenameInfo Parse(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return new SubtitleFilenameInfo(null, null, SubtitleMatchTier.None, []);
        }

        var (season, episode, tier) = ParseSeasonEpisode(fileName);

        return new SubtitleFilenameInfo(season, episode, tier, DetectLanguageTags(fileName));
    }

    private static (int? Season, int? Episode, SubtitleMatchTier Tier) ParseSeasonEpisode(string fileName)
    {
        var combined = SeasonEpisode.Match(fileName);
        if (combined.Success)
        {
            return (int.Parse(combined.Groups[1].Value), int.Parse(combined.Groups[2].Value),
                SubtitleMatchTier.Exact);
        }

        var alt = SeasonEpisodeAlt.Match(fileName);
        if (alt.Success)
        {
            return (int.Parse(alt.Groups[1].Value), int.Parse(alt.Groups[2].Value),
                SubtitleMatchTier.Guess);
        }

        var chineseEpisode = ChineseEpisode.Match(fileName);
        var episode = chineseEpisode.Success
            ? int.Parse(chineseEpisode.Groups[1].Value)
            : MatchInt(fileName, PlainEpisode);

        if (episode is not null)
        {
            // 第N季第N话: the Chinese season marker rides along when present.
            var season = MatchInt(fileName, ChineseSeason);
            return (season, episode, SubtitleMatchTier.Exact);
        }

        var seasonOnly = MatchInt(fileName, ChineseSeason)
                         ?? MatchInt(fileName, SeasonOnly, 1)
                         ?? MatchInt(fileName, SeasonOnly, 2);
        return seasonOnly is not null
            ? (seasonOnly, null, SubtitleMatchTier.Guess)
            : (null, null, SubtitleMatchTier.None);
    }

    private static int? MatchInt(string input, Regex regex, int group = 1)
    {
        var match = regex.Match(input);
        // Alternation patterns leave non-participating groups as empty strings,
        // so group participation must be checked before parsing.
        return match.Success && match.Groups[group].Success && match.Groups[group].Value.Length > 0
            ? int.Parse(match.Groups[group].Value)
            : null;
    }

    private static IReadOnlyList<string> DetectLanguageTags(string fileName)
    {
        var tags = new List<string>();
        foreach (var (token, tag, ascii) in LanguageTokens)
        {
            var hit = ascii
                ? Regex.IsMatch(fileName, $@"(?:^|[^a-zA-Z]){token}(?:[^a-zA-Z]|$)", RegexOptions.IgnoreCase)
                : fileName.Contains(token, StringComparison.Ordinal);
            if (hit && !tags.Contains(tag))
            {
                tags.Add(tag);
            }
        }

        return tags;
    }
}
