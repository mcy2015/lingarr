using System.Collections.Generic;
using Lingarr.Server.Controllers;
using Lingarr.Server.Interfaces.Services;
using Lingarr.Server.Models.Api;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Xunit;

namespace Lingarr.Server.Tests.Controllers;

public class SubtitleControllerParseTests
{
    [Fact]
    public void ParseFileNames_MapsParserOutputWithTierNames()
    {
        var controller = new SubtitleController(
            new Mock<ISubtitleService>().Object,
            new Mock<ISubtitleImportService>().Object);

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
        var controller = new SubtitleController(
            new Mock<ISubtitleService>().Object,
            new Mock<ISubtitleImportService>().Object);

        var response = controller.ParseFileNames(new SubtitleFilenameParseRequest { Filenames = [] });

        var result = Assert.IsType<OkObjectResult>(response.Result);
        Assert.Empty(Assert.IsType<List<SubtitleFilenameParseResult>>(result.Value));
    }
}
