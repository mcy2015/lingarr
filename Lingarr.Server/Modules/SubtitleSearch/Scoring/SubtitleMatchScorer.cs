using System.Text.RegularExpressions;
using Lingarr.Server.Modules.SubtitleSearch.Abstractions;

namespace Lingarr.Server.Modules.SubtitleSearch.Scoring;

/// <summary>
/// Match scoring of subtitle candidates (tech design ③ / PRD AC9):
/// <list type="table">
/// <item><term>hash hit</term><description>100 — short-circuit, the candidate's release facts are irrelevant</description></item>
/// <item><term>title</term><description>55 — normalized exact match or release-name prefix</description></item>
/// <item><term>year</term><description>15 — a 4-digit token within ±1 of the request year</description></item>
/// <item><term>season/episode</term><description>20 — episode requests whose SxxEyy (or NxMM) tag matches exactly</description></item>
/// <item><term>release tokens</term><description>up to 10 — request release-name token overlap</description></item>
/// <item><term>wrong-episode cap</term><description>episode requests cap any candidate without the requested SxxEyy tag at 70 — below the auto line</description></item>
/// </list>
/// Weights follow bazarr's observed ranking (hash above everything, then
/// title, then season/episode, then release — github.com/morpheus65535/bazarr,
/// GPL; factual behavioural observation only, no code copied, ⑬) with
/// project tuning: the single 75 auto-download threshold is the PM decision
/// of PRD ruling 2. Candidates below the threshold are flagged — never
/// silently dropped — so the caller records the low-confidence skip (AC12a).
/// </summary>
public sealed partial class SubtitleMatchScorer
{
    /// <summary>Hash hit score (③): a file-hash match is the exact file.</summary>
    public const int HashMatchScore = 100;

    /// <summary>
    /// Single auto-download threshold, default 75 (0–100 configurable via
    /// the subtitle_search_auto_score setting; PRD ruling 2).
    /// </summary>
    public const int DefaultAutoScore = 75;

    /// <summary>
    /// Score ceiling for wrong-episode candidates on an episode request
    /// (QA1 P1-2): a candidate whose SxxEyy tag disagrees with the requested
    /// episode — or that carries no episode tag at all — can never reach the
    /// default 75 auto line, however strong its title/year/token overlap.
    /// </summary>
    public const int WrongEpisodeScoreCap = 70;

    /// <summary>
    /// Stable signal keys of <see cref="ScoreSignal"/> (B09): the front end
    /// maps these to localized labels; unknown keys fall back to the raw
    /// name, so renaming here is a coordinated front-end change.
    /// </summary>
    public const string HashSignalName = "hash";
    public const string TitleSignalName = "title";
    public const string YearSignalName = "year";
    public const string EpisodeSignalName = "episode";
    public const string ReleaseTokensSignalName = "releaseTokens";
    public const string WrongEpisodeCapSignalName = "wrongEpisodeCap";

    /// <summary>
    /// Releases the score of one candidate against its request (records are immutable — a scored copy returns).
    /// B09: the scored copy also carries the per-dimension signal breakdown
    /// whose points sum to <see cref="SubtitleCandidate.MatchScore"/> by
    /// construction (the score IS the sum — the invariant the candidate
    /// drawer renders cannot drift).
    /// </summary>
    public SubtitleCandidate Score(SubtitleSearchRequest request, SubtitleCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(candidate);

        var signals = ComputeSignals(request, candidate);
        return candidate with
        {
            MatchScore = signals.Sum(signal => signal.Points),
            Signals = signals
        };
    }

    /// <summary>
    /// Scores and orders candidates: descending score, equal scores keep
    /// input order (stable — OrderByDescending is stable), so a deterministic
    /// tie-break chain can be layered on top by the orchestrator (site
    /// priority × bilingual × format, ⑩).
    /// </summary>
    public IReadOnlyList<SubtitleCandidate> ScoreAll(SubtitleSearchRequest request, IEnumerable<SubtitleCandidate> candidates)
        => ScoreAll(request, candidates, preferHearingImpaired: false);

