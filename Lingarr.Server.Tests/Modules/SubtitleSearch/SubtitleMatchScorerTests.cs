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
}
