using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Linq.Expressions;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Lingarr.Core.Configuration;
using Lingarr.Core.Data;
using Lingarr.Core.Entities;
using Lingarr.Core.Enum;
using Lingarr.Server.Controllers;
using Lingarr.Server.Hubs;
using Lingarr.Server.Models;
using Lingarr.Server.Models.FileSystem;
using Lingarr.Server.Modules.Integration;
using Lingarr.Server.Modules.Infrastructure.Notifications;
using Lingarr.Server.Modules.Subtitle;
using Lingarr.Server.Modules.Subtitle.PostProcessing;
using Lingarr.Server.Modules.SubtitleSearch;
using Lingarr.Server.Modules.SubtitleSearch.Abstractions;
using Lingarr.Server.Modules.SubtitleSearch.Orchestration;
using Lingarr.Server.Modules.SubtitleSearch.Pipeline;
using Lingarr.Server.Modules.SubtitleSearch.Providers;
using Lingarr.Server.Modules.SubtitleSearch.Queue;
using Lingarr.Server.Modules.SubtitleSearch.Requests;
using Lingarr.Server.Modules.SubtitleSearch.Scoring;
using Lingarr.Server.Modules.SubtitleSearch.Telemetry;
using Lingarr.Server.Modules.Translation;
using Lingarr.Server.Modules.Settings;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Lingarr.Server.Tests.Modules.SubtitleSearch;

/// <summary>
/// Download-path contracts of the QA fixes: quota telemetry must never
/// discard a fetched payload (QA2 P2-3), one translation request per target
/// language (QA3 P3-1), the skipTranslation contract, the archive entry
/// budgets and the SubtitleHttp payload cap (QA2 P2-5), and the module's
/// dedicated 120s download clients (QA2 P2-4). Real SQLite because the
/// pipeline writes history/telemetry through EF.
/// </summary>
[Collection("SubtitleHttpThrottle")]
public class SubtitleDownloadPipelineTests
{
    private const string SrtText = "1\r\n00:00:01,000 --> 00:00:02,000\r\nHello world\r\n";

    private static byte[] SrtPayload => Encoding.UTF8.GetBytes(SrtText);

    /// <summary>
    /// Verify expression for the Plex refresh fire-and-forget enqueue.
    /// Hangfire's <c>Enqueue&lt;T&gt;</c> is an extension method over
    /// <c>IBackgroundJobClient.Create</c> (unverifiable by Moq), so the
    /// assertion targets the underlying Create call and matches the job the
    /// extension builds from the lambda: the PlexRefreshJob type and its
    /// parameterless Execute entry point, in the fire-and-forget state.
    /// </summary>
    private static Expression<Func<IBackgroundJobClient, string>> PlexRefreshCreate() =>
        client => client.Create(
            It.Is<Job>(job => job.Type == typeof(PlexRefreshJob)
                && job.Method.Name == nameof(PlexRefreshJob.Execute)),
            It.IsAny<EnqueuedState>());

