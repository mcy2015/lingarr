using System.Globalization;

namespace Lingarr.Server.Modules.Subtitle;

/// <summary>
/// Parses one dot-separated filename segment into the language codes a subtitle
/// file carries. Extends the plain ISO/culture lookup with two conventions the
/// culture index cannot know:
/// <list type="bullet">
/// <item>Fansub tags: <c>chs</c>/<c>sc</c> (simplified Chinese), <c>cht</c>/<c>tc</c>
/// (traditional), <c>jp</c> (Japanese) and friends.</item>
/// <item>Dual-language combo tags: <c>scjp</c>, <c>tceng</c>, <c>chs&amp;eng</c>,
/// 简日/繁英 … — one file legitimately carries two languages and counts for both.</item>
/// </list>
/// Chinese variants canonicalise onto the two coverage codes the app matches
/// targets against: all simplified forms → <c>zh</c>, all traditional forms →
/// <c>zh-TW</c>. In a subtitle-filename context these tokens never mean their
/// ISO neighbours (Sardinian <c>sc</c>, Tuvalu …), so the fansub map takes
/// precedence over the culture index.
/// </summary>
public static class SubtitleLanguageParser
{
    /// <summary>Fansub token → canonical code. Longest keys must win when splitting.</summary>
    private static readonly Dictionary<string, string> TokenMap = new(StringComparer.OrdinalIgnoreCase)
    {
        // Simplified Chinese
        ["chs"] = "zh",
        ["sc"] = "zh",
        ["gb"] = "zh",
        // Traditional Chinese
        ["cht"] = "zh-TW",
        ["tc"] = "zh-TW",
        ["big5"] = "zh-TW",
        // Japanese / Korean fansub spellings
        ["jp"] = "ja",
        ["jpn"] = "ja",
        ["kor"] = "ko",
        ["kr"] = "ko",
        ["eng"] = "en"
    };

    /// <summary>Chinese-script tags, including the common dual-language pairs.</summary>
    private static readonly Dictionary<string, string[]> CjkTagMap = new(StringComparer.Ordinal)
    {
        ["简体中文"] = ["zh"],
        ["简中"] = ["zh"],
        ["简体"] = ["zh"],
        ["中字"] = ["zh"],
        ["繁体中文"] = ["zh-TW"],
        ["繁體中文"] = ["zh-TW"],
        ["繁中"] = ["zh-TW"],
        ["繁體"] = ["zh-TW"],
        ["繁体"] = ["zh-TW"],
        ["简日"] = ["zh", "ja"],
        ["簡日"] = ["zh", "ja"],
        ["繁日"] = ["zh-TW", "ja"],
        ["简英"] = ["zh", "en"],
        ["簡英"] = ["zh", "en"],
        ["繁英"] = ["zh-TW", "en"],
        ["简韩"] = ["zh", "ko"],
        ["簡韓"] = ["zh", "ko"],
        ["繁韩"] = ["zh-TW", "ko"],
        ["中英"] = ["zh", "en"],
        ["中日"] = ["zh", "ja"]
    };

    /// <summary>Culture-validated codes folded onto the canonical Chinese pair.</summary>
    private static readonly Dictionary<string, string> ChineseFolding = new(StringComparer.OrdinalIgnoreCase)
    {
        ["zh-hans"] = "zh",
        ["zh-cn"] = "zh",
        ["zh-sg"] = "zh",
        ["zh"] = "zh",
        ["zh-hant"] = "zh-TW",
        ["zh-tw"] = "zh-TW",
        ["zh-hk"] = "zh-TW",
        ["zh-mo"] = "zh-TW"
    };

    private static readonly string[] ComboSeparators = ["&", "+", "·"];

    /// <summary>Every culture name plus ISO-2/ISO-3 code, built once.</summary>
    private static readonly HashSet<string> KnownCultureCodes = BuildKnownCultureCodes();

    /// <summary>
    /// Single-word language names ("english", "chinese") → ISO code, for
    /// OpenSubtitles-style tags like <c>movie.english.srt</c>. Only pure
    /// alphabetic names are kept so qualified names ("Chinese (Simplified)")
    /// can never appear as one dot-part; ambiguous names are dropped.
    /// </summary>
    private static readonly Dictionary<string, string> NameToCode = BuildNameToCode();

