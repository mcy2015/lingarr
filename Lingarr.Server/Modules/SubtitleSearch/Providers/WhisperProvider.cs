using System.Diagnostics;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Lingarr.Core.Configuration;
using Lingarr.Contracts.Settings;
using Lingarr.Server.Modules.SubtitleSearch.Abstractions;

namespace Lingarr.Server.Modules.SubtitleSearch.Providers;

/// <summary>
/// Whisper transcription fallback (B04): an OpenAI-compatible
/// <c>POST {endpoint}/audio/transcriptions</c> service consulted as the TAIL
/// of the source chain — a source, not a post-processing stage, so it rides
/// the ordinary registry/scoring/download machinery. The search extracts a
/// clip of the media's first audio stream with ffmpeg, sends it as multipart
/// (file + model + language + response_format=srt) and offers the returned
/// SRT as a single candidate labelled with the AUDIO language (zh audio
/// produces zh subtitles that land directly; en-audio output enters the
/// translation flow like any en download). <c>HashMatch</c> is always false
/// — a transcription is media-exact but never a file-hash match — and the
/// release info mirrors the media's own release name so the regular scorer
/// can price it honestly.
/// <para>
/// The source is SETTINGS-GATED (<see cref="ISettingsGatedSubtitleProvider"/>):
/// until endpoint, model and key are all present the registry omits it, so
/// the unconfigured default neither lists nor queries anything.
/// </para>
/// </summary>
[SubtitleProvider("whisper", requiresAuth: false, searchTimeoutSeconds: 300)]
public sealed class WhisperProvider : SubtitleProviderBase, ISubtitleProvider, ISubtitleProviderHealth, ISettingsGatedSubtitleProvider
{
    /// <summary>
    /// Length of the audio clip sent for transcription. The AudioLanguageService
    /// precedent clips 30s (language naming only); transcription needs more
    /// speech to be useful, but every additional second is paid for in tokens
    /// on every search round — 120s is the accepted cost/quality compromise
    /// (the batch's priced decision; transcribing the full movie was rejected
    /// as a per-search token explosion).
    /// </summary>
    public const int ClipSeconds = 120;

    /// <summary>
    /// Runs the ffmpeg clip extraction and returns its stderr (failure
    /// messages). Injectable so unit tests exercise the HTTP and candidate
    /// logic without spawning a process.
    /// </summary>
    public delegate Task ClipExtractor(string mediaFile, string clipPath, CancellationToken cancellationToken);

    private readonly ISettingsAccess _settings;
    private readonly IHostEnvironment _environment;
    private readonly ClipExtractor _clipExtractor;

    public WhisperProvider(
        ISettingsAccess settings,
        SubtitleCandidateCache candidateCache,
        HttpClient httpClient,
        ILogger<WhisperProvider> logger,
        IHostEnvironment? environment = null,
        ClipExtractor? clipExtractor = null)
        : base("whisper", httpClient, candidateCache, logger)
    {
        _settings = settings;
        _environment = environment ?? new Microsoft.Extensions.Hosting.Internal.HostingEnvironment();
        _clipExtractor = clipExtractor ?? ExtractClip;
    }

    public string Provider => ProviderKey;

