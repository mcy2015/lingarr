using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Lingarr.Core.Configuration;
using Lingarr.Core.Data;
using Lingarr.Core.Entities;
using Lingarr.Core.Enum;
using Lingarr.Server.Exceptions;
using Lingarr.Server.Models.FileSystem;
using Lingarr.Server.Modules.Settings;
using Lingarr.Server.Modules.Subtitle;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;

namespace Lingarr.Server.Modules.Media;

/// <summary>
/// One subtitle stream parsed out of an ffprobe -show_streams report.
/// Pure data — <see cref="EmbeddedTrackService.ParseFFprobeOutput"/> builds
/// these without touching a process or the database, which is what the unit
/// tests exercise.
/// </summary>
public sealed record ParsedEmbeddedTrack(
    int StreamIndex,
    string? Language,
    string? RawLanguage,
    string? Title,
    string Codec,
    bool IsTextBased);

/// <summary>
/// Probes embedded subtitle tracks of media containers with ffprobe and
/// persists them as <see cref="MediaSubtitleTrack"/> rows (F2a: probe +
/// display only). Each scanned file ends up with one scan-marker row
/// (StreamIndex = -1) carrying the incremental "path|mtime" key, plus one row
/// per subtitle stream; a scan replaces all rows of the file it belongs to.
/// F2b additionally extracts text tracks via ffmpeg as translation sources.
/// PRD card A piggybacks on the same probe: the report now carries audio
/// streams too, and their normalized languages backfill
/// movies/episodes.audio_languages while that column is still null
/// (*arr payloads outrank the local probe).
/// </summary>
public class EmbeddedTrackService : IEmbeddedTrackService
{
    /// <summary>Progress log cadence for ScanAll (files between two log lines).</summary>
    public const int ProgressLogInterval = 25;

    /// <summary>
    /// Audit P0-6: number of file replacements buffered before one write
    /// transaction is flushed (plus a final flush per run). The old pipeline
    /// issued two write transactions per file.
    /// </summary>
    public const int FlushBatchSize = 50;

    /// <summary>Id chunk size for batched deletes — headroom under SQLite's parameter limit.</summary>
    private const int DeleteChunkSize = 500;

    /// <summary>
    /// QA4 P1-1: consecutive probe failures after which a file is backed
    /// off, and the number of subsequent scan rounds the skip lasts. A
    /// broken container (ffprobe timeout, corrupt file) otherwise burned
    /// batch budget on every run with nothing to show for it.
    /// </summary>
    public const int FailureBackoffThreshold = 3;
    public const int FailureBackoffRounds = 3;

    private const int ProbeTimeoutMs = 10_000;

    /// <summary>Extraction is a stream copy plus text conversion — generous cap.</summary>
    private const int ExtractTimeoutMs = 120_000;

    /// <summary>
    /// Runs the ffmpeg extraction binary and returns its stderr (used in
    /// failure messages). Injectable so unit tests exercise the reuse and
    /// failure logic without spawning a process.
    /// </summary>
    public delegate Task<string> FfmpegRunner(
        string binary,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken);

    /// <summary>
    /// Runs ffprobe on one media file and returns its JSON report. Injectable
    /// so the scan pipeline (batch limit, resume, flush cadence) is testable
    /// without spawning a process.
    /// </summary>
    public delegate Task<string> FfprobeRunner(string filePath, CancellationToken cancellationToken);

