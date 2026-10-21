using System.Numerics;
using Lingarr.Server.Models.FileSystem;
using MathNet.Numerics.IntegralTransforms;

namespace Lingarr.Server.Modules.Subtitle.TimelineSync;

/// <summary>One ratio's cross-correlation verdict (B05).</summary>
/// <param name="Ratio">The evaluated stretch factor.</param>
/// <param name="Evaluated">False when the ratio was skipped (duration gate).</param>
/// <param name="OffsetSeconds">Best-lag offset: corrected cue time t' = t×R + offset.</param>
/// <param name="Score">Normalized cross-correlation peak in [-1, 1].</param>
/// <param name="SkipReason">Why the ratio was not evaluated.</param>
public sealed record RatioCorrelation(
    double Ratio,
    bool Evaluated,
    double OffsetSeconds = 0,
    double Score = 0,
    string? SkipReason = null);

/// <summary>
/// The ratio enumeration's overall verdict (B05): every candidate plus the
/// chosen one, or a failure reason when nothing could be evaluated.
/// </summary>
public sealed record CorrelationOutcome(
    RatioCorrelation? Chosen,
    IReadOnlyList<RatioCorrelation> Results,
    string? FailureReason);

/// <summary>
/// FFT cross-correlation of the audio envelope against the subtitle voice
/// envelope over the nine frame-rate ratios (B05; see the implementation
/// notes in docs/replace-bazarr-csf-batches-2026-10-21.zh-CN.md):
/// <para>
/// Both envelopes are rasterized on the shared 10ms grid over the audio
/// duration, z-scored, and cross-correlated with
/// <c>r = IFFT(conj(FFT(A))·FFT(S))</c> at N = next-pow-2(2L). Math.NET's
/// transforms run with <see cref="FourierOptions.NoScaling"/> and the
/// inverse result is divided by N manually — the symmetric default
/// scaling would fold an unwanted 1/√N into every lag (verified by
/// probe: a delta-pair cross-correlation must yield exactly 1.0). The
/// peak lag m maps to <c>offset = −m×10ms</c>; the score is the peak
/// divided by the product of both norms (a normalized correlation
/// coefficient).
/// </para>
/// <para>
/// Ratio selection: a scaled candidate must beat the R=1 best by ≥10% to
/// win (anti-false-scale guard); when R=1 itself fails the duration gate
/// the scaled candidates compete directly (a PAL-converted subtitle's tail
/// overflowing the audio is a symptom, not a coincidence).
/// </para>
/// </summary>
public sealed class EnvelopeCorrelator
{
    /// <summary>
    /// The nine candidate stretch factors (B05 algorithm spec): 1.0 for a
    /// pure offset, the six pairwise ratios of the big-three cinema frame
    /// rates (23.976 NTSC film / 24 film / 25 PAL), and ±0.1% for
    /// drop-frame and stream-grab drift.
    /// </summary>
    public static readonly IReadOnlyList<double> Ratios =
    [
        1.0,
        25.0 / 23.976,
        23.976 / 25.0,
        24.0 / 23.976,
        23.976 / 24.0,
        25.0 / 24.0,
        24.0 / 25.0,
        1.001,
        0.999
    ];

    /// <summary>
    /// How far a candidate offset may reach before the service gate rejects
    /// it. The correlator itself scans every lag — an out-of-range best
    /// peak must surface as "offset out of range", not silently disappear
    /// into a clipped search window.
    /// </summary>
    public const double MaxOffsetSeconds = 700.0;

    /// <summary>
    /// The subtitle's stretched last cue may exceed the audio duration by
    /// at most 5% before the ratio is considered impossible.
    /// </summary>
    public const double DurationTolerance = 1.05;

    /// <summary>A scaled winner must beat the R=1 best by at least 10%.</summary>
    public const double ScaledWinMargin = 1.10;

    private const double WindowMs = 10.0;

