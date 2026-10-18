using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Lingarr.Contracts.Settings;
using Lingarr.Core.Configuration;
using Lingarr.Core.Data;
using Lingarr.Core.Entities;
using Lingarr.Core.Enum;
using Lingarr.Server.Controllers;
using Lingarr.Server.Modules.SubtitleSearch;
using Lingarr.Server.Modules.SubtitleSearch.Abstractions;
using Lingarr.Server.Modules.SubtitleSearch.Manifests;
using Lingarr.Server.Modules.SubtitleSearch.Orchestration;
using Lingarr.Server.Modules.SubtitleSearch.Scoring;
using Lingarr.Server.Tests.Modules.SubtitleSearch;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Lingarr.Server.Tests.Controllers;

/// <summary>
/// Controller contracts of the subtitle-search surface: the M1 skeleton
/// stays intact (unknown provider 404, non-probing sources 501) and the
/// M2 test endpoint runs the health probe for sources that implement
/// <see cref="ISubtitleProviderHealth"/> — provider failures land as
/// ok=false with a message, never a 500 (translate-test precedent). The
/// providers endpoint serves the real settings-card shape: manifest
/// fields passed through plus requiresAuth/credentialsConfigured, with
/// per-source degradation when the secret store cannot be read — and the
/// observation batch's cooldown health pair read through the production
/// store round-trip.
/// The M3/M4 endpoints (search/download/pin/backlog) run against the real
/// orchestrator/queue/pipeline stack on SQLite through
/// <see cref="SubtitleEndpointFixture"/> — validation codes, cache
/// ownership, and the locked Query/SkipTranslation contract. The history
/// endpoint (screen 13) paginates real subtitle_downloads rows with the
/// keyset cursor, the id tiebreak over same-timestamp bursts and the
/// media-title join (episodes through season→show).
/// </summary>
public class SubtitleSearchControllerTests
{
    [SubtitleProvider("fakesubs", requiresAuth: true)]
    private class FakeProvider : ISubtitleProvider
    {
        public virtual string Provider => "fakesubs";

        public virtual Task<SubtitleSearchResult> SearchAsync(SubtitleSearchRequest request, CancellationToken cancellationToken)
            => Task.FromResult(SubtitleSearchResult.Empty);

        public virtual Task<Lingarr.Server.Modules.SubtitleSearch.Abstractions.SubtitleDownload> DownloadAsync(SubtitleCandidateRef candidateRef, CancellationToken cancellationToken)
            => throw new NotSupportedException("Test double; M2 fills real downloads.");
    }

    /// <summary>
    /// Scriptable search/download double for the endpoint tests: remembers
    /// the request it saw (Query passthrough) and answers one candidate
    /// plus a minimal SRT download payload.
    /// </summary>
    [SubtitleProvider("scripted")]
    private sealed class ScriptedSearchProvider(string language = "zh") : FakeProvider
    {
        public SubtitleSearchRequest? SeenRequest { get; private set; }

        public override string Provider => "scripted";

        public override Task<SubtitleSearchResult> SearchAsync(
            SubtitleSearchRequest request, CancellationToken cancellationToken)
        {
            SeenRequest = request;
            return Task.FromResult(new SubtitleSearchResult(
            [
                new SubtitleCandidate(
                    "scripted",
                    ExternalId: "ext-1",
                    language,
                    MatchScore: 0,
                    Forced: false,
                    HearingImpaired: false,
                    ReleaseInfo: "Movie.2019.WEBDL-1080p",
                    Uploader: null,
                    Format: "srt",
                    Size: 1234,
                    HashMatch: false)
            ]));
        }

        public override Task<Lingarr.Server.Modules.SubtitleSearch.Abstractions.SubtitleDownload> DownloadAsync(
            SubtitleCandidateRef candidateRef, CancellationToken cancellationToken)
            => Task.FromResult(new Lingarr.Server.Modules.SubtitleSearch.Abstractions.SubtitleDownload(
                "scripted",
                "Movie.2019.WEBDL-1080p.srt",
                "srt",
                Encoding.UTF8.GetBytes("1\r\n00:00:01,000 --> 00:00:02,000\r\nHello world\r\n")));
    }

    [SubtitleProvider("probing")]
    private sealed class ProbingFakeProvider : FakeProvider, ISubtitleProviderHealth
    {
        public Task<SubtitleProviderTestOutcome> TestConnectionAsync(CancellationToken cancellationToken)
            => Task.FromResult(new SubtitleProviderTestOutcome(true, "probe ok"));
    }

    [SubtitleProvider("failing")]
    private sealed class FailingProbeFakeProvider : FakeProvider, ISubtitleProviderHealth
    {
        public Task<SubtitleProviderTestOutcome> TestConnectionAsync(CancellationToken cancellationToken)
            => throw SubtitleProviderException.ForMissingCredentials("failing", "API key");
    }

    // The seven v1 source keys as test doubles — attribute name and
    // Provider property match the production providers so the registry
    // keys and the endpoint answer line up; the real manifest classes
    // supply the settings forms.
    [SubtitleProvider("opensubtitles", requiresAuth: true)]
    private sealed class FakeOpenSubtitlesProvider : FakeProvider
    {
        public override string Provider => "opensubtitles";
    }

    [SubtitleProvider("subsource", requiresAuth: true)]
    private sealed class FakeSubSourceProvider : FakeProvider
    {
        public override string Provider => "subsource";
    }

    [SubtitleProvider("subdl")]
    private sealed class FakeSubdlProvider : FakeProvider
    {
        public override string Provider => "subdl";
    }

    [SubtitleProvider("gestdown")]
    private sealed class FakeGestdownProvider : FakeProvider
    {
        public override string Provider => "gestdown";
    }

    [SubtitleProvider("xunlei")]
    private sealed class FakeXunleiProvider : FakeProvider
    {
        public override string Provider => "xunlei";
    }

    [SubtitleProvider("assrt", requiresAuth: true)]
    private sealed class FakeAssrtProvider : FakeProvider
    {
        public override string Provider => "assrt";
    }

    [SubtitleProvider("shooter")]
    private sealed class FakeShooterProvider : FakeProvider
    {
        public override string Provider => "shooter";
    }

    private static ISubtitleProvider[] SevenSources() =>
    [
        new FakeOpenSubtitlesProvider(),
        new FakeSubSourceProvider(),
        new FakeSubdlProvider(),
        new FakeGestdownProvider(),
        new FakeXunleiProvider(),
        new FakeAssrtProvider(),
        new FakeShooterProvider()
    ];