    private static SubtitleCandidate Candidate(string language = "en", string provider = "fake") => new(
        provider,
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

    /// <summary>Provider double whose download answers a plain SRT payload.</summary>
    internal sealed class DownloadingProvider : ISubtitleProvider
    {
        public string Provider => "fake";

        public Task<SubtitleSearchResult> SearchAsync(
            SubtitleSearchRequest request, CancellationToken cancellationToken)
            => Task.FromResult(SubtitleSearchResult.Empty);

        public Task<Lingarr.Server.Modules.SubtitleSearch.Abstractions.SubtitleDownload> DownloadAsync(
            SubtitleCandidateRef candidateRef, CancellationToken cancellationToken)
            => Task.FromResult(new Lingarr.Server.Modules.SubtitleSearch.Abstractions.SubtitleDownload(
                "fake", "Movie.2019.WEBDL-1080p.srt", "srt", SrtPayload));
    }

    /// <summary>Provider double whose download always throws the preset failure.</summary>
    internal sealed class FailingProvider(SubtitleProviderException exception) : ISubtitleProvider
    {
        public string Provider => "fake";

        public Task<SubtitleSearchResult> SearchAsync(
            SubtitleSearchRequest request, CancellationToken cancellationToken)
            => Task.FromResult(SubtitleSearchResult.Empty);

        public Task<Lingarr.Server.Modules.SubtitleSearch.Abstractions.SubtitleDownload> DownloadAsync(
            SubtitleCandidateRef candidateRef, CancellationToken cancellationToken)
            => Task.FromException<Lingarr.Server.Modules.SubtitleSearch.Abstractions.SubtitleDownload>(exception);
    }

    /// <summary>Provider double serving a zero-byte payload (empty content).</summary>
    internal sealed class EmptyPayloadProvider : ISubtitleProvider
    {
        public string Provider => "fake";

        public Task<SubtitleSearchResult> SearchAsync(
            SubtitleSearchRequest request, CancellationToken cancellationToken)
            => Task.FromResult(SubtitleSearchResult.Empty);

        public Task<Lingarr.Server.Modules.SubtitleSearch.Abstractions.SubtitleDownload> DownloadAsync(
            SubtitleCandidateRef candidateRef, CancellationToken cancellationToken)
            => Task.FromResult(new Lingarr.Server.Modules.SubtitleSearch.Abstractions.SubtitleDownload(
                "fake", "Movie.2019.WEBDL-1080p.srt", "srt", []));
    }

    /// <summary>Provider double serving a prebuilt archive payload (season packs).</summary>
    internal sealed class ArchiveProvider(string fileName, byte[] archive) : ISubtitleProvider
    {
        public string Provider => "fake";

        public Task<SubtitleSearchResult> SearchAsync(
            SubtitleSearchRequest request, CancellationToken cancellationToken)
            => Task.FromResult(SubtitleSearchResult.Empty);

        public Task<Lingarr.Server.Modules.SubtitleSearch.Abstractions.SubtitleDownload> DownloadAsync(
            SubtitleCandidateRef candidateRef, CancellationToken cancellationToken)
            => Task.FromResult(new Lingarr.Server.Modules.SubtitleSearch.Abstractions.SubtitleDownload(
                "fake", fileName, "zip", archive));
    }

    /// <summary>
    /// Provider double whose download answers a plain SRT payload and
    /// counts the calls — the cooldown-gate assertions need to prove ZERO
    /// upstream fetches inside the window.
    /// </summary>
    internal sealed class CountingProvider(string providerKey = "fake") : ISubtitleProvider
    {
        public string Provider => providerKey;
        public int DownloadCalls { get; private set; }

        public Task<SubtitleSearchResult> SearchAsync(
            SubtitleSearchRequest request, CancellationToken cancellationToken)
            => Task.FromResult(SubtitleSearchResult.Empty);

        public Task<Lingarr.Server.Modules.SubtitleSearch.Abstractions.SubtitleDownload> DownloadAsync(
            SubtitleCandidateRef candidateRef, CancellationToken cancellationToken)
        {
            DownloadCalls++;
            return Task.FromResult(new Lingarr.Server.Modules.SubtitleSearch.Abstractions.SubtitleDownload(
                providerKey, "Movie.2019.WEBDL-1080p.srt", "srt", SrtPayload));
        }
    }

    // ── Blacklist write side (P2 data batch): content-invalid failures
    // blacklist the (provider, external_id) pair for 90 days; transport
    // failures never do.

    [Fact]
    public async Task Download_ContentInvalidFailure_WritesAndRefreshesBlacklistRow()
    {
        // An unparseable archive is a permanent property of the candidate —
        // the same external id would serve the same bytes forever.
        using var fixture = new SubtitleEndpointFixture(new FailingProvider(
            SubtitleProviderException.ForInvalidResponse(
                "fake", "the archive could not be read (corrupt central directory).")));

        var outcome = await fixture.Pipeline.DownloadAsync(
            Candidate("en"), MediaType.Movie, fixture.MovieId, SubtitleDownloadSource.Auto,
            searchSessionId: null, CancellationToken.None);
        Assert.Equal(SubtitleDownloadResult.Failed, outcome.Result);

        var row = await fixture.DbContext.SubtitleSearchBlacklists.SingleAsync();
        Assert.Equal("fake", row.Provider);
        Assert.Equal("ext-1", row.ExternalId);
        Assert.Contains("unexpected response", row.Reason);
        // Locked read/write contract: 90-day window from the failure.
        Assert.InRange(row.ExpiresAt!.Value,
            DateTimeOffset.UtcNow.AddDays(89), DateTimeOffset.UtcNow.AddDays(91));

        // A repeat failure refreshes the same row instead of stacking rows.
        var before = row.ExpiresAt!.Value;
        await fixture.Pipeline.DownloadAsync(
            Candidate("en"), MediaType.Movie, fixture.MovieId, SubtitleDownloadSource.Auto,
            searchSessionId: null, CancellationToken.None);

        var refreshed = await fixture.DbContext.SubtitleSearchBlacklists.SingleAsync();
        Assert.True(refreshed.ExpiresAt!.Value >= before);
        Assert.Equal(2, await fixture.DbContext.SubtitleDownloads.CountAsync());
    }

    [Fact]
    public async Task Download_TimeoutFailure_DoesNotBlacklist()
    {
        using var fixture = new SubtitleEndpointFixture(new FailingProvider(
            SubtitleProviderException.ForTimeout("fake")));

        var outcome = await fixture.Pipeline.DownloadAsync(
            Candidate("en"), MediaType.Movie, fixture.MovieId, SubtitleDownloadSource.Auto,
            searchSessionId: null, CancellationToken.None);

        Assert.Equal(SubtitleDownloadResult.Failed, outcome.Result);
        Assert.Equal(0, await fixture.DbContext.SubtitleSearchBlacklists.CountAsync());
        // The attempt still lands its history row.
        Assert.Equal(1, await fixture.DbContext.SubtitleDownloads.CountAsync());
    }

    [Fact]
    public async Task Download_RateLimitFailure_DoesNotBlacklist()
    {
        using var fixture = new SubtitleEndpointFixture(new FailingProvider(
            SubtitleProviderException.ForHttpStatus("fake", System.Net.HttpStatusCode.TooManyRequests)));

        var outcome = await fixture.Pipeline.DownloadAsync(
            Candidate("en"), MediaType.Movie, fixture.MovieId, SubtitleDownloadSource.Auto,
            searchSessionId: null, CancellationToken.None);

        Assert.Equal(SubtitleDownloadResult.Failed, outcome.Result);
        Assert.Equal(0, await fixture.DbContext.SubtitleSearchBlacklists.CountAsync());
    }

    // ── R3 quota circuit-breaker (the 406 mirror of the failure cases
    // above): a daily-quota-exhaustion verdict cools the source down until
    // the source's own reset moment (or the UTC-midnight fallback), never
    // blacklists the candidate, and still lands the Failed history row.

    [Fact]
    public async Task Download_QuotaExhausted406_CoolsTheSourceDownUntilTheReportedReset()
    {
        var resetUtc = DateTimeOffset.UtcNow.AddHours(6);
        using var fixture = new SubtitleEndpointFixture(new FailingProvider(
            SubtitleProviderException.ForDailyLimit(
                "fake", resetEpochSeconds: null, httpStatus: 406, resetUtc: resetUtc)));

        var outcome = await fixture.Pipeline.DownloadAsync(
            Candidate("en"), MediaType.Movie, fixture.MovieId, SubtitleDownloadSource.Auto,
            searchSessionId: null, CancellationToken.None);

        Assert.Equal(SubtitleDownloadResult.Failed, outcome.Result);
        Assert.Contains("daily quota", outcome.Error);

        var cooldowns = await fixture.Cooldowns.GetActiveAsync();
        var cooldown = cooldowns["fake"];
        // The source's own reset moment, under the quota_exhausted bucket.
        Assert.Equal(resetUtc, cooldown.Until);
        Assert.Equal(SubtitleSourceCooldownStore.QuotaExhaustedReason, cooldown.Reason);

        // A quota verdict is a source state, never a candidate defect.
        Assert.Equal(0, await fixture.DbContext.SubtitleSearchBlacklists.CountAsync());
        Assert.Equal(1, await fixture.DbContext.SubtitleDownloads.CountAsync());
    }

    [Fact]
    public async Task Download_QuotaExhausted406WithoutReset_CoolsDownToTheNextUtcMidnight()
    {
        using var fixture = new SubtitleEndpointFixture(new FailingProvider(
            SubtitleProviderException.ForDailyLimit("fake", httpStatus: 406)));

        var outcome = await fixture.Pipeline.DownloadAsync(
            Candidate("en"), MediaType.Movie, fixture.MovieId, SubtitleDownloadSource.Auto,
            searchSessionId: null, CancellationToken.None);

        Assert.Equal(SubtitleDownloadResult.Failed, outcome.Result);

        var cooldowns = await fixture.Cooldowns.GetActiveAsync();
        var cooldown = cooldowns["fake"];
        Assert.Equal(SubtitleSourceCooldownStore.QuotaExhaustedReason, cooldown.Reason);
        // Midnight fallback: exactly the next UTC day boundary.
        Assert.Equal(TimeSpan.Zero, cooldown.Until.TimeOfDay);
        Assert.Equal(cooldown.Until, new DateTimeOffset(cooldown.Until.UtcDateTime.Date, TimeSpan.Zero));
    }

    // ── assrt 493 download-cooldown closure (dc2d6a4 follow-up): the
    // RateLimited verdict of a persistent 493/509 throttle now persists its
    // cooldown on the download side (only DailyLimit did before), and the
    // download entry reads the map — inside the window a re-click makes
    // zero upstream requests and answers CooldownSkipped immediately; once
    // the window passes, the ordinary download path resumes.

    [Fact]
    public async Task Download_PersistentThrottle493_PersistsTheRateLimitCooldown()
    {
        // ForHttpStatus maps the non-standard 493 onto RateLimited with the
        // one-minute span — the same verdict AssrtProvider surfaces after
        // its patient retry (AssrtProviderTests pin the provider contract;
        // this pins the pipeline wiring that used to drop the span).
        using var fixture = new SubtitleEndpointFixture(new FailingProvider(
            SubtitleProviderException.ForHttpStatus("fake", (HttpStatusCode)493)));

        var outcome = await fixture.Pipeline.DownloadAsync(
            Candidate("en"), MediaType.Movie, fixture.MovieId, SubtitleDownloadSource.Manual,
            searchSessionId: null, CancellationToken.None);

        Assert.Equal(SubtitleDownloadResult.Failed, outcome.Result);
        Assert.Contains("throttled the download", outcome.Error);

        var cooldowns = await fixture.Cooldowns.GetActiveAsync();
        var cooldown = cooldowns["fake"];
        Assert.Equal(nameof(SubtitleProviderFailureReason.RateLimited), cooldown.Reason);
        // One-minute window from the verdict (493/509 span).
        Assert.InRange(cooldown.Until,
            DateTimeOffset.UtcNow.AddSeconds(55), DateTimeOffset.UtcNow.AddSeconds(65));

        // A throttle is a source state, never a candidate defect — and the
        // attempt keeps its Failed history row.
        Assert.Equal(0, await fixture.DbContext.SubtitleSearchBlacklists.CountAsync());
        Assert.Equal(1, await fixture.DbContext.SubtitleDownloads.CountAsync());
    }

    [Fact]
    public async Task Download_WithinRateLimitCooldown_SkipsTheUpstreamFetchEntirely()
    {
        // The user-visible bug: every re-click on a cached candidate paid
        // two throttled HTTP rounds + the 8s patient retry before the
        // failure toast. Inside the window the gate answers CooldownSkipped
        // with zero provider calls and without waiting.
        var fake = new CountingProvider();
        var other = new CountingProvider("other");
        using var fixture = new SubtitleEndpointFixture(fake, other);
        await fixture.Cooldowns.RecordFailureAsync(
            "fake", SubtitleProviderException.ForHttpStatus("fake", (HttpStatusCode)493));

        var stopwatch = Stopwatch.StartNew();
        var outcome = await fixture.Pipeline.DownloadAsync(
            Candidate("en"), MediaType.Movie, fixture.MovieId, SubtitleDownloadSource.Manual,
            searchSessionId: null, CancellationToken.None);
        stopwatch.Stop();

        Assert.Equal(SubtitleDownloadResult.CooldownSkipped, outcome.Result);
        Assert.Null(outcome.FilePath);
        Assert.Equal(0, fake.DownloadCalls); // zero upstream requests
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1),
            $"the cooldown skip must be immediate, took {stopwatch.Elapsed}");
        Assert.NotNull(outcome.CooldownUntilUtc);

        // The refused click still lands its history row, distinct from Failed.
        var row = await fixture.DbContext.SubtitleDownloads.SingleAsync();
        Assert.Equal(SubtitleDownloadResult.CooldownSkipped, row.Result);