    /// <summary>
    /// Correlates one audio envelope against one cue set over all ratios.
    /// Pure function of its inputs (no I/O) — the service owns every gate
    /// that needs files or settings.
    /// </summary>
    public CorrelationOutcome Correlate(AudioEnvelope audio, IReadOnlyList<SubtitleItem> cues)
    {
        if (audio.LogEnergies.Length == 0)
        {
            return new CorrelationOutcome(null, [], "the audio envelope is empty.");
        }

        if (cues.Count == 0)
        {
            return new CorrelationOutcome(null, [], "the subtitle has no cues.");
        }

        var audioNorm = ZScore(audio.LogEnergies, out var audioValues);
        if (audioNorm <= 0)
        {
            return new CorrelationOutcome(null, [], "the audio has no loudness variation to align against.");
        }

        var length = audio.LogEnergies.Length;
        int fftLength = NextPowerOfTwo(2L * length);
        var audioSpectrum = new Complex[fftLength];
        for (var i = 0; i < length; i++)
        {
            audioSpectrum[i] = new Complex(audioValues[i], 0);
        }

        Fourier.Forward(audioSpectrum, FourierOptions.NoScaling);

        var scratch = new Complex[fftLength];
        var lastEndMs = 0.0;
        foreach (var cue in cues)
        {
            lastEndMs = Math.Max(lastEndMs, cue.EndTime);
        }

        var results = new List<RatioCorrelation>(Ratios.Count);
        foreach (var ratio in Ratios)
        {
            var stretchedEndSeconds = lastEndMs * ratio / 1000.0;
            if (stretchedEndSeconds > audio.DurationSeconds * DurationTolerance)
            {
                results.Add(new RatioCorrelation(
                    ratio,
                    Evaluated: false,
                    SkipReason:
                    $"the subtitle's stretched end ({stretchedEndSeconds:0.0}s) exceeds the audio duration ({audio.DurationSeconds:0.0}s) by more than 5%"));
                continue;
            }

            var subtitleNorm = RasterizeAndZScore(cues, ratio, length, scratch, out var subtitleValues);
            if (subtitleNorm <= 0)
            {
                results.Add(new RatioCorrelation(ratio, Evaluated: false,
                    SkipReason: "the stretched subtitle lands entirely outside the audio"));
                continue;
            }

            // scratch doubles as the spectrum buffer: rasterized values live
            // in the real parts, transformed in place. NoScaling everywhere
            // plus the manual 1/N keeps the correlation in unscaled units.
            Fourier.Forward(scratch, FourierOptions.NoScaling);
            for (var i = 0; i < fftLength; i++)
            {
                scratch[i] = Complex.Conjugate(audioSpectrum[i]) * scratch[i];
            }

            Fourier.Inverse(scratch, FourierOptions.NoScaling);
            for (var i = 0; i < fftLength; i++)
            {
                scratch[i] /= fftLength;
            }

            var (offsetSeconds, score) = FindPeak(scratch, fftLength, audioNorm * subtitleNorm);
            results.Add(new RatioCorrelation(ratio, Evaluated: true, offsetSeconds, score));
        }

        return Choose(results);
    }

    /// <summary>
    /// Applies the selection rule: a scaled candidate needs the 10% margin
    /// over the R=1 best; with R=1 unevaluated the scaled candidates
    /// compete among themselves.
    /// </summary>
    private static CorrelationOutcome Choose(IReadOnlyList<RatioCorrelation> results)
    {
        RatioCorrelation? bestUnit = null;
        RatioCorrelation? bestScaled = null;
        foreach (var result in results)
        {
            if (!result.Evaluated)
            {
                continue;
            }

            if (Math.Abs(result.Ratio - 1.0) < 1e-12)
            {
                if (bestUnit is null || result.Score > bestUnit.Score)
                {
                    bestUnit = result;
                }
            }
            else if (bestScaled is null || result.Score > bestScaled.Score)
            {
                bestScaled = result;
            }
        }

        if (bestUnit is null && bestScaled is null)
        {
            return new CorrelationOutcome(
                null, results,
                "the subtitle is longer than the media audio even after every candidate frame-rate scaling (duration gate).");
        }

        if (bestScaled is not null
            && (bestUnit is null || bestScaled.Score >= bestUnit.Score * ScaledWinMargin))
        {
            return new CorrelationOutcome(bestScaled, results, null);
        }

        return new CorrelationOutcome(bestUnit, results, null);
    }