    /// <summary>
    /// Ordering variant with the B07 hearing-impaired preference
    /// (<c>subtitle_search_prefer_hearing_impaired</c>, default false): with
    /// the flag on, a hearing-impaired (SDH) candidate outranks a non-hi one
    /// when scores tie — layered BEFORE the stable input order, exactly where
    /// the orchestrator's final chain slots it (above the bilingual/format
    /// polish). A pure tiebreak: the score itself, the auto threshold and
    /// every signal never see the flag, so with it off (the default) the
    /// ordering is byte-identical to the pre-B07 behaviour (回归门).
    /// </summary>
    public IReadOnlyList<SubtitleCandidate> ScoreAll(
        SubtitleSearchRequest request,
        IEnumerable<SubtitleCandidate> candidates,
        bool preferHearingImpaired)
    {
        return candidates
            .Select(candidate => Score(request, candidate))
            .OrderByDescending(candidate => candidate.MatchScore)
            .ThenByDescending(candidate => preferHearingImpaired && candidate.HearingImpaired)
            .ToList();
    }

    /// <summary>
    /// Whether a scored candidate crosses the auto-download threshold for
    /// the given configured line (default
    /// <see cref="DefaultAutoScore"/>). Below the line the orchestrator skips
    /// and records LowConfidence (③/AC12a).
    /// </summary>
    public bool IsAutoEligible(SubtitleCandidate candidate, int threshold = DefaultAutoScore)
        => candidate.MatchScore >= threshold;

    /// <summary>
    /// Nullable-threshold overload (B02 upgrade rounds): the effective auto
    /// line there is computed per target — max of the configured
    /// subtitle_search_auto_score and the old file's score + the upgrade
    /// margin — and may legitimately be absent (null), which defers to
    /// <see cref="DefaultAutoScore"/>. Semantics are otherwise identical to
    /// the int overload.
    /// </summary>
    public bool IsAutoEligible(SubtitleCandidate candidate, int? minScoreOverride)
        => candidate.MatchScore >= (minScoreOverride ?? DefaultAutoScore);

