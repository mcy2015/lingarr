using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Lingarr.Core.Configuration;
using Lingarr.Core.Data;
using Lingarr.Core.Entities;
using Lingarr.Core.Enum;
using Lingarr.Server.Modules.Infrastructure;
using Lingarr.Server.Modules.SubtitleSearch.Abstractions;
using Lingarr.Server.Modules.SubtitleSearch.Jobs;
using Lingarr.Server.Modules.SubtitleSearch.Scoring;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Lingarr.Server.Tests.Modules.SubtitleSearch.Jobs;

/// <summary>
/// One-round contracts of the subtitle upgrade re-search batch (B02): the
/// upgrade verdict (a candidate must beat max(auto score, old score +
/// margin) to land; same score never replaces), the replacement itself (new
/// file lands through the pipeline with Source=Upgrade, the old file is
/// deleted only after the replacement exists, a same-path landing overwrites
/// atomically and deletes nothing), the candidate-window conditions (file
/// still on disk, inside the window, score at or below 100 - margin), the
/// master-switch short-circuit and the cancellation contract (a cancelled
/// round writes no history row, marks nothing failed and rethrows
/// OperationCanceledException).
/// <para>
/// Real SQLite + the shared <see cref="SubtitleEndpointFixture"/> because
/// the whole decision graph (selection SQL, request building, orchestrator
/// scoring, download pipeline, history rows) is relational; the subtitle
/// service stays the fixture's mock so the landing path is exactly
/// predictable ("&lt;video&gt;.srt", no language tag) — which is also what
/// makes the replaced-path vs same-path split testable.
/// </para>
/// </summary>
public class SubtitleUpgradeJobTests
{
    private const string SrtText = "1\r\n00:00:01,000 --> 00:00:02,000\r\n你好世界\r\n";

    /// <summary>
    /// The landing path the fixture's mocked naming service yields for the
    /// seeded movie: template input "Movie.2019.WEBDL-1080p.mkv" + ".srt",
    /// CreateFilePath passthrough (no language tag).
    /// </summary>
    private static string LandingPath(UpgradeJobFixture fixture) =>
        Path.Combine(fixture.MediaDirectory, "Movie.2019.WEBDL-1080p.srt");

    private static SubtitleCandidate Candidate(
        string language = "zh",
        string externalId = "ext-upgrade-1",
        bool hashMatch = false) => new(
        "scripted",
        ExternalId: externalId,
        language,
        MatchScore: 0,
        Forced: false,
        HearingImpaired: false,
        ReleaseInfo: "Movie.2019.WEBDL-1080p",
        Uploader: null,
        Format: "srt",
        Size: 1234,
        hashMatch);

    [Fact]
    public async Task Execute_BetterCandidate_LandsWithUpgradeSourceAndDeletesTheOldFile()
    {
        // Acceptance shape: old score 70, margin 3 (default), auto score 75
        // (default) → the line is max(75, 73) = 75; the candidate scores 80
        // (title 55 + year 15 + full release tokens 10) → upgrade. The new
        // file lands, the OLD path (different from the landing path) is
        // deleted, and the history row records Source=Upgrade.
        using var fixture = new UpgradeJobFixture(request => [Candidate()]);
        await fixture.Settings.SetSetting(SettingKeys.SubtitleSearch.UpgradeEnabled, "true");
        var oldPath = fixture.SeedSucceededDownload(
            score: 70, fileName: "Movie.2019.WEBDL-1080p.zh.v1.srt");

        await fixture.Job.Execute();

        var landed = LandingPath(fixture);
        Assert.True(File.Exists(landed), $"the upgrade must land on disk ({landed})");
        Assert.Contains("你好世界", File.ReadAllText(landed));
        Assert.False(File.Exists(oldPath), "the superseded file must be deleted after the replacement landed");

        var upgrade = await fixture.HistoryRowAsync(SubtitleDownloadSource.Upgrade);
        Assert.Equal(SubtitleDownloadResult.Succeeded, upgrade.Result);
        Assert.Equal(80, upgrade.MatchScore);
        Assert.Equal(landed, upgrade.FilePath);
        Assert.Equal("zh", upgrade.Language);

        Assert.Equal(1, fixture.Source.SearchCalls);
        Assert.Equal(1, fixture.Source.DownloadCalls);
        fixture.ScheduleService.Verify(
            service => service.UpdateJobState(It.IsAny<string>(), "Processing"), Times.Once);
        fixture.ScheduleService.Verify(
            service => service.UpdateJobState(It.IsAny<string>(), "Succeeded"), Times.Once);
    }

