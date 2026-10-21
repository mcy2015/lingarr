using System;
using System.Collections.Generic;
using System.Linq;
using Lingarr.Server.Modules.SubtitleSearch.Abstractions;
using Lingarr.Server.Modules.SubtitleSearch.Scoring;
using Xunit;

namespace Lingarr.Server.Tests.Modules.SubtitleSearch;

/// <summary>
/// Match-scoring rules of tech design ③ / PRD AC9: hash short-circuit 100;
/// title 55 (exact/prefix); year ±1 15; exact SxxEyy 20 (episodes);
/// release-token overlap up to 10; the single 75 auto threshold flags
/// low-confidence candidates instead of dropping them. Stable ordering for
/// ties because the orchestrator layers site-priority tie-breaks on top.
/// </summary>
public class SubtitleMatchScorerTests
{
    private readonly SubtitleMatchScorer _scorer = new();

    private static SubtitleSearchRequest MovieRequest(
        string title = "Movie",
        int? year = 2019,
        string? releaseName = "Movie.2019.WEBDL-1080p") => new(
        title, year, Season: null, Episode: null,
        ReleaseName: releaseName, VideoHash: null, Languages: ["en"], ImdbId: null);

    private static SubtitleSearchRequest EpisodeRequest(
        string title = "Show",
        int? year = null,
        string? releaseName = "Show.S01E02.WEBDL-1080p",
        int season = 1,
        int episode = 2) => new(
        title, year, Season: season, Episode: episode,
        ReleaseName: releaseName, VideoHash: null, Languages: ["en"], ImdbId: null);

    private static SubtitleCandidate Candidate(
        string releaseInfo,
        bool hashMatch = false,
        string language = "en") => new(
        "opensubtitles",
        ExternalId: "ext-1",
        Language: language,
        MatchScore: 0,
        Forced: false,
        HearingImpaired: false,
        ReleaseInfo: releaseInfo,
        Uploader: null,
        Format: "srt",
        Size: null,
        HashMatch: hashMatch);

    [Fact]
    public void HashHit_ShortCircuitsTo100()
    {
        // Release facts are garbage on purpose: a hash match is the exact
        // file and must outrank every textual signal (③).
        var scored = _scorer.Score(MovieRequest(), Candidate("total garbage", hashMatch: true));

        Assert.Equal(100, scored.MatchScore);
    }

    [Fact]
    public void MovieFullMatch_ScoresTitleYearAndTokens()
    {
        var scored = _scorer.Score(MovieRequest(), Candidate("Movie.2019.WEBDL-1080p"));

        // title 55 + year 15 + all four release tokens (movie/2019/webdl/1080p) = 10
        Assert.Equal(80, scored.MatchScore);
    }

    [Fact]
    public void TitlePrefix_MatchesButNonLeadingTitleDoesNot()
    {
        var prefix = _scorer.Score(MovieRequest(), Candidate("Movie 2019 BluRay-1080p"));

        // Prefix yes (55) + year (15) + 3 of 4 tokens (7.5 rounds away from zero to 8) = 78.
        Assert.Equal(78, prefix.MatchScore);

        // "The Movie 2019" does not lead with the title — no title points.
        var embedded = _scorer.Score(MovieRequest(releaseName: null), Candidate("The Movie 2019"));
        Assert.Equal(15, embedded.MatchScore);
    }

    [Fact]
    public void YearTolerance_IsPlusMinusOne()
    {
        var request = MovieRequest(year: 2019, releaseName: null);

        Assert.Equal(70, _scorer.Score(request, Candidate("Movie 2020 WEBDL-1080p")).MatchScore); // title 55 + year 15
        Assert.Equal(70, _scorer.Score(request, Candidate("Movie 2018 WEBDL-1080p")).MatchScore);
        Assert.Equal(55, _scorer.Score(request, Candidate("Movie 2022 WEBDL-1080p")).MatchScore); // outside ±1
    }