    /// <summary>
    /// Tries to read languages off one filename segment (a dot-separated part,
    /// brackets stripped). Returns false when the segment carries no language.
    /// </summary>
    public static bool TryParse(string? part, out IReadOnlyList<string> languages)
    {
        languages = Array.Empty<string>();
        var cleaned = (part ?? string.Empty).Trim()
            .Replace("[", "").Replace("]", "")
            .Replace("【", "").Replace("】", "")
            .Trim();
        if (cleaned.Length == 0)
        {
            return false;
        }

        // 1. Chinese-script tags first: they never collide with Latin codes.
        if (CjkTagMap.TryGetValue(cleaned, out var cjkLanguages))
        {
            languages = cjkLanguages;
            return true;
        }

        // 2. Exact fansub token.
        if (TokenMap.TryGetValue(cleaned, out var mapped))
        {
            languages = [mapped];
            return true;
        }

        // 3. Separator-joined combos: "chs&eng", "sc+jp".
        if (cleaned.IndexOfAny(['&', '+', '·']) >= 0)
        {
            var segments = cleaned.Split(ComboSeparators, StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length >= 2
                && segments.All(segment => TryParseSingle(segment, out _)))
            {
                languages = segments
                    .Select(segment => TryParseSingle(segment, out var code) ? code : string.Empty)
                    .Where(code => !string.IsNullOrEmpty(code))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                return true;
            }
        }

        // 4. Glued combos: greedy longest-token consumption of "scjp", "tceng"…
        if (!IsPlainCultureCode(cleaned))
        {
            var glued = TrySplitGlued(cleaned);
            if (glued is { Count: >= 2 })
            {
                languages = glued;
                return true;
            }
        }

        // 5. Standard ISO / BCP-47 code through the culture index, with the
        //    Chinese script/region variants folded onto zh / zh-TW.
        if (TryParseSingle(cleaned, out var single))
        {
            languages = [single];
            return true;
        }

        return false;
    }

    /// <summary>True when the whole segment already validates as a culture code.</summary>
    private static bool IsPlainCultureCode(string value) =>
        !ChineseFolding.ContainsKey(value.ToLowerInvariant())
        && KnownCultureCodes.Contains(value.ToLowerInvariant());

    private static bool TryParseSingle(string value, out string code)
    {
        code = string.Empty;
        if (TokenMap.TryGetValue(value, out var mapped))
        {
            code = mapped;
            return true;
        }

        // Known codes = enumerated cultures ∪ the Chinese alias table. Linux
        // ICU does not enumerate zh-TW/zh-CN… (LanguageCodeService keeps the
        // same LegacyChineseAliases for the same reason), while
        // GetCultureInfo accepts any junk string, so it cannot gate here.
        var normalized = value.ToLowerInvariant();
        if (!KnownCultureCodes.Contains(normalized) && !ChineseFolding.ContainsKey(normalized))
        {
            if (!NameToCode.TryGetValue(normalized, out var nameCode))
            {
                return false;
            }

            code = nameCode;
            return true;
        }

        code = ChineseFolding.GetValueOrDefault(normalized, normalized);
        return true;
    }

    /// <summary>Greedy longest-prefix consumption over the fansub token table.</summary>
    private static List<string>? TrySplitGlued(string value)
    {
        var tokens = TokenMap.Keys.OrderByDescending(k => k.Length).ToList();
        var found = new List<string>();
        var rest = value.ToLowerInvariant();
        while (rest.Length > 0)
        {
            var matched = false;
            foreach (var token in tokens)
            {
                if (rest.StartsWith(token, StringComparison.Ordinal))
                {
                    var code = TokenMap[token];
                    if (!found.Contains(code, StringComparer.OrdinalIgnoreCase))
                    {
                        found.Add(code);
                    }
                    rest = rest[token.Length..];
                    matched = true;
                    break;
                }
            }

            if (!matched)
            {
                return null;
            }
        }

        // A single token that consumed the whole string was already handled by
        // the exact-match step; require an actual combination here.
        return found.Count >= 2 ? found : null;
    }

    private static HashSet<string> BuildKnownCultureCodes()
    {
        var codes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var culture in CultureInfo.GetCultures(CultureTypes.AllCultures))
        {
            if (!string.IsNullOrEmpty(culture.Name))
            {
                codes.Add(culture.Name);
            }

            if (!string.IsNullOrEmpty(culture.TwoLetterISOLanguageName))
            {
                codes.Add(culture.TwoLetterISOLanguageName);
            }

            if (!string.IsNullOrEmpty(culture.ThreeLetterISOLanguageName))
            {
                codes.Add(culture.ThreeLetterISOLanguageName);
            }
        }

        return codes;
    }

    private static Dictionary<string, string> BuildNameToCode()
    {
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var culture in CultureInfo.GetCultures(CultureTypes.NeutralCultures))
        {
            if (string.IsNullOrEmpty(culture.Name) || string.IsNullOrEmpty(culture.TwoLetterISOLanguageName))
            {
                continue;
            }

            foreach (var displayName in new[] { culture.EnglishName, culture.NativeName })
            {
                if (displayName.Length == 0 || !displayName.All(char.IsLetter))
                {
                    continue;
                }

                var key = displayName.ToLowerInvariant();
                if (names.TryGetValue(key, out var existing)
                    && !string.Equals(existing, culture.TwoLetterISOLanguageName, StringComparison.OrdinalIgnoreCase))
                {
                    // Two cultures claim the same single word — too ambiguous to use.
                    names.Remove(key);
                    continue;
                }

                names[key] = culture.TwoLetterISOLanguageName;
            }
        }

        return names;
    }
}