        // One source's cooldown never touches another source's downloads.
        var unaffected = await fixture.Pipeline.DownloadAsync(
            Candidate("en", provider: "other"), MediaType.Movie, fixture.MovieId,
            SubtitleDownloadSource.Manual, searchSessionId: null, CancellationToken.None);
        Assert.Equal(SubtitleDownloadResult.Succeeded, unaffected.Result);
        Assert.Equal(1, other.DownloadCalls);
    }

    [Fact]
    public async Task Download_AfterTheRateLimitCooldownExpires_ResumesTheNormalPath()
    {
        // An expired entry never survives GetActiveAsync — the download
        // runs the ordinary fetch path again.
        var provider = new CountingProvider();
        using var fixture = new SubtitleEndpointFixture(provider);
        fixture.Settings.SetSetting(
            Lingarr.Core.Configuration.SettingKeys.SubtitleSearch.SourceCooldowns,
            System.Text.Json.JsonSerializer.Serialize(
                new Dictionary<string, SubtitleSourceCooldown>
                {
                    ["fake"] = new SubtitleSourceCooldown(
                        DateTimeOffset.UtcNow.AddMinutes(-1),
                        nameof(SubtitleProviderFailureReason.RateLimited),
                        "fake throttled the download (HTTP 493).")
                })).GetAwaiter().GetResult();

        var outcome = await fixture.Pipeline.DownloadAsync(
            Candidate("en"), MediaType.Movie, fixture.MovieId, SubtitleDownloadSource.Manual,
            searchSessionId: null, CancellationToken.None);

        Assert.Equal(SubtitleDownloadResult.Succeeded, outcome.Result);
        Assert.Equal(1, provider.DownloadCalls);
        Assert.Null(outcome.CooldownUntilUtc);
    }

    [Fact]
    public async Task Download_EmptyPayload_FailsAndBlacklistsInsteadOfWritingCoverage()
    {
        // A zero-byte entry used to parse into an empty SRT that was written
        // to disk and counted as coverage; it is a content defect of the
        // candidate and blacklists like any other unusable payload.
        using var fixture = new SubtitleEndpointFixture(new EmptyPayloadProvider());

        var outcome = await fixture.Pipeline.DownloadAsync(
            Candidate("en"), MediaType.Movie, fixture.MovieId, SubtitleDownloadSource.Manual,
            searchSessionId: null, CancellationToken.None);

        Assert.Equal(SubtitleDownloadResult.Failed, outcome.Result);
        Assert.Null(outcome.FilePath);
        Assert.Equal(
            "the downloaded subtitle is empty.",
            (await fixture.DbContext.SubtitleDownloads.SingleAsync()).Error);
        var row = await fixture.DbContext.SubtitleSearchBlacklists.SingleAsync();
        Assert.Equal(("fake", "ext-1"), (row.Provider, row.ExternalId));
        Assert.InRange(row.ExpiresAt!.Value,
            DateTimeOffset.UtcNow.AddDays(89), DateTimeOffset.UtcNow.AddDays(91));
    }

    [Fact]
    public async Task Download_EnqueuesOneRequestPerTargetLanguage()
    {
        // QA3 P3-1: only the first target used to be enqueued — every other
        // configured target silently got no translation.
        using var fixture = new SubtitleEndpointFixture(new DownloadingProvider());

        var outcome = await fixture.Pipeline.DownloadAsync(
            Candidate("en"), MediaType.Movie, fixture.MovieId, SubtitleDownloadSource.Manual,
            searchSessionId: null, CancellationToken.None);

        Assert.Equal(SubtitleDownloadResult.Succeeded, outcome.Result);
        Assert.True(File.Exists(outcome.FilePath), $"the subtitle must land on disk ({outcome.FilePath})");
        Assert.Equal(41, outcome.TranslationRequestId);
        Assert.False(outcome.TranslationDuplicate);

        fixture.TranslationQueue.Verify(
            queue => queue.CreateImportRequest(
                It.IsAny<string>(), It.IsAny<string>(), "en", "zh",
                It.IsAny<int?>(), MediaType.Movie, It.IsAny<string?>(), It.IsAny<bool>()),
            Times.Once);
        fixture.TranslationQueue.Verify(
            queue => queue.CreateImportRequest(
                It.IsAny<string>(), It.IsAny<string>(), "en", "ja",
                It.IsAny<int?>(), MediaType.Movie, It.IsAny<string?>(), It.IsAny<bool>()),
            Times.Once);

        var history = await fixture.DbContext.SubtitleDownloads.SingleAsync();
        Assert.Equal(SubtitleDownloadResult.Succeeded, history.Result);
    }

    [Fact]
    public async Task Download_SkipTranslation_LandsTheFileWithoutTouchingTheQueue()
    {
        // Locked API contract: true = the file still lands, the translation
        // queue is never called (not even for the non-target language).
        using var fixture = new SubtitleEndpointFixture(new DownloadingProvider());

        var outcome = await fixture.Pipeline.DownloadAsync(
            Candidate("en"), MediaType.Movie, fixture.MovieId, SubtitleDownloadSource.Manual,
            searchSessionId: null, CancellationToken.None, skipTranslation: true);

        Assert.Equal(SubtitleDownloadResult.Succeeded, outcome.Result);
        Assert.True(File.Exists(outcome.FilePath));
        Assert.Null(outcome.TranslationRequestId);
        Assert.False(outcome.TranslationDuplicate);
        fixture.TranslationQueue.Verify(
            queue => queue.CreateImportRequest(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<int?>(), It.IsAny<MediaType>(), It.IsAny<string?>(), It.IsAny<bool>()),
            Times.Never);
    }

    [Fact]
    public async Task Download_TargetLanguageHit_StaysTerminal()
    {
        using var fixture = new SubtitleEndpointFixture(new DownloadingProvider());

        var outcome = await fixture.Pipeline.DownloadAsync(
            Candidate("zh"), MediaType.Movie, fixture.MovieId, SubtitleDownloadSource.Manual,
            searchSessionId: null, CancellationToken.None);

        Assert.Equal(SubtitleDownloadResult.Succeeded, outcome.Result);
        Assert.True(File.Exists(outcome.FilePath));
        Assert.Null(outcome.TranslationRequestId); // zh direct hit: no translation
        fixture.TranslationQueue.Verify(
            queue => queue.CreateImportRequest(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<int?>(), It.IsAny<MediaType>(), It.IsAny<string?>(), It.IsAny<bool>()),
            Times.Never);
    }

    [Fact]
    public async Task Download_PlexRefreshEnabled_EnqueuesOneLibraryRefresh()
    {
        // B03 acceptance 1 (download path): a landed subtitle on the
        // single-entry success path fires exactly one Plex refresh job when
        // the switch is on — one enqueue per landing, the job's own guard
        // merges any burst.
        using var fixture = new SubtitleEndpointFixture(new DownloadingProvider());
        fixture.Settings.SetSetting(
            Lingarr.Core.Configuration.SettingKeys.Integration.PlexRefreshEnabled, "true");

        var outcome = await fixture.Pipeline.DownloadAsync(
            Candidate("zh"), MediaType.Movie, fixture.MovieId, SubtitleDownloadSource.Manual,
            searchSessionId: null, CancellationToken.None);

        Assert.Equal(SubtitleDownloadResult.Succeeded, outcome.Result);
        Assert.True(File.Exists(outcome.FilePath));
        fixture.BackgroundJobs.Verify(PlexRefreshCreate(), Times.Once);
    }

    [Fact]
    public async Task Download_PlexRefreshUnset_EnqueuesNothing()
    {
        // B03 acceptance 3: unset (the seeded default) means zero enqueues —
        // the feature must stay inert until it is configured.
        using var fixture = new SubtitleEndpointFixture(new DownloadingProvider());

        var outcome = await fixture.Pipeline.DownloadAsync(
            Candidate("zh"), MediaType.Movie, fixture.MovieId, SubtitleDownloadSource.Manual,
            searchSessionId: null, CancellationToken.None);

        Assert.Equal(SubtitleDownloadResult.Succeeded, outcome.Result);
        fixture.BackgroundJobs.Verify(PlexRefreshCreate(), Times.Never);
    }

    [Fact]
    public async Task Download_SameTargetTwice_OverwritesInsteadOfStackingCopies()
    {
        // User ruling 2026-10-19: re-downloading the same episode+language
        // replaces the file — the old "(1)" suffixing stacked duplicates and
        // left players on the stale first copy.
        using var fixture = new SubtitleEndpointFixture(new DownloadingProvider());

        var first = await fixture.Pipeline.DownloadAsync(
            Candidate("zh"), MediaType.Movie, fixture.MovieId, SubtitleDownloadSource.Manual,
            searchSessionId: null, CancellationToken.None);
        var second = await fixture.Pipeline.DownloadAsync(
            Candidate("zh"), MediaType.Movie, fixture.MovieId, SubtitleDownloadSource.Manual,
            searchSessionId: null, CancellationToken.None);

        Assert.Equal(SubtitleDownloadResult.Succeeded, second.Result);
        Assert.Equal(first.FilePath, second.FilePath); // same landing path
        var directory = Path.GetDirectoryName(second.FilePath);
        // exactly one subtitle file: no "(1)" sibling, no temp leftovers
        Assert.Equal(
            Path.GetFileName(second.FilePath),
            Directory.GetFiles(directory!, "*.srt").Select(Path.GetFileName).Single());
    }

    // ── B07 caption suffix: a forced / hearing-impaired candidate carries
    // its dimension into the file name through the REAL naming chain (the
    // fixture's default pipeline mocks CreateFilePath to a passthrough, so
    // these run a pipeline variant wired to the production SubtitleService).

    [Fact]
    public async Task Download_ForcedCandidate_LandsWithTheForcedCaptionSuffix()
    {
        using var fixture = new SubtitleEndpointFixture(new DownloadingProvider());
        var pipeline = fixture.PipelineWithRealNaming();

        var outcome = await pipeline.DownloadAsync(
            Candidate("zh") with { Forced = true }, MediaType.Movie, fixture.MovieId,
            SubtitleDownloadSource.Manual, searchSessionId: null, CancellationToken.None);

        Assert.Equal(SubtitleDownloadResult.Succeeded, outcome.Result);
        // CreateFilePath re-appends the caption after the language — the
        // exact vocabulary the scanner and the normalizer already parse.
        Assert.EndsWith("Movie.2019.WEBDL-1080p.zh.forced.srt", outcome.FilePath);
        Assert.True(File.Exists(outcome.FilePath));
    }

    [Fact]
    public async Task Download_HearingImpairedCandidate_LandsWithTheHiCaptionSuffix()
    {
        using var fixture = new SubtitleEndpointFixture(new DownloadingProvider());
        var pipeline = fixture.PipelineWithRealNaming();

        var outcome = await pipeline.DownloadAsync(
            Candidate("zh") with { HearingImpaired = true }, MediaType.Movie, fixture.MovieId,
            SubtitleDownloadSource.Manual, searchSessionId: null, CancellationToken.None);

        Assert.Equal(SubtitleDownloadResult.Succeeded, outcome.Result);
        Assert.EndsWith("Movie.2019.WEBDL-1080p.zh.hi.srt", outcome.FilePath);
    }

    [Fact]
    public async Task Download_ForcedAndHearingImpairedCandidate_ForcedWinsTheSuffix()
    {
        // A name can carry only ONE caption segment (the parsers keep the
        // last one and the normalizer would propose a rename) — forced wins
        // because it is the dimension that changes coverage semantics.
        using var fixture = new SubtitleEndpointFixture(new DownloadingProvider());
        var pipeline = fixture.PipelineWithRealNaming();

        var outcome = await pipeline.DownloadAsync(
            Candidate("zh") with { Forced = true, HearingImpaired = true },
            MediaType.Movie, fixture.MovieId,
            SubtitleDownloadSource.Manual, searchSessionId: null, CancellationToken.None);

        Assert.Equal(SubtitleDownloadResult.Succeeded, outcome.Result);
        Assert.EndsWith("Movie.2019.WEBDL-1080p.zh.forced.srt", outcome.FilePath);
        Assert.DoesNotContain(".zh.hi", outcome.FilePath);
    }

    [Fact]
    public async Task Download_SeasonPackForcedCandidate_LandsEveryEntryWithTheSuffix()
    {
        using var fixture = new SubtitleEndpointFixture(new ArchiveProvider(
            "6ixtynin9.The.Series.S01.THAI.720p.NF.WEBRip.x264-GalaxyTV.zip",
            ZipOf(
                (PackEntry(1, 1), SrtOf("episode one")),
                (PackEntry(1, 2), SrtOf("episode two")))));
        var episode1 = fixture.SeedEpisode(1, 1);
        var episode2 = fixture.SeedEpisode(1, 2);
        var pipeline = fixture.PipelineWithRealNaming();

        var outcome = await pipeline.DownloadAsync(
            Candidate("zh") with { Forced = true }, MediaType.Episode, episode2.Id,
            SubtitleDownloadSource.Manual, searchSessionId: null, CancellationToken.None);

        Assert.Equal(SubtitleDownloadResult.Succeeded, outcome.Result);
        Assert.Equal(2, outcome.FilesLanded);
        Assert.EndsWith(".zh.forced.srt", Path.GetFileName(outcome.FilePath));
        Assert.True(File.Exists(Path.Combine(
            episode1.Directory, Path.GetFileNameWithoutExtension(episode1.VideoFileName) + ".zh.forced.srt")));
        Assert.True(File.Exists(Path.Combine(
            episode2.Directory, Path.GetFileNameWithoutExtension(episode2.VideoFileName) + ".zh.forced.srt")));
    }

    // ── B07 auto-round dedupe: forced-only files are their own dimension —
    // they answer a forced candidate but never block the full search. ────

    [Fact]
    public async Task Download_AutoRound_ForcedOnlyFileDoesNotBlockTheFullCandidate()
    {
        using var fixture = new SubtitleEndpointFixture(new DownloadingProvider());
        var forcedPath = Path.Combine(fixture.MediaDirectory, "Movie.2019.WEBDL-1080p.zh.forced.srt");
        fixture.SubtitleService
            .Setup(service => service.GetSubtitles(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(new List<Subtitles>
            {
                new()
                {
                    Path = forcedPath,
                    FileName = "Movie.2019.WEBDL-1080p.zh.forced",
                    Language = "zh",
                    Languages = ["zh"],
                    Caption = "forced"
                }
            });

        var outcome = await fixture.Pipeline.DownloadAsync(
            Candidate("zh"), MediaType.Movie, fixture.MovieId, SubtitleDownloadSource.Auto,
            searchSessionId: null, CancellationToken.None);

        // The forced-only file does not satisfy the full need: the full
        // candidate still downloads (and lands beside it).
        Assert.Equal(SubtitleDownloadResult.Succeeded, outcome.Result);
        Assert.True(File.Exists(outcome.FilePath));
    }

    [Fact]
    public async Task Download_AutoRound_ForcedCandidateSkipsOnAnExistingForcedFile()
    {
        using var fixture = new SubtitleEndpointFixture(new DownloadingProvider());
        var forcedPath = Path.Combine(fixture.MediaDirectory, "Movie.2019.WEBDL-1080p.zh.forced.srt");
        fixture.SubtitleService
            .Setup(service => service.GetSubtitles(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(new List<Subtitles>
            {
                new()
                {
                    Path = forcedPath,
                    FileName = "Movie.2019.WEBDL-1080p.zh.forced",
                    Language = "zh",
                    Languages = ["zh"],
                    Caption = "forced"
                }
            });

        var outcome = await fixture.Pipeline.DownloadAsync(
            Candidate("zh") with { Forced = true }, MediaType.Movie, fixture.MovieId,
            SubtitleDownloadSource.Auto, searchSessionId: null, CancellationToken.None);

        // The dimensions agree: the forced candidate dedupes against the
        // forced file (same dimension), and the history row points at it.
        Assert.Equal(SubtitleDownloadResult.AlreadyExistsSkipped, outcome.Result);
        Assert.Equal(forcedPath, outcome.FilePath);
    }

    [Fact]
    public async Task Download_Success_DropsTheDetailPageSubtitleFileCache()
    {
        // Regression: the search modal closes with a downloaded event and
        // the detail page refetches /media/{type}/{id}/subtitles right
        // away; that endpoint serves a short-TTL cache of directory scans,
        // so the landed file must drop its directory's cached scan or the
        // Files card keeps showing the pre-download list until the TTL
        // expires.
        using var fixture = new SubtitleEndpointFixture(new DownloadingProvider());

        var scans = 0;
        fixture.SubtitleService
            .Setup(service => service.GetAllSubtitles(It.IsAny<string>()))
            .ReturnsAsync(new List<Subtitles>())
            .Callback(() => scans++);

        // Only _dbContext + _subtitleService are touched on the movie path
        // of the endpoint, so the unused collaborators stay null.
        var controller = new MediaController(
            null!, null!, fixture.SubtitleService.Object, null!, fixture.DbContext, null!, null!);
        await controller.GetMediaSubtitles(MediaType.Movie, fixture.MovieId);
        Assert.Equal(1, scans); // the first view scans and primes the cache

        var outcome = await fixture.Pipeline.DownloadAsync(
            Candidate("zh"), MediaType.Movie, fixture.MovieId, SubtitleDownloadSource.Manual,
            searchSessionId: null, CancellationToken.None);
        Assert.Equal(SubtitleDownloadResult.Succeeded, outcome.Result);
        var afterDownload = scans; // whatever the coverage refresher scanned

        await controller.GetMediaSubtitles(MediaType.Movie, fixture.MovieId);
        Assert.Equal(afterDownload + 1, scans); // entry dropped → rescanned
    }

    [Fact]
    public async Task Download_DuplicateContentPerTarget_IsSkippedAndFlagged()
    {
        using var fixture = new SubtitleEndpointFixture(new DownloadingProvider());

        // The zh pair already exists (import-service dedup rule) — zh must be
        // skipped while ja still gets its request.
        fixture.DbContext.TranslationRequests.Add(new TranslationRequest
        {
            Title = "Movie",
            SourceLanguage = "en",
            TargetLanguage = "zh",
            MediaType = MediaType.Movie,
            MediaId = 1,
            Status = TranslationStatus.Completed,
            ContentHash = ContentHashOfSrtPayload()
        });
        await fixture.DbContext.SaveChangesAsync();

        var outcome = await fixture.Pipeline.DownloadAsync(
            Candidate("en"), MediaType.Movie, fixture.MovieId, SubtitleDownloadSource.Manual,
            searchSessionId: null, CancellationToken.None);

        Assert.True(outcome.TranslationDuplicate);
        Assert.Equal(41, outcome.TranslationRequestId); // ja's request
        fixture.TranslationQueue.Verify(
            queue => queue.CreateImportRequest(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), "zh",
                It.IsAny<int?>(), It.IsAny<MediaType>(), It.IsAny<string?>(), It.IsAny<bool>()),
            Times.Never);
        fixture.TranslationQueue.Verify(
            queue => queue.CreateImportRequest(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), "ja",
                It.IsAny<int?>(), It.IsAny<MediaType>(), It.IsAny<string?>(), It.IsAny<bool>()),
            Times.Once);
    }

    [Fact]
    public async Task Download_QuotaUpsertFailure_DoesNotDiscardTheFetchedPayload()
    {
        // QA2 P2-3: the quota upsert shared the download try-block, so a SQL
        // hiccup threw away an already-downloaded payload with no history
        // row. Non-fatal now — the file lands, the history row is written.
        using var fixture = new SubtitleEndpointFixture(new DownloadingProvider());
        await fixture.DbContext.Database.ExecuteSqlRawAsync("DROP TABLE provider_quota_usage");

        var outcome = await fixture.Pipeline.DownloadAsync(
            Candidate("en"), MediaType.Movie, fixture.MovieId, SubtitleDownloadSource.Manual,
            searchSessionId: null, CancellationToken.None);

        Assert.Equal(SubtitleDownloadResult.Succeeded, outcome.Result);
        Assert.True(File.Exists(outcome.FilePath));
        var history = await fixture.DbContext.SubtitleDownloads.SingleAsync();
        Assert.Equal(SubtitleDownloadResult.Succeeded, history.Result);
    }

    // ── Season-pack fan-out: subtitle sources habitually answer an episode
    // search with a whole-season archive; the single-entry pick used to
    // land one (often wrong-numbered) file next to the initiating episode.
    // Every entry that parses to an episode number of the initiating
    // season now lands next to its own sibling episode.

    private static string SrtOf(string marker) =>
        $"1\r\n00:00:01,000 --> 00:00:02,000\r\nHello {marker}\r\n";

    private static string PackEntry(int seasonNumber, int episodeNumber) =>
        $"6ixtynin9.The.Series.S{seasonNumber:D2}E{episodeNumber:D2}.720p.NF.WEBRip.x264-GalaxyTV.srt";

    /// <summary>The output name the naming template derives from the episode's video file (CreateFilePath mocked to the passthrough).</summary>
    private static string SubtitleNameOf(SubtitleEndpointFixture.EpisodeRef episode) =>
        episode.VideoFileName[..^".mkv".Length] + ".srt";

    private static byte[] ZipOf(params (string Name, string Text)[] entries)
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, text) in entries)
            {
                WriteEntry(archive, name, text);
            }
        }

        return buffer.ToArray();
    }

    [Fact]
    public async Task Download_SeasonPack_LandsEveryEntryNextToItsOwnEpisode()
    {
        using var fixture = new SubtitleEndpointFixture(new ArchiveProvider(
            "6ixtynin9.The.Series.S01.THAI.720p.NF.WEBRip.x264-GalaxyTV.zip",
            ZipOf(
                (PackEntry(1, 1), SrtOf("episode one")),
                (PackEntry(1, 2), SrtOf("episode two")),
                (PackEntry(1, 3), SrtOf("episode three")))));
        var episode1 = fixture.SeedEpisode(1, 1);
        var episode2 = fixture.SeedEpisode(1, 2);
        var episode3 = fixture.SeedEpisode(1, 3);

        var outcome = await fixture.Pipeline.DownloadAsync(
            Candidate("en"), MediaType.Episode, episode2.Id, SubtitleDownloadSource.Manual,
            searchSessionId: null, CancellationToken.None);

        Assert.Equal(SubtitleDownloadResult.Succeeded, outcome.Result);
        Assert.Equal(3, outcome.FilesLanded);
        // the history/broadcast path is the initiating episode's own file
        Assert.Equal(Path.Combine(episode2.Directory, SubtitleNameOf(episode2)), outcome.FilePath);

        Assert.Contains("episode one", File.ReadAllText(Path.Combine(episode1.Directory, SubtitleNameOf(episode1))));
        Assert.Contains("episode two", File.ReadAllText(Path.Combine(episode2.Directory, SubtitleNameOf(episode2))));
        Assert.Contains("episode three", File.ReadAllText(Path.Combine(episode3.Directory, SubtitleNameOf(episode3))));

        // one history row for the whole pack, pointing at E02's file
        var history = await fixture.DbContext.SubtitleDownloads.SingleAsync();
        Assert.Equal(SubtitleDownloadResult.Succeeded, history.Result);
        Assert.Equal(outcome.FilePath, history.FilePath);
    }

    [Fact]
    public async Task Download_SeasonPackWithoutTheInitiatingEntry_FallsBackToTheSinglePickForIt()
    {
        // E01+E03 pack initiated from E02: both entries home in on their own
        // episodes, and the initiating episode keeps the original
        // single-pick behaviour — the largest entry, same selection rules
        // as before the fan-out existed (never empty-handed).
        var largestEntry = SrtOf("episode three")
            + "2\r\n00:00:03,000 --> 00:00:04,000\r\nThe largest payload of the pack wins the single pick\r\n";
        using var fixture = new SubtitleEndpointFixture(new ArchiveProvider(
            "pack.zip",
            ZipOf(
                (PackEntry(1, 1), SrtOf("episode one")),
                (PackEntry(1, 3), largestEntry))));
        var episode1 = fixture.SeedEpisode(1, 1);
        var episode2 = fixture.SeedEpisode(1, 2);
        var episode3 = fixture.SeedEpisode(1, 3);

        var outcome = await fixture.Pipeline.DownloadAsync(
            Candidate("zh"), MediaType.Episode, episode2.Id, SubtitleDownloadSource.Manual,
            searchSessionId: null, CancellationToken.None);

        Assert.Equal(SubtitleDownloadResult.Succeeded, outcome.Result);
        Assert.Equal(3, outcome.FilesLanded);
        Assert.Contains("episode one", File.ReadAllText(Path.Combine(episode1.Directory, SubtitleNameOf(episode1))));
        Assert.Contains("episode three", File.ReadAllText(Path.Combine(episode3.Directory, SubtitleNameOf(episode3))));
        // the fallback single pick (largest entry = E03) landed next to E02
        Assert.Contains("episode three", File.ReadAllText(Path.Combine(episode2.Directory, SubtitleNameOf(episode2))));
    }

    [Fact]
    public async Task Download_PackWithoutEpisodeNumbers_KeepsTheSingleEntryBehaviour()
    {
        // No entry parses to an episode number → the fan-out never triggers
        // and the download behaves exactly as before: one picked entry
        // (largest payload) next to the initiating episode, nothing else.
        using var fixture = new SubtitleEndpointFixture(new ArchiveProvider(
            "pack.zip",
            ZipOf(
                ("Show.pack.a.srt", SrtOf("alpha")),
                ("Show.pack.b.srt", SrtOf("beta the larger entry of this pack")))));
        var episode1 = fixture.SeedEpisode(1, 1);
        var episode2 = fixture.SeedEpisode(1, 2);

        var outcome = await fixture.Pipeline.DownloadAsync(
            Candidate("zh"), MediaType.Episode, episode2.Id, SubtitleDownloadSource.Manual,
            searchSessionId: null, CancellationToken.None);

        Assert.Equal(SubtitleDownloadResult.Succeeded, outcome.Result);
        Assert.Equal(1, outcome.FilesLanded);
        Assert.Equal(Path.Combine(episode2.Directory, SubtitleNameOf(episode2)), outcome.FilePath);
        Assert.Contains("beta the larger entry", File.ReadAllText(outcome.FilePath!));
        Assert.Empty(Directory.GetFiles(episode1.Directory));
    }

    [Fact]
    public async Task Download_SeasonPack_IgnoresCrossSeasonEntries()
    {
        using var fixture = new SubtitleEndpointFixture(new ArchiveProvider(
            "pack.zip",
            ZipOf(
                (PackEntry(1, 1), SrtOf("season one episode one")),
                (PackEntry(1, 2), SrtOf("season one episode two")),
                (PackEntry(2, 1), SrtOf("season two episode one")))));
        var seasonOneEpisode1 = fixture.SeedEpisode(1, 1);
        var seasonOneEpisode2 = fixture.SeedEpisode(1, 2);
        var seasonTwoEpisode1 = fixture.SeedEpisode(2, 1);

        var outcome = await fixture.Pipeline.DownloadAsync(
            Candidate("zh"), MediaType.Episode, seasonOneEpisode1.Id, SubtitleDownloadSource.Manual,
            searchSessionId: null, CancellationToken.None);

        Assert.Equal(SubtitleDownloadResult.Succeeded, outcome.Result);
        Assert.Equal(2, outcome.FilesLanded);
        Assert.True(File.Exists(Path.Combine(seasonOneEpisode1.Directory, SubtitleNameOf(seasonOneEpisode1))));
        Assert.True(File.Exists(Path.Combine(seasonOneEpisode2.Directory, SubtitleNameOf(seasonOneEpisode2))));
        // S02E01 is a different season's file — never homed in on this one
        Assert.Empty(Directory.GetFiles(seasonTwoEpisode1.Directory));
    }

    [Fact]
    public async Task Download_SeasonPackOfANonTargetLanguage_EnqueuesOneImportRequestPerFile()
    {
        using var fixture = new SubtitleEndpointFixture(new ArchiveProvider(
            "pack.zip",
            ZipOf(
                (PackEntry(1, 1), SrtOf("episode one")),
                (PackEntry(1, 2), SrtOf("episode two")),
                (PackEntry(1, 3), SrtOf("episode three")))));
        // One target language so the per-file count reads 1:1 — with the
        // default zh+ja pair the multi-target contract (one request per
        // target per file) would double the calls.
        fixture.Settings.SetSetting(
            Lingarr.Core.Configuration.SettingKeys.Translation.TargetLanguages,
            System.Text.Json.JsonSerializer.Serialize(new List<TargetLanguage>
            {
                new() { Name = "Chinese", Code = "zh" }
            })).GetAwaiter().GetResult();
        var episode1 = fixture.SeedEpisode(1, 1);
        var episode2 = fixture.SeedEpisode(1, 2);
        var episode3 = fixture.SeedEpisode(1, 3);

        var requestMediaIds = new List<int?>();
        fixture.TranslationQueue
            .Setup(queue => queue.CreateImportRequest(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<int?>(), It.IsAny<MediaType>(), It.IsAny<string?>(), It.IsAny<bool>()))
            .ReturnsAsync(41)
            .Callback((string _, string _, string _, string _, int? mediaId, MediaType _, string _, bool _)
                => requestMediaIds.Add(mediaId));

        var outcome = await fixture.Pipeline.DownloadAsync(
            Candidate("en"), MediaType.Episode, episode2.Id, SubtitleDownloadSource.Manual,
            searchSessionId: null, CancellationToken.None);

        Assert.Equal(SubtitleDownloadResult.Succeeded, outcome.Result);
        Assert.Equal(3, outcome.FilesLanded);
        fixture.TranslationQueue.Verify(
            queue => queue.CreateImportRequest(
                It.IsAny<string>(), It.IsAny<string>(), "en", "zh",
                It.IsAny<int?>(), MediaType.Episode, It.IsAny<string?>(), It.IsAny<bool>()),
            Times.Exactly(3));
        // every request is bound to the episode its file landed next to
        Assert.Equal(new List<int?> { episode1.Id, episode2.Id, episode3.Id }, requestMediaIds);
    }

    [Fact]
    public async Task Download_SeasonPackWithSkipTranslation_LandsTheFilesWithoutTouchingTheQueue()
    {
        // Locked manual-download contract, per file: the pack lands in full,
        // the translation queue is never called.
        using var fixture = new SubtitleEndpointFixture(new ArchiveProvider(
            "pack.zip",
            ZipOf(
                (PackEntry(1, 1), SrtOf("episode one")),
                (PackEntry(1, 2), SrtOf("episode two")),
                (PackEntry(1, 3), SrtOf("episode three")))));
        fixture.SeedEpisode(1, 1);
        var episode2 = fixture.SeedEpisode(1, 2);
        fixture.SeedEpisode(1, 3);

        var outcome = await fixture.Pipeline.DownloadAsync(
            Candidate("en"), MediaType.Episode, episode2.Id, SubtitleDownloadSource.Manual,
            searchSessionId: null, CancellationToken.None, skipTranslation: true);

        Assert.Equal(SubtitleDownloadResult.Succeeded, outcome.Result);
        Assert.Equal(3, outcome.FilesLanded);
        Assert.Null(outcome.TranslationRequestId);
        fixture.TranslationQueue.Verify(
            queue => queue.CreateImportRequest(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<int?>(), It.IsAny<MediaType>(), It.IsAny<string?>(), It.IsAny<bool>()),
            Times.Never);
    }

    [Fact]
    public async Task Download_SeasonPack_DropsTheDetailPageFileCacheOfEveryWrittenDirectory()
    {
        // The Files card serves cached directory scans; a fanned-out pack
        // writes into several episode directories and EVERY written
        // directory must drop its entry — invalidating only the first one
        // written leaves the other detail pages on the pre-download list.
        using var fixture = new SubtitleEndpointFixture(new ArchiveProvider(
            "pack.zip",
            ZipOf(
                (PackEntry(1, 3), SrtOf("episode three")), // first landed file → the first-directory-only bug shape
                (PackEntry(1, 1), SrtOf("episode one")),
                (PackEntry(1, 2), SrtOf("episode two")))));
        var episode1 = fixture.SeedEpisode(1, 1);
        var episode2 = fixture.SeedEpisode(1, 2);
        fixture.SeedEpisode(1, 3);
        // The card scans the season directory — point it at E01's directory
        // so the cache that must drop is NOT the first one written (E03's).
        fixture.DbContext.Seasons.Single().Path = episode1.Directory;
        await fixture.DbContext.SaveChangesAsync();
        var showId = fixture.DbContext.Shows.Single().Id;

        var scans = 0;
        fixture.SubtitleService
            .Setup(service => service.GetAllSubtitles(It.IsAny<string>()))
            .ReturnsAsync(new List<Subtitles>())
            .Callback(() => scans++);

        var controller = new MediaController(
            null!, null!, fixture.SubtitleService.Object, null!, fixture.DbContext, null!, null!);
        await controller.GetMediaSubtitles(MediaType.Show, showId);
        Assert.Equal(1, scans); // the first view scans and primes the cache

        var outcome = await fixture.Pipeline.DownloadAsync(
            Candidate("zh"), MediaType.Episode, episode2.Id, SubtitleDownloadSource.Manual,
            searchSessionId: null, CancellationToken.None);
        Assert.Equal(SubtitleDownloadResult.Succeeded, outcome.Result);
        Assert.Equal(3, outcome.FilesLanded);
        var afterDownload = scans; // whatever the coverage refresher scanned

        await controller.GetMediaSubtitles(MediaType.Show, showId);
        Assert.Equal(afterDownload + 1, scans); // E01's directory entry dropped → rescanned
    }

    private static string ContentHashOfSrtPayload()
    {
        // The pipeline's hash rule: SHA-256 over LF-normalized UTF-8 text.
        var normalized = SrtText.Replace("\r\n", "\n").Replace("\r", "\n");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))
            .ToLowerInvariant();
    }

    private readonly SubtitleArchiveExtractor _extractor = new();

    [Fact]
    public void Extract_RejectsArchivesWithMoreEntriesThanTheBudget()
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            for (var i = 0; i <= SubtitleArchiveExtractor.MaxArchiveEntries; i++)
            {
                var entry = archive.CreateEntry($"track.{i}.srt");
                using var writer = new StreamWriter(entry.Open());
                writer.Write(SrtText);
            }
        }

        var exception = Assert.Throws<SubtitleProviderException>(() =>
            _extractor.Extract(buffer.ToArray(), "pack.zip", "zip", requestedLanguage: "zh", provider: "test"));
        Assert.Equal(SubtitleProviderFailureReason.InvalidResponse, exception.Reason);
        Assert.Contains("entry budget", exception.Message);
    }

    [Fact]
    public void Extract_RejectsSingleEntriesPastTheDecompressionBudget()
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = archive.CreateEntry("movie.zh.srt");
            using var stream = entry.Open();
            // Zeros deflate to almost nothing stored-side; the cap fires on
            // the decompressed byte count, not the stored size.
            stream.Write(new byte[SubtitleArchiveExtractor.MaxEntryBytes + 1], 0, (int)(SubtitleArchiveExtractor.MaxEntryBytes + 1));
        }

        var exception = Assert.Throws<SubtitleProviderException>(() =>
            _extractor.Extract(buffer.ToArray(), "movie.zip", "zip", requestedLanguage: "zh", provider: "test"));
        Assert.Equal(SubtitleProviderFailureReason.InvalidResponse, exception.Reason);
        Assert.Contains("budget", exception.Message);
    }

    [Fact]
    public void Extract_StillPicksTheRequestedLanguageEntry_FromBenignArchives()
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteEntry(archive, "other.srt", SrtText);
            WriteEntry(archive, "movie.zh.srt", SrtText);
        }

        var picked = _extractor.Extract(buffer.ToArray(), "movie.zip", "zip", requestedLanguage: "zh", provider: "test");
        Assert.Equal("movie.zh.srt", picked.FileName);
        Assert.Equal(SrtPayload, picked.Content);
    }

    private static void WriteEntry(ZipArchive archive, string name, string content)
    {
        var entry = archive.CreateEntry(name);
        using var writer = new StreamWriter(entry.Open());
        writer.Write(content);
    }

    // --- SubtitleHttp payload cap (QA2 P2-5), exercised through the public
    // provider surface: assrt's download path streams via SendForBytesAsync.

    private static AssrtProvider CreateAssrtProvider(StubHttpMessageHandler handler)
    {
        // The assrt request pacing (official 20/min) waits in real time
        // between requests — lift it for these payload-cap cases so the
        // four scripted rounds don't sleep 3s apart; the interval itself
        // is pinned in AssrtProviderTests.
        SubtitleHttp.SetMinimumRequestInterval("assrt", TimeSpan.Zero);
        return new AssrtProvider(
            SubtitleSettingsDouble.Create(secrets: new Dictionary<string, string>
            {
                [SubtitleSourceSettingKeys.Assrt.Token] = "test-token"
            }).Object,
            new SubtitleCandidateCache(
                new MemoryCache(new MemoryCacheOptions()), NullLogger<SubtitleCandidateCache>.Instance),
            handler.ToClient(),
            NullLogger<AssrtProvider>.Instance);
    }

    private static SubtitleSearchRequest MovieRequest() => new(
        "Movie", 2019, Season: null, Episode: null,
        ReleaseName: null, VideoHash: null, Languages: ["zh"], ImdbId: null);

    [Fact]
    public async Task DownloadPayload_OverTheDeclaredSizeBudget_IsRejectedAsInvalidResponse()
    {
        var handler = new StubHttpMessageHandler()
            .EnqueueJson(SubtitleFixtures.Read("assrt", "search.json"))
            .EnqueueJson(SubtitleFixtures.Read("assrt", "detail.json"))
            .EnqueueJson(SubtitleFixtures.Read("assrt", "detail.json"))
            .Enqueue(_ =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent("x"u8.ToArray())
                };
                // Declared size lies past the 100MB budget — rejected before
                // any body byte is read.
                response.Content.Headers.ContentLength = 100L * 1024 * 1024 + 1;
                return response;
            });

        var provider = CreateAssrtProvider(handler);
        var search = await provider.SearchAsync(MovieRequest(), CancellationToken.None);

        var exception = await Assert.ThrowsAsync<SubtitleProviderException>(() =>
            provider.DownloadAsync(
                new SubtitleCandidateRef(search.Candidates[1].ExternalId), CancellationToken.None));
        Assert.Equal(SubtitleProviderFailureReason.InvalidResponse, exception.Reason);
        Assert.Contains("budget", exception.Message);
    }

    [Fact]
    public async Task DownloadPayload_StreamingPastTheSizeBudget_IsRejectedMidRead()
    {
        // No Content-Length at all: the cap must hold against the bytes that
        // actually arrive, not only against a truthful header.
        var handler = new StubHttpMessageHandler()
            .EnqueueJson(SubtitleFixtures.Read("assrt", "search.json"))
            .EnqueueJson(SubtitleFixtures.Read("assrt", "detail.json"))
            .EnqueueJson(SubtitleFixtures.Read("assrt", "detail.json"))
            .Enqueue(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new EndlessStream())
            });

        var provider = CreateAssrtProvider(handler);
        var search = await provider.SearchAsync(MovieRequest(), CancellationToken.None);

        var exception = await Assert.ThrowsAsync<SubtitleProviderException>(() =>
            provider.DownloadAsync(
                new SubtitleCandidateRef(search.Candidates[1].ExternalId), CancellationToken.None));
        Assert.Equal(SubtitleProviderFailureReason.InvalidResponse, exception.Reason);
        Assert.Contains("budget", exception.Message);
    }

    /// <summary>An unseekable, never-ending stream — the no-header bomb shape.</summary>
    private sealed class EndlessStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush() => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count)
        {
            Array.Clear(buffer, offset, count);
            return count;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    // --- Module wiring (QA2 P2-4): dedicated download clients with the long
    // budget, search clients keep 10s + the 2MB JSON ceiling.

    [Fact]
    public void Module_RegistersDedicatedDownloadClientsWithTheLongBudget()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSubtitleSearchModule();
        using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IHttpClientFactory>();

        var download = factory.CreateClient(SubtitleProviderBase.HttpClientName("assrt") + "-download");
        Assert.Equal(TimeSpan.FromSeconds(120), download.Timeout);

        var search = factory.CreateClient(SubtitleProviderBase.HttpClientName("assrt"));
        Assert.Equal(TimeSpan.FromSeconds(10), search.Timeout);
        Assert.Equal(2L * 1024 * 1024, search.MaxResponseContentBufferSize);
    }
}