    private static ISubtitleSourceManifest[] RealManifests() =>
    [
        new OpenSubtitlesPluginManifest(),
        new SubSourcePluginManifest(),
        new SubdlPluginManifest(),
        new GestdownPluginManifest(),
        new XunleiPluginManifest(),
        new AssrtPluginManifest(),
        new ShooterPluginManifest()
    ];

    private static SubtitleSearchController CreateController(
        ISubtitleProvider[] providers,
        ISubtitleSourceManifest[]? manifests = null,
        Mock<ISettingsAccess>? settings = null,
        InMemorySettingService? settingValues = null,
        SubtitleQuotaStore? quotaStore = null)
    {
        var registry = new SubtitleProviderRegistry(
            providers,
            manifests ?? [],
            NullLogger<SubtitleProviderRegistry>.Instance);
        return new SubtitleSearchController(
            registry,
            settings?.Object ?? SubtitleSettingsDouble.Create().Object,
            CreateOrchestrator(registry, settingValues),
            quotaStore ?? new SubtitleQuotaStore(),
            queueService: null!,
            pinService: null!,
            pipeline: null!,
            requestBuilder: null!,
            dbContext: null!,
            NullLogger<SubtitleSearchController>.Instance);
    }

    /// <summary>
    /// Real orchestrator wiring so the providers endpoint reads cooldowns
    /// through the production path (scoped store over the stateful settings
    /// double). Telemetry is never reached by the endpoints that run under
    /// this stack — the cooldown store and cache are live because
    /// GetActiveCooldownsAsync uses them.
    /// </summary>
    private static SubtitleSearchOrchestrator CreateOrchestrator(
        SubtitleProviderRegistry registry,
        InMemorySettingService? settingValues)
        => new(
            registry,
            new SubtitleMatchScorer(),
            new SubtitleSourceCooldownStore(
                settingValues ?? new InMemorySettingService(),
                NullLogger<SubtitleSourceCooldownStore>.Instance),
            telemetry: null!,
            new MemoryCache(new MemoryCacheOptions()),
            NullLogger<SubtitleSearchOrchestrator>.Instance);

    /// <summary>
    /// Full-stack controller over the shared SQLite fixture — the M3/M4
    /// endpoints run the real orchestrator/queue/pipeline wiring, and the
    /// observation endpoints (history, cooldown health) read the same
    /// SQLite database and the same scoped settings the fixture owns.
    /// </summary>
    private static SubtitleSearchController CreateFullController(SubtitleEndpointFixture fixture)
        => new(
            fixture.Registry,
            SubtitleSettingsDouble.Create().Object,
            fixture.Orchestrator,
            new SubtitleQuotaStore(),
            fixture.QueueService,
            fixture.PinService,
            fixture.Pipeline,
            fixture.RequestBuilder,
            fixture.DbContext,
            NullLogger<SubtitleSearchController>.Instance);

    // Mirrors the production MVC options (InfrastructureModule): camelCase,
    // enums as strings, nulls dropped.
    private static readonly JsonSerializerOptions CamelCase = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    private static JsonDocument Body(ObjectResult result)
        => JsonDocument.Parse(JsonSerializer.Serialize(result.Value, CamelCase));

    [Fact]
    public async Task TestProvider_UnknownProvider_Returns404WithProviderEcho()
    {
        var controller = CreateController([new FakeProvider()]);

        var result = Assert.IsType<NotFoundObjectResult>(await controller.TestProvider("nosuch", CancellationToken.None));

        Assert.Equal(404, result.StatusCode);
        using var body = Body(result);
        Assert.Equal("nosuch", body.RootElement.GetProperty("provider").GetString());
        Assert.NotNull(body.RootElement.GetProperty("message").GetString());
    }

    [Fact]
    public async Task TestProvider_NonProbingSource_KeepsTheStructured501()
    {
        // Sources without ISubtitleProviderHealth (test doubles, future
        // compiled-in sources before they gain a probe) keep the M1
        // contract: structured 501, not an exception.
        var controller = CreateController([new FakeProvider()]);

        var result = Assert.IsType<ObjectResult>(
            await controller.TestProvider("fakesubs", CancellationToken.None));

        Assert.Equal(501, result.StatusCode);
        using var body = Body(result);
        Assert.Equal("fakesubs", body.RootElement.GetProperty("provider").GetString());
        Assert.NotNull(body.RootElement.GetProperty("message").GetString());
    }