    [Fact]
    public async Task Execute_SameLandingPath_OverwritesAtomicallyAndDeletesNothing()
    {
        // The other half of the replacement contract: when the naming
        // template yields the SAME path (a same-name re-download), the
        // atomic write already replaced the bytes and no delete runs — the
        // media never sits without its subtitle.
        using var fixture = new UpgradeJobFixture(request => [Candidate()]);
        await fixture.Settings.SetSetting(SettingKeys.SubtitleSearch.UpgradeEnabled, "true");
        var oldPath = fixture.SeedSucceededDownload(
            score: 70, fileName: "Movie.2019.WEBDL-1080p.srt", content: "OLD-BYTES");
        Assert.Equal(LandingPath(fixture), oldPath);

        await fixture.Job.Execute();

        Assert.True(File.Exists(oldPath));
        Assert.Contains("你好世界", File.ReadAllText(oldPath)); // replaced, not deleted
        var upgrade = await fixture.HistoryRowAsync(SubtitleDownloadSource.Upgrade);
        Assert.Equal(SubtitleDownloadResult.Succeeded, upgrade.Result);
        Assert.Equal(oldPath, upgrade.FilePath);
    }

    [Fact]
    public async Task Execute_BelowTheUpgradedLine_KeepsEverythingAndRecordsLowConfidence()
    {
        // Old score 79, margin 3 → the line is max(75, 82) = 82; the best
        // candidate scores 80 → nothing lands, nothing is deleted, and the
        // LowConfidence row answers "why did nothing change" with both
        // scores (the honest skip, never a silent one).
        using var fixture = new UpgradeJobFixture(request => [Candidate()]);
        await fixture.Settings.SetSetting(SettingKeys.SubtitleSearch.UpgradeEnabled, "true");
        var oldPath = fixture.SeedSucceededDownload(
            score: 79, fileName: "Movie.2019.WEBDL-1080p.zh.v1.srt", content: "OLD-BYTES");

        await fixture.Job.Execute();

        Assert.True(File.Exists(oldPath));
        Assert.Equal("OLD-BYTES", File.ReadAllText(oldPath));
        Assert.False(File.Exists(LandingPath(fixture)));

        var skip = await fixture.HistoryRowAsync(SubtitleDownloadSource.Upgrade);
        Assert.Equal(SubtitleDownloadResult.LowConfidenceSkipped, skip.Result);
        Assert.Equal(80, skip.MatchScore);
        Assert.Equal(string.Empty, skip.FilePath);
        Assert.NotNull(skip.Error);
        Assert.Contains("80 (< 82", skip.Error!);
        Assert.Contains("old score 79", skip.Error!);

        Assert.Equal(1, fixture.Source.SearchCalls);
        Assert.Equal(0, fixture.Source.DownloadCalls); // never fetched below the line
    }

    [Fact]
    public async Task Execute_SameScore_NeverReplaces()
    {
        // Anti-loop core: old score 80, candidate 80 → the line is 83, the
        // same score is below it — identical content would otherwise be
        // re-landed every round.
        using var fixture = new UpgradeJobFixture(request => [Candidate()]);
        await fixture.Settings.SetSetting(SettingKeys.SubtitleSearch.UpgradeEnabled, "true");
        var oldPath = fixture.SeedSucceededDownload(
            score: 80, fileName: "Movie.2019.WEBDL-1080p.zh.v1.srt", content: "OLD-BYTES");

        await fixture.Job.Execute();

        Assert.True(File.Exists(oldPath));
        Assert.Equal(0, fixture.Source.DownloadCalls);
        var skip = await fixture.HistoryRowAsync(SubtitleDownloadSource.Upgrade);
        Assert.Equal(SubtitleDownloadResult.LowConfidenceSkipped, skip.Result);
        Assert.Contains("80 (< 83", skip.Error!);
    }

