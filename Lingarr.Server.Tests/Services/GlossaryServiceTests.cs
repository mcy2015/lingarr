using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Lingarr.Core.Data;
using Lingarr.Core.Entities;
using Lingarr.Server.Models.Api;
using Lingarr.Server.Models.FileSystem;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Lingarr.Server.Modules.Translation;

namespace Lingarr.Server.Tests.Services;

public class GlossaryServiceTests : IDisposable
{
    private readonly LingarrDbContext _dbContext;

    public GlossaryServiceTests()
    {
        _dbContext = new LingarrDbContext(new DbContextOptionsBuilder<LingarrDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
    }

    public void Dispose() => _dbContext.Database.EnsureDeleted();

    private GlossaryService CreateService() => new(_dbContext);

    private async Task SeedAsync(params (string Source, string Target, bool Locked)[] entries)
    {
        _dbContext.TranslationGlossaries.AddRange(entries.Select(e => new TranslationGlossary
        {
            SourceTerm = e.Source,
            TargetTerm = e.Target,
            IsLocked = e.Locked
        }));
        await _dbContext.SaveChangesAsync();
    }

    [Fact]
    public async Task ApplyLockedTerms_AppendsMissingTargetToFirstLine()
    {
        await SeedAsync(("Ye Wenjie", "叶文洁", true));
        var items = new List<SubtitleItem>
        {
            new()
            {
                Lines = ["Ye Wenjie counted the waves."],
                PlaintextLines = ["Ye Wenjie counted the waves."],
                TranslatedLines = ["叶教授数过海浪。"]
            }
        };

        var changed = await CreateService().ApplyLockedTerms(items);

        Assert.Equal(1, changed);
        Assert.Contains("叶文洁", items[0].TranslatedLines[0]);
        Assert.StartsWith("叶教授数过海浪。", items[0].TranslatedLines[0]);
    }

    [Fact]
    public async Task ApplyLockedTerms_SkipsCorrectAndUnlockedEntries()
    {
        await SeedAsync(
            ("Wallfacer", "面壁者", true),
            ("sophon", "智子", false));
        var items = new List<SubtitleItem>
        {
            new()
            {
                Lines = ["The Wallfacer project"],
                PlaintextLines = ["The Wallfacer project"],
                TranslatedLines = ["面壁者计划"]
            },
            new()
            {
                Lines = ["A sophon unfolded"],
                PlaintextLines = ["A sophon unfolded"],
                TranslatedLines = ["一颗质子展开了"]
            }
        };

        var changed = await CreateService().ApplyLockedTerms(items);

        Assert.Equal(0, changed);
        Assert.Equal("面壁者计划", items[0].TranslatedLines[0]);
        Assert.Equal("一颗质子展开了", items[1].TranslatedLines[0]);
    }

    [Fact]
    public async Task Crud_RoundTripsEntries()
    {
        var service = CreateService();
        var created = await service.Create(new TranslationGlossary
        {
            SourceTerm = "dark forest",
            TargetTerm = "黑暗森林",
            IsLocked = true
        });
        Assert.True(created.Id > 0);

        created.Note = "trilogy term";
        var updated = await service.Update(created.Id, created);
        Assert.Equal("trilogy term", updated?.Note);

        Assert.True(await service.Delete(created.Id));
        Assert.Empty(await service.GetAll());
    }

    [Fact]
    public async Task FindHits_MatchesCaseInsensitive()
    {
        await SeedAsync(("Ye Wenjie", "叶文洁", true), ("Wallfacer", "面壁者", false));

        var hits = await CreateService().FindHits("yE wEnjiE appears here");

        Assert.Single(hits);
        Assert.Equal("叶文洁", hits[0].TargetTerm);
    }

    // ===== Bulk CSV import (prototype screen-06 C「导入 CSV」) =====

    private static GlossaryBulkEntry Entry(string? source, string? target, string? mode = null) =>
        new() { SourceTerm = source, TargetTerm = target, MatchMode = mode };

    [Fact]
    public async Task BulkImport_CreatesValidRowsAndMapsMatchMode()
    {
        var result = await CreateService().BulkImport(new GlossaryBulkRequest
        {
            Entries =
            [
                Entry("Ye Wenjie", "叶文洁", "locked"),
                Entry("Wallfacer", "面壁者", "CONTEXT"),
                Entry("Sophon", "智子")
            ]
        });

        Assert.Equal(3, result.Imported);
        Assert.Equal(0, result.Skipped);

        var all = await _dbContext.TranslationGlossaries.OrderBy(g => g.SourceTerm).ToListAsync();
        Assert.Equal(3, all.Count);
        Assert.Contains(all, g => g.SourceTerm == "Ye Wenjie" && g.TargetTerm == "叶文洁" && g.IsLocked);
        // match mode is case-insensitive; a missing mode means advisory
        Assert.Contains(all, g => g.SourceTerm == "Wallfacer" && !g.IsLocked);
        Assert.Contains(all, g => g.SourceTerm == "Sophon" && !g.IsLocked);
    }

    [Fact]
    public async Task BulkImport_SkipsInvalidRows()
    {
        var result = await CreateService().BulkImport(new GlossaryBulkRequest
        {
            Entries =
            [
                Entry("  ", "target"),                 // blank source
                Entry("source", ""),                   // blank target
                Entry("mode term", "译名", "garbage"),  // unknown match mode
                Entry("valid", "有效")                  // still imported
            ]
        });

        Assert.Equal(1, result.Imported);
        Assert.Equal(3, result.Skipped);
        Assert.Single(await _dbContext.TranslationGlossaries.ToListAsync());
    }

    [Fact]
    public async Task BulkImport_UpdatesStoredSourceTermsAndSkipsBatchDuplicates()
    {
        await SeedAsync(("Ye Wenjie", "叶文洁", false));

        var result = await CreateService().BulkImport(new GlossaryBulkRequest
        {
            Entries =
            [
                Entry("ye wenjie", "叶文洁(改)", "locked"), // stored row: upsert keeps identity
                Entry("Wallfacer", "面壁者", "locked"),
                Entry("WALLFACER", "面壁者2", "locked")     // in-batch duplicate: skipped
            ]
        });

        Assert.Equal(2, result.Imported);
        Assert.Equal(1, result.Skipped);

        var all = await _dbContext.TranslationGlossaries.OrderBy(g => g.SourceTerm).ToListAsync();
        Assert.Equal(2, all.Count);

        var ye = all.Single(g => g.SourceTerm == "Ye Wenjie");
        Assert.Equal("叶文洁(改)", ye.TargetTerm);
        Assert.True(ye.IsLocked);

        // first batch row wins for the duplicated source
        var wall = all.Single(g => g.SourceTerm == "Wallfacer");
        Assert.Equal("面壁者", wall.TargetTerm);
    }

    [Fact]
    public async Task BulkImport_EmptyRequest_ImportsNothing()
    {
        var result = await CreateService().BulkImport(new GlossaryBulkRequest());

        Assert.Equal(0, result.Imported);
        Assert.Equal(0, result.Skipped);
    }
}
