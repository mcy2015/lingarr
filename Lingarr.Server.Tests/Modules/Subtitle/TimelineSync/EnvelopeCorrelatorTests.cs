using System;
using System.Collections.Generic;
using System.Linq;
using Lingarr.Server.Models.FileSystem;
using Lingarr.Server.Modules.Subtitle.TimelineSync;
using Lingarr.Server.Tests.Fixtures.Subtitles.Timeline;
using Xunit;
using static Lingarr.Server.Tests.Fixtures.Subtitles.Timeline.TimelineFixtureFactory;

namespace Lingarr.Server.Tests.Modules.Subtitle.TimelineSync;

/// <summary>
/// Correlation contracts of <see cref="EnvelopeCorrelator"/> (B05): known
/// synthetic transforms must be identified exactly (offset within one
/// 10ms window, ratio within 1e-9), the anti-false-scale rule must pick
/// R=1 for pure shifts and the true ratio for frame-rate mismatches, and
/// the duration gate must skip impossible ratios instead of evaluating
/// them.
/// </summary>
public sealed class EnvelopeCorrelatorTests
{
    private readonly EnvelopeCorrelator _correlator = new();

    private static List<SubtitleItem> ItemsOf(IEnumerable<(int StartMs, int EndMs)> cues) =>
        [.. cues.Select(cue => new SubtitleItem { StartTime = cue.StartMs, EndTime = cue.EndMs })];

    [Fact]
    public void PureShift_Plus2500_ConvergesWithinOneWindow()
    {
        var truth = GroundTruthCues();
        var audio = EnvelopeForSpeech(truth);

        var outcome = _correlator.Correlate(audio, ItemsOf(Shifted(truth, 2500)));

        Assert.NotNull(outcome.Chosen);
        Assert.Equal(1.0, outcome.Chosen.Ratio, 9);
        // Subtitle late by 2.5s → correction offset −2.5s.
        Assert.InRange(outcome.Chosen.OffsetSeconds, -2.510, -2.490);
        Assert.True(outcome.Chosen.Score >= 0.10, $"score {outcome.Chosen.Score:0.000} must clear the default gate");
    }

    [Fact]
    public void PureShift_Minus1200_ConvergesWithinOneWindow()
    {
        var truth = GroundTruthCues();
        var audio = EnvelopeForSpeech(truth);

        var outcome = _correlator.Correlate(audio, ItemsOf(Shifted(truth, -1200)));

        Assert.NotNull(outcome.Chosen);
        Assert.Equal(1.0, outcome.Chosen.Ratio, 9);
        Assert.InRange(outcome.Chosen.OffsetSeconds, 1.190, 1.210);
        Assert.True(outcome.Chosen.Score >= 0.10);
    }

    [Fact]
    public void Aligned_R1FindsZeroOffset()
    {
        var truth = GroundTruthCues();
        var audio = EnvelopeForSpeech(truth);

        var outcome = _correlator.Correlate(audio, ItemsOf(truth));

        var unit = outcome.Results.Single(r => Math.Abs(r.Ratio - 1.0) < 1e-12);
        Assert.True(unit.Evaluated);
        Assert.InRange(unit.OffsetSeconds, -0.010, 0.010);
        Assert.True(unit.Score >= 0.10);
    }

    [Fact]
    public void FrameRateMismatch_FilmSubtitleOnPalVideo_IdentifiesSqueezeRatio()
    {
        // The subtitle was authored on the 23.976fps film cut; the video is
        // the 25fps PAL speed-up, so every audio event sits at film time ×
        // 23.976/25. The correction t' = t×R therefore needs R = 23.976/25.
        var film = GroundTruthCues();
        var pal = Scaled(film, PalSqueeze);
        var audio = EnvelopeForSpeech(pal);

        var outcome = _correlator.Correlate(audio, ItemsOf(film));

        Assert.NotNull(outcome.Chosen);
        Assert.Equal(PalSqueeze, outcome.Chosen.Ratio, 9);
        Assert.InRange(Math.Abs(outcome.Chosen.OffsetSeconds), 0, 0.05);
        Assert.True(outcome.Chosen.Score >= 0.10);

        // The 10% rule is what promotes the scaled candidate: R=1 must be
        // clearly below the winner on this fixture.
        var unit = outcome.Results.Single(r => Math.Abs(r.Ratio - 1.0) < 1e-12);
        Assert.True(unit.Evaluated);
        Assert.True(outcome.Chosen.Score >= unit.Score * 1.10,
            $"winner {outcome.Chosen.Score:0.000} vs R=1 {unit.Score:0.000}");
    }