    [Fact]
    public async Task Execute_TargetOutsideTheCandidateConditions_IsNotReOffered()
    {
        // The selection conditions (B02 decisions): score ceiling (98 >
        // 100 - 3 → a hash-perfect file has nothing to upgrade to), the
        // on-disk check (coverage no longer on disk is a fresh-search gap,
        // not an upgrade) and the window (a row older than 14 days has had
        // its chance). None of the three targets may trigger a provider
        // call.
        using var fixture = new UpgradeJobFixture(request => [Candidate()]);
        await fixture.Settings.SetSetting(SettingKeys.SubtitleSearch.UpgradeEnabled, "true");
        fixture.SeedSucceededDownload(
            score: 98, fileName: "Movie.2019.WEBDL-1080p.zh.hash.srt"); // score ceiling
        fixture.SeedSucceededDownload(
            score: 70, fileName: "Movie.2019.WEBDL-1080p.en.gone.srt", language: "en",
            content: null); // row without a file — coverage no longer on disk
        fixture.SeedSucceededDownload(
            score: 70, fileName: "Movie.2019.WEBDL-1080p.ja.old.srt", language: "ja",
            createdAt: DateTimeOffset.UtcNow.AddDays(-20)); // outside the 14-day window

        await fixture.Job.Execute();

        Assert.Equal(0, fixture.Source.SearchCalls);
        Assert.Equal(0, fixture.Source.DownloadCalls);
        // exactly the three seeded rows — no LowConfidence row either, the
        // targets were never offered
        Assert.Equal(3, await fixture.DbContext.SubtitleDownloads.AsNoTracking().CountAsync());
        fixture.ScheduleService.Verify(
            service => service.UpdateJobState(It.IsAny<string>(), "Succeeded"), Times.Once);
    }

    [Fact]
    public async Task Execute_MasterSwitchOff_ShortCircuitsBeforeAnyReadOrProviderAction()
    {
        // The M0014 seeded default: one log line, no selection search, no
        // provider call, no history row — and the round still reports
        // Succeeded (the rollback lever is not an error).
        using var fixture = new UpgradeJobFixture(request => [Candidate()]);
        // UpgradeEnabled left unset — the seeded default reads as false.
        var oldPath = fixture.SeedSucceededDownload(
            score: 70, fileName: "Movie.2019.WEBDL-1080p.zh.v1.srt", content: "OLD-BYTES");

        await fixture.Job.Execute();

        Assert.Equal(0, fixture.Source.SearchCalls);
        Assert.Equal(0, fixture.Source.DownloadCalls);
        Assert.Equal("OLD-BYTES", File.ReadAllText(oldPath));
        Assert.Single(await fixture.DbContext.SubtitleDownloads.AsNoTracking().ToListAsync());
        fixture.ScheduleService.Verify(
            service => service.UpdateJobState(It.IsAny<string>(), "Succeeded"), Times.Once);
    }

