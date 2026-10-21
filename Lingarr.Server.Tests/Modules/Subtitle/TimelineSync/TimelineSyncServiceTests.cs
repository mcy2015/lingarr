using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Lingarr.Core.Configuration;
using Lingarr.Server.Exceptions;
using Lingarr.Server.Modules.Settings;
using Lingarr.Server.Modules.Subtitle;
using Lingarr.Server.Modules.Subtitle.TimelineSync;
using Lingarr.Server.Tests.Fixtures.Subtitles.Timeline;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;
using static Lingarr.Server.Tests.Fixtures.Subtitles.Timeline.TimelineFixtureFactory;

namespace Lingarr.Server.Tests.Modules.Subtitle.TimelineSync;

/// <summary>
/// End-to-end contracts of <see cref="TimelineSyncService"/> (B05) over
/// real files in a temp directory with a synthetic envelope extractor
/// (the ffmpeg path has its own test): the acceptance fixtures — +2.5s /
/// −1.2s shifts corrected within 40ms per cue, the 23.976→25 frame-rate
/// fixture — plus idempotence, restore, every gate branch, and the
/// ffmpeg-unavailable propagation (AC3).
/// </summary>
public sealed class TimelineSyncServiceTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "lingarr-timeline-sync-" + Guid.NewGuid().ToString("N"));

    private readonly Mock<ISettingService> _settings = new();
    private readonly FakeExtractor _extractor = new();
    private readonly TimelineSyncService _service;

    public TimelineSyncServiceTests()
    {
        Directory.CreateDirectory(_directory);
        _settings
            .Setup(service => service.GetSetting(SettingKeys.SubtitleSearch.TimelineSyncMinScore))
            .ReturnsAsync("0.10");
        _service = new TimelineSyncService(
            _extractor,
            new EnvelopeCorrelator(),
            new SubtitleTimelineRewriter(new SubtitleEncodingService()),
            _settings.Object,
            NullLogger<TimelineSyncService>.Instance);
    }

    private string WriteSrt(string name, string content)
    {
        var path = Path.Combine(_directory, name);
        File.WriteAllText(path, content);
        return path;
    }

    private string WriteGroundTruthShiftedSrt(string name, int shiftMs)
    {
        var truth = GroundTruthCues();
        _extractor.Envelope = EnvelopeForSpeech(truth);
        return WriteSrt(name, BuildSrt(Shifted(truth, shiftMs)));
    }

    [Fact]
    public async Task ShiftedPlus2500_CorrectsEveryCueWithin40Ms()
    {
        var path = WriteGroundTruthShiftedSrt("movie.2023.zh.srt", 2500);

        var outcome = await _service.SyncFileAsync(path, path + ".mkv", CancellationToken.None);

        Assert.Equal(TimelineSyncStatus.Corrected, outcome.Status);
        Assert.True(outcome.Cues > 0);
        var corrected = ParseSrt(File.ReadAllText(path));
        var truth = GroundTruthCues();
        Assert.Equal(truth.Count, corrected.Count);
        for (var i = 0; i < truth.Count; i++)
        {
            Assert.True(Math.Abs(corrected[i].StartMs - truth[i].StartMs) < 40,
                $"cue {i}: start {corrected[i].StartMs} vs truth {truth[i].StartMs}");
            Assert.True(Math.Abs(corrected[i].EndMs - truth[i].EndMs) < 40,
                $"cue {i}: end {corrected[i].EndMs} vs truth {truth[i].EndMs}");
        }

        Assert.True(File.Exists(path + SubtitleTimelineRewriter.BackupSuffix));
    }

    [Fact]
    public async Task ShiftedMinus1200_CorrectsEveryCueWithin40Ms()
    {
        var path = WriteGroundTruthShiftedSrt("movie.2023.zh.srt", -1200);

        var outcome = await _service.SyncFileAsync(path, path + ".mkv", CancellationToken.None);

        Assert.Equal(TimelineSyncStatus.Corrected, outcome.Status);
        var corrected = ParseSrt(File.ReadAllText(path));
        var truth = GroundTruthCues();
        for (var i = 0; i < truth.Count; i++)
        {
            Assert.True(Math.Abs(corrected[i].StartMs - truth[i].StartMs) < 40);
            Assert.True(Math.Abs(corrected[i].EndMs - truth[i].EndMs) < 40);
        }
    }

    [Fact]
    public async Task FrameRateMismatch_23976To25_CorrectsWithinTenthOfAPercent()
    {
        // Film-timed subtitle on the PAL-speed-up video (see the correlator
        // fixture): the rewrite must map every cue onto the audio schedule.
        var film = GroundTruthCues();
        _extractor.Envelope = EnvelopeForSpeech(Scaled(film, PalSqueeze));
        var path = WriteSrt("movie.2023.zh.srt", BuildSrt(film));

        var outcome = await _service.SyncFileAsync(path, path + ".mkv", CancellationToken.None);

        Assert.Equal(TimelineSyncStatus.Corrected, outcome.Status);
        Assert.Equal(PalSqueeze, outcome.Ratio, 9);

        var corrected = ParseSrt(File.ReadAllText(path));
        var expected = Scaled(film, PalSqueeze);
        Assert.Equal(expected.Count, corrected.Count);
        var tolerance = DurationSeconds * 1000 * 0.001; // 0.1% of the duration
        for (var i = 0; i < expected.Count; i++)
        {
            Assert.True(Math.Abs(corrected[i].StartMs - expected[i].StartMs) < tolerance,
                $"cue {i}: start {corrected[i].StartMs} vs expected {expected[i].StartMs}");
            Assert.True(Math.Abs(corrected[i].EndMs - expected[i].EndMs) < tolerance);
        }
    }

    [Fact]
    public async Task SecondRun_IsIdempotentAndKeepsTheBackupIntact()
    {
        var path = WriteGroundTruthShiftedSrt("movie.2023.zh.srt", 2500);
        await _service.SyncFileAsync(path, path + ".mkv", CancellationToken.None);

        var backup = path + SubtitleTimelineRewriter.BackupSuffix;
        var backupBytes = File.ReadAllBytes(backup);
        var backupTime = File.GetLastWriteTimeUtc(backup);
        var correctedBytes = File.ReadAllBytes(path);
        _extractor.Calls = 0;

        var second = await _service.SyncFileAsync(path, path + ".mkv", CancellationToken.None);

        Assert.Equal(TimelineSyncStatus.AlreadyCorrected, second.Status);
        Assert.Equal(0, _extractor.Calls); // the gate fires before any decode
        Assert.Equal(backupBytes, File.ReadAllBytes(backup));
        Assert.Equal(backupTime, File.GetLastWriteTimeUtc(backup));
        Assert.Equal(correctedBytes, File.ReadAllBytes(path));
    }

    [Fact]
    public async Task Restore_ReturnsTheOriginalBytes()
    {
        var path = WriteGroundTruthShiftedSrt("movie.2023.zh.srt", 2500);
        var original = File.ReadAllBytes(path);
        await _service.SyncFileAsync(path, path + ".mkv", CancellationToken.None);
        Assert.NotEqual(original, File.ReadAllBytes(path));

        await _service.RestoreFileAsync(path, CancellationToken.None);

        Assert.Equal(original, File.ReadAllBytes(path));
        Assert.False(File.Exists(path + SubtitleTimelineRewriter.BackupSuffix));
    }

    [Fact]
    public async Task RestoreWithoutBackup_Throws()
    {
        var path = WriteSrt("movie.2023.zh.srt", BuildSrt(GroundTruthCues()));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.RestoreFileAsync(path, CancellationToken.None));
    }

    [Fact]
    public async Task AlignedFile_NeedsNoCorrectionAndWritesNothing()
    {
        var truth = GroundTruthCues();
        _extractor.Envelope = EnvelopeForSpeech(truth);
        var path = WriteSrt("movie.2023.zh.srt", BuildSrt(truth));
        var before = File.ReadAllBytes(path);

        var outcome = await _service.SyncFileAsync(path, path + ".mkv", CancellationToken.None);

        Assert.Equal(TimelineSyncStatus.NoCorrectionNeeded, outcome.Status);
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.False(File.Exists(path + SubtitleTimelineRewriter.BackupSuffix));
    }

    [Fact]
    public async Task Uncorrelated_FailsOnScoreWithoutTouchingTheFile()
    {
        _extractor.Envelope = EnvelopeForSpeech(GroundTruthCues());
        var path = WriteSrt("movie.2023.zh.srt", BuildSrt(RandomSpeech()));
        var before = File.ReadAllBytes(path);

        var outcome = await _service.SyncFileAsync(path, path + ".mkv", CancellationToken.None);

        Assert.Equal(TimelineSyncStatus.Failed, outcome.Status);
        Assert.Contains("below the configured minimum", outcome.Reason);
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.False(File.Exists(path + SubtitleTimelineRewriter.BackupSuffix));
    }

    [Fact]
    public async Task OffsetBeyond700s_FailsOnTheSanityGate()
    {
        var sparse = RandomSpeech(20, seed: 3)
            .Select(cue => (StartMs: cue.StartMs / 6, EndMs: cue.StartMs / 6 + 400))
            .ToList();
        _extractor.Envelope = EnvelopeForSpeech(sparse, durationSeconds: 800);
        var path = WriteSrt("movie.2023.zh.srt", BuildSrt(Shifted(sparse, 710_000)));

        var outcome = await _service.SyncFileAsync(path, path + ".mkv", CancellationToken.None);

        Assert.Equal(TimelineSyncStatus.Failed, outcome.Status);
        Assert.Contains("sanity limit", outcome.Reason);
        Assert.False(File.Exists(path + SubtitleTimelineRewriter.BackupSuffix));
    }

    [Fact]
    public async Task SubtitleLongerThanAudio_FailsOnTheDurationGate()
    {
        _extractor.Envelope = EnvelopeForSpeech(GroundTruthCues());
        var path = WriteSrt("movie.2023.zh.srt", BuildSrt(Shifted(GroundTruthCues(), 60_000)));

        var outcome = await _service.SyncFileAsync(path, path + ".mkv", CancellationToken.None);

        Assert.Equal(TimelineSyncStatus.Failed, outcome.Status);
        Assert.NotNull(outcome.Reason);
    }

    [Fact]
    public async Task ExistingBackup_SkipsBeforeDecoding()
    {
        var path = WriteSrt("movie.2023.zh.srt", BuildSrt(GroundTruthCues()));
        File.WriteAllText(path + SubtitleTimelineRewriter.BackupSuffix, "previous backup");

        var outcome = await _service.SyncFileAsync(path, path + ".mkv", CancellationToken.None);

        Assert.Equal(TimelineSyncStatus.AlreadyCorrected, outcome.Status);
        Assert.Equal(0, _extractor.Calls);
        Assert.Equal("previous backup", File.ReadAllText(path + SubtitleTimelineRewriter.BackupSuffix));
    }

    [Fact]
    public async Task MissingSubtitleFile_Fails()
    {
        var outcome = await _service.SyncFileAsync(
            Path.Combine(_directory, "absent.srt"), "/video.mkv", CancellationToken.None);

        Assert.Equal(TimelineSyncStatus.Failed, outcome.Status);
    }

    [Fact]
    public async Task UnsupportedExtension_Fails()
    {
        var path = WriteSrt("movie.2023.zh.sub", "garbage");

        var outcome = await _service.SyncFileAsync(path, path + ".mkv", CancellationToken.None);

        Assert.Equal(TimelineSyncStatus.Failed, outcome.Status);
        Assert.Contains("unsupported subtitle format", outcome.Reason);
    }

    [Fact]
    public async Task UnparseableSubtitle_FailsWithParseReason()
    {
        var path = WriteSrt("movie.2023.zh.srt", "not a subtitle at all");
        _extractor.Envelope = EnvelopeForSpeech(GroundTruthCues());

        var outcome = await _service.SyncFileAsync(path, path + ".mkv", CancellationToken.None);

        Assert.Equal(TimelineSyncStatus.Failed, outcome.Status);
        Assert.Contains("could not parse", outcome.Reason);
    }

    [Fact]
    public async Task FfmpegUnavailable_PropagatesAndKeepsTheFile()
    {
        _extractor.Throw = new FFprobeUnavailableException("ffmpeg was not found on PATH.");
        var path = WriteSrt("movie.2023.zh.srt", BuildSrt(GroundTruthCues()));
        var before = File.ReadAllBytes(path);

        await Assert.ThrowsAsync<FFprobeUnavailableException>(
            () => _service.SyncFileAsync(path, path + ".mkv", CancellationToken.None));

        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.False(File.Exists(path + SubtitleTimelineRewriter.BackupSuffix));
    }

    [Fact]
    public async Task MinScoreSetting_FallsBackToDefaultWhenUnparsable()
    {
        _settings
            .Setup(service => service.GetSetting(SettingKeys.SubtitleSearch.TimelineSyncMinScore))
            .ReturnsAsync("not a number");
        var truth = GroundTruthCues();
        _extractor.Envelope = EnvelopeForSpeech(truth);
        var path = WriteSrt("movie.2023.zh.srt", BuildSrt(RandomSpeech()));

        var outcome = await _service.SyncFileAsync(path, path + ".mkv", CancellationToken.None);

        Assert.Equal(TimelineSyncStatus.Failed, outcome.Status);
        Assert.Contains("0.10", outcome.Reason);
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    /// <summary>Envelope source standing in for the ffmpeg extractor.</summary>
    private sealed class FakeExtractor : IAudioEnvelopeExtractor
    {
        public AudioEnvelope? Envelope { get; set; }
        public Exception? Throw { get; set; }
        public int Calls { get; set; }

        public Task<AudioEnvelope> ExtractAsync(string videoPath, CancellationToken cancellationToken)
        {
            Calls++;
            if (Throw is not null)
            {
                throw Throw;
            }

            return Task.FromResult(Envelope ?? throw new InvalidOperationException("fixture envelope not set"));
        }
    }
}
