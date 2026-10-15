using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Lingarr.Core.Data;
using Lingarr.Core.Entities;
using Lingarr.Core.Enum;
using Lingarr.Core.Interfaces;
using Lingarr.Server.Models;
using Lingarr.Server.Models.FileSystem;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;
using Lingarr.Server.Modules.Infrastructure;
using Lingarr.Server.Modules.Settings;
using Lingarr.Server.Modules.Media;
using Lingarr.Server.Modules.Subtitle;
using Lingarr.Server.Modules.Translation;

namespace Lingarr.Server.Tests.Jobs;

public class AutomatedTranslationJobTests
{
    [Fact]
    public async Task ProcessMovies_ScansPastInitialWindowWhenEarlyItemsTooNew()
    {
        var tempDirectory = Directory.CreateTempSubdirectory();
        try
        {
            var dbContext = BuildContext();
            await using var context = dbContext;

            var movies = await SeedMoviesAsync(context, tempDirectory.FullName);
            var processor = new RecordingMediaSubtitleProcessor();
            var job = CreateJob(context, processor);
            ConfigureJobForMovies(job);

            var result = await InvokeProcessMoviesAsync(job);

            Assert.True(result);
            Assert.Single(processor.ProcessedTitles);
            Assert.Equal(movies[^1].Title, processor.ProcessedTitles.Single());
        }
        finally
        {
            tempDirectory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task ProcessMovies_SelectsOnlyMediaNeedingTranslation_AndTouchesTheFileSystemOnlyForThem()
    {
        var tempDirectory = Directory.CreateTempSubdirectory();
        try
        {
            var context = BuildContext();
            await using var dbContext = context;

            // three equally eligible movies on disk, but only two carry the
            // needs_translation flag the statistics scan computed
            var needing = CreateMovie(11, "Needing One", tempDirectory.FullName, TimeSpan.FromHours(80));
            var alsoNeeding = CreateMovie(12, "Needing Two", tempDirectory.FullName, TimeSpan.FromHours(90));
            var notNeeding = CreateMovie(13, "Already Covered", tempDirectory.FullName, TimeSpan.FromHours(80));
            needing.NeedsTranslation = true;
            alsoNeeding.NeedsTranslation = true;
            notNeeding.NeedsTranslation = false;
            dbContext.Movies.AddRange(needing, alsoNeeding, notNeeding);
            await dbContext.SaveChangesAsync();

            // the real per-media processor behind a mocked subtitle service:
            // audit P0-5 asserts the per-media filesystem lookup happens for
            // the selected candidates only, never for the whole library
            var subtitleService = new Mock<ISubtitleService>();
            subtitleService
                .Setup(service => service.GetSubtitles(It.IsAny<string>(), It.IsAny<string>()))
                .ReturnsAsync(new List<Subtitles>());
            var processor = new MediaSubtitleProcessor(
                new Mock<ITranslationRequestService>().Object,
                NullLogger<IMediaSubtitleProcessor>.Instance,
                new NoOpSettingService(),
                subtitleService.Object,
                dbContext,
                new Mock<IEmbeddedTrackService>().Object);
            var job = CreateJob(dbContext, processor);
            ConfigureJobForMovies(job);

            var result = await InvokeProcessMoviesAsync(job);

            Assert.False(result); // no subtitles on disk → nothing initiated
            // the filesystem was consulted exactly once per selected candidate
            subtitleService.Verify(service => service.GetSubtitles(needing.Path!, needing.FileName!), Times.Once);
            subtitleService.Verify(service => service.GetSubtitles(alsoNeeding.Path!, alsoNeeding.FileName!), Times.Once);
            // media that needs no translation is never selected — not even loaded
            subtitleService.Verify(service => service.GetSubtitles(notNeeding.Path!, notNeeding.FileName!), Times.Never);
            subtitleService.Verify(service => service.GetSubtitles(It.IsAny<string>(), It.IsAny<string>()), Times.Exactly(2));
        }
        finally
        {
            tempDirectory.Delete(recursive: true);
        }
    }

    private static LingarrDbContext BuildContext()
    {
        var options = new DbContextOptionsBuilder<LingarrDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new LingarrDbContext(options);
    }

    private static async Task<List<Movie>> SeedMoviesAsync(LingarrDbContext context, string directory)
    {
        var movies = new List<Movie>
        {
            CreateMovie(1, "Too Fresh 1", directory, TimeSpan.FromHours(1)),
            CreateMovie(2, "Too Fresh 2", directory, TimeSpan.FromHours(2)),
            CreateMovie(3, "Eligible Movie", directory, TimeSpan.FromHours(80))
        };

        context.Movies.AddRange(movies);
        await context.SaveChangesAsync();
        return movies;
    }

    private static Movie CreateMovie(int id, string title, string directory, TimeSpan age)
    {
        var fileName = $"{title.Replace(' ', '_')}.mkv";
        var filePath = Path.Combine(directory, fileName);
        File.WriteAllText(filePath, "stub");
        File.SetLastWriteTimeUtc(filePath, DateTime.UtcNow - age);

        return new Movie
        {
            Id = id,
            RadarrId = id,
            Title = title,
            FileName = fileName,
            Path = filePath,
            DateAdded = DateTime.UtcNow - age,
            IncludeInTranslation = true,
            // SQL selection (audit P0-5): every seeded movie is a candidate;
            // the age threshold still does the winnowing inside the rotation
            NeedsTranslation = true
        };
    }

    private static AutomatedTranslationJob CreateJob(
        LingarrDbContext context,
        IMediaSubtitleProcessor processor)
    {
        return new AutomatedTranslationJob(
            context,
            NullLogger<AutomatedTranslationJob>.Instance,
            processor,
            new NoOpScheduleService(),
            new NoOpSettingService());
    }

    private static void ConfigureJobForMovies(AutomatedTranslationJob job)
    {
        SetPrivateField(job, "_maxTranslationsPerRun", 1);
        SetPrivateField(job, "_defaultMovieAgeThreshold", TimeSpan.FromHours(48));
        SetPrivateField(job, "_defaultShowAgeThreshold", TimeSpan.FromHours(48));
    }

    private static async Task<bool> InvokeProcessMoviesAsync(AutomatedTranslationJob job)
    {
        var method = typeof(AutomatedTranslationJob)
            .GetMethod("ProcessMovies", BindingFlags.NonPublic | BindingFlags.Instance);
        if (method == null)
        {
            throw new InvalidOperationException("ProcessMovies method not found via reflection.");
        }

        var resultTask = (Task<int>)method.Invoke(job, new object[] { 1 })!;
        var result = await resultTask.ConfigureAwait(false);
        return result > 0;
    }

    private static void SetPrivateField<T>(AutomatedTranslationJob job, string fieldName, T value)
    {
        var field = typeof(AutomatedTranslationJob)
            .GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance);
        if (field == null)
        {
            throw new InvalidOperationException($"Field '{fieldName}' not found on AutomatedTranslationJob.");
        }

        field.SetValue(job, value);
    }

    private sealed class RecordingMediaSubtitleProcessor : IMediaSubtitleProcessor
    {
        public List<string> ProcessedTitles { get; } = new();

        public Task<bool> ProcessMedia(IMedia media, MediaType mediaType)
        {
            ProcessedTitles.Add(media.Title);
            return Task.FromResult(true);
        }
    }

    private sealed class NoOpScheduleService : IScheduleService
    {
        public Task Initialize() => Task.CompletedTask;
        public Task<List<RecurringJobStatus>> GetRecurringJobs() =>
            Task.FromResult<List<RecurringJobStatus>>([]);
        public List<QueueDepthStatus> GetQueueDepths() => [];
        public Task<bool> SetJobEnabled(string jobId, bool enabled) => Task.FromResult(false);
        public Task<UpdateCronResult> UpdateJobCron(string jobId, string cron) =>
            Task.FromResult(UpdateCronResult.NotFound);
        public string GetJobState(string jobId) => string.Empty;
        public Task UpdateJobState(string jobId, string state) => Task.CompletedTask;
    }

    private sealed class NoOpSettingService : ISettingService
    {
        public event SettingChangedHandler? SettingChanged
        {
            add { }
            remove { }
        }

        public Task<string?> GetSetting(string key) => Task.FromResult<string?>(null);

        public Task<Dictionary<string, string>> GetSettings(IEnumerable<string> keys)
        {
            var dictionary = keys.ToDictionary(static key => key, static _ => string.Empty);
            return Task.FromResult(dictionary);
        }

        public Task<bool> SetSetting(string key, string value) => Task.FromResult(true);

        public Task<bool> SetSettings(Dictionary<string, string> settings) => Task.FromResult(true);

        public Task<List<T>> GetSettingAsJson<T>(string key) where T : class => Task.FromResult(new List<T>());
        public Task<bool> SetEncryptedSetting(string key, string value)=> Task.FromResult(true);

        public Task<string?> GetEncryptedSetting(string key) => Task.FromResult<string?>(null);

        public Task<Dictionary<string, bool>> GetEncryptedSettingStates(IEnumerable<string> keys) => Task.FromResult(new Dictionary<string, bool>());
    }
}
