using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Lingarr.Core.Configuration;
using Lingarr.Core.Data;
using Lingarr.Core.Entities;
using Lingarr.Core.Enum;
using Lingarr.Server.Models;
using Lingarr.Server.Modules.Settings;
using Lingarr.Server.Modules.Subtitle;
using Lingarr.Server.Modules.SubtitleSearch.Pipeline;
using Lingarr.Server.Modules.SubtitleSearch.Queue;
using Lingarr.Server.Modules.Translation;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Lingarr.Server.Tests.Modules.SubtitleSearch;

/// <summary>
/// Walkthrough fix B1: the single-media refresher writes exactly what the
/// daily statistics scan would write — movies get their coverage snapshot +
/// need flags, episodes resolve to the owning show whose aggregated
/// SubtitleCoverageJson is rebuilt from fresh season scans together with
/// every episode flag — so a subtitle that lands on disk (download or
/// translation writeback) surfaces immediately instead of on the next daily
/// run. Real SQLite because the refresher writes through ExecuteUpdate; the
/// subtitle service is the production one so directory scans, filename
/// language parsing and byte statting run exactly as in production.
/// </summary>
public class SubtitleCoverageRefresherTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly LingarrDbContext _dbContext;
    private readonly InMemorySettingService _settings = new();
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "lingarr-refresher-" + Guid.NewGuid().ToString("N"));

    private readonly SubtitleCoverageRefresher _refresher;

    public SubtitleCoverageRefresherTests()
    {
        Directory.CreateDirectory(_root);
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _dbContext = new RefresherDbContext(new DbContextOptionsBuilder<LingarrDbContext>()
            .UseSqlite(_connection)
            .UseSnakeCaseNamingConvention()
            .Options);
        _dbContext.Database.EnsureCreated();

        // zh is the deployment target: covering it flips the need flag.
        _settings.SetSetting(
            SettingKeys.Translation.TargetLanguages,
            JsonSerializer.Serialize(new List<TargetLanguage>
            {
                new() { Name = "Chinese", Code = "zh" }
            })).GetAwaiter().GetResult();

        var subtitleService = new SubtitleService(
            NullLogger<SubtitleService>.Instance,
            new LanguageCodeService(),
            _settings);
        _refresher = new SubtitleCoverageRefresher(
            _dbContext, subtitleService, new TargetLanguageResolver(_settings),
            NullLogger<SubtitleCoverageRefresher>.Instance);
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        _connection.Dispose();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // temp cleanup is best-effort
        }

        GC.SuppressFinalize(this);
    }

    private string WriteSubtitle(string relativeDir, string fileName, int bytes)
    {
        var directory = Path.Combine(_root, relativeDir);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, fileName);
        File.WriteAllBytes(path, new byte[bytes]);
        return directory;
    }

    // ── movie: coverage snapshot + need flags, daily-scan parity ──────

    [Fact]
    public async Task RefreshAsync_Movie_RebuildsCoverageAndFlipsTheNeedFlag()
    {
        var directory = Path.Combine(_root, "movie1");
        Directory.CreateDirectory(directory);
        var movie = new Movie
        {
            RadarrId = 1,
            Title = "Movie",
            Year = 2019,
            Path = directory,
            FileName = "Movie.2019.WEBDL-1080p.mkv",
            DateAdded = DateTimeOffset.UtcNow,
            AudioLanguages = ["en"], // not a target: missing zh means needs
            NeedsTranslation = true
        };
        _dbContext.Movies.Add(movie);
        await _dbContext.SaveChangesAsync();

        WriteSubtitle("movie1", "Movie.2019.WEBDL-1080p.en.srt", 40);
        WriteSubtitle("movie1", "Movie.2019.WEBDL-1080p.zh.srt", 100);

        await _refresher.RefreshAsync(MediaType.Movie, movie.Id, CancellationToken.None);

        var stored = await _dbContext.Movies.AsNoTracking().SingleAsync(m => m.Id == movie.Id);
        Assert.NotNull(stored.SubtitleCoverage);
        // movie coverage semantics (MediaLanguageCoverage): 0/1 per language
        Assert.Equal(1, stored.SubtitleCoverage!["zh"].Count);
        Assert.Equal(100, stored.SubtitleCoverage!["zh"].Bytes);
        Assert.Equal(1, stored.SubtitleCoverage!["en"].Count);
        Assert.Equal(40, stored.SubtitleCoverage!["en"].Bytes);
        // B07 regression gate: a library without forced files serializes the
        // slot without the forced fields (byte-identical pre-B07 JSON).
        Assert.Equal(0, stored.SubtitleCoverage!["zh"].ForcedCount);
        Assert.DoesNotContain("forcedCount", stored.SubtitleCoverageJson);
        // zh is covered and the audio is no target → no longer needing
        Assert.False(stored.NeedsTranslation);
        Assert.False(stored.NoSubtitleNeeded);
    }

    // ── B07: forced-only files are their own coverage dimension, presented
    // separately and never exempting the full search. ────────────────────

    [Fact]
    public async Task RefreshAsync_MovieForcedOnly_CountsForcedDimensionAndKeepsTheNeedFlag()
    {
        var directory = Path.Combine(_root, "movie-forced");
        Directory.CreateDirectory(directory);
        var movie = new Movie
        {
            RadarrId = 11,
            Title = "Forced Movie",
            Year = 2021,
            Path = directory,
            FileName = "Forced.Movie.2021.WEBDL-1080p.mkv",
            DateAdded = DateTimeOffset.UtcNow,
            AudioLanguages = ["en"],
            NeedsTranslation = true
        };
        _dbContext.Movies.Add(movie);
        await _dbContext.SaveChangesAsync();

        // The pipeline's own B07 naming: a forced candidate lands as
        // {base}.{language}.forced.{ext}.
        WriteSubtitle("movie-forced", "Forced.Movie.2021.WEBDL-1080p.zh.forced.srt", 80);

        await _refresher.RefreshAsync(MediaType.Movie, movie.Id, CancellationToken.None);

        var stored = await _dbContext.Movies.AsNoTracking().SingleAsync(m => m.Id == movie.Id);
        Assert.NotNull(stored.SubtitleCoverage);
        // zh exists as a slot — but ONLY in the forced dimension.
        Assert.Equal(0, stored.SubtitleCoverage!["zh"].Count);
        Assert.Equal(0, stored.SubtitleCoverage!["zh"].Bytes);
        Assert.Equal(1, stored.SubtitleCoverage!["zh"].ForcedCount);
        Assert.Equal(80, stored.SubtitleCoverage!["zh"].ForcedBytes);
        // The forced dimension does not exempt the full search: zh still
        // reads as missing for the need rule.
        Assert.True(stored.NeedsTranslation);
    }

    [Fact]
    public async Task RefreshAsync_MovieFullAndForced_KeepsBothDimensionsApart()
    {
        var directory = Path.Combine(_root, "movie-mixed");
        Directory.CreateDirectory(directory);
        var movie = new Movie
        {
            RadarrId = 12,
            Title = "Mixed Movie",
            Year = 2022,
            Path = directory,
            FileName = "Mixed.Movie.2022.WEBDL-1080p.mkv",
            DateAdded = DateTimeOffset.UtcNow,
            AudioLanguages = ["en"],
            NeedsTranslation = true
        };
        _dbContext.Movies.Add(movie);
        await _dbContext.SaveChangesAsync();

        WriteSubtitle("movie-mixed", "Mixed.Movie.2022.WEBDL-1080p.zh.srt", 100);
        WriteSubtitle("movie-mixed", "Mixed.Movie.2022.WEBDL-1080p.zh.forced.srt", 30);
        // sdh/cc/hi captions are FULL subtitles for a specific audience —
        // they must not slide into the forced dimension.
        WriteSubtitle("movie-mixed", "Mixed.Movie.2022.WEBDL-1080p.zh.hi.srt", 60);

        await _refresher.RefreshAsync(MediaType.Movie, movie.Id, CancellationToken.None);

        var stored = await _dbContext.Movies.AsNoTracking().SingleAsync(m => m.Id == movie.Id);
        // Movie semantics stay 0/1 per dimension: full + hi are one "full"
        // presence (bytes summed), the forced file one forced presence.
        Assert.Equal(1, stored.SubtitleCoverage!["zh"].Count);
        Assert.Equal(160, stored.SubtitleCoverage!["zh"].Bytes);
        Assert.Equal(1, stored.SubtitleCoverage!["zh"].ForcedCount);
        Assert.Equal(30, stored.SubtitleCoverage!["zh"].ForcedBytes);
        // The full file covers zh regardless of the forced sibling.
        Assert.False(stored.NeedsTranslation);
    }

    [Fact]
    public async Task RefreshAsync_EpisodeForcedOnly_DoesNotSatisfyTheEpisodeNeed()
    {
        var show = await SeedShowAsync(5, "show5/s01",
            "show5.s01e01.mkv", "show5.s01e02.mkv");
        var episodes = show.Seasons.SelectMany(s => s.Episodes).OrderBy(e => e.Id).ToList();
        // E01 carries only a forced zh track; E02 carries a full one.
        WriteSubtitle("show5/s01", "show5.s01e01.zh.forced.srt", 50);
        WriteSubtitle("show5/s01", "show5.s01e02.zh.srt", 70);

        await _refresher.RefreshAsync(MediaType.Episode, episodes[0].Id, CancellationToken.None);

        var storedShow = await _dbContext.Shows.AsNoTracking().SingleAsync(s => s.Id == show.Id);
        // The two dimensions coexist per language: E02's full file is the
        // one full count, E01's forced track the one forced count.
        Assert.Equal(1, storedShow.SubtitleCoverage!["zh"].Count);
        Assert.Equal(70, storedShow.SubtitleCoverage!["zh"].Bytes);
        Assert.Equal(1, storedShow.SubtitleCoverage!["zh"].ForcedCount);
        Assert.Equal(50, storedShow.SubtitleCoverage!["zh"].ForcedBytes);

        var storedEpisodes = await _dbContext.Episodes.AsNoTracking()
            .Where(e => e.Season.ShowId == show.Id)
            .OrderBy(e => e.Id)
            .ToListAsync();
        // Forced-only never exempts E01's full search; E02's full file does.
        Assert.True(storedEpisodes[0].NeedsTranslation);
        Assert.False(storedEpisodes[1].NeedsTranslation);
    }

    [Fact]
    public async Task RefreshAsync_MovieWithoutPath_ClearsStaleCoverage()
    {
        // parity with the daily scan's pathless-movie branch: no directory,
        // no coverage (null, not {}) and the rule judges on audio alone.
        var movie = new Movie
        {
            RadarrId = 2,
            Title = "Pathless",
            Year = 2020,
            Path = null,
            FileName = null,
            DateAdded = DateTimeOffset.UtcNow,
            SubtitleCoverageJson = "{\"zh\":{\"count\":1,\"bytes\":1}}",
            AudioLanguages = ["en"],
            NeedsTranslation = false
        };
        _dbContext.Movies.Add(movie);
        await _dbContext.SaveChangesAsync();

        await _refresher.RefreshAsync(MediaType.Movie, movie.Id, CancellationToken.None);

        var stored = await _dbContext.Movies.AsNoTracking().SingleAsync(m => m.Id == movie.Id);
        Assert.Null(stored.SubtitleCoverageJson);
        Assert.True(stored.NeedsTranslation);
    }

    // ── episode/show: the show aggregate is refreshed, no daily wait ──

    private async Task<Show> SeedShowAsync(int showId, string relativeDir, params string[] episodeFileNames)
    {
        var directory = Path.Combine(_root, relativeDir);
        Directory.CreateDirectory(directory);
        var show = new Show
        {
            Id = showId,
            SonarrId = showId,
            Title = $"show {showId}",
            Path = Path.Combine(_root, $"show{showId}-root"),
            DateAdded = DateTimeOffset.UtcNow,
            SubtitleCoverageJson = "{\"en\":{\"count\":2,\"bytes\":999}}" // stale daily snapshot
        };
        var season = new Season
        {
            Id = showId * 10,
            SeasonNumber = 1,
            Path = directory,
            ShowId = show.Id,
            Show = show
        };
        var episodeId = showId * 100;
        foreach (var fileName in episodeFileNames)
        {
            season.Episodes.Add(new Episode
            {
                Id = ++episodeId,
                SonarrId = episodeId,
                EpisodeNumber = episodeId - showId * 100,
                Title = fileName,
                Path = directory,
                FileName = fileName,
                SeasonId = season.Id,
                Season = season,
                NeedsTranslation = true // what the last daily scan left behind
            });
        }

        show.Seasons.Add(season);
        _dbContext.Shows.Add(show);
        await _dbContext.SaveChangesAsync();
        return show;
    }

    [Fact]
    public async Task RefreshAsync_Episode_RebuildsTheShowAggregateAndEpisodeFlags()
    {
        var show = await SeedShowAsync(1, "show1/s01",
            "show1.s01e01.mkv", "show1.s01e02.mkv");
        var episodes = show.Seasons.SelectMany(s => s.Episodes).OrderBy(e => e.Id).ToList();
        WriteSubtitle("show1/s01", "show1.s01e01.zh.srt", 60);

        await _refresher.RefreshAsync(MediaType.Episode, episodes[0].Id, CancellationToken.None);

        var storedShow = await _dbContext.Shows.AsNoTracking().SingleAsync(s => s.Id == show.Id);
        Assert.NotNull(storedShow.SubtitleCoverage);
        // the stale en snapshot is gone: fresh scan attributes the one file
        // to episode 1 — zh covered for exactly one episode.
        Assert.False(storedShow.SubtitleCoverage.ContainsKey("en"));
        Assert.Equal(1, storedShow.SubtitleCoverage!["zh"].Count);
        Assert.Equal(60, storedShow.SubtitleCoverage!["zh"].Bytes);

        var storedEpisodes = await _dbContext.Episodes.AsNoTracking()
            .Where(e => e.Season.ShowId == show.Id)
            .OrderBy(e => e.Id)
            .ToListAsync();
        Assert.False(storedEpisodes[0].NeedsTranslation); // zh landed for it
        Assert.True(storedEpisodes[1].NeedsTranslation); // still missing zh
    }

    [Fact]
    public async Task RefreshAsync_ShowById_RebuildsTheSameAggregate()
    {
        var show = await SeedShowAsync(2, "show2/s01",
            "show2.s01e01.mkv", "show2.s01e02.mkv");
        WriteSubtitle("show2/s01", "show2.s01e01.zh.srt", 10);
        WriteSubtitle("show2/s01", "show2.s01e02.zh.srt", 20);

        await _refresher.RefreshAsync(MediaType.Show, show.Id, CancellationToken.None);

        var storedShow = await _dbContext.Shows.AsNoTracking().SingleAsync(s => s.Id == show.Id);
        Assert.Equal(2, storedShow.SubtitleCoverage!["zh"].Count);
        Assert.Equal(30, storedShow.SubtitleCoverage!["zh"].Bytes);

        var storedEpisodes = await _dbContext.Episodes.AsNoTracking()
            .Where(e => e.Season.ShowId == show.Id)
            .ToListAsync();
        Assert.All(storedEpisodes, episode => Assert.False(episode.NeedsTranslation));
    }

    [Fact]
    public async Task RefreshAsync_EpisodeOfPathlessSeason_KeepsStoredShowSnapshot()
    {
        // the daily scan's rule: a show whose seasons carry no path keeps
        // its stored coverage — nothing was scanned, so nothing fresher
        // exists. The episode flags still re-run on audio/override alone.
        var show = await SeedShowAsync(3, "show3/s01", "show3.s01e01.mkv");
        var season = await _dbContext.Seasons.SingleAsync(s => s.ShowId == show.Id);
        season.Path = null;
        await _dbContext.SaveChangesAsync();
        var staleJson = show.SubtitleCoverageJson;
        var episodeId = show.Seasons.SelectMany(s => s.Episodes).Single().Id;

        await _refresher.RefreshAsync(MediaType.Episode, episodeId, CancellationToken.None);

        var storedShow = await _dbContext.Shows.AsNoTracking().SingleAsync(s => s.Id == show.Id);
        Assert.Equal(staleJson, storedShow.SubtitleCoverageJson);
    }

    [Fact]
    public async Task RefreshAsync_UnknownMedia_IsASilentNoOp()
    {
        await _refresher.RefreshAsync(MediaType.Movie, 424242, CancellationToken.None);
        await _refresher.RefreshAsync(MediaType.Episode, 424243, CancellationToken.None);
        await _refresher.RefreshAsync(MediaType.Show, 424244, CancellationToken.None);
    }

    // ── B08: per-media target-language overrides ─────────────────────
    //
    // Resolution order (TargetLanguageResolver): media override > global;
    // empty stored string = explicit「无目标」; NULL = follow global. All
    // three states pin against a global [zh,en] list.

    private async Task SetGlobalTargetsAsync(params string[] codes)
    {
        await _settings.SetSetting(
            SettingKeys.Translation.TargetLanguages,
            JsonSerializer.Serialize(codes
                .Select(code => new TargetLanguage { Name = code, Code = code })
                .ToList()));
    }

    private async Task<Movie> SeedMovieAsync(
        int radarrId,
        string relativeDir,
        string? targetLanguagesOverride,
        bool needsTranslation = true,
        string[]? audioLanguages = null)
    {
        var directory = Path.Combine(_root, relativeDir);
        Directory.CreateDirectory(directory);
        var movie = new Movie
        {
            RadarrId = radarrId,
            Title = $"movie {radarrId}",
            Year = 2000 + radarrId,
            Path = directory,
            FileName = $"movie{radarrId}.mkv",
            DateAdded = DateTimeOffset.UtcNow,
            // Default audio is no configured target; tests whose global list
            // contains "en" must pass an explicit non-target audio or the
            // shared need rule's native-audio exemption would decide the
            // verdict before any coverage gap could.
            AudioLanguages = audioLanguages ?? ["en"],
            TargetLanguagesOverride = targetLanguagesOverride,
            NeedsTranslation = needsTranslation
        };
        _dbContext.Movies.Add(movie);
        await _dbContext.SaveChangesAsync();
        return movie;
    }

    [Fact]
    public async Task RefreshAsync_MovieOverride_RestrictsTheNeedVerdictToTheOverride()
    {
        // B08 acceptance 1: override=zh, global=[zh,en] — only zh counts,
        // so a movie covered in zh stops needing translation even though
        // en is missing.
        await SetGlobalTargetsAsync("zh", "en");
        var directory = Path.Combine(_root, "movie-ovr");
        Directory.CreateDirectory(directory);
        var movie = new Movie
        {
            RadarrId = 21,
            Title = "Overridden",
            Year = 2023,
            Path = directory,
            FileName = "Overridden.2023.WEBDL-1080p.mkv",
            DateAdded = DateTimeOffset.UtcNow,
            AudioLanguages = ["en"],
            TargetLanguagesOverride = "zh", // en drops out of the verdict
            NeedsTranslation = true
        };
        _dbContext.Movies.Add(movie);
        await _dbContext.SaveChangesAsync();
        WriteSubtitle("movie-ovr", "Overridden.2023.WEBDL-1080p.zh.srt", 100);

        await _refresher.RefreshAsync(MediaType.Movie, movie.Id, CancellationToken.None);

        var stored = await _dbContext.Movies.AsNoTracking().SingleAsync(m => m.Id == movie.Id);
        // zh is covered under the override → no longer needing; with the
        // global list this would read TRUE (en missing)
        Assert.False(stored.NeedsTranslation);
    }

    [Fact]
    public async Task RefreshAsync_MovieWithoutOverride_FollowsTheGlobalList()
    {
        // The NULL column is the regression gate: pre-B08 behavior, judged
        // against the full global list. The audio is a non-target ("ko") so
        // the verdict derives from the coverage gap alone — audio "en" would
        // trip the shared rule's native-audio exemption (en is a global
        // target here) and exempt the movie before the missing en file
        // could matter.
        await SetGlobalTargetsAsync("zh", "en");
        var movie = await SeedMovieAsync(22, "movie-null", null, audioLanguages: ["ko"]);
        WriteSubtitle("movie-null", "movie22.zh.srt", 80);

        await _refresher.RefreshAsync(MediaType.Movie, movie.Id, CancellationToken.None);

        var stored = await _dbContext.Movies.AsNoTracking().SingleAsync(m => m.Id == movie.Id);
        // zh covered but en missing under the global list → still needing
        Assert.True(stored.NeedsTranslation);
    }

    [Fact]
    public async Task RefreshAsync_MovieEmptyOverride_IsExplicitNoTargets()
    {
        // B08: "" is the explicit「无目标」state — nothing can be missing,
        // so the movie stops needing subtitles. Distinct from the
        // no_subtitle_needed audio exemption, which stays FALSE here.
        await SetGlobalTargetsAsync("zh", "en");
        var movie = await SeedMovieAsync(23, "movie-empty", string.Empty);

        await _refresher.RefreshAsync(MediaType.Movie, movie.Id, CancellationToken.None);

        var stored = await _dbContext.Movies.AsNoTracking().SingleAsync(m => m.Id == movie.Id);
        Assert.False(stored.NeedsTranslation);
        Assert.False(stored.NoSubtitleNeeded); // not the audio exemption
        // the stored column itself keeps the state distinguishable from NULL
        Assert.Equal(string.Empty, stored.TargetLanguagesOverride);
    }

    [Fact]
    public async Task RefreshAsync_ShowOverride_AppliesToEveryEpisode()
    {
        // B08 acceptance 2: the SHOW's override governs every episode —
        // en-missing-under-global never flags an episode of a zh-only show.
        await SetGlobalTargetsAsync("zh", "en");
        var show = await SeedShowAsync(6, "show6/s01",
            "show6.s01e01.mkv", "show6.s01e02.mkv");
        show.TargetLanguagesOverride = "zh";
        await _dbContext.SaveChangesAsync();
        // every episode carries zh, nobody carries en
        WriteSubtitle("show6/s01", "show6.s01e01.zh.srt", 50);
        WriteSubtitle("show6/s01", "show6.s01e02.zh.srt", 50);

        await _refresher.RefreshAsync(MediaType.Show, show.Id, CancellationToken.None);

        var storedEpisodes = await _dbContext.Episodes.AsNoTracking()
            .Where(e => e.Season.ShowId == show.Id)
            .ToListAsync();
        Assert.Equal(2, storedEpisodes.Count);
        Assert.All(storedEpisodes, episode => Assert.False(episode.NeedsTranslation));
    }

    [Fact]
    public async Task RefreshedFlags_DriveTheGapQueue_OverrideKeepsMediaOut()
    {
        // B08 acceptance 1 (SQLite): the queue's gap INSERT selects on the
        // needs_translation flags the refresher wrote — an overridden movie
        // that only misses a removed target stays OUT of the queue while an
        // identical un-overridden sibling enters it. The follower's audio is
        // a non-target ("ko"): audio "en" would trip the native-audio
        // exemption (en is a global target here) and keep it out too.
        await SetGlobalTargetsAsync("zh", "en");
        var overridden = await SeedMovieAsync(24, "movie-queue-ovr", "zh");
        var follower = await SeedMovieAsync(25, "movie-queue-null", null, audioLanguages: ["ko"]);
        // both carry zh only — en missing matters to the follower alone
        WriteSubtitle("movie-queue-ovr", "movie24.zh.srt", 60);
        WriteSubtitle("movie-queue-null", "movie25.zh.srt", 60);

        await _refresher.RefreshAsync(MediaType.Movie, overridden.Id, CancellationToken.None);
        await _refresher.RefreshAsync(MediaType.Movie, follower.Id, CancellationToken.None);

        var queue = new SubtitleSearchQueueService(
            _dbContext, NullLogger<SubtitleSearchQueueService>.Instance);
        var enqueued = await queue.EnqueueGapMediaAsync(CancellationToken.None);
        Assert.Equal(1, enqueued); // the follower only

        var queuedIds = await _dbContext.SubtitleSearchStates.AsNoTracking()
            .Where(state => state.MediaType == MediaType.Movie)
            .Select(state => state.MediaId)
            .ToListAsync();
        Assert.Equal(new[] { follower.Id }, queuedIds);
        Assert.DoesNotContain(overridden.Id, queuedIds);

        // the explicit「无目标」state keeps a media out of the queue too
        var explicitNone = await SeedMovieAsync(26, "movie-queue-empty", string.Empty);
        await _refresher.RefreshAsync(MediaType.Movie, explicitNone.Id, CancellationToken.None);
        await queue.EnqueueGapMediaAsync(CancellationToken.None);
        var queuedAfter = await _dbContext.SubtitleSearchStates.AsNoTracking()
            .Where(state => state.MediaType == MediaType.Movie)
            .Select(state => state.MediaId)
            .ToListAsync();
        Assert.DoesNotContain(explicitNone.Id, queuedAfter);
    }

    private sealed class RefresherDbContext(DbContextOptions options) : LingarrDbContext(options);

    // ── B04: embedded tracks as coverage (setting-gated) ──────────────
    //
    // The flag is read through the settings service the refresher was built
    // with: _refresher (the shared ctor instance) carries NO settings
    // service, so it pins the flag-off regression gate regardless of what a
    // previous test stored. The B04 cases build their own settings-aware
    // refresher around the same SQLite database and temp tree.

    private SubtitleCoverageRefresher CreateRefresher(bool embeddedAsCoverage)
    {
        _settings.SetSetting(
            SettingKeys.SubtitleSearch.EmbeddedTrackCountsAsCoverage,
            embeddedAsCoverage ? "true" : "false").GetAwaiter().GetResult();
        return new SubtitleCoverageRefresher(
            _dbContext,
            new SubtitleService(
                NullLogger<SubtitleService>.Instance,
                new LanguageCodeService(),
                _settings),
            new TargetLanguageResolver(_settings),
            NullLogger<SubtitleCoverageRefresher>.Instance,
            _settings);
    }

    private async Task<Movie> SeedMovieWithEmbeddedTrackAsync(int radarrId, string relativeDir)
    {
        var directory = Path.Combine(_root, relativeDir);
        Directory.CreateDirectory(directory);
        var movie = new Movie
        {
            RadarrId = radarrId,
            Title = $"movie {radarrId}",
            Year = 2000 + radarrId,
            Path = directory,
            FileName = $"movie{radarrId}.mkv",
            DateAdded = DateTimeOffset.UtcNow,
            AudioLanguages = ["en"], // not a target: the verdict turns on zh alone
            NeedsTranslation = true
        };
        _dbContext.Movies.Add(movie);
        // save FIRST: the track row needs the database-assigned movie id
        await _dbContext.SaveChangesAsync();
        _dbContext.MediaSubtitleTracks.Add(new MediaSubtitleTrack
        {
            MediaType = MediaType.Movie,
            MediaId = movie.Id,
            StreamIndex = 3,
            Language = "zh",
            RawLanguage = "chi",
            Title = "简体中文",
            Codec = "srt",
            IsTextBased = true,
            FilePath = Path.Combine(directory, movie.FileName)
        });
        await _dbContext.SaveChangesAsync();
        return movie;
    }

    [Fact]
    public async Task RefreshAsync_MovieEmbeddedTracks_FlagOn_ExemptsTheGapQueue()
    {
        // B04 acceptance 1 (on): a movie whose container carries a zh track
        // gains the embedded coverage dimension, stops needing translation
        // and therefore never enters EnqueueGapMediaAsync's gap INSERT.
        var movie = await SeedMovieWithEmbeddedTrackAsync(31, "movie-emb-on");
        var refresher = CreateRefresher(embeddedAsCoverage: true);

        await refresher.RefreshAsync(MediaType.Movie, movie.Id, CancellationToken.None);

        var stored = await _dbContext.Movies.AsNoTracking().SingleAsync(m => m.Id == movie.Id);
        Assert.NotNull(stored.SubtitleCoverage);
        // the embedded dimension is its own slot: zh has NO file coverage
        // (Count 0) but the embedded dimension (1).
        Assert.Equal(0, stored.SubtitleCoverage!["zh"].Count);
        Assert.Equal(1, stored.SubtitleCoverage!["zh"].EmbeddedCount);
        Assert.Contains("\"embeddedCount\":1", stored.SubtitleCoverageJson);
        // the dimension satisfies the need rule while the switch is on
        Assert.False(stored.NeedsTranslation);

        var queue = new SubtitleSearchQueueService(
            _dbContext, NullLogger<SubtitleSearchQueueService>.Instance);
        var enqueued = await queue.EnqueueGapMediaAsync(CancellationToken.None);
        Assert.Equal(0, enqueued);
        var queuedIds = await _dbContext.SubtitleSearchStates.AsNoTracking()
            .Where(state => state.MediaType == MediaType.Movie)
            .Select(state => state.MediaId)
            .ToListAsync();
        Assert.DoesNotContain(movie.Id, queuedIds);
    }

    [Fact]
    public async Task RefreshAsync_MovieEmbeddedTracks_FlagOff_KeepsPreB04Behavior()
    {
        // B04 acceptance 1 (off, regression gate): the same fixture through
        // the settings-less refresher — no embedded dimension is written,
        // the JSON carries no embeddedCount key and the movie still needs
        // translation, so the gap queue DOES pick it up.
        var movie = await SeedMovieWithEmbeddedTrackAsync(32, "movie-emb-off");

        await _refresher.RefreshAsync(MediaType.Movie, movie.Id, CancellationToken.None);

        var stored = await _dbContext.Movies.AsNoTracking().SingleAsync(m => m.Id == movie.Id);
        Assert.NotNull(stored.SubtitleCoverage);
        // no files and no merge: the track changes NOTHING — zh is not even
        // a key of the snapshot, exactly the pre-B04 output.
        Assert.False(stored.SubtitleCoverage!.ContainsKey("zh"));
        Assert.DoesNotContain("embeddedCount", stored.SubtitleCoverageJson);
        Assert.True(stored.NeedsTranslation);

        var queue = new SubtitleSearchQueueService(
            _dbContext, NullLogger<SubtitleSearchQueueService>.Instance);
        await queue.EnqueueGapMediaAsync(CancellationToken.None);
        var queuedIds = await _dbContext.SubtitleSearchStates.AsNoTracking()
            .Where(state => state.MediaType == MediaType.Movie)
            .Select(state => state.MediaId)
            .ToListAsync();
        Assert.Contains(movie.Id, queuedIds);
    }

    [Fact]
    public async Task RefreshAsync_MovieGraphicTrack_AlsoCountsAsEmbeddedCoverage()
    {
        // A PGS track genuinely is a subtitle the player renders — the
        // embedded dimension counts graphic tracks too (only the EXTRACT
        // action is text-only).
        var directory = Path.Combine(_root, "movie-emb-pgs");
        Directory.CreateDirectory(directory);
        var movie = new Movie
        {
            RadarrId = 33,
            Title = "PGS Movie",
            Year = 2024,
            Path = directory,
            FileName = "movie33.mkv",
            DateAdded = DateTimeOffset.UtcNow,
            AudioLanguages = ["en"],
            NeedsTranslation = true
        };
        _dbContext.Movies.Add(movie);
        await _dbContext.SaveChangesAsync();
        _dbContext.MediaSubtitleTracks.Add(new MediaSubtitleTrack
        {
            MediaType = MediaType.Movie,
            MediaId = movie.Id,
            StreamIndex = 5,
            Language = "zh",
            RawLanguage = "chi",
            Title = null,
            Codec = "pgs",
            IsTextBased = false,
            FilePath = Path.Combine(directory, movie.FileName)
        });
        await _dbContext.SaveChangesAsync();

        await CreateRefresher(embeddedAsCoverage: true)
            .RefreshAsync(MediaType.Movie, movie.Id, CancellationToken.None);

        var stored = await _dbContext.Movies.AsNoTracking().SingleAsync(m => m.Id == movie.Id);
        Assert.Equal(1, stored.SubtitleCoverage!["zh"].EmbeddedCount);
        Assert.False(stored.NeedsTranslation);
    }

    [Fact]
    public async Task RefreshAsync_ShowEmbeddedTracks_FlagOn_CountsDistinctEpisodes()
    {
        // Show semantics: the embedded dimension counts EPISODES carrying a
        // track in the language, and every such episode's need flag flips —
        // while an episode without tracks stays a gap.
        await SetGlobalTargetsAsync("zh");
        var show = await SeedShowAsync(7, "show7/s01",
            "show7.s01e01.mkv", "show7.s01e02.mkv");
        var episodes = show.Seasons.SelectMany(s => s.Episodes).OrderBy(e => e.Id).ToList();
        _dbContext.MediaSubtitleTracks.AddRange(
            new MediaSubtitleTrack
            {
                MediaType = MediaType.Episode,
                MediaId = episodes[0].Id,
                StreamIndex = 2,
                Language = "zh",
                RawLanguage = "chi",
                Title = null,
                Codec = "srt",
                IsTextBased = true,
                FilePath = "show7.s01e01.mkv"
            },
            // two tracks in the SAME episode of the same language: still one
            // episode counted for the show dimension
            new MediaSubtitleTrack
            {
                MediaType = MediaType.Episode,
                MediaId = episodes[0].Id,
                StreamIndex = 4,
                Language = "zh",
                RawLanguage = "chi",
                Title = null,
                Codec = "srt",
                IsTextBased = true,
                FilePath = "show7.s01e01.mkv"
            });
        await _dbContext.SaveChangesAsync();

        await CreateRefresher(embeddedAsCoverage: true)
            .RefreshAsync(MediaType.Show, show.Id, CancellationToken.None);

        var storedShow = await _dbContext.Shows.AsNoTracking().SingleAsync(s => s.Id == show.Id);
        Assert.Equal(1, storedShow.SubtitleCoverage!["zh"].EmbeddedCount);
        Assert.Equal(0, storedShow.SubtitleCoverage!["zh"].Count);

        var storedEpisodes = await _dbContext.Episodes.AsNoTracking()
            .Where(e => e.Season.ShowId == show.Id)
            .OrderBy(e => e.Id)
            .ToListAsync();
        Assert.False(storedEpisodes[0].NeedsTranslation); // owns the zh track
        Assert.True(storedEpisodes[1].NeedsTranslation); // no track, still a gap
    }
}
