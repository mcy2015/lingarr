using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Lingarr.Core.Data;
using Lingarr.Core.Entities;
using Lingarr.Core.Enum;
using Lingarr.Server.Exceptions;
using Lingarr.Server.Modules.Subtitle;
using Microsoft.EntityFrameworkCore;

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
/// </summary>
public class EmbeddedTrackService : IEmbeddedTrackService
{
    /// <summary>Progress log cadence for ScanAll (files between two log lines).</summary>
    public const int ProgressLogInterval = 25;

    private const int ProbeTimeoutMs = 10_000;

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

    private readonly LingarrDbContext _dbContext;
    private readonly ILogger<EmbeddedTrackService> _logger;
    private string? _ffprobePath;
    private bool _ffprobeResolved;

    public EmbeddedTrackService(LingarrDbContext dbContext, ILogger<EmbeddedTrackService> logger)
    {
        _dbContext = dbContext;
        _logger = logger;
    }

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

        targets.AddRange((await _dbContext.Movies
                .Select(movie => new { movie.Id, movie.Path, movie.FileName })
                .ToListAsync(cancellationToken))
            .Select(movie => new ScanTarget(MediaType.Movie, movie.Id, movie.Path, movie.FileName)));

        targets.AddRange((await _dbContext.Episodes
                .Select(episode => new { episode.Id, episode.Path, episode.FileName })
                .ToListAsync(cancellationToken))
            .Select(episode => new ScanTarget(MediaType.Episode, episode.Id, episode.Path, episode.FileName)));

        if (targets.Count == 0)
        {
            _logger.LogInformation("Embedded track scan: library is empty, nothing to probe.");
            return new EmbeddedTrackScanSummary(0, 0, 0, 0, 0);
        }

