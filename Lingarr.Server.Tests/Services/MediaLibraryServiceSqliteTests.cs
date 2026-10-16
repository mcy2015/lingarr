using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Lingarr.Core.Configuration;
using Lingarr.Core.Data;
using Lingarr.Core.Entities;
using Lingarr.Core.Enum;
using Lingarr.Server.Models;
using Lingarr.Server.Models.Api;
using Lingarr.Server.Modules.Media;
using Lingarr.Server.Modules.Settings;
using Lingarr.Server.Modules.Translation;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Lingarr.Server.Tests.Services;

/// <summary>
/// SQL-translation coverage for the pushed-down library aggregation
/// (scale audit P0-7/P0-8): InMemory never validates translatability, so the
/// aggregate queries (projection + anti-join "newest request per media" +
/// GROUP BY rollups + paging inputs) run against a real SQLite schema here.
/// Behavioural parity with the previous in-memory aggregation is asserted on
/// the same fixtures: state precedence, latest-request-wins, tie-breaking,
/// filters, ordering and paging boundaries, plus the single-media direct
/// path behind /media/library?id=. The audio-aware states (PRD card A) get
/// their own SQL-pushdown assertions via the recorded command log.
/// </summary>
public class MediaLibraryServiceSqliteTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly LingarrDbContext _dbContext;
    private readonly Mock<ISettingService> _settingService = new();
    private readonly Mock<ICostService> _costService = new();
    private readonly List<string> _sqlLog = [];
    private int _nextExternalId = 100;

    public MediaLibraryServiceSqliteTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _dbContext = new LingarrDbContext(new DbContextOptionsBuilder<LingarrDbContext>()
            .UseSqlite(_connection)
            .AddInterceptors(new RecordingInterceptor(_sqlLog))
            .Options);
        _dbContext.Database.EnsureCreated();

        _settingService
            .Setup(s => s.GetSettingAsJson<TargetLanguage>(It.IsAny<string>()))
            .ReturnsAsync(new List<TargetLanguage> { new() { Name = "Chinese", Code = "zh" } });
        _costService
            .Setup(c => c.GetRates())
            .ReturnsAsync(new Dictionary<string, decimal> { ["libretranslate"] = 5.0m });
        _settingService
            .Setup(s => s.GetSetting(It.IsAny<string>()))
            .ReturnsAsync("libretranslate");
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

    public void Dispose()
    {
        _dbContext.Dispose();
        _connection.Dispose();
    }

    private MediaLibraryService CreateService() =>
        new(_dbContext, _settingService.Object, _costService.Object,
            NullLogger<MediaLibraryService>.Instance);

    private async Task<Show> SeedShowAsync(
        string title,
        int[] episodesPerSeason,
        Dictionary<string, MediaLanguageCoverage>? coverage = null,
        int? explicitId = null)
    {
        var show = new Show
        {
            SonarrId = _nextExternalId++,
            Title = title,
            Path = "/tv/" + title,
            DateAdded = DateTimeOffset.UtcNow,
            SubtitleCoverage = coverage
        };
        if (explicitId is not null)
        {
            show.Id = explicitId.Value;
        }
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

    private async Task<Movie> SeedMovieAsync(
        string title,
        Dictionary<string, MediaLanguageCoverage>? coverage = null,
        int? explicitId = null,
        string[]? audioLanguages = null,
        bool? audioOverride = null,
        bool needsTranslation = false,
        bool noSubtitleNeeded = false)
    {
        var movie = new Movie
        {
            RadarrId = _nextExternalId++,
            Title = title,
            FileName = title + ".mkv",
            Path = "/movies/" + title,
            DateAdded = DateTimeOffset.UtcNow,
            SubtitleCoverage = coverage,
            AudioLanguages = audioLanguages,
            AudioOverride = audioOverride,
            NeedsTranslation = needsTranslation,
            NoSubtitleNeeded = noSubtitleNeeded
        };
        if (explicitId is not null)
        {
            movie.Id = explicitId.Value;
        }
        _dbContext.Movies.Add(movie);
        await _dbContext.SaveChangesAsync();
        return movie;
    }

    private async Task SeedRequestAsync(
        MediaType mediaType,
        int mediaId,
        TranslationStatus status,
        DateTimeOffset? updatedAt = null)
    {
        var request = new TranslationRequest
        {
            Title = "req",
            SourceLanguage = "en",
            TargetLanguage = "zh",
            MediaType = mediaType,
            MediaId = mediaId,
            Status = status,
            UpdatedAt = updatedAt ?? DateTimeOffset.UtcNow
        };
        _dbContext.TranslationRequests.Add(request);
        await _dbContext.SaveChangesAsync();
        if (updatedAt is not null)
        {
            // SaveChanges' UpdateTimestamps overwrites UpdatedAt on Added rows,
            // so the intended timestamp is applied set-based, bypassing the
            // change tracker.
            await _dbContext.TranslationRequests
                .Where(r => r.Id == request.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.UpdatedAt, updatedAt.Value));
        }
    }

    private static List<Episode> EpisodesOf(Show show) =>
        show.Seasons.SelectMany(season => season.Episodes).ToList();

    [Fact]
    public async Task AggregatesAcrossSeasonsAndMedia_OrdersAndPagesInSql()
    {
        // Alpha: 2 seasons, 3 episodes, newest per episode = 2 completed + 1 in progress.
        var alpha = await SeedShowAsync("Alpha", [2, 1]);
        var alphaEpisodes = EpisodesOf(alpha);
        var alphaNewest = DateTimeOffset.UtcNow.AddMinutes(5);
        await SeedRequestAsync(MediaType.Show, alphaEpisodes[0].Id, TranslationStatus.Completed, DateTimeOffset.UtcNow.AddDays(-3));
        await SeedRequestAsync(MediaType.Show, alphaEpisodes[1].Id, TranslationStatus.Completed, DateTimeOffset.UtcNow.AddDays(-2));
        await SeedRequestAsync(MediaType.Show, alphaEpisodes[2].Id, TranslationStatus.InProgress, alphaNewest);

        // Beta: ep1 latest Failed (after an older Completed), ep2 Completed.
        var beta = await SeedShowAsync("Beta", [2]);
        var betaEpisodes = EpisodesOf(beta);
        await SeedRequestAsync(MediaType.Show, betaEpisodes[0].Id, TranslationStatus.Completed, DateTimeOffset.UtcNow.AddDays(-5));
        await SeedRequestAsync(MediaType.Show, betaEpisodes[0].Id, TranslationStatus.Failed, DateTimeOffset.UtcNow.AddDays(-1));
        await SeedRequestAsync(MediaType.Show, betaEpisodes[1].Id, TranslationStatus.Completed, DateTimeOffset.UtcNow.AddDays(-4));

        var caper = await SeedMovieAsync("Caper");
        await SeedRequestAsync(MediaType.Movie, caper.Id, TranslationStatus.Pending);
        await SeedMovieAsync("Dormant");

        var service = CreateService();

        // page 1 of 2 over the title-ordered mixed list (Alpha, Beta, Caper, Dormant)
        var page1 = await service.GetLibrary(null, null, null, null, "Title", true, 1, 2);
        Assert.Equal(4, page1.TotalCount);
        Assert.Equal(["Alpha", "Beta"], page1.Items.Select(i => i.Title).ToArray());

        var alphaItem = page1.Items.Single(i => i.Title == "Alpha");
        Assert.Equal(MediaType.Show, alphaItem.MediaType);
        Assert.Equal(2, alphaItem.SeasonCount);
        Assert.Equal(3, alphaItem.EpisodeCount);
        Assert.Equal(MediaTranslationState.InProgress, alphaItem.State);
        Assert.Equal(2, alphaItem.CompletedCount);
        Assert.Equal(0, alphaItem.FailedCount);
        Assert.Equal(alphaNewest, alphaItem.LastActivityAt);
        // the inline-detail show graph hydrates for the page's shows only
        Assert.NotNull(alphaItem.Show);
        Assert.Equal(2, alphaItem.Show.Seasons.Count);

        var betaItem = page1.Items.Single(i => i.Title == "Beta");
        Assert.Equal(MediaTranslationState.Failed, betaItem.State);
        Assert.Equal(1, betaItem.CompletedCount);
        Assert.Equal(1, betaItem.FailedCount);
        Assert.NotNull(betaItem.Show);

        // page 2: movies only — no Show graph attached
        var page2 = await service.GetLibrary(null, null, null, null, "Title", true, 2, 2);
        Assert.Equal(4, page2.TotalCount);
        Assert.Equal(["Caper", "Dormant"], page2.Items.Select(i => i.Title).ToArray());
        var page2Items = page2.Items.ToArray();
        Assert.Equal(MediaTranslationState.Queued, page2Items[0].State);
        Assert.Equal(MediaTranslationState.None, page2Items[1].State);
        Assert.Equal(1, page2Items[0].EpisodeCount);
        Assert.All(page2.Items, item => Assert.Null(item.Show));

        // one past the end: empty page, truthful totals
        var page3 = await service.GetLibrary(null, null, null, null, "Title", true, 3, 2);
        Assert.Equal(4, page3.TotalCount);
        Assert.Empty(page3.Items);
    }

    [Fact]
    public async Task SearchFilterPushesDownAcrossMediaTypes()
    {
        await SeedShowAsync("Apple", [1]);
        await SeedShowAsync("Banana", [1]);
        await SeedMovieAsync("Apricot");

        var result = await CreateService().GetLibrary(null, "ap", null, null, "Title", true, 1, 20);

        Assert.Equal(2, result.TotalCount);
        Assert.Equal(["Apple", "Apricot"], result.Items.Select(i => i.Title).ToArray());
    }

    [Fact]
    public async Task NewestRequestPerEpisodeWins_EqualTimestampsBreakById()
    {
        var show = await SeedShowAsync("Ties", [2]);
        var episodes = EpisodesOf(show);
        var tie = DateTimeOffset.UtcNow.AddDays(-1);

        // ep1: three generations — only the newest counts.
        await SeedRequestAsync(MediaType.Show, episodes[0].Id, TranslationStatus.Pending, tie.AddDays(-3));
        await SeedRequestAsync(MediaType.Show, episodes[0].Id, TranslationStatus.Failed, tie.AddDays(-2));
        await SeedRequestAsync(MediaType.Show, episodes[0].Id, TranslationStatus.Completed, tie.AddDays(-1));

        // ep2: identical UpdatedAt — the higher Id (seeded later) is the keeper.
        await SeedRequestAsync(MediaType.Show, episodes[1].Id, TranslationStatus.Failed, tie);
        await SeedRequestAsync(MediaType.Show, episodes[1].Id, TranslationStatus.Completed, tie);

        var item = (await CreateService().GetLibrary(MediaType.Show, null, null, null, "Title", true, 1, 20))
            .Items.Single();

        Assert.Equal(MediaTranslationState.Completed, item.State);
        Assert.Equal(2, item.CompletedCount);
        Assert.Equal(0, item.FailedCount);
        Assert.Equal(tie, item.LastActivityAt);
    }

    [Fact]
    public async Task EpisodeConventionRequests_CountIntoTheShowRollup()
    {
        // request creation writes episode requests as MediaType.Episode
        // (AutomatedTranslationJob → MediaSubtitleProcessor); the legacy
        // convention is MediaType.Show. Both must roll up into the show's
        // state column — before the fix only Show-typed rows counted.
        var show = await SeedShowAsync("Both Conventions", [2]);
        var episodes = EpisodesOf(show);
        var caper = await SeedMovieAsync("Caper");
        await SeedRequestAsync(MediaType.Episode, episodes[0].Id, TranslationStatus.Completed, DateTimeOffset.UtcNow.AddDays(-2));
        await SeedRequestAsync(MediaType.Episode, episodes[1].Id, TranslationStatus.InProgress, DateTimeOffset.UtcNow.AddDays(-1));
        // movie aggregation is untouched: its own convention still counts
        await SeedRequestAsync(MediaType.Movie, caper.Id, TranslationStatus.Completed, DateTimeOffset.UtcNow);

        var result = await CreateService().GetLibrary(null, null, null, null, "Title", true, 1, 20);

        var showItem = result.Items.Single(i => i.Title == "Both Conventions");
        Assert.Equal(MediaType.Show, showItem.MediaType);
        Assert.Equal(MediaTranslationState.InProgress, showItem.State);
        Assert.Equal(1, showItem.CompletedCount);
        Assert.Equal(2, showItem.EpisodeCount);

        var movieItem = result.Items.Single(i => i.Title == "Caper");
        Assert.Equal(MediaTranslationState.Completed, movieItem.State);
        Assert.Equal(1, movieItem.CompletedCount);

        // the single-media direct path aggregates episode rows too
        var detail = await CreateService().GetLibraryItem(MediaType.Show, show.Id);
        Assert.Equal(MediaTranslationState.InProgress, detail!.State);
        Assert.Equal(1, detail.CompletedCount);
    }

    [Fact]
    public async Task NewestEpisodeRequestWins_AcrossBothMediaTypeConventions()
    {
        // one episode carries both conventions: an older Show-typed Completed
        // row and a newer Episode-typed Failed row — the newest one wins and
        // the older row must not double-count.
        var show = await SeedShowAsync("Mixed", [1]);
        var episode = EpisodesOf(show)[0];
        await SeedRequestAsync(MediaType.Show, episode.Id, TranslationStatus.Completed, DateTimeOffset.UtcNow.AddDays(-2));
        await SeedRequestAsync(MediaType.Episode, episode.Id, TranslationStatus.Failed, DateTimeOffset.UtcNow.AddDays(-1));

        var item = (await CreateService().GetLibrary(MediaType.Show, null, null, null, "Title", true, 1, 20))
            .Items.Single();

        Assert.Equal(MediaTranslationState.Failed, item.State);
        Assert.Equal(0, item.CompletedCount);
        Assert.Equal(1, item.FailedCount);
    }

    [Fact]
    public async Task NonPositivePageNumbers_ClampToTheFirstPage()
    {
        await SeedShowAsync("Alpha", [1]);
        await SeedShowAsync("Beta", [1]);
        await SeedShowAsync("Gamma", [1]);

        var service = CreateService();

        // pageNumber 0 and negative used to reach List.Skip with a negative
        // count and throw — they clamp to page 1 instead of 500ing
        var pageZero = await service.GetLibrary(null, null, null, null, "Title", true, 0, 2);
        Assert.Equal(3, pageZero.TotalCount);
        Assert.Equal(1, pageZero.PageNumber);
        Assert.Equal(["Alpha", "Beta"], pageZero.Items.Select(i => i.Title).ToArray());

        var pageNegative = await service.GetLibrary(null, null, null, null, "Title", true, -3, 2);
        Assert.Equal(1, pageNegative.PageNumber);
        Assert.Equal(["Alpha", "Beta"], pageNegative.Items.Select(i => i.Title).ToArray());
    }

    [Fact]
    public async Task StateAndTargetLanguageFiltersStayTruthful()
    {
        await SeedShowAsync("Full-zh", [2], new Dictionary<string, MediaLanguageCoverage>
        {
            ["zh"] = new() { Count = 2, Bytes = 100 }
        });
        await SeedShowAsync("Half-zh", [2], new Dictionary<string, MediaLanguageCoverage>
        {
            ["zh"] = new() { Count = 1, Bytes = 100 }
        });
        var blown = await SeedMovieAsync("Blown");
        await SeedRequestAsync(MediaType.Movie, blown.Id, TranslationStatus.Failed);

        var service = CreateService();

        // "missing zh" keeps partial coverage and no-coverage items
        var missingZh = await service.GetLibrary(null, null, null, "zh", "Title", true, 1, 20);
        Assert.Equal(2, missingZh.TotalCount);
        Assert.Equal(["Blown", "Half-zh"], missingZh.Items.Select(i => i.Title).ToArray());

        // state filter narrows to the failed movie
        var failed = await service.GetLibrary(null, null, "Failed", null, "Title", true, 1, 20);
        Assert.Equal("Blown", failed.Items.Single().Title);
    }

    [Fact]
    public async Task SingleMediaDirectPath_BuildsOneItemWithoutFullAggregation()
    {
        // a show and a movie share the numeric id: shows win for a null
        // mediaType, mirroring the old FirstOrDefault-over-lists behaviour.
        const int sharedId = 10;
        var show = await SeedShowAsync("Solo", [2], explicitId: sharedId);
        var episodes = EpisodesOf(show);
        await SeedRequestAsync(MediaType.Show, episodes[0].Id, TranslationStatus.Completed, DateTimeOffset.UtcNow.AddDays(-2));
        await SeedRequestAsync(MediaType.Show, episodes[1].Id, TranslationStatus.Pending, DateTimeOffset.UtcNow.AddDays(-1));
        await SeedMovieAsync("Twin", explicitId: sharedId);

        var service = CreateService();

        var showItem = await service.GetLibraryItem(MediaType.Show, sharedId);
        Assert.NotNull(showItem);
        Assert.Equal("Solo", showItem.Title);
        Assert.Equal(2, showItem.EpisodeCount);
        Assert.Equal(1, showItem.SeasonCount);
        Assert.Equal(MediaTranslationState.Queued, showItem.State);
        Assert.Equal(1, showItem.CompletedCount);
        Assert.NotNull(showItem.Show);
        Assert.Equal(2, showItem.Show.Seasons.Single().Episodes.Count);

        // null mediaType resolves the show, not the movie with the same id
        Assert.Equal("Solo", (await service.GetLibraryItem(null, sharedId))!.Title);

        // typed lookup finds the movie instead
        var movieItem = await service.GetLibraryItem(MediaType.Movie, sharedId);
        Assert.NotNull(movieItem);
        Assert.Equal("Twin", movieItem.Title);
        Assert.Equal(1, movieItem.EpisodeCount);
        Assert.Equal(MediaTranslationState.None, movieItem.State);
        Assert.Null(movieItem.Show);

        // unknown id and type mismatches stay 404-shaped
        Assert.Null(await service.GetLibraryItem(MediaType.Movie, 999));
        Assert.Null(await service.GetLibraryItem(null, 999));
    }

    // ── audio-aware states (PRD card A): SQL pushdown + item fields ──

    [Fact]
    public async Task NativeFilterPushesDownToSqlAndFlagsItems()
    {
        // native movie: the scan judged it not needing (zh audio exempt)
        await SeedMovieAsync("Native Movie", audioLanguages: ["zh"], needsTranslation: false, noSubtitleNeeded: true);
        // still missing the target
        await SeedMovieAsync("Gap Movie", audioLanguages: ["en"], needsTranslation: true);
        // show whose episodes all need nothing
        var nativeShow = await SeedShowAsync("Native Show", [2]);
        foreach (var episode in EpisodesOf(nativeShow))
        {
            episode.NeedsTranslation = false;
            episode.NoSubtitleNeeded = true;
        }
        // show with one episode still needing
        var gapShow = await SeedShowAsync("Gap Show", [2]);
        EpisodesOf(gapShow)[0].NeedsTranslation = false;
        EpisodesOf(gapShow)[1].NeedsTranslation = true;
        await _dbContext.SaveChangesAsync();

        _sqlLog.Clear();
        var result = await CreateService().GetLibrary(
            null, null, MediaLibraryService.NativeState, null, "Title", true, 1, 20);

        Assert.Equal(2, result.TotalCount);
        Assert.Equal(["Native Movie", "Native Show"], result.Items.Select(i => i.Title).ToArray());
        Assert.All(result.Items, item => Assert.True(item.NeedsNoSubtitles));

        // the state filtered inside SQL, over the needs flags (movies) and
        // the per-episode aggregate (shows) — not in memory after the fact
        var mediaQueries = _sqlLog.Where(sql => sql.Contains("FROM \"movies\"", StringComparison.OrdinalIgnoreCase)
                                                || sql.Contains("FROM \"shows\"", StringComparison.OrdinalIgnoreCase))
            .ToList();
        // The test context has no snake-case naming convention, so accept
        // both "needs_translation" (production) and "NeedsTranslation" (raw
        // model naming) by comparing underscore-stripped, case-insensitively.
        Assert.Contains(mediaQueries, sql =>
            sql.Replace("_", "").Contains("nosubtitleneeded", StringComparison.OrdinalIgnoreCase));
        // an episode-less show is not native (vacuous All would lie)
        await SeedShowAsync("Empty Show", Array.Empty<int>());
        var withEmpty = await CreateService().GetLibrary(
            MediaType.Show, null, MediaLibraryService.NativeState, null, "Title", true, 1, 20);
        Assert.Equal(["Native Show"], withEmpty.Items.Select(i => i.Title).ToArray());
    }

    [Fact]
    public async Task AudioUnknownFilterPushesDownToSqlAndAggregatesShows()
    {
        await SeedMovieAsync("Unknown Audio", audioLanguages: null);
        await SeedMovieAsync("Known Audio", audioLanguages: ["en"]);
        var show = await SeedShowAsync("Mixed Show", [2]);
        EpisodesOf(show)[0].AudioLanguages = ["zh"];
        // episode 2 stays null → the whole show reports unknown audio
        await _dbContext.SaveChangesAsync();

        _sqlLog.Clear();
        var result = await CreateService().GetLibrary(
            null, null, MediaLibraryService.AudioUnknownState, null, "Title", true, 1, 20);

        Assert.Equal(2, result.TotalCount);
        Assert.Equal(["Mixed Show", "Unknown Audio"], result.Items.Select(i => i.Title).ToArray());
        Assert.Null(result.Items.Single(i => i.Title == "Unknown Audio").AudioLanguages);
        Assert.Null(result.Items.Single(i => i.Title == "Mixed Show").AudioLanguages);

        // the predicate reached SQL as an IS NULL on the audio column
        Assert.Contains(_sqlLog, sql =>
            sql.Replace("_", "").Contains("audiolanguages", StringComparison.OrdinalIgnoreCase)
            && sql.Contains("IS NULL", StringComparison.OrdinalIgnoreCase));

        // known audio never matches the filter and passes through unchanged
        var known = (await CreateService().GetLibrary(MediaType.Movie, null, null, null, "Title", true, 1, 20))
            .Items.Single(i => i.Title == "Known Audio");
        Assert.Equal(["en"], known.AudioLanguages);
    }

    [Fact]
    public async Task ItemsCarryAudioLanguagesAndNeedFlags_AggregatingShowsOverEpisodes()
    {
        var movie = await SeedMovieAsync("Caper", audioLanguages: ["zh", "en"], needsTranslation: false, noSubtitleNeeded: true);
        var show = await SeedShowAsync("Union", [2]);
        var episodes = EpisodesOf(show);
        episodes[0].AudioLanguages = ["zh"];
        episodes[1].AudioLanguages = ["en", "zh"];
        episodes[1].NeedsTranslation = true; // one needing episode keeps the show out of native
        await _dbContext.SaveChangesAsync();

        var result = await CreateService().GetLibrary(null, null, null, null, "Title", true, 1, 20);

        var movieItem = result.Items.Single(i => i.Title == "Caper");
        Assert.Equal(["zh", "en"], movieItem.AudioLanguages); // passthrough, stored order
        Assert.True(movieItem.NeedsNoSubtitles);

        var showItem = result.Items.Single(i => i.Title == "Union");
        Assert.Equal(["en", "zh"], showItem.AudioLanguages); // distinct union
        Assert.False(showItem.NeedsNoSubtitles);

        // the detail direct path reports the same fields
        var detail = await CreateService().GetLibraryItem(MediaType.Movie, movie.Id);
        Assert.Equal(["zh", "en"], detail!.AudioLanguages);
        Assert.True(detail.NeedsNoSubtitles);
        var showDetail = await CreateService().GetLibraryItem(MediaType.Show, show.Id);
        Assert.Equal(["en", "zh"], showDetail!.AudioLanguages);
        Assert.False(showDetail.NeedsNoSubtitles);
    }
}
