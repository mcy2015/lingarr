using System;
using System.IO;
using System.Threading.Tasks;
using Lingarr.Core.Configuration;
using Lingarr.Server.Modules.Settings;
using Lingarr.Server.Modules.Subtitle;
using Lingarr.Server.Modules.Translation;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Lingarr.Server.Tests.Services.Subtitle;

/// <summary>
/// Naming-template expansion for translated subtitle output files
/// (prototype screen-06 C「文件命名」编辑). The template setting
/// {原名} = cleaned base name (caption/language segments stripped),
/// {语言} = target language tag; caption and subtitle tag are appended
/// after the template and the extension still follows the output format.
/// </summary>
public class SubtitleServiceNamingTests
{
    private readonly Mock<ISettingService> _settings = new();

    private SubtitleService CreateService() => new(
        NullLogger<SubtitleService>.Instance,
        new LanguageCodeService(),
        _settings.Object);

    private void SetTemplate(string? value) =>
        _settings
            .Setup(s => s.GetSetting(SettingKeys.Translation.SubtitleNamingTemplate))
            .ReturnsAsync(value);

    [Fact]
    public async Task MissingSetting_FallsBackToDefaultTemplate()
    {
        _settings
            .Setup(s => s.GetSetting(It.IsAny<string>()))
            .ReturnsAsync((string?)null);

        var path = await CreateService().CreateFilePath("/media/Show.S01E05.en.srt", "zh", "");

        Assert.Equal("/media/Show.S01E05.zh.srt", path.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task BlankSetting_FallsBackToDefaultTemplate(string? blank)
    {
        SetTemplate(blank);

        var path = await CreateService().CreateFilePath("/media/Movie.2021.en.srt", "zh", "");

        Assert.EndsWith("Movie.2021.zh.srt", path);
    }

    [Fact]
    public async Task DefaultTemplate_KeepsCaptionAndTagAfterLanguage()
    {
        SetTemplate(SettingKeys.Translation.DefaultSubtitleNamingTemplate);

        var path = await CreateService().CreateFilePath("/media/Show.S01E05.en.sdh.srt", "zh", "Lingarr");

        Assert.EndsWith("Show.S01E05.zh.sdh.lingarr.srt", path);
    }

    [Fact]
    public async Task CustomTemplateWithoutLanguageVariable_OutputsTemplateAsIs()
    {
        SetTemplate("{原名}");

        var path = await CreateService().CreateFilePath("/media/Show.S01E05.en.srt", "zh", "");

        // no {语言} variable: the language is simply not part of the name
        Assert.EndsWith("Show.S01E05.srt", path);
    }

    [Fact]
    public async Task CustomTemplateWithLiteralText_ExpandsAroundIt()
    {
        SetTemplate("{原名}-sub-{语言}");

        var path = await CreateService().CreateFilePath("/media/Movie.2021.en.ass", "zh", "");

        Assert.EndsWith("Movie.2021-sub-zh.ass", path);
    }

    [Fact]
    public async Task EmptyTargetLanguage_ProducesNoDanglingDots()
    {
        SetTemplate(SettingKeys.Translation.DefaultSubtitleNamingTemplate);

        // remove_language_tag passes an empty target language
        var path = await CreateService().CreateFilePath("/media/Show.S01E05.en.srt", "", "");

        Assert.EndsWith("Show.S01E05.srt", path);
        Assert.DoesNotContain("..", path);
    }

    [Fact]
    public async Task CustomTemplate_StillAppendsCaptionAndTag()
    {
        SetTemplate("{原名}.{语言}");

        var path = await CreateService().CreateFilePath("/media/Show.S01E05.en.cc.srt", "zh", "tag");

        // "en" and "cc" are stripped as language/caption; the template
        // re-inserts the target language, caption and tag follow
        Assert.EndsWith("Show.S01E05.zh.cc.tag.srt", path);
    }
}
