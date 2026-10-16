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
        if (request.Season is int season && request.Episode is int episode
            && SeasonEpisodeMatches(release, season, episode))
        {
            score += 20;
        }

        // Release tokens up to 10: proportion of the request's scene tokens
        // present in the candidate release (resolution/source/group). No
        // request release name → no points.
        score += ReleaseTokenScore(request.ReleaseName, release);

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
