using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Lingarr.Server.Modules.Settings;
using Lingarr.Server.Modules.Subtitle;
using Lingarr.Server.Modules.Translation;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Lingarr.Server.Tests.Services.Subtitle;

/// <summary>
/// Audit P0-4: GetAllSubtitles walks the directory tree exactly once and
/// filters by the supported-extension set (case-insensitive), replacing the
/// old one-recursive-sweep-per-extension loop. These tests pin the result
/// set, the ordering (extension groups in .srt/.ssa/.ass/.vtt order, the
/// order the per-extension sweeps produced), the stricter extension match
/// and the single traversal itself.
/// </summary>
public class SubtitleServiceEnumerationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lingarr-sub-enumeration-" + Guid.NewGuid().ToString("N"));
    private readonly Mock<ISettingService> _settings = new();

    public SubtitleServiceEnumerationTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { /* temp cleanup best effort */ }
        GC.SuppressFinalize(this);
    }

    private SubtitleService CreateService(Func<string, IEnumerable<string>>? enumerator = null) => new(
        NullLogger<SubtitleService>.Instance,
        new LanguageCodeService(),
        _settings.Object,
        directoryEnumerator: enumerator);

    private string WriteFile(string relativePath, int bytes = 8)
    {
        var path = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[bytes]);
        return path;
    }

    [Fact]
    public async Task SinglePass_CollectsEverySupportedFormat_FromSubdirectoriesAndMixedCase()
    {
        // mixed-case extensions must match case-insensitively; non-subtitle
        // files and ".srtx" (which the old Windows "*.srt" glob quirk
        // swallowed) stay excluded.
        var srtLower = WriteFile("movie.zh.srt");
        var srtUpper = Path.Combine(_root, "movie.en.SRT");
        File.WriteAllBytes(srtUpper, new byte[8]);
        var ssa = WriteFile("styles.ja.ssa");
        var ass = WriteFile(Path.Combine("sub", "show.s01e01.chs.ass"));
        var vtt = WriteFile(Path.Combine("sub", "deeper", "show.s01e01.sdh.en.vtt"));
        WriteFile("movie.mkv");
        WriteFile("notes.txt");
        WriteFile("weird.srtx");

        var subtitles = await CreateService().GetAllSubtitles(_root);

        var paths = subtitles.Select(s => s.Path).ToList();
        Assert.Contains(srtLower, paths);
        Assert.Contains(srtUpper, paths);
        Assert.Contains(ssa, paths);
        Assert.Contains(ass, paths);
        Assert.Contains(vtt, paths);
        Assert.Equal(5, subtitles.Count);

        // Format is the canonical lowercase bucket extension even for ".SRT"
        Assert.Equal(".srt", subtitles.Single(s => s.Path == srtUpper).Format);
        // language / caption parsing is untouched by the enumeration change
        Assert.Equal("zh", subtitles.Single(s => s.Path == srtLower).Language);
        Assert.Equal("en", subtitles.Single(s => s.Path == vtt).Language);
        Assert.Equal("sdh", subtitles.Single(s => s.Path == vtt).Caption);
    }

    [Fact]
    public async Task ResultOrder_MatchesLegacyPerExtensionGrouping()
    {
        // alphabetical within the tree would put ".ass" first; the legacy
        // per-extension sweeps emitted .srt files first, then .ssa, .ass,
        // .vtt — consumers picking "first match" depend on that.
        WriteFile("b.chs.ass");
        WriteFile("a.zh.srt");
        WriteFile("c.ja.ssa");
        WriteFile("d.en.vtt");

        var subtitles = await CreateService().GetAllSubtitles(_root);

        Assert.Equal(
            [".srt", ".ssa", ".ass", ".vtt"],
            subtitles.Select(s => s.Format).ToArray());
    }

    [Fact]
    public async Task DirectoryTree_IsEnumeratedExactlyOnce()
    {
        WriteFile("a.zh.srt");
        WriteFile(Path.Combine("sub", "b.en.ass"));

        var enumeratedPaths = new List<string>();
        var service = CreateService(path =>
        {
            enumeratedPaths.Add(path);
            return Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories);
        });

        var subtitles = await service.GetAllSubtitles(_root);

        Assert.Equal(2, subtitles.Count);
        Assert.Equal([_root], enumeratedPaths);
    }

    [Fact]
    public async Task MissingDirectory_ReturnsEmptyList()
    {
        var subtitles = await CreateService().GetAllSubtitles(Path.Combine(_root, "does-not-exist"));

        Assert.Empty(subtitles);
    }

    [Fact]
    public async Task EmptyDirectory_ReturnsEmptyList()
    {
        var subtitles = await CreateService().GetAllSubtitles(_root);

        Assert.Empty(subtitles);
    }
}
