using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Lingarr.Core.Configuration;
using Lingarr.Core.Entities;
using Lingarr.Core.Enum;
using Lingarr.Server.Models.FileSystem;
using Lingarr.Server.Modules.Subtitle;
using Lingarr.Server.Modules.Subtitle.PostProcessing;
using Lingarr.Server.Modules.SubtitleSearch;
using Lingarr.Server.Modules.SubtitleSearch.Abstractions;
using Lingarr.Server.Modules.SubtitleSearch.Pipeline;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Lingarr.Server.Tests.Modules.SubtitleSearch;

/// <summary>
/// Pipeline/post-processing integration contracts (B01): stages run AFTER
/// the atomic write and BEFORE the history row, in DI order, with the
/// variant mode resolved from settings; a stage failure never sinks the
/// landed file (Succeeded row + error field naming the stage, no rethrow);
/// mode "none"/unset keeps the landed bytes identical to a build without
/// the framework (regression gate); the season-pack fan-out post-processes
/// every landed entry too.
/// </summary>
public class SubtitleDownloadPipelinePostProcessTests
{
    private const string TraditionalSrt =
        "1\r\n00:00:01,000 --> 00:00:02,000\r\n繁體中文字幕測試\r\n";

    /// <summary>
    /// The exact bytes the pipeline's own ConvertToSrt step (default on)
    /// produces for <see cref="TraditionalSrt"/> — the pre-B01 landed form
    /// every byte-identity assertion compares against.
    /// </summary>
    private static byte[] RenderedSrt =>
        Encoding.UTF8.GetBytes("1\n00:00:01,000 --> 00:00:02,000\n繁體中文字幕測試\n\n");

    private static byte[] Payload => Encoding.UTF8.GetBytes(TraditionalSrt);

    private static SubtitleCandidate Candidate(string language = "en") => new(
        "fake",
        ExternalId: "ext-1",
        language,
        MatchScore: 90,
        Forced: false,
        HearingImpaired: false,
        ReleaseInfo: "Movie.2019.WEBDL-1080p",
        Uploader: null,
        Format: "srt",
        Size: 1234,
        HashMatch: false);

    /// <summary>Provider double serving the traditional-Chinese SRT payload.</summary>
    private sealed class TraditionalProvider : ISubtitleProvider
    {
        public string Provider => "fake";

        public Task<SubtitleSearchResult> SearchAsync(
            SubtitleSearchRequest request, CancellationToken cancellationToken)
            => Task.FromResult(SubtitleSearchResult.Empty);

        public Task<Lingarr.Server.Modules.SubtitleSearch.Abstractions.SubtitleDownload> DownloadAsync(
            SubtitleCandidateRef candidateRef, CancellationToken cancellationToken)
            => Task.FromResult(new Lingarr.Server.Modules.SubtitleSearch.Abstractions.SubtitleDownload(
                "fake", "Movie.2019.WEBDL-1080p.srt", "srt", Payload));
    }

    /// <summary>Provider double serving a two-entry episode-marked season pack.</summary>
    private sealed class SeasonPackProvider(byte[] archive) : ISubtitleProvider
    {
        public string Provider => "fake";

        public Task<SubtitleSearchResult> SearchAsync(
            SubtitleSearchRequest request, CancellationToken cancellationToken)
            => Task.FromResult(SubtitleSearchResult.Empty);

        public Task<Lingarr.Server.Modules.SubtitleSearch.Abstractions.SubtitleDownload> DownloadAsync(
            SubtitleCandidateRef candidateRef, CancellationToken cancellationToken)
            => Task.FromResult(new Lingarr.Server.Modules.SubtitleSearch.Abstractions.SubtitleDownload(
                "fake", "pack.zip", "zip", archive));
    }

    /// <summary>Captures what the file looked like when the stage ran.</summary>
    private sealed class RecordingStage(List<string> calls) : ISubtitlePostProcessor
    {
        public List<(string Path, byte[]? Bytes, string? Mode)> Observations { get; } = [];

        public string Name => "Recorder";

        public Task<PostProcessResult> ProcessAsync(
            string filePath, PostProcessContext context, CancellationToken cancellationToken)
        {
            calls.Add(Name);
            Observations.Add((filePath, File.Exists(filePath) ? File.ReadAllBytes(filePath) : null, context.ChineseVariantMode));
            return Task.FromResult(PostProcessResult.Unchanged("recorder"));
        }
    }

