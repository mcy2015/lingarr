using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Lingarr.Server.Models.Integrations;
using Lingarr.Server.Modules.Infrastructure;
using Lingarr.Server.Modules.Integration;
using Lingarr.Server.Modules.Settings;
using Lingarr.Server.Modules.Sync;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Lingarr.Server.Tests.Modules.Sync;

/// <summary>
/// The sync jobs no longer wrap the whole run in a database transaction; these
/// tests pin the job orchestration: stream into the sync service, orphan removal
/// with the observed ids, and job-state transitions on success and failure.
/// </summary>
public class SyncJobTests
{
    private static async IAsyncEnumerable<RadarrMovie> MovieStream(params int[] ids)
    {
        foreach (var id in ids)
        {
            await Task.Yield();
            yield return new RadarrMovie
            {
                Id = id,
                Title = $"Movie {id}",
                Path = $"/movies/Movie {id}",
                RootFolderPath = "/movies",
                Added = "2026-01-01T00:00:00Z",
                HasFile = true
            };
        }
    }

    private static Mock<ISettingService> SettingsMockReturning(bool includeByDefault)
    {
        var settings = new Mock<ISettingService>();
        settings
            .Setup(s => s.GetSetting(It.IsAny<string>()))
            .ReturnsAsync(includeByDefault ? "true" : "false");
        return settings;
    }

    [Fact]
    public async Task SyncMovieJob_StreamsMoviesAndRemovesOrphansWithObservedIds()
    {
        var radarr = new Mock<IRadarrService>();
        radarr.Setup(r => r.GetMovies()).Returns(MovieStream(1, 2, 3));

        var movieSync = new Mock<IMovieSyncService>();
        movieSync
            .Setup(s => s.SyncMovies(It.IsAny<IAsyncEnumerable<RadarrMovie>>(), true))
            .ReturnsAsync(new List<int> { 1, 2, 3 });

        var schedule = new Mock<IScheduleService>();
        var job = new SyncMovieJob(
            radarr.Object,
            NullLogger<SyncMovieJob>.Instance,
            schedule.Object,
            movieSync.Object,
            SettingsMockReturning(true).Object);

        await job.Execute();

        movieSync.Verify(
            s => s.SyncMovies(It.IsAny<IAsyncEnumerable<RadarrMovie>>(), true),
            Times.Once);
        movieSync.Verify(
            s => s.RemoveNonExistentMovies(
                It.Is<IEnumerable<int>>(ids => ids.SequenceEqual(new[] { 1, 2, 3 }))),
            Times.Once);
        schedule.Verify(s => s.UpdateJobState(It.IsAny<string>(), "Processing"), Times.Once);
        schedule.Verify(s => s.UpdateJobState(It.IsAny<string>(), "Succeeded"), Times.Once);
    }

    [Fact]
    public async Task SyncMovieJob_MidRunFailure_MarksJobFailedWithoutThrowing()
    {
        var radarr = new Mock<IRadarrService>();
        radarr.Setup(r => r.GetMovies()).Returns(MovieStream(1, 2, 3));

        var movieSync = new Mock<IMovieSyncService>();
        movieSync
            .Setup(s => s.SyncMovies(It.IsAny<IAsyncEnumerable<RadarrMovie>>(), It.IsAny<bool>()))
            .ThrowsAsync(new InvalidOperationException("partial sync aborted mid-run"));

        var schedule = new Mock<IScheduleService>();
        var job = new SyncMovieJob(
            radarr.Object,
            NullLogger<SyncMovieJob>.Instance,
            schedule.Object,
            movieSync.Object,
            SettingsMockReturning(false).Object);

        // The job swallows the exception (Hangfire AutomaticRetry(0)); committed
        // batches inside the service remain in the database.
        await job.Execute();

        schedule.Verify(s => s.UpdateJobState(It.IsAny<string>(), "Failed"), Times.Once);
        movieSync.Verify(s => s.RemoveNonExistentMovies(It.IsAny<IEnumerable<int>>()), Times.Never);
    }

    [Fact]
    public async Task SyncShowJob_SyncsShowsAndRemovesOrphansWithObservedIds()
    {
        var shows = new List<SonarrShow>
        {
            new()
            {
                Id = 1,
                Title = "Show 1",
                Path = "/tv/Show 1",
                Added = "2026-01-01T00:00:00Z",
                SeasonFolder = true
            },
            new()
            {
                Id = 2,
                Title = "Show 2",
                Path = "/tv/Show 2",
                Added = "2026-01-01T00:00:00Z",
                SeasonFolder = true
            }
        };
        var sonarr = new Mock<ISonarrService>();
        sonarr.Setup(s => s.GetShows()).ReturnsAsync(shows);

        var showSync = new Mock<IShowSyncService>();
        var schedule = new Mock<IScheduleService>();
        var job = new SyncShowJob(
            sonarr.Object,
            NullLogger<SyncShowJob>.Instance,
            schedule.Object,
            showSync.Object,
            SettingsMockReturning(true).Object);

        await job.Execute();

        showSync.Verify(s => s.SyncShows(shows, true), Times.Once);
        showSync.Verify(
            s => s.RemoveNonExistentShows(It.Is<HashSet<int>>(set => set.SetEquals(new[] { 1, 2 }))),
            Times.Once);
        schedule.Verify(s => s.UpdateJobState(It.IsAny<string>(), "Succeeded"), Times.Once);
    }

    [Fact]
    public async Task SyncShowJob_MidRunFailure_MarksJobFailedWithoutThrowing()
    {
        var sonarr = new Mock<ISonarrService>();
        sonarr.Setup(s => s.GetShows()).ReturnsAsync(new List<SonarrShow>());

        var showSync = new Mock<IShowSyncService>();
        showSync
            .Setup(s => s.SyncShows(It.IsAny<List<SonarrShow>>(), It.IsAny<bool>()))
            .ThrowsAsync(new InvalidOperationException("partial sync aborted mid-run"));

        var schedule = new Mock<IScheduleService>();
        var job = new SyncShowJob(
            sonarr.Object,
            NullLogger<SyncShowJob>.Instance,
            schedule.Object,
            showSync.Object,
            SettingsMockReturning(false).Object);

        await job.Execute();

        schedule.Verify(s => s.UpdateJobState(It.IsAny<string>(), "Failed"), Times.Once);
        showSync.Verify(s => s.RemoveNonExistentShows(It.IsAny<HashSet<int>>()), Times.Never);
    }
}
