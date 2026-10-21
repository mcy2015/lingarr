using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Lingarr.Core.Configuration;
using Lingarr.Core.Data;
using Lingarr.Core.Entities;
using Lingarr.Core.Enum;
using Lingarr.Server.Modules.Auth;
using Lingarr.Server.Modules.Infrastructure;
using Lingarr.Server.Modules.Infrastructure.Notifications;
using Lingarr.Server.Modules.Settings;
using Lingarr.Server.Modules.SubtitleSearch.Abstractions;
using Lingarr.Server.Tests.Modules.SubtitleSearch;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Lingarr.Server.Tests.Modules.Infrastructure.Notifications;

/// <summary>
/// Notification fan-out contracts (B11): the locked webhook JSON contract
/// (all fields, ISO 8601 timestamp), the Telegram sendMessage shape (URL
/// carries the bot token, body carries chat_id + text), the allow-list
/// verdict (an unlisted event leaves the process), the master switch, the
/// per-channel isolation (a failing channel is a warning and never blocks
/// the other channel or the caller), the encrypted-channel token read, and
/// the library title resolution. The HTTP layer is the shared
/// <see cref="StubHttpMessageHandler"/>; settings are the Moq seam so the
/// token's read path is verifiable.
/// </summary>
public class NotificationServiceTests
{
    private const string WebhookUrl = "http://hooks.test/lingarr";
    private const string BotToken = "12345:unit-test-bot-token";
    private const string ChatId = "-1001234567890";