    /// <summary>
    /// Tiebreak helper of the orchestrator's final ordering (design ⑧
    /// polish): a bilingual candidate outranks a monolingual one when score
    /// and hash verdict tie. Detection is deliberately simple and
    /// documented: the <see cref="SubtitleCandidate.Language"/> field is the
    /// authority — a value that splits on non-alphanumeric separators into
    /// two or more short alpha tokens is a multi-language code
    /// ("zh+en", "zh-en"…). As a fallback for sources that hide the fact
    /// in the release name only, the raw (un-normalized) release info is
    /// scanned for the common Chinese/English bilingual markers
    /// ("chs&amp;eng", "cht&amp;eng" — kept raw because the scorer's
    /// normalization folds the separator into a space and would erase
    /// exactly the signal).
    /// </summary>
    public static bool IsBilingual(SubtitleCandidate candidate)
    {
        var language = candidate.Language?.Trim() ?? string.Empty;
        if (language.Length > 0)
        {
            // Same token grammar as the release normalization, on the
            // lowercased code: "zh+en" → ["zh","en"] (two short alpha
            // tokens = multi-language), "zh" → one token, "zho-chs" → two.
            var tokens = NormalizeKeepDigitsRegex()
                .Split(language.ToLowerInvariant())
                .Where(token => token.Length > 0)
                .ToList();
            if (tokens.Count >= 2
                && tokens.All(token => token.Length is >= 2 and <= 5 && token.All(char.IsLetter)))
            {
                return true;
            }
        }

        var release = candidate.ReleaseInfo;
        if (string.IsNullOrEmpty(release))
        {
            return false;
        }

        return release.Contains("chs&eng", StringComparison.OrdinalIgnoreCase)
               || release.Contains("cht&eng", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Tiebreak helper of the orchestrator's final ordering: format
    /// preference rank, lower wins — srt before ass before vtt
    /// (case-insensitive), everything else (sub/zip/unknown…) ranks last.
    /// Rationale: srt is the pipeline's native output format (the
    /// convert-to-srt default), ass is the Chinese-source mainstay that
    /// converts losslessly, vtt is the rarest of the three.
    /// </summary>
    public static int FormatPreference(string? format) => (format ?? string.Empty).Trim().ToLowerInvariant() switch
    {
        "srt" => 0,
        "ass" or "ssa" => 1,
        "vtt" => 2,
        _ => 3
    };

    /// <summary>
    /// Splits a search keyword that ends in an SxxEyy (or NxMM) tag into the
    /// keyword without the tag and the tag's numbers (R3b: the front end
    /// pre-fills "Show Name S01E01" as the manual-search query). Reuses the
    /// scorer's own tag recognition — <see cref="SeasonEpisodeTagRegex"/> on
    /// the <see cref="Normalize"/>d form — so the split and the scoring see
    /// the exact same tag grammar.
    /// <para>
    /// "Show Name S01E01" → ("Show Name", (1, 1)); "Show.Name.1x02" →
    /// ("Show.Name", (1, 2)); a keyword without a trailing tag (or one that
    /// is nothing but the tag) returns itself with a null tag. Separator
    /// characters left dangling at the cut (dots, dashes, spaces) are
    /// trimmed; separators inside the head are preserved.
    /// </para>
    /// </summary>
    public static (string Keyword, (int Season, int Episode)? Tag) SplitTrailingSeasonEpisode(string? keyword)
    {
        if (string.IsNullOrWhiteSpace(keyword))
        {
            return (keyword ?? string.Empty, null);
        }

        var normalized = Normalize(keyword);
        Match? trailing = null;
        foreach (var match in SeasonEpisodeTagRegex().Matches(normalized).Cast<Match>())
        {
            if (match.Index + match.Length == normalized.Length)
            {
                trailing = match;
            }
        }

        if (trailing is null)
        {
            return (keyword, null);
        }

        // The tag itself is separator-free, so its text occurs verbatim
        // (ignoring case) at the tail of the original keyword — cut there.
        var cut = keyword.ToLowerInvariant().LastIndexOf(trailing.Value, StringComparison.Ordinal);
        var head = cut > 0 ? keyword[..cut].TrimEnd(' ', '.', '-', '_') : string.Empty;
        if (head.Length == 0)
        {
            // Tag-only keyword ("S01E01") — nothing usable stays behind.
            return (keyword, null);
        }

        return (head, (int.Parse(trailing.Groups["s"].Value), int.Parse(trailing.Groups["e"].Value)));
    }

    /// <summary>
    /// The per-dimension breakdown behind <see cref="Score"/> (B09). Every
    /// signal contributes exactly the points it earned (0 on a miss) and the
    /// score is the sum, so the drawer's「总和 = matchScore」holds by
    /// construction:
    /// <list type="bullet">
    /// <item>hash hit — the single signal "hash" at 100 (③ short-circuit;
    /// release facts are irrelevant for an exact-file match).</item>
    /// <item>title / year / episode / releaseTokens — one row each, the
    /// episode row only on episode requests (a movie has no episode
    /// dimension to miss).</item>
    /// <item>wrong-episode cap — when the QA1 P1-2 cap actually bites (raw
    /// sum above the cap), one negative row closes the gap between the raw
    /// sum and the capped score; a cap that changes nothing adds no row.</item>
    /// </list>
    /// </summary>
    private static IReadOnlyList<ScoreSignal> ComputeSignals(SubtitleSearchRequest request, SubtitleCandidate candidate)
    {
        // ③: hash hit short-circuits at 100 — no other fact can (or needs
        // to) move an exact-file match.
        if (candidate.HashMatch)
        {
            return [new ScoreSignal(HashSignalName, HashMatchScore, Hit: true)];
        }

        var release = Normalize(candidate.ReleaseInfo ?? string.Empty);
        var signals = new List<ScoreSignal>();

        // Title 55: exact normalized equality or release-name prefix. A
        // title inside the name but not leading is worth nothing — that is
        // the observed false-positive shape (sequels, "Movie, The" style).
        var title = Normalize(request.Title);
        var titleHit = release.Length > 0 && title.Length > 0
            && (release.Equals(title, StringComparison.OrdinalIgnoreCase)
                || release.StartsWith(title + " ", StringComparison.OrdinalIgnoreCase));
        signals.Add(new ScoreSignal(TitleSignalName, titleHit ? 55 : 0, titleHit));

        // Year 15: any 4-digit token within ±1 tolerates off-by-one release
        // years between providers.
        var yearHit = request.Year is int year && ContainsYearWithin(release, year, tolerance: 1);
        signals.Add(new ScoreSignal(YearSignalName, yearHit ? 15 : 0, yearHit));

        // Season/episode 20: episode requests only, and only on an exact
        // SxxEyy (or NxMM) match — a wrong episode never scores here.
        var isEpisodeRequest = request.Season.HasValue && request.Episode.HasValue;
        var episodeTagMatches = isEpisodeRequest
            && SeasonEpisodeMatches(release, request.Season!.Value, request.Episode!.Value);
        if (isEpisodeRequest)
        {
            signals.Add(new ScoreSignal(EpisodeSignalName, episodeTagMatches ? 20 : 0, episodeTagMatches));
        }

        // Release tokens up to 10: proportion of the request's scene tokens
        // present in the candidate release (resolution/source/group). No
        // request release name → no points (miss row).
        var tokenPoints = ReleaseTokenScore(request.ReleaseName, release);
        signals.Add(new ScoreSignal(ReleaseTokensSignalName, tokenPoints, tokenPoints > 0));

        // Wrong-episode cap (QA1 P1-2): episode requests resolve the year
        // through Season→Show, so a wrong-episode candidate of the same show
        // earned title 55 + year 15 + tokens up to 10 = 79 ≥ 75 and the auto
        // round downloaded the wrong episode. Any candidate that does not
        // carry the requested SxxEyy tag — wrong tag or no tag at all — is
        // capped below the default auto line. Hash matches returned above.
        // The cap row exists only when it actually lowers the total — its
        // points are the (negative) difference, keeping the sum invariant.
        if (isEpisodeRequest && !episodeTagMatches)
        {
            var raw = signals.Sum(signal => signal.Points);
            if (raw > WrongEpisodeScoreCap)
            {
                signals.Add(new ScoreSignal(WrongEpisodeCapSignalName, WrongEpisodeScoreCap - raw, Hit: true));
            }
        }

        return signals;
    }

    /// <summary>
    /// Normalization for token comparisons: lowercase, every non-alphanumeric
    /// run collapsed to one space, trimmed. "Movie.2023.WEBDL-1080p" →
    /// "movie 2023 webdl 1080p".
    /// </summary>
    internal static string Normalize(string value)
        => WhitespaceRunRegex()
            .Replace(NormalizeKeepDigitsRegex().Replace(value.ToLowerInvariant(), " "), " ")
            .Trim();

    private static bool ContainsYearWithin(string normalizedRelease, int year, int tolerance)
    {
        foreach (var token in normalizedRelease.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (token.Length == 4 && int.TryParse(token, out var candidate)
                && Math.Abs(candidate - year) <= tolerance)
            {
                return true;
            }
        }

        return false;
    }

    private static bool SeasonEpisodeMatches(string normalizedRelease, int season, int episode)
    {
        foreach (var match in SeasonEpisodeTagRegex().Matches(normalizedRelease).Cast<Match>())
        {
            var tagSeason = int.Parse(match.Groups["s"].Value);
            var tagEpisode = int.Parse(match.Groups["e"].Value);
            if (tagSeason == season && tagEpisode == episode)
            {
                return true;
            }
        }

        return false;
    }

    private static int ReleaseTokenScore(string? requestRelease, string normalizedCandidateRelease)
    {
        if (string.IsNullOrWhiteSpace(requestRelease) || normalizedCandidateRelease.Length == 0)
        {
            return 0;
        }

        var requestTokens = TokenSet(requestRelease);
        if (requestTokens.Count == 0)
        {
            return 0;
        }

        var candidateTokens = TokenSet(normalizedCandidateRelease);
        var matched = requestTokens.Count(token => candidateTokens.Contains(token));
        // Proportional 0–10, rounded to the nearest point.
        return (int)Math.Round(10.0 * matched / requestTokens.Count, MidpointRounding.AwayFromZero);
    }

    private static HashSet<string> TokenSet(string value)
        => new(
            Normalize(value).Split(' ', StringSplitOptions.RemoveEmptyEntries),
            StringComparer.OrdinalIgnoreCase);

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NormalizeKeepDigitsRegex();

    [GeneratedRegex("\\s+")]
    private static partial Regex WhitespaceRunRegex();

    /// <summary>Scene episode tags: S01E02 and the 1x02 form, word-bounded.</summary>
    [GeneratedRegex(@"\b(?:s(?<s>\d{1,2})e(?<e>\d{1,3})|(?<s>\d{1,2})x(?<e>\d{1,3}))\b")]
    private static partial Regex SeasonEpisodeTagRegex();
}
