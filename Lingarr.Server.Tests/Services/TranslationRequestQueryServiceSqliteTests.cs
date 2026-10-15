using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Lingarr.Core.Data;
using Lingarr.Core.Entities;
using Lingarr.Core.Enum;
using Lingarr.Server.Hubs;
using Lingarr.Server.Modules.Subtitle;
using Lingarr.Server.Modules.Translation;
using Microsoft.Data.Sqlite;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Moq;
using Xunit;

namespace Lingarr.Server.Tests.Services;

/// <summary>
/// SQL-translation coverage for the parent-media filter of the translation
/// request list (scale audit P1-6): the show-scoped episode rollup must stay
/// in SQL as a correlated EXISTS over episodes→seasons→shows — never a
/// mediaIds IN(...) list built from a thousand episode ids. Behavioural
/// assertions cover the legacy MediaType.Show convention on episode requests
/// (the library aggregation reads it) alongside the MediaType.Episode rows
/// request creation writes, unknown parent ids, and movie rows with colliding
/// media ids.
/// </summary>
public class TranslationRequestQueryServiceSqliteTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly LingarrDbContext _dbContext;
    private readonly List<string> _sqlLog = [];
    private int _nextRequestId = 1;

    public TranslationRequestQueryServiceSqliteTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _dbContext = new LingarrDbContext(new DbContextOptionsBuilder<LingarrDbContext>()
            .UseSqlite(_connection)
            .AddInterceptors(new RecordingInterceptor(_sqlLog))
            .Options);
        _dbContext.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        _connection.Dispose();
    }

    private sealed class RecordingInterceptor(List<string> log) : DbCommandInterceptor
    {
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            log.Add(command.CommandText);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    private TranslationRequestQueryService CreateService() => new(
        _dbContext,
        new Mock<IHubContext<TranslationRequestsHub>>().Object,
        new Mock<ITranslationRequestEventService>().Object,
        new Mock<ISubtitleService>().Object);

    private async Task<Show> SeedShowAsync(string title, int[] episodesPerSeason, int explicitId)
    {
        var show = new Show
        {
            Id = explicitId,
            SonarrId = explicitId,
            Title = title,
            Path = "/tv/" + title,
            DateAdded = DateTimeOffset.UtcNow
        };
        for (var seasonNumber = 1; seasonNumber <= episodesPerSeason.Length; seasonNumber++)
        {
            var season = new Season
            {
                SeasonNumber = seasonNumber,
                Path = $"/tv/{title}/S{seasonNumber:00}",
                Show = show
            };
            for (var i = 1; i <= episodesPerSeason[seasonNumber - 1]; i++)
            {
                season.Episodes.Add(new Episode
                {
                    SonarrId = i,
                    EpisodeNumber = i,
                    Title = $"S{seasonNumber}E{i}",
                    FileName = $"{title}.S{seasonNumber:00}E{i:00}.mkv",
                    Season = season
                });
            }
            show.Seasons.Add(season);
        }
        _dbContext.Shows.Add(show);
        await _dbContext.SaveChangesAsync();
        return show;
    }

    private async Task SeedMovieAsync(int explicitId)
    {
        _dbContext.Movies.Add(new Movie
        {
            Id = explicitId,
            RadarrId = explicitId,
            Title = "Movie " + explicitId,
            FileName = $"movie{explicitId}.mkv",
            Path = "/movies/movie" + explicitId,
            DateAdded = DateTimeOffset.UtcNow
        });
        await _dbContext.SaveChangesAsync();
    }

    private async Task SeedRequestAsync(
        MediaType mediaType,
        int mediaId,
        TranslationStatus status,
        DateTimeOffset? createdAt = null)
    {
        _dbContext.TranslationRequests.Add(new TranslationRequest
        {
            Title = $"req {_nextRequestId}",
            SourceLanguage = "en",
            TargetLanguage = "zh",
            MediaType = mediaType,
            MediaId = mediaId,
            Status = status,
            CreatedAt = createdAt ?? DateTimeOffset.UtcNow.AddMinutes(-_nextRequestId)
        });
        _nextRequestId++;
        await _dbContext.SaveChangesAsync();
    }

    private static List<Episode> EpisodesOf(Show show) =>
        show.Seasons.SelectMany(season => season.Episodes).ToList();

    [Fact]
    public async Task ParentFilter_ReturnsOnlyThatShowsEpisodeRequests_PushedDownToSql()
    {
        var alpha = await SeedShowAsync("Alpha", [2, 1], explicitId: 50);
        var alphaEpisodes = EpisodesOf(alpha);
        var beta = await SeedShowAsync("Beta", [1], explicitId: 51);
        var betaEpisode = EpisodesOf(beta)[0];

        // both episode-request conventions: request creation writes
        // MediaType.Episode, the legacy library aggregation reads MediaType.Show
        await SeedRequestAsync(MediaType.Episode, alphaEpisodes[0].Id, TranslationStatus.Completed);
        await SeedRequestAsync(MediaType.Show, alphaEpisodes[1].Id, TranslationStatus.Failed);
        await SeedRequestAsync(MediaType.Episode, alphaEpisodes[2].Id, TranslationStatus.Pending);
        await SeedRequestAsync(MediaType.Episode, betaEpisode.Id, TranslationStatus.Completed);

        _sqlLog.Clear();
        var result = await CreateService().GetTranslationRequests(
            null, "CreatedAt", false, 1, 20, null, null, mediaParentId: alpha.Id);

        // only Alpha's episode requests, across seasons and both conventions
        Assert.Equal(3, result.TotalCount);
        Assert.Equal(
            alphaEpisodes.Select(e => e.Id).OrderBy(id => id).ToList(),
            result.Items.Select(r => r.MediaId!.Value).OrderBy(id => id).ToList());

        // the filter ran inside SQL: a correlated EXISTS over episodes joined to
        // seasons — and no mediaIds IN(...) parameter list exists at all. Both
        // the count and the page query carry the pushed-down predicate.
        var requestsQueries = _sqlLog
            .Where(sql => sql.Contains("TranslationRequests", StringComparison.OrdinalIgnoreCase)
                          && sql.Contains("SELECT", StringComparison.OrdinalIgnoreCase))
            .ToList();
        Assert.NotEmpty(requestsQueries);
        Assert.All(requestsQueries, sql =>
        {
            Assert.Contains("EXISTS", sql, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Episodes", sql, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Seasons", sql, StringComparison.OrdinalIgnoreCase);
            // the mediaIds parameter list is gone; the MediaType gate itself
            // legitimately compiles to a small IN (...) of enum values
            Assert.DoesNotContain("MediaId\" IN (", sql, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public async Task ParentFilter_UnknownShowId_YieldsEmptyPage()
    {
        var show = await SeedShowAsync("Alpha", [1], explicitId: 60);
        await SeedRequestAsync(MediaType.Episode, EpisodesOf(show)[0].Id, TranslationStatus.Completed);

        var result = await CreateService().GetTranslationRequests(
            null, "CreatedAt", false, 1, 20, null, null, mediaParentId: 99999);

        Assert.Equal(0, result.TotalCount);
        Assert.Empty(result.Items);
    }

    [Fact]
    public async Task ParentFilter_ExcludesMovieRequestsWithCollidingMediaIds()
    {
        var show = await SeedShowAsync("Alpha", [1], explicitId: 70);
        var episode = EpisodesOf(show)[0];

        // a movie whose id collides with the episode's id: its request must
        // not leak into the show's history (the media type gates the join)
        await SeedMovieAsync(episode.Id);
        await SeedRequestAsync(MediaType.Movie, episode.Id, TranslationStatus.Completed);
        await SeedRequestAsync(MediaType.Episode, episode.Id, TranslationStatus.InProgress);

        var result = await CreateService().GetTranslationRequests(
            null, "CreatedAt", false, 1, 20, null, null, mediaParentId: show.Id);

        var item = Assert.Single(result.Items);
        Assert.Equal(MediaType.Episode, item.MediaType);
        Assert.Equal(episode.Id, item.MediaId);
    }

    [Fact]
    public async Task ParentFilter_CombinesWithStatusFilter()
    {
        var show = await SeedShowAsync("Alpha", [2], explicitId: 80);
        var episodes = EpisodesOf(show);
        await SeedRequestAsync(MediaType.Episode, episodes[0].Id, TranslationStatus.Completed);
        await SeedRequestAsync(MediaType.Episode, episodes[1].Id, TranslationStatus.Failed);

        var result = await CreateService().GetTranslationRequests(
            null, "CreatedAt", false, 1, 20, TranslationStatus.Failed, null, mediaParentId: show.Id);

        var item = Assert.Single(result.Items);
        Assert.Equal(TranslationStatus.Failed, item.Status);
        Assert.Equal(episodes[1].Id, item.MediaId);
    }
}
