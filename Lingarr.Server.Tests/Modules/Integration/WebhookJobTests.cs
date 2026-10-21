using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Lingarr.Core.Data;
using Lingarr.Core.Entities;
using Lingarr.Core.Enum;
using Lingarr.Core.Interfaces;
using Lingarr.Server.Models.Webhooks;
using Lingarr.Server.Modules.Integration;
using Lingarr.Server.Modules.Media;
using Lingarr.Server.Modules.Settings;
using Lingarr.Server.Modules.SubtitleSearch.Queue;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Lingarr.Server.Tests.Modules.Integration;

/// <summary>
/// Webhook boost contracts of QA3 P2-3/P2-4/P3-11 plus the Sonarr episode
/// parity (design ⑤): the subtitle-search master switch gates the boost
/// (rollback lever closed end-to-end), only Radarr/Sonarr Download events
/// boost the queue (Grab/Rename/Test just import; the Sonarr boost targets
/// exactly the episode the payload carried), a legacy payload without an
/// eventType keeps the old boost behaviour, and a boost failure never
/// fails the import job. Real SQLite because the boost is verified through
/// the actual queue upsert.
/// </summary>
public class WebhookJobTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly HookDbContext _dbContext;
    private readonly Mock<IMediaService> _mediaService = new();
    private readonly Mock<IMediaSubtitleProcessor> _processor = new();
    private readonly Mock<ISettingService> _settingService = new();
    private readonly Movie _movie;
    private readonly Episode _episode;
    private readonly WebhookJob _job;

    public WebhookJobTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        // Snake-case naming: the queue upsert addresses migration column
        // names in raw SQL, so the schema must match production naming.
        _dbContext = new HookDbContext(new DbContextOptionsBuilder<LingarrDbContext>()
            .UseSqlite(_connection)
            .UseSnakeCaseNamingConvention()
            .Options);
        _dbContext.Database.EnsureCreated();

        _movie = new Movie
        {
            RadarrId = 77,
            Title = "Boosted Movie",
            Path = "/media/boosted",
            FileName = "boosted.mkv",
            DateAdded = DateTimeOffset.UtcNow,
            NeedsTranslation = true,
            NoSubtitleNeeded = false
        };
        _dbContext.Movies.Add(_movie);

        var show = new Show
        {
            SonarrId = 900,
            Title = "Boosted Show",
            Path = "/media/show",
            DateAdded = DateTimeOffset.UtcNow
        };
        _episode = new Episode
        {
            SonarrId = 501,
            EpisodeNumber = 1,
            Title = "Boosted Episode",
            Path = "/media/show/season-1",
            FileName = "boosted.s01e01.mkv",
            DateAdded = DateTimeOffset.UtcNow,
            Season = new Season { SeasonNumber = 1, Show = show },
            NeedsTranslation = true,
            NoSubtitleNeeded = false
        };
        _dbContext.Episodes.Add(_episode);
        _dbContext.SaveChangesAsync().GetAwaiter().GetResult();

        _mediaService
            .Setup(service => service.GetMovieIdOrSyncFromRadarrMovieId(77))
            .ReturnsAsync(_movie.Id);
        _mediaService
            .Setup(service => service.GetEpisodeIdOrSyncFromSonarrEpisodeId(501))
            .ReturnsAsync(_episode.Id);
        _processor
            .Setup(processor => processor.ProcessMedia(It.IsAny<IMedia>(), It.IsAny<MediaType>()))
            .ReturnsAsync(true);

        var queue = new SubtitleSearchQueueService(
            _dbContext, _settingService.Object, NullLogger<SubtitleSearchQueueService>.Instance);
        _job = new WebhookJob(
            _dbContext,
            _mediaService.Object,
            _processor.Object,
            queue,
            _settingService.Object,
            NullLogger<WebhookJob>.Instance);
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    private sealed class HookDbContext(DbContextOptions options) : LingarrDbContext(options);

    private void WithSubtitleSearchEnabled(bool enabled)
        => _settingService
            .Setup(service => service.GetSettings(It.IsAny<IEnumerable<string>>()))
            .ReturnsAsync((IEnumerable<string> keys) =>
                keys.ToDictionary(key => key, key => enabled ? "true" : "false"));

    private static RadarrWebhookPayload Payload(string? eventType) => new()
    {
        EventType = eventType,
        Movie = new RadarrWebhookMovie { Id = 77, Title = "Boosted Movie" }
    };

    private static SonarrWebhookPayload SonarrPayload(string? eventType) => new()
    {
        EventType = eventType,
        Series = new SonarrWebhookSeries { Id = 900, Title = "Boosted Show" },
        Episodes = [new SonarrWebhookEpisode { Id = 501 }]
    };

    private async Task<SubtitleSearchState?> QueueRowAsync()
        => await _dbContext.SubtitleSearchStates
            .AsNoTracking()
            .FirstOrDefaultAsync(row => row.MediaType == MediaType.Movie && row.MediaId == _movie.Id);

    private async Task<SubtitleSearchState?> EpisodeQueueRowAsync()
        => await _dbContext.SubtitleSearchStates
            .AsNoTracking()
            .FirstOrDefaultAsync(row => row.MediaType == MediaType.Episode && row.MediaId == _episode.Id);

    [Fact]
    public async Task Radarr_DownloadEvent_WithSearchEnabled_BoostsTheQueue()
    {
        WithSubtitleSearchEnabled(true);

        await _job.ProcessRadarrWebhook(Payload("Download"));

        _processor.Verify(
            processor => processor.ProcessMedia(It.IsAny<IMedia>(), MediaType.Movie), Times.Once);
        var row = await QueueRowAsync();
        Assert.NotNull(row);
        Assert.Equal(SubtitleSearchStatus.Pending, row!.Status);
        Assert.Equal(SubtitlePriorityRank.WebhookBoost, row.PriorityRank);
        Assert.NotNull(row.WebhookBoostUntil);
        Assert.True(row.WebhookBoostUntil > DateTimeOffset.UtcNow.AddHours(47));
    }

    [Fact]
    public async Task Radarr_NonDownloadEvent_ImportsButNeverBoosts()
    {
        WithSubtitleSearchEnabled(true);

        await _job.ProcessRadarrWebhook(Payload("Grab"));
        await _job.ProcessRadarrWebhook(Payload("Test"));

        // Media import processing ran for both events — only the queue
        // side-effect is gated on the event type.
        _processor.Verify(
            processor => processor.ProcessMedia(It.IsAny<IMedia>(), MediaType.Movie), Times.Exactly(2));
        Assert.Null(await QueueRowAsync());
    }

    [Fact]
    public async Task Radarr_DisabledSearch_ImportsButNeverBoosts()
    {
        // QA3 P2-3: the master switch is the rollback lever — a webhook
        // must not re-arm the queue while the automated search is off.
        WithSubtitleSearchEnabled(false);

        await _job.ProcessRadarrWebhook(Payload("Download"));

        _processor.Verify(
            processor => processor.ProcessMedia(It.IsAny<IMedia>(), MediaType.Movie), Times.Once);
        Assert.Null(await QueueRowAsync());
    }

    [Fact]
    public async Task Radarr_LegacyPayloadWithoutEventType_StillBoosts()
    {
        // Senders that never populated eventType keep the pre-gate
        // behaviour: only an explicit non-Download value holds the boost.
        WithSubtitleSearchEnabled(true);

        await _job.ProcessRadarrWebhook(Payload(null));

        Assert.NotNull(await QueueRowAsync());
    }

    [Fact]
    public async Task Radarr_MediaNotNeedingTranslation_DoesNotBoost()
    {
        WithSubtitleSearchEnabled(true);
        _movie.NeedsTranslation = false;
        _dbContext.Movies.Update(_movie);
        await _dbContext.SaveChangesAsync();

        await _job.ProcessRadarrWebhook(Payload("Download"));

        Assert.Null(await QueueRowAsync());
    }

    [Fact]
    public async Task Radarr_BoostFailure_DoesNotFailTheImportJob()
    {
        // QA3 P3-11: the boost is a side-effect of import handling — a
        // queue-write failure logs and the job still succeeds (no rethrow,
        // no pointless Hangfire retry of an already-imported movie).
        WithSubtitleSearchEnabled(true);
        await _dbContext.Database.ExecuteSqlRawAsync("DROP TABLE subtitle_search_state");

        var exception = await Record.ExceptionAsync(() => _job.ProcessRadarrWebhook(Payload("Download")));

        Assert.Null(exception);
        _processor.Verify(
            processor => processor.ProcessMedia(It.IsAny<IMedia>(), MediaType.Movie), Times.Once);
    }

    // ── Sonarr: the same boost contracts on the episode path (design ⑤) ──

    [Fact]
    public async Task Sonarr_DownloadEvent_WithSearchEnabled_BoostsTheEpisode()
    {
        WithSubtitleSearchEnabled(true);

        await _job.ProcessSonarrWebhook(SonarrPayload("Download"));

        _processor.Verify(
            processor => processor.ProcessMedia(It.IsAny<IMedia>(), MediaType.Episode), Times.Once);
        var row = await EpisodeQueueRowAsync();
        Assert.NotNull(row);
        Assert.Equal(SubtitleSearchStatus.Pending, row!.Status);
        Assert.Equal(SubtitlePriorityRank.WebhookBoost, row.PriorityRank);
        Assert.NotNull(row.WebhookBoostUntil);
        Assert.True(row.WebhookBoostUntil > DateTimeOffset.UtcNow.AddHours(47));
        // the boost targets exactly this episode — the movie row is untouched
        Assert.Null(await QueueRowAsync());
    }

    [Fact]
    public async Task Sonarr_NonDownloadEvent_ImportsButNeverBoosts()
    {
        WithSubtitleSearchEnabled(true);

        await _job.ProcessSonarrWebhook(SonarrPayload("Grab"));
        await _job.ProcessSonarrWebhook(SonarrPayload("Test"));

        // Media import processing ran for both events — only the queue
        // side-effect is gated on the event type.
        _processor.Verify(
            processor => processor.ProcessMedia(It.IsAny<IMedia>(), MediaType.Episode), Times.Exactly(2));
        Assert.Null(await EpisodeQueueRowAsync());
    }

    [Fact]
    public async Task Sonarr_DisabledSearch_ImportsButNeverBoosts()
    {
        // The master switch gates the Sonarr boost exactly like the Radarr
        // one — with the automated search off, a webhook never re-arms the
        // queue.
        WithSubtitleSearchEnabled(false);

        await _job.ProcessSonarrWebhook(SonarrPayload("Download"));

        _processor.Verify(
            processor => processor.ProcessMedia(It.IsAny<IMedia>(), MediaType.Episode), Times.Once);
        Assert.Null(await EpisodeQueueRowAsync());
    }

    [Fact]
    public async Task Sonarr_LegacyPayloadWithoutEventType_StillBoosts()
    {
        // Same compatibility decision as Radarr: senders that never
        // populated eventType keep the pre-gate behaviour.
        WithSubtitleSearchEnabled(true);

        await _job.ProcessSonarrWebhook(SonarrPayload(null));

        Assert.NotNull(await EpisodeQueueRowAsync());
    }

    [Fact]
    public async Task Sonarr_EpisodeNotNeedingTranslation_DoesNotBoost()
    {
        WithSubtitleSearchEnabled(true);
        _episode.NeedsTranslation = false;
        _dbContext.Episodes.Update(_episode);
        await _dbContext.SaveChangesAsync();

        await _job.ProcessSonarrWebhook(SonarrPayload("Download"));

        _processor.Verify(
            processor => processor.ProcessMedia(It.IsAny<IMedia>(), MediaType.Episode), Times.Once);
        Assert.Null(await EpisodeQueueRowAsync());
    }
}
