using System.Diagnostics;
using Lingarr.Server.Exceptions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lingarr.Server.Modules.Subtitle.TimelineSync;

/// <summary>
/// Production <see cref="IAudioEnvelopeExtractor"/> (B05): streams
/// <c>ffmpeg -i {video} -vn -ac 1 -ar 16000 -f s16le -</c> and folds the
/// stdout PCM into 10ms log-energy windows while it arrives — the raw PCM
/// never materializes (a two-hour movie is ~230MB of s16le; the envelope
/// is ~5.7MB of doubles).
/// <para>
/// ffmpeg resolution follows the EmbeddedTrackService precedent
/// (<c>LINGARR_FFMPEG_PATH</c> → first ffmpeg on PATH →
/// <see cref="FFprobeUnavailableException"/> with an install hint), with
/// the resolver injectable so tests can point at a script that replays
/// fixed PCM bytes.
/// </para>
/// </summary>
public sealed class AudioEnvelopeExtractor : IAudioEnvelopeExtractor
{
    /// <summary>The shared envelope grid width (16kHz → 160 samples per window).</summary>
    public const double WindowSizeMs = 10.0;

    private const int SampleRate = 16_000;
    private const int SamplesPerWindow = (int)(SampleRate * WindowSizeMs / 1000); // 160
    private const int BytesPerWindow = SamplesPerWindow * 2; // s16le
    private const int ReadChunkBytes = 64 * 1024;

    /// <summary>
    /// Generous wall-clock budget for one full-media decode: audio-only
    /// decoding runs many times realtime even on NAS hardware, but a
    /// wedged process must never pin a download landing forever.
    /// </summary>
    private static readonly TimeSpan DecodeTimeout = TimeSpan.FromMinutes(10);

    private readonly ILogger<AudioEnvelopeExtractor> _logger;
    private readonly Func<string> _ffmpegPathResolver;

    private string? _ffmpegPath;
    private bool _ffmpegResolved;

    public AudioEnvelopeExtractor(
        ILogger<AudioEnvelopeExtractor>? logger = null,
        Func<string>? ffmpegPathResolver = null)
    {
        _logger = logger ?? NullLogger<AudioEnvelopeExtractor>.Instance;
        _ffmpegPathResolver = ffmpegPathResolver ?? ResolveFfmpegPath;
    }

