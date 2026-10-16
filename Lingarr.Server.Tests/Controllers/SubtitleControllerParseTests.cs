using System;
using System.Collections.Generic;
using Lingarr.Core.Data;
using Lingarr.Server.Controllers;
using Lingarr.Server.Models.Api;
using Lingarr.Server.Modules.Sync;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;
using Lingarr.Server.Modules.Subtitle;

namespace Lingarr.Server.Tests.Controllers;

public class SubtitleControllerParseTests
{
    [Fact]
    public void ParseFileNames_MapsParserOutputWithTierNames()
    {
        var controller = CreateController();

        var response = controller.ParseFileNames(new SubtitleFilenameParseRequest
        {
            Filenames =
            [
                "Shōgun.S01E05.chs.ass",
                "幕府将军.第8集.简体.srt",
                "Show.S01.complete.ass",
                "Blade.Runner.2049.chs.srt"
            ]
        });

        var result = Assert.IsType<OkObjectResult>(response.Result);
        var results = Assert.IsType<List<SubtitleFilenameParseResult>>(result.Value);

        Assert.Equal(4, results.Count);
        Assert.Equal("Exact", results[0].MatchTier);
        Assert.Equal(1, results[0].Season);
        Assert.Equal(5, results[0].Episode);
        Assert.Equal("zh-Hans", Assert.Single(results[0].LanguageTags));

        Assert.Equal("Exact", results[1].MatchTier);
        Assert.Null(results[1].Season);
        Assert.Equal(8, results[1].Episode);

        Assert.Equal("Guess", results[2].MatchTier);
        Assert.Null(results[2].Episode);

        Assert.Equal("None", results[3].MatchTier);
        Assert.Equal(["zh-Hans"], results[3].LanguageTags);
    }

    [Fact]
    public void ParseFileNames_EmptyList_ReturnsEmpty()
    {
        var controller = CreateController();

        var response = controller.ParseFileNames(new SubtitleFilenameParseRequest { Filenames = [] });

        var result = Assert.IsType<OkObjectResult>(response.Result);
        Assert.Empty(Assert.IsType<List<SubtitleFilenameParseResult>>(result.Value));
    }

    private static SubtitleController CreateController() => new(
        new Mock<ISubtitleService>().Object,
        new Mock<ISubtitleImportService>().Object,
        new Mock<ISubtitleNormalizeService>().Object,
        new PathConversionService(
            new LingarrDbContext(
                new DbContextOptionsBuilder<LingarrDbContext>()
                    .UseInMemoryDatabase(Guid.NewGuid().ToString())
                    .Options)),
        NullLogger<SubtitleController>.Instance);
}
