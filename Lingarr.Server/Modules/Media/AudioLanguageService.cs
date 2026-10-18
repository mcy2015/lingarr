using System.Diagnostics;
using Lingarr.Core.Configuration;
using Lingarr.Core.Data;
using Lingarr.Core.Entities;
using Lingarr.Core.Enum;
using Lingarr.Server.Modules.Settings;
using Microsoft.EntityFrameworkCore;

namespace Lingarr.Server.Modules.Media;

/// <summary>Outcome of one audio-language confirmation action.</summary>
/// <param name="Languages">Normalized codes the action stored (["zh"]).</param>
/// <param name="Source">Where they came from: "remote-asr" or "manual".</param>
/// <param name="TargetDescription">Human-readable scope, e.g. "movie" or "3 episodes".</param>
/// <param name="WriteBack">mkvpropedit tag write-back note; null when it did not run.</param>
public record AudioLanguageResult(
    string[] Languages,
    string Source,
    string TargetDescription,
    string? WriteBack = null);

/// <summary>
/// Audio-language confirmation for files whose containers carry no language
/// tag (walk-through options C+D+E): a 30-second clip of the first audio
/// stream goes to a remote OpenAI-compatible transcription endpoint whose
/// verbose_json response names the spoken language; the detected code is
/// stored (flipping the manual provenance flag so sync payloads never
/// overwrite it) and — behind a setting — written back into the container
/// with mkvpropedit so every other tool (*arr, Plex) sees the tag too.
/// Manual editing shares the same write path.
/// </summary>
public class AudioLanguageService
{
    private const int ClipSeconds = 30;