    /// <inheritdoc />
    public async Task<AudioEnvelope> ExtractAsync(string videoPath, CancellationToken cancellationToken)
    {
        var binary = _ffmpegPathResolver();
        var startInfo = new ProcessStartInfo
        {
            FileName = binary,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true, // -nostdin also set; belt and suspenders
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in new[]
                 {
                     "-nostdin",
                     "-i", videoPath,
                     "-vn",
                     "-ac", "1",
                     "-ar", SampleRate.ToString(),
                     "-f", "s16le",
                     "pipe:1"
                 })
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        try
        {
            process.Start();
            process.StandardInput.Close(); // ffmpeg never needs our stdin
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new FFprobeUnavailableException(
                $"ffmpeg binary '{binary}' could not be started.", ex);
        }

        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            var envelope = await ReadEnvelopeAsync(process.StandardOutput.BaseStream, cancellationToken);

            // stdout is fully consumed (ffmpeg exited or hit EOF); now wait
            // for the process itself with the decode budget.
            var exitTask = process.WaitForExitAsync(cancellationToken);
            var completed = await Task.WhenAny(exitTask, Task.Delay(DecodeTimeout, cancellationToken));
            if (completed != exitTask)
            {
                cancellationToken.ThrowIfCancellationRequested();
                process.Kill(entireProcessTree: true);
                throw new TimeoutException(
                    $"ffmpeg exceeded the {DecodeTimeout.TotalMinutes:0}m audio decode timeout for '{videoPath}'.");
            }

            await exitTask;

            if (process.ExitCode != 0)
            {
                var stderr = await stderrTask;
                throw new InvalidOperationException(
                    $"ffmpeg exited with code {process.ExitCode} while decoding '{Path.GetFileName(videoPath)}'." +
                    $"{(string.IsNullOrWhiteSpace(stderr) ? string.Empty : " " + stderr.Trim())}");
            }

            if (envelope.LogEnergies.Length == 0)
            {
                var stderr = await stderrTask;
                throw new InvalidOperationException(
                    $"ffmpeg produced no audio for '{Path.GetFileName(videoPath)}'." +
                    $"{(string.IsNullOrWhiteSpace(stderr) ? string.Empty : " " + stderr.Trim())}");
            }

            _logger.LogDebug(
                "Extracted a {Windows}-window ({Seconds:0.0}s) audio envelope from {Video}.",
                envelope.LogEnergies.Length, envelope.DurationSeconds, videoPath);
            return envelope;
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
            throw;
        }
        catch (IOException)
        {
            // The pipe died mid-read (usually a non-zero ffmpeg exit); let the
            // exit-code branch produce the actionable message by re-reading a
            // drained stream is not possible — surface the read failure with
            // whatever stderr says instead.
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
            var stderr = await SafelyDrainAsync(stderrTask);
            throw new InvalidOperationException(
                $"ffmpeg audio stream broke while decoding '{Path.GetFileName(videoPath)}'." +
                $"{(string.IsNullOrWhiteSpace(stderr) ? string.Empty : " " + stderr.Trim())}");
        }
    }

    /// <summary>
    /// Streams the s16le stdout into per-window log energies. A trailing
    /// partial window (odd byte or sub-160-sample remainder) is folded in
    /// with the samples it has — ffmpeg pads nothing, and a half window at
    /// the file tail never moves a correlation peak.
    /// </summary>
    private static async Task<AudioEnvelope> ReadEnvelopeAsync(
        Stream output, CancellationToken cancellationToken)
    {
        var windows = new List<double>(4096);
        var buffer = new byte[ReadChunkBytes];
        var carry = 0; // leftover bytes of a sample split across reads
        double energy = 0; // running energy of the current window
        var samplesInWindow = 0;

        int read;
        while ((read = await output.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)) > 0)
        {
            var offset = 0;
            if (carry > 0)
            {
                // Complete the split sample with the first byte of this chunk.
                var sample = (short)(carry & 0xFF | (buffer[0] << 8));
                energy += SampleSquare(sample);
                samplesInWindow++;
                offset = 1;
                carry = 0;

                (energy, samplesInWindow) = FlushWhenFull(windows, energy, samplesInWindow);
            }

            var availableBytes = read - offset;
            var position = offset;

            // Whole samples first.
            var wholeBytes = availableBytes - (availableBytes & 1);
            var end = position + wholeBytes;
            while (position < end)
            {
                var sample = (short)(buffer[position] | (buffer[position + 1] << 8));
                energy += SampleSquare(sample);
                samplesInWindow++;

                if (samplesInWindow == SamplesPerWindow)
                {
                    windows.Add(LogEnergy(energy));
                    energy = 0;
                    samplesInWindow = 0;
                }

                position += 2;
            }

            if (position < read)
            {
                carry = buffer[position]; // high byte arrives with the next read
            }
        }

        if (samplesInWindow > 0)
        {
            windows.Add(LogEnergy(energy)); // trailing partial window
        }

        return new AudioEnvelope([.. windows]);
    }

    private static (double Energy, int Samples) FlushWhenFull(
        List<double> windows, double energy, int samples)
    {
        if (samples == SamplesPerWindow)
        {
            windows.Add(LogEnergy(energy));
            return (0, 0);
        }

        return (energy, samples);
    }

    private static double SampleSquare(short sample)
    {
        var normalized = sample / 32768.0;
        return normalized * normalized;
    }

    /// <summary>The spec's log compression: v = log10(1 + e) (B05 implementation notes).</summary>
    private static double LogEnergy(double energy) => Math.Log10(1.0 + energy);

    private static async Task<string> SafelyDrainAsync(Task<string> task)
    {
        try
        {
            return await task;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    /// <summary>ffmpeg counterpart of the ffprobe resolution precedent (env → PATH → hint).</summary>
    private string ResolveFfmpegPath()
    {
        if (_ffmpegResolved)
        {
            return _ffmpegPath!;
        }

        _ffmpegPath = ResolveBinaryPath("ffmpeg", "LINGARR_FFMPEG_PATH");
        _ffmpegResolved = true;
        return _ffmpegPath;
    }

    private static string ResolveBinaryPath(string binary, string environmentVariable)
    {
        var configured = Environment.GetEnvironmentVariable(environmentVariable);
        if (!string.IsNullOrWhiteSpace(configured))
        {
            if (File.Exists(configured))
            {
                return configured;
            }

            throw new FFprobeUnavailableException(
                $"{environmentVariable} points at '{configured}' but no file exists there.");
        }

        var onPath = FindOnPath(binary);
        if (onPath == null)
        {
            throw new FFprobeUnavailableException(
                $"{binary} was not found on PATH. Install ffmpeg (the image does this for you) " +
                $"or point {environmentVariable} at the {binary} binary, then run the timeline sync again.");
        }

        return onPath;
    }

    private static string? FindOnPath(string binary)
    {
        string[] names = OperatingSystem.IsWindows()
            ? [$"{binary}.exe", $"{binary}.bat"]
            : [binary];

        var pathVariable = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var directory in pathVariable.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                foreach (var name in names)
                {
                    var candidate = Path.Combine(directory.Trim(), name);
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
            }
            catch (ArgumentException)
            {
                // A malformed PATH entry cannot hold the binary; skip it.
            }
        }

        return null;
    }
}