    [Fact]
    public async Task Execute_JobTokenCancelled_NoHistoryRowNoFailureMarkAndRethrows()
    {
        // Cancellation contract (SubtitleSearchJob precedent): the token
        // penetrates the in-flight target through the orchestrator's linked
        // budget; the round rethrows OperationCanceledException after the
        // target unwinds — no history row for it (nothing was refused or
        // skipped), no Succeeded state on the round.
        using var fixture = new UpgradeJobFixture(request => [Candidate()]);
        await fixture.Settings.SetSetting(SettingKeys.SubtitleSearch.UpgradeEnabled, "true");
        fixture.SeedSucceededDownload(score: 70, fileName: "Movie.2019.WEBDL-1080p.zh.v1.srt");

        using var cts = new CancellationTokenSource();
        var inFlight = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Source.OnSearching = async token =>
        {
            inFlight.TrySetResult();
            // cancelled exactly by the job token (linked into this one)
            await Task.Delay(Timeout.Infinite, token);
        };

        var round = fixture.Job.Execute(cts.Token);
        try
        {
            await inFlight.Task.WaitAsync(TimeSpan.FromSeconds(30));
        }
        catch (TimeoutException)
        {
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => round);
            Assert.Fail("The upgrade target never became in flight within 30s.");
        }

        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => round);

        // exactly the seeded row — the cancelled target wrote nothing
        Assert.Single(await fixture.DbContext.SubtitleDownloads.AsNoTracking().ToListAsync());
        Assert.Equal(1, fixture.Source.SearchCalls);
        Assert.Equal(0, fixture.Source.DownloadCalls);
        fixture.ScheduleService.Verify(
            service => service.UpdateJobState(It.IsAny<string>(), "Succeeded"), Times.Never);
    }

    // ── fixture ─────────────────────────────────────────────────────

    /// <summary>
    /// The shared <see cref="SubtitleEndpointFixture"/> (real SQLite, real
    /// request builder/orchestrator/pipeline, mocked naming service) plus
    /// the job under test and its schedule double. The serial round needs
    /// no scope factory — the job's own collaborators serve it.
    /// </summary>
    private sealed class UpgradeJobFixture : IDisposable
    {
        internal readonly SubtitleEndpointFixture Endpoint;
        internal readonly Mock<IScheduleService> ScheduleService = new();
        internal readonly ScriptedUpgradeSource Source;
        internal readonly SubtitleUpgradeJob Job;

        internal UpgradeJobFixture(Func<SubtitleSearchRequest, SubtitleCandidate[]> candidates)
        {
            Source = new ScriptedUpgradeSource(candidates, Encoding.UTF8.GetBytes(SrtText));
            Endpoint = new SubtitleEndpointFixture(Source);
            ScheduleService
                .Setup(service => service.UpdateJobState(It.IsAny<string>(), It.IsAny<string>()))
                .Returns(Task.CompletedTask);
            Job = new SubtitleUpgradeJob(
                Endpoint.Settings,
                ScheduleService.Object,
                Endpoint.DbContext,
                Endpoint.RequestBuilder,
                Endpoint.Orchestrator,
                Endpoint.Pipeline,
                new SubtitleMatchScorer(),
                NullLogger<SubtitleUpgradeJob>.Instance);
        }

        internal string MediaDirectory => Endpoint.MediaDirectory;

        internal int MovieId => Endpoint.MovieId;

        internal InMemorySettingService Settings => Endpoint.Settings;

        internal LingarrDbContext DbContext => Endpoint.DbContext;

        /// <summary>
        /// Seeds the old, still-on-disk succeeded download the round is to
        /// re-offer: a real file in the fixture's media directory plus its
        /// history row (the production write side's exact shape — FilePath
        /// is the naming-template landing path of the previous download).
        /// </summary>
        internal string SeedSucceededDownload(
            int score,
            string fileName,
            string language = "zh",
            string? content = "OLD-BYTES",
            DateTimeOffset? createdAt = null)
        {
            var path = Path.Combine(Endpoint.MediaDirectory, fileName);
            if (content is not null)
            {
                File.WriteAllText(path, content);
            }

            Endpoint.DbContext.SubtitleDownloads.Add(new Lingarr.Core.Entities.SubtitleDownload
            {
                MediaType = MediaType.Movie,
                MediaId = Endpoint.MovieId,
                Provider = "opensubtitles",
                ExternalId = "ext-old",
                Language = language,
                FilePath = path,
                MatchScore = score,
                HashMatch = false,
                Source = SubtitleDownloadSource.Auto,
                Result = SubtitleDownloadResult.Succeeded,
                CreatedAt = createdAt ?? DateTimeOffset.UtcNow.AddHours(-1)
            });
            Endpoint.DbContext.SaveChanges();
            return path;
        }

        /// <summary>The single row the round wrote for the given trigger.</summary>
        internal async Task<Lingarr.Core.Entities.SubtitleDownload> HistoryRowAsync(
            SubtitleDownloadSource source)
            => await Endpoint.DbContext.SubtitleDownloads.AsNoTracking()
                .SingleAsync(row => row.Source == source);

        public void Dispose() => Endpoint.Dispose();
    }

    /// <summary>Provider double: scripted candidates + a plain SRT payload.</summary>
    private sealed class ScriptedUpgradeSource : ISubtitleProvider
    {
        private readonly Func<SubtitleSearchRequest, SubtitleCandidate[]> _candidates;
        private readonly byte[] _payload;
        private int _searchCalls;
        private int _downloadCalls;

        public string Provider => "scripted";

        /// <summary>Optional gate awaited inside SearchAsync (cancellation test).</summary>
        public Func<CancellationToken, Task>? OnSearching { get; set; }

        public int SearchCalls => Volatile.Read(ref _searchCalls);
        public int DownloadCalls => Volatile.Read(ref _downloadCalls);

        public ScriptedUpgradeSource(
            Func<SubtitleSearchRequest, SubtitleCandidate[]> candidates, byte[] payload)
        {
            _candidates = candidates;
            _payload = payload;
        }

        public async Task<SubtitleSearchResult> SearchAsync(
            SubtitleSearchRequest request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _searchCalls);
            if (OnSearching is { } hook)
            {
                await hook(cancellationToken);
            }

            return new SubtitleSearchResult(_candidates(request));
        }

        public Task<Lingarr.Server.Modules.SubtitleSearch.Abstractions.SubtitleDownload> DownloadAsync(
            SubtitleCandidateRef candidateRef, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _downloadCalls);
            return Task.FromResult(
                new Lingarr.Server.Modules.SubtitleSearch.Abstractions.SubtitleDownload(
                    Provider, "Movie.2019.WEBDL-1080p.srt", "srt", _payload));
        }
    }
}