/// <summary>
/// Full download-path fixture: real SQLite schema, a movie row in a temp
/// directory, real extractor/telemetry/queue/pin services and mocked
/// interfaces where the production work is elsewhere (encoding passthrough,
/// file-path template, translation queue). Shared with the controller
/// endpoint tests.
/// </summary>
    internal sealed class SubtitleEndpointFixture : IDisposable
    {
        internal readonly SqliteConnection Connection;
        internal readonly LingarrDbContext DbContext;
        internal readonly InMemorySettingService Settings = new();
        internal readonly MemoryCache Cache = new(new MemoryCacheOptions());
        internal readonly Mock<ISubtitleService> SubtitleService = new();
        internal readonly Mock<ITranslationRequestQueueService> TranslationQueue = new();
        internal readonly Mock<IBackgroundJobClient> BackgroundJobs = new();

        /// <summary>
        /// Post-processing stages the fixture's composite runs (B01): empty by
        /// default — the pre-B01 behaviour — and mutable per test before the
        /// pipeline runs (the composite enumerates per call).
        /// </summary>
        internal readonly List<ISubtitlePostProcessor> PostProcessStages = [];

        internal readonly string MediaDirectory;
        internal readonly SubtitleDownloadPipeline Pipeline;
        internal readonly SubtitleSearchOrchestrator Orchestrator;
        internal readonly SubtitleSearchQueueService QueueService;
        internal readonly SubtitlePinService PinService;
        internal readonly SubtitleSearchRequestBuilder RequestBuilder;
        internal readonly SubtitleProviderRegistry Registry;

        /// <summary>
        /// Shared cooldown store of the pipeline and the orchestrator (R3):
        /// quota-exhaustion verdicts recorded by the download side must be
        /// visible to the orchestrator's round-skip reads — one store, one
        /// persisted map, as in production DI (both scoped over one request).
        /// </summary>
        internal readonly SubtitleSourceCooldownStore Cooldowns;

        /// <summary>
        /// B11 notification sink: the REAL fan-out service over the stub
        /// HTTP handler, sharing the fixture's in-memory settings. Tests
        /// that never touch notifications leave notifications_enabled unset,
        /// so every NotifyAsync call early-outs; embed tests flip the switch
        /// and assert on <see cref="NotificationHandler"/>.Requests.
        /// </summary>
        internal readonly StubHttpMessageHandler NotificationHandler = new();
        internal readonly NotificationService Notifications;

    internal SubtitleEndpointFixture(params ISubtitleProvider[] providers)
    {
        Connection = new SqliteConnection("DataSource=:memory:");
        Connection.Open();
        DbContext = new EndpointDbContext(new DbContextOptionsBuilder<LingarrDbContext>()
            .UseSqlite(Connection)
            .UseSnakeCaseNamingConvention()
            .Options);
        DbContext.Database.EnsureCreated();

        MediaDirectory = Path.Combine(Path.GetTempPath(), "lingarr-subtitle-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(MediaDirectory);
        DbContext.Movies.Add(new Movie
        {
            RadarrId = 1,
            Title = "Movie",
            Year = 2019,
            FileName = "Movie.2019.WEBDL-1080p.mkv",
            Path = MediaDirectory,
            DateAdded = DateTimeOffset.UtcNow
        });
        DbContext.SaveChanges();
        MovieId = DbContext.Movies.Single().Id;

        // Translation targets: zh (the deployment target) + ja (the second
        // target that the multi-target fix must not forget).
        Settings.SetSetting(
            Lingarr.Core.Configuration.SettingKeys.Translation.TargetLanguages,
            System.Text.Json.JsonSerializer.Serialize(new List<TargetLanguage>
            {
                new() { Name = "Chinese", Code = "zh" },
                new() { Name = "Japanese", Code = "ja" }
            })).GetAwaiter().GetResult();

        SubtitleService
            .Setup(service => service.GetSubtitles(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(new List<Subtitles>());
        SubtitleService
            .Setup(service => service.GetAllSubtitles(It.IsAny<string>()))
            .ReturnsAsync(new List<Subtitles>());
        SubtitleService
            .Setup(service => service.CreateFilePath(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync((string path, string language, string tag) => path);
        TranslationQueue
            .Setup(queue => queue.CreateImportRequest(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<int?>(), It.IsAny<MediaType>(), It.IsAny<string?>(), It.IsAny<bool>()))
            .ReturnsAsync(41);

        Registry = new SubtitleProviderRegistry(
            providers, Array.Empty<ISubtitleSourceManifest>(), NullLogger<SubtitleProviderRegistry>.Instance);
        Cooldowns = new SubtitleSourceCooldownStore(
            Settings, NullLogger<SubtitleSourceCooldownStore>.Instance);
        Notifications = new NotificationService(
            Settings,
            DbContext,
            new WebhookChannel(NotificationHandler.ToClient()),
            new TelegramChannel(NotificationHandler.ToClient()),
            NullLogger<NotificationService>.Instance);
        Pipeline = new SubtitleDownloadPipeline(
            DbContext,
            Registry,
            new SubtitleArchiveExtractor(),
            new PassthroughEncodingService(),
            SubtitleService.Object,
            new CompositePostProcessor(
                PostProcessStages, NullLogger<CompositePostProcessor>.Instance),
            TranslationQueue.Object,
            Settings,
            Cooldowns,
            new SubtitleTelemetryService(DbContext),
            new SubtitleCoverageRefresher(
                DbContext, SubtitleService.Object, new TargetLanguageResolver(Settings),
                NullLogger<SubtitleCoverageRefresher>.Instance),
            HubContext(),
            NullLogger<SubtitleDownloadPipeline>.Instance,
            BackgroundJobs.Object,
            Notifications);
        Orchestrator = new SubtitleSearchOrchestrator(
            Registry,
            new SubtitleMatchScorer(),
            Cooldowns,
            new SubtitleTelemetryService(DbContext),
            Cache,
            NullLogger<SubtitleSearchOrchestrator>.Instance);
        QueueService = new SubtitleSearchQueueService(DbContext, Settings, NullLogger<SubtitleSearchQueueService>.Instance);
        PinService = new SubtitlePinService(DbContext, NullLogger<SubtitlePinService>.Instance);
        RequestBuilder = new SubtitleSearchRequestBuilder(
            DbContext, Settings, new LazyMediaHasher());
    }

        /// <summary>Id of the seeded movie row.</summary>
        internal int MovieId { get; }

        /// <summary>
        /// Pipeline variant wired to the PRODUCTION SubtitleService — the
        /// default fixture mocks CreateFilePath to a passthrough, so the B07
        /// caption-suffix tests run the real naming chain (template
        /// expansion + caption re-append) exactly as production lands it.
        /// </summary>
        internal SubtitleDownloadPipeline PipelineWithRealNaming()
        {
            var subtitleService = new SubtitleService(
                NullLogger<SubtitleService>.Instance,
                new LanguageCodeService(),
                Settings);
            return new SubtitleDownloadPipeline(
                DbContext,
                Registry,
                new SubtitleArchiveExtractor(),
                new PassthroughEncodingService(),
                subtitleService,
                new CompositePostProcessor(
                    PostProcessStages, NullLogger<CompositePostProcessor>.Instance),
                TranslationQueue.Object,
                Settings,
                Cooldowns,
                new SubtitleTelemetryService(DbContext),
                new SubtitleCoverageRefresher(
                    DbContext, subtitleService, new TargetLanguageResolver(Settings),
                    NullLogger<SubtitleCoverageRefresher>.Instance),
            HubContext(),
            NullLogger<SubtitleDownloadPipeline>.Instance,
            BackgroundJobs.Object,
            Notifications);
    }

    // ── Season-pack fan-out seeding: one lazily-created show/season, each
    // episode in its own directory under the fixture's media root — the
    // multi-directory shape the fan-out must invalidate per directory.
    // The write side the pipeline reads (Episode.Path = subtitle landing
    // directory, Episode.FileName = video file name) is seeded exactly as
    // the pipeline's own ComputeOutputPathAsync consumes it.
    private Show? _seedShow;
    private readonly Dictionary<int, Season> _seedSeasons = new();

    /// <summary>Seeded episode anchor: library id, directory, video file name.</summary>
    internal sealed record EpisodeRef(int Id, string Directory, string VideoFileName);

    internal EpisodeRef SeedEpisode(int seasonNumber, int episodeNumber)
    {
        if (_seedShow is null)
        {
            _seedShow = new Show
            {
                SonarrId = 1,
                Title = "6ixtynin9 The Series",
                Path = MediaDirectory,
                DateAdded = DateTimeOffset.UtcNow
            };
            DbContext.Shows.Add(_seedShow);
        }

        if (!_seedSeasons.TryGetValue(seasonNumber, out var season))
        {
            season = new Season
            {
                SeasonNumber = seasonNumber,
                Path = Path.Combine(MediaDirectory, $"Season {seasonNumber:D2}"),
                Show = _seedShow
            };
            DbContext.Seasons.Add(season);
            _seedSeasons[seasonNumber] = season;
        }

        var directory = Path.Combine(MediaDirectory, $"s{seasonNumber:D2}e{episodeNumber:D2}");
        Directory.CreateDirectory(directory);
        var episode = new Episode
        {
            SonarrId = seasonNumber * 1000 + episodeNumber,
            EpisodeNumber = episodeNumber,
            Title = $"Episode {episodeNumber}",
            FileName = $"Show.S{seasonNumber:D2}E{episodeNumber:D2}.720p.NF.WEBRip.x264-GalaxyTV.mkv",
            Path = directory,
            Season = season,
            DateAdded = DateTimeOffset.UtcNow
        };
        DbContext.Episodes.Add(episode);
        DbContext.SaveChanges();
        return new EpisodeRef(episode.Id, directory, episode.FileName);
    }

    public void Dispose()
    {
        DbContext.Dispose();
        Connection.Dispose();
        Cache.Dispose();
        try
        {
            Directory.Delete(MediaDirectory, recursive: true);
        }
        catch (IOException)
        {
            // Temp cleanup is best-effort.
        }

        GC.SuppressFinalize(this);
    }

    private static IHubContext<JobProgressHub> HubContext()
    {
        var proxy = new Mock<IClientProxy>();
        proxy
            .Setup(client => client.SendCoreAsync(
                It.IsAny<string>(), It.IsAny<object?[]>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var clients = new Mock<IHubClients>();
        clients.Setup(hubClients => hubClients.Group(It.IsAny<string>())).Returns(proxy.Object);
        var context = new Mock<IHubContext<JobProgressHub>>();
        context.Setup(hubContext => hubContext.Clients).Returns(clients.Object);
        return context.Object;
    }

    private sealed class EndpointDbContext(DbContextOptions options) : LingarrDbContext(options);

    /// <summary>UTF-8 in, UTF-8 out — the pipeline's encoding step is not under test here.</summary>
    private sealed class PassthroughEncodingService : ISubtitleEncodingService
    {
        public Encoding Detect(byte[] content) => Encoding.UTF8;

        public byte[] ConvertToUtf8(byte[] content) => content;
    }
}
