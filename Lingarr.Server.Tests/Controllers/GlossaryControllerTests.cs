using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Lingarr.Core.Entities;
using Lingarr.Server.Controllers;
using Lingarr.Server.Modules.Translation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Xunit;

namespace Lingarr.Server.Tests.Controllers;

/// <summary>
/// Glossary CSV export (R9a): GET /api/glossary/export serves the exact
/// symmetric counterpart of the CSV import — UTF-8 with BOM so Excel opens
/// CJK terms directly, the source,target,mode header matching the import
/// parser verbatim (mode ∈ locked/context, the spellings BulkImport
/// accepts), standard quote escaping, and an empty glossary still yielding
/// the header row.
/// </summary>
public class GlossaryControllerTests
{
    private readonly Mock<IGlossaryService> _glossaryService = new();
    private readonly GlossaryController _controller;

    public GlossaryControllerTests()
    {
        _controller = new GlossaryController(_glossaryService.Object);
    }

    private static TranslationGlossary Entry(string source, string target, bool locked) => new()
    {
        SourceTerm = source,
        TargetTerm = target,
        IsLocked = locked
    };

    [Fact]
    public async Task Export_EmptyGlossary_YieldsBomAndHeaderRowOnly()
    {
        _glossaryService.Setup(s => s.GetAll()).ReturnsAsync([]);

        var result = await _controller.ExportCsv();

        var file = Assert.IsType<FileContentResult>(result);
        Assert.Equal("text/csv", file.ContentType);
        Assert.Equal("lingarr-glossary.csv", file.FileDownloadName);
        // UTF-8 BOM first — Excel opens CJK terms without the import wizard
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, file.FileContents.Take(3).ToArray());
        Assert.Equal("\uFEFFsource,target,mode\r\n", Encoding.UTF8.GetString(file.FileContents));
        _glossaryService.Verify(s => s.GetAll(), Times.Once);
    }

    [Fact]
    public async Task Export_Entries_MapsModeAndEscapesCommasAndQuotes()
    {
        _glossaryService.Setup(s => s.GetAll()).ReturnsAsync(
        [
            Entry("Ye Wenjie", "叶文洁", locked: true),
            Entry("Wall, facer", "面壁者", locked: false),
            Entry("say \"hi\"", "说“你好”", locked: true)
        ]);

        var result = await _controller.ExportCsv();

        var file = Assert.IsType<FileContentResult>(result);
        // Every row below re-imports losslessly: the mode spellings are the
        // canonical locked/context BulkImport accepts, and fields carrying
        // commas/quotes are wrapped with doubled inner quotes exactly as
        // the import parser unescapes them.
        Assert.Equal(
            "\uFEFFsource,target,mode\r\n" +
            "Ye Wenjie,叶文洁,locked\r\n" +
            "\"Wall, facer\",面壁者,context\r\n" +
            "\"say \"\"hi\"\"\",说“你好”,locked\r\n",
            Encoding.UTF8.GetString(file.FileContents));
    }

    [Fact]
    public void Export_IsGuardedByTheControllerLingarrAccessPolicy()
    {
        // The export rides the class-level LingarrAccess policy like every
        // other glossary endpoint — no extra attribute, no AllowAnonymous hole.
        var controllerType = typeof(GlossaryController);
        Assert.Contains(controllerType.GetCustomAttributes(true),
            a => a is AuthorizeAttribute authorize && authorize.Policy == "LingarrAccess");

        var method = controllerType.GetMethod(nameof(GlossaryController.ExportCsv));
        Assert.NotNull(method);
        Assert.DoesNotContain(method.GetCustomAttributes(true), a => a is AllowAnonymousAttribute);
    }
}
