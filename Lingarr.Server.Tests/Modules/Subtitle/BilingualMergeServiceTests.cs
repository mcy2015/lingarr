using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Lingarr.Core.Configuration;
using Lingarr.Server.Modules.Settings;
using Lingarr.Server.Modules.Subtitle;
using Lingarr.Server.Modules.Translation;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Lingarr.Server.Tests.Modules.Subtitle;

/// <summary>
/// B06 acceptance fixtures of <see cref="BilingualMergeService"/> over real
/// files in a temp directory: 完全对齐 (100 cues, jitter ≤200ms → 100
/// bilingual cues), 部分错位 (≥500ms misalignment leaves both cues
/// monolingual), 一空一满 (an empty counterpart passes every cue through),
/// plus the naming-template expansion, the ASS round trip and the
/// request-shape refusals.
/// </summary>
public sealed class BilingualMergeServiceTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "lingarr-bilingual-merge-" + Guid.NewGuid().ToString("N"));

    private readonly Mock<ISettingService> _settings = new();
    private readonly SubtitleService _subtitleService;
    private readonly BilingualMergeService _service;

    public BilingualMergeServiceTests()
    {
        Directory.CreateDirectory(_directory);
        // No naming template configured → CreateFilePath falls back to the
        // historical "{原名}.{语言}" (tests that need a custom template set
        // it up per-case).
        _settings
            .Setup(service => service.GetSetting(It.IsAny<string>()))
            .ReturnsAsync((string?)null);
        _subtitleService = new SubtitleService(
            NullLogger<SubtitleService>.Instance,
            new LanguageCodeService(),
            _settings.Object);
        _service = new BilingualMergeService(
            _subtitleService,
            NullLogger<BilingualMergeService>.Instance);
    }

    /// <summary>Cue fixture: 700ms duration, 1000ms period — the B06 acceptance grid.</summary>
    private static string Srt(IEnumerable<(int StartMs, string Text)> cues) => string.Join(string.Empty, cues.Select((cue, index) =>
        $"{index + 1}\n{Format(cue.StartMs)} --> {Format(cue.StartMs + 700)}\n{cue.Text}\n\n"));

    private static string Format(int milliseconds)
    {
        var time = TimeSpan.FromMilliseconds(milliseconds);
        return $"{(int)time.TotalHours:00}:{time.Minutes:00}:{time.Seconds:00},{time.Milliseconds:00}";
    }

    private string WriteSrt(string name, string content)
    {
        var path = Path.Combine(_directory, name);
        File.WriteAllText(path, content);
        return path;
    }

    private string WriteAss(string name, IReadOnlyList<(int StartMs, string Text)> cues)
    {
        var dialogues = string.Concat(cues.Select(cue =>
            $"Dialogue: Marked=0,{AssTime(cue.StartMs)},{AssTime(cue.StartMs + 700)},Default,,0000,0000,0000,,{cue.Text}\n"));
        var content = "[Script Info]\nScriptType: v4.00+\nWrapStyle: 0\n\n[V4+ Styles]\n" +
                      "Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding\n" +
                      "Style: Default,Arial,20,&H00FFFFFF,&H000000FF,&H00000000,&H00000000,0,0,0,0,100,100,0,0,1,1,0,2,20,20,23,1\n\n" +
                      "[Events]\nFormat: Marked, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text\n" +
                      dialogues;
        return WriteSrt(name, content);
    }

    private static string AssTime(int milliseconds)
    {
        var time = TimeSpan.FromMilliseconds(milliseconds);
        return $"{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}.{time.Milliseconds / 10:00}";
    }

    private async Task<BilingualMergeResult> Merge100CueFixtureAsync(
        IReadOnlyList<int> secondaryShifts,
        IReadOnlyList<int> primaryShifts)
    {
        var primaryPath = WriteSrt("movie.2023.zh.srt",
            Srt(Enumerable.Range(0, 100).Select(i => (i * 1000 + primaryShifts[i], $"中文第{i}行"))));
        var secondaryPath = WriteSrt("movie.2023.en.srt",
            Srt(Enumerable.Range(0, 100).Select(i => (i * 1000 + secondaryShifts[i], $"line {i}"))));

        return await _service.MergeAsync(
            primaryPath, "zh", secondaryPath, "en", CancellationToken.None);
    }

    [Fact]
    public async Task FullyAligned100Cues_PairsAllAndReparsesAsBilingual()
    {
        var result = await Merge100CueFixtureAsync(
            secondaryShifts: Enumerable.Repeat(0, 100).ToList(),
            primaryShifts: Enumerable.Repeat(0, 100).ToList());

        // 验收 1: 100 cues each misaligned ≤200ms → 100 bilingual output cues.
        Assert.Equal(100, result.PairedCues);
        Assert.Equal(0, result.PrimaryOnlyCues);
        Assert.Equal(0, result.SecondaryOnlyCues);

        // Output naming: {base}.{primary}.{secondary}.srt (default template).
        Assert.Equal(
            Path.Combine(_directory, "movie.2023.zh.en.srt"),
            result.OutputPath);

        // 验收 2: the output reparses as bilingual — every cue carries the
        // zh line on top and the en line below.
        var reparsed = await _subtitleService.ReadSubtitles(result.OutputPath);
        Assert.Equal(100, reparsed.Count);
        for (var i = 0; i < reparsed.Count; i++)
        {
            Assert.Equal($"中文第{i}行", reparsed[i].Lines[0]);
            Assert.Equal($"line {i}", reparsed[i].Lines[1]);
            Assert.Equal(i * 1000, reparsed[i].StartTime);
        }
    }

    [Fact]
    public async Task JitterUpTo200Ms_StillPairsEveryCue()
    {
        // Alternating 0/150/200ms jitter on both sides (never the same shift,
        // so the pairwise misalignment reaches 200ms).
        var shifts = Enumerable.Range(0, 100).Select(i => i % 3 * 100).ToList();

        var result = await Merge100CueFixtureAsync(
            secondaryShifts: shifts,
            primaryShifts: Enumerable.Repeat(0, 100).ToList());

        Assert.Equal(100, result.PairedCues);
        Assert.Equal(0, result.PrimaryOnlyCues + result.SecondaryOnlyCues);
    }

    [Fact]
    public async Task Misaligned600Ms_DoesNotPairAndKeepsBothCuesMonolingual()
    {
        // Cue 20 and 21 of the secondary lag by 600ms — above the 500ms
        // pairing boundary on this sub-second cue grid (same-cue overlap
        // 100/700 = 14%, neighbour overlap 300/700 = 43%, both < 50%).
        var secondaryShifts = Enumerable.Repeat(0, 100).ToList();
        secondaryShifts[20] = 600;
        secondaryShifts[21] = 600;

        var result = await Merge100CueFixtureAsync(
            secondaryShifts: secondaryShifts,
            primaryShifts: Enumerable.Repeat(0, 100).ToList());

        // The two 600ms-misaligned cues per side stay monolingual; the
        // streams re-sync right after, so every remaining cue still pairs
        // (100 - 2 flushed primary cues = 98 pairs, 102 output cues total).
        Assert.Equal(98, result.PairedCues);
        Assert.Equal(2, result.PrimaryOnlyCues);
        Assert.Equal(2, result.SecondaryOnlyCues);

        // 验收 2 on the mixed output: 98 bilingual cues (two lines) plus the
        // four monolingual survivors (one line) — two zh, two en.
        var reparsed = await _subtitleService.ReadSubtitles(result.OutputPath);
        Assert.Equal(102, reparsed.Count);

        var bilingual = reparsed.Where(cue => cue.Lines.Count == 2).ToList();
        var monolingual = reparsed.Where(cue => cue.Lines.Count == 1).ToList();
        Assert.Equal(98, bilingual.Count);
        Assert.Equal(4, monolingual.Count);
        Assert.Contains(monolingual, cue => cue.Lines[0] == "中文第20行");
        Assert.Contains(monolingual, cue => cue.Lines[0] == "中文第21行");
        Assert.Contains(monolingual, cue => cue.Lines[0] == "line 20");
        Assert.Contains(monolingual, cue => cue.Lines[0] == "line 21");

        // The misaligned neighbours never cross-pair: every bilingual cue
        // matches zh line i with en line i.
        foreach (var cue in bilingual)
        {
            var index = int.Parse(cue.Lines[1]["line ".Length..]);
            Assert.Equal($"中文第{index}行", cue.Lines[0]);
        }
    }

    [Fact]
    public async Task OneEmptyOneFull_PassesEveryCueThroughMonolingual()
    {
        var primaryPath = WriteSrt("movie.2023.zh.srt",
            Srt(Enumerable.Range(0, 30).Select(i => (i * 1000, $"中文第{i}行"))));
        var secondaryPath = WriteSrt("movie.2023.en.srt", string.Empty);

        var result = await _service.MergeAsync(
            primaryPath, "zh", secondaryPath, "en", CancellationToken.None);

        Assert.Equal(0, result.PairedCues);
        Assert.Equal(30, result.PrimaryCues);
        Assert.Equal(30, result.PrimaryOnlyCues);
        Assert.Equal(0, result.SecondaryOnlyCues);

        var reparsed = await _subtitleService.ReadSubtitles(result.OutputPath);
        Assert.Equal(30, reparsed.Count);
        Assert.All(reparsed, cue =>
        {
            Assert.Single(cue.Lines);
            Assert.StartsWith("中文第", cue.Lines[0]);
        });
    }

    [Fact]
    public async Task AssPrimaryMergedWithSrtSecondary_ReparsesAsBilingual()
    {
        var primaryPath = WriteAss("movie.2023.zh.ass", [(1000, "中文一"), (3000, "中文二")]);
        var secondaryPath = WriteSrt("movie.2023.en.srt",
            Srt([(1000, "line one"), (3000, "line two")]));

        var result = await _service.MergeAsync(
            primaryPath, "zh", secondaryPath, "en", CancellationToken.None);

        Assert.Equal(2, result.PairedCues);
        Assert.Equal(
            Path.Combine(_directory, "movie.2023.zh.en.ass"),
            result.OutputPath);

        // The ASS round trip: the primary text rides the Dialogue line and
        // the secondary is appended below (its {\fs16} size tag strips out
        // of the plaintext form).
        var reparsed = await _subtitleService.ReadSubtitles(result.OutputPath);
        Assert.Equal(2, reparsed.Count);
        Assert.Equal("中文一", reparsed[0].Lines[0]);
        Assert.Equal("line one", reparsed[0].PlaintextLines[^1]);
        Assert.Equal("中文二", reparsed[1].Lines[0]);
        Assert.Equal("line two", reparsed[1].PlaintextLines[^1]);
    }

    [Fact]
    public async Task CustomNamingTemplate_IsRespected()
    {
        _settings
            .Setup(service => service.GetSetting(SettingKeys.Translation.SubtitleNamingTemplate))
            .ReturnsAsync("{原名}.{语言}.dual");

        var primaryPath = WriteSrt("movie.2023.zh.srt", Srt([(1000, "中文一")]));
        var secondaryPath = WriteSrt("movie.2023.en.srt", Srt([(1000, "line one")]));

        var result = await _service.MergeAsync(
            primaryPath, "zh", secondaryPath, "en", CancellationToken.None);

        Assert.EndsWith("movie.2023.zh.dual.en.srt", result.OutputPath);
    }

    [Fact]
    public async Task SameLanguage_IsRefused()
    {
        var primaryPath = WriteSrt("movie.2023.zh.srt", Srt([(1000, "中文一")]));
        var secondaryPath = WriteSrt("movie.2023.chs.srt", Srt([(1000, "中文二")]));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.MergeAsync(primaryPath, "zh", secondaryPath, "zh", CancellationToken.None));

        Assert.Contains("same language", exception.Message);
    }

    [Fact]
    public async Task SamePathTwice_IsRefused()
    {
        var path = WriteSrt("movie.2023.zh.srt", Srt([(1000, "中文一")]));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.MergeAsync(path, "zh", path, "en", CancellationToken.None));
    }

    [Fact]
    public async Task MissingFile_IsRefused()
    {
        var primaryPath = WriteSrt("movie.2023.zh.srt", Srt([(1000, "中文一")]));
        var missing = Path.Combine(_directory, "movie.2023.en.srt");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.MergeAsync(primaryPath, "zh", missing, "en", CancellationToken.None));

        Assert.Contains("was not found on the server", exception.Message);
    }

    [Fact]
    public async Task BothFilesEmpty_IsRefused()
    {
        var primaryPath = WriteSrt("movie.2023.zh.srt", string.Empty);
        var secondaryPath = WriteSrt("movie.2023.en.srt", string.Empty);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.MergeAsync(primaryPath, "zh", secondaryPath, "en", CancellationToken.None));

        Assert.Contains("nothing to merge", exception.Message);
    }

    [Fact]
    public async Task TemplateResolvingOntoAnInput_IsRefused()
    {
        // "{原名}" drops every language tag; with the secondary tag appended
        // the output would land exactly on the secondary input file
        // ({base}.en.srt) — the guard refuses before anything is written.
        _settings
            .Setup(service => service.GetSetting(SettingKeys.Translation.SubtitleNamingTemplate))
            .ReturnsAsync("{原名}");

        var primaryPath = WriteSrt("movie.2023.zh.srt", Srt([(1000, "中文一")]));
        var secondaryPath = WriteSrt("movie.2023.en.srt", Srt([(1000, "line one")]));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.MergeAsync(primaryPath, "zh", secondaryPath, "en", CancellationToken.None));

        Assert.Contains("naming template", exception.Message);
        Assert.Contains("line one", File.ReadAllText(secondaryPath));
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