    private readonly Mock<ISettingService> _settingService = new();
    private readonly Dictionary<string, string> _plainSettings = new();
    private readonly Dictionary<string, string> _encryptedSettings = new();
    private readonly StubHttpMessageHandler _handler = new();
    private readonly Mock<ILogger<NotificationService>> _logger = new();
    private readonly NotificationDbContext _dbContext = new(
        new DbContextOptionsBuilder<LingarrDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    public NotificationServiceTests()
    {
        _settingService
            .Setup(service => service.GetSettings(It.IsAny<IEnumerable<string>>()))
            .ReturnsAsync((IEnumerable<string> keys) => keys
                .Where(key => _plainSettings.ContainsKey(key))
                .ToDictionary(key => key, key => _plainSettings[key]));
        _settingService
            .Setup(service => service.GetSetting(It.IsAny<string>()))
            .ReturnsAsync((string key) => _plainSettings.TryGetValue(key, out var value) ? value : null);
        _settingService
            .Setup(service => service.GetEncryptedSetting(It.IsAny<string>()))
            .ReturnsAsync((string key) => _encryptedSettings.TryGetValue(key, out var value) ? value : null);
    }

    private void EnableNotifications(string events = "downloaded,upgraded", bool webhook = true, bool telegram = true)
    {
        _plainSettings[SettingKeys.Notifications.Enabled] = "true";
        _plainSettings[SettingKeys.Notifications.Events] = events;
        if (webhook)
        {
            _plainSettings[SettingKeys.Notifications.WebhookUrl] = WebhookUrl;
        }

        if (telegram)
        {
            _plainSettings[SettingKeys.Notifications.TelegramChatId] = ChatId;
            _encryptedSettings[SettingKeys.Notifications.TelegramBotToken] = BotToken;
        }
    }

    private NotificationService CreateService(
        HttpClient? webhookClient = null,
        HttpClient? telegramClient = null) => new(
        _settingService.Object,
        _dbContext,
        new WebhookChannel(webhookClient ?? _handler.ToClient()),
        new TelegramChannel(telegramClient ?? _handler.ToClient()),
        _logger.Object);

    private static NotificationEvent DownloadedEvent(
        string title = "Movie",
        string language = "en",
        string provider = "opensubtitles") => new(
        NotificationService.EventDownloaded,
        MediaType.Movie,
        MediaId: null,
        Title: title,
        Language: language,
        Provider: provider,
        Result: "succeeded");

    private static async Task<JsonDocument> ReadJsonAsync(HttpRequestMessage request)
    {
        var payload = await request.Content!.ReadAsStringAsync();
        return JsonDocument.Parse(payload);
    }

    // ── Acceptance 1: downloaded reaches both channels with the contract.

    [Fact]
    public async Task Notify_Downloaded_DeliversTheContractJsonToTheWebhook()
    {
        EnableNotifications();
        _handler.EnqueueStatus(HttpStatusCode.OK).EnqueueStatus(HttpStatusCode.OK);
        var service = CreateService();

        await service.NotifyAsync(DownloadedEvent());

        var webhook = _handler.Requests.Single(request => request.Method == HttpMethod.Post
                                                          && request.RequestUri!.ToString() == WebhookUrl);
        Assert.Contains("application/json", webhook.Content!.Headers.ContentType!.ToString());

        using var json = await ReadJsonAsync(webhook);
        var root = json.RootElement;
        Assert.Equal("downloaded", root.GetProperty("event").GetString());
        Assert.Equal("movie", root.GetProperty("mediaType").GetString());
        Assert.Equal("Movie", root.GetProperty("title").GetString());
        Assert.Equal("en", root.GetProperty("language").GetString());
        Assert.Equal("opensubtitles", root.GetProperty("provider").GetString());
        Assert.Equal("succeeded", root.GetProperty("result").GetString());

        // The timestamp must be ISO 8601 round-trippable and UTC ("O" ends
        // in Z or an explicit +00:00 offset).
        var timestamp = root.GetProperty("timestamp").GetString();
        Assert.NotNull(timestamp);
        Assert.True(DateTimeOffset.TryParse(timestamp, out var parsed), $"not ISO 8601: {timestamp}");
        Assert.Equal(TimeSpan.Zero, parsed.Offset);
    }

    [Fact]
    public async Task Notify_Downloaded_SendsTheTelegramMessage()
    {
        EnableNotifications();
        _handler.EnqueueStatus(HttpStatusCode.OK).EnqueueStatus(HttpStatusCode.OK);
        var service = CreateService();

        await service.NotifyAsync(DownloadedEvent());

        var telegram = _handler.Requests.Single(request =>
            request.RequestUri!.Host == "api.telegram.org");
        Assert.Equal(
            $"https://api.telegram.org/bot{BotToken}/sendMessage",
            telegram.RequestUri!.ToString());

        using var json = await ReadJsonAsync(telegram);
        Assert.Equal(ChatId, json.RootElement.GetProperty("chat_id").GetString());
        var text = json.RootElement.GetProperty("text").GetString();
        Assert.NotNull(text);
        Assert.Contains("downloaded", text);
        Assert.Contains("Movie", text);
        Assert.Contains("en", text);
        Assert.Contains("opensubtitles", text);
    }

    [Fact]
    public async Task Notify_FailureEvent_CarriesTheDetailAndResultLabel()
    {
        EnableNotifications(events: "download_failed", webhook: true, telegram: false);
        _handler.EnqueueStatus(HttpStatusCode.OK);
        var service = CreateService();

        await service.NotifyAsync(new NotificationEvent(
            NotificationService.EventDownloadFailed, MediaType.Movie, MediaId: null,
            Title: "Movie", Language: "en", Provider: "opensubtitles",
            Result: "failed", Detail: "opensubtitles throttled the download (HTTP 493)."));

        using var json = await ReadJsonAsync(_handler.Requests.Single());
        Assert.Equal("download_failed", json.RootElement.GetProperty("event").GetString());
        Assert.Equal("failed", json.RootElement.GetProperty("result").GetString());
        Assert.Contains("throttled", json.RootElement.GetProperty("detail").GetString());
    }

    // ── Acceptance 2: allow-list + master switch + channel isolation.

    [Fact]
    public async Task Notify_EventNotOnTheAllowList_SendsNothing()
    {
        EnableNotifications(events: "upgraded,job_failed");
        var service = CreateService();

        await service.NotifyAsync(DownloadedEvent()); // downloaded is not listed

        Assert.Empty(_handler.Requests);
    }

    [Fact]
    public async Task Notify_MasterSwitchOff_SendsNothing()
    {
        _plainSettings[SettingKeys.Notifications.Enabled] = "false";
        _plainSettings[SettingKeys.Notifications.Events] = "downloaded";
        _plainSettings[SettingKeys.Notifications.WebhookUrl] = WebhookUrl;
        var service = CreateService();

        await service.NotifyAsync(DownloadedEvent());

        Assert.Empty(_handler.Requests);
    }

    [Fact]
    public async Task Notify_WebhookRejectsWith500_WarnsOnceAndStillDeliversTelegram()
    {
        EnableNotifications();
        _handler.EnqueueStatus(HttpStatusCode.InternalServerError);
        _handler.EnqueueStatus(HttpStatusCode.OK);
        var service = CreateService();

        // The channel failure is a warning — the call must not throw and
        // the Telegram half of the fan-out still runs (per-channel try/catch).
        await service.NotifyAsync(DownloadedEvent());

        Assert.Equal(2, _handler.Requests.Count);
        VerifyWarningOnce("Webhook notification");
    }

    [Fact]
    public async Task Notify_TelegramTransportFailure_LogsARedactedMessage()
    {
        EnableNotifications();
        _handler.EnqueueStatus(HttpStatusCode.OK); // webhook fine
        // The telegram responder throws an exception whose message embeds the
        // full request URL (token included) — the transport-failure shape the
        // redaction exists for. The raw exception object must never be logged.
        _handler.Enqueue(_ => throw new HttpRequestException(
            $"Failed to fetch https://api.telegram.org/bot{BotToken}/sendMessage"));
        var service = CreateService();

        await service.NotifyAsync(DownloadedEvent());

        Assert.Equal(2, _handler.Requests.Count);
        _logger.Verify(logger => logger.Log(
            LogLevel.Warning,
            It.IsAny<EventId>(),
            It.Is<It.IsAnyType>((state, _) =>
                state.ToString()!.Contains("***")
                && !state.ToString()!.Contains(BotToken)),
            It.IsAny<Exception?>(),
            It.IsAny<Func<It.IsAnyType, Exception?, string>>()), Times.Once);
    }

    [Fact]
    public async Task Notify_SettingsReadFails_SwallowsIntoAWarningNeverThrows()
    {
        _settingService
            .Setup(service => service.GetSettings(It.IsAny<IEnumerable<string>>()))
            .ThrowsAsync(new InvalidOperationException("settings store unavailable"));
        var service = CreateService();

        // The never-block-the-main-flow contract: the embed sites call this
        // inline in their success/failure paths.
        await service.NotifyAsync(DownloadedEvent());

        Assert.Empty(_handler.Requests);
        VerifyWarningOnce("Notification dispatch");
    }

    [Fact]
    public async Task Notify_NoChannelConfigured_SendsNothingAndDoesNotThrow()
    {
        _plainSettings[SettingKeys.Notifications.Enabled] = "true";
        _plainSettings[SettingKeys.Notifications.Events] = "downloaded";
        // No webhook URL, no telegram token/chat — an unconfigured
        // integration makes no requests.
        var service = CreateService();

        await service.NotifyAsync(DownloadedEvent());

        Assert.Empty(_handler.Requests);
    }

    // ── The token contract: encrypted read, never the plain channel.

    [Fact]
    public async Task Notify_TokenIsReadThroughTheEncryptedChannelOnly()
    {
        EnableNotifications();
        _handler.EnqueueStatus(HttpStatusCode.OK).EnqueueStatus(HttpStatusCode.OK);
        var service = CreateService();

        await service.NotifyAsync(DownloadedEvent());

        _settingService.Verify(
            service => service.GetEncryptedSetting(SettingKeys.Notifications.TelegramBotToken),
            Times.Once);
        _settingService.Verify(
            service => service.GetSetting(SettingKeys.Notifications.TelegramBotToken),
            Times.Never);
    }

    // ── Allow-list fallback semantics.

    [Theory]
    [InlineData(null, "downloaded", true)]   // missing row → seeded default
    [InlineData("", "downloaded", true)]     // blank row → seeded default
    [InlineData("", "translation_failed", false)]
    [InlineData("downloaded,upgraded", "upgraded", true)]
    [InlineData(" Downloaded , job_failed ", "downloaded", true)] // trimmed, case-insensitive
    [InlineData("upgraded", "downloaded", false)]
    public void IsEventAllowed_MatchesTheAllowListSemantics(
        string? configured, string eventName, bool expected)
    {
        Assert.Equal(expected, NotificationService.IsEventAllowed(configured, eventName));
    }

    [Fact]
    public void ParseEvents_BlankFallsBackToTheSeededDefault()
    {
        Assert.Equal(
            new[] { "downloaded", "upgraded" },
            NotificationService.ParseEvents(null));
        Assert.Equal(
            new[] { "downloaded", "upgraded" },
            NotificationService.ParseEvents("  "));
    }

    [Fact]
    public void Redact_ReplacesEverySecretOccurrence()
    {
        Assert.Equal(
            "fetch https://api.telegram.org/bot***/sendMessage failed",
            NotificationService.Redact(
                $"fetch https://api.telegram.org/bot{BotToken}/sendMessage failed", BotToken));
        Assert.Equal("unchanged", NotificationService.Redact("unchanged", null));
    }

    // ── Title resolution: the library display shapes.

    [Fact]
    public async Task Notify_TitleMissingOnAMovieEvent_ResolvedFromTheLibrary()
    {
        _dbContext.Movies.Add(new Movie
        {
            RadarrId = 1,
            Title = "Resolved Movie",
            Year = 2019,
            FileName = "Resolved.2019.mkv",
            Path = "/media",
            DateAdded = DateTimeOffset.UtcNow
        });
        await _dbContext.SaveChangesAsync();
        var movieId = await _dbContext.Movies.Select(movie => movie.Id).SingleAsync();

        EnableNotifications(webhook: true, telegram: false);
        _handler.EnqueueStatus(HttpStatusCode.OK);
        var service = CreateService();

        await service.NotifyAsync(new NotificationEvent(
            NotificationService.EventDownloaded, MediaType.Movie, movieId,
            Title: null, Language: "en", Provider: "opensubtitles", Result: "succeeded"));

        using var json = await ReadJsonAsync(_handler.Requests.Single());
        Assert.Equal("Resolved Movie", json.RootElement.GetProperty("title").GetString());
    }

    [Fact]
    public async Task Notify_EpisodeEvent_ResolvesTheShowEpisodeTitle()
    {
        var show = new Show
        {
            SonarrId = 1,
            Title = "Show",
            Path = "/media/show",
            DateAdded = DateTimeOffset.UtcNow
        };
        _dbContext.Shows.Add(show);
        _dbContext.Seasons.Add(new Season { SeasonNumber = 2, Path = "/media/show/s02", Show = show });
        await _dbContext.SaveChangesAsync();
        var season = await _dbContext.Seasons.SingleAsync();
        _dbContext.Episodes.Add(new Episode
        {
            SonarrId = 201,
            EpisodeNumber = 3,
            Title = "Pilot",
            FileName = "Show.S02E03.mkv",
            Path = "/media/show/s02",
            Season = season,
            DateAdded = DateTimeOffset.UtcNow
        });
        await _dbContext.SaveChangesAsync();
        var episodeId = await _dbContext.Episodes.Select(episode => episode.Id).SingleAsync();

        EnableNotifications(webhook: true, telegram: false);
        _handler.EnqueueStatus(HttpStatusCode.OK);
        var service = CreateService();

        await service.NotifyAsync(new NotificationEvent(
            NotificationService.EventDownloaded, MediaType.Episode, episodeId,
            Title: null, Language: "zh", Provider: "subsource", Result: "succeeded"));

        using var json = await ReadJsonAsync(_handler.Requests.Single());
        Assert.Equal("Show - S02E03 - Pilot", json.RootElement.GetProperty("title").GetString());
        Assert.Equal("episode", json.RootElement.GetProperty("mediaType").GetString());
    }

    [Fact]
    public async Task Notify_VanishedMediaRow_NotifiesWithNoTitleInsteadOfFailing()
    {
        EnableNotifications(webhook: true, telegram: false);
        _handler.EnqueueStatus(HttpStatusCode.OK);
        var service = CreateService();

        await service.NotifyAsync(new NotificationEvent(
            NotificationService.EventDownloaded, MediaType.Movie, MediaId: 424242,
            Title: null, Language: "en", Provider: "opensubtitles", Result: "succeeded"));

        using var json = await ReadJsonAsync(_handler.Requests.Single());
        // Honest shape: the media row is gone, so no title is invented — the
        // event key, media id facts and timestamp still leave the process.
        Assert.Throws<KeyNotFoundException>(() => json.RootElement.GetProperty("title"));
    }

    private void VerifyWarningOnce(string messageFragment)
    {
        _logger.Verify(logger => logger.Log(
            LogLevel.Warning,
            It.IsAny<EventId>(),
            It.Is<It.IsAnyType>((state, _) => state.ToString()!.Contains(messageFragment)),
            It.IsAny<Exception?>(),
            It.IsAny<Func<It.IsAnyType, Exception?, string>>()), Times.Once);
    }

    /// <summary>Bare context for the title-resolution reads (the InMemory precedent).</summary>
    private sealed class NotificationDbContext(DbContextOptions options) : LingarrDbContext(options);
}

/// <summary>
/// Embed-point contracts (B11) through the real download pipeline on the
/// shared <see cref="SubtitleEndpointFixture"/> (real SQLite, real settings
/// double): a success fires exactly one "downloaded" through the configured
/// channels with the library-resolved title, a failure fires
/// "download_failed" with the readable reason, the shipped defaults
/// (switch off / event unlisted / upgrade-source landing) send nothing —
/// and the pipeline result itself never depends on the notification.
/// </summary>
public class NotificationEmbedTests
{
    private const string SrtText = "1\r\n00:00:01,000 --> 00:00:02,000\r\nHello world\r\n";

