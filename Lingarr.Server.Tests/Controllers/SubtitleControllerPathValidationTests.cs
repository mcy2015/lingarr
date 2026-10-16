using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Lingarr.Core.Data;
using Lingarr.Core.Entities;
using Lingarr.Core.Enum;
using Lingarr.Server.Controllers;
using Lingarr.Server.Models;
using Lingarr.Server.Models.Api;
using Lingarr.Server.Models.FileSystem;
using Lingarr.Server.Modules.Subtitle;
using Lingarr.Server.Modules.Sync;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;

namespace Lingarr.Server.Tests.Controllers;

/// <summary>
/// Audit S6/P1-12: the recursive subtitle endpoints (/subtitle/all and
/// scan-path) only accept paths at or inside a library media root. Outside
/// paths get a readable 400 before the filesystem is touched; library paths
/// pass through unchanged. import-paths runs every submitted file through
/// the same whitelist (P1 bypass: it used to read any named server file).
/// </summary>
public class SubtitleControllerPathValidationTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly LingarrDbContext _context;
    private readonly Mock<ISubtitleService> _subtitleService = new();
    private readonly Mock<ISubtitleImportService> _importService = new();

    public SubtitleControllerPathValidationTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _context = new LingarrDbContext(
            new DbContextOptionsBuilder<LingarrDbContext>()
                .UseSqlite(_connection)
                .Options);
        _context.Database.EnsureCreated();

        // Mapping destinations define the whitelisted roots.
        _context.PathMappings.Add(new PathMapping
        {
            SourcePath = "/data/torrents/movies",
            DestinationPath = "/media/movies",
            MediaType = MediaType.Movie
        });
        _context.PathMappings.Add(new PathMapping
        {
            SourcePath = "/data/torrents/tv",
            DestinationPath = "/media/tv",
            MediaType = MediaType.Show
        });
        _context.SaveChanges();
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    private SubtitleController CreateController() => new(
        _subtitleService.Object,
        _importService.Object,
        new Mock<ISubtitleNormalizeService>().Object,
        new PathConversionService(_context));

    /// <summary>Reads properties off anonymous response objects in assertions.</summary>
    private static string? MessageOf(BadRequestObjectResult result) =>
        result.Value?.GetType().GetProperty("message")?.GetValue(result.Value) as string;

    [Fact]
    public async Task GetAllSubtitles_PathInsideMediaRoots_PassesThrough()
    {
        _subtitleService
            .Setup(s => s.GetAllSubtitles("/media/movies/Dune"))
            .ReturnsAsync(new List<Subtitles>
            {
                new() { Path = "/media/movies/Dune/Dune.zh.srt", FileName = "Dune.zh.srt" }
            });

        var response = await CreateController().GetAllSubtitles(
            new SubtitlePath { Path = "/media/movies/Dune" });

        var result = Assert.IsType<OkObjectResult>(response.Result);
        Assert.Single(Assert.IsType<List<Subtitles>>(result.Value));
        _subtitleService.Verify(s => s.GetAllSubtitles("/media/movies/Dune"), Times.Once);
    }

    [Fact]
    public async Task GetAllSubtitles_PathOutsideMediaRoots_Returns400WithoutScanning()
    {
        var response = await CreateController().GetAllSubtitles(
            new SubtitlePath { Path = "/etc/secrets" });

        var result = Assert.IsType<BadRequestObjectResult>(response.Result);
        Assert.Contains("outside", MessageOf(result));
        _subtitleService.Verify(s => s.GetAllSubtitles(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task GetAllSubtitles_EmptyPath_Returns400()
    {
        var response = await CreateController().GetAllSubtitles(
            new SubtitlePath { Path = "" });

        Assert.IsType<BadRequestObjectResult>(response.Result);
        _subtitleService.Verify(s => s.GetAllSubtitles(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task ScanPath_PathInsideMediaRoots_PassesThrough()
    {
        _importService
            .Setup(s => s.ScanPath("/media/tv/Show/Season 1"))
            .ReturnsAsync(new List<ScannedSubtitleFile>());

        var response = await CreateController().ScanPath(
            new SubtitlePath { Path = "/media/tv/Show/Season 1" });

        Assert.IsType<OkObjectResult>(response.Result);
        _importService.Verify(s => s.ScanPath("/media/tv/Show/Season 1"), Times.Once);
    }

    [Fact]
    public async Task ScanPath_PathOutsideMediaRoots_Returns400BeforeTouchingDisk()
    {
        // "/etc" exists on almost every host — validation must reject it with
        // 400 rather than letting the 404/200 split probe for it.
        var response = await CreateController().ScanPath(
            new SubtitlePath { Path = "/etc" });

        var result = Assert.IsType<BadRequestObjectResult>(response.Result);
        Assert.Contains("outside", MessageOf(result));
        _importService.Verify(s => s.ScanPath(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task ScanPath_NoRootsConfigured_ReturnsReadable400()
    {
        // Wipe the mappings and the library: nothing whitelisted, reject.
        _context.PathMappings.RemoveRange(_context.PathMappings);
        _context.SaveChanges();

        var response = await CreateController().ScanPath(
            new SubtitlePath { Path = "/media/movies" });

        var result = Assert.IsType<BadRequestObjectResult>(response.Result);
        Assert.Contains("No media roots", MessageOf(result));
        _importService.Verify(s => s.ScanPath(It.IsAny<string>()), Times.Never);
    }

    // ── import-paths (P1 bypass) ──────────────────────────────────

    [Fact]
    public async Task ImportPaths_AnyPathOutsideMediaRoots_Returns400WithoutImporting()
    {
        // One legit path is not enough: a single offender in the batch must
        // abort the whole request before any file is read.
        var request = new ImportPathsRequest
        {
            Paths = new List<string> { "/media/movies/Dune/Dune.zh.srt", "/etc/passwd" }
        };

        var response = await CreateController().ImportPaths(request);

        var result = Assert.IsType<BadRequestObjectResult>(response.Result);
        Assert.Contains("outside", MessageOf(result));
        Assert.Contains("/etc/passwd", MessageOf(result)); // offending path echoed
        VerifyImportPathsNeverCalled();
    }

    [Fact]
    public async Task ImportPaths_DotDotEscapeInWhitelistedPrefix_Returns400()
    {
        // P0 + P1 combined: the path textually starts inside a whitelisted
        // root, but resolves outside it — the pre-fix lexical check let this
        // through and the endpoint would have read /etc/passwd.
        var request = new ImportPathsRequest
        {
            Paths = new List<string> { "/media/movies/../../etc/passwd" }
        };

        var response = await CreateController().ImportPaths(request);

        Assert.IsType<BadRequestObjectResult>(response.Result);
        VerifyImportPathsNeverCalled();
    }

    [Fact]
    public async Task ImportPaths_AllPathsInsideMediaRoots_Imports()
    {
        var request = new ImportPathsRequest
        {
            Paths = new List<string>
            {
                "/media/movies/Dune/Dune.zh.srt",
                "/media/tv/Show/Season 1/Show.S01E01.zh.srt"
            }
        };
        _importService
            .Setup(s => s.ImportPaths(
                request.Paths, request.SourceLanguage, request.TargetLanguage,
                request.Translate, request.MediaType, request.MediaId, request.Season,
                request.ChineseConvert, request.IsBilingual))
            .ReturnsAsync(new List<ImportedSubtitleResult>());

        var response = await CreateController().ImportPaths(request);

        var result = Assert.IsType<OkObjectResult>(response.Result);
        var value = Assert.IsType<ImportSubtitlesResponse>(result.Value);
        Assert.Empty(value.Results);
        _importService.Verify(s => s.ImportPaths(
            request.Paths, request.SourceLanguage, request.TargetLanguage,
            request.Translate, request.MediaType, request.MediaId, request.Season,
            request.ChineseConvert, request.IsBilingual), Times.Once);
    }

    [Fact]
    public async Task ImportPaths_EmptyPathList_Returns400WithoutImporting()
    {
        var request = new ImportPathsRequest { Paths = new List<string>() };

        var response = await CreateController().ImportPaths(request);

        Assert.IsType<BadRequestObjectResult>(response.Result);
        VerifyImportPathsNeverCalled();
    }

    private void VerifyImportPathsNeverCalled() => _importService.Verify(s => s.ImportPaths(
        It.IsAny<List<string>>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
        It.IsAny<string?>(), It.IsAny<int?>(), It.IsAny<int?>(), It.IsAny<string?>(),
        It.IsAny<bool>()), Times.Never);
}