    [Fact]
    public async Task TestProvider_ProbingSource_ReturnsOkResultWithLatency()
    {
        var controller = CreateController([new ProbingFakeProvider()]);

        var actionResult = await controller.TestProvider("probing", CancellationToken.None);
        var result = Assert.IsType<OkObjectResult>(actionResult);
        using var body = Body(result);

        Assert.True(body.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("probe ok", body.RootElement.GetProperty("message").GetString());
        // Measured around the probe — a real number, never decorative.
        Assert.True(body.RootElement.GetProperty("latencyMs").GetDouble() >= 0);
    }

    [Fact]
    public async Task TestProvider_ProbeFailure_ReturnsOkFalseWithReasonMessage()
    {
        // Provider failures are verdicts, not 500s (translate-test
        // precedent; PRD AC2 needs the readable bucket).
        var controller = CreateController([new FailingProbeFakeProvider()]);

        var actionResult = await controller.TestProvider("failing", CancellationToken.None);
        var result = Assert.IsType<OkObjectResult>(actionResult);
        using var body = Body(result);

        Assert.False(body.RootElement.GetProperty("ok").GetBoolean());
        Assert.Contains("API key", body.RootElement.GetProperty("message").GetString());
    }

    [Fact]
    public async Task TestProvider_LookupIsCaseInsensitive()
    {
        var controller = CreateController([new ProbingFakeProvider()]);

        var actionResult = await controller.TestProvider("Probing", CancellationToken.None);

        Assert.IsType<OkObjectResult>(actionResult);
    }

    [Fact]
    public async Task GetProviders_Returns200WithAllSourcesInRegistryOrder()
    {
        var controller = CreateController(SevenSources(), RealManifests());

        var result = Assert.IsType<OkObjectResult>(await controller.GetProviders());
        using var body = Body(result);

        // One card per registered source, registry order, no extras.
        Assert.Equal(JsonValueKind.Array, body.RootElement.ValueKind);
        Assert.Equal(7, body.RootElement.GetArrayLength());
        Assert.Equal(
            new[] { "opensubtitles", "subsource", "subdl", "gestdown", "xunlei", "assrt", "shooter" },
            body.RootElement.EnumerateArray().Select(card => card.GetProperty("provider").GetString()).ToArray());

        // Manifest fields pass through untouched: display name,
        // description and the settings form with its field types.
        var opensubtitles = body.RootElement[0];
        Assert.Equal("OpenSubtitles", opensubtitles.GetProperty("displayName").GetString());
        Assert.Contains("OpenSubtitles.com", opensubtitles.GetProperty("description").GetString());
        var settings = opensubtitles.GetProperty("settings");
        Assert.Equal(2, settings.GetArrayLength());
        Assert.Equal(SubtitleSourceSettingKeys.OpenSubtitles.ApiKey, settings[0].GetProperty("key").GetString());
        // Enum member names in PascalCase — what the global
        // JsonStringEnumConverter emits and the settings UI matches on
        // (client types/plugin.ts maps 'Secret'/'Url').
        Assert.Equal("Secret", settings[0].GetProperty("type").GetString());
        Assert.Equal(SubtitleSourceSettingKeys.OpenSubtitles.BaseUrl, settings[1].GetProperty("key").GetString());
        Assert.Equal("Url", settings[1].GetProperty("type").GetString());
    }

    [Fact]
    public async Task GetProviders_RequiresAuthOnlyForTheThreeKeyedSources()
    {
        var controller = CreateController(SevenSources(), RealManifests());

        var result = Assert.IsType<OkObjectResult>(await controller.GetProviders());
        using var body = Body(result);

        foreach (var card in body.RootElement.EnumerateArray())
        {
            var expected = card.GetProperty("provider").GetString() switch
            {
                "opensubtitles" => true,
                "assrt" => true,
                "subsource" => true, // R4: the v1 API made the key mandatory
                _ => false
            };
            Assert.Equal(expected, card.GetProperty("requiresAuth").GetBoolean());
        }
    }

    [Fact]
    public async Task GetProviders_StoredSecretMarksOnlyThatSourceConfigured()
    {
        var controller = CreateController(
            SevenSources(),
            RealManifests(),
            SubtitleSettingsDouble.Create(secrets: new Dictionary<string, string>
            {
                [SubtitleSourceSettingKeys.OpenSubtitles.ApiKey] = "os-api-key",
                // Whitespace-only secrets do not count as configured.
                [SubtitleSourceSettingKeys.Subdl.ApiKey] = "   "
            }));

        var result = Assert.IsType<OkObjectResult>(await controller.GetProviders());
        using var body = Body(result);

        foreach (var card in body.RootElement.EnumerateArray())
        {
            var expected = card.GetProperty("provider").GetString() == "opensubtitles";
            Assert.Equal(expected, card.GetProperty("credentialsConfigured").GetBoolean());
        }
    }

    [Fact]
    public async Task GetProviders_ManifestLessEntryDegradesToProviderKeyAndEmptySettings()
    {
        // Test-double path (registry entries without a manifest) must not
        // throw: the card degrades to the provider key, null description
        // and an empty settings list.
        var controller = CreateController([new FakeProvider()]);

        var result = Assert.IsType<OkObjectResult>(await controller.GetProviders());
        using var body = Body(result);

        var card = body.RootElement[0];
        Assert.Equal("fakesubs", card.GetProperty("provider").GetString());
        Assert.Equal("fakesubs", card.GetProperty("displayName").GetString());
        // Null description — absent under WhenWritingNull, null otherwise.
        Assert.True(
            !card.TryGetProperty("description", out var description) || description.ValueKind == JsonValueKind.Null);
        Assert.Equal(0, card.GetProperty("settings").GetArrayLength());
        Assert.False(card.GetProperty("credentialsConfigured").GetBoolean());
    }

    [Fact]
    public async Task GetProviders_SecretReadFailureDegradesThatSourceAndKeepsTheList()
    {
        // One unreadable secret store must not 500 the settings page: the
        // failing source renders as unconfigured, the rest stay accurate.
        var settings = SubtitleSettingsDouble.Create(secrets: new Dictionary<string, string>
        {
            [SubtitleSourceSettingKeys.Assrt.Token] = "assrt-token"
        });
        settings
            .Setup(access => access.GetEncryptedSettingAsync(SubtitleSourceSettingKeys.OpenSubtitles.ApiKey))
            .ThrowsAsync(new InvalidOperationException("settings store down"));

        var controller = CreateController(SevenSources(), RealManifests(), settings);

        var result = Assert.IsType<OkObjectResult>(await controller.GetProviders());
        using var body = Body(result);

        Assert.Equal(7, body.RootElement.GetArrayLength());
        var byProvider = body.RootElement.EnumerateArray()
            .ToDictionary(card => card.GetProperty("provider").GetString());
        Assert.False(byProvider["opensubtitles"].GetProperty("credentialsConfigured").GetBoolean());
        Assert.True(byProvider["assrt"].GetProperty("credentialsConfigured").GetBoolean());
    }

    [Fact]
    public async Task GetProviders_NoSourcesRegistered_ReturnsEmptyList()
    {
        // Honest empty state: no source registered yet.
        var controller = CreateController([]);

        var result = Assert.IsType<OkObjectResult>(await controller.GetProviders());

        using var body = Body(result);
        Assert.Equal(JsonValueKind.Array, body.RootElement.ValueKind);
        Assert.Equal(0, body.RootElement.GetArrayLength());
    }

    // --- search (⑥): validation codes, response shape, Query contract.

    [Fact]
    public async Task Search_WithoutProvider_Answers400()
    {
        var controller = CreateController([new FakeProvider()]);

        var result = Assert.IsType<BadRequestObjectResult>(await controller.Search(
            new SubtitleSearchRequestBody(1, "Movie", ["en"], ""), CancellationToken.None));

        using var body = Body(result);
        Assert.NotNull(body.RootElement.GetProperty("message").GetString());
    }

    [Fact]
    public async Task Search_WithInvalidMediaType_Answers400()
    {
        var controller = CreateController([new FakeProvider()]);

        Assert.IsType<BadRequestObjectResult>(await controller.Search(
            new SubtitleSearchRequestBody(1, "Season", ["en"], "fakesubs"), CancellationToken.None));
    }

    [Fact]
    public async Task Search_ReturnsScoredCandidatesWithTheDownloadCacheKey()
    {
        var provider = new ScriptedSearchProvider();
        using var fixture = new SubtitleEndpointFixture(provider);
        var controller = CreateFullController(fixture);

        var result = Assert.IsType<OkObjectResult>(await controller.Search(
            new SubtitleSearchRequestBody(fixture.MovieId, "Movie", ["zh"], "scripted"),
            CancellationToken.None));
        using var body = Body(result);

        Assert.NotEmpty(body.RootElement.GetProperty("cacheKey").GetString());
        var candidates = body.RootElement.GetProperty("candidates");
        Assert.Equal(1, candidates.GetArrayLength());
        Assert.Equal("scripted", candidates[0].GetProperty("provider").GetString());
        Assert.Equal("ext-1", candidates[0].GetProperty("externalId").GetString());
        Assert.Equal("zh", candidates[0].GetProperty("language").GetString());
        Assert.True(candidates[0].GetProperty("matchScore").GetInt32() >= 0);
        Assert.Equal(JsonValueKind.Array, body.RootElement.GetProperty("sourceOutcomes").ValueKind);

        // No keyword override in the body → the library-derived query runs.
        Assert.Null(provider.SeenRequest?.Query);
    }

    [Fact]
    public async Task Search_KeywordOverride_ReachesTheOrchestratorRequest()
    {
        // Locked API contract: Query replaces the derived search term; the
        // controller→orchestrator passthrough must carry it verbatim.
        var provider = new ScriptedSearchProvider();
        using var fixture = new SubtitleEndpointFixture(provider);
        var controller = CreateFullController(fixture);

        await controller.Search(
            new SubtitleSearchRequestBody(fixture.MovieId, "Movie", ["zh"], "scripted", Query: "custom keywords"),
            CancellationToken.None);

        Assert.Equal("custom keywords", provider.SeenRequest?.Query);
    }

    // --- download (⑦): expired cache 410, ownership mismatch 400, success
    // shape, SkipTranslation contract.

    [Fact]
    public async Task Download_UnknownOrExpiredCacheKey_Answers410()
    {
        using var fixture = new SubtitleEndpointFixture(new ScriptedSearchProvider());
        var controller = CreateFullController(fixture);

        var result = Assert.IsType<ObjectResult>(await controller.Download(
            new SubtitleDownloadRequestBody("no-such-key", 0, fixture.MovieId, "Movie", null),
            CancellationToken.None));

        Assert.Equal(410, result.StatusCode);
        using var body = Body(result);
        Assert.Equal("no-such-key", body.RootElement.GetProperty("cacheKey").GetString());
        Assert.NotNull(body.RootElement.GetProperty("message").GetString());
    }

    [Fact]
    public async Task Download_ForAnotherMediaItem_Answers400()
    {
        // QA3 P3-2: the cache key belongs to exactly the media the search
        // ran for — replaying it against another library row is rejected.
        var provider = new ScriptedSearchProvider();
        using var fixture = new SubtitleEndpointFixture(provider);
        var controller = CreateFullController(fixture);
        var search = Assert.IsType<OkObjectResult>(await controller.Search(
            new SubtitleSearchRequestBody(fixture.MovieId, "Movie", ["zh"], "scripted"),
            CancellationToken.None));
        var searchResponse = (SubtitleSearchResponse)search.Value!;

        var result = Assert.IsType<BadRequestObjectResult>(await controller.Download(
            new SubtitleDownloadRequestBody(searchResponse.CacheKey, 0, fixture.MovieId + 1, "Movie", null),
            CancellationToken.None));

        using var body = Body(result);
        Assert.NotNull(body.RootElement.GetProperty("message").GetString());
        Assert.Equal(searchResponse.CacheKey, body.RootElement.GetProperty("cacheKey").GetString());
    }

    [Fact]
    public async Task Download_Success_AnswersTheLandingPathAndHistoryRow()
    {
        var provider = new ScriptedSearchProvider();
        using var fixture = new SubtitleEndpointFixture(provider);
        var controller = CreateFullController(fixture);
        var search = Assert.IsType<OkObjectResult>(await controller.Search(
            new SubtitleSearchRequestBody(fixture.MovieId, "Movie", ["zh"], "scripted"),
            CancellationToken.None));
        var searchResponse = (SubtitleSearchResponse)search.Value!;

        var result = Assert.IsType<OkObjectResult>(await controller.Download(
            new SubtitleDownloadRequestBody(searchResponse.CacheKey, 0, fixture.MovieId, "Movie", "Manual"),
            CancellationToken.None));
        using var body = Body(result);

        Assert.True(body.RootElement.GetProperty("historyId").GetInt32() > 0);
        Assert.True(File.Exists(body.RootElement.GetProperty("filePath").GetString()));
        Assert.Equal("Succeeded", body.RootElement.GetProperty("result").GetString());
        Assert.False(body.RootElement.GetProperty("translationDuplicate").GetBoolean());
        // zh is a target language → terminal, no translation request
        // (nulls are dropped by the response serializer).
        Assert.False(body.RootElement.TryGetProperty("translationRequestId", out _));
    }

    [Fact]
    public async Task Download_WithSkipTranslation_LandsTheFileWithoutEnqueue()
    {
        // Locked API contract passthrough: a non-target-language download
        // with skipTranslation=true lands on disk and never touches the
        // translation queue.
        var provider = new ScriptedSearchProvider(language: "en");
        using var fixture = new SubtitleEndpointFixture(provider);
        var controller = CreateFullController(fixture);
        var search = Assert.IsType<OkObjectResult>(await controller.Search(
            new SubtitleSearchRequestBody(fixture.MovieId, "Movie", ["en"], "scripted"),
            CancellationToken.None));
        var searchResponse = (SubtitleSearchResponse)search.Value!;

        var result = Assert.IsType<OkObjectResult>(await controller.Download(
            new SubtitleDownloadRequestBody(
                searchResponse.CacheKey, 0, fixture.MovieId, "Movie", "Manual", SkipTranslation: true),
            CancellationToken.None));
        using var body = Body(result);

        Assert.Equal("Succeeded", body.RootElement.GetProperty("result").GetString());
        Assert.True(File.Exists(body.RootElement.GetProperty("filePath").GetString()));
        Assert.False(body.RootElement.TryGetProperty("translationRequestId", out _));
        fixture.TranslationQueue.Verify(
            queue => queue.CreateImportRequest(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<int?>(), It.IsAny<MediaType>(), It.IsAny<string?>(), It.IsAny<bool>()),
            Times.Never);
    }

    [Fact]
    public async Task Download_WithoutSkipTranslation_EnqueuesTheNonTargetLanguage()
    {
        // The contrast case of the contract: same en download, default
        // flag → the translation queue is used.
        var provider = new ScriptedSearchProvider(language: "en");
        using var fixture = new SubtitleEndpointFixture(provider);
        var controller = CreateFullController(fixture);
        var search = Assert.IsType<OkObjectResult>(await controller.Search(
            new SubtitleSearchRequestBody(fixture.MovieId, "Movie", ["en"], "scripted"),
            CancellationToken.None));
        var searchResponse = (SubtitleSearchResponse)search.Value!;

        var result = Assert.IsType<OkObjectResult>(await controller.Download(
            new SubtitleDownloadRequestBody(searchResponse.CacheKey, 0, fixture.MovieId, "Movie", "Manual"),
            CancellationToken.None));
        using var body = Body(result);

        Assert.Equal(41, body.RootElement.GetProperty("translationRequestId").GetInt32());
    }

    // --- pin (⑤/⑥): response shape of the effective row.

    [Fact]
    public async Task Pin_CreatesAndPinsTheRow_AndAnswersTheEffectiveState()
    {
        using var fixture = new SubtitleEndpointFixture(new ScriptedSearchProvider());
        var controller = CreateFullController(fixture);

        var result = Assert.IsType<OkObjectResult>(await controller.Pin(
            new SubtitlePinRequestBody(fixture.MovieId, "Movie", Pinned: true), CancellationToken.None));
        using var body = Body(result);

        Assert.Equal("Movie", body.RootElement.GetProperty("mediaType").GetString());
        Assert.Equal(fixture.MovieId, body.RootElement.GetProperty("mediaId").GetInt32());
        Assert.True(body.RootElement.GetProperty("pinned").GetBoolean());
        Assert.Equal("Pinned", body.RootElement.GetProperty("priorityRank").GetString());
        Assert.Equal("Pending", body.RootElement.GetProperty("status").GetString());
    }

    // --- backlog (⑥): the aggregate answers real counts.

    [Fact]
    public async Task Backlog_AggregatesTheQueueIntoRealCounts()
    {
        using var fixture = new SubtitleEndpointFixture(new ScriptedSearchProvider());
        var now = DateTimeOffset.UtcNow;
        fixture.DbContext.SubtitleSearchStates.AddRange(
            QueueRow(MediaType.Movie, 1, nextAttemptAt: now.AddHours(-1)),
            QueueRow(MediaType.Movie, 2, pinned: true),
            QueueRow(MediaType.Movie, 3,
                rank: SubtitlePriorityRank.WebhookBoost, boostUntil: now.AddHours(6)),
            QueueRow(MediaType.Movie, 4, nextAttemptAt: now.AddHours(6)),
            QueueRow(MediaType.Movie, 5, status: SubtitleSearchStatus.Succeeded));
        await fixture.DbContext.SaveChangesAsync();
        var controller = CreateFullController(fixture);

        var result = Assert.IsType<OkObjectResult>(await controller.Backlog(CancellationToken.None));
        using var body = Body(result);

        // 4 non-terminal rows (3 due + 1 backing off); 1 pinned; 1 live
        // boost; 3 due right now; the Succeeded row counts nowhere.
        Assert.Equal(4, body.RootElement.GetProperty("backlog").GetInt32());
        Assert.Equal(1, body.RootElement.GetProperty("pinnedCount").GetInt32());
        Assert.Equal(1, body.RootElement.GetProperty("webhookBoostedCount").GetInt32());
        Assert.Equal(3, body.RootElement.GetProperty("nextRoundCount").GetInt32());
    }

    private static SubtitleSearchState QueueRow(
        MediaType mediaType,
        int mediaId,
        SubtitlePriorityRank rank = SubtitlePriorityRank.Normal,
        SubtitleSearchStatus status = SubtitleSearchStatus.Pending,
        bool pinned = false,
        DateTimeOffset? nextAttemptAt = null,
        DateTimeOffset? boostUntil = null) => new()
    {
        MediaType = mediaType,
        MediaId = mediaId,
        Status = status,
        PriorityRank = rank,
        Pinned = pinned,
        WebhookBoostUntil = boostUntil,
        NextAttemptAt = nextAttemptAt ?? DateTimeOffset.UtcNow.AddHours(-1),
        UpdatedAt = DateTimeOffset.UtcNow
    };

    // --- providers cooldown health (⑧, observation batch): the settings
    // cards read the active cooldown map through the production store
    // round-trip — persisted JSON, expired entries dropped, 401/403 on the
    // far-future credential sentinel.

    [Fact]
    public async Task GetProviders_ActiveCooldownMarksUntilAndReason()
    {
        var settingValues = new InMemorySettingService();
        await new SubtitleSourceCooldownStore(
                settingValues, NullLogger<SubtitleSourceCooldownStore>.Instance)
            .RecordFailureAsync("opensubtitles", SubtitleProviderException.ForHttpStatus(
                "opensubtitles", HttpStatusCode.TooManyRequests));
        var controller = CreateController(SevenSources(), RealManifests(), settingValues: settingValues);

        var result = Assert.IsType<OkObjectResult>(await controller.GetProviders());
        using var body = Body(result);

        var byProvider = body.RootElement.EnumerateArray()
            .ToDictionary(card => card.GetProperty("provider").GetString()!);
        var cooled = byProvider["opensubtitles"];
        // 429 → 1h cooldown from now, bucketed reason for the card label.
        var until = DateTimeOffset.Parse(cooled.GetProperty("cooldownUntil").GetString()!);
        Assert.True(until > DateTimeOffset.UtcNow.AddMinutes(55));
        Assert.True(until <= DateTimeOffset.UtcNow.AddHours(1).AddMinutes(1));
        Assert.Equal("RateLimited", cooled.GetProperty("cooldownReason").GetString());

        // Every healthy source carries neither field (nulls are dropped).
        foreach (var (provider, card) in byProvider)
        {
            if (provider == "opensubtitles")
            {
                continue;
            }

            Assert.True(
                !card.TryGetProperty("cooldownUntil", out var untilValue) || untilValue.ValueKind == JsonValueKind.Null,
                $"{provider} must not report a cooldown");
            Assert.True(
                !card.TryGetProperty("cooldownReason", out var reasonValue) || reasonValue.ValueKind == JsonValueKind.Null,
                $"{provider} must not report a cooldown reason");
        }
    }

    [Fact]
    public async Task GetProviders_ExpiredCooldownIsDropped()
    {
        // The store keeps only active entries — a cooldown whose until lies
        // in the past must not reach the card (it would read as stuck).
        var settingValues = new InMemorySettingService();
        var expired = new Dictionary<string, SubtitleSourceCooldown>
        {
            ["opensubtitles"] = new(DateTimeOffset.UtcNow.AddMinutes(-5), "RateLimited", null)
        };
        await settingValues.SetSetting(
            SettingKeys.SubtitleSearch.SourceCooldowns,
            JsonSerializer.Serialize(expired));
        var controller = CreateController(SevenSources(), RealManifests(), settingValues: settingValues);

        var result = Assert.IsType<OkObjectResult>(await controller.GetProviders());
        using var body = Body(result);

        foreach (var card in body.RootElement.EnumerateArray())
        {
            Assert.True(
                !card.TryGetProperty("cooldownUntil", out var until) || until.ValueKind == JsonValueKind.Null);
            Assert.True(
                !card.TryGetProperty("cooldownReason", out var reason) || reason.ValueKind == JsonValueKind.Null);
        }
    }

    [Fact]
    public async Task GetProviders_UnauthorizedCooldownCarriesTheCredentialSentinel()
    {
        // 401/403 cools down "until the credentials change" — the far-future
        // sentinel, which the card renders as a reason label instead of a
        // silly year-9999 timestamp.
        var settingValues = new InMemorySettingService();
        await new SubtitleSourceCooldownStore(
                settingValues, NullLogger<SubtitleSourceCooldownStore>.Instance)
            .RecordFailureAsync("assrt", SubtitleProviderException.ForHttpStatus(
                "assrt", HttpStatusCode.Unauthorized));
        var controller = CreateController(SevenSources(), RealManifests(), settingValues: settingValues);

        var result = Assert.IsType<OkObjectResult>(await controller.GetProviders());
        using var body = Body(result);

        var assrt = body.RootElement.EnumerateArray()
            .Single(card => card.GetProperty("provider").GetString() == "assrt");
        Assert.Equal("Unauthorized", assrt.GetProperty("cooldownReason").GetString());
        var until = DateTimeOffset.Parse(assrt.GetProperty("cooldownUntil").GetString()!);
        Assert.Equal(SubtitleSourceCooldownStore.CredentialChangeHorizon, until);
    }

    // --- providers quota observability (R3): the singleton store's latest
    // snapshot aggregates onto the card as quotaRemaining/quotaResetUtc;
    // sources without a captured snapshot carry neither field, and the
    // quota_exhausted cooldown reaches the card like any other bucket.

    [Fact]
    public async Task GetProviders_QuotaSnapshotMarksRemainingAndResetOnThatSourceOnly()
    {
        var resetUtc = new DateTimeOffset(2026, 10, 18, 0, 0, 0, TimeSpan.Zero);
        var quotaStore = new SubtitleQuotaStore();
        quotaStore.Record("opensubtitles", new SubtitleQuotaSnapshot(
            Remaining: 7, Total: null, Requests: 33, ResetUtc: resetUtc,
            CapturedAtUtc: DateTimeOffset.UtcNow));
        var controller = CreateController(SevenSources(), RealManifests(), quotaStore: quotaStore);

        var result = Assert.IsType<OkObjectResult>(await controller.GetProviders());
        using var body = Body(result);

        var opensubtitles = body.RootElement.EnumerateArray()
            .Single(card => card.GetProperty("provider").GetString() == "opensubtitles");
        Assert.Equal(7, opensubtitles.GetProperty("quotaRemaining").GetInt32());
        Assert.Equal(resetUtc, DateTimeOffset.Parse(opensubtitles.GetProperty("quotaResetUtc").GetString()!));

        // Every source without a captured snapshot carries neither field
        // (nulls are dropped under WhenWritingNull — the badge stays hidden).
        foreach (var card in body.RootElement.EnumerateArray())
        {
            if (card.GetProperty("provider").GetString() == "opensubtitles")
            {
                continue;
            }

            Assert.True(
                !card.TryGetProperty("quotaRemaining", out var remaining) || remaining.ValueKind == JsonValueKind.Null,
                $"{card.GetProperty("provider").GetString()} must not report a quota");
            Assert.True(
                !card.TryGetProperty("quotaResetUtc", out var reset) || reset.ValueKind == JsonValueKind.Null,
                $"{card.GetProperty("provider").GetString()} must not report a quota reset");
        }
    }

    [Fact]
    public async Task GetProviders_WithoutAnyQuotaSnapshot_OmitsTheQuotaFields()
    {
        var controller = CreateController(SevenSources(), RealManifests());

        var result = Assert.IsType<OkObjectResult>(await controller.GetProviders());
        using var body = Body(result);

        foreach (var card in body.RootElement.EnumerateArray())
        {
            Assert.True(
                !card.TryGetProperty("quotaRemaining", out var remaining) || remaining.ValueKind == JsonValueKind.Null);
            Assert.True(
                !card.TryGetProperty("quotaResetUtc", out var reset) || reset.ValueKind == JsonValueKind.Null);
        }
    }

    [Fact]
    public async Task GetProviders_QuotaExhaustedCooldownCarriesUntilAndReason()
    {
        // The R3 circuit-breaker state on the card: until = the source's own
        // reset moment, reason = the quota_exhausted bucket.
        var resetUtc = DateTimeOffset.UtcNow.AddHours(6);
        var settingValues = new InMemorySettingService();
        await new SubtitleSourceCooldownStore(
                settingValues, NullLogger<SubtitleSourceCooldownStore>.Instance)
            .RecordQuotaExhaustedAsync("opensubtitles", resetUtc, "quota gone");
        var controller = CreateController(SevenSources(), RealManifests(), settingValues: settingValues);

        var result = Assert.IsType<OkObjectResult>(await controller.GetProviders());
        using var body = Body(result);

        var opensubtitles = body.RootElement.EnumerateArray()
            .Single(card => card.GetProperty("provider").GetString() == "opensubtitles");
        Assert.Equal("quota_exhausted", opensubtitles.GetProperty("cooldownReason").GetString());
        Assert.Equal(resetUtc, DateTimeOffset.Parse(opensubtitles.GetProperty("cooldownUntil").GetString()!));
    }

    // --- history (screen 13): keyset pagination over subtitle_downloads
    // with the media-title join, filters, and the honest empty states.

    /// <summary>
    /// Seeds one show/season/episode chain and returns the episode id —
    /// history episode titles resolve through season→show.
    /// </summary>
    private static async Task<(int EpisodeId, int ShowId)> SeedEpisodeAsync(LingarrDbContext db)
    {
        var show = new Show
        {
            SonarrId = 1,
            Title = "Silo",
            Path = "/tv/silo",
            DateAdded = DateTimeOffset.UtcNow
        };
        var season = new Season { SeasonNumber = 1, Show = show };
        var episode = new Episode
        {
            SonarrId = 2,
            EpisodeNumber = 2,
            Title = "The Common",
            Season = season,
            DateAdded = DateTimeOffset.UtcNow
        };
        db.Shows.Add(show);
        db.Seasons.Add(season);
        db.Episodes.Add(episode);
        await db.SaveChangesAsync();
        return (episode.Id, show.Id);
    }

    /// <summary>
    /// One history row. The entity name clashes with the ABI record of the
    /// same name — the entity is fully qualified here (documented pitfall).
    /// </summary>
    private static Lingarr.Core.Entities.SubtitleDownload DownloadRow(
        MediaType mediaType,
        int mediaId,
        string provider,
        SubtitleDownloadResult result,
        DateTimeOffset createdAt,
        string language = "zh",
        string filePath = "/media/movies/Movie.2019.WEBDL-1080p.zh.srt",
        string? error = null,
        SubtitleDownloadSource source = SubtitleDownloadSource.Manual) => new()
    {
        MediaType = mediaType,
        MediaId = mediaId,
        Provider = provider,
        ExternalId = "ext-1",
        Language = language,
        FilePath = filePath,
        MatchScore = 90,
        HashMatch = false,
        Source = source,
        Result = result,
        Error = error,
        CreatedAt = createdAt
    };

    private static SubtitleHistoryResponse HistoryPage(IActionResult result)
        => Assert.IsType<SubtitleHistoryResponse>(Assert.IsType<OkObjectResult>(result).Value);

    [Fact]
    public async Task History_ReturnsNewestFirstWithLibraryTitlesAndFilterCombos()
    {
        using var fixture = new SubtitleEndpointFixture(new ScriptedSearchProvider());
        var (episodeId, showId) = await SeedEpisodeAsync(fixture.DbContext);
        var now = DateTimeOffset.UtcNow;
        fixture.DbContext.SubtitleDownloads.AddRange(
            DownloadRow(MediaType.Movie, fixture.MovieId, "opensubtitles", SubtitleDownloadResult.Succeeded,
                now.AddMinutes(-1)),
            DownloadRow(MediaType.Episode, episodeId, "assrt", SubtitleDownloadResult.Failed,
                now.AddMinutes(-2), language: "en", filePath: string.Empty, error: "assrt rejected the request",
                source: SubtitleDownloadSource.Auto),
            DownloadRow(MediaType.Movie, fixture.MovieId, "opensubtitles", SubtitleDownloadResult.LowConfidenceSkipped,
                now.AddMinutes(-3)),
            DownloadRow(MediaType.Movie, fixture.MovieId, "gestdown", SubtitleDownloadResult.Succeeded,
                now.AddMinutes(-4), source: SubtitleDownloadSource.Webhook),
            // Media row deleted long ago — the download stays, the title goes null.
            DownloadRow(MediaType.Movie, 999_999, "opensubtitles", SubtitleDownloadResult.AlreadyExistsSkipped,
                now.AddMinutes(-5)));
        await fixture.DbContext.SaveChangesAsync();
        var controller = CreateFullController(fixture);

        // Unfiltered: newest first (row 1 was seeded with the newest
        // timestamp), one page covers the seeded rows.
        var page = HistoryPage(await controller.History());
        Assert.Null(page.NextCursor);
        Assert.Equal(new[] { 1, 2, 3, 4, 5 }, page.Items.Select(item => item.Id).ToArray());

        var newest = page.Items[0];
        Assert.Equal("Movie", newest.MediaType);
        Assert.Equal(fixture.MovieId, newest.MediaId);
        Assert.Equal("Movie", newest.MediaTitle);
        Assert.Equal("opensubtitles", newest.Provider);
        Assert.Equal("zh", newest.Language);
        Assert.Equal("Manual", newest.Source);
        Assert.Equal("Succeeded", newest.Result);
        Assert.Null(newest.Error);
        Assert.Equal("Movie.2019.WEBDL-1080p.zh.srt", newest.FileName);

        var episodeRow = page.Items[1];
        Assert.Equal("Episode", episodeRow.MediaType);
        Assert.Equal("Silo S01E02 The Common", episodeRow.MediaTitle);
        // the owning show's library id rides along — the client routes
        // episode rows into the SHOW page and must not use the episode id
        Assert.Equal(showId, episodeRow.ShowId);
        Assert.Equal("Auto", episodeRow.Source);
        Assert.Equal("Failed", episodeRow.Result);
        Assert.Equal("assrt rejected the request", episodeRow.Error);
        Assert.Null(episodeRow.FileName); // nothing landed

        // movie rows never carry a show id
        Assert.Null(newest.ShowId);

        Assert.Null(page.Items[4].MediaTitle); // removed media → null

        // Filter combinations narrow to the matching rows only.
        var episodes = HistoryPage(await controller.History(mediaType: "episode"));
        Assert.Equal(new[] { 2 }, episodes.Items.Select(item => item.Id).ToArray());

        var webhook = HistoryPage(await controller.History(provider: "gestdown"));
        Assert.Equal(new[] { 4 }, webhook.Items.Select(item => item.Id).ToArray());

        var failures = HistoryPage(await controller.History(result: "Failed"));
        Assert.Equal(new[] { 2 }, failures.Items.Select(item => item.Id).ToArray());

        var combo = HistoryPage(await controller.History(mediaType: "movie", provider: "opensubtitles", result: "Succeeded"));
        Assert.Equal(new[] { 1 }, combo.Items.Select(item => item.Id).ToArray());
    }

    [Fact]
    public async Task History_KeysetWalkCoversEveryRowWithoutGapsOrDupes()
    {
        // Ascending one-second stamps, paged two at a time: the walk must
        // cover every row exactly once (ids DESC inside the walk order
        // because id 5 carries the newest stamp).
        using var fixture = new SubtitleEndpointFixture(new ScriptedSearchProvider());
        var stamp = new DateTimeOffset(2026, 10, 15, 8, 0, 0, TimeSpan.Zero);
        fixture.DbContext.SubtitleDownloads.AddRange(
            Enumerable.Range(0, 5)
                .Select(index => DownloadRow(
                    MediaType.Movie, fixture.MovieId, "opensubtitles",
                    SubtitleDownloadResult.Succeeded, stamp.AddSeconds(index)))
                .ToArray());
        await fixture.DbContext.SaveChangesAsync();
        var controller = CreateFullController(fixture);

        var seen = new List<int>();
        string? cursor = null;
        for (var round = 0; round < 5; round++)
        {
            var page = HistoryPage(await controller.History(cursor: cursor, limit: 2));
            seen.AddRange(page.Items.Select(item => item.Id));
            if (page.NextCursor is null)
            {
                // Two rows per page over five rows: 2 + 2 + 1 — the last
                // page must be the third one.
                Assert.Equal(2, round);
                break;
            }

            cursor = page.NextCursor;
        }

        // Newest first, nothing skipped, nothing repeated.
        Assert.Equal(new[] { 5, 4, 3, 2, 1 }, seen);
    }

    [Fact]
    public async Task History_SameTimestampBurstsPageWithoutGapsOrDupes()
    {
        // The sharper edge of the tiebreak: every row carries the exact
        // same created_at, so the walk is pure id DESC.
        using var fixture = new SubtitleEndpointFixture(new ScriptedSearchProvider());
        var stamp = new DateTimeOffset(2026, 10, 15, 9, 30, 0, TimeSpan.Zero);
        fixture.DbContext.SubtitleDownloads.AddRange(
            Enumerable.Range(0, 5)
                .Select(_ => DownloadRow(
                    MediaType.Movie, fixture.MovieId, "opensubtitles",
                    SubtitleDownloadResult.Succeeded, stamp))
                .ToArray());
        await fixture.DbContext.SaveChangesAsync();
        var controller = CreateFullController(fixture);

        var first = HistoryPage(await controller.History(limit: 3));
        Assert.Equal(new[] { 5, 4, 3 }, first.Items.Select(item => item.Id).ToArray());
        Assert.NotNull(first.NextCursor);

        var second = HistoryPage(await controller.History(cursor: first.NextCursor, limit: 3));
        Assert.Equal(new[] { 2, 1 }, second.Items.Select(item => item.Id).ToArray());
        // Last page: no cursor to follow.
        Assert.Null(second.NextCursor);
    }

    [Fact]
    public async Task History_EmptyAndUnknownProvider_AnswerHonestEmptyPages()
    {
        using var fixture = new SubtitleEndpointFixture(new ScriptedSearchProvider());
        var controller = CreateFullController(fixture);

        // Nothing recorded yet.
        var empty = HistoryPage(await controller.History());
        Assert.Empty(empty.Items);
        Assert.Null(empty.NextCursor);

        // Unknown provider is a filter, not an address — empty page, not 404.
        var unknown = HistoryPage(await controller.History(provider: "nosuchsource"));
        Assert.Empty(unknown.Items);
        Assert.Null(unknown.NextCursor);
    }

    [Fact]
    public async Task History_LimitIsClampedIntoRange()
    {
        using var fixture = new SubtitleEndpointFixture(new ScriptedSearchProvider());
        var now = DateTimeOffset.UtcNow;
        fixture.DbContext.SubtitleDownloads.AddRange(
            DownloadRow(MediaType.Movie, fixture.MovieId, "opensubtitles", SubtitleDownloadResult.Succeeded,
                now.AddMinutes(-1)),
            DownloadRow(MediaType.Movie, fixture.MovieId, "opensubtitles", SubtitleDownloadResult.Succeeded,
                now.AddMinutes(-2)),
            DownloadRow(MediaType.Movie, fixture.MovieId, "opensubtitles", SubtitleDownloadResult.Succeeded,
                now.AddMinutes(-3)));
        await fixture.DbContext.SaveChangesAsync();
        var controller = CreateFullController(fixture);

        // Omitted/zero limit falls back to the default of 50 (all rows).
        var all = HistoryPage(await controller.History());
        Assert.Equal(3, all.Items.Count);
        Assert.Null(all.NextCursor);

        // Below-range limits clamp to 1; above-range to 100 — both answer
        // 200 with a follow-up cursor instead of rejecting. Row 1 is the
        // newest (now-1min), so the single-item page is row 1.
        var single = HistoryPage(await controller.History(limit: -5));
        Assert.Equal(new[] { 1 }, single.Items.Select(item => item.Id).ToArray());
        Assert.NotNull(single.NextCursor);

        var capped = HistoryPage(await controller.History(limit: 1000));
        Assert.Equal(3, capped.Items.Count);
        Assert.Null(capped.NextCursor);
    }

    [Fact]
    public async Task History_InvalidParameters_Answer400()
    {
        using var fixture = new SubtitleEndpointFixture(new ScriptedSearchProvider());
        var controller = CreateFullController(fixture);

        Assert.IsType<BadRequestObjectResult>(await controller.History(mediaType: "season"));
        Assert.IsType<BadRequestObjectResult>(await controller.History(result: "Maybe"));

        var result = Assert.IsType<BadRequestObjectResult>(await controller.History(cursor: "not-a-cursor"));
        using var body = Body(result);
        Assert.NotNull(body.RootElement.GetProperty("message").GetString());
    }

    [Fact]
    public async Task History_SerializesTheLockedWireShape()
    {
        // The front end's contract: camelCase names, PascalCase enum names
        // as strings, nulls dropped, ISO-8601 timestamps.
        using var fixture = new SubtitleEndpointFixture(new ScriptedSearchProvider());
        fixture.DbContext.SubtitleDownloads.Add(DownloadRow(
            MediaType.Movie, fixture.MovieId, "opensubtitles", SubtitleDownloadResult.Succeeded,
            new DateTimeOffset(2026, 10, 15, 10, 0, 0, TimeSpan.Zero)));
        await fixture.DbContext.SaveChangesAsync();
        var controller = CreateFullController(fixture);

        var result = Assert.IsType<OkObjectResult>(await controller.History());
        using var body = Body(result);

        var item = body.RootElement.GetProperty("items")[0];
        Assert.Equal(fixture.MovieId, item.GetProperty("mediaId").GetInt32());
        Assert.Equal("Movie", item.GetProperty("mediaType").GetString());
        Assert.Equal("Movie", item.GetProperty("mediaTitle").GetString());
        Assert.Equal("opensubtitles", item.GetProperty("provider").GetString());
        Assert.Equal("zh", item.GetProperty("language").GetString());
        Assert.Equal("Manual", item.GetProperty("source").GetString());
        Assert.Equal("Succeeded", item.GetProperty("result").GetString());
        Assert.Equal("Movie.2019.WEBDL-1080p.zh.srt", item.GetProperty("fileName").GetString());
        Assert.NotNull(item.GetProperty("createdAt").GetString());
        // error is null and therefore dropped by the response serializer
        Assert.False(item.TryGetProperty("error", out _));
        // last page: nextCursor is null → dropped on the wire (same
        // convention as translationRequestId; the client reads falsy)
        Assert.False(body.RootElement.TryGetProperty("nextCursor", out _));
    }
}