    private static byte[] SrtPayload => System.Text.Encoding.UTF8.GetBytes(SrtText);

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

    /// <summary>Provider double answering a plain SRT payload (the pipeline-tests precedent).</summary>
    private sealed class SrtProvider : ISubtitleProvider
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

    /// <summary>Provider double whose download always fails content-invalid.</summary>
    private sealed class FailingProvider : ISubtitleProvider
    {
        public string Provider => "fake";

        public Task<SubtitleSearchResult> SearchAsync(
            SubtitleSearchRequest request, CancellationToken cancellationToken)
            => Task.FromResult(SubtitleSearchResult.Empty);

        public Task<Lingarr.Server.Modules.SubtitleSearch.Abstractions.SubtitleDownload> DownloadAsync(
            SubtitleCandidateRef candidateRef, CancellationToken cancellationToken)
            => Task.FromException<Lingarr.Server.Modules.SubtitleSearch.Abstractions.SubtitleDownload>(
                SubtitleProviderException.ForInvalidResponse(
                    "fake", "the archive could not be read (corrupt central directory)."));
    }

    private static async Task EnableNotificationsAsync(
        SubtitleEndpointFixture fixture, string events, bool webhook = true, bool telegram = false)
    {
        await fixture.Settings.SetSetting(SettingKeys.Notifications.Enabled, "true");
        await fixture.Settings.SetSetting(SettingKeys.Notifications.Events, events);
        if (webhook)
        {
            await fixture.Settings.SetSetting(SettingKeys.Notifications.WebhookUrl, "http://hooks.test/lingarr");
        }

        if (telegram)
        {
            await fixture.Settings.SetSetting(SettingKeys.Notifications.TelegramChatId, "-1001234567890");
            await fixture.Settings.SetEncryptedSetting(SettingKeys.Notifications.TelegramBotToken, "12345:embed-test-token");
        }
    }