    /// <summary>
    /// Rasterizes the cue set, stretched by <paramref name="ratio"/>, onto
    /// the shared grid: window j is 1 when the stretched cue intervals
    /// overlap [j×10ms, (j+1)×10ms). The values are z-scored in place.
    /// Returns the L2 norm (0 when nothing lands on the grid).
    /// </summary>
    private static double RasterizeAndZScore(
        IReadOnlyList<SubtitleItem> cues,
        double ratio,
        int length,
        Complex[] buffer,
        out double[] values)
    {
        Array.Clear(buffer);

        foreach (var cue in cues)
        {
            // Overlap test in window units: [start/Ratio ... end/Ratio] in
            // 10ms cells, clamped to the audio span.
            var firstWindow = Math.Floor(cue.StartTime * ratio / WindowMs);
            var lastWindowExclusive = Math.Ceiling(cue.EndTime * ratio / WindowMs);
            var from = Math.Max(0, (int)firstWindow);
            var to = Math.Min(length, (int)lastWindowExclusive);
            for (var j = from; j < to; j++)
            {
                buffer[j] = new Complex(1, 0);
            }
        }

        values = new double[length];
        var sum = 0.0;
        var active = 0;
        for (var i = 0; i < length; i++)
        {
            values[i] = buffer[i].Real;
            sum += values[i];
            if (values[i] > 0)
            {
                active++;
            }
        }

        if (active == 0)
        {
            return 0;
        }

        var mean = sum / length;
        var squared = 0.0;
        for (var i = 0; i < length; i++)
        {
            var centered = values[i] - mean;
            values[i] = centered;
            squared += centered * centered;
        }

        var std = Math.Sqrt(squared / length);
        if (std < 1e-12)
        {
            return 0;
        }

        var norm = 0.0;
        for (var i = 0; i < length; i++)
        {
            values[i] /= std;
            norm += values[i] * values[i];
        }

        // The FFT consumes the z-scored values, not the raw 0/1 raster:
        // without centering the DC component would dominate the peak.
        for (var i = 0; i < length; i++)
        {
            buffer[i] = new Complex(values[i], 0);
        }

        return Math.Sqrt(norm);
    }

    /// <summary>
    /// Scans every circular lag for the correlation peak. r[m] for
    /// m &lt; N/2 is a positive lag, m ≥ N/2 wraps to m−N; the mapping to
    /// the correction offset is <c>offset = −lag×10ms</c> (see the
    /// implementation notes for the derivation).
    /// </summary>
    private static (double OffsetSeconds, double Score) FindPeak(
        Complex[] correlation, int fftLength, double normalization)
    {
        var bestValue = double.NegativeInfinity;
        var bestLag = 0L;
        var half = fftLength / 2;
        for (var m = 0; m < fftLength; m++)
        {
            var value = correlation[m].Real;
            if (value > bestValue)
            {
                bestValue = value;
                bestLag = m < half ? m : m - (long)fftLength;
            }
        }

        return (-bestLag * WindowMs / 1000.0, bestValue / normalization);
    }

    /// <summary>Z-scores values in place; returns the L2 norm (0 for a constant array).</summary>
    private static double ZScore(double[] values, out double[] scored)
    {
        scored = new double[values.Length];
        var sum = 0.0;
        foreach (var value in values)
        {
            sum += value;
        }

        var mean = sum / values.Length;
        var squared = 0.0;
        for (var i = 0; i < values.Length; i++)
        {
            var centered = values[i] - mean;
            scored[i] = centered;
            squared += centered * centered;
        }

        var std = Math.Sqrt(squared / values.Length);
        if (std < 1e-12)
        {
            return 0;
        }

        var norm = 0.0;
        for (var i = 0; i < scored.Length; i++)
        {
            scored[i] /= std;
            norm += scored[i] * scored[i];
        }

        return Math.Sqrt(norm);
    }

    private static int NextPowerOfTwo(long value)
    {
        // Envelope lengths are int-bounded and the doubling stays far below
        // 2^31 for any real media (a 2h film needs 2^21).
        var power = 1;
        while (power < value)
        {
            power <<= 1;
        }

        return power;
    }
}