    private static Mock<ISubtitlePostProcessor> Stage(
        string name,
        List<string>? calls = null,
        PostProcessResult? result = null)
    {
        var stage = new Mock<ISubtitlePostProcessor>();
        stage.SetupGet(processor => processor.Name).Returns(name);
        stage
            .Setup(processor => processor.ProcessAsync(
                It.IsAny<string>(), It.IsAny<PostProcessContext>(), It.IsAny<CancellationToken>()))
            .Callback(() => calls?.Add(name))
            .ReturnsAsync(result ?? PostProcessResult.Unchanged("noop"));
        return stage;
    }

    private static byte[] ZipOf(params (string Name, string Text)[] entries)
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, text) in entries)
            {
                var entry = archive.CreateEntry(name);
                using var writer = new StreamWriter(entry.Open());
                writer.Write(text);
            }
        }

        return buffer.ToArray();
    }

    [Fact]
    public async Task Download_RunsStagesAfterTheAtomicWriteWithTheConfiguredMode()
    {
        using var fixture = new SubtitleEndpointFixture(new TraditionalProvider());
        await fixture.Settings.SetSetting(
            SettingKeys.SubtitleSearch.PostProcessChineseVariant, "simplified");
        var recorder = new RecordingStage(new List<string>());
        fixture.PostProcessStages.Add(recorder);

        var outcome = await fixture.Pipeline.DownloadAsync(
            Candidate("en"), MediaType.Movie, fixture.MovieId, SubtitleDownloadSource.Manual,
            searchSessionId: null, CancellationToken.None);

        Assert.Equal(SubtitleDownloadResult.Succeeded, outcome.Result);
        var observation = Assert.Single(recorder.Observations);
        // The stage saw the fully written file — post-processing runs after
        // the atomic write, before the history row.
        Assert.Equal(RenderedSrt, observation.Bytes);
        Assert.Equal("simplified", observation.Mode);
        Assert.Equal(outcome.FilePath, observation.Path);
    }

    [Fact]
    public async Task Download_StagesRunInRegistrationOrder()
    {
        using var fixture = new SubtitleEndpointFixture(new TraditionalProvider());
        var calls = new List<string>();
        var first = Stage("Alpha", calls);
        var second = Stage("Beta", calls);
        fixture.PostProcessStages.Add(first.Object);
        fixture.PostProcessStages.Add(second.Object);

        await fixture.Pipeline.DownloadAsync(
            Candidate("en"), MediaType.Movie, fixture.MovieId, SubtitleDownloadSource.Manual,
            searchSessionId: null, CancellationToken.None);

        Assert.Equal(new[] { "Alpha", "Beta" }, calls);
    }

    [Fact]
    public async Task Download_StageFailure_KeepsTheFileAndNamesTheStageInTheHistoryRow()
    {
        using var fixture = new SubtitleEndpointFixture(new TraditionalProvider());
        await fixture.Settings.SetSetting(
            SettingKeys.SubtitleSearch.PostProcessChineseVariant, "simplified");
        var exploding = Stage("Exploder");
        exploding
            .Setup(processor => processor.ProcessAsync(
                It.IsAny<string>(), It.IsAny<PostProcessContext>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("stage exploded"));
        var after = Stage("StillRuns");
        fixture.PostProcessStages.Add(exploding.Object);
        fixture.PostProcessStages.Add(after.Object);

        // No rethrow: a stage problem must never sink the download.
        var outcome = await fixture.Pipeline.DownloadAsync(
            Candidate("en"), MediaType.Movie, fixture.MovieId, SubtitleDownloadSource.Manual,
            searchSessionId: null, CancellationToken.None);

        Assert.Equal(SubtitleDownloadResult.Succeeded, outcome.Result);
        Assert.NotNull(outcome.Error);
        Assert.Contains("Exploder", outcome.Error);
        // The subtitle still landed, byte-identical to the pre-stage form.
        Assert.Equal(RenderedSrt, File.ReadAllBytes(outcome.FilePath!));
        after.Verify(processor => processor.ProcessAsync(
            It.IsAny<string>(), It.IsAny<PostProcessContext>(), It.IsAny<CancellationToken>()), Times.Once);

        var history = await fixture.DbContext.SubtitleDownloads.SingleAsync();
        Assert.Equal(SubtitleDownloadResult.Succeeded, history.Result);
        Assert.Contains("Exploder", history.Error);
    }

    [Fact]
    public async Task Download_NoneMode_LandsBytesIdenticalToPreFramework()
    {
        // Regression gate (B01 AC1): with the mode unset OR "none" the real
        // variant stage must leave the landed bytes exactly as the pipeline
        // wrote them — no parse, no rewrite.
        using var fixture = new SubtitleEndpointFixture(new TraditionalProvider());
        fixture.PostProcessStages.Add(new ChineseVariantStage(
            new SubtitleEncodingService(), NullLogger<ChineseVariantStage>.Instance));

        var unset = await fixture.Pipeline.DownloadAsync(
            Candidate("en"), MediaType.Movie, fixture.MovieId, SubtitleDownloadSource.Manual,
            searchSessionId: null, CancellationToken.None);
        Assert.Null(unset.Error);
        Assert.Equal(RenderedSrt, File.ReadAllBytes(unset.FilePath!));

        await fixture.Settings.SetSetting(
            SettingKeys.SubtitleSearch.PostProcessChineseVariant, "none");
        File.Delete(unset.FilePath!);
        var none = await fixture.Pipeline.DownloadAsync(
            Candidate("en"), MediaType.Movie, fixture.MovieId, SubtitleDownloadSource.Manual,
            searchSessionId: null, CancellationToken.None);
        Assert.Null(none.Error);
        Assert.Equal(RenderedSrt, File.ReadAllBytes(none.FilePath!));
    }

    [Fact]
    public async Task Download_SimplifiedMode_LandsSimplifiedSrt()
    {
        using var fixture = new SubtitleEndpointFixture(new TraditionalProvider());
        await fixture.Settings.SetSetting(
            SettingKeys.SubtitleSearch.PostProcessChineseVariant, "simplified");
        fixture.PostProcessStages.Add(new ChineseVariantStage(
            new SubtitleEncodingService(), NullLogger<ChineseVariantStage>.Instance));

        var outcome = await fixture.Pipeline.DownloadAsync(
            Candidate("en"), MediaType.Movie, fixture.MovieId, SubtitleDownloadSource.Manual,
            searchSessionId: null, CancellationToken.None);

        Assert.Equal(SubtitleDownloadResult.Succeeded, outcome.Result);
        Assert.Null(outcome.Error);
        var landed = File.ReadAllText(outcome.FilePath!);
        Assert.Contains("繁体中文字幕测试", landed);
        Assert.DoesNotContain("繁體", landed);
    }

    [Fact]
    public async Task Download_SeasonPack_PostProcessesEveryLandedEntry()
    {
        var entryName = (int season, int episode) =>
            $"6ixtynin9.The.Series.S{season:D2}E{episode:D2}.720p.NF.WEBRip.x264-GalaxyTV.srt";
        using var fixture = new SubtitleEndpointFixture(new SeasonPackProvider(ZipOf(
            (entryName(1, 1), TraditionalSrt),
            (entryName(1, 2), TraditionalSrt))));
        var episode1 = fixture.SeedEpisode(1, 1);
        var episode2 = fixture.SeedEpisode(1, 2);
        await fixture.Settings.SetSetting(
            SettingKeys.SubtitleSearch.PostProcessChineseVariant, "simplified");
        var recorder = new RecordingStage(new List<string>());
        fixture.PostProcessStages.Add(new ChineseVariantStage(
            new SubtitleEncodingService(), NullLogger<ChineseVariantStage>.Instance));
        fixture.PostProcessStages.Add(recorder);

        var outcome = await fixture.Pipeline.DownloadAsync(
            Candidate("zh"), MediaType.Episode, episode2.Id, SubtitleDownloadSource.Manual,
            searchSessionId: null, CancellationToken.None);

        Assert.Equal(SubtitleDownloadResult.Succeeded, outcome.Result);
        Assert.Equal(2, outcome.FilesLanded);
        Assert.Null(outcome.Error);

        // Every landed file went through post-processing AND the conversion.
        Assert.Equal(2, recorder.Observations.Count);
        Assert.All(recorder.Observations, observation => Assert.Equal("simplified", observation.Mode));
        foreach (var episode in new[] { episode1, episode2 })
        {
            var subtitle = Path.Combine(episode.Directory, episode.VideoFileName[..^".mkv".Length] + ".srt");
            Assert.Contains("繁体中文字幕测试", File.ReadAllText(subtitle));
        }
    }
}