    [Fact]
    public async Task Download_Success_WithNotificationsOn_FiresDownloadedOnceThroughBothChannels()
    {
        using var fixture = new SubtitleEndpointFixture(new SrtProvider());
        await EnableNotificationsAsync(fixture, "downloaded", webhook: true, telegram: true);
        fixture.NotificationHandler.EnqueueStatus(HttpStatusCode.OK);
        fixture.NotificationHandler.EnqueueStatus(HttpStatusCode.OK);

        var outcome = await fixture.Pipeline.DownloadAsync(
            Candidate(), MediaType.Movie, fixture.MovieId, SubtitleDownloadSource.Manual,
            searchSessionId: null, CancellationToken.None);

        Assert.Equal(SubtitleDownloadResult.Succeeded, outcome.Result);
        Assert.Equal(2, fixture.NotificationHandler.Requests.Count);

        var webhook = fixture.NotificationHandler.Requests.Single(request =>
            request.RequestUri!.ToString() == "http://hooks.test/lingarr");
        using var json = JsonDocument.Parse(await webhook.Content!.ReadAsStringAsync());
        var root = json.RootElement;
        Assert.Equal("downloaded", root.GetProperty("event").GetString());
        // The fixture's seeded movie row is the production write side — the
        // title comes back resolved from it, not from the embed site.
        Assert.Equal("Movie", root.GetProperty("title").GetString());
        Assert.Equal("en", root.GetProperty("language").GetString());
        Assert.Equal("fake", root.GetProperty("provider").GetString());

        var telegram = fixture.NotificationHandler.Requests.Single(request =>
            request.RequestUri!.Host == "api.telegram.org");
        Assert.Equal(
            "https://api.telegram.org/bot12345:embed-test-token/sendMessage",
            telegram.RequestUri!.ToString());
    }

