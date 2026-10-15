using System;
using System.Net;
using System.Threading.Tasks;
using System.Collections.Generic;
using Lingarr.Core.Configuration;
using Lingarr.Core.Data;
using System.Net.Http;
using Lingarr.Server.Modules.Integration;
using Lingarr.Server.Modules.Sync;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;
using Lingarr.Server.Modules.Settings;
using Lingarr.Server.Modules.Media;
using Lingarr.Server.Modules.Subtitle;

namespace Lingarr.Server.Tests.Services;

public class MediaServiceTests
{
    [Fact]
    public async Task GetEpisodeIdOrSyncFromSonarrEpisodeId_WhenEpisodeNotFound_ReturnsZeroWithoutFullResync()
    {
        // Arrange
        var options = new DbContextOptionsBuilder<LingarrDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var context = new LingarrDbContext(options);
        await context.SaveChangesAsync();

        var sonarrMock = new Mock<ISonarrService>();
        var radarrMock = new Mock<IRadarrService>();
        var movieSyncMock = new Mock<IMovieSyncService>();
        var subtitleMock = new Mock<ISubtitleService>();
        var showSyncServiceMock = new Mock<IShowSyncService>();
        var settingMock = new Mock<ISettingService>();
        settingMock
            .Setup(s => s.GetSetting(SettingKeys.Integration.SonarrDefaultInclude))
            .ReturnsAsync("true");

        // Sonarr answers 404 for the stale episode id.
        sonarrMock
            .Setup(s => s.GetEpisode(It.IsAny<int>()))
            .ThrowsAsync(new HttpRequestException("Not found", null, HttpStatusCode.NotFound));

        var mediaService = new MediaService(context,
            subtitleMock.Object,
            sonarrMock.Object,
            showSyncServiceMock.Object,
            radarrMock.Object,
            movieSyncMock.Object,
            settingMock.Object,
            NullLogger<MediaService>.Instance);

        // Act
        var result = await mediaService.GetEpisodeIdOrSyncFromSonarrEpisodeId(150);

        // Assert: the webhook fallback must no longer pull the whole library and
        // re-sync every show — the scheduled show sync reconciles removals instead.
        sonarrMock.Verify(s => s.GetEpisode(150), Times.Once);
        sonarrMock.Verify(s => s.GetShows(), Times.Never);
        showSyncServiceMock.Verify(
            s => s.SyncShows(It.IsAny<List<Lingarr.Server.Models.Integrations.SonarrShow>>(), It.IsAny<bool>()),
            Times.Never);
        Assert.Equal(0, result);
    }
}
