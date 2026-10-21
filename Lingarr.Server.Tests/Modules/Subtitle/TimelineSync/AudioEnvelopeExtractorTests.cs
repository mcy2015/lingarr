using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Lingarr.Server.Exceptions;
using Lingarr.Server.Modules.Subtitle.TimelineSync;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Lingarr.Server.Tests.Modules.Subtitle.TimelineSync;

/// <summary>
/// Process-level contracts of <see cref="AudioEnvelopeExtractor"/> (B05).
/// The "ffmpeg binary" is a generated shell script that replays a fixed
/// PCM file on stdout (the repo has no fake-process precedent; a script
/// exercises the real spawn/stream/accumulate path without needing ffmpeg
/// installed). One guarded test uses real ffmpeg when present — it is
/// tagged Integration because it depends on the host environment (it
/// self-skips otherwise; CI carries no ffmpeg).
/// </summary>
public sealed class AudioEnvelopeExtractorTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "lingarr-envelope-" + Guid.NewGuid().ToString("N"));

    public AudioEnvelopeExtractorTests()
    {
        Directory.CreateDirectory(_directory);
    }

    /// <summary>
    /// 2 seconds of 16kHz mono s16le PCM alternating loud (amplitude
    /// 20000) and silent 10ms windows → exactly 200 windows, 320 bytes
    /// each.
    /// </summary>
    private string WriteAlternatingPcm()
    {
        var path = Path.Combine(_directory, "fixed.pcm");
        using var output = File.Create(path);
        var buffer = new byte[320];
        for (var window = 0; window < 200; window++)
        {
            if (window % 2 == 0)
            {
                for (var sample = 0; sample < 160; sample++)
                {
                    var value = sample % 2 == 0 ? 20_000 : -20_000; // crude loud square wave
                    buffer[sample * 2] = (byte)(value & 0xFF);
                    buffer[sample * 2 + 1] = (byte)((value >> 8) & 0xFF);
                }
            }
            else
            {
                Array.Clear(buffer, 0, buffer.Length);
            }

            output.Write(buffer, 0, buffer.Length);
        }

        return path;
    }

    private string WriteScript(string body)
    {
        var path = Path.Combine(_directory, "fake-ffmpeg.sh");
        File.WriteAllText(path, "#!/bin/sh\n" + body + "\n");
        using var chmod = Process.Start(new ProcessStartInfo
        {
            FileName = "/bin/chmod",
            Arguments = $"+x \"{path}\"",
            UseShellExecute = false,
            CreateNoWindow = true
        });
        chmod?.WaitForExit(5000);
        return path;
    }

    private static AudioEnvelopeExtractor ExtractorFor(string binary) => new(
        NullLogger<AudioEnvelopeExtractor>.Instance,
        ffmpegPathResolver: () => binary);

    [Fact]
    public async Task ScriptReplayingPcm_ProducesTheWindowEnergies()
    {
        var pcm = WriteAlternatingPcm();
        var script = WriteScript($"cat \"{pcm}\"");
        var extractor = ExtractorFor(script);

        var envelope = await extractor.ExtractAsync("/any/video.mkv", CancellationToken.None);

        Assert.Equal(200, envelope.LogEnergies.Length);
        Assert.Equal(2.0, envelope.DurationSeconds, 3);
        for (var j = 0; j < envelope.LogEnergies.Length; j++)
        {
            if (j % 2 == 0)
            {
                // 160 × (20000/32768)² ≈ 59.6 → log10(60.6) ≈ 1.78
                Assert.InRange(envelope.LogEnergies[j], 1.6, 1.9);
            }
            else
            {
                Assert.InRange(envelope.LogEnergies[j], -0.01, 0.01);
            }
        }
    }

    [Fact]
    public async Task NonZeroExit_FailsWithExitCode()
    {
        var script = WriteScript("echo 'boom' >&2\nexit 1");
        var extractor = ExtractorFor(script);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => extractor.ExtractAsync("/any/video.mkv", CancellationToken.None));
        Assert.Contains("exited with code 1", ex.Message);
        Assert.Contains("boom", ex.Message);
    }

    [Fact]
    public async Task NoAudio_FailsWithNoAudio()
    {
        var script = WriteScript("exit 0");
        var extractor = ExtractorFor(script);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => extractor.ExtractAsync("/any/video.mkv", CancellationToken.None));
        Assert.Contains("no audio", ex.Message);
    }

    [Fact]
    public async Task UnstartableBinary_ThrowsFfprobeUnavailable()
    {
        var extractor = ExtractorFor(Path.Combine(_directory, "does-not-exist"));

        await Assert.ThrowsAsync<FFprobeUnavailableException>(
            () => extractor.ExtractAsync("/any/video.mkv", CancellationToken.None));
    }

    [Fact]
    public async Task ResolverThrowingUnavailable_Propagates()
    {
        var extractor = new AudioEnvelopeExtractor(
            NullLogger<AudioEnvelopeExtractor>.Instance,
            ffmpegPathResolver: () => throw new FFprobeUnavailableException("ffmpeg was not found on PATH."));

        var ex = await Assert.ThrowsAsync<FFprobeUnavailableException>(
            () => extractor.ExtractAsync("/any/video.mkv", CancellationToken.None));
        Assert.Contains("not found on PATH", ex.Message);
    }

    /// <summary>
    /// Real-ffmpeg smoke test: hand-writes a 2s 440Hz sine WAV and decodes
    /// it through the production argument list. Skips (passes trivially)
    /// on hosts without ffmpeg — the CI image does not carry one; the
    /// script tests above cover the process plumbing.
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task RealFfmpeg_DecodesSineWaveWav_WhenPresent()
    {
        var ffmpeg = ResolveFfmpegOrNull();
        if (ffmpeg is null)
        {
            return; // ffmpeg not installed here — nothing to smoke-test
        }

        var wav = WriteSineWav();
        var envelope = await new AudioEnvelopeExtractor(
                NullLogger<AudioEnvelopeExtractor>.Instance,
                ffmpegPathResolver: () => ffmpeg)
            .ExtractAsync(wav, CancellationToken.None);

        Assert.True(envelope.LogEnergies.Length >= 195, $"expected ~200 windows, got {envelope.LogEnergies.Length}");
        Assert.All(envelope.LogEnergies, value => Assert.True(value > 0.1, "a constant sine must look loud"));
    }

    private static string? ResolveFfmpegOrNull()
    {
        var configured = Environment.GetEnvironmentVariable("LINGARR_FFMPEG_PATH");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
        {
            return configured;
        }

        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(directory.Trim(), "ffmpeg");
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch (ArgumentException)
            {
                // skip malformed PATH entries
            }
        }

        return null;
    }

    private string WriteSineWav()
    {
        const int sampleRate = 16_000;
        const int samples = sampleRate * 2;
        var path = Path.Combine(_directory, "sine.wav");
        using var output = File.Create(path);
        using var writer = new BinaryWriter(output);

        writer.Write("RIFF"u8);
        writer.Write(36 + samples * 2);
        writer.Write("WAVE"u8);
        writer.Write("fmt "u8);
        writer.Write(16);
        writer.Write((short)1); // PCM
        writer.Write((short)1); // mono
        writer.Write(sampleRate);
        writer.Write(sampleRate * 2); // byte rate
        writer.Write((short)2); // block align
        writer.Write((short)16); // bits
        writer.Write("data"u8);
        writer.Write(samples * 2);
        for (var i = 0; i < samples; i++)
        {
            var value = (short)(12_000 * Math.Sin(2 * Math.PI * 440 * i / sampleRate));
            writer.Write(value);
        }

        writer.Flush();
        return path;
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