    [Fact]
    public async Task Download_Success_WithNotificationsDisabled_SendsNothing()
    {
        // The shipped-default shape: the fixture's settings never enable
        // notifications, so the embed is a settings read and nothing else.
        using var fixture = new SubtitleEndpointFixture(new SrtProvider());

        var outcome = await fixture.Pipeline.DownloadAsync(
            Candidate(), MediaType.Movie, fixture.MovieId, SubtitleDownloadSource.Manual,
            searchSessionId: null, CancellationToken.None);

        Assert.Equal(SubtitleDownloadResult.Succeeded, outcome.Result);
        Assert.Empty(fixture.NotificationHandler.Requests);
    }

    [Fact]
    public async Task Download_Success_WithTheEventFilteredOut_SendsNothing()
    {
        using var fixture = new SubtitleEndpointFixture(new SrtProvider());
        await EnableNotificationsAsync(fixture, "upgraded");

        var outcome = await fixture.Pipeline.DownloadAsync(
            Candidate(), MediaType.Movie, fixture.MovieId, SubtitleDownloadSource.Manual,
            searchSessionId: null, CancellationToken.None);

        Assert.Equal(SubtitleDownloadResult.Succeeded, outcome.Result);
        Assert.Empty(fixture.NotificationHandler.Requests);
    }

