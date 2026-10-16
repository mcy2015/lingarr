using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Lingarr.Core.Data;
using Lingarr.Core.Entities;
using Lingarr.Core.Enum;
using Lingarr.Server.Modules.Sync;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Caching.Memory;
using Xunit;

namespace Lingarr.Server.Tests.Modules.Sync;

/// <summary>
/// Audit S6: the mapping lookup behind every path conversion is memoized in
/// the shared memory cache (short TTL — PathMapping writes go through
/// MappingService, a channel without a change event to hook), and the media
/// root whitelist for the recursive subtitle endpoints derives from mapping
/// destinations or, absent those, the library directories.
/// </summary>
public class PathConversionServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly QueryCounter _counter;
    private readonly LingarrDbContext _context;
    private readonly MemoryCache _cache;

    public PathConversionServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _counter = new QueryCounter();
        var options = new DbContextOptionsBuilder<LingarrDbContext>()
            .UseSqlite(_connection)
            .AddInterceptors(_counter)
            .Options;
        _context = new LingarrDbContext(options);
        _context.Database.EnsureCreated();
        _cache = new MemoryCache(new MemoryCacheOptions());
    }

    public void Dispose()
    {
        _cache.Dispose();
        _context.Dispose();
        _connection.Dispose();
    }

    private PathConversionService CreateService() => new(_context, _cache);

    private async Task SeedMappingAsync(
        string source, string destination, MediaType mediaType)
    {
        _context.PathMappings.Add(new PathMapping
        {
            SourcePath = source, DestinationPath = destination, MediaType = mediaType
        });
        await _context.SaveChangesAsync();
    }

    // ── mapping cache ─────────────────────────────────────────────

    [Fact]
    public async Task ConvertAndMapPath_RepeatedConversions_QueryMappingsOnce()
    {
        await SeedMappingAsync("/data/torrents/movies", "/media/movies", MediaType.Movie);

        var service = CreateService();
        var first = service.ConvertAndMapPath("/data/torrents/movies/Dune/Dune.mkv", MediaType.Movie);
        var second = service.ConvertAndMapPath("/data/torrents/movies/Blade/Blade.mkv", MediaType.Movie);

        Assert.Equal("/media/movies/Dune/Dune.mkv", first);
        Assert.Equal("/media/movies/Blade/Blade.mkv", second);
        Assert.Equal(1, _counter.MappingQueries);
    }

    [Fact]
    public async Task MappingCache_EntryIsMemoizedPerMediaType()
    {
        await SeedMappingAsync("/data/torrents/movies", "/media/movies", MediaType.Movie);
        await SeedMappingAsync("/data/torrents/tv", "/media/tv", MediaType.Show);

        var service = CreateService();
        service.ConvertAndMapPath("/data/torrents/movies/Dune.mkv", MediaType.Movie);
        service.ConvertAndMapPath("/data/torrents/movies/Blade.mkv", MediaType.Movie);
        service.ConvertAndMapPath("/data/torrents/tv/Show", MediaType.Show);
        service.ConvertAndMapPath("/data/torrents/tv/Other", MediaType.Show);

        // One query per media type, then cached separately.
        Assert.Equal(2, _counter.MappingQueries);
        Assert.True(_cache.TryGetValue(PathConversionService.MappingCacheKey(MediaType.Movie), out _));
        Assert.True(_cache.TryGetValue(PathConversionService.MappingCacheKey(MediaType.Show), out _));
    }

    [Fact]
    public async Task MappingCache_MissAfterRemoval_RequiresAndServesFreshData()
    {
        await SeedMappingAsync("/old/source", "/old/target", MediaType.Movie);
        var service = CreateService();
        Assert.Equal("/old/target/a.mkv", service.ConvertAndMapPath("/old/source/a.mkv", MediaType.Movie));
        Assert.Equal(1, _counter.MappingQueries);

        // Simulate the TTL expiring (an expired entry is exactly a cache miss)
        // while an admin rewrites the mappings.
        _context.PathMappings.Remove(_context.PathMappings.Single(m => m.MediaType == MediaType.Movie));
        _context.PathMappings.Add(new PathMapping
        {
            SourcePath = "/new/source", DestinationPath = "/new/target", MediaType = MediaType.Movie
        });
        await _context.SaveChangesAsync();
        _cache.Remove(PathConversionService.MappingCacheKey(MediaType.Movie));

        // Delta assertions: the rewrite above ran its own SELECT, so compare
        // before/after instead of absolute counts.
        var queriesBefore = _counter.MappingQueries;
        var converted = service.ConvertAndMapPath("/new/source/a.mkv", MediaType.Movie);

        Assert.Equal(queriesBefore + 1, _counter.MappingQueries);
        Assert.Equal("/new/target/a.mkv", converted);
    }

    [Fact]
    public async Task MappingCache_WithoutCache_FallsBackToDirectQueries()
    {
        // A null cache (optional dependency) must degrade to the old behaviour.
        await SeedMappingAsync("/s", "/d", MediaType.Movie);

        var service = new PathConversionService(_context);
        service.ConvertAndMapPath("/s/a.mkv", MediaType.Movie);
        service.ConvertAndMapPath("/s/b.mkv", MediaType.Movie);

        Assert.Equal(2, _counter.MappingQueries);
    }

    // ── media roots ───────────────────────────────────────────────

    [Fact]
    public async Task ValidatePathInsideMediaRoots_WithMappings_UnionsMappingDestinationsAndLibraryRoots()
    {
        await SeedMappingAsync("/data/torrents/movies", "/media/movies", MediaType.Movie);
        // A partial mapping install: this movie lives outside the mapped root
        // (shows direct, movies mapped). The union keeps the unmapped side
        // scannable instead of rejecting it.
        _context.Movies.Add(new Movie
        {
            RadarrId = 1, Title = "Dune", Path = "/srv/media/dune", FileName = "dune",
            DateAdded = DateTimeOffset.UtcNow
        });
        await _context.SaveChangesAsync();

        var service = CreateService();

        Assert.Null(await service.ValidatePathInsideMediaRootsAsync("/media/movies"));
        Assert.Null(await service.ValidatePathInsideMediaRootsAsync("/media/movies/Dune"));
        // library-derived root (parent of /srv/media/dune)
        Assert.Null(await service.ValidatePathInsideMediaRootsAsync("/srv/media/dune"));
        // still bounded: sibling prefixes and unrelated trees stay rejected
        Assert.NotNull(await service.ValidatePathInsideMediaRootsAsync("/media/movies2/Dune"));
        Assert.NotNull(await service.ValidatePathInsideMediaRootsAsync("/opt/elsewhere"));
    }

    [Fact]
    public async Task ValidatePathInsideMediaRoots_WithoutMappings_UsesLibraryParentDirectories()
    {
        var show = new Show
        {
            SonarrId = 1, Title = "Show", Path = "/data/media/tv/Show", DateAdded = DateTimeOffset.UtcNow
        };
        var season = new Season { SeasonNumber = 1, Show = show };
        show.Seasons.Add(season);
        season.Episodes.Add(new Episode
        {
            SonarrId = 5, EpisodeNumber = 5, Title = "Pilot",
            Path = "/data/media/tv/Show/Season 1", FileName = "Show.S01E05",
            Season = season
        });
        _context.Movies.Add(new Movie
        {
            RadarrId = 1, Title = "Dune", Path = "/data/media/movies/Dune", FileName = "Dune",
            DateAdded = DateTimeOffset.UtcNow
        });
        _context.Shows.Add(show);
        await _context.SaveChangesAsync();

        var service = CreateService();

        // Roots are the parents of every library path: movies root, show dir,
        // season dir. Everything at or under them is allowed.
        Assert.Null(await service.ValidatePathInsideMediaRootsAsync("/data/media/movies"));
        Assert.Null(await service.ValidatePathInsideMediaRootsAsync("/data/media/movies/Dune/sub.srt"));
        Assert.Null(await service.ValidatePathInsideMediaRootsAsync("/data/media/tv/Show/Season 1"));

        // Outside the library, the sibling-prefix trap and the empty case.
        Assert.NotNull(await service.ValidatePathInsideMediaRootsAsync("/data/media/movies2"));
        Assert.NotNull(await service.ValidatePathInsideMediaRootsAsync("/etc"));
        Assert.NotNull(await service.ValidatePathInsideMediaRootsAsync(""));
    }

    [Fact]
    public async Task ValidatePathInsideMediaRoots_NormalizesWindowsStylePaths()
    {
        await SeedMappingAsync("C:\\torrents\\movies", "/media/movies", MediaType.Movie);
        var service = CreateService();

        Assert.Null(await service.ValidatePathInsideMediaRootsAsync("\\media\\movies\\Dune"));
        Assert.Null(await service.ValidatePathInsideMediaRootsAsync("/media/movies/Dune"));
        Assert.NotNull(await service.ValidatePathInsideMediaRootsAsync("D:\\elsewhere"));
    }

    [Fact]
    public async Task ValidatePathInsideMediaRoots_EmptyLibraryAndNoMappings_RejectsEverything()
    {
        var service = CreateService();

        var rejection = await service.ValidatePathInsideMediaRootsAsync("/any/path");

        Assert.NotNull(rejection);
        Assert.Contains("No media roots", rejection!);
    }

    // ── traversal (P0): resolved vs lexical comparison ────────────

    [Fact]
    public async Task ValidatePathInsideMediaRoots_DotDotTraversal_IsRejectedAfterResolution()
    {
        await SeedMappingAsync("/data/torrents/movies", "/media/movies", MediaType.Movie);
        var service = CreateService();

        // Every vector below starts with the whitelisted prefix "/media/movies",
        // so the old lexical prefix check waved each one through — the OS then
        // resolved the ".." during enumeration and escaped the whitelist.
        // Resolved, none of them lands at/inside the root.
        Assert.NotNull(await service.ValidatePathInsideMediaRootsAsync("/media/movies/../../etc"));
        Assert.NotNull(await service.ValidatePathInsideMediaRootsAsync("/media/movies/.."));
        Assert.NotNull(await service.ValidatePathInsideMediaRootsAsync("/media/movies/sub/../../.."));
        Assert.NotNull(await service.ValidatePathInsideMediaRootsAsync("/media/movies//../.."));
        Assert.NotNull(await service.ValidatePathInsideMediaRootsAsync("/media/movies/Dune/../../../srv"));
    }

    [Fact]
    public async Task ValidatePathInsideMediaRoots_RedundantSegments_ResolveInsideAndPass()
    {
        await SeedMappingAsync("/data/torrents/movies", "/media/movies", MediaType.Movie);
        var service = CreateService();

        // ".", duplicate separators, trailing "/." and a trailing slash are
        // not traversal — resolved they stay at/inside the root and must keep
        // working (the fix must not over-reject harmless spellings).
        Assert.Null(await service.ValidatePathInsideMediaRootsAsync("/media/movies"));
        Assert.Null(await service.ValidatePathInsideMediaRootsAsync("/media/movies/"));
        Assert.Null(await service.ValidatePathInsideMediaRootsAsync("/media/movies/."));
        Assert.Null(await service.ValidatePathInsideMediaRootsAsync("/media/movies/./Dune"));
        Assert.Null(await service.ValidatePathInsideMediaRootsAsync("/media/movies//Dune"));
        Assert.Null(await service.ValidatePathInsideMediaRootsAsync("/media/movies/Dune/."));
    }

    [Fact]
    public async Task ValidatePathInsideMediaRoots_InputGetFullPathRejects_ReturnsInvalidPathError()
    {
        await SeedMappingAsync("/data/torrents/movies", "/media/movies", MediaType.Movie);
        var service = CreateService();

        // A NUL byte can never be a real path component; it must be rejected
        // as invalid rather than passed on to the filesystem layer.
        var rejection = await service.ValidatePathInsideMediaRootsAsync("/media/movies/Du\0ne");

        Assert.NotNull(rejection);
        Assert.Contains("not a valid path", rejection!);
    }

    // ── media roots cache (QA5 P2-4) ──────────────────────────────

    [Fact]
    public async Task MediaRootsCache_RepeatedValidations_QueryLibraryTablesOnce()
    {
        _context.Movies.Add(new Movie
        {
            RadarrId = 1, Title = "Dune", Path = "/data/media/movies/Dune", FileName = "Dune",
            DateAdded = DateTimeOffset.UtcNow
        });
        await _context.SaveChangesAsync();

        var service = CreateService();
        Assert.Null(await service.ValidatePathInsideMediaRootsAsync("/data/media/movies"));
        var queriesAfterFirst = _counter.MediaRootQueries;

        // The first validation really hit the library tables (movies + shows
        // + episodes), not an empty pass.
        Assert.True(queriesAfterFirst > 0);

        // Any number of further validations — pass or reject — is served
        // from the cache: no additional library queries.
        Assert.Null(await service.ValidatePathInsideMediaRootsAsync("/data/media/movies/Dune"));
        Assert.NotNull(await service.ValidatePathInsideMediaRootsAsync("/etc"));

        Assert.Equal(queriesAfterFirst, _counter.MediaRootQueries);
        Assert.True(_cache.TryGetValue(PathConversionService.MediaRootsCacheKey, out _));
    }

    [Fact]
    public async Task MediaRootsCache_WithoutCache_RequeriesLibraryTables()
    {
        // A null cache (optional dependency) must degrade to the old behaviour.
        _context.Movies.Add(new Movie
        {
            RadarrId = 1, Title = "Dune", Path = "/data/media/movies/Dune", FileName = "Dune",
            DateAdded = DateTimeOffset.UtcNow
        });
        await _context.SaveChangesAsync();

        var service = new PathConversionService(_context);
        Assert.Null(await service.ValidatePathInsideMediaRootsAsync("/data/media/movies"));
        Assert.Null(await service.ValidatePathInsideMediaRootsAsync("/data/media/movies/Dune"));

        // Two validations × three library tables, no caching.
        Assert.Equal(6, _counter.MediaRootQueries);
    }

    [Fact]
    public async Task MediaRootsCache_MissAfterRemoval_ServesFreshRoots()
    {
        _context.Movies.Add(new Movie
        {
            RadarrId = 1, Title = "Dune", Path = "/data/media/movies/Dune", FileName = "Dune",
            DateAdded = DateTimeOffset.UtcNow
        });
        await _context.SaveChangesAsync();

        var service = CreateService();
        Assert.NotNull(await service.ValidatePathInsideMediaRootsAsync("/srv/other"));

        // Simulate the TTL expiring while a second library got synced: the
        // fresh computation must pick the new root up.
        _context.Movies.Add(new Movie
        {
            RadarrId = 2, Title = "Blade", Path = "/srv/other/Blade", FileName = "Blade",
            DateAdded = DateTimeOffset.UtcNow
        });
        await _context.SaveChangesAsync();
        _cache.Remove(PathConversionService.MediaRootsCacheKey);

        Assert.Null(await service.ValidatePathInsideMediaRootsAsync("/srv/other/Blade"));
    }

    /// <summary>
    /// Counts SELECT statements against the mapping and library tables.
    /// Gated on the SELECT prefix so EnsureCreated DDL and seed INSERTs are
    /// not miscounted.
    /// </summary>
    private sealed class QueryCounter : DbCommandInterceptor
    {
        public int MappingQueries;
        public int MediaRootQueries;

        private void Count(DbCommand command)
        {
            if (!command.CommandText.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (command.CommandText.Contains("PathMappings", StringComparison.OrdinalIgnoreCase))
            {
                MappingQueries++;
            }
            else if (command.CommandText.Contains("Movies", StringComparison.OrdinalIgnoreCase)
                || command.CommandText.Contains("Shows", StringComparison.OrdinalIgnoreCase)
                || command.CommandText.Contains("Episodes", StringComparison.OrdinalIgnoreCase))
            {
                MediaRootQueries++;
            }
        }

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result)
        {
            Count(command);
            return result;
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Count(command);
            return ValueTask.FromResult(result);
        }
    }
}
