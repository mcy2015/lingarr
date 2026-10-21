using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Lingarr.Contracts.Settings;
using Lingarr.Core.Configuration;
using Lingarr.Server.Modules.SubtitleSearch;
using Lingarr.Server.Modules.SubtitleSearch.Abstractions;
using Lingarr.Server.Modules.SubtitleSearch.Providers;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Lingarr.Server.Tests.Modules.SubtitleSearch;

/// <summary>
/// B04 whisper fallback: the registry's settings gate (unconfigured whisper
/// is omitted entirely), the search's ffmpeg→multipart→single-candidate
/// chain (audio language labelling, never a hash match, exact SRT bytes as
/// the download), the honest empty answers (no audio language in the
/// requested set, unknown audio, no readable file) and the connection test.
/// The ffmpeg clip extraction is injected as a stub that writes the clip
/// file; the HTTP leg rides the recorded fixture response.
/// </summary>
public class WhisperProviderTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "lingarr-whisper-" + Guid.NewGuid().ToString("N"));

    private static SubtitleCandidateCache NewCache() =>
        new(new MemoryCache(new MemoryCacheOptions()), NullLogger<SubtitleCandidateCache>.Instance);

    private static readonly Dictionary<string, string> ConfiguredSettings = new()
    {
        [SettingKeys.SubtitleSearch.Whisper.Endpoint] = "http://whisper.test/v1",
        [SettingKeys.SubtitleSearch.Whisper.Model] = "whisper-1"
    };

    private const string ConfiguredKey = "whisper-key";

    private static Mock<ISettingsAccess> ConfiguredSettingsAccess(Dictionary<string, string>? extra = null)
    {
        var settings = new Dictionary<string, string>(ConfiguredSettings);
        if (extra != null)
        {
            foreach (var (key, value) in extra)
            {
                settings[key] = value;
            }
        }

        return SubtitleSettingsDouble.Create(
            settings,
            new Dictionary<string, string> { [SettingKeys.SubtitleSearch.Whisper.ApiKey] = ConfiguredKey });
    }

    private WhisperProvider Create(
        Mock<ISettingsAccess> settings,
        StubHttpMessageHandler handler,
        string? mediaFile = null) => new(
        settings.Object,
        NewCache(),
        handler.ToClient(),
        NullLogger<WhisperProvider>.Instance,
        environment: StubEnvironment(),
        clipExtractor: (file, clipPath, _) =>
        {
            // The stub "ffmpeg": records the media file it was asked to clip
            // and materializes the clip the transcription request will read.
            ClipSources.Add(file);
            Directory.CreateDirectory(Path.GetDirectoryName(clipPath)!);
            File.WriteAllText(clipPath, "flac-bytes");
            return Task.CompletedTask;
        });

    private readonly List<string> _clipSources = [];

    private List<string> ClipSources => _clipSources;

    private IHostEnvironment StubEnvironment()
    {
        var environment = new Mock<IHostEnvironment>();
        environment.SetupGet(host => host.ContentRootPath).Returns(_root);
        return environment.Object;
    }

    /// <summary>The media file the request carries (a real temp file — the provider stats it).</summary>
    private async Task<string> CreateMediaFileAsync()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "Movie.2019.WEBDL-1080p.mkv");
        await File.WriteAllTextAsync(path, "container-bytes");
        return path;
    }

    private static SubtitleSearchRequest Request(
        string[] languages,
        string? mediaFilePath = null,
        string[]? audioLanguages = null,
        string? releaseName = "Movie.2019.WEBDL-1080p") => new(
        "Movie", 2019, Season: null, Episode: null,
        ReleaseName: releaseName, VideoHash: null,
        Languages: languages, ImdbId: null,
        MediaFilePath: mediaFilePath,
        AudioLanguages: audioLanguages);

    public WhisperProviderTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    // ── registry gate ────────────────────────────────────────────────

    [Fact]
    public void RegistryGate_UnconfiguredWhisper_IsOmittedEntirely()
    {
        // B04 acceptance 3 (off): without all three settings the registry
        // does not register the source — All stays empty, Find answers null.
        var settings = SubtitleSettingsDouble.Create(
            new Dictionary<string, string> { [SettingKeys.SubtitleSearch.Whisper.Endpoint] = "http://whisper.test/v1" },
            secrets: null);
        var provider = Create(settings, new StubHttpMessageHandler());

        var registry = new SubtitleProviderRegistry(
            [provider], [], NullLogger<SubtitleProviderRegistry>.Instance);

        Assert.Empty(registry.All);
        Assert.Null(registry.Find("whisper"));
    }

    [Fact]
    public void RegistryGate_FullyConfiguredWhisper_RegistersWithItsOwnBudget()
    {
        var provider = Create(ConfiguredSettingsAccess(), new StubHttpMessageHandler());

        var registry = new SubtitleProviderRegistry(
            [provider], [], NullLogger<SubtitleProviderRegistry>.Instance);

        var entry = Assert.Single(registry.All);
        Assert.Equal("whisper", entry.Provider.Provider);
        // the transcription source declared its own 300s search budget on
        // the attribute — the orchestrator applies it instead of the 10s
        // round default (and the client timeout mirrors it).
        Assert.Equal(TimeSpan.FromSeconds(300), entry.SearchBudget);
        var attribute = (SubtitleProviderAttribute?)Attribute.GetCustomAttribute(
            typeof(WhisperProvider), typeof(SubtitleProviderAttribute));
        Assert.NotNull(attribute);
        Assert.Equal(300, attribute!.SearchTimeoutSeconds);
    }

    [Fact]
    public async Task RegistryGate_SettingsReadFailure_ReadsAsUnconfigured()
    {
        var settings = new Mock<ISettingsAccess>();
        settings
            .Setup(access => access.GetSettingAsync(It.IsAny<string>()))
            .ThrowsAsync(new InvalidOperationException("settings store hiccup"));
        var provider = Create(settings, new StubHttpMessageHandler());

        Assert.False(await provider.IsConfiguredAsync(CancellationToken.None));

        var registry = new SubtitleProviderRegistry(
            [provider], [], NullLogger<SubtitleProviderRegistry>.Instance);
        Assert.Empty(registry.All);
    }

    // ── search: the ffmpeg → multipart → candidate chain ─────────────

    [Fact]
    public async Task Search_TranscribesTheClipIntoASingleAudioLanguageCandidate()
    {
        var handler = new StubHttpMessageHandler();
        // Capture the LIVE multipart form inside the responder — the
        // provider disposes the request content after the send returns, so
        // the field values are read while the request is still alive.
        var fields = new Dictionary<string, string>();
        var filePartPresent = false;
        handler.Enqueue(request =>
        {
            if (request.Content is MultipartFormDataContent form)
            {
                foreach (var part in form)
                {
                    var name = part.Headers.ContentDisposition?.Name?.Trim('"');
                    if (name == "file")
                    {
                        filePartPresent = true;
                    }
                    else if (name != null)
                    {
                        fields[name] = part.ReadAsStringAsync().GetAwaiter().GetResult();
                    }
                }
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(SubtitleFixtures.Read("whisper", "transcription.srt"))
            };
        });
        var provider = Create(ConfiguredSettingsAccess(), handler);
        var mediaFile = await CreateMediaFileAsync();

        var result = await provider.SearchAsync(
            Request(["zh", "en"], mediaFile, audioLanguages: ["en"]),
            CancellationToken.None);

        // exactly one transcription POST, addressed to the configured
        // endpoint with the Bearer credential of the encrypted setting
        Assert.Single(handler.Requests);
        var requestMessage = handler.SingleRequest;
        Assert.Equal(HttpMethod.Post, requestMessage.Method);
        Assert.Equal("http://whisper.test/v1/audio/transcriptions", requestMessage.RequestUri!.ToString());
        Assert.Equal("Bearer", requestMessage.Headers.Authorization!.Scheme);
        Assert.Equal(ConfiguredKey, requestMessage.Headers.Authorization.Parameter);

        // multipart: model + language (the AUDIO language) + srt format + file
        Assert.Equal("whisper-1", fields.GetValueOrDefault("model"));
        Assert.Equal("en", fields.GetValueOrDefault("language"));
        Assert.Equal("srt", fields.GetValueOrDefault("response_format"));
        Assert.True(filePartPresent);

        // the clip extraction ran against the media file itself
        Assert.Equal(new[] { mediaFile }, _clipSources);

        // single candidate: audio-language label, never a hash match, the
        // media's own release as release info, fixture byte size
        var candidate = Assert.Single(result.Candidates);
        Assert.Equal("whisper", candidate.Provider);
        Assert.Equal("en", candidate.Language);
        Assert.False(candidate.HashMatch);
        Assert.Equal("srt", candidate.Format);
        Assert.Equal("Movie.2019.WEBDL-1080p", candidate.ReleaseInfo);
        Assert.Equal("whisper", candidate.Uploader);
        Assert.True(candidate.Size > 0);
    }

    [Fact]
    public async Task Search_DownloadServesTheExactTranscriptBytes()
    {
        var handler = new StubHttpMessageHandler().Enqueue(
            _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(SubtitleFixtures.Read("whisper", "transcription.srt"))
            });
        var provider = Create(ConfiguredSettingsAccess(), handler);
        var mediaFile = await CreateMediaFileAsync();

        var result = await provider.SearchAsync(
            Request(["zh"], mediaFile, audioLanguages: ["zh"]),
            CancellationToken.None);
        var candidate = Assert.Single(result.Candidates);

        // the ref handed out by the search resolves to the transcript bytes
        // verbatim — the download pipeline can land it like any candidate
        var download = await provider.DownloadAsync(new SubtitleCandidateRef(candidate.ExternalId), CancellationToken.None);
        Assert.Equal("whisper", download.Provider);
        Assert.Equal("srt", download.Format);
        var transcript = SubtitleFixtures.Read("whisper", "transcription.srt");
        Assert.Equal(
            transcript,
            System.Text.Encoding.UTF8.GetString(download.Content));
        // the clip scratch file was cleaned up after the round
        Assert.Empty(Directory.GetFiles(Path.Combine(_root, "config", "imports", "whisper")));
    }

    [Fact]
    public async Task Search_NoAudioLanguageInRequestedSet_AnswersEmptyWithoutAnyWork()
    {
        // whisper can only produce subtitles in the audio's language: a
        // round that did not ask for it gets the honest empty answer before
        // any ffmpeg or HTTP happens.
        var handler = new StubHttpMessageHandler();
        var provider = Create(ConfiguredSettingsAccess(), handler);
        var mediaFile = await CreateMediaFileAsync();

        var result = await provider.SearchAsync(
            Request(["zh"], mediaFile, audioLanguages: ["ko", "ja"]),
            CancellationToken.None);

        Assert.Empty(result.Candidates);
        Assert.Empty(handler.Requests);
        Assert.Empty(_clipSources);
    }

    [Fact]
    public async Task Search_UnknownAudioOrMissingFile_AnswersEmpty()
    {
        var handler = new StubHttpMessageHandler();
        var provider = Create(ConfiguredSettingsAccess(), handler);
        var mediaFile = await CreateMediaFileAsync();

        var unknownAudio = await provider.SearchAsync(
            Request(["zh"], mediaFile, audioLanguages: null), CancellationToken.None);
        Assert.Empty(unknownAudio.Candidates);

        var missingFile = await provider.SearchAsync(
            Request(["zh"], mediaFilePath: null, audioLanguages: ["zh"]), CancellationToken.None);
        Assert.Empty(missingFile.Candidates);

        Assert.Empty(handler.Requests);
        Assert.Empty(_clipSources);
    }

    [Fact]
    public async Task Search_IncompleteSettings_RaisesMissingCredentials()
    {
        // belt under the registry gate: if the provider is ever reached
        // without its settings, it fails loudly instead of guessing.
        var settings = SubtitleSettingsDouble.Create(); // nothing configured
        var provider = Create(settings, new StubHttpMessageHandler());

        var exception = await Assert.ThrowsAsync<SubtitleProviderException>(
            async () => await provider.SearchAsync(
                Request(["zh"], await CreateMediaFileAsync(), audioLanguages: ["zh"]),
                CancellationToken.None));
        Assert.Equal(SubtitleProviderFailureReason.MissingCredentials, exception.Reason);
    }

    [Fact]
    public async Task Search_EmptyTranscriptionBody_IsAnInvalidResponse()
    {
        var handler = new StubHttpMessageHandler().Enqueue(
            _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("   ")
            });
        var provider = Create(ConfiguredSettingsAccess(), handler);
        var mediaFile = await CreateMediaFileAsync();

        var exception = await Assert.ThrowsAsync<SubtitleProviderException>(
            () => provider.SearchAsync(
                Request(["zh"], mediaFile, audioLanguages: ["zh"]),
                CancellationToken.None));
        Assert.Equal(SubtitleProviderFailureReason.InvalidResponse, exception.Reason);
    }

    // ── connection test ──────────────────────────────────────────────

    [Fact]
    public async Task TestConnection_ModelListAnswer_ReportsOk()
    {
        var handler = new StubHttpMessageHandler().EnqueueJson("""{"data":[]}""");
        var provider = Create(ConfiguredSettingsAccess(), handler);

        var outcome = await provider.TestConnectionAsync(CancellationToken.None);

        Assert.True(outcome.Ok);
        Assert.Equal("http://whisper.test/v1/models", handler.SingleRequestUri!.ToString());
    }

    [Fact]
    public async Task TestConnection_RejectedCredentials_SurfacesTheUnauthorizedReason()
    {
        var handler = new StubHttpMessageHandler().EnqueueStatus(HttpStatusCode.Unauthorized);
        var provider = Create(ConfiguredSettingsAccess(), handler);

        var exception = await Assert.ThrowsAsync<SubtitleProviderException>(
            () => provider.TestConnectionAsync(CancellationToken.None));
        Assert.Equal(SubtitleProviderFailureReason.Unauthorized, exception.Reason);
    }

    [Fact]
    public async Task TestConnection_NoModelList_OnAReachableEndpoint_IsStillOk()
    {
        // transcription-only deployments may not expose /models — a 404
        // still proves the endpoint answers.
        var handler = new StubHttpMessageHandler().EnqueueStatus(HttpStatusCode.NotFound);
        var provider = Create(ConfiguredSettingsAccess(), handler);

        var outcome = await provider.TestConnectionAsync(CancellationToken.None);

        Assert.True(outcome.Ok);
        Assert.Contains("transcription-only", outcome.Message);
    }
}