        await LoadMarkers(cancellationToken);
        return await ScanTargets(targets, force, cancellationToken);
    }

    /// <summary>
    /// Pure parser for ffprobe's <c>-print_format json -show_streams
    /// -select_streams s</c> output: one <see cref="ParsedEmbeddedTrack"/> per
    /// subtitle stream, in stream order. Language tags are normalized through
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

    // ── scan pipeline ─────────────────────────────────────────────

    /// <summary>
    /// Scan markers of the current run, keyed by (media type, media id):
    /// path|mtime key, "missing-file", or absent when never scanned. Loaded
    /// once per run so the skip check costs no query per file; kept in step
    /// as rows are replaced.
    /// </summary>
    private Dictionary<(int Type, int Id), string?> Markers { get; set; } = [];

    private sealed record ScanTarget(MediaType Type, int Id, string? Directory, string? FileName);

    private async Task<EmbeddedTrackScanSummary> ScanTargets(
        List<ScanTarget> targets,
        bool force,
        CancellationToken cancellationToken)
    {
        int probed = 0, skipped = 0, missing = 0, failed = 0, processed = 0;

        foreach (var (type, id, directory, fileName) in targets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            processed++;
            if (processed % ProgressLogInterval == 0)
            {
                _logger.LogInformation(
                    "Embedded track scan: {Processed}/{Total} media files processed",
                    processed, targets.Count);
            }

            var outcome = await ScanOne(type, id, directory, fileName, force, cancellationToken);
            switch (outcome)
            {
                case ScanOutcome.Probed:
                    probed++;
                    break;
                case ScanOutcome.Skipped:
                    skipped++;
                    break;
                case ScanOutcome.Missing:
                    missing++;
                    break;
                case ScanOutcome.Failed:
                    failed++;
                    break;
            }
        }

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
        MediaType type,
        int id,
        string? directory,
        string? fileName,
        bool force,
        CancellationToken cancellationToken)
    {
        var filePath = ResolveFilePath(directory, fileName, out var noPath);
        if (noPath)
        {
            // Nothing probeable and nothing to show — drop stale rows.
            await RemoveRows(type, id, cancellationToken);
            return ScanOutcome.Skipped;
        }

        if (!File.Exists(filePath))
        {
            _logger.LogWarning(
                "Embedded track scan: media file missing for {MediaType} {MediaId} at {Path}",
                type, id, filePath);
            await ReplaceRows(type, id, filePath, [], MediaSubtitleTrack.MissingFileHash, cancellationToken);
            return ScanOutcome.Missing;
        }

        var key = ComputeScanKey(filePath);
        if (!force
            && Markers.TryGetValue(((int)type, id), out var storedKey)
            && storedKey == key)
        {
            return ScanOutcome.Skipped;
        }

        string json;
        try
        {
            json = await RunFFprobe(filePath, cancellationToken);
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
            return ScanOutcome.Failed;
        }

        List<ParsedEmbeddedTrack> tracks;
        try
        {
            tracks = ParseFFprobeOutput(json);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex,
                "Embedded track scan: unparsable ffprobe output for {MediaType} {MediaId} at {Path} — continuing with the next file",
                type, id, filePath);
            return ScanOutcome.Failed;
        }

        await ReplaceRows(type, id, filePath, tracks, key, cancellationToken);
        if (tracks.Count > 0)
        {
            _logger.LogInformation(
                "Embedded track scan: found {Count} subtitle tracks in {Path}",
                tracks.Count, filePath);
        }

        return ScanOutcome.Probed;
    }

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

    /// <summary>Deletes every row of the media item and inserts the fresh scan.</summary>
    private async Task ReplaceRows(
        MediaType type,
        int id,
        string filePath,
        List<ParsedEmbeddedTrack> tracks,
        string scanKey,
        CancellationToken cancellationToken)
    {
        await RemoveRows(type, id, cancellationToken);

        _dbContext.MediaSubtitleTracks.AddRange(tracks.Select(track => new MediaSubtitleTrack
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
        _dbContext.MediaSubtitleTracks.Add(new MediaSubtitleTrack
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
        await _dbContext.SaveChangesAsync(cancellationToken);

        // keep the in-memory marker cache in step with what the DB now holds
        Markers[((int)type, id)] = scanKey;
    }

    private async Task RemoveRows(MediaType type, int id, CancellationToken cancellationToken)
    {
        await _dbContext.MediaSubtitleTracks
            .Where(track => track.MediaType == type && track.MediaId == id)
            .ExecuteDeleteAsync(cancellationToken);
        Markers.Remove(((int)type, id));
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
                    .Select(item => new { item.Id, item.Path, item.FileName })
                    .FirstOrDefaultAsync(cancellationToken);
                return movie == null
                    ? []
                    : [new ScanTarget(MediaType.Movie, movie.Id, movie.Path, movie.FileName)];

            case MediaType.Episode:
                var episode = await _dbContext.Episodes
                    .Where(item => item.Id == id)
                    .Select(item => new { item.Id, item.Path, item.FileName })
                    .FirstOrDefaultAsync(cancellationToken);
                return episode == null
                    ? []
                    : [new ScanTarget(MediaType.Episode, episode.Id, episode.Path, episode.FileName)];

            case MediaType.Season:
                return (await _dbContext.Episodes
                        .Where(episode => episode.SeasonId == id)
                        .Select(episode => new { episode.Id, episode.Path, episode.FileName })
                        .ToListAsync(cancellationToken))
                    .Select(episode => new ScanTarget(MediaType.Episode, episode.Id, episode.Path, episode.FileName))
                    .ToList();

            case MediaType.Show:
                return (await _dbContext.Episodes
                        .Where(episode => _dbContext.Seasons.Any(season =>
                            season.Id == episode.SeasonId && season.ShowId == id))
                        .Select(episode => new { episode.Id, episode.Path, episode.FileName })
                        .ToListAsync(cancellationToken))
                    .Select(episode => new ScanTarget(MediaType.Episode, episode.Id, episode.Path, episode.FileName))
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
        // quotes survive without manual quoting.
        foreach (var argument in new[]
                 {
                     "-v", "quiet",
                     "-print_format", "json",
                     "-show_streams",
                     "-select_streams", "s",
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

        var configured = Environment.GetEnvironmentVariable("LINGARR_FFPROBE_PATH");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            if (File.Exists(configured))
            {
                _ffprobePath = configured;
                _ffprobeResolved = true;
                return _ffprobePath;
            }

            throw new FFprobeUnavailableException(
                $"LINGARR_FFPROBE_PATH points at '{configured}' but no file exists there.");
        }

        var onPath = FindOnPath("ffprobe");
        if (onPath == null)
        {
            throw new FFprobeUnavailableException(
                "ffprobe was not found on PATH. Install ffmpeg (the image does this for you) " +
                "or point LINGARR_FFPROBE_PATH at the ffprobe binary, then run the embedded track scan again.");
        }

        _ffprobePath = onPath;
        _ffprobeResolved = true;
        return _ffprobePath;
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