    [Fact]
    public async Task Download_UpgradeSourceLanding_StaysQuietTheJobOwnsTheUpgradedEvent()
    {
        // The no-double-notify rule: an upgrade round's landing carries
        // Source=Upgrade, so the pipeline never fires "downloaded" for it —
        // the upgrade job's deal point fires "upgraded" instead (pinned by
        // SubtitleUpgradeJobTests).
        using var fixture = new SubtitleEndpointFixture(new SrtProvider());
        await EnableNotificationsAsync(fixture, "downloaded,upgraded");

        var outcome = await fixture.Pipeline.DownloadAsync(
            Candidate(), MediaType.Movie, fixture.MovieId, SubtitleDownloadSource.Upgrade,
            searchSessionId: null, CancellationToken.None);

        Assert.Equal(SubtitleDownloadResult.Succeeded, outcome.Result);
        Assert.Empty(fixture.NotificationHandler.Requests);
    }

    [Fact]
    public async Task Download_Failure_WithDownloadFailedAllowed_FiresOnceWithTheReason()
    {
        using var fixture = new SubtitleEndpointFixture(new FailingProvider());
        await EnableNotificationsAsync(fixture, "download_failed");
        fixture.NotificationHandler.EnqueueStatus(HttpStatusCode.OK);

        var outcome = await fixture.Pipeline.DownloadAsync(
            Candidate(), MediaType.Movie, fixture.MovieId, SubtitleDownloadSource.Auto,
            searchSessionId: null, CancellationToken.None);

        Assert.Equal(SubtitleDownloadResult.Failed, outcome.Result);
        var webhook = Assert.Single(fixture.NotificationHandler.Requests);
        using var json = JsonDocument.Parse(await webhook.Content!.ReadAsStringAsync());
        Assert.Equal("download_failed", json.RootElement.GetProperty("event").GetString());
        Assert.Equal("failed", json.RootElement.GetProperty("result").GetString());
        Assert.Contains("corrupt central directory", json.RootElement.GetProperty("detail").GetString());
    }
}

/// <summary>
/// Startup seeding of the notification rows (B11), following the
/// StartupServiceTests shape: fresh databases get the five rows with the
/// off-by-default switch and the two-event default; stored rows are never
/// overwritten; the bot token row round-trips through the real encryption
/// service (the whisper/plex encrypted-row precedent).
/// </summary>
[Collection("StartupServiceEnv")]
public class NotificationSettingsSeedTests
{
    private static IServiceProvider BuildProvider()
    {
        // The database name must be captured OUTSIDE the options lambda —
        // inline it and every scope mints a fresh empty store (the
        // StartupServiceTests precedent hoists it for exactly this reason).
        var databaseName = Guid.NewGuid().ToString();
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddMemoryCache();
        services.AddScoped<ISettingService, StubSettingService>();
        services.AddScoped<IAuthService, AuthService>();
        services.AddDbContext<LingarrDbContext>(options =>
            options.UseInMemoryDatabase(databaseName));
        return services.BuildServiceProvider();
    }