    /// <summary>
    /// ASR verbose_json language values (full English names, OpenAI shape)
    /// → normalized codes. Cantonese folds onto zh, matching the container
    /// tag folding; codes that survive <see cref="EmbeddedTrackService.NormalizeLanguage"/>
    /// alone (like SenseVoice's "zh") take that path instead.
    /// </summary>
    private static readonly Dictionary<string, string> AsrLanguageNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["chinese"] = "zh",
        ["mandarin"] = "zh",
        ["cantonese"] = "zh",
        ["english"] = "en",
        ["japanese"] = "ja",
        ["korean"] = "ko",
        ["french"] = "fr",
        ["german"] = "de",
        ["spanish"] = "es",
        ["italian"] = "it",
        ["russian"] = "ru",
        ["thai"] = "th",
        ["vietnamese"] = "vi",
        ["hindi"] = "hi",
        ["portuguese"] = "pt",
        ["dutch"] = "nl",
        ["polish"] = "pl",
        ["arabic"] = "ar",
        ["turkish"] = "tr",
        ["indonesian"] = "id"
    };

    /// <summary>Normalized code → ISO 639-2 for the mkvpropedit write-back.</summary>
    private static readonly Dictionary<string, string> Iso639_2 = new(StringComparer.OrdinalIgnoreCase)
    {
        ["zh"] = "chi",
        ["en"] = "eng",
        ["ja"] = "jpn",
        ["ko"] = "kor",
        ["fr"] = "fre",
        ["de"] = "ger",
        ["es"] = "spa",
        ["it"] = "ita",
        ["ru"] = "rus",
        ["th"] = "tha",
        ["vi"] = "vie",
        ["hi"] = "hin",
        ["pt"] = "por",
        ["nl"] = "nld",
        ["pl"] = "pol",
        ["ar"] = "ara",
        ["tr"] = "tur",
        ["id"] = "ind"
    };

    private readonly LingarrDbContext _dbContext;
    private readonly ISettingService _settingService;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<AudioLanguageService> _logger;
    private readonly IHostEnvironment _environment;

    public AudioLanguageService(
        LingarrDbContext dbContext,
        ISettingService settingService,
        IHttpClientFactory httpClientFactory,
        ILogger<AudioLanguageService> logger,
        IHostEnvironment? environment = null)
    {
        _dbContext = dbContext;
        _settingService = settingService;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _environment = environment ?? new Microsoft.Extensions.Hosting.Internal.HostingEnvironment();
    }

    // ── remote identification (option C) ──────────────────────────

    /// <summary>
    /// Identifies the spoken language of the media's first audio stream via
    /// the configured remote ASR and stores it. Movie → its own row; show →
    /// one sampled episode, applied to every episode that is not itself
    /// manually set (fixes Sonarr's name-based guesses, keeps per-episode
    /// manual values); episode → that episode.
    /// </summary>
    public async Task<AudioLanguageResult> IdentifyAsync(
        MediaType mediaType,
        int id,
        CancellationToken cancellationToken = default)
    {
        var (settings, file) = await ResolveIdentificationTarget(mediaType, id, cancellationToken);
        if (!File.Exists(file))
        {
            throw new InvalidOperationException(
                $"The media file '{file}' does not exist on disk — identification needs a readable container.");
        }

        var clipPath = Path.Combine(
            _environment.ContentRootPath, "config", "imports", "audio-id",
            $"clip.{mediaType.ToString().ToLowerInvariant()}.{id}.flac");
        Directory.CreateDirectory(Path.GetDirectoryName(clipPath)!);

        string? writeBack = null;
        try
        {
            ExtractClip(file, clipPath);
            var languageName = await DetectLanguageAsync(clipPath, settings, cancellationToken);
            var code = NormalizeDetected(languageName)
                ?? throw new InvalidOperationException(
                    $"The identification service returned '{languageName ?? "nothing"}', which maps to no known language.");

            writeBack = await TryWriteBackTag(file, code, settings, cancellationToken);
            var (stored, target) = await StoreAsync(mediaType, id, [code], "remote-asr", overwriteManual: false, cancellationToken);
            return new AudioLanguageResult(stored, "remote-asr", target, writeBack);
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

    // ── manual editing (option E) ──────────────────────────────────

    /// <summary>
    /// Stores hand-picked language codes with the manual provenance flag.
    /// Shows cascade to every episode; an empty list resets the values and
    /// the flag back to automatic/unknown.
    /// </summary>
    public async Task<AudioLanguageResult> SetManualAsync(
        MediaType mediaType,
        int id,
        IEnumerable<string> codes,
        CancellationToken cancellationToken = default)
    {
        var normalized = codes
            .Select(EmbeddedTrackService.NormalizeLanguage)
            .Where(code => code != null)
            .Select(code => code!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var (stored, target) = await StoreAsync(
            mediaType, id, [.. normalized], "manual", overwriteManual: true, cancellationToken);
        return new AudioLanguageResult(stored, "manual", target);
    }

    /// <summary>
    /// One write path for both actions. Empty codes reset to unknown and
    /// clear the manual flag; identification (overwriteManual=false) skips
    /// rows another manual action owns. Returns the stored codes and a
    /// scope description.
    /// </summary>
    private async Task<(string[] Stored, string Target)> StoreAsync(
        MediaType mediaType,
        int id,
        string[] codes,
        string source,
        bool overwriteManual,
        CancellationToken cancellationToken)
    {
        switch (mediaType)
        {
            case MediaType.Movie:
            {
                var movie = await _dbContext.Movies.FirstOrDefaultAsync(m => m.Id == id, cancellationToken)
                            ?? throw new InvalidOperationException($"No movie {id}.");
                ApplyTo(movie, codes, overwriteManual);
                await _dbContext.SaveChangesAsync(cancellationToken);
                return (movie.AudioLanguages ?? [], "movie");
            }

            case MediaType.Episode:
            {
                var episode = await _dbContext.Episodes.FirstOrDefaultAsync(e => e.Id == id, cancellationToken)
                              ?? throw new InvalidOperationException($"No episode {id}.");
                ApplyTo(episode, codes, overwriteManual);
                await _dbContext.SaveChangesAsync(cancellationToken);
                return (episode.AudioLanguages ?? [], "episode");
            }

            case MediaType.Show:
            {
                if (!await _dbContext.Shows.AnyAsync(s => s.Id == id, cancellationToken))
                {
                    throw new InvalidOperationException($"No show {id}.");
                }

                var episodes = await _dbContext.Episodes
                    .Where(e => e.Season.ShowId == id)
                    .ToListAsync(cancellationToken);
                // manual set = explicit intent for every episode; identification
                // only fixes non-manual rows (per-episode manual values win)
                var targets = episodes
                    .Where(e => overwriteManual || e.AudioLanguagesManual != true)
                    .ToList();
                foreach (var episode in targets)
                {
                    ApplyTo(episode, codes, overwriteManual);
                }

                await _dbContext.SaveChangesAsync(cancellationToken);
                return (codes, targets.Count == episodes.Count
                    ? $"{episodes.Count} episodes"
                    : $"{targets.Count} of {episodes.Count} episodes");
            }

            default:
                throw new InvalidOperationException($"Unsupported media type {mediaType}.");
        }
    }

    private static void ApplyTo(Movie movie, string[] codes, bool overwriteManual)
    {
        if (codes.Length == 0 && !overwriteManual)
        {
            return;
        }

        movie.AudioLanguages = codes.Length > 0 ? codes : null;
        movie.AudioLanguagesManual = codes.Length > 0;
    }

    private static void ApplyTo(Episode episode, string[] codes, bool overwriteManual)
    {
        if (codes.Length == 0 && !overwriteManual)
        {
            return;
        }

        episode.AudioLanguages = codes.Length > 0 ? codes : null;
        episode.AudioLanguagesManual = codes.Length > 0;
    }

    private async Task<(AudioIdSettings Settings, string File)> ResolveIdentificationTarget(
        MediaType mediaType,
        int id,
        CancellationToken cancellationToken)
    {
        var settings = await LoadSettings(cancellationToken);
        if (!settings.IsConfigured)
        {
            throw new InvalidOperationException(
                "Audio identification is not configured — set the service URL and API key in Settings → Audio.");
        }

        switch (mediaType)
        {
            case MediaType.Movie:
            {
                var movie = await _dbContext.Movies
                    .Where(m => m.Id == id)
                    .Select(m => new { m.Path, m.FileName })
                    .FirstOrDefaultAsync(cancellationToken)
                    ?? throw new InvalidOperationException($"No movie {id}.");
                return (settings, EmbeddedTrackService.ResolveFilePath(movie.Path, movie.FileName, out _) ?? "");
            }

            case MediaType.Episode:
            {
                var episode = await _dbContext.Episodes
                    .Where(e => e.Id == id)
                    .Select(e => new { e.Path, e.FileName })
                    .FirstOrDefaultAsync(cancellationToken)
                    ?? throw new InvalidOperationException($"No episode {id}.");
                return (settings, EmbeddedTrackService.ResolveFilePath(episode.Path, episode.FileName, out _) ?? "");
            }

            case MediaType.Show:
            {
                // one sample represents the series: the first episode with a
                // resolvable file, in season/episode order for determinism
                var first = await _dbContext.Seasons
                    .Where(season => season.ShowId == id)
                    .SelectMany(season => season.Episodes)
                    .OrderBy(season => season.Season.SeasonNumber)
                    .ThenBy(episode => episode.EpisodeNumber)
                    .Select(episode => new { episode.Path, episode.FileName })
                    .FirstOrDefaultAsync(cancellationToken)
                    ?? throw new InvalidOperationException($"Show {id} has no episodes to sample.");
                return (settings, EmbeddedTrackService.ResolveFilePath(first.Path, first.FileName, out _) ?? "");
            }

            default:
                throw new InvalidOperationException($"Unsupported media type {mediaType}.");
        }
    }

    private async Task<AudioIdSettings> LoadSettings(CancellationToken cancellationToken)
    {
        var baseUrl = await _settingService.GetSetting(SettingKeys.AudioIdentification.BaseUrl);
        var apiKey = await _settingService.GetEncryptedSetting(SettingKeys.AudioIdentification.ApiKey);
        var model = await _settingService.GetSetting(SettingKeys.AudioIdentification.Model);
        var writeback = await _settingService.GetSetting(SettingKeys.AudioIdentification.Writeback);
        return new AudioIdSettings(
            baseUrl?.TrimEnd('/'),
            string.IsNullOrWhiteSpace(apiKey) ? null : apiKey,
            string.IsNullOrWhiteSpace(model) ? "FunAudioLLM/SenseVoiceSmall" : model,
            string.Equals(writeback, "true", StringComparison.OrdinalIgnoreCase));
    }

    private sealed record AudioIdSettings(string? BaseUrl, string? ApiKey, string Model, bool WriteBack)
    {
        public bool IsConfigured =>
            !string.IsNullOrWhiteSpace(BaseUrl) && ApiKey != null;
    }

    private void ExtractClip(string file, string clipPath)
    {
        var ffmpeg = ResolveBinary("ffmpeg", "LINGARR_FFMPEG_PATH");
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
                     "-i", file,
                     "-map", "0:a:0",
                     "-ac", "1", "-ar", "16000",
                     "-c:a", "flac",
                     clipPath
                 })
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
                            ?? throw new InvalidOperationException("ffmpeg could not be started.");
        if (!process.WaitForExit(30_000) || process.ExitCode != 0)
        {
            var stderr = process.StandardError.ReadToEnd();
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            throw new InvalidOperationException(
                $"ffmpeg could not extract a clip: exit {process.ExitCode}. {stderr[..Math.Min(400, stderr.Length)]}");
        }
    }

    private async Task<string?> DetectLanguageAsync(
        string clipPath,
        AudioIdSettings settings,
        CancellationToken cancellationToken)
    {
        using var client = _httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromMinutes(2);
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", settings.ApiKey);

        await using var fileStream = File.OpenRead(clipPath);
        using var form = new MultipartFormDataContent
        {
            { new StringContent(settings.Model), "model" },
            { new StringContent("verbose_json"), "response_format" },
            { new StreamContent(fileStream), "file", "clip.flac" }
        };

        var url = $"{settings.BaseUrl}/audio/transcriptions";
        using var response = await client.PostAsync(url, form, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"The identification service rejected the clip ({(int)response.StatusCode}): {Truncate(body)}");
        }

        using var document = System.Text.Json.JsonDocument.Parse(body);
        return document.RootElement.TryGetProperty("language", out var language)
            && language.ValueKind == System.Text.Json.JsonValueKind.String
            ? language.GetString()
            : null;
    }

    /// <summary>ASR language value → normalized code (name map first, code fallback).</summary>
    private static string? NormalizeDetected(string? language) =>
        language is null
            ? null
            : AsrLanguageNames.GetValueOrDefault(language)
              ?? EmbeddedTrackService.NormalizeLanguage(language);

    // ── tag write-back (option D) ──────────────────────────────────

    /// <summary>
    /// Writes the language tag of the first audio track back into the
    /// container with mkvpropedit (metadata-only, seconds). MKV only — other
    /// containers keep the database-only value. Best effort: a failure
    /// returns the reason instead of failing the identification.
    /// </summary>
    private async Task<string?> TryWriteBackTag(
        string file,
        string code,
        AudioIdSettings settings,
        CancellationToken cancellationToken)
    {
        if (!settings.WriteBack)
        {
            return null;
        }

        if (!file.EndsWith(".mkv", StringComparison.OrdinalIgnoreCase))
        {
            return "write-back skipped: not an MKV container";
        }

        if (!Iso639_2.TryGetValue(code, out var iso))
        {
            return $"write-back skipped: no ISO 639-2 spelling for '{code}'";
        }

        try
        {
            var mkvpropedit = ResolveBinary("mkvpropedit", "LINGARR_MKVPROPEDIT_PATH");
            var startInfo = new ProcessStartInfo
            {
                FileName = mkvpropedit,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            foreach (var argument in new[] { file, "--edit", "track:a1", "--set", $"language={iso}" })
            {
                startInfo.ArgumentList.Add(argument);
            }

            using var process = Process.Start(startInfo);
            if (process == null)
            {
                return "write-back failed: mkvpropedit could not start";
            }

            await process.WaitForExitAsync(cancellationToken);
            return process.ExitCode == 0
                ? $"tag written back to track a1 ({iso})"
                : $"write-back failed (exit {process.ExitCode})";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Audio tag write-back failed for {File}", file);
            return $"write-back failed: {Truncate(ex.Message)}";
        }
    }

    private static string ResolveBinary(string binary, string environmentVariable)
    {
        var configured = Environment.GetEnvironmentVariable(environmentVariable);
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
        {
            return configured;
        }

        var pathVariable = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        var name = OperatingSystem.IsWindows() ? $"{binary}.exe" : binary;
        foreach (var directory in pathVariable.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(directory.Trim(), name);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException(
            $"{binary} was not found on PATH — the Docker image ships it; other setups install it or set {environmentVariable}.");
    }

    private static string Truncate(string value) => value.Length <= 300 ? value : value[..300] + "…";
}
