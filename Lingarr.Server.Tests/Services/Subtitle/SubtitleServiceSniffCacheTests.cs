using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using Lingarr.Core.Data;
using Lingarr.Core.Entities;
using Lingarr.Server.Modules.Settings;
using Lingarr.Server.Modules.Subtitle;
using Lingarr.Server.Modules.Translation;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Lingarr.Server.Tests.Services.Subtitle;

/// <summary>
/// Audit P1-7: sniff results are persisted in subtitle_language_cache, so a
/// fresh process (empty in-memory cache) answers from the database instead
/// of re-reading every untagged file head. The static in-memory cache is
/// cleared between phases to simulate the restart; the database connection
/// (and the files) stay.
/// </summary>
public class SubtitleServiceSniffCacheTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lingarr-sniff-" + Guid.NewGuid().ToString("N"));
    private readonly Mock<ISettingService> _settings = new();
    private int _headReads;

    public SubtitleServiceSniffCacheTests()
    {
        Directory.CreateDirectory(_root);
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
    }

    public void Dispose()
    {
        _connection.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private LingarrDbContext CreateContext() => new(
        new DbContextOptionsBuilder<LingarrDbContext>()
            .UseSqlite(_connection)
            .Options);

    private SubtitleService CreateService(LingarrDbContext? dbContext) => new(
        NullLogger<SubtitleService>.Instance,
        new LanguageCodeService(),
        _settings.Object,
        subtitleHeadReader: path =>
        {
            _headReads++;
            return File.ReadAllBytes(path);
        },
        dbContext: dbContext);

    /// <summary>Simulates a process restart: the static L1 cache starts empty.</summary>
    private static void ClearInMemorySniffCache()
    {
        var field = typeof(SubtitleService).GetField("SniffCache",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(field);
        // ConcurrentDictionary routes IDictionary.Clear() to its public
        // Clear(), which drops every entry without allocation.
        var cache = (IDictionary)field!.GetValue(null)!;
        cache.Clear();
    }

    private string WriteSubtitle(string fileName, string content)
    {
        var path = Path.Combine(_root, fileName);
        File.WriteAllText(path, content, Encoding.UTF8);
        return path;
    }

    [Fact]
    public async Task FreshProcess_ServesThePersistedResult_WithoutReadingTheFile()
    {
        // Han-only content sniffs as zh on the first pass.
        var path = WriteSubtitle("movie.srt", "你好世界这是测试");

        using (var context = CreateContext())
        {
            context.Database.EnsureCreated();
            await CreateService(context).GetAllSubtitles(_root);
        }

        Assert.Equal(1, _headReads);
        using (var verify = CreateContext())
        {
            var row = await verify.SubtitleLanguageCaches.AsNoTracking().SingleAsync();
            Assert.Equal(path, row.Path);
            Assert.Equal("zh", row.Language);
            Assert.Equal(new FileInfo(path).Length, row.Length);
        }

        // Restart: fresh L1, fresh DbContext over the same database — the
        // read-through must answer without touching the file.
        ClearInMemorySniffCache();
        _headReads = 0;
        using (var context = CreateContext())
        {
            var subtitles = await CreateService(context).GetAllSubtitles(_root);
            Assert.Equal("zh", subtitles.Single().Language);
        }

        Assert.Equal(0, _headReads);
    }

    [Fact]
    public async Task NullSniffResult_IsPersistedAndServedLikeAnyOther()
    {
        // No letters at all — the detector returns null, and that negative
        // result is cached so the file is not re-read on every scan.
        WriteSubtitle("numbers.srt", "1\n00:00:01,000 --> 00:00:02,000\n1234\n\n2\n00:00:03,000 --> 00:00:04,000\n5678\n");

        using (var context = CreateContext())
        {
            context.Database.EnsureCreated();
            var subtitles = await CreateService(context).GetAllSubtitles(_root);
            Assert.Equal("unknown", subtitles.Single().Language);
        }

        Assert.Equal(1, _headReads);
        using (var verify = CreateContext())
        {
            var row = await verify.SubtitleLanguageCaches.AsNoTracking().SingleAsync();
            Assert.Null(row.Language);
        }

        ClearInMemorySniffCache();
        _headReads = 0;
        using (var context = CreateContext())
        {
            var subtitles = await CreateService(context).GetAllSubtitles(_root);
            Assert.Equal("unknown", subtitles.Single().Language);
        }

        Assert.Equal(0, _headReads);
    }

    [Fact]
    public async Task ChangedMtime_IsResniffed_AndTheRowIsUpdated()
    {
        var path = WriteSubtitle("movie.srt", "你好世界这是测试");

        using (var context = CreateContext())
        {
            context.Database.EnsureCreated();
            await CreateService(context).GetAllSubtitles(_root);
        }

        Assert.Equal(1, _headReads);

        // The file is edited (different content + mtime): the (path, mtime,
        // length) key misses, the file is re-read and the row is replaced.
        File.WriteAllText(path, "1\n00:00:01,000 --> 00:00:02,000\n1234\n", Encoding.UTF8);
        var newWriteTime = new FileInfo(path).LastWriteTimeUtc.Add(TimeSpan.FromSeconds(5));
        File.SetLastWriteTimeUtc(path, newWriteTime);

        ClearInMemorySniffCache();
        _headReads = 0;
        using (var context = CreateContext())
        {
            var subtitles = await CreateService(context).GetAllSubtitles(_root);
            Assert.Equal("unknown", subtitles.Single().Language);
        }

        Assert.Equal(1, _headReads);
        using (var verify = CreateContext())
        {
            var row = await verify.SubtitleLanguageCaches.AsNoTracking().SingleAsync();
            Assert.Null(row.Language);
            Assert.Equal(newWriteTime.Ticks, row.MtimeTicks);
            Assert.Equal(new FileInfo(path).Length, row.Length);
        }
    }

    [Fact]
    public async Task ForeignLanguageTaggedFiles_StillSkipTheSniffEntirely()
    {
        // Filename tags always win (requirement F1) — no sniff, no cache row.
        WriteSubtitle("movie.zh.srt", "你好世界这是测试");

        using (var context = CreateContext())
        {
            context.Database.EnsureCreated();
            var subtitles = await CreateService(context).GetAllSubtitles(_root);
            Assert.Equal("zh", subtitles.Single().Language);
        }

        Assert.Equal(0, _headReads);
        using (var verify = CreateContext())
        {
            Assert.Empty(await verify.SubtitleLanguageCaches.ToListAsync());
        }
    }
}