    [Fact]
    public void EpisodeExactSeasonEpisode_Adds20()
    {
        var request = EpisodeRequest();

        var right = _scorer.Score(request, Candidate("Show.S01E02.WEBDL-1080p"));
        // title 55 + season/episode 20 + all release tokens 10 = 85
        Assert.Equal(85, right.MatchScore);

        var wrong = _scorer.Score(request, Candidate("Show.S01E03.WEBDL-1080p"));
        // No season/episode points for a wrong episode; tokens overlap 3 of 4 = 8 (rounded).
        Assert.Equal(63, wrong.MatchScore);
    }

    [Fact]
    public void WrongEpisodeCandidate_WithYearIsCappedBelowTheAutoLine()
    {
        // QA1 P1-2: episode requests carry the SHOW year (resolved through
        // Season→Show), so the same-show wrong episode used to reach
        // 55 + 15 + 9 = 79 ≥ 75 and crossed the auto line.
        var request = EpisodeRequest(year: 2019);

        var wrongEpisode = _scorer.Score(request, Candidate("Show.S01E03.2019.WEBDL-1080p"));

        // raw = 55 + 15 + 8 (3 of 4 tokens) = 78 → capped to 70
        Assert.Equal(70, wrongEpisode.MatchScore);
        Assert.False(_scorer.IsAutoEligible(wrongEpisode)); // below the default 75
    }

    [Fact]
    public void EpisodeCandidate_WithoutAnyEpisodeTag_IsCappedToo()
    {
        // A season-pack-ish release with no SxxEyy tag at all is not
        // provably the requested episode — capped the same way.
        var request = EpisodeRequest(year: 2019);

        var untagged = _scorer.Score(request, Candidate("Show.2019.WEBDL-1080p"));

        // raw = 55 + 15 + 8 (3 of 4 tokens) = 78 → capped to 70
        Assert.Equal(70, untagged.MatchScore);
        Assert.False(_scorer.IsAutoEligible(untagged));
    }

    [Fact]
    public void CorrectEpisodeCandidate_IsNeverCapped()
    {
        var request = EpisodeRequest(year: 2019);

        var right = _scorer.Score(request, Candidate("Show.S01E02.2019.WEBDL-1080p"));

        // title 55 + year 15 + episode 20 + tokens 10 = 100 — the cap only
        // touches candidates whose tag disagrees (or is absent).
        Assert.Equal(100, right.MatchScore);
        Assert.True(_scorer.IsAutoEligible(right));
    }

    [Fact]
    public void HashMatch_WrongEpisodeTag_StaysExemptFromTheCap()
    {
        // A file-hash match is the exact file — its (garbage) textual tags
        // must not drag it under the auto line.
        var request = EpisodeRequest(year: 2019);

        var scored = _scorer.Score(request, Candidate("Show.S01E99.2019.WEBDL-1080p", hashMatch: true));

        Assert.Equal(100, scored.MatchScore);
        Assert.True(_scorer.IsAutoEligible(scored));
    }

    [Fact]
    public void NxMmForm_CountsAsSeasonEpisodeTag()
    {
        var request = EpisodeRequest(season: 2, episode: 5, releaseName: "Show.2x05");

        var scored = _scorer.Score(request, Candidate("Show 2x05"));
        // title 55 + season/episode 20 + tokens 10 = 85
        Assert.Equal(85, scored.MatchScore);
    }

    [Fact]
    public void MovieReleaseWithoutEpisodeTag_IsNotPenalized()
    {
        // A movie candidate never gains or loses episode points — the
        // season/episode rule only fires for episode requests.
        var scored = _scorer.Score(MovieRequest(), Candidate("Movie.2019.WEBDL-1080p"));

        Assert.Equal(80, scored.MatchScore);
    }

