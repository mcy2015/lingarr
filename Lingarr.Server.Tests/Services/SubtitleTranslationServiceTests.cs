using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Lingarr.Contracts.Exceptions;
using Lingarr.Contracts.Models;
using Lingarr.Contracts.Models.Batch;
using Lingarr.Contracts.Translation;
using Lingarr.Core.Entities;
using Lingarr.Core.Enum;
using Lingarr.Server.Interfaces.Services;
using Lingarr.Server.Interfaces.Services.Translation;
using Lingarr.Server.Models;
using Lingarr.Server.Models.FileSystem;
using Lingarr.Server.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Lingarr.Server.Tests.Services;

public class SubtitleTranslationServiceTests
{
    private static TranslationRequest NewRequest() => new()
    {
        Title = "test",
        SourceLanguage = "en",
        TargetLanguage = "es",
        MediaType = MediaType.Movie,
        Status = TranslationStatus.InProgress
    };

    private static SubtitleItem Subtitle(int position, params string[] lines) => new()
    {
        Position = position,
        Lines = lines.ToList(),
        PlaintextLines = lines.ToList()
    };

    /// <summary>
    /// Mirrors how <c>TranslationRequestService.TranslateContentAsync</c> builds items for the
    /// <c>/api/translate/content</c> endpoint: position and text only, no timing information.
    /// </summary>
    private static SubtitleItem ContentLine(int position, string line) => Subtitle(position, line);

    private sealed record TranslateCall(string Text, List<string>? ContextBefore, List<string>? ContextAfter);

    private static readonly JsonSerializerOptions ExpectedJsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>
    /// Expected context entry when translated context is enabled. Built from a dictionary on purpose,
    /// so the tests pin the wire format itself rather than share a type with the code under test.
    /// </summary>
    private static string ContextJson(int position, string line, string? translation = null)
    {
        var entry = new Dictionary<string, object> { ["position"] = position, ["line"] = line };
        if (translation is not null)
        {
            entry["translation"] = translation;
        }
        return JsonSerializer.Serialize(entry, ExpectedJsonOptions);
    }

    private sealed class PerLineHarness
    {
        public Mock<ITranslationService> TranslationServiceMock { get; init; } = null!;
        public Mock<IProgressService> ProgressServiceMock { get; init; } = null!;
        public SubtitleTranslationService Service { get; init; } = null!;
        public List<TranslateCall> Calls { get; init; } = [];
    }

    private sealed class BatchHarness
    {
        public Mock<IProgressService> ProgressServiceMock { get; init; } = null!;
        public SubtitleTranslationService Service { get; init; } = null!;
    }

