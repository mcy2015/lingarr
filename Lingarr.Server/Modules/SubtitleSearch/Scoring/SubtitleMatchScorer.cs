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

    /// <summary>Releases the score of one candidate against its request (records are immutable — a scored copy returns).</summary>
    public SubtitleCandidate Score(SubtitleSearchRequest request, SubtitleCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(candidate);

        return candidate with { MatchScore = ComputeScore(request, candidate) };
    }

    /// <summary>
    /// Scores and orders candidates: descending score, equal scores keep
    /// input order (stable — OrderByDescending is stable), so a deterministic
    /// tie-break chain can be layered on top by the orchestrator (site
    /// priority × bilingual × format, ⑩).
    /// </summary>
    public IReadOnlyList<SubtitleCandidate> ScoreAll(SubtitleSearchRequest request, IEnumerable<SubtitleCandidate> candidates)
    {
        return candidates
            .Select(candidate => Score(request, candidate))
            .OrderByDescending(candidate => candidate.MatchScore)
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

    private static int ComputeScore(SubtitleSearchRequest request, SubtitleCandidate candidate)
    {
        // ③: hash hit short-circuits at 100 — no other fact can (or needs
        // to) move an exact-file match.
        if (candidate.HashMatch)
        {
            return HashMatchScore;
        }

        var release = Normalize(candidate.ReleaseInfo ?? string.Empty);
        var score = 0;

        // Title 55: exact normalized equality or release-name prefix. A
        // title inside the name but not leading is worth nothing — that is
        // the observed false-positive shape (sequels, "Movie, The" style).
        var title = Normalize(request.Title);
        if (release.Length > 0 && title.Length > 0
            && (release.Equals(title, StringComparison.OrdinalIgnoreCase)
                || release.StartsWith(title + " ", StringComparison.OrdinalIgnoreCase)))
        {
            score += 55;
        }

        // Year 15: any 4-digit token within ±1 tolerates off-by-one release
        // years between providers.
        if (request.Year is int year && ContainsYearWithin(release, year, tolerance: 1))
        {
            score += 15;
        }

        // Season/episode 20: episode requests only, and only on an exact
        // SxxEyy (or NxMM) match — a wrong episode never scores here.
        var isEpisodeRequest = request.Season.HasValue && request.Episode.HasValue;
        var episodeTagMatches = isEpisodeRequest
            && SeasonEpisodeMatches(release, request.Season!.Value, request.Episode!.Value);
        if (episodeTagMatches)
        {
            score += 20;
        }

        // Release tokens up to 10: proportion of the request's scene tokens
        // present in the candidate release (resolution/source/group). No
        // request release name → no points.
        score += ReleaseTokenScore(request.ReleaseName, release);

        // Wrong-episode cap (QA1 P1-2): episode requests resolve the year
        // through Season→Show, so a wrong-episode candidate of the same show
        // earned title 55 + year 15 + tokens up to 10 = 79 ≥ 75 and the auto
        // round downloaded the wrong episode. Any candidate that does not
        // carry the requested SxxEyy tag — wrong tag or no tag at all — is
        // capped below the default auto line. Hash matches returned above.
        if (isEpisodeRequest && !episodeTagMatches)
        {
            score = Math.Min(score, WrongEpisodeScoreCap);
        }

        return score;
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