    [Fact]
    public void NullReleaseFacts_ScoreZero()
    {
        var scored = _scorer.Score(MovieRequest(), Candidate(releaseInfo: null!));

        Assert.Equal(0, scored.MatchScore);
    }

    [Fact]
    public void AutoThreshold_FlagsBelowTheLineWithoutDropping()
    {
        // 75 is the boundary: at or above is auto-eligible (PRD ruling 2).
        // Title 55 + year 15 + 2 of 4 release tokens (5, rounded away from
        // zero) = exactly 75.
        var atLine = _scorer.Score(MovieRequest(), Candidate("Movie 2019 BluRay"));
        Assert.Equal(75, atLine.MatchScore);
        Assert.True(_scorer.IsAutoEligible(atLine));

        var belowLine = _scorer.Score(MovieRequest(year: null, releaseName: null), Candidate("Movie"));
        Assert.Equal(55, belowLine.MatchScore);
        Assert.False(_scorer.IsAutoEligible(belowLine));

        var custom = _scorer.IsAutoEligible(atLine, threshold: 80);
        Assert.False(custom);
    }

    [Fact]
    public void NullableThresholdOverload_NullDefersToDefault()
    {
        // B02 upgrade rounds compute the line per target; null (the caller
        // has no override) defers to the default 75, a value replaces it
        // exactly like the int overload.
        var atLine = _scorer.Score(MovieRequest(), Candidate("Movie 2019 BluRay"));
        Assert.Equal(75, atLine.MatchScore);

        Assert.True(_scorer.IsAutoEligible(atLine, minScoreOverride: null));
        Assert.True(_scorer.IsAutoEligible(atLine, minScoreOverride: 75));
        Assert.False(_scorer.IsAutoEligible(atLine, minScoreOverride: 82));
    }

    [Fact]
    public void ScoreAll_OrdersDescendingAndKeepsTiesStable()
    {
        var request = MovieRequest(year: null, releaseName: null);
        var candidates = new List<SubtitleCandidate>
        {
            Candidate("unrelated"),            // 0
            Candidate("Movie 2019 thing"),     // 55 (title prefix)
            Candidate("Movie"),                // 55 (exact)
            Candidate("Movie", hashMatch: true) // 100
        };

        var scored = _scorer.ScoreAll(request, candidates);

        Assert.Equal(100, scored[0].MatchScore);
        // The two 55s keep their input order (stable) — the orchestrator's
        // site-priority tie-break (⑩) layers on top of this determinism.
        Assert.Equal("Movie 2019 thing", scored[1].ReleaseInfo);
        Assert.Equal("Movie", scored[2].ReleaseInfo);
        Assert.Equal(0, scored[3].MatchScore);
    }

    [Fact]
    public void Score_ReturnsScoredCopy_LeavingInputUntouched()
    {
        var candidate = Candidate("Movie.2019.WEBDL-1080p");

        var scored = _scorer.Score(MovieRequest(), candidate);

        Assert.Equal(80, scored.MatchScore);
        Assert.Equal(0, candidate.MatchScore);
        Assert.NotSame(candidate, scored);
    }

    [Fact]
    public void SplitTrailingSeasonEpisode_StripsTheTrailingTag()
    {
        // R3b: the request builder reuses this split so a pre-filled
        // "Show Name S01E01" query equals the default derived request.
        var (keyword, tag) = SubtitleMatchScorer.SplitTrailingSeasonEpisode("Show Name S01E01");
        Assert.Equal("Show Name", keyword);
        Assert.Equal((1, 1), tag);

        (keyword, tag) = SubtitleMatchScorer.SplitTrailingSeasonEpisode("Show.Name.1x02");
        Assert.Equal("Show.Name", keyword);
        Assert.Equal((1, 2), tag);

        // Only the TRAILING tag splits — inner tags stay part of the keyword.
        (keyword, tag) = SubtitleMatchScorer.SplitTrailingSeasonEpisode("Show S01E01 S01E02");
        Assert.Equal("Show S01E01", keyword);
        Assert.Equal((1, 2), tag);
    }