    /// <summary>Container codec_name → display codec ("subrip" → "srt"…).</summary>
    private static readonly Dictionary<string, string> CodecAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["subrip"] = "srt",
        ["mov_text"] = "srt",
        ["webvtt"] = "vtt",
        ["hdmv_pgs_subtitle"] = "pgs",
        ["pgs_subtitle"] = "pgs",
        ["dvb_subtitle"] = "dvb",
        ["dvbsub"] = "dvb"
    };

    /// <summary>Bitmap/graphic subtitle codecs — displayed as not translatable.</summary>
    private static readonly HashSet<string> GraphicCodecs = new(StringComparer.OrdinalIgnoreCase)
    {
        "pgs",
        "pgs_subtitle",
        "hdmv_pgs_subtitle",
        "dvb",
        "dvb_subtitle",
        "dvbsub",
        "dvb_teletext",
        "eia_608",
        "eia_708"
    };

    /// <summary>
    /// ISO-639-2 spellings muxers favour for Chinese ("zho" in MKV, "chi" in
    /// MP4) that the filename parser has no rule for — folded onto the same
    /// "zh" code the coverage chips match against.
    /// </summary>
    private static readonly Dictionary<string, string> ContainerLanguageFolding = new(StringComparer.OrdinalIgnoreCase)
    {
        ["chi"] = "zh",
        ["zho"] = "zh"
    };

    /// <summary>
    /// QA4 P1-1: in-process probe-failure backoff keyed by (media type, id),
    /// carrying (consecutive failures, scan rounds left to skip). Static
    /// because each Hangfire run resolves the service from a fresh scope —
    /// instance state would reset every round and the backoff would never
    /// engage. A restart resets it, which a single-container deployment can
    /// live with: the worst case is that backed-off files are probed (and
    /// fail) once more.
    /// </summary>
    private static readonly ConcurrentDictionary<(int Type, int Id), (int FailCount, int SkipRounds)> FailureBackoffs = new();

    private readonly LingarrDbContext _dbContext;
    private readonly ILogger<EmbeddedTrackService> _logger;
    private readonly IHostEnvironment _environment;
    private readonly FfmpegRunner _ffmpegRunner;
    private readonly FfprobeRunner _ffprobeRunner;
    private readonly ISettingService? _settingService;
    private string? _ffprobePath;
    private string? _ffmpegPath;
    private bool _ffprobeResolved;
    private bool _ffmpegResolved;

    public EmbeddedTrackService(
        LingarrDbContext dbContext,
        ILogger<EmbeddedTrackService> logger,
        IHostEnvironment? environment = null,
        FfmpegRunner? ffmpegRunner = null,
        Func<string>? ffmpegPathResolver = null,
        ISettingService? settingService = null,
        FfprobeRunner? ffprobeRunner = null)
    {
        _dbContext = dbContext;
        _logger = logger;
        // Optional collaborators default to the production behavior — tests
        // inject a stub environment (temp content root) and a recording
        // ffmpeg runner without DI ceremony, mirroring SubtitleService.
        _environment = environment ?? new Microsoft.Extensions.Hosting.Internal.HostingEnvironment();
        _ffmpegRunner = ffmpegRunner ?? RunFfmpeg;
        _ffmpegPathResolver = ffmpegPathResolver ?? ResolveFfmpegPath;
        // Audit P0-6: the batch limit setting (code fallback default — no
        // migration) and an injectable probe keep the scan pipeline testable.
        _settingService = settingService;
        _ffprobeRunner = ffprobeRunner ?? RunFFprobe;
    }

    private readonly Func<string> _ffmpegPathResolver;

    /// <inheritdoc />
    public async Task<EmbeddedTrackScanSummary> ScanMedia(
        MediaType mediaType,
        int id,
        bool force = false,
        CancellationToken cancellationToken = default)
    {
        var targets = await ResolveTargets(mediaType, id, cancellationToken);
        if (targets.Count == 0)
        {
            _logger.LogWarning("Embedded track scan: no media found for {MediaType} {MediaId}", mediaType, id);
            return new EmbeddedTrackScanSummary(0, 0, 0, 0, 0);
        }

        await LoadMarkers(cancellationToken);
        return await ScanTargets(targets, force, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<EmbeddedTrackScanSummary> ScanAll(
        bool force = false,
        CancellationToken cancellationToken = default)
    {
        var targets = new List<ScanTarget>();

        // Audit P0-6: deterministic id order turns the marker table into a
        // stable resume cursor — a run cut short by the batch limit continues
        // exactly where the previous one stopped, because already-scanned
        // files are free (marker skip, no ffprobe).
        targets.AddRange((await _dbContext.Movies
                .Select(movie => new { movie.Id, movie.Path, movie.FileName, movie.AudioLanguages })
                .OrderBy(movie => movie.Id)
                .ToListAsync(cancellationToken))
            .Select(movie => new ScanTarget(MediaType.Movie, movie.Id, movie.Path, movie.FileName, movie.AudioLanguages)));

        targets.AddRange((await _dbContext.Episodes
                .Select(episode => new { episode.Id, episode.Path, episode.FileName, episode.AudioLanguages })
                .OrderBy(episode => episode.Id)
                .ToListAsync(cancellationToken))
            .Select(episode => new ScanTarget(MediaType.Episode, episode.Id, episode.Path, episode.FileName, episode.AudioLanguages)));

        if (targets.Count == 0)
        {
            _logger.LogInformation("Embedded track scan: library is empty, nothing to probe.");
            return new EmbeddedTrackScanSummary(0, 0, 0, 0, 0);
        }

        await LoadMarkers(cancellationToken);
        var workLimit = await ResolveBatchLimit();
        return await ScanTargets(targets, force, workLimit, cancellationToken);
    }

    /// <summary>
    /// Audit P0-6: per-run probe budget. A missing, blank or unparsable
    /// <see cref="SettingKeys.Automation.EmbeddedScanBatchLimit"/> value (or
    /// one below 1) falls back to
    /// <see cref="SettingKeys.Automation.DefaultEmbeddedScanBatchLimit"/> —
    /// the code-default pattern of SubtitleNamingTemplate, no migration.
    /// </summary>
    private async Task<int> ResolveBatchLimit()
    {
        try
        {
            if (_settingService != null)
            {
                var raw = await _settingService.GetSetting(SettingKeys.Automation.EmbeddedScanBatchLimit);
                if (!string.IsNullOrWhiteSpace(raw)
                    && int.TryParse(raw, out var parsed)
                    && parsed > 0)
                {
                    return parsed;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Embedded track scan: reading the batch limit setting failed — using the default of {Default}",
                SettingKeys.Automation.DefaultEmbeddedScanBatchLimit);
        }

        return SettingKeys.Automation.DefaultEmbeddedScanBatchLimit;
    }

    /// <summary>
    /// Pure parser for ffprobe's <c>-print_format json -show_streams</c>
    /// output (card A removed <c>-select_streams s</c> so audio streams reach
    /// the parser too): one <see cref="ParsedEmbeddedTrack"/> per subtitle
    /// stream, in stream order. Non-subtitle streams are skipped; a stream
    /// without a codec_type keeps parsing as a subtitle stream — the
    /// -select_streams-era reports (and their unit fixtures) carry none.
    /// Language tags are normalized through
    /// <see cref="SubtitleLanguageParser"/> (zh-CN → zh); unrecognised tags
    /// keep <see cref="ParsedEmbeddedTrack.RawLanguage"/> with a null
    /// <see cref="ParsedEmbeddedTrack.Language"/>. Throws
    /// <see cref="JsonException"/> on malformed JSON — callers treat that as
    /// a per-file probe failure.
    /// </summary>
    public static List<ParsedEmbeddedTrack> ParseFFprobeOutput(string json)
    {
        using var document = JsonDocument.Parse(json);
        var tracks = new List<ParsedEmbeddedTrack>();

        if (!document.RootElement.TryGetProperty("streams", out var streams)
            || streams.ValueKind != JsonValueKind.Array)
        {
            return tracks;
        }

        foreach (var stream in streams.EnumerateArray())
        {
            if (!IsSubtitleStream(stream))
            {
                continue;
            }

            if (!stream.TryGetProperty("index", out var indexElement)
                || !indexElement.TryGetInt32(out var streamIndex))
            {
                continue;
            }

            var codecName = stream.TryGetProperty("codec_name", out var codecElement)
                && codecElement.ValueKind == JsonValueKind.String
                    ? codecElement.GetString() ?? string.Empty
                    : string.Empty;

            string? rawLanguage = null;
            string? title = null;
            if (stream.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Object)
            {
                if (tags.TryGetProperty("language", out var languageElement)
                    && languageElement.ValueKind == JsonValueKind.String)
                {
                    rawLanguage = languageElement.GetString();
                }

                if (tags.TryGetProperty("title", out var titleElement)
                    && titleElement.ValueKind == JsonValueKind.String)
                {
                    title = titleElement.GetString();
                }
            }

            tracks.Add(new ParsedEmbeddedTrack(
                streamIndex,
                NormalizeLanguage(rawLanguage),
                string.IsNullOrWhiteSpace(rawLanguage) ? null : rawLanguage.Trim(),
                string.IsNullOrWhiteSpace(title) ? null : title.Trim(),
                NormalizeCodec(codecName),
                !GraphicCodecs.Contains(codecName)));
        }

        return tracks;
    }

    /// <summary>
    /// Pure parser counterpart of <see cref="ParseFFprobeOutput"/> for the
    /// audio streams of the same report: the distinct normalized languages in
    /// stream order ("chi" + "english" → ["zh","en"]). Streams without a
    /// language tag and unrecognized/und tags contribute nothing, so an empty
    /// result means "probed, still unknown" — callers keep the column null
    /// instead of storing an empty array.
    /// </summary>
    public static List<string> ParseAudioLanguages(string json)
    {
        using var document = JsonDocument.Parse(json);
        var languages = new List<string>();

        if (!document.RootElement.TryGetProperty("streams", out var streams)
            || streams.ValueKind != JsonValueKind.Array)
        {
            return languages;
        }

        foreach (var stream in streams.EnumerateArray())
        {
            if (!IsAudioStream(stream))
            {
                continue;
            }

            var normalized = NormalizeLanguage(ReadLanguageTag(stream));
            if (normalized != null && !languages.Contains(normalized, StringComparer.OrdinalIgnoreCase))
            {
                languages.Add(normalized);
            }
        }

        return languages;
    }

    /// <summary>
    /// Normalized distinct codes from one *arr mediaInfo.audioLanguages truth
    /// string ("english / chi" → ["en","zh"]): the highest-trust audio source
    /// of PRD card A. "und" and unrecognized tokens drop out, preserving
    /// first-occurrence order.
    /// </summary>
    public static List<string> ParseAudioLanguageString(string? audioLanguages)
    {
        var codes = new List<string>();
        if (string.IsNullOrWhiteSpace(audioLanguages))
        {
            return codes;
        }

        foreach (var token in audioLanguages.Split('/',
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var normalized = NormalizeLanguage(token);
            if (normalized != null && !codes.Contains(normalized, StringComparer.OrdinalIgnoreCase))
            {
                codes.Add(normalized);
            }
        }

        return codes;
    }

    /// <summary>
    /// Normalized distinct codes from the *arr languages aggregate names
    /// ([{name:"English"}] → ["en"]) — the lower-trust fallback of the card A
    /// chain. Unknown names (including *arr's "Unknown") drop out.
    /// </summary>
    public static List<string> NormalizeLanguageNames(IEnumerable<string?> names)
    {
        var codes = new List<string>();
        foreach (var name in names)
        {
            var normalized = NormalizeLanguage(name);
            if (normalized != null && !codes.Contains(normalized, StringComparer.OrdinalIgnoreCase))
            {
                codes.Add(normalized);
            }
        }

        return codes;
    }

    /// <summary>
    /// True when the stream explicitly declares codec_type "audio" (the
    /// card A piggyback source). Reports without codec_type never match.
    /// </summary>
    private static bool IsAudioStream(JsonElement stream) =>
        stream.TryGetProperty("codec_type", out var typeElement)
        && typeElement.ValueKind == JsonValueKind.String
        && string.Equals(typeElement.GetString(), "audio", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True for streams that become subtitle tracks. A missing or empty
    /// codec_type counts as one — the -select_streams s era reports carried
    /// none, and every stream that reached the parser was a subtitle stream.
    /// </summary>
    private static bool IsSubtitleStream(JsonElement stream)
    {
        if (!stream.TryGetProperty("codec_type", out var typeElement)
            || typeElement.ValueKind != JsonValueKind.String)
        {
            return true;
        }

        var codecType = typeElement.GetString();
        return string.IsNullOrEmpty(codecType)
            || string.Equals(codecType, "subtitle", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The trimmed language tag of a stream, or null when absent/blank.</summary>
    private static string? ReadLanguageTag(JsonElement stream)
    {
        if (!stream.TryGetProperty("tags", out var tags)
            || tags.ValueKind != JsonValueKind.Object
            || !tags.TryGetProperty("language", out var languageElement)
            || languageElement.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var tag = languageElement.GetString()?.Trim();
        return string.IsNullOrWhiteSpace(tag) ? null : tag;
    }

    /// <summary>
    /// Normalizes one container language tag: the filename parser's rules
    /// (fansub tokens, zh-CN folding…) plus the container-only ISO-639-2
    /// Chinese spellings it has no rule for. Null when nothing recognizes
    /// the tag.
    /// </summary>
    public static string? NormalizeLanguage(string? rawLanguage)
    {
        var tag = rawLanguage?.Trim();
        if (string.IsNullOrWhiteSpace(tag))
        {
            return null;
        }

        if (ContainerLanguageFolding.TryGetValue(tag, out var folded))
        {
            return folded;
        }

        return SubtitleLanguageParser.TryParse(tag, out var languages)
            ? languages[0]
            : null;
    }

    private static string NormalizeCodec(string codecName) =>
        CodecAliases.GetValueOrDefault(codecName, codecName.ToLowerInvariant());

    // ── extraction (F2b) ──────────────────────────────────────────

    /// <summary>
    /// File name of the materialized translation source next to the video:
    /// <c>{videoBase}.{language}.srt</c>. Public so <see cref="MediaSubtitleProcessor"/>
    /// can predict the path before extraction (it becomes part of the media
    /// hash), while the writing itself happens in
    /// <see cref="PrepareTranslationSource"/>.
    /// </summary>
    public static string TranslationSourceFileName(string? mediaFileName, string language)
    {
        // Entity file names carry no extension and release base names contain
        // dots, so only a known video extension is stripped — never the last
        // dot-separated segment.
        var baseName = mediaFileName ?? "embedded";
        foreach (var extension in VideoExtensions)
        {
            if (baseName.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            {
                baseName = baseName[..^extension.Length];
                break;
            }
        }

        return $"{baseName}.{language.ToLowerInvariant()}.srt";
    }

    /// <summary>
    /// The languages a media item already covers: every language of its
    /// external subtitle files plus every recognized language of its embedded
    /// text/graphic tracks, all lowercased. This union is what "target
    /// language already present" means for F2b (manual initiation rejects,
    /// the automated processor skips).
    /// </summary>
    public static HashSet<string> BuildAvailableLanguages(
        IEnumerable<Subtitles> externalSubtitles,
        IEnumerable<MediaSubtitleTrack> embeddedTracks)
    {
        var languages = new HashSet<string>();
        foreach (var subtitle in externalSubtitles)
        {
            // collection expressions need a target type — no `var` here.
            List<string> carried = subtitle.Languages.Count > 0 ? subtitle.Languages : [subtitle.Language];
            foreach (var language in carried)
            {
                if (!string.IsNullOrEmpty(language))
                {
                    languages.Add(language.ToLowerInvariant());
                }
            }
        }

        foreach (var track in embeddedTracks)
        {
            if (!string.IsNullOrEmpty(track.Language))
            {
                languages.Add(track.Language.ToLowerInvariant());
            }
        }

        return languages;
    }

    /// <inheritdoc />
    public async Task<string> ExtractTrack(
        MediaType mediaType,
        int mediaId,
        int streamIndex,
        CancellationToken cancellationToken = default)
    {
        var track = await LoadTrack(mediaType, mediaId, streamIndex, cancellationToken);
        ValidateExtractable(track);

        var container = track.FilePath;
        if (string.IsNullOrEmpty(container) || !File.Exists(container))
        {
            throw new InvalidOperationException(
                $"The media container '{container}' recorded for {mediaType} {mediaId} no longer exists on disk. " +
                "Re-index the media and run the embedded track scan again.");
        }

        var target = ExtractionFilePath(mediaType, mediaId, streamIndex, ComputeScanKey(container));
        // Same track translated again → the previous extraction is reused; a
        // zero-byte leftover from a crashed run does not count.
        if (File.Exists(target) && new FileInfo(target).Length > 0)
        {
            _logger.LogDebug(
                "Embedded track extraction reused for {MediaType} {MediaId} stream {StreamIndex}: {Path}",
                mediaType, mediaId, streamIndex, target);
            return target;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        _logger.LogInformation(
            "Extracting embedded subtitle stream {StreamIndex} of {MediaType} {MediaId} to {Path}",
            streamIndex, mediaType, mediaId, target);

        // ASS/mov_text are converted to SRT (-c:s srt) so the regular SRT
        // parser can read the result; graphics styling is lost, text isn't.
        string stderr;
        try
        {
            stderr = await _ffmpegRunner(
                _ffmpegPathResolver(),
                ["-y", "-i", container, "-map", $"0:{streamIndex}", "-c:s", "srt", target],
                cancellationToken);
        }
        catch
        {
            // never leave a partial extraction behind for the reuse check
            if (File.Exists(target))
            {
                try { File.Delete(target); } catch { /* best effort */ }
            }
            throw;
        }

        if (!File.Exists(target) || new FileInfo(target).Length == 0)
        {
            throw new InvalidOperationException(
                $"ffmpeg extracted nothing for stream {streamIndex} of {container}.{(string.IsNullOrWhiteSpace(stderr) ? string.Empty : " " + stderr.Trim())}");
        }

        return target;
    }

    /// <inheritdoc />
    public async Task<string> PrepareTranslationSource(
        MediaType mediaType,
        int mediaId,
        int streamIndex,
        CancellationToken cancellationToken = default)
    {
        var extracted = await ExtractTrack(mediaType, mediaId, streamIndex, cancellationToken);
        var track = await LoadTrack(mediaType, mediaId, streamIndex, cancellationToken);
        var media = await ResolveMediaDirectory(mediaType, mediaId, cancellationToken);

        var sourcePath = Path.Combine(
            media.Directory,
            TranslationSourceFileName(media.FileName, track.Language ?? "unknown"));

        if (!File.Exists(sourcePath) || new FileInfo(sourcePath).Length == 0)
        {
            File.Copy(extracted, sourcePath, overwrite: true);
            _logger.LogInformation(
                "Materialized embedded track {StreamIndex} of {MediaType} {MediaId} as translation source {Path}",
                streamIndex, mediaType, mediaId, sourcePath);
        }

        return sourcePath;
    }

    /// <summary>
    /// Deterministic extraction cache path:
    /// <c>{contentRoot}/config/imports/embedded/embedded.{type}.{id}.s{index}.{scanKey}.srt</c>.
    /// The container's scan key (path + mtime) is part of the name, so a
    /// replaced video file invalidates its extraction instead of serving a
    /// stale subtitle.
    /// </summary>
    private string ExtractionFilePath(MediaType mediaType, int mediaId, int streamIndex, string scanKey)
    {
        // Base64 carries path-hostile characters — fold to file-safe hex.
        var safeKey = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(scanKey)))[..12];
        return Path.Combine(
            _environment.ContentRootPath,
            "config", "imports", "embedded",
            $"embedded.{mediaType.ToString().ToLowerInvariant()}.{mediaId}.s{streamIndex}.{safeKey}.srt");
    }

    private async Task<MediaSubtitleTrack> LoadTrack(
        MediaType mediaType,
        int mediaId,
        int streamIndex,
        CancellationToken cancellationToken)
    {
        var track = await _dbContext.MediaSubtitleTracks
            .FirstOrDefaultAsync(candidate =>
                candidate.MediaType == mediaType
                && candidate.MediaId == mediaId
                && candidate.StreamIndex == streamIndex, cancellationToken);
        if (track == null)
        {
            throw new InvalidOperationException(
                $"No embedded track with stream index {streamIndex} is recorded for {mediaType} {mediaId}. " +
                "Run the embedded track scan (Settings → Automation) first.");
        }

        return track;
    }

    private static void ValidateExtractable(MediaSubtitleTrack track)
    {
        if (!track.IsTextBased)
        {
            throw new InvalidOperationException(
                $"Embedded stream {track.StreamIndex} ({track.Codec}) is image-based and cannot be extracted as text.");
        }
    }

    private sealed record MediaDirectory(string Directory, string? FileName);

    private async Task<MediaDirectory> ResolveMediaDirectory(
        MediaType mediaType,
        int mediaId,
        CancellationToken cancellationToken)
    {
        switch (mediaType)
        {
            case MediaType.Movie:
                var movie = await _dbContext.Movies
                    .Where(item => item.Id == mediaId)
                    .Select(item => new { item.Path, item.FileName })
                    .FirstOrDefaultAsync(cancellationToken);
                if (movie?.Path != null)
                {
                    return new MediaDirectory(movie.Path, movie.FileName);
                }
                break;

            case MediaType.Episode:
                var episode = await _dbContext.Episodes
                    .Where(item => item.Id == mediaId)
                    .Select(item => new { item.Path, item.FileName })
                    .FirstOrDefaultAsync(cancellationToken);
                if (episode?.Path != null)
                {
                    return new MediaDirectory(episode.Path, episode.FileName);
                }
                break;
        }

        throw new InvalidOperationException(
            $"{mediaType} {mediaId} has no media directory on record, so the extracted track cannot be placed next to the video.");
    }

    // ── scan pipeline ─────────────────────────────────────────────

    /// <summary>
    /// Scan markers of the current run, keyed by (media type, media id):
    /// path|mtime key, "missing-file", or absent when never scanned. Loaded
    /// once per run so the skip check costs no query per file; kept in step
    /// as rows are replaced.
    /// </summary>
    private Dictionary<(int Type, int Id), string?> Markers { get; set; } = [];

    private sealed record ScanTarget(MediaType Type, int Id, string? Directory, string? FileName, string[]? AudioLanguages);

    private async Task<EmbeddedTrackScanSummary> ScanTargets(
        List<ScanTarget> targets,
        bool force,
        CancellationToken cancellationToken)
        => await ScanTargets(targets, force, workLimit: null, cancellationToken);

    private async Task<EmbeddedTrackScanSummary> ScanTargets(
        List<ScanTarget> targets,
        bool force,
        int? workLimit,
        CancellationToken cancellationToken)
    {
        int probed = 0, skipped = 0, missing = 0, failed = 0, processed = 0, work = 0;

        foreach (var target in targets)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Audit P0-6: the run stops after workLimit files that needed
            // work (probe / first missing observation / failed retry).
            // Unchanged files cost no ffprobe and never consume budget, and
            // QA4 P1-1 extended the same guarantee to repeat observations:
            // a marker that already says "missing" and a file inside its
            // failure-backoff rounds are skipped for free, so permanently
            // broken queue-head files cannot starve the media behind them.
            if (workLimit is int limit && work >= limit)
            {
                _logger.LogInformation(
                    "Embedded track scan: batch limit of {Limit} reached — {Deferred} of {Total} files deferred to the next run",
                    limit, targets.Count - processed, targets.Count);
                break;
            }

            processed++;
            if (processed % ProgressLogInterval == 0)
            {
                _logger.LogInformation(
                    "Embedded track scan: {Processed}/{Total} media files processed",
                    processed, targets.Count);
            }

            var outcome = await ScanOne(target, force, cancellationToken);
            switch (outcome)
            {
                case ScanOutcome.Probed:
                    probed++;
                    work++;
                    break;
                case ScanOutcome.Skipped:
                    skipped++;
                    break;
                case ScanOutcome.Missing:
                    missing++;
                    work++;
                    break;
                case ScanOutcome.Failed:
                    failed++;
                    work++;
                    break;
            }

            if (_filesSinceFlush >= FlushBatchSize)
            {
                await FlushAsync(cancellationToken);
            }
        }

        // Whatever the run ended on (limit, end of list), queued row
        // replacements are persisted before returning.
        await FlushAsync(cancellationToken);

        _logger.LogInformation(
            "Embedded track scan finished: {Total} files — {Probed} probed, {Skipped} unchanged, {Missing} without file, {Failed} failed",
            targets.Count, probed, skipped, missing, failed);
        return new EmbeddedTrackScanSummary(targets.Count, probed, skipped, missing, failed);
    }

    private enum ScanOutcome
    {
        Probed,
        Skipped,
        Missing,
        Failed
    }

    private async Task<ScanOutcome> ScanOne(
        ScanTarget target,
        bool force,
        CancellationToken cancellationToken)
    {
        var type = target.Type;
        var id = target.Id;
        var filePath = ResolveFilePath(target.Directory, target.FileName, out var noPath);
        if (noPath)
        {
            // Nothing probeable and nothing to show — drop stale rows.
            QueueRemoval(type, id);
            return ScanOutcome.Skipped;
        }

        if (!File.Exists(filePath))
        {
            // QA4 P1-1 (scan budget starvation): a file whose marker already
            // says "missing" is still missing — re-recording the identical
            // marker would consume batch budget on every round, and with
            // targets ordered by id a queue head of permanently missing
            // files starves every new file behind them (including the
            // audio-language backfill). The skip is deliberately not gated
            // on force: a nonexistent file has no content force could
            // re-probe, and writing the same marker again changes nothing.
            if (Markers.TryGetValue(((int)type, id), out var storedMarker)
                && storedMarker == MediaSubtitleTrack.MissingFileHash)
            {
                return ScanOutcome.Skipped;
            }

            _logger.LogWarning(
                "Embedded track scan: media file missing for {MediaType} {MediaId} at {Path}",
                type, id, filePath);
            QueueReplace(type, id, filePath, [], MediaSubtitleTrack.MissingFileHash);
            return ScanOutcome.Missing;
        }

        var key = ComputeScanKey(filePath);
        if (!force
            && Markers.TryGetValue(((int)type, id), out var storedKey)
            && storedKey == key)
        {
            return ScanOutcome.Skipped;
        }

        // QA4 P1-1: files whose probe failed repeatedly (timeout, corrupt
        // container) sit out a few rounds instead of burning budget every
        // run. The skip is free, so it cannot starve anything. A manual
        // force run bypasses the backoff — the user asked for a retry.
        if (!force && TryConsumeBackoffRound(type, id))
        {
            return ScanOutcome.Skipped;
        }

        string json;
        try
        {
            json = await _ffprobeRunner(filePath, cancellationToken);
        }
        catch (FFprobeUnavailableException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Embedded track scan: ffprobe failed for {MediaType} {MediaId} at {Path} — continuing with the next file",
                type, id, filePath);
            RecordProbeFailure(type, id);
            return ScanOutcome.Failed;
        }

        List<ParsedEmbeddedTrack> tracks;
        List<string> audioLanguages;
        try
        {
            tracks = ParseFFprobeOutput(json);
            audioLanguages = ParseAudioLanguages(json);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex,
                "Embedded track scan: unparsable ffprobe output for {MediaType} {MediaId} at {Path} — continuing with the next file",
                type, id, filePath);
            RecordProbeFailure(type, id);
            return ScanOutcome.Failed;
        }

        RecordProbeSuccess(type, id);
        QueueReplace(type, id, filePath, tracks, key);
        QueueAudioLanguageUpdate(target, audioLanguages);
        if (tracks.Count > 0)
        {
            _logger.LogInformation(
                "Embedded track scan: found {Count} subtitle tracks in {Path}",
                tracks.Count, filePath);
        }

        return ScanOutcome.Probed;
    }

    // ── probe-failure backoff (QA4 P1-1) ──────────────────────────

    /// <summary>Clears the in-process failure backoff — test isolation hook.</summary>
    public static void ResetScanFailureBackoff() => FailureBackoffs.Clear();

    /// <summary>
    /// Consumes one backoff round for the media item. Returns true when the
    /// item is still backed off and this round must skip it without probing.
    /// </summary>
    private static bool TryConsumeBackoffRound(MediaType type, int id)
    {
        var key = ((int)type, id);
        while (FailureBackoffs.TryGetValue(key, out var state))
        {
            if (state.SkipRounds <= 0)
            {
                return false;
            }

            // TryUpdate instead of an indexer write: the scan pipeline is
            // single-threaded in practice, this is just defensive against a
            // concurrent manual scan of the same media item.
            if (FailureBackoffs.TryUpdate(key, (state.FailCount, state.SkipRounds - 1), state))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Records one probe failure. Reaching
    /// <see cref="FailureBackoffThreshold"/> consecutive failures arms
    /// <see cref="FailureBackoffRounds"/> skip rounds; a failure after a
    /// backed-off retry failed re-arms them (the count is only cleared by a
    /// success or a restart).
    /// </summary>
    private static void RecordProbeFailure(MediaType type, int id)
    {
        FailureBackoffs.AddOrUpdate(
            ((int)type, id),
            _ => (1, 0),
            (_, current) => current.FailCount + 1 >= FailureBackoffThreshold
                ? (current.FailCount + 1, FailureBackoffRounds)
                : (current.FailCount + 1, current.SkipRounds));
    }

    /// <summary>A successful probe resets the failure bookkeeping entirely.</summary>
    private static void RecordProbeSuccess(MediaType type, int id) =>
        FailureBackoffs.TryRemove(((int)type, id), out _);

    /// <summary>
    /// Container file path from the entity's directory + file name. Movie and
    /// episode entities store the file name WITHOUT extension — but release
    /// base names contain dots ("Movie.2023.WEBDL-1080p"), so the extension
    /// can never be inferred from the name. Existence only: exact path first,
    /// then the base name plus each known video extension.
    /// </summary>
    private static readonly string[] VideoExtensions =
        [".mkv", ".mp4", ".m4v", ".avi", ".mov", ".wmv", ".mpg", ".mpeg", ".ts", ".m2ts"];

    private static string ResolveFilePath(string? directory, string? fileName, out bool noPath)
    {
        noPath = string.IsNullOrEmpty(directory) || string.IsNullOrEmpty(fileName);
        if (noPath)
        {
            return string.Empty;
        }

        var combined = Path.Combine(directory!, fileName!);
        if (File.Exists(combined))
        {
            return combined;
        }

        foreach (var extension in VideoExtensions)
        {
            var candidate = combined + extension;
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return combined;
    }

    // ── batched persistence (audit P0-6) ──────────────────────────
    //
    // The old pipeline wrote per file: one ExecuteDelete (implicit
    // transaction) plus one SaveChanges — two WAL commits per scanned file.
    // Replacements are now queued and flushed in one transaction per
    // FlushBatchSize files; a crash before a flush only loses that batch,
    // which the next run re-probes (the scan is idempotent).

    private readonly List<(MediaType Type, int Id)> _pendingRemovals = [];
    private readonly List<MediaSubtitleTrack> _pendingTracks = [];
    private readonly List<(MediaType Type, int Id, string[] Codes)> _pendingAudioUpdates = [];
    private int _filesSinceFlush;

    /// <summary>Queues the replacement of every row of the media item.</summary>
    private void QueueReplace(
        MediaType type,
        int id,
        string filePath,
        List<ParsedEmbeddedTrack> tracks,
        string scanKey)
    {
        _pendingRemovals.Add((type, id));

        _pendingTracks.AddRange(tracks.Select(track => new MediaSubtitleTrack
        {
            MediaType = type,
            MediaId = id,
            StreamIndex = track.StreamIndex,
            Language = track.Language,
            RawLanguage = track.RawLanguage,
            Title = track.Title,
            Codec = track.Codec,
            IsTextBased = track.IsTextBased,
            FilePath = filePath,
            MediaHash = scanKey
        }));
        _pendingTracks.Add(new MediaSubtitleTrack
        {
            MediaType = type,
            MediaId = id,
            StreamIndex = MediaSubtitleTrack.ScanMarkerStreamIndex,
            Language = null,
            RawLanguage = null,
            Title = null,
            Codec = string.Empty,
            IsTextBased = false,
            FilePath = filePath,
            MediaHash = scanKey
        });
        _filesSinceFlush++;

        // keep the in-memory marker cache in step with what the DB will hold
        // after the flush; (type, id) targets are unique within a run.
        Markers[((int)type, id)] = scanKey;
    }

    /// <summary>Queues the deletion of every row of the media item.</summary>
    private void QueueRemoval(MediaType type, int id)
    {
        _pendingRemovals.Add((type, id));
        _filesSinceFlush++;
        Markers.Remove(((int)type, id));
    }

    /// <summary>
    /// Card A piggyback: queues the audio-language backfill gathered by the
    /// probe of this file — but only while the column is null. *arr payloads
    /// outrank the local probe, so a sync-written value is never overwritten,
    /// and "probed but no recognized audio language" stays null ("unknown")
    /// instead of becoming an empty array.
    /// </summary>
    private void QueueAudioLanguageUpdate(ScanTarget target, List<string> audioLanguages)
    {
        if (target.AudioLanguages != null || audioLanguages.Count == 0)
        {
            return;
        }

        _pendingAudioUpdates.Add((target.Type, target.Id, [.. audioLanguages]));
    }

    /// <summary>
    /// Persists every queued operation in a single transaction: batched
    /// ExecuteDelete per media type, then one SaveChanges for all inserts and
    /// batched ExecuteUpdate statements for the audio backfills. Deletes run
    /// before the inserts land, so a replaced file's old rows cannot collide
    /// with its fresh ones.
    /// </summary>
    private async Task FlushAsync(CancellationToken cancellationToken)
    {
        if (_pendingRemovals.Count == 0 && _pendingTracks.Count == 0 && _pendingAudioUpdates.Count == 0)
        {
            return;
        }

        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
        foreach (var group in _pendingRemovals.GroupBy(removal => removal.Type))
        {
            var mediaType = group.Key;
            foreach (var idChunk in group.Select(removal => removal.Id).Chunk(DeleteChunkSize))
            {
                var ids = idChunk.ToList();
                await _dbContext.MediaSubtitleTracks
                    .Where(track => track.MediaType == mediaType && ids.Contains(track.MediaId))
                    .ExecuteDeleteAsync(cancellationToken);
            }
        }

        _dbContext.MediaSubtitleTracks.AddRange(_pendingTracks);
        await _dbContext.SaveChangesAsync(cancellationToken);
        await FlushAudioUpdatesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        _pendingRemovals.Clear();
        _pendingTracks.Clear();
        _filesSinceFlush = 0;
    }

    /// <summary>
    /// Audio backfills as batched ExecuteUpdate statements grouped by
    /// (media type, JSON) — the same statement shape as the batched deletes,
    /// no tracker round trip on a 100k-scale first scan. updated_at is bumped
    /// alongside so the row bookkeeping stays honest.
    /// </summary>
    private async Task FlushAudioUpdatesAsync(CancellationToken cancellationToken)
    {
        if (_pendingAudioUpdates.Count == 0)
        {
            return;
        }

        foreach (var group in _pendingAudioUpdates.GroupBy(
                     update => (update.Type, Codes: string.Join(",", update.Codes))))
        {
            var mediaType = group.Key.Type;
            var codes = group.First().Codes;
            foreach (var idChunk in group.Select(update => update.Id).Distinct().Chunk(DeleteChunkSize))
            {
                var ids = idChunk.ToList();
                switch (mediaType)
                {
                    case MediaType.Movie:
                        await _dbContext.Movies
                            .Where(movie => ids.Contains(movie.Id))
                            .ExecuteUpdateAsync(setters => setters
                                .SetProperty(movie => movie.AudioLanguages, codes)
                                .SetProperty(movie => movie.UpdatedAt, DateTimeOffset.UtcNow),
                                cancellationToken);
                        break;

                    case MediaType.Episode:
                        await _dbContext.Episodes
                            .Where(episode => ids.Contains(episode.Id))
                            .ExecuteUpdateAsync(setters => setters
                                .SetProperty(episode => episode.AudioLanguages, codes)
                                .SetProperty(episode => episode.UpdatedAt, DateTimeOffset.UtcNow),
                                cancellationToken);
                        break;
                }
            }
        }

        _pendingAudioUpdates.Clear();
    }

    /// <summary>Incremental key: content of Movie.MediaHash belongs to the translation
    /// pipeline, so the scan keeps its own "path|mtime" digest on the marker row.</summary>
    private static string ComputeScanKey(string filePath)
    {
        var lastWrite = File.GetLastWriteTimeUtc(filePath);
        using var sha256 = SHA256.Create();
        var hash = sha256.ComputeHash(Encoding.UTF8.GetBytes($"{filePath}|{lastWrite.Ticks}"));
        return Convert.ToBase64String(hash);
    }

    private async Task<Dictionary<(int Type, int Id), string?>> LoadMarkers(CancellationToken cancellationToken)
    {
        Markers = (await _dbContext.MediaSubtitleTracks
                .Where(track => track.StreamIndex == MediaSubtitleTrack.ScanMarkerStreamIndex)
                .Select(track => new { track.MediaType, track.MediaId, track.MediaHash })
                .ToListAsync(cancellationToken))
            .ToDictionary(
                marker => ((int)marker.MediaType, marker.MediaId),
                marker => marker.MediaHash);
        return Markers;
    }

    private async Task<List<ScanTarget>> ResolveTargets(
        MediaType mediaType,
        int id,
        CancellationToken cancellationToken)
    {
        switch (mediaType)
        {
            case MediaType.Movie:
                var movie = await _dbContext.Movies
                    .Where(item => item.Id == id)
                    .Select(item => new { item.Id, item.Path, item.FileName, item.AudioLanguages })
                    .FirstOrDefaultAsync(cancellationToken);
                return movie == null
                    ? []
                    : [new ScanTarget(MediaType.Movie, movie.Id, movie.Path, movie.FileName, movie.AudioLanguages)];

            case MediaType.Episode:
                var episode = await _dbContext.Episodes
                    .Where(item => item.Id == id)
                    .Select(item => new { item.Id, item.Path, item.FileName, item.AudioLanguages })
                    .FirstOrDefaultAsync(cancellationToken);
                return episode == null
                    ? []
                    : [new ScanTarget(MediaType.Episode, episode.Id, episode.Path, episode.FileName, episode.AudioLanguages)];

            case MediaType.Season:
                return (await _dbContext.Episodes
                        .Where(episode => episode.SeasonId == id)
                        .Select(episode => new { episode.Id, episode.Path, episode.FileName, episode.AudioLanguages })
                        .ToListAsync(cancellationToken))
                    .Select(episode => new ScanTarget(MediaType.Episode, episode.Id, episode.Path, episode.FileName, episode.AudioLanguages))
                    .ToList();

            case MediaType.Show:
                return (await _dbContext.Episodes
                        .Where(episode => _dbContext.Seasons.Any(season =>
                            season.Id == episode.SeasonId && season.ShowId == id))
                        .Select(episode => new { episode.Id, episode.Path, episode.FileName, episode.AudioLanguages })
                        .ToListAsync(cancellationToken))
                    .Select(episode => new ScanTarget(MediaType.Episode, episode.Id, episode.Path, episode.FileName, episode.AudioLanguages))
                    .ToList();

            default:
                _logger.LogWarning("Embedded track scan: unsupported media type {MediaType}", mediaType);
                return [];
        }
    }

    // ── ffprobe process ───────────────────────────────────────────

    private async Task<string> RunFFprobe(string filePath, CancellationToken cancellationToken)
    {
        var binary = ResolveFFprobePath();

        var startInfo = new ProcessStartInfo
        {
            FileName = binary,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        // ArgumentList escapes per platform — container paths with spaces or
        // quotes survive without manual quoting. Card A: no -select_streams s
        // — audio streams must reach the parser for the audio-language
        // piggyback (still exactly one ffprobe per file).
        foreach (var argument in new[]
                 {
                     "-v", "quiet",
                     "-print_format", "json",
                     "-show_streams",
                     filePath
                 })
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        try
        {
            process.Start();
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new FFprobeUnavailableException(
                $"ffprobe binary '{binary}' could not be started.", ex);
        }

        // Drain both pipes concurrently — with -v quiet stderr stays empty,
        // but a blocked pipe must never be what the timeout trips over.
        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await Task.WhenAny(process.WaitForExitAsync(cancellationToken), Task.Delay(ProbeTimeoutMs, cancellationToken));
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                throw new TimeoutException($"ffprobe exceeded the {ProbeTimeoutMs / 1000}s timeout.");
            }
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
            throw;
        }

        if (process.ExitCode != 0)
        {
            var stderr = await stderrTask;
            throw new InvalidOperationException(
                $"ffprobe exited with code {process.ExitCode}.{(string.IsNullOrWhiteSpace(stderr) ? string.Empty : " " + stderr.Trim())}");
        }

        return await stdoutTask;
    }

    /// <summary>
    /// Production ffmpeg runner: writes the extraction to the output path
    /// named in the arguments and returns stderr for the failure message.
    /// </summary>
    private static async Task<string> RunFfmpeg(
        string binary,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = binary,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        try
        {
            process.Start();
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new FFprobeUnavailableException(
                $"ffmpeg binary '{binary}' could not be started.", ex);
        }

        // ffmpeg logs to stderr; stdout stays empty but must drain anyway.
        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await Task.WhenAny(process.WaitForExitAsync(cancellationToken), Task.Delay(ExtractTimeoutMs, cancellationToken));
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                throw new TimeoutException($"ffmpeg exceeded the {ExtractTimeoutMs / 1000}s extraction timeout.");
            }
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
            throw;
        }

        var stderrResult = await stderrTask;
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"ffmpeg exited with code {process.ExitCode}.{(string.IsNullOrWhiteSpace(stderrResult) ? string.Empty : " " + stderrResult.Trim())}");
        }

        _ = await stdoutTask;
        return stderrResult;
    }

    /// <summary>
    /// Resolves the ffprobe binary once per service instance:
    /// LINGARR_FFPROBE_PATH when set, otherwise the first ffprobe on PATH.
    /// Throws <see cref="FFprobeUnavailableException"/> with an install hint
    /// when neither exists — the environment lacks the binary, not a single
    /// broken file.
    /// </summary>
    private string ResolveFFprobePath()
    {
        if (_ffprobeResolved)
        {
            return _ffprobePath!;
        }

        _ffprobePath = ResolveBinaryPath("ffprobe", "LINGARR_FFPROBE_PATH");
        _ffprobeResolved = true;
        return _ffprobePath;
    }

    /// <summary>ffmpeg counterpart of <see cref="ResolveFFprobePath"/> (LINGARR_FFMPEG_PATH / PATH).</summary>
    private string ResolveFfmpegPath()
    {
        if (_ffmpegResolved)
        {
            return _ffmpegPath!;
        }

        _ffmpegPath = ResolveBinaryPath("ffmpeg", "LINGARR_FFMPEG_PATH");
        _ffmpegResolved = true;
        return _ffmpegPath;
    }

    /// <summary>Env override → first match on PATH; install hint when neither exists.</summary>
    private static string ResolveBinaryPath(string binary, string environmentVariable)
    {
        var configured = Environment.GetEnvironmentVariable(environmentVariable);
        if (!string.IsNullOrWhiteSpace(configured))
        {
            if (File.Exists(configured))
            {
                return configured;
            }

            throw new FFprobeUnavailableException(
                $"{environmentVariable} points at '{configured}' but no file exists there.");
        }

        var onPath = FindOnPath(binary);
        if (onPath == null)
        {
            throw new FFprobeUnavailableException(
                $"{binary} was not found on PATH. Install ffmpeg (the image does this for you) " +
                $"or point {environmentVariable} at the {binary} binary, then run the embedded track scan again.");
        }

        return onPath;
    }

    private static string? FindOnPath(string binary)
    {
        string[] names = OperatingSystem.IsWindows()
            ? [$"{binary}.exe", $"{binary}.bat"]
            : [binary];

        var pathVariable = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var directory in pathVariable.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var name in names)
            {
                var candidate = Path.Combine(directory.Trim(), name);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }
}
