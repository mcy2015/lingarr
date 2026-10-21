namespace Lingarr.Server.Modules.Subtitle.TimelineSync;

/// <summary>
/// The audio loudness envelope of one media file, sampled on the shared
/// 10ms grid (B05): one value per window, each the log-compressed energy
/// of that window's decoded 16kHz mono samples. The array length IS the
/// media's audio duration (length × 10ms) — the correlator's duration
/// gate reads it directly, so no second ffprobe call is needed.
/// </summary>
/// <param name="LogEnergies">
/// Per-window log energy v = log10(1 + Σ(x/32768)²). Silence ≈ 0, a
/// full-scale speech window ≈ 1.9 (see the B05 implementation notes:
/// log compression folds ~60dB of dynamics into ~2 orders of magnitude).
/// </param>
/// <param name="WindowMs">Window width in milliseconds (fixed at 10).</param>
public sealed record AudioEnvelope(double[] LogEnergies, double WindowMs = AudioEnvelopeExtractor.WindowSizeMs)
{
    /// <summary>Audio duration covered by the envelope.</summary>
    public double DurationSeconds => LogEnergies.Length * WindowMs / 1000.0;
}

/// <summary>
/// Extracts the audio envelope of a video file by streaming ffmpeg's
/// s16le output (B05). The extraction is a separate seam so tests can feed
/// synthetic envelopes without decoding real media — a full-movie decode
/// has no place inside a unit test run.
/// </summary>
public interface IAudioEnvelopeExtractor
{
    /// <summary>
    /// Decodes the media's audio track and returns its 10ms log-energy
    /// envelope. PCM never materializes in memory: stdout is consumed in
    /// chunks and folded into per-window energies as it arrives.
    /// </summary>
    /// <param name="videoPath">Path of the media container.</param>
    /// <param name="cancellationToken">Caller cancellation; kills ffmpeg when triggered.</param>
    /// <exception cref="Lingarr.Server.Exceptions.FFprobeUnavailableException">
    /// ffmpeg cannot be resolved or started (LINGARR_FFMPEG_PATH / PATH —
    /// the ResolveBinaryPath precedent).
    /// </exception>
    /// <exception cref="InvalidOperationException">ffmpeg exited non-zero (no/broken audio track).</exception>
    Task<AudioEnvelope> ExtractAsync(string videoPath, CancellationToken cancellationToken);
}