    [Fact]
    public void SplitTrailingSeasonEpisode_WithoutATrailingTag_IsAStructuralNoOp()
    {
        var (keyword, tag) = SubtitleMatchScorer.SplitTrailingSeasonEpisode("Movie 2019 WEBDL");
        Assert.Equal("Movie 2019 WEBDL", keyword);
        Assert.Null(tag);

        // Tag-only keyword: nothing usable stays behind — returned as-is.
        (keyword, tag) = SubtitleMatchScorer.SplitTrailingSeasonEpisode("S01E01");
        Assert.Equal("S01E01", keyword);
        Assert.Null(tag);

        (keyword, tag) = SubtitleMatchScorer.SplitTrailingSeasonEpisode(null);
        Assert.Equal(string.Empty, keyword);
        Assert.Null(tag);
    }

    // --- bilingual detection + format preference (design ⑧-polish
    // tiebreaks consumed by the orchestrator's final ordering).

    [Fact]
    public void IsBilingual_LanguageFieldWithASeparatorBetweenCodes_IsBilingual()
    {
        // Documented rule: the Language field splits on non-alphanumeric
        // separators into two or more short alpha tokens.
        Assert.True(SubtitleMatchScorer.IsBilingual(Candidate("Movie.2019", language: "zh+en")));
        Assert.True(SubtitleMatchScorer.IsBilingual(Candidate("Movie.2019", language: "zh-en")));
        Assert.True(SubtitleMatchScorer.IsBilingual(Candidate("Movie.2019", language: "zh_CN&en")));

        // Single code — monolingual regardless of the release name.
        Assert.False(SubtitleMatchScorer.IsBilingual(Candidate("Movie.2019", language: "zh")));
        Assert.False(SubtitleMatchScorer.IsBilingual(Candidate("Movie.2019", language: "en")));
        // Digits/garbage tokens are not language codes.
        Assert.False(SubtitleMatchScorer.IsBilingual(Candidate("Movie.2019", language: "2019")));
    }

    [Fact]
    public void IsBilingual_ReleaseMarkerFallback_CatchesSourcesThatHideItInTheName()
    {
        // The common chs&eng naming of Chinese bilingual releases — checked
        // on the RAW release string (normalization would erase the "&").
        Assert.True(SubtitleMatchScorer.IsBilingual(
            Candidate("Movie.2019.WEBDL-1080p.chs&eng.ass", language: "zh")));
        Assert.True(SubtitleMatchScorer.IsBilingual(
            Candidate("Movie.2019.WEBDL-1080p.CHT&ENG.srt", language: "zh")));
        Assert.False(SubtitleMatchScorer.IsBilingual(
            Candidate("Movie.2019.WEBDL-1080p.chs.srt", language: "zh")));
    }

    [Fact]
    public void FormatPreference_RanksSrtBeforeAssBeforeVtt()
    {
        Assert.True(SubtitleMatchScorer.FormatPreference("srt") < SubtitleMatchScorer.FormatPreference("ass"));
        Assert.True(SubtitleMatchScorer.FormatPreference("ass") < SubtitleMatchScorer.FormatPreference("vtt"));
        // Case-insensitive; unknown formats rank last, missing too.
        Assert.Equal(SubtitleMatchScorer.FormatPreference("SRT"), SubtitleMatchScorer.FormatPreference("srt"));
        Assert.True(SubtitleMatchScorer.FormatPreference("vtt") < SubtitleMatchScorer.FormatPreference("sub"));
        Assert.True(SubtitleMatchScorer.FormatPreference("vtt") < SubtitleMatchScorer.FormatPreference(null));
        Assert.Equal(SubtitleMatchScorer.FormatPreference("zip"), SubtitleMatchScorer.FormatPreference(null));
    }
}