    private static async Task<Setting?> FindRowAsync(IServiceProvider serviceProvider, string key)
    {
        await using var scope = serviceProvider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<LingarrDbContext>();
        return await dbContext.Settings.FindAsync(key);
    }

    [Fact]
    public async Task SeedNotificationSettings_FreshDatabase_SeedsOffSwitchAndDefaultEvents()
    {
        var serviceProvider = BuildProvider();
        var startupService = new StartupService(serviceProvider, NullLogger<StartupService>.Instance);

        await startupService.StartAsync(CancellationToken.None);

        Assert.Equal("false", (await FindRowAsync(
            serviceProvider, SettingKeys.Notifications.Enabled))!.Value);
        Assert.Equal(string.Empty, (await FindRowAsync(
            serviceProvider, SettingKeys.Notifications.WebhookUrl))!.Value);
        Assert.Equal(string.Empty, (await FindRowAsync(
            serviceProvider, SettingKeys.Notifications.TelegramBotToken))!.Value);
        Assert.Equal(string.Empty, (await FindRowAsync(
            serviceProvider, SettingKeys.Notifications.TelegramChatId))!.Value);
        Assert.Equal(SettingKeys.Notifications.DefaultEvents, (await FindRowAsync(
            serviceProvider, SettingKeys.Notifications.Events))!.Value);
        Assert.Null((await FindRowAsync(
            serviceProvider, SettingKeys.Notifications.Enabled))!.Provider);
    }