    [Fact]
    public void PureShift_NeverPicksAScaledRatio()
    {
        var truth = GroundTruthCues();
        var audio = EnvelopeForSpeech(truth);

        var outcome = _correlator.Correlate(audio, ItemsOf(Shifted(truth, 2500)));

        // Even though ±0.1% candidates drift little on a 600s span, the
        // margin rule must keep R=1 as the chosen candidate.
        Assert.Equal(1.0, outcome.Chosen!.Ratio, 9);
    }

    [Fact]
    public void Uncorrelated_PeakStaysBelowDefaultGate()
    {
        var audio = EnvelopeForSpeech(GroundTruthCues());

        var outcome = _correlator.Correlate(audio, ItemsOf(RandomSpeech()));

        // Whatever the peak, it must not look like an alignment.
        Assert.True(outcome.Chosen!.Score < 0.10,
            $"uncorrelated peak {outcome.Chosen.Score:0.000} must stay below the 0.10 gate");
    }

    [Fact]
    public void ShiftBeyond700s_FullLagScanStillFindsTruePeak()
    {
        // Sparse speech in the first 100s of an 800s track, subtitle late
        // by 710s: the true peak sits outside ±700s — the correlator must
        // still find it (the service's sanity gate consumes it), rather
        // than hiding it inside a clipped search window.
        var sparse = RandomSpeech(20, seed: 3)
            .Select(cue => (StartMs: cue.StartMs / 6, EndMs: cue.StartMs / 6 + 400))
            .ToList();
        var audio = EnvelopeForSpeech(sparse, durationSeconds: 800);

        var outcome = _correlator.Correlate(audio, ItemsOf(Shifted(sparse, 710_000)));

        Assert.NotNull(outcome.Chosen);
        Assert.InRange(outcome.Chosen.OffsetSeconds, -710.010, -709.990);
        Assert.True(outcome.Chosen.Score >= 0.10);
    }

    [Fact]
    public void SubtitleLongerThanAudio_AllRatiosSkipped()
    {
        var truth = GroundTruthCues();
        var audio = EnvelopeForSpeech(truth);

        // 660s of subtitle against 600s of audio: even the biggest squeeze
        // (×0.95904) lands at 633s > 630s (600×1.05) — every ratio skipped.
        var longCues = Shifted(truth, 60_000);
        var outcome = _correlator.Correlate(audio, ItemsOf(longCues));

        Assert.Null(outcome.Chosen);
        Assert.NotNull(outcome.FailureReason);
        Assert.Contains("duration", outcome.FailureReason);
        Assert.All(outcome.Results, result => Assert.False(result.Evaluated));
    }

    [Fact]
    public void DurationGate_SkipsOnlyOverflowingRatios()
    {
        var truth = GroundTruthCues();
        var audio = EnvelopeForSpeech(truth);

        // Last end 599.9s: R=1 fits (599.9 ≤ 630), the ×1.04271 stretch
        // (625.5s) fits too, but a schedule ending at 620s overflows only
        // past the stretch ratios.
        var stretchedEnd = Shifted(truth, 20_000); // last end 619.9s
        var outcome = _correlator.Correlate(audio, ItemsOf(stretchedEnd));

        var unit = outcome.Results.Single(r => Math.Abs(r.Ratio - 1.0) < 1e-12);
        Assert.True(unit.Evaluated); // 619.9 ≤ 630

        var stretch = outcome.Results.Single(r => Math.Abs(r.Ratio - 25.0 / 23.976) < 1e-9);
        Assert.False(stretch.Evaluated); // 646.4 > 630
        Assert.NotNull(stretch.SkipReason);
    }

    [Fact]
    public void EmptyCues_FailsCleanly()
    {
        var audio = EnvelopeForSpeech(GroundTruthCues());
        var outcome = _correlator.Correlate(audio, []);

        Assert.Null(outcome.Chosen);
        Assert.NotNull(outcome.FailureReason);
    }
}
