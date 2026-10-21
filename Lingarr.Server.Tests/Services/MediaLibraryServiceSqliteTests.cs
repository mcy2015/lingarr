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
using Lingarr.Server.Modules.SubtitleSearch.Pipeline;
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
        // M0011's FTS5 virtual table is not part of the EF model (virtual
        // tables cannot be), so test schemas build it explicitly — from then
        // on the context's save hook maintains it exactly like production
        // and the search-side drift self-heal has the real shape to work on.
        ExecuteRaw("CREATE VIRTUAL TABLE media_title_fts USING fts5(title, media_type UNINDEXED)");

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
            new TargetLanguageResolver(_settingService.Object),
            NullLogger<MediaLibraryService>.Instance);

    private async Task<Show> SeedShowAsync(
        string title,
        int[] episodesPerSeason,
        Dictionary<string, MediaLanguageCoverage>? coverage = null,
        int? explicitId = null,
        int? year = null,
        string? imdbId = null,
        double? imdbRating = null,
        string? path = null)
    {
        var show = new Show
        {
            SonarrId = _nextExternalId++,
            Title = title,
            Path = path ?? "/tv/" + title,
            DateAdded = DateTimeOffset.UtcNow,
            SubtitleCoverage = coverage,
            Year = year,
            ImdbId = imdbId,
            ImdbRating = imdbRating
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
                Path = $"{show.Path}/S{seasonNumber:00}",
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
        bool noSubtitleNeeded = false,
        int? year = null,
        string? imdbId = null,
        string? quality = null,
        long? sizeBytes = null,
        double? imdbRating = null,
        string? path = null)
    {
        var movie = new Movie
        {
            RadarrId = _nextExternalId++,
            Title = title,
            FileName = title + ".mkv",
            Path = path ?? "/movies/" + title,
            DateAdded = DateTimeOffset.UtcNow,
            SubtitleCoverage = coverage,
            AudioLanguages = audioLanguages,
            AudioOverride = audioOverride,
            NeedsTranslation = needsTranslation,
            NoSubtitleNeeded = noSubtitleNeeded,
            Year = year,
            ImdbId = imdbId,
            Quality = quality,
            SizeBytes = sizeBytes,
            ImdbRating = imdbRating
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

    private object? ExecuteScalarRaw(string sql)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    private long Scalar(string sql) => Convert.ToInt64(ExecuteScalarRaw(sql));

    private string? ScalarString(string sql) => Convert.ToString(ExecuteScalarRaw(sql));

    private void ExecuteRaw(string sql)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

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

    // ── B2: search matches the on-disk path alongside the title ─────────
    // A CJK-titled library row typically carries the romanised release name
    // only in its path — the title dimension alone (FTS or LIKE) can never
    // surface it.

    [Fact]
    public async Task Search_MatchesTheOnDiskPathForBothBranches()
    {
        // 狗阵 ("Black Dog") releases live under the English name on disk.
        await SeedMovieAsync("狗阵", path: "/movies/Black Dog (2024)");
        await SeedShowAsync("黑猫警长", [1], path: "/tv/Black Cat Sheriff");
        // same-title control: matches by title regardless of path
        await SeedMovieAsync("Black Dog Movie", path: "/movies/bdm");

        var service = CreateService();

        // ASCII branch (FTS id set ∪ path substring): the FTS finds nothing
        // for the CJK titles, the path arm surfaces both.
        var english = await service.GetLibrary(null, "black dog", null, null, "Title", true, 1, 20);
        Assert.Equal(2, english.TotalCount);
        Assert.Equal(
            ["Black Dog Movie", "狗阵"],
            english.Items.Select(i => i.Title).ToArray());

        // CJK branch (title LIKE OR path LIKE): the Chinese title still hits
        // by title, and a Chinese path fragment hits by path.
        var chineseTitle = await service.GetLibrary(null, "狗阵", null, null, "Title", true, 1, 20);
        Assert.Equal("狗阵", chineseTitle.Items.Single().Title);
        var chinesePath = await service.GetLibrary(null, "黑猫警长", null, null, "Title", true, 1, 20);
        Assert.Equal("黑猫警长", chinesePath.Items.Single().Title);
        // the English show path answers to an ASCII term too
        var sheriff = await service.GetLibrary(MediaType.Show, "sheriff", null, null, "Title", true, 1, 20);
        Assert.Equal("黑猫警长", sheriff.Items.Single().Title);

        // a term in no title and no path still returns nothing
        Assert.Equal(0, (await service.GetLibrary(null, "nonexistent term", null, null, "Title", true, 1, 20)).TotalCount);
    }

    [Fact]
    public async Task Search_PathUnionAppliesToTheSqlPagedPathToo()
    {
        // target-language filter forces the full aggregation path; the
        // default browsing shape pages in SQL — the path arm must behave
        // identically on both (GetLibraryPagedAsync shares the predicates,
        // this pins it end to end).
        await SeedMovieAsync("狗阵", path: "/movies/Black Dog (2024)");

        var aggregated = await CreateService().GetLibrary(null, "black dog", null, "zh", "Title", true, 1, 20);
        Assert.Equal("狗阵", aggregated.Items.Single().Title);

        var paged = await CreateService().GetLibrary(null, "black dog", null, null, "Title", true, 1, 20);
        Assert.Equal("狗阵", paged.Items.Single().Title);
        Assert.Equal(1, paged.TotalCount);
    }

    // ── FTS5 title search (audit P1-4): prefix semantics, id-set pushdown,
    //    LIKE fallbacks and the drift self-heal ──────────────────────────

    [Fact]
    public async Task FtsSearch_MatchesWordPrefixesNotInfixes()
    {
        // Opaque paths: the search predicate is a title-OR-path union (B2),
        // so this title-semantics test seeds paths that carry no title text
        // — otherwise a title infix would surface through the path match
        // and blur what is being pinned here.
        await SeedShowAsync("Star Trek", [1], path: "/tv/s1");
        await SeedMovieAsync("Starship Troopers", path: "/movies/m1");
        await SeedMovieAsync("Startled Muse", path: "/movies/m2");
        await SeedMovieAsync("Pineapple", path: "/movies/m3");

        var service = CreateService();

        // "star" prefixes the first word of three titles across both types
        var star = await service.GetLibrary(null, "star", null, null, "Title", true, 1, 20);
        Assert.Equal(3, star.TotalCount);
        Assert.Equal(
            ["Star Trek", "Starship Troopers", "Startled Muse"],
            star.Items.Select(i => i.Title).ToArray());

        // Word boundary (the locked semantics change): a mid-word fragment
        // matches nothing under prefix matching, where the old substring
        // LIKE would have found Pineapple via "apple".
        Assert.Equal(0, (await service.GetLibrary(null, "apple", null, null, "Title", true, 1, 20)).TotalCount);
        Assert.Equal(1, (await service.GetLibrary(null, "pine", null, null, "Title", true, 1, 20)).TotalCount);

        // A prefix matches any token of the title, not only the first word
        Assert.Equal(1, (await service.GetLibrary(null, "trek", null, null, "Title", true, 1, 20)).TotalCount);
        Assert.Equal(0, (await service.GetLibrary(null, "rek", null, null, "Title", true, 1, 20)).TotalCount);

        // Multi-word queries AND their prefix phrases
        Assert.Equal(1, (await service.GetLibrary(null, "star mus", null, null, "Title", true, 1, 20)).TotalCount);
        Assert.Equal("Startled Muse",
            (await service.GetLibrary(null, "star mus", null, null, "Title", true, 1, 20)).Items.Single().Title);
    }

    [Fact]
    public async Task FtsSearch_FiltersByIdSetNotTitleScan()
    {
        // Opaque paths (see FtsSearch_MatchesWordPrefixesNotInfixes): the
        // path arm of the union predicate must not manufacture extra hits
        // here — what this test pins is the SQL shape of the FTS branch.
        await SeedShowAsync("Apple Orchard", [1], path: "/tv/s1");
        await SeedShowAsync("Apricot Tree", [1], path: "/tv/s2");
        await SeedMovieAsync("Apricot Lane", path: "/movies/m1");
        await SeedMovieAsync("Pineapple Express", path: "/movies/m2");

        _sqlLog.Clear();
        var result = await CreateService().GetLibrary(null, "ap", null, null, "Title", true, 1, 20);

        Assert.Equal(
            ["Apple Orchard", "Apricot Lane", "Apricot Tree"],
            result.Items.Select(i => i.Title).ToArray());

        // The media scans consumed the FTS-resolved id set: membership over
        // ids, no lower(title) LIKE predicate left — the audit P1-4
        // full-table title scan is gone from the media queries of both
        // types. The B2 path arm rides along as an OR-ed instr() on the
        // path column (never a LIKE). (The show side carries two matched
        // ids so the IN form is unambiguous; EF may render a single-id
        // membership as an equality.)
        var mediaQueries = _sqlLog.Where(sql =>
                sql.Contains("FROM \"Shows\"", StringComparison.OrdinalIgnoreCase)
                || sql.Contains("FROM \"Movies\"", StringComparison.OrdinalIgnoreCase))
            .ToList();
        Assert.True(mediaQueries.Count >= 2,
            $"expected the show and movie scans, got: {string.Join(Environment.NewLine, _sqlLog)}");
        Assert.All(mediaQueries, sql =>
            Assert.DoesNotContain("LIKE", sql.ToUpperInvariant()));
        Assert.Contains(mediaQueries, sql => sql.ToUpperInvariant().Contains("IN ("));
    }

    [Fact]
    public async Task NonAsciiSearch_FallsBackToTheLikePath()
    {
        await SeedShowAsync("星际穿越", [1]);
        await SeedMovieAsync("流浪地球");

        _sqlLog.Clear();
        var result = await CreateService().GetLibrary(null, "穿越", null, null, "Title", true, 1, 20);

        // Substring semantics survive for CJK: "穿越" is a suffix of the
        // title — a word-prefix FTS match would miss it (unicode61 does not
        // segment hanzi runs), so the LIKE path serves it.
        Assert.Equal(1, result.TotalCount);
        Assert.Equal("星际穿越", result.Items.Single().Title);

        var mediaQueries = _sqlLog.Where(sql =>
                sql.Contains("FROM \"Shows\"", StringComparison.OrdinalIgnoreCase)
                || sql.Contains("FROM \"Movies\"", StringComparison.OrdinalIgnoreCase))
            .ToList();
        Assert.NotEmpty(mediaQueries);
        // The fallback predicate ran inside the media scans — EF translates
        // Contains on SQLite to instr() in current versions (older: LIKE),
        // so accept either shape; what must NOT appear is an FTS MATCH.
        Assert.Contains(mediaQueries, sql =>
            sql.Contains("LIKE", StringComparison.OrdinalIgnoreCase)
            || sql.Contains("instr(", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(mediaQueries, sql =>
            sql.Contains("MATCH", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task FtsIndex_RebuildsItselfWhenDrifted()
    {
        await SeedShowAsync("Healed Show", [1]);
        await SeedMovieAsync("Healed Movie");

        // Seeding ran through the save hook: every media row (show, its
        // episode, the movie) carries an index row.
        Assert.Equal(3L, Scalar("SELECT COUNT(*) FROM media_title_fts"));

        // Simulate drift — a write lost mid-save, or the orphan left behind
        // by a set-based ExecuteDelete removal that bypasses the tracker.
        ExecuteRaw("DELETE FROM media_title_fts WHERE media_type = 'movie'");

        var result = await CreateService().GetLibrary(null, "healed", null, null, "Title", true, 1, 20);

        Assert.Equal(2, result.TotalCount);
        Assert.Equal(["Healed Movie", "Healed Show"], result.Items.Select(i => i.Title).ToArray());
        Assert.Equal(3L, Scalar("SELECT COUNT(*) FROM media_title_fts"));
    }

    [Fact]
    public async Task SearchWithoutTheFtsTable_FallsBackToLike()
    {
        // Schemas built by EnsureCreated (fixtures predating M0011) carry no
        // virtual table: the search must degrade to the LIKE predicate and
        // the save hook must quietly skip the missing table.
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        await using var dbContext = new LingarrDbContext(new DbContextOptionsBuilder<LingarrDbContext>()
            .UseSqlite(connection)
            .Options);
        await dbContext.Database.EnsureCreatedAsync();
        dbContext.Movies.Add(new Movie
        {
            RadarrId = 500,
            Title = "Fallback Film",
            FileName = "Fallback.2020.mkv",
            Path = "/movies/fallback",
            DateAdded = DateTimeOffset.UtcNow
        });
        await dbContext.SaveChangesAsync();

        var service = new MediaLibraryService(
            dbContext, _settingService.Object, _costService.Object,
            new TargetLanguageResolver(_settingService.Object),
            NullLogger<MediaLibraryService>.Instance);
        var result = await service.GetLibrary(null, "fall", null, null, "Title", true, 1, 20);

        Assert.Equal(1, result.TotalCount);
        Assert.Equal("Fallback Film", result.Items.Single().Title);
    }

    [Fact]
    public async Task TitleWritesMaintainTheFtsIndex()
    {
        var show = await SeedShowAsync("Old Title", [1]);
        var movie = await SeedMovieAsync("Caper");

        // Seeding ran through the save hook — the movie, the show and the
        // episode each carry an index row under their type-encoded rowid.
        Assert.Equal(3L, Scalar("SELECT COUNT(*) FROM media_title_fts"));
        Assert.Equal("Old Title",
            ScalarString($"SELECT title FROM media_title_fts WHERE media_type = 'show' AND rowid = 2000000000000 + {show.Id}"));

        // A rename through a tracked save re-indexes the row (upsert).
        movie.Title = "New Title";
        await _dbContext.SaveChangesAsync();
        Assert.Equal("New Title",
            ScalarString($"SELECT title FROM media_title_fts WHERE media_type = 'movie' AND rowid = 1000000000000 + {movie.Id}"));
        Assert.Equal(3L, Scalar("SELECT COUNT(*) FROM media_title_fts"));

        // A flag-only update (the statistics scan's writes look exactly like
        // this) never re-indexes.
        movie.NeedsTranslation = true;
        await _dbContext.SaveChangesAsync();
        Assert.Equal(3L, Scalar("SELECT COUNT(*) FROM media_title_fts"));
        Assert.Equal("New Title",
            ScalarString($"SELECT title FROM media_title_fts WHERE media_type = 'movie' AND rowid = 1000000000000 + {movie.Id}"));

        // Deleting a tracked entity removes its index row; set-based
        // ExecuteDelete removals leave orphans that the drift self-heal
        // cleans on the next search instead.
        _dbContext.Movies.Remove(movie);
        await _dbContext.SaveChangesAsync();
        Assert.Equal(0L,
            Scalar($"SELECT COUNT(*) FROM media_title_fts WHERE media_type = 'movie' AND rowid = 1000000000000 + {movie.Id}"));
        Assert.Equal(2L, Scalar("SELECT COUNT(*) FROM media_title_fts"));
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

    // ── library B2: *arr metadata columns on the projection ──────────

    [Fact]
    public async Task ItemsCarryB2Columns_MoviesFullShowsYearAndRatingOnly()
    {
        // movie with the full set from Radarr's movieFile/telemetry payload
        await SeedMovieAsync(
            "Dune",
            year: 2024,
            imdbId: "tt15239678",
            quality: "WEBDL-1080p",
            sizeBytes: 4_500_000_000,
            imdbRating: 8.5);
        // show: quality/size are movie-file concepts, its projection carries
        // only the series-level year/imdb/rating
        await SeedShowAsync("Foundation", [2], year: 2021, imdbId: "tt2397406", imdbRating: 7.7);
        // legacy rows without the columns stay null, never fabricated
        await SeedMovieAsync("Bare");

        var result = await CreateService().GetLibrary(null, null, null, null, "Title", true, 1, 20);

        var movie = result.Items.Single(i => i.Title == "Dune");
        Assert.Equal(2024, movie.Year);
        Assert.Equal("tt15239678", movie.ImdbId);
        Assert.Equal("WEBDL-1080p", movie.Quality);
        Assert.Equal(4_500_000_000, movie.SizeBytes);
        Assert.Equal(8.5, movie.ImdbRating);

        var show = result.Items.Single(i => i.Title == "Foundation");
        Assert.Equal(2021, show.Year);
        Assert.Equal("tt2397406", show.ImdbId);
        Assert.Equal(7.7, show.ImdbRating);
        Assert.Null(show.Quality);
        Assert.Null(show.SizeBytes);

        var bare = result.Items.Single(i => i.Title == "Bare");
        Assert.Null(bare.Year);
        Assert.Null(bare.ImdbId);
        Assert.Null(bare.Quality);
        Assert.Null(bare.SizeBytes);
        Assert.Null(bare.ImdbRating);

        // the single-media direct path carries the same columns
        var movieDetail = await CreateService().GetLibraryItem(MediaType.Movie, movie.Id);
        Assert.Equal("tt15239678", movieDetail!.ImdbId);
        Assert.Equal("WEBDL-1080p", movieDetail.Quality);
        var showDetail = await CreateService().GetLibraryItem(MediaType.Show, show.Id);
        Assert.Equal(2021, showDetail!.Year);
        Assert.Null(showDetail.Quality);
    }

    // ── P0-7 closeout: two-phase default path (SQL paging + page-only
    //    coverage hydration), behaviour-equal to the full aggregation ──

    [Fact]
    public async Task DefaultPathPagesInSql_EquivalentToTheFullAggregationPath()
    {
        // mixed library where every item lacks zh coverage, so the
        // target-language filter of the legacy path keeps everything and the
        // two paths page over the very same set
        await SeedShowAsync("Alpha Show", [2, 1], new Dictionary<string, MediaLanguageCoverage>
        {
            ["en"] = new() { Count = 3, Bytes = 900 }
        });
        await SeedShowAsync("Beta Show", [1]);
        var blown = await SeedMovieAsync("Caper Movie");
        await SeedRequestAsync(MediaType.Movie, blown.Id, TranslationStatus.Failed);
        var partial = await SeedShowAsync("Delta Show", [2], new Dictionary<string, MediaLanguageCoverage>
        {
            ["en"] = new() { Count = 1, Bytes = 100 }
        });
        await SeedRequestAsync(
            MediaType.Show, EpisodesOf(partial)[0].Id, TranslationStatus.Completed, DateTimeOffset.UtcNow.AddDays(-2));
        await SeedMovieAsync("Echo Movie", audioLanguages: ["en"]);
        var native = await SeedShowAsync("Foxtrot Show", [2]);
        foreach (var episode in EpisodesOf(native))
        {
            episode.NoSubtitleNeeded = true;
        }
        await _dbContext.SaveChangesAsync();

        var service = CreateService();
        for (var page = 1; page <= 4; page++)
        {
            // default browsing shape → two-phase SQL path
            var paged = await service.GetLibrary(null, null, null, null, "Title", true, page, 2);
            // target-language filter forces the full in-memory aggregation
            var aggregated = await service.GetLibrary(null, null, null, "zh", "Title", true, page, 2);

            Assert.Equal(aggregated.TotalCount, paged.TotalCount);
            Assert.Equal(aggregated.PageNumber, paged.PageNumber);
            Assert.Equal(aggregated.PageSize, paged.PageSize);
            Assert.Equal(
                aggregated.Items.Select(i => (i.MediaType, i.Id, i.Title)).ToList(),
                paged.Items.Select(i => (i.MediaType, i.Id, i.Title)).ToList());

            foreach (var expected in aggregated.Items)
            {
                var actual = paged.Items.Single(i => i.Id == expected.Id && i.MediaType == expected.MediaType);
                Assert.Equal(expected.SeasonCount, actual.SeasonCount);
                Assert.Equal(expected.EpisodeCount, actual.EpisodeCount);
                Assert.Equal(expected.State, actual.State);
                Assert.Equal(expected.CompletedCount, actual.CompletedCount);
                Assert.Equal(expected.FailedCount, actual.FailedCount);
                Assert.Equal(expected.NeedsNoSubtitles, actual.NeedsNoSubtitles);
                Assert.Equal(expected.LastActivityAt, actual.LastActivityAt);
                Assert.Equal(
                    expected.Tracks.Select(t => (t.Language, t.Have, t.Total, t.State)).ToList(),
                    actual.Tracks.Select(t => (t.Language, t.Have, t.Total, t.State)).ToList());
                // inline show graphs hydrate on the paged path too
                if (expected.MediaType == MediaType.Show)
                {
                    Assert.NotNull(actual.Show);
                    Assert.Equal(expected.Show!.Seasons.Count, actual.Show!.Seasons.Count);
                }
            }
        }
    }

    [Fact]
    public async Task DefaultPathHydratesCoverageForThePageRowsOnly()
    {
        // a library bigger than the page: six movies, each with a coverage
        // snapshot — the default path must never pull the JSON for all of
        // them, only for the page's two rows
        for (var i = 1; i <= 6; i++)
        {
            await SeedMovieAsync($"Movie {i}", new Dictionary<string, MediaLanguageCoverage>
            {
                ["en"] = new() { Count = 1, Bytes = 10 * i }
            });
        }

        _sqlLog.Clear();
        var page = await CreateService().GetLibrary(null, null, null, null, "Title", true, 1, 2);

        Assert.Equal(6, page.TotalCount);
        Assert.Equal(2, page.Items.Count());
        // the hydrated coverage actually built the page rows' tracks
        Assert.All(page.Items, item =>
            Assert.Equal(1, item.Tracks.Single(t => t.Language == "en").Have));

        // every SQL statement touching the coverage column is a page-scoped
        // hydration (IN-list over the page ids): the phase-1 scan leaves the
        // column out entirely, so no statement selects it for the whole
        // library. At most one hydration query per media type.
        var coverageQueries = _sqlLog
            .Where(sql => sql.Replace("_", "").Contains("SubtitleCoverageJson", StringComparison.OrdinalIgnoreCase))
            .ToList();
        Assert.NotEmpty(coverageQueries);
        Assert.True(coverageQueries.Count <= 2,
            $"expected at most one coverage hydration per media type, got {coverageQueries.Count}");
        Assert.All(coverageQueries, sql =>
            Assert.True(
                sql.Contains("IN", StringComparison.OrdinalIgnoreCase),
                $"coverage hydration must be page-scoped (IN over page ids), got: {sql}"));
    }
}