    [Fact]
    public async Task SeedNotificationSettings_ExistingRows_AreNeverOverwritten()
    {
        var serviceProvider = BuildProvider();
        await using (var seedScope = serviceProvider.CreateAsyncScope())
        {
            var seedDb = seedScope.ServiceProvider.GetRequiredService<LingarrDbContext>();
            seedDb.Settings.Add(new Setting
            {
                Key = SettingKeys.Notifications.Enabled,
                Value = "true"
            });
            seedDb.Settings.Add(new Setting
            {
                Key = SettingKeys.Notifications.Events,
                Value = "job_failed"
            });
            await seedDb.SaveChangesAsync();
        }
        var startupService = new StartupService(serviceProvider, NullLogger<StartupService>.Instance);

        await startupService.StartAsync(CancellationToken.None);

        Assert.Equal("true", (await FindRowAsync(
            serviceProvider, SettingKeys.Notifications.Enabled))!.Value);
        Assert.Equal("job_failed", (await FindRowAsync(
            serviceProvider, SettingKeys.Notifications.Events))!.Value);
    }

    [Fact]
    public async Task StartAsync_AfterTheSeed_TheBotTokenRoundTripsThroughRealEncryption()
    {
        var serviceProvider = BuildProvider();
        var startupService = new StartupService(serviceProvider, NullLogger<StartupService>.Instance);
        await startupService.StartAsync(CancellationToken.None);

        await using var scope = serviceProvider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<LingarrDbContext>();
        var settingService = new SettingService(
            dbContext,
            NullLogger<ISettingService>.Instance,
            new MemoryCache(new MemoryCacheOptions()),
            new SettingChangeNotifier(),
            new EncryptionService(new EphemeralDataProtectionProvider()));

        var saved = await settingService.SetEncryptedSetting(
            SettingKeys.Notifications.TelegramBotToken, "12345:seed-roundtrip");
        Assert.True(saved);
        Assert.Equal("12345:seed-roundtrip", await settingService.GetEncryptedSetting(
            SettingKeys.Notifications.TelegramBotToken));

        // At rest the row is ciphertext — never the plaintext token.
        var row = await dbContext.Settings.FindAsync(SettingKeys.Notifications.TelegramBotToken);
        Assert.NotNull(row);
        Assert.NotEqual("12345:seed-roundtrip", row.Value);
    }

    /// <summary>Minimal stand-in — the startup path only reads settings through the context.</summary>
    private sealed class StubSettingService : ISettingService
    {
        public event SettingChangedHandler? SettingChanged
        {
            add { }
            remove { }
        }

        public Task<string?> GetSetting(string key) => Task.FromResult<string?>(null);

        public Task<Dictionary<string, string>> GetSettings(IEnumerable<string> keys) =>
            Task.FromResult(new Dictionary<string, string>());

        public Task<bool> SetSetting(string key, string value) => Task.FromResult(true);

        public Task<bool> SetSettings(Dictionary<string, string> settings) => Task.FromResult(true);

        public Task<List<T>> GetSettingAsJson<T>(string key) where T : class =>
            Task.FromResult(new List<T>());

        public Task<bool> SetEncryptedSetting(string key, string value) => Task.FromResult(true);

        public Task<string?> GetEncryptedSetting(string key) => Task.FromResult<string?>(null);

        public Task<Dictionary<string, bool>> GetEncryptedSettingStates(IEnumerable<string> keys) =>
            Task.FromResult(new Dictionary<string, bool>());
    }
}