    private static PerLineHarness CreatePerLineHarness(
        Func<string, string> translate,
        bool useTranslatedContext = false,
        bool mergeStackedLines = true)
    {
        var calls = new List<TranslateCall>();
        var translationServiceMock = new Mock<ITranslationService>();
        translationServiceMock
            .Setup(t => t.GetLanguagePair(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((string source, string target, CancellationToken _) => new LanguagePair { Source = source, Target = target, Tier = MatchTier.Exact });
        translationServiceMock
            .Setup(t => t.TranslateAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<List<string>?>(),
                It.IsAny<List<string>?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((string text, string _, string _, List<string>? before, List<string>? after, CancellationToken _) =>
            {
                calls.Add(new TranslateCall(text, before?.ToList(), after?.ToList()));
                return translate(text);
            });

        var progressServiceMock = new Mock<IProgressService>();
        progressServiceMock
            .Setup(p => p.Emit(It.IsAny<TranslationRequest>(), It.IsAny<int>()))
            .Returns(Task.CompletedTask);
        progressServiceMock
            .Setup(progressService => progressService.EmitLine(
                It.IsAny<TranslationRequest>(),
                It.IsAny<int>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<LanguagePair?>()))
            .Returns(Task.CompletedTask);
        progressServiceMock
            .Setup(p => p.EmitLines(It.IsAny<TranslationRequest>(), It.IsAny<List<TranslatedLineData>>()))
            .Returns(Task.CompletedTask);

        return new PerLineHarness
        {
            TranslationServiceMock = translationServiceMock,
            ProgressServiceMock = progressServiceMock,
            Service = new SubtitleTranslationService(
                [new TranslationServiceEntry("test", translationServiceMock.Object, null)],
                NullLogger.Instance,
                progressServiceMock.Object,
                useTranslatedContext,
                mergeStackedLines),
            Calls = calls
        };
    }

    private static BatchHarness CreateBatchHarness(Func<List<BatchSubtitleItem>, Dictionary<int, string>> batchTranslate)
    {
        var translationServiceMock = new Mock<ITranslationService>();
        translationServiceMock
            .Setup(t => t.GetLanguagePair(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((string source, string target, CancellationToken _) => new LanguagePair { Source = source, Target = target, Tier = MatchTier.Exact });
        var batchTranslationServiceMock = new Mock<IBatchTranslationService>();
        batchTranslationServiceMock
            .Setup(b => b.TranslateBatchAsync(
                It.IsAny<List<BatchSubtitleItem>>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((List<BatchSubtitleItem> items, string _, string _, CancellationToken _) => batchTranslate(items));

        var progressServiceMock = new Mock<IProgressService>();
        progressServiceMock
            .Setup(p => p.Emit(It.IsAny<TranslationRequest>(), It.IsAny<int>()))
            .Returns(Task.CompletedTask);
        progressServiceMock
            .Setup(p => p.EmitLines(It.IsAny<TranslationRequest>(), It.IsAny<List<TranslatedLineData>>()))
            .Returns(Task.CompletedTask);

        return new BatchHarness
        {
            ProgressServiceMock = progressServiceMock,
            Service = new SubtitleTranslationService(
                [new TranslationServiceEntry("test", translationServiceMock.Object, batchTranslationServiceMock.Object)],
                NullLogger.Instance,
                progressServiceMock.Object)
        };
    }

    #region TranslateSubtitles Tests

    [Fact]
    public async Task TranslateSubtitles_SingleLine_TranslatesAsOneCallAndReturnsOneLine()
    {
        // Arrange
        var harness = CreatePerLineHarness(_ => "hola");
        var subtitles = new List<SubtitleItem> { Subtitle(1, "hello") };

        // Act
        await harness.Service.TranslateSubtitles(subtitles, NewRequest(),
            stripSubtitleFormatting: false,
            preserveLineBreaks: false,
            contextBefore: 0,
            contextAfter: 0,
            CancellationToken.None);

        // Assert
        Assert.Equal(["hola"], subtitles[0].TranslatedLines);
        harness.TranslationServiceMock.Verify(
            t => t.TranslateAsync("hello", "en", "es", null, null, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task TranslateSubtitles_MultiLinePreserveOff_MergesIntoOneCallAndReturnsOneLine()
    {
        // Arrange
        var captured = new List<string>();
        var harness = CreatePerLineHarness(text =>
        {
            captured.Add(text);
            return "hola mundo";
        });
        var subtitles = new List<SubtitleItem> { Subtitle(1, "hello", "world") };

        // Act
        await harness.Service.TranslateSubtitles(subtitles, NewRequest(),
            stripSubtitleFormatting: false,
            preserveLineBreaks: false,
            contextBefore: 0,
            contextAfter: 0,
            CancellationToken.None);

        // Assert
        Assert.Equal(["hello world"], captured);
        Assert.Equal(["hola mundo"], subtitles[0].TranslatedLines);
        harness.TranslationServiceMock.Verify(
            t => t.TranslateAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<List<string>?>(),
                It.IsAny<List<string>?>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task TranslateSubtitles_MultiLinePreserveOffStripOn_RewrapsLongTranslation()
    {
        // Arrange - multi-line merged input with a long translation; rewrap kicks in because strip is on
        var harness = CreatePerLineHarness(_ => "Esta es una traducción bastante larga que sin duda excederá el límite de cuarenta y dos caracteres");
        var subtitles = new List<SubtitleItem> { Subtitle(1, "line a", "line b") };

        // Act
        await harness.Service.TranslateSubtitles(subtitles, NewRequest(),
            stripSubtitleFormatting: true,
            preserveLineBreaks: false,
            contextBefore: 0,
            contextAfter: 0,
            CancellationToken.None);

        // Assert
        var translated = subtitles[0].TranslatedLines;
        Assert.True(translated.Count > 1, "Expected the long translation to be wrapped into multiple lines");
        Assert.All(translated, line => Assert.True(line.Length <= 42, $"Line exceeded max length: '{line}'"));
    }

    [Fact]
    public async Task TranslateSubtitles_MultiLinePreserveOn_TranslatesEachLineSeparately()
    {
        // Arrange
        var captured = new List<string>();
        var harness = CreatePerLineHarness(text =>
        {
            captured.Add(text);
            return text == "hello" ? "hola" : "mundo";
        });
        var subtitles = new List<SubtitleItem> { Subtitle(1, "hello", "world") };

        // Act
        await harness.Service.TranslateSubtitles(subtitles, NewRequest(),
            stripSubtitleFormatting: false,
            preserveLineBreaks: true,
            contextBefore: 0,
            contextAfter: 0,
            CancellationToken.None);

        // Assert
        Assert.Equal(["hello", "world"], captured);
        Assert.Equal(["hola", "mundo"], subtitles[0].TranslatedLines);
        harness.TranslationServiceMock.Verify(
            t => t.TranslateAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<List<string>?>(),
                It.IsAny<List<string>?>(),
                It.IsAny<CancellationToken>()),
            Times.Exactly(2));
    }

    [Fact]
    public async Task TranslateSubtitles_PreserveOnPerLineTranslationWithEmbeddedNewline_KeepsModelOutputVerbatim()
    {
        // Arrange - a per-line translation contains a literal '\n'. We must NOT round-trip through ToSubtitleLines
        // (which would split on '\n' and trip the count-mismatch fallback). Output should be stored as the model returned.
        var harness = CreatePerLineHarness(text => text == "hello" ? "ho\nla" : "mundo");
        var subtitles = new List<SubtitleItem> { Subtitle(1, "hello", "world") };

        // Act
        await harness.Service.TranslateSubtitles(subtitles, NewRequest(),
            stripSubtitleFormatting: false,
            preserveLineBreaks: true,
            contextBefore: 0,
            contextAfter: 0,
            CancellationToken.None);

        // Assert
        Assert.Equal(["ho\nla", "mundo"], subtitles[0].TranslatedLines);
    }

    [Fact]
    public async Task TranslateSubtitles_WhitespaceLine_SkipsTranslationAndPassesThrough()
    {
        // Arrange
        var translatedCalls = 0;
        var harness = CreatePerLineHarness(_ =>
        {
            translatedCalls++;
            return "mundo";
        });
        var subtitles = new List<SubtitleItem> { Subtitle(1, "", "world") };

        // Act
        await harness.Service.TranslateSubtitles(subtitles, NewRequest(),
            stripSubtitleFormatting: false,
            preserveLineBreaks: true,
            contextBefore: 0,
            contextAfter: 0,
            CancellationToken.None);

        // Assert
        Assert.Equal(["", "mundo"], subtitles[0].TranslatedLines);
        Assert.Equal(1, translatedCalls);
    }

    // ===== Per-file dedup =====
    // Many fansub .ass files stack multiple Dialogue lines at the same timestamp
    // with byte-identical plaintext (shadow/glow/border/main rendered as separate
    // entries). The service caches by (StartTime, EndTime, plaintext) and reuses
    // the translation for the duplicates.

    [Fact]
    public async Task TranslateSubtitles_StackedDuplicates_TranslatesOnceAndReusesResult()
    {
        // Arrange - four entries with the same (Start, End, plaintext): the layered shadow/glow/border/main case
        var captured = new List<string>();
        var harness = CreatePerLineHarness(text => { captured.Add(text); return "hola"; });
        var subtitles = new List<SubtitleItem>
        {
            new() { Position = 1, StartTime = 1000, EndTime = 2000, Lines = ["Hello"], PlaintextLines = ["Hello"] },
            new() { Position = 2, StartTime = 1000, EndTime = 2000, Lines = ["Hello"], PlaintextLines = ["Hello"] },
            new() { Position = 3, StartTime = 1000, EndTime = 2000, Lines = ["Hello"], PlaintextLines = ["Hello"] },
            new() { Position = 4, StartTime = 1000, EndTime = 2000, Lines = ["Hello"], PlaintextLines = ["Hello"] }
        };

        // Act
        await harness.Service.TranslateSubtitles(subtitles, NewRequest(),
            stripSubtitleFormatting: false,
            preserveLineBreaks: false,
            contextBefore: 0,
            contextAfter: 0,
            CancellationToken.None);

        // Assert
        Assert.Single(captured);
        Assert.All(subtitles, s => Assert.Equal(["hola"], s.TranslatedLines));
    }

    [Fact]
    public async Task TranslateSubtitles_SameTextDifferentTimes_TranslatesEachIndependently()
    {
        // Arrange - same plaintext at different timestamps are different scenes; each must be translated
        var captured = new List<string>();
        var harness = CreatePerLineHarness(text => { captured.Add(text); return "si"; });
        var subtitles = new List<SubtitleItem>
        {
            new() { Position = 1, StartTime = 1000, EndTime = 2000, Lines = ["Yes"], PlaintextLines = ["Yes"] },
            new() { Position = 2, StartTime = 5000, EndTime = 6000, Lines = ["Yes"], PlaintextLines = ["Yes"] },
            new() { Position = 3, StartTime = 9000, EndTime = 10000, Lines = ["Yes"], PlaintextLines = ["Yes"] }
        };

        // Act
        await harness.Service.TranslateSubtitles(subtitles, NewRequest(),
            stripSubtitleFormatting: false,
            preserveLineBreaks: false,
            contextBefore: 0,
            contextAfter: 0,
            CancellationToken.None);

        // Assert
        Assert.Equal(3, captured.Count);
    }

    [Fact]
    public async Task TranslateSubtitles_NoDuplicates_TranslatesEvery()
    {
        // Arrange - SRT/VTT typical case: every entry unique
        var captured = new List<string>();
        var harness = CreatePerLineHarness(text => { captured.Add(text); return $"tr:{text}"; });
        var subtitles = new List<SubtitleItem>
        {
            new() { Position = 1, StartTime = 1000, EndTime = 2000, Lines = ["Hello"], PlaintextLines = ["Hello"] },
            new() { Position = 2, StartTime = 3000, EndTime = 4000, Lines = ["World"], PlaintextLines = ["World"] },
            new() { Position = 3, StartTime = 5000, EndTime = 6000, Lines = ["How are you?"], PlaintextLines = ["How are you?"] }
        };

        // Act
        await harness.Service.TranslateSubtitles(subtitles, NewRequest(),
            stripSubtitleFormatting: false,
            preserveLineBreaks: false,
            contextBefore: 0,
            contextAfter: 0,
            CancellationToken.None);

        // Assert
        Assert.Equal(["Hello", "World", "How are you?"], captured);
        Assert.Equal(["tr:Hello"], subtitles[0].TranslatedLines);
        Assert.Equal(["tr:World"], subtitles[1].TranslatedLines);
        Assert.Equal(["tr:How are you?"], subtitles[2].TranslatedLines);
    }

    [Fact]
    public async Task TranslateSubtitles_SameTimestampDifferentText_TranslatesEachIndependently()
    {
        // Arrange - overlapping dual-speaker dialogue: same timestamp, different text — both translated
        var captured = new List<string>();
        var harness = CreatePerLineHarness(text => { captured.Add(text); return $"tr:{text}"; });
        var subtitles = new List<SubtitleItem>
        {
            new() { Position = 1, StartTime = 1000, EndTime = 2000, Lines = ["Run!"], PlaintextLines = ["Run!"] },
            new() { Position = 2, StartTime = 1000, EndTime = 2000, Lines = ["Watch out!"], PlaintextLines = ["Watch out!"] }
        };

        // Act
        await harness.Service.TranslateSubtitles(subtitles, NewRequest(),
            stripSubtitleFormatting: false,
            preserveLineBreaks: false,
            contextBefore: 0,
            contextAfter: 0,
            CancellationToken.None);

        // Assert
        Assert.Equal(["Run!", "Watch out!"], captured);
    }

    #endregion

    #region Context Tests

    [Fact]
    public async Task TranslateSubtitles_TranslatedContextOff_ContextIsSourceTextOnBothSides()
    {
        // Arrange
        var harness = CreatePerLineHarness(text => $"tr:{text}");
        var subtitles = new List<SubtitleItem>
        {
            Subtitle(1, "Hello"),
            Subtitle(2, "How are you"),
            Subtitle(3, "Fine")
        };

        // Act
        await harness.Service.TranslateSubtitles(subtitles, NewRequest(),
            stripSubtitleFormatting: false,
            preserveLineBreaks: false,
            contextBefore: 2,
            contextAfter: 1,
            CancellationToken.None);

        // Assert - identical to the behaviour before the toggle existed
        Assert.Equal(3, harness.Calls.Count);
        Assert.Null(harness.Calls[0].ContextBefore);
        Assert.Equal(["How are you"], harness.Calls[0].ContextAfter);
        Assert.Equal(["Hello"], harness.Calls[1].ContextBefore);
        Assert.Equal(["Fine"], harness.Calls[1].ContextAfter);
        Assert.Equal(["Hello", "How are you"], harness.Calls[2].ContextBefore);
        Assert.Null(harness.Calls[2].ContextAfter);
    }

    [Fact]
    public async Task TranslateSubtitles_TranslatedContextOn_PreviouslyTranslatedLinesArePairedSentenceBySentence()
    {
        // Arrange
        var harness = CreatePerLineHarness(text => $"tr:{text}", useTranslatedContext: true);
        var subtitles = new List<SubtitleItem>
        {
            Subtitle(1, "Hello"),
            Subtitle(2, "How are you"),
            Subtitle(3, "Fine")
        };

        // Act
        await harness.Service.TranslateSubtitles(subtitles, NewRequest(),
            stripSubtitleFormatting: false,
            preserveLineBreaks: false,
            contextBefore: 2,
            contextAfter: 0,
            CancellationToken.None);

        // Assert - each earlier line is one JSON object with position, line and translation, in order
        Assert.Equal(3, harness.Calls.Count);
        Assert.Null(harness.Calls[0].ContextBefore);
        Assert.Equal(
            [ContextJson(1, "Hello", "tr:Hello")],
            harness.Calls[1].ContextBefore);
        Assert.Equal(
            [ContextJson(1, "Hello", "tr:Hello"), ContextJson(2, "How are you", "tr:How are you")],
            harness.Calls[2].ContextBefore);
        Assert.Equal("{\"position\":1,\"line\":\"Hello\",\"translation\":\"tr:Hello\"}", harness.Calls[1].ContextBefore![0]);
    }

    [Fact]
    public async Task TranslateSubtitles_TranslatedContextOn_ContextAfterIsStructuredWithoutTranslation()
    {
        // Arrange
        var harness = CreatePerLineHarness(text => $"tr:{text}", useTranslatedContext: true);
        var subtitles = new List<SubtitleItem>
        {
            Subtitle(1, "Hello"),
            Subtitle(2, "How are you"),
            Subtitle(3, "Fine")
        };

        // Act
        await harness.Service.TranslateSubtitles(subtitles, NewRequest(),
            stripSubtitleFormatting: false,
            preserveLineBreaks: false,
            contextBefore: 1,
            contextAfter: 2,
            CancellationToken.None);

        // Assert - the lines after have not been translated yet, so they carry position and source only
        Assert.Equal([ContextJson(2, "How are you"), ContextJson(3, "Fine")], harness.Calls[0].ContextAfter);
        Assert.Equal([ContextJson(3, "Fine")], harness.Calls[1].ContextAfter);
        Assert.Null(harness.Calls[2].ContextAfter);
        Assert.Equal("{\"position\":3,\"line\":\"Fine\"}", harness.Calls[1].ContextAfter![0]);
        Assert.Equal([ContextJson(2, "How are you", "tr:How are you")], harness.Calls[2].ContextBefore);
    }

    [Fact]
    public async Task TranslateSubtitles_TranslatedContextOn_ResumedLinesArePairedFromPersistedTranslation()
    {
        // Arrange - line 1 was translated in an earlier run and pre-filled the way TranslationJob resumes it
        var harness = CreatePerLineHarness(text => $"tr:{text}", useTranslatedContext: true);
        var resumed = Subtitle(1, "Hello");
        resumed.TranslatedLines = ["Hola"];
        var subtitles = new List<SubtitleItem> { resumed, Subtitle(2, "Fine") };

        // Act
        await harness.Service.TranslateSubtitles(subtitles, NewRequest(),
            stripSubtitleFormatting: false,
            preserveLineBreaks: false,
            contextBefore: 1,
            contextAfter: 0,
            CancellationToken.None);

        // Assert - the persisted translation is used, the line itself is not re-translated
        Assert.Single(harness.Calls);
        Assert.Equal("Fine", harness.Calls[0].Text);
        Assert.Equal([ContextJson(1, "Hello", "Hola")], harness.Calls[0].ContextBefore);
    }

    [Fact]
    public async Task TranslateSubtitles_TranslatedContextOnPreserveLineBreaks_MultiLinePairIsCollapsedToOneLineEachSide()
    {
        // Arrange - with preserveLineBreaks the first subtitle is translated line by line, yielding two TranslatedLines
        var harness = CreatePerLineHarness(text => $"tr:{text}", useTranslatedContext: true);
        var subtitles = new List<SubtitleItem>
        {
            Subtitle(1, "Hello", "world"),
            Subtitle(2, "Bye")
        };

        // Act
        await harness.Service.TranslateSubtitles(subtitles, NewRequest(),
            stripSubtitleFormatting: false,
            preserveLineBreaks: true,
            contextBefore: 1,
            contextAfter: 0,
            CancellationToken.None);

        // Assert - both sides of the pair are single lines, so the block structure stays intact
        Assert.Equal(["tr:Hello", "tr:world"], subtitles[0].TranslatedLines);
        var byeCall = Assert.Single(harness.Calls, call => call.Text == "Bye");
        Assert.Equal([ContextJson(1, "Hello world", "tr:Hello tr:world")], byeCall.ContextBefore);
    }

    [Fact]
    public async Task TranslateSubtitles_MergeStackedLinesOff_RepeatedTextIsTranslatedIndependentlyWithContext()
    {
        // Arrange - the /api/translate/content shape: same text on every line and no timing information.
        // With merging opted out every line is its own request and carries its own context.
        var harness = CreatePerLineHarness(text => $"tr:{text}", useTranslatedContext: true, mergeStackedLines: false);
        var subtitles = new List<SubtitleItem>
        {
            ContentLine(1, "Yeah."),
            ContentLine(2, "Yeah."),
            ContentLine(3, "Yeah.")
        };

        // Act
        await harness.Service.TranslateSubtitles(subtitles, NewRequest(),
            stripSubtitleFormatting: false,
            preserveLineBreaks: false,
            contextBefore: 1,
            contextAfter: 0,
            CancellationToken.None);

        // Assert - three separate calls, each of the later ones carrying the previous line as context
        Assert.Equal(3, harness.Calls.Count);
        Assert.Null(harness.Calls[0].ContextBefore);
        Assert.Equal([ContextJson(1, "Yeah.", "tr:Yeah.")], harness.Calls[1].ContextBefore);
        Assert.Equal([ContextJson(2, "Yeah.", "tr:Yeah.")], harness.Calls[2].ContextBefore);
    }

    [Fact]
    public async Task TranslateSubtitles_MergeStackedLinesOn_UntimedRepeatedTextIsStillMerged()
    {
        // Arrange - the default keeps the stacked-layer merge, so lines that share (0, 0, text) are reused
        var captured = new List<string>();
        var harness = CreatePerLineHarness(text => { captured.Add(text); return "tr:Yeah."; });
        var subtitles = new List<SubtitleItem> { ContentLine(1, "Yeah."), ContentLine(2, "Yeah.") };

        // Act
        await harness.Service.TranslateSubtitles(subtitles, NewRequest(),
            stripSubtitleFormatting: false,
            preserveLineBreaks: false,
            contextBefore: 0,
            contextAfter: 0,
            CancellationToken.None);

        // Assert
        Assert.Single(captured);
        Assert.All(subtitles, s => Assert.Equal(["tr:Yeah."], s.TranslatedLines));
    }

    [Fact]
    public async Task TranslateSubtitles_TranslatedContextOn_EmptyStoredTranslationIsPassedWithoutTranslationField()
    {
        // Arrange - the model answers nothing for the first line; that must not become an example for the next one
        var harness = CreatePerLineHarness(text => text == "Hello" ? "" : $"tr:{text}", useTranslatedContext: true);
        var subtitles = new List<SubtitleItem> { Subtitle(1, "Hello"), Subtitle(2, "Bye") };

        // Act
        await harness.Service.TranslateSubtitles(subtitles, NewRequest(),
            stripSubtitleFormatting: false,
            preserveLineBreaks: false,
            contextBefore: 1,
            contextAfter: 0,
            CancellationToken.None);

        // Assert - stored as empty, passed on like an untranslated line
        Assert.Equal([""], subtitles[0].TranslatedLines);
        Assert.Equal([ContextJson(1, "Hello")], harness.Calls[1].ContextBefore);
    }

    [Fact]
    public async Task TranslateSubtitles_TranslatedContextOn_EmbeddedNewlineAndNonAsciiStayOnOneReadableLine()
    {
        // Arrange - a /content line carries the whole multi-line subtitle with a literal newline
        var harness = CreatePerLineHarness(text => text == "Hello\nworld" ? "你好\n世界" : "再见", useTranslatedContext: true);
        var subtitles = new List<SubtitleItem> { ContentLine(1, "Hello\nworld"), ContentLine(2, "Bye") };

        // Act
        await harness.Service.TranslateSubtitles(subtitles, NewRequest(),
            stripSubtitleFormatting: false,
            preserveLineBreaks: false,
            contextBefore: 1,
            contextAfter: 0,
            CancellationToken.None);

        // Assert - the newline is JSON-escaped so the entry stays one physical line, CJK is not \u-escaped
        var entry = Assert.Single(harness.Calls[1].ContextBefore!);
        Assert.Equal("{\"position\":1,\"line\":\"Hello\\nworld\",\"translation\":\"你好\\n世界\"}", entry);
        Assert.DoesNotContain('\n', entry);
    }

    #endregion

    #region ProcessSubtitleBatch Tests

    [Fact]
    public async Task ProcessSubtitleBatch_PreserveOff_JoinsWithSpaceAndReturnsSingleLine()
    {
        // Arrange
        List<BatchSubtitleItem> seen = [];
        var harness = CreateBatchHarness(items =>
        {
            seen = items;
            return items.ToDictionary(i => i.Position, _ => "hola mundo");
        });
        var subtitles = new List<SubtitleItem> { Subtitle(1, "hello", "world") };

        // Act
        await harness.Service.ProcessSubtitleBatch(subtitles,
            "en", "es",
            stripSubtitleFormatting: false,
            preserveLineBreaks: false,
            CancellationToken.None);

        // Assert
        Assert.Equal("hello world", seen[0].Line);
        Assert.Equal(["hola mundo"], subtitles[0].TranslatedLines);
    }

    [Fact]
    public async Task ProcessSubtitleBatch_PreserveOnMultiLine_JoinsWithNewlineAndSplitsBack()
    {
        // Arrange
        List<BatchSubtitleItem> seen = [];
        var harness = CreateBatchHarness(items =>
        {
            seen = items;
            return items.ToDictionary(i => i.Position, _ => "hola\nmundo");
        });
        var subtitles = new List<SubtitleItem> { Subtitle(1, "hello", "world") };

        // Act
        await harness.Service.ProcessSubtitleBatch(subtitles,
            "en", "es",
            stripSubtitleFormatting: false,
            preserveLineBreaks: true,
            CancellationToken.None);

        // Assert
        Assert.Equal("hello\nworld", seen[0].Line);
        Assert.Equal(["hola", "mundo"], subtitles[0].TranslatedLines);
    }

    [Fact]
    public async Task ProcessSubtitleBatch_PreserveOnButLineCountMismatch_CollapsesToSingleLine()
    {
        // Arrange - model returns three '\n'-separated lines but source had two; expect fallback to a merged single line
        var harness = CreateBatchHarness(items =>
            items.ToDictionary(i => i.Position, _ => "hola\nmundo\nextra"));
        var subtitles = new List<SubtitleItem> { Subtitle(1, "hello", "world") };

        // Act
        await harness.Service.ProcessSubtitleBatch(subtitles,
            "en", "es",
            stripSubtitleFormatting: false,
            preserveLineBreaks: true,
            CancellationToken.None);

        // Assert
        Assert.Equal(["hola mundo extra"], subtitles[0].TranslatedLines);
    }

    [Fact]
    public async Task ProcessSubtitleBatch_PreserveOffStripOnMultiLine_RewrapsLongTranslation()
    {
        // Arrange
        var harness = CreateBatchHarness(items =>
            items.ToDictionary(i => i.Position, _ => "Esta es una traducción bastante larga que sin duda excederá el límite de cuarenta y dos caracteres"));
        var subtitles = new List<SubtitleItem> { Subtitle(1, "line a", "line b") };

        // Act
        await harness.Service.ProcessSubtitleBatch(subtitles,
            "en", "es",
            stripSubtitleFormatting: true,
            preserveLineBreaks: false,
            CancellationToken.None);

        // Assert
        var translated = subtitles[0].TranslatedLines;
        Assert.True(translated.Count > 1, "Expected the long translation to be wrapped into multiple lines");
        Assert.All(translated, line => Assert.True(line.Length <= 42, $"Line exceeded max length: '{line}'"));
    }

    [Fact]
    public async Task ProcessSubtitleBatch_MissingTranslation_FallsBackToOriginalLines()
    {
        // Arrange
        var harness = CreateBatchHarness(_ => new Dictionary<int, string>());
        var subtitles = new List<SubtitleItem> { Subtitle(1, "hello", "world") };

        // Act
        await harness.Service.ProcessSubtitleBatch(subtitles,
            "en", "es",
            stripSubtitleFormatting: false,
            preserveLineBreaks: false,
            CancellationToken.None);

        // Assert
        Assert.Equal(["hello", "world"], subtitles[0].TranslatedLines);
    }

    #endregion

    #region Chain-wide best-match resolution

    private static Mock<ITranslationService> MockService(LanguagePair? pair, Func<string, string> translate)
    {
        var mock = new Mock<ITranslationService>();
        mock.Setup(service => service.GetLanguagePair(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(pair);
        mock.Setup(service => service.TranslateAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<List<string>?>(), It.IsAny<List<string>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string text, string _, string _, List<string>? _, List<string>? _, CancellationToken _) => translate(text));
        return mock;
    }

    [Fact]
    public async Task TranslateSubtitleLine_PrefersExactMatchOverNeutralFallback()
    {
        // Arrange
        var primary = MockService(new LanguagePair { Source = "en", Target = "zh", Tier = MatchTier.NeutralEquivalent }, _ => "primary");
        var fallback = MockService(new LanguagePair { Source = "en", Target = "zh-TW", Tier = MatchTier.Exact }, _ => "fallback");
        var service = new SubtitleTranslationService(
            [
                new TranslationServiceEntry("primary", primary.Object, null),
                new TranslationServiceEntry("fallback", fallback.Object, null)
            ],
            NullLogger.Instance);

        // Act
        var (translation, serviceName, pair) = await service.TranslateSubtitleLine(
            new TranslateAbleSubtitleLine { SubtitleLine = "hi", SourceLanguage = "en", TargetLanguage = "zh-TW" },
            CancellationToken.None);

        // Assert
        Assert.Equal("fallback", translation);
        Assert.Equal("fallback", serviceName);
        Assert.Equal(MatchTier.Exact, pair.Tier);
        Assert.Equal("zh-TW", pair.Target);
    }

    [Fact]
    public async Task TranslateSubtitleLine_SameTierBreaksTiesByChainOrder()
    {
        // Arrange
        var primary = MockService(new LanguagePair { Source = "en", Target = "zh-TW", Tier = MatchTier.Exact }, _ => "primary");
        var fallback = MockService(new LanguagePair { Source = "en", Target = "zh-TW", Tier = MatchTier.Exact }, _ => "fallback");
        var service = new SubtitleTranslationService(
            [
                new TranslationServiceEntry("primary", primary.Object, null),
                new TranslationServiceEntry("fallback", fallback.Object, null)
            ],
            NullLogger.Instance);

        // Act
        var (translation, serviceName, _) = await service.TranslateSubtitleLine(
            new TranslateAbleSubtitleLine { SubtitleLine = "hi", SourceLanguage = "en", TargetLanguage = "zh-TW" },
            CancellationToken.None);

        // Assert
        Assert.Equal("primary", translation);
        Assert.Equal("primary", serviceName);
    }

    [Fact]
    public async Task TranslateSubtitleLine_FallbackMatchSurfacesMatchedCodesOnEmittedLine()
    {
        // Arrange
        var only = MockService(new LanguagePair { Source = "en", Target = "zh", Tier = MatchTier.NeutralEquivalent }, _ => "ni hao");
        var progress = new Mock<IProgressService>();
        progress.Setup(progressService => progressService.Emit(It.IsAny<TranslationRequest>(), It.IsAny<int>())).Returns(Task.CompletedTask);
        progress.Setup(progressService => progressService.EmitLine(
                It.IsAny<TranslationRequest>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string?>(), It.IsAny<LanguagePair?>()))
            .Returns(Task.CompletedTask);
        var service = new SubtitleTranslationService(
            [new TranslationServiceEntry("only", only.Object, null)],
            NullLogger.Instance,
            progress.Object);
        var request = new TranslationRequest
        {
            Title = "t",
            SourceLanguage = "en",
            TargetLanguage = "zh-TW",
            MediaType = MediaType.Movie,
            Status = TranslationStatus.InProgress
        };

        // Act
        await service.TranslateSubtitles([Subtitle(1, "hi")], request,
            stripSubtitleFormatting: false,
            preserveLineBreaks: false,
            contextBefore: 0,
            contextAfter: 0,
            CancellationToken.None);

        // Assert
        progress.Verify(progressService => progressService.EmitLine(
            request, 1, "hi", "ni hao", "only",
            It.Is<LanguagePair?>(pair => pair != null && pair.Source == "en" && pair.Target == "zh" && pair.Tier == MatchTier.NeutralEquivalent)),
            Times.Once);
    }

    [Fact]
    public async Task TranslateSubtitleLine_NoServiceSupportsPair_Throws()
    {
        // Arrange
        var none = MockService(pair: null, _ => "unused");
        var service = new SubtitleTranslationService(
            [new TranslationServiceEntry("none", none.Object, null)],
            NullLogger.Instance);

        // Act + Assert
        await Assert.ThrowsAsync<TranslationException>(() =>
            service.TranslateSubtitleLine(
                new TranslateAbleSubtitleLine { SubtitleLine = "hi", SourceLanguage = "en", TargetLanguage = "zh-TW" },
                CancellationToken.None));
    }

    #endregion

    #region Producing-service derivation (P1 fix: cost/chain attribution)

    private static Mock<IProgressService> ProgressMock()
    {
        var progress = new Mock<IProgressService>();
        progress.Setup(p => p.Emit(It.IsAny<TranslationRequest>(), It.IsAny<int>())).Returns(Task.CompletedTask);
        progress.Setup(p => p.EmitLine(
                It.IsAny<TranslationRequest>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string?>(), It.IsAny<LanguagePair?>()))
            .Returns(Task.CompletedTask);
        progress.Setup(p => p.EmitLines(It.IsAny<TranslationRequest>(), It.IsAny<List<TranslatedLineData>>()))
            .Returns(Task.CompletedTask);
        return progress;
    }

    [Fact]
    public async Task ResolveProducingService_ReturnsSoleProvider_WhenItProducedEverything()
    {
        // Arrange
        var harness = CreatePerLineHarness(text => $"tr:{text}");
        var subtitles = new List<SubtitleItem> { Subtitle(1, "a"), Subtitle(2, "b"), Subtitle(3, "c") };

        // Act
        await harness.Service.TranslateSubtitles(subtitles, NewRequest(),
            stripSubtitleFormatting: false,
            preserveLineBreaks: false,
            contextBefore: 0,
            contextAfter: 0,
            CancellationToken.None);

        // Assert
        Assert.Equal("test", harness.Service.ResolveProducingService());
    }

    [Fact]
    public async Task ResolveProducingService_ReturnsFallback_WhenFallbackProducedAllLines()
    {
        // Arrange - the primary fails on every line, the fallback takes over
        var failing = MockChainService(ExactPair, _ => null);
        var working = MockChainService(ExactPair, text => $"zh:{text}");
        var service = new SubtitleTranslationService(
            [
                new TranslationServiceEntry("gemini", failing.Object, null),
                new TranslationServiceEntry("openai", working.Object, null)
            ],
            NullLogger.Instance,
            ProgressMock().Object);

        var subtitles = new List<SubtitleItem> { Subtitle(1, "a"), Subtitle(2, "b") };

        // Act
        await service.TranslateSubtitles(subtitles, NewRequest(),
            stripSubtitleFormatting: false,
            preserveLineBreaks: false,
            contextBefore: 0,
            contextAfter: 0,
            CancellationToken.None);

        // Assert - the run reports the provider that actually produced it,
        // not the configured primary
        Assert.Equal("openai", service.ResolveProducingService());
    }

    [Fact]
    public async Task ResolveProducingService_MajorityWins_TiesBreakToTheMostRecentProducer()
    {
        // Arrange - primary produces positions 1-2, then fails; fallback
        // produces positions 3-4: a 2:2 tie resolved towards the provider of
        // the highest (most recently produced) position
        var primaryCalls = 0;
        var primary = MockChainService(ExactPair, text =>
        {
            primaryCalls++;
            return primaryCalls <= 2 ? $"p:{text}" : null;
        });
        var fallback = MockChainService(ExactPair, text => $"f:{text}");
        var service = new SubtitleTranslationService(
            [
                new TranslationServiceEntry("primary", primary.Object, null),
                new TranslationServiceEntry("fallback", fallback.Object, null)
            ],
            NullLogger.Instance,
            ProgressMock().Object);

        var subtitles = new List<SubtitleItem> { Subtitle(1, "a"), Subtitle(2, "b"), Subtitle(3, "c"), Subtitle(4, "d") };

        // Act
        await service.TranslateSubtitles(subtitles, NewRequest(),
            stripSubtitleFormatting: false,
            preserveLineBreaks: false,
            contextBefore: 0,
            contextAfter: 0,
            CancellationToken.None);

        // Assert
        Assert.Equal("fallback", service.ResolveProducingService());
    }

    [Fact]
    public async Task ResolveProducingService_ReturnsMajorityProducer_WhenPrimaryRecoveredMidRun()
    {
        // Arrange - the primary fails once, then recovers and produces the
        // remaining lines: the majority producer is reported
        var primaryCalls = 0;
        var primary = MockChainService(ExactPair, text =>
        {
            primaryCalls++;
            return primaryCalls == 1 ? null : $"p:{text}";
        });
        var fallback = MockChainService(ExactPair, text => $"f:{text}");
        var service = new SubtitleTranslationService(
            [
                new TranslationServiceEntry("primary", primary.Object, null),
                new TranslationServiceEntry("fallback", fallback.Object, null)
            ],
            NullLogger.Instance,
            ProgressMock().Object);

        var subtitles = new List<SubtitleItem> { Subtitle(1, "a"), Subtitle(2, "b"), Subtitle(3, "c") };

        // Act
        await service.TranslateSubtitles(subtitles, NewRequest(),
            stripSubtitleFormatting: false,
            preserveLineBreaks: false,
            contextBefore: 0,
            contextAfter: 0,
            CancellationToken.None);

        // Assert - primary covered 2 of 3 positions
        Assert.Equal("primary", service.ResolveProducingService());
    }

    [Fact]
    public async Task ResolveProducingService_ReturnsNull_WhenNothingNewWasTranslated()
    {
        // Arrange - a full resume: every line already carries a persisted translation
        var harness = CreatePerLineHarness(_ => "unused");
        var resumed = Subtitle(1, "hello");
        resumed.TranslatedLines = ["hola"];
        var subtitles = new List<SubtitleItem> { resumed };

        // Act
        await harness.Service.TranslateSubtitles(subtitles, NewRequest(),
            stripSubtitleFormatting: false,
            preserveLineBreaks: false,
            contextBefore: 0,
            contextAfter: 0,
            CancellationToken.None);

        // Assert - no provenance recorded, callers keep the configured primary
        Assert.Null(harness.Service.ResolveProducingService());
    }

    [Fact]
    public async Task ResolveProducingService_BatchFallback_ReturnsFallback()
    {
        // Arrange - the primary batch-capable provider fails, the fallback batch completes
        var failingTranslation = new Mock<ITranslationService>();
        failingTranslation
            .Setup(t => t.GetLanguagePair(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ExactPair);
        var failingBatch = new Mock<IBatchTranslationService>();
        failingBatch
            .Setup(b => b.TranslateBatchAsync(
                It.IsAny<List<BatchSubtitleItem>>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TranslationException("primary batch exploded"));

        var workingTranslation = new Mock<ITranslationService>();
        workingTranslation
            .Setup(t => t.GetLanguagePair(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ExactPair);
        var workingBatch = new Mock<IBatchTranslationService>();
        workingBatch
            .Setup(b => b.TranslateBatchAsync(
                It.IsAny<List<BatchSubtitleItem>>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((List<BatchSubtitleItem> items, string _, string _, CancellationToken _) =>
                items.ToDictionary(item => item.Position, item => $"zh:{item.Line}"));

        var service = new SubtitleTranslationService(
            [
                new TranslationServiceEntry("gemini", failingTranslation.Object, failingBatch.Object),
                new TranslationServiceEntry("openai", workingTranslation.Object, workingBatch.Object)
            ],
            NullLogger.Instance,
            ProgressMock().Object);

        var request = NewRequest();

        // Act
        await service.TranslateSubtitlesBatch(
            [Subtitle(1, "a"), Subtitle(2, "b")],
            request,
            stripSubtitleFormatting: false,
            preserveLineBreaks: false,
            batchSize: 2,
            CancellationToken.None);

        // Assert
        Assert.Equal("openai", service.ResolveProducingService());
    }

    #endregion

    #region Fallback visibility events (P1-2)

    private static Mock<ITranslationService> MockChainService(LanguagePair pair, Func<string, string?> translate)
    {
        var mock = new Mock<ITranslationService>();
        mock.Setup(service => service.GetLanguagePair(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(pair);
        mock.Setup(service => service.TranslateAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<List<string>?>(), It.IsAny<List<string>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string text, string _, string _, List<string>? _, List<string>? _, CancellationToken _) =>
            {
                var result = translate(text);
                return result ?? throw new HttpRequestException("TooManyRequests", null, HttpStatusCode.TooManyRequests);
            });
        mock.Setup(service => service.GetModels()).ReturnsAsync(new ModelsResponse());
        return mock;
    }

    private static readonly LanguagePair ExactPair = new()
    {
        Source = "en", Target = "zh", Tier = MatchTier.Exact
    };

    private static SubtitleTranslationService ChainService(
        ITranslationRequestEventService eventService,
        params (string Name, Mock<ITranslationService> Mock)[] chain) => new(
            chain.Select(entry => new TranslationServiceEntry(entry.Name, entry.Mock.Object, null)).ToList(),
            NullLogger.Instance,
            eventService: eventService);

    [Fact]
    public async Task DegradedFallback_RecordsProviderFailureAndDegradation()
    {
        var eventService = new Mock<ITranslationRequestEventService>();
        var failing = MockChainService(ExactPair, _ => null); // always 429
        var working = MockChainService(ExactPair, text => $"zh:{text}");
        var service = ChainService(eventService.Object, ("gemini", failing), ("openai", working));

        var result = await service.TranslateSubtitleLine(
            new TranslateAbleSubtitleLine
            {
                SubtitleLine = "hello",
                SourceLanguage = "en",
                TargetLanguage = "zh",
                RequestId = 2417
            },
            CancellationToken.None);

        Assert.Equal("zh:hello", result.Translation);
        eventService.Verify(e => e.LogTimelineEvent(
            2417, TranslationEventType.ProviderFailed, TranslationStatus.InProgress,
            "gemini", It.Is<string?>(message => message != null && message.Contains("429"))),
            Times.Once);
        eventService.Verify(e => e.LogTimelineEvent(
            2417, TranslationEventType.Degraded, TranslationStatus.InProgress,
            "openai", "gemini -> openai"),
            Times.Once);
    }

    [Fact]
    public async Task PrimarySucceeds_RecordsNoFallbackEvents()
    {
        var eventService = new Mock<ITranslationRequestEventService>();
        var working = MockChainService(ExactPair, text => $"zh:{text}");
        var unused = MockChainService(ExactPair, text => $"zh2:{text}");
        var service = ChainService(eventService.Object, ("openai", working), ("deepl", unused));

        await service.TranslateSubtitleLine(
            new TranslateAbleSubtitleLine
            {
                SubtitleLine = "hello",
                SourceLanguage = "en",
                TargetLanguage = "zh",
                RequestId = 1
            },
            CancellationToken.None);

        eventService.Verify(e => e.LogTimelineEvent(
            It.IsAny<int>(), It.IsAny<TranslationEventType>(), It.IsAny<TranslationStatus>(),
            It.IsAny<string?>(), It.IsAny<string?>()),
            Times.Never);
    }

    [Fact]
    public async Task Degradation_IsRecordedOncePerRequest()
    {
        var eventService = new Mock<ITranslationRequestEventService>();
        var failing = MockChainService(ExactPair, _ => null);
        var working = MockChainService(ExactPair, text => $"zh:{text}");
        var service = ChainService(eventService.Object, ("gemini", failing), ("openai", working));

        for (var line = 0; line < 3; line++)
        {
            await service.TranslateSubtitleLine(
                new TranslateAbleSubtitleLine
                {
                    SubtitleLine = $"line {line}",
                    SourceLanguage = "en",
                    TargetLanguage = "zh",
                    RequestId = 2417
                },
                CancellationToken.None);
        }

        // the walk retries the primary on every line; the timeline shows the
        // degradation story once, not once per line
        eventService.Verify(e => e.LogTimelineEvent(
            2417, TranslationEventType.ProviderFailed, It.IsAny<TranslationStatus>(),
            It.IsAny<string?>(), It.IsAny<string?>()),
            Times.Once);
        eventService.Verify(e => e.LogTimelineEvent(
            2417, TranslationEventType.Degraded, It.IsAny<TranslationStatus>(),
            It.IsAny<string?>(), It.IsAny<string?>()),
            Times.Once);
    }

    [Fact]
    public async Task NoRequestId_NoEventsRecorded()
    {
        // ad-hoc API line translations carry no request timeline
        var eventService = new Mock<ITranslationRequestEventService>();
        var failing = MockChainService(ExactPair, _ => null);
        var working = MockChainService(ExactPair, text => $"zh:{text}");
        var service = ChainService(eventService.Object, ("gemini", failing), ("openai", working));

        await service.TranslateSubtitleLine(
            new TranslateAbleSubtitleLine { SubtitleLine = "hello", SourceLanguage = "en", TargetLanguage = "zh" },
            CancellationToken.None);

        eventService.Verify(e => e.LogTimelineEvent(
            It.IsAny<int>(), It.IsAny<TranslationEventType>(), It.IsAny<TranslationStatus>(),
            It.IsAny<string?>(), It.IsAny<string?>()),
            Times.Never);
    }

    [Fact]
    public async Task TranslateSubtitlesBatch_RecordsBatchCompletedPerBatch()
    {
        var eventService = new Mock<ITranslationRequestEventService>();
        var translationMock = new Mock<ITranslationService>();
        translationMock
            .Setup(t => t.GetLanguagePair(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ExactPair);
        var batchMock = new Mock<IBatchTranslationService>();
        batchMock
            .Setup(b => b.TranslateBatchAsync(
                It.IsAny<List<BatchSubtitleItem>>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((List<BatchSubtitleItem> items, string _, string _, CancellationToken _) =>
                items.ToDictionary(item => item.Position, item => $"zh:{item.Line}"));

        var progressMock = new Mock<IProgressService>();
        progressMock.Setup(p => p.Emit(It.IsAny<TranslationRequest>(), It.IsAny<int>())).Returns(Task.CompletedTask);
        progressMock.Setup(p => p.EmitLines(It.IsAny<TranslationRequest>(), It.IsAny<List<TranslatedLineData>>()))
            .Returns(Task.CompletedTask);

        var service = new SubtitleTranslationService(
            [new TranslationServiceEntry("openai", translationMock.Object, batchMock.Object)],
            NullLogger.Instance,
            progressMock.Object,
            eventService: eventService.Object);

        var request = new TranslationRequest
        {
            Title = "t",
            SourceLanguage = "en",
            TargetLanguage = "zh",
            MediaType = MediaType.Movie,
            Status = TranslationStatus.InProgress
        };

        await service.TranslateSubtitlesBatch(
            [Subtitle(1, "a"), Subtitle(2, "b"), Subtitle(3, "c")],
            request,
            stripSubtitleFormatting: false,
            preserveLineBreaks: false,
            batchSize: 2,
            CancellationToken.None);

        eventService.Verify(e => e.LogTimelineEvent(
            request.Id, TranslationEventType.BatchCompleted, TranslationStatus.InProgress,
            null, "batch 1/2 · 2/3 lines"),
            Times.Once);
        eventService.Verify(e => e.LogTimelineEvent(
            request.Id, TranslationEventType.BatchCompleted, TranslationStatus.InProgress,
            null, "batch 2/2 · 3/3 lines"),
            Times.Once);
    }

    #endregion
}