    /// <inheritdoc />
    public async Task<bool> IsConfiguredAsync(CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var settings = await LoadSettingsAsync(cancellationToken);
            return settings.IsConfigured;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Gate contract: a read failure reads as "not configured".
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<SubtitleSearchResult> SearchAsync(SubtitleSearchRequest request, CancellationToken cancellationToken)
    {
        var settings = await LoadSettingsAsync(cancellationToken);
        if (!settings.IsConfigured)
        {
            throw SubtitleProviderException.ForMissingCredentials(ProviderKey, "whisper endpoint, model and key");
        }

        // Whisper can only produce subtitles in the AUDIO's language — when
        // no audio language intersects the round's requested languages the
        // honest answer is empty, before any ffmpeg or HTTP work.
        var language = TranscriptionLanguage(request);
        if (language is null)
        {
            Logger.LogDebug(
                "Whisper search for '{Title}': no audio language intersects the requested languages; skipping transcription.",
                request.Title);
            return SubtitleSearchResult.Empty;
        }

        if (string.IsNullOrWhiteSpace(request.MediaFilePath) || !File.Exists(request.MediaFilePath))
        {
            Logger.LogDebug(
                "Whisper search for '{Title}': no readable media file on record; skipping transcription.",
                request.Title);
            return SubtitleSearchResult.Empty;
        }

        // Deterministic clip path per media file (content-root scratch area,
        // AudioLanguageService precedent) — removed again in the finally so
        // repeated rounds never accumulate audio data.
        var clipName = "clip."
                       + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(request.MediaFilePath)))[..12]
                       + ".flac";
        var clipPath = Path.Combine(
            _environment.ContentRootPath, "config", "imports", "whisper", clipName);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(clipPath)!);
            await _clipExtractor(request.MediaFilePath, clipPath, cancellationToken);
            var transcript = await TranscribeAsync(clipPath, language, settings, cancellationToken);
            if (string.IsNullOrWhiteSpace(transcript))
            {
                throw SubtitleProviderException.ForInvalidResponse(
                    ProviderKey, "the transcription service returned an empty body.");
            }

            var content = Encoding.UTF8.GetBytes(transcript);
            var externalId = CandidateCache.Store(ProviderKey, new CandidateContext(content, language));
            var candidate = new SubtitleCandidate(
                ProviderKey,
                externalId,
                language,
                MatchScore: 0,
                Forced: false,
                HearingImpaired: false,
                // The transcription IS this media's release — mirroring its
                // own release name lets the regular scorer price it; it can
                // never claim a hash match.
                ReleaseInfo: request.ReleaseName ?? request.Title,
                Uploader: "whisper",
                Format: "srt",
                Size: content.Length,
                HashMatch: false);
            return new SubtitleSearchResult([candidate]);
        }
        finally
        {
            try
            {
                if (File.Exists(clipPath))
                {
                    File.Delete(clipPath);
                }
            }
            catch
            {
                /* best effort — the clip name is deterministic and reused */
            }
        }
    }

    /// <inheritdoc />
    public Task<SubtitleDownload> DownloadAsync(SubtitleCandidateRef candidateRef, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // The transcription already happened at search time — the cached
        // payload IS the download; no second service call, no expiry race.
        var context = CandidateCache.Resolve<CandidateContext>(candidateRef, ProviderKey);
        return Task.FromResult(new SubtitleDownload(
            ProviderKey,
            $"whisper.{context.Language}.srt",
            "srt",
            context.Content));
    }

    /// <inheritdoc />
    public async Task<SubtitleProviderTestOutcome> TestConnectionAsync(CancellationToken cancellationToken)
    {
        var settings = await LoadSettingsAsync(cancellationToken);
        if (!settings.IsConfigured)
        {
            throw SubtitleProviderException.ForMissingCredentials(ProviderKey, "whisper endpoint, model and key");
        }

        // One cheap real request: the OpenAI-compatible model list. A 404 is
        // tolerated as "reachable" — transcription-only deployments (e.g. a
        // bare faster-whisper server) may not expose /models while the
        // transcription endpoint itself works.
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{settings.Endpoint}/models");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.ApiKey);
        request.Headers.Accept.ParseAdd("application/json");
        try
        {
            using var _ = await SubtitleHttp.SendCheckedAsync(Http, request, ProviderKey, cancellationToken);
            return new SubtitleProviderTestOutcome(true, "Whisper endpoint reachable (model list answered).");
        }
        catch (SubtitleProviderException ex) when (ex.Message.Contains("404"))
        {
            return new SubtitleProviderTestOutcome(
                true, "Whisper endpoint reachable (no model list — transcription-only deployment).");
        }
    }

    /// <summary>
    /// The transcription language: the first audio language that the round
    /// actually asked for (case-insensitive). Null when the audio is unknown
    /// or no audio language is a requested one.
    /// </summary>
    internal static string? TranscriptionLanguage(SubtitleSearchRequest request)
    {
        if (request.AudioLanguages is not { Length: > 0 } audio)
        {
            return null;
        }

        foreach (var requested in request.Languages)
        {
            foreach (var candidate in audio)
            {
                if (string.Equals(candidate, requested, StringComparison.OrdinalIgnoreCase))
                {
                    return candidate.ToLowerInvariant();
                }
            }
        }

        return null;
    }

    private async Task<WhisperSettings> LoadSettingsAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var endpoint = await _settings.GetSettingAsync(SettingKeys.SubtitleSearch.Whisper.Endpoint);
        var model = await _settings.GetSettingAsync(SettingKeys.SubtitleSearch.Whisper.Model);
        var apiKey = await _settings.GetEncryptedSettingAsync(SettingKeys.SubtitleSearch.Whisper.ApiKey);
        return new WhisperSettings(
            NormalizeBaseUrl(endpoint),
            string.IsNullOrWhiteSpace(model) ? null : model.Trim(),
            string.IsNullOrWhiteSpace(apiKey) ? null : apiKey.Trim());
    }

    private sealed record WhisperSettings(string? Endpoint, string? Model, string? ApiKey)
    {
        public bool IsConfigured => Endpoint != null && Model != null && ApiKey != null;
    }

    private async Task<string> TranscribeAsync(
        string clipPath,
        string language,
        WhisperSettings settings,
        CancellationToken cancellationToken)
    {
        await using var fileStream = File.OpenRead(clipPath);
        using var form = new MultipartFormDataContent
        {
            { new StringContent(settings.Model!), "model" },
            { new StringContent(language), "language" },
            { new StringContent("srt"), "response_format" },
            { new StreamContent(fileStream), "file", "clip.flac" }
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{settings.Endpoint}/audio/transcriptions");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.ApiKey);
        request.Content = form;

        using var response = await SubtitleHttp.SendCheckedAsync(Http, request, ProviderKey, cancellationToken);
        return await response.Content.ReadAsStringAsync(cancellationToken);
    }

    /// <summary>
    /// Production clip extraction: the AudioLanguageService.ExtractClip
    /// argument shape with the clip length raised to
    /// <see cref="ClipSeconds"/> (see its doc for the cost rationale).
    /// 16kHz mono FLAC of the first audio stream.
    /// </summary>
    private async Task ExtractClip(string mediaFile, string clipPath, CancellationToken cancellationToken)
    {
        var ffmpeg = ResolveFfmpeg();
        var startInfo = new ProcessStartInfo
        {
            FileName = ffmpeg,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in new[]
                 {
                     "-y", "-t", ClipSeconds.ToString(),
                     "-i", mediaFile,
                     "-map", "0:a:0",
                     "-ac", "1", "-ar", "16000",
                     "-c:a", "flac",
                     clipPath
                 })
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
                            ?? throw SubtitleProviderException.ForInvalidResponse(
                                ProviderKey, "ffmpeg could not be started for the whisper clip.");
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            throw;
        }

        var stderr = await stderrTask;
        if (process.ExitCode != 0 || !File.Exists(clipPath) || new FileInfo(clipPath).Length == 0)
        {
            var snippet = stderr.Length <= 400 ? stderr : stderr[..400];
            throw SubtitleProviderException.ForInvalidResponse(
                ProviderKey,
                $"ffmpeg could not extract a whisper clip (exit {process.ExitCode}). {snippet}".Trim());
        }
    }

    /// <summary>ffmpeg resolution — env override first, then PATH (AudioLanguageService precedent).</summary>
    private string ResolveFfmpeg()
    {
        var configured = Environment.GetEnvironmentVariable("LINGARR_FFMPEG_PATH");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
        {
            return configured;
        }

        var pathVariable = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        var name = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";
        foreach (var directory in pathVariable.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(directory.Trim(), name);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw SubtitleProviderException.ForInvalidResponse(
            ProviderKey,
            "ffmpeg was not found on PATH — set LINGARR_FFMPEG_PATH or install ffmpeg to use the whisper source.");
    }

    /// <summary>Provider-side state a download needs — the transcript bytes themselves.</summary>
    private sealed record CandidateContext(byte[] Content, string Language);
}
