using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Lingarr.Server.Models.FileSystem;
using Lingarr.Server.Modules.Subtitle;
using Lingarr.Server.Modules.Subtitle.TimelineSync;

namespace Lingarr.Server.Tests.Fixtures.Subtitles.Timeline;

/// <summary>
/// Synthetic timeline-sync fixtures (B05): deterministic speech schedules
/// rendered both as an audio envelope (log energies on the shared 10ms
/// grid) and as SRT cue text, with the mismatch transformations — constant
/// shift, PAL frame-rate squeeze — applied to either side. Everything is
/// generated in-code (no binary fixtures): the same ground truth feeds
/// audio and subtitle, so a correct correlator must converge on the known
/// transform.
/// </summary>
public static class TimelineFixtureFactory
{
    /// <summary>Fixture media duration in seconds (60_000 envelope windows).</summary>
    public const double DurationSeconds = 600.0;

    /// <summary>Cues on the ground-truth schedule: 120 cues × 5s spacing.</summary>
    public const int CueCount = 120;

    /// <summary>Ground-truth cue length (48% speech ratio with 5s spacing).</summary>
    public const int CueDurationMs = 2_400;

    /// <summary>Ground-truth cue spacing.</summary>
    public const int CueSpacingMs = 5_000;

    /// <summary>First cue start (keeps negative-shift scenarios off zero).</summary>
    public const int FirstCueStartMs = 2_500;

    /// <summary>23.976/25 — the PAL speed-up factor (film timing → PAL timing).</summary>
    public const double PalSqueeze = 23.976 / 25.0;

    /// <summary>A strong speech window's energy, in the extractor's units.</summary>
    private const double LoudEnergy = 50.0;

    /// <summary>A silent window's residual energy (noise floor).</summary>
    private const double SilenceEnergy = 0.0002;

    /// <summary>
    /// The ground-truth speech schedule in film timing: cue k spans
    /// [2500 + k×5000, +2400) ms, k = 0..119 (last end 599.9s).
    /// </summary>
    public static List<(int StartMs, int EndMs)> GroundTruthCues() =>
        [.. Enumerable.Range(0, CueCount).Select(k => (FirstCueStartMs + k * CueSpacingMs,
            FirstCueStartMs + k * CueSpacingMs + CueDurationMs))];

    /// <summary>
    /// Renders a speech schedule as an audio envelope on the 10ms grid:
    /// loud windows log10(1+50) ≈ 1.71, silent windows ≈ 0, both with a
    /// deterministic wiggle — a constant silence floor would degenerate the
    /// correlator's z-score, and real silence is never perfectly flat
    /// either. Mirrors the extractor's log10(1+e) output shape.
    /// </summary>
    public static AudioEnvelope EnvelopeForSpeech(
        IEnumerable<(int StartMs, int EndMs)> speech,
        double durationSeconds = DurationSeconds,
        int seed = 1)
    {
        var windows = (int)(durationSeconds * 1000.0 / 10.0);
        var values = new double[windows];
        var speaking = new bool[windows];

        foreach (var (startMs, endMs) in speech)
        {
            var from = Math.Max(0, startMs / 10);
            var to = Math.Min(windows, (endMs + 9) / 10);
            for (var j = from; j < to; j++)
            {
                speaking[j] = true;
            }
        }

        for (var j = 0; j < windows; j++)
        {
            var wiggle = 0.01 * Math.Sin(j * 0.7 + seed);
            values[j] = Math.Log10(1.0 + (speaking[j] ? LoudEnergy : SilenceEnergy)) + wiggle;
        }

        return new AudioEnvelope(values);
    }

    /// <summary>
    /// A speech schedule that does not correlate with the ground truth: a
    /// distinct pseudo-random sparse pattern (~4% speech), deterministic
    /// per seed.
    /// </summary>
    public static List<(int StartMs, int EndMs)> RandomSpeech(int count = 30, int seed = 7)
    {
        var cues = new List<(int StartMs, int EndMs)>(count);
        double state = seed;
        for (var i = 0; i < count; i++)
        {
            state = (state * 1103515245 + 12345) % 2147483648;
            var start = (int)(state / 2147483648.0 * (DurationSeconds * 1000 - 5000));
            cues.Add((start, start + 400));
        }

        return cues;
    }

    /// <summary>Shifts a schedule by a constant.</summary>
    public static List<(int StartMs, int EndMs)> Shifted(
        IEnumerable<(int StartMs, int EndMs)> cues, int shiftMs) =>
        [.. cues.Select(cue => (cue.StartMs + shiftMs, cue.EndMs + shiftMs))];

    /// <summary>Scales a schedule by a factor (the frame-rate transform).</summary>
    public static List<(int StartMs, int EndMs)> Scaled(
        IEnumerable<(int StartMs, int EndMs)> cues, double factor) =>
        [.. cues.Select(cue => ((int)Math.Round(cue.StartMs * factor), (int)Math.Round(cue.EndMs * factor)))];

    /// <summary>Renders a cue schedule as parseable SRT text via the real writer.</summary>
    public static string BuildSrt(IEnumerable<(int StartMs, int EndMs)> cues, string text = "对白文本")
    {
        var items = cues.Select((cue, index) => new SubtitleItem
        {
            Position = index + 1,
            StartTime = cue.StartMs,
            EndTime = cue.EndMs,
            Lines = [text],
            PlaintextLines = [text],
            TranslatedLines = [text]
        }).ToList();

        using var buffer = new MemoryStream();
        new SrtWriter().WriteStreamAsync(
            buffer, items, stripSubtitleFormatting: false, bilingual: false).GetAwaiter().GetResult();
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>Parses SRT text back into cues (round-trip reader for assertions).</summary>
    public static List<(int StartMs, int EndMs)> ParseSrt(string content)
    {
        using var input = new MemoryStream(Encoding.UTF8.GetBytes(content));
        return new SrtParser().ParseStream(input, Encoding.UTF8)
            .Select(item => (item.StartTime, item.EndTime))
            .ToList();
    }
}
