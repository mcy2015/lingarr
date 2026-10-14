using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Lingarr.Core.Data;
using Lingarr.Core.Entities;
using Lingarr.Core.Enum;
using Lingarr.Server.Controllers;
using Lingarr.Server.Modules.Media;
using Lingarr.Server.Modules.Sync;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Lingarr.Server.Tests.Services;

/// <summary>
/// Path-mapping ground truth: the status endpoint probes the stored media
/// directories, the test endpoint answers through the same
/// PathConversionService the sync pipeline uses, and a mapping edit takes
/// effect on the very next conversion (no cache to invalidate).
/// </summary>
public class PathMappingHealthTests : IDisposable
{
    private readonly LingarrDbContext _context;
    private readonly string _tempRoot;

    public PathMappingHealthTests()
    {
        var options = new DbContextOptionsBuilder<LingarrDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _context = new LingarrDbContext(options);

        _tempRoot = Path.Combine(Path.GetTempPath(), "lingarr-maptest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_tempRoot, "movies", "Foo (2024)"));
    }

    public void Dispose()
    {
        _context.Dispose();
        try
        {
            Directory.Delete(_tempRoot, true);
        }
        catch
        {
            // temp cleanup is best-effort
        }
        GC.SuppressFinalize(this);
    }

    private PathConversionService Conversion => new(_context);

    // ── GetMappingStatus ─────────────────────────────────────────────

    [Fact]
    public async Task Status_FlagsMissingDirectoriesDistinctPerMediaType()
    {
        var reachable = Path.Combine(_tempRoot, "movies");
        _context.Movies.Add(new Movie
        {
            RadarrId = 1,
            Title = "A",
            Path = reachable,
            FileName = "a",
            DateAdded = DateTimeOffset.UtcNow
        });
        _context.Movies.Add(new Movie
        {
            RadarrId = 2,
            Title = "B",
            Path = reachable,
            FileName = "b",
            DateAdded = DateTimeOffset.UtcNow
        });
        var show = new Show { SonarrId = 1, Title = "S", Path = "/nope", DateAdded = DateTimeOffset.UtcNow };
        var season = new Season { SeasonNumber = 1, Path = "/nope/S01", Show = show };
        season.Episodes.Add(new Episode
        {
            SonarrId = 1,
            EpisodeNumber = 1,
            Title = "E1",
            Path = "/nope/show-1",
            Season = season
        });
        season.Episodes.Add(new Episode
        {
            SonarrId = 2,
            EpisodeNumber = 2,
            Title = "E2",
            Path = "/nope/show-1",
            Season = season
        });
        show.Seasons.Add(season);
        _context.Shows.Add(show);
        await _context.SaveChangesAsync();

        var service = new MappingService(_context);
        var status = await service.GetMappingStatus();

        // directory-level dedup: two movies collapse to the reachable dir,
        // two episodes collapse to the missing dir (show/season paths are not
        // subtitle write targets and are not probed)
        Assert.Equal(2, status.Checked);
        var unreachable = Assert.Single(status.Unreachable);
        Assert.Equal("show", unreachable.MediaType);
        Assert.Equal("/nope/show-1", unreachable.Path);
    }

    [Fact]
    public async Task Status_EmptyLibrary_ReportsNothingUnreachable()
    {
        var service = new MappingService(_context);
        var status = await service.GetMappingStatus();

        Assert.Equal(0, status.Checked);
        Assert.Empty(status.Unreachable);
    }

    // ── TryConvertAndMapPath / test endpoint verdicts ────────────────

    [Fact]
    public async Task Test_HitAndExists_OkVerdict()
    {
        _context.PathMappings.Add(new PathMapping
        {
            SourcePath = "/data/movies",
            DestinationPath = Path.Combine(_tempRoot, "movies"),
            MediaType = MediaType.Movie
        });
        await _context.SaveChangesAsync();

        var controller = new MappingController(
            new MappingService(_context), Conversion);

        var action = controller.TestMapping(new Lingarr.Server.Models.Api.TestMappingRequest
        {
            Path = "/data/movies/Foo (2024)",
            MediaType = "movie"
        });

        var ok = Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(action);
        var result = Assert.IsType<Lingarr.Server.Models.Api.TestMappingResult>(ok.Value!);
        Assert.True(result.Ok);
        Assert.True(result.Matched);
        Assert.True(result.Exists);
        Assert.Equal(NormalizeToOs(Path.Combine(_tempRoot, "movies", "Foo (2024)")), result.MappedPath);
    }

    [Fact]
    public async Task Test_HitButMissingDirectory_FailsWithExistsFalse()
    {
        _context.PathMappings.Add(new PathMapping
        {
            SourcePath = "/data/movies",
            DestinationPath = Path.Combine(_tempRoot, "nowhere"),
            MediaType = MediaType.Movie
        });
        await _context.SaveChangesAsync();

        var controller = new MappingController(
            new MappingService(_context), Conversion);

        var action = controller.TestMapping(new Lingarr.Server.Models.Api.TestMappingRequest
        {
            Path = "/data/movies/Foo (2024)",
            MediaType = "movie"
        });

        var ok = Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(action);
        var result = Assert.IsType<Lingarr.Server.Models.Api.TestMappingResult>(ok.Value!);
        Assert.False(result.Ok);
        Assert.True(result.Matched);
        Assert.False(result.Exists);
    }

    [Fact]
    public async Task Test_NoMappingHit_FailsWithMatchedFalse()
    {
        _context.PathMappings.Add(new PathMapping
        {
            SourcePath = "/data/movies",
            DestinationPath = Path.Combine(_tempRoot, "movies"),
            MediaType = MediaType.Movie
        });
        await _context.SaveChangesAsync();

        var controller = new MappingController(
            new MappingService(_context), Conversion);

        var action = controller.TestMapping(new Lingarr.Server.Models.Api.TestMappingRequest
        {
            Path = "/other/root/Foo",
            MediaType = "movie"
        });

        var ok = Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(action);
        var result = Assert.IsType<Lingarr.Server.Models.Api.TestMappingResult>(ok.Value!);
        Assert.False(result.Ok);
        Assert.False(result.Matched);
        Assert.False(result.Exists);
        // unmatched input still comes back normalized (windows → posix)
        Assert.Equal("/other/root/Foo", result.MappedPath);
    }

    [Fact]
    public async Task Test_MediaTypeIsScopedToItsMappings()
    {
        _context.PathMappings.Add(new PathMapping
        {
            SourcePath = "/data/movies",
            DestinationPath = Path.Combine(_tempRoot, "movies"),
            MediaType = MediaType.Movie
        });
        await _context.SaveChangesAsync();

        var controller = new MappingController(
            new MappingService(_context), Conversion);

        // the movie mapping must not answer for show paths
        var action = controller.TestMapping(new Lingarr.Server.Models.Api.TestMappingRequest
        {
            Path = "/data/movies/Show",
            MediaType = "show"
        });

        var ok = Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(action);
        var result = Assert.IsType<Lingarr.Server.Models.Api.TestMappingResult>(ok.Value!);
        Assert.False(result.Matched);
    }

    [Fact]
    public async Task Test_UnknownMediaTypeOrEmptyPath_Returns400()
    {
        var controller = new MappingController(Mock.Of<IMappingService>(), Conversion);

        var badType = controller.TestMapping(new Lingarr.Server.Models.Api.TestMappingRequest
        {
            Path = "/x",
            MediaType = "season"
        });
        Assert.IsType<Microsoft.AspNetCore.Mvc.BadRequestObjectResult>(badType);

        var emptyPath = controller.TestMapping(new Lingarr.Server.Models.Api.TestMappingRequest
        {
            Path = " ",
            MediaType = "movie"
        });
        Assert.IsType<Microsoft.AspNetCore.Mvc.BadRequestObjectResult>(emptyPath);
    }

    // ── 生效性回归:改映射下一次调用即生效(无缓存) ──────────────────

    [Fact]
    public async Task EditingAMapping_AppliesOnTheVeryNextConversion()
    {
        var mapping = new PathMapping
        {
            SourcePath = "/data/movies",
            DestinationPath = "/first",
            MediaType = MediaType.Movie
        };
        _context.PathMappings.Add(mapping);
        await _context.SaveChangesAsync();

        var conversion = Conversion;
        Assert.Equal("/first/Foo", conversion.ConvertAndMapPath("/data/movies/Foo", MediaType.Movie));

        mapping.DestinationPath = "/second";
        _context.PathMappings.Update(mapping);
        await _context.SaveChangesAsync();

        Assert.Equal("/second/Foo", conversion.ConvertAndMapPath("/data/movies/Foo", MediaType.Movie));
    }

    /// <summary>ConvertAndMapPath and TryConvertAndMapPath must agree (one code path).</summary>
    [Fact]
    public void TryConvert_AndConvert_ProduceTheSamePath()
    {
        var path = "\\data\\movies\\Foo";
        var tried = Conversion.TryConvertAndMapPath(path, MediaType.Movie);

        Assert.Equal(Conversion.ConvertAndMapPath(path, MediaType.Movie), tried.MappedPath);
        Assert.Equal("/data/movies/Foo", tried.MappedPath);
    }

    private static string NormalizeToOs(string path) => path.Replace('/', Path.DirectorySeparatorChar);
}
