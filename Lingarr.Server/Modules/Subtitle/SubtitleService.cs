using System.Collections.Concurrent;
using System.Reflection;
using System.Text;
using Lingarr.Contracts.Translation;
using Lingarr.Core.Configuration;
using Lingarr.Core.Data;
using Lingarr.Core.Entities;
using Lingarr.Server.Modules.Settings;
using Lingarr.Server.Modules.Subtitle;
using Lingarr.Server.Models.FileSystem;
using Lingarr.Server.Modules.Translation;
using Microsoft.EntityFrameworkCore;
using SubtitleValidationOptions = Lingarr.Server.Models.SubtitleValidationOptions;

namespace Lingarr.Server.Modules.Subtitle;

public class SubtitleService : ISubtitleService
{
    private static readonly string[] SupportedExtensions = [".srt", ".ssa", ".ass", ".vtt"];
    private static readonly string[] SupportedCaptions = ["sdh", "cc", "forced", "hi"];

    private readonly ILogger<SubtitleService> _logger;
    private readonly LanguageCodeService _languageCodeService;
    private readonly ISettingService _settingService;
    private readonly ISubtitleEncodingService _encodingService;
    private readonly Func<string, byte[]> _readSubtitleHead;
    private readonly Func<string, IEnumerable<string>> _enumerateFiles;
    private readonly LingarrDbContext? _dbContext;

    // Content-sniff results keyed on (path, mtime, length) — edits invalidate
    // the entry. Static because scans run in fresh scopes; the cache should
    // survive them. Coarse cap, evicts the oldest half when exceeded.
    private static readonly ConcurrentDictionary<SniffCacheKey, (long Tick, string? Language)> SniffCache = new();
    private static long _sniffTick;
    private const int MaxSniffCacheEntries = 500;

    public SubtitleService(
        ILogger<SubtitleService> logger,
        LanguageCodeService languageCodeService,
        ISettingService settingService,
        ISubtitleEncodingService? subtitleEncodingService = null,
        Func<string, byte[]>? subtitleHeadReader = null,
        Func<string, IEnumerable<string>>? directoryEnumerator = null,
        LingarrDbContext? dbContext = null)
    {
        _logger = logger;
        _languageCodeService = languageCodeService;
        _settingService = settingService;
        // Optional collaborators default to the production implementations —
        // tests inject counting stubs without DI ceremony.
        _encodingService = subtitleEncodingService ?? new SubtitleEncodingService();
        _readSubtitleHead = subtitleHeadReader ?? ReadSubtitleHead;
        // Audit P0-4: one recursive traversal per directory instead of one
        // per supported extension.
        _enumerateFiles = directoryEnumerator ?? EnumerateFilesResiliently;
        // Audit P1-7: the sniff results are additionally persisted in
        // subtitle_language_cache — the 500-entry in-memory cap is useless on
        // a 100k library, and every scan of an untagged file would re-read
        // its head. Null keeps the pre-P1-7 behavior (tests without a
        // database); production DI always supplies the scoped context.
        _dbContext = dbContext;
    }

    private readonly record struct SniffCacheKey(string Path, long LastWriteUtcTicks, long Length);

    /// <summary>
    /// QA4 P1-2: production directory traversal. The old
    /// Directory.EnumerateFiles(AllDirectories) was a lazy enumeration that
    /// threw UnauthorizedAccessException (or an IOException) mid-iteration
    /// as soon as the walk hit an unreadable subdirectory — on a NAS that
    /// killed the whole statistics scan hours in. This explicit stack walk
    /// isolates every directory: one that cannot be listed is logged and
    /// skipped, and the rest of the tree still enumerates.
    /// </summary>
    private IEnumerable<string> EnumerateFilesResiliently(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            string[] files;
            try
            {
                files = Directory.GetFiles(directory);
                foreach (var subdirectory in Directory.EnumerateDirectories(directory))
                {
                    pending.Push(subdirectory);
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException
                or IOException
                or DirectoryNotFoundException)
            {
                _logger.LogWarning(ex,
                    "Skipping unreadable directory {Directory} while collecting subtitles — the rest of the tree is still scanned",
                    directory);
                continue;
            }

            // Yields must stay outside the try — iterator blocks cannot
            // yield inside a try that has a catch.
            foreach (var file in files)
            {
                yield return file;
            }
        }
    }

    /// <inheritdoc />
    public async Task<List<Subtitles>> GetAllSubtitles(string path)
    {
        if (!Directory.Exists(path))
        {
            _logger.LogInformation(
                "Failed to collect subtitles in path {Path}. Try reindexing or verify that the media is correctly set up in the source system.",
                path);
            return new List<Subtitles>();
        }

        // Audit P0-4: ONE physical traversal of the directory tree. The old
        // code ran Directory.GetFiles(*, AllDirectories) once per extension
        // (4 recursive sweeps); this enumerates once with "*" and buckets the
        // files by their (lowercased) extension. The extension buckets are
        // emitted in SupportedExtensions order so the result list keeps the
        // exact ordering the per-extension sweeps produced — consumers that
        // pick a "first match" see the same list. Matching is
        // case-insensitive, so ".SRT" is found on case-sensitive filesystems
        // too; extensions beyond the known set (including the Windows
        // "*.srt"-matches-".srtx" quirk) are excluded.
        var filesByExtension = SupportedExtensions.ToDictionary(
            extension => extension,
            _ => new List<string>(),
            StringComparer.Ordinal);

        foreach (var file in _enumerateFiles(path))
        {
            var extension = Path.GetExtension(file).ToLowerInvariant();
            if (filesByExtension.TryGetValue(extension, out var bucket))
            {
                bucket.Add(file);
            }
        }

        var subtitles = new List<Subtitles>();
        foreach (var extension in SupportedExtensions)
        {
            subtitles.AddRange(filesByExtension[extension].Select(file => ParseSubtitleFile(file, extension)));
        }

        // Audit P1-7: persist the sniff results queued while parsing — one
        // SaveChanges per directory tree, not one write per file.
        await FlushPersistedSniffsAsync();

        return subtitles;
    }

    /// <summary>
    /// Parses one discovered subtitle file: caption tag, language tag(s)
    /// (fansub and dual-language forms) and — only when the file name carries
    /// no language at all — the content sniff. The extension comes from the
    /// bucket the file was collected into, so <see cref="Subtitles.Format"/>
    /// stays the canonical lowercase form.
    /// </summary>
    private Subtitles ParseSubtitleFile(string file, string extension)
    {
        var fileName = Path.GetFileNameWithoutExtension(file);
        var parts = fileName.Split('.').Reverse().ToList();
        var language = "";
        var languages = new List<string>();
        var caption = "";

        // First look for caption
        var captionPart = parts.FirstOrDefault(p => SupportedCaptions.Contains(p.ToLower()));
        if (captionPart != null)
        {
            caption = captionPart.ToLower();
            parts.Remove(captionPart);
        }

        // Then look for language in remaining parts. Fansub tags
        // (chs/sc/scjp/简日…) parse into one or two canonical codes;
        // the first hit wins and a dual-language file carries both.
        var languagePart = parts.FirstOrDefault(p => SubtitleLanguageParser.TryParse(p, out _));
        if (languagePart != null
            && SubtitleLanguageParser.TryParse(languagePart, out var parsed))
        {
            languages = parsed.ToList();
            language = parsed[0];
            parts.Remove(languagePart);
        }
        // Hindi is an exception, if we didn't find a language, and we did found Hindi, We set that as language
        else if (caption == "hi" && language == "")
        {
            language = caption;
            languages = [caption];
            caption = "";
        }

        // Filename tags always win; content sniffing only rescues files
        // that would otherwise land on "unknown" (requirement F1).
        if (string.IsNullOrEmpty(language))
        {
            var sniffed = SniffContentLanguage(file);
            if (sniffed is not null)
            {
                language = sniffed;
                languages = [sniffed];
            }
        }

        return new Subtitles
        {
            Path = file,
            FileName = fileName,
            Language = string.IsNullOrEmpty(language) ? "unknown" : language,
            Languages = languages.Count > 0 ? languages : ["unknown"],
            Caption = caption,
            Format = extension
        };
    }

    /// <summary>
    /// Reads the file head, normalises it to UTF-8 (GB18030/Big5/Shift-JIS files
    /// decode correctly through <see cref="ISubtitleEncodingService"/>) and sniffs
    /// the content language. Results are cached on (path, mtime, length) so a
    /// repeated scan of unchanged files performs no file reads — L1 in the
    /// static in-process cache, L2 (audit P1-7) in subtitle_language_cache so
    /// a fresh process answers from the database instead of re-reading the
    /// file head.
    /// </summary>
    private string? SniffContentLanguage(string file)
    {
        try
        {
            var info = new FileInfo(file);
            if (!info.Exists)
            {
                return null;
            }

            var key = new SniffCacheKey(file, info.LastWriteTimeUtc.Ticks, info.Length);
            if (SniffCache.TryGetValue(key, out var cached))
            {
                return cached.Language;
            }

            // L2 read-through: a persisted row that matches (mtime, length)
            // answers directly — including a null language ("sniffed, still
            // unknown"), which is cached like any other result.
            var persisted = FindPersistedSniff(key);
            if (persisted != null)
            {
                CacheSniffResult(key, persisted.Language);
                return persisted.Language;
            }

            var bytes = _readSubtitleHead(file);
            var text = Encoding.UTF8.GetString(_encodingService.ConvertToUtf8(bytes));
            var extension = Path.GetExtension(file).ToLowerInvariant();
            if (extension is ".ass" or ".ssa")
            {
                // Style layers ([Script Info], Format:/Style: lines, the nine
                // comma-separated fields before the text) are Latin noise that
                // would push the sample out of the Han window.
                text = StripAssStyleLayers(text);
            }

            var detected = SubtitleContentLanguageDetector.DetectLanguage(text);

            _logger.LogDebug("Sniffed content language '{Language}' for {File}", detected ?? "unknown", file);
            CacheSniffResult(key, detected);
            QueuePersistedSniff(key, detected);
            return detected;
        }
        catch (Exception ex)
        {
            // A broken file must never break the scan.
            _logger.LogDebug(ex, "Content language sniff failed for {File}", file);
            return null;
        }
    }

    /// <summary>
    /// Reads the persisted sniff row for the key's path. Returns null when
    /// the row is absent or was written for a different (mtime, length) —
    /// i.e. the file changed since. Database problems degrade to "not
    /// cached" (the sniff still runs) instead of failing the scan.
    /// </summary>
    private SubtitleLanguageCache? FindPersistedSniff(SniffCacheKey key)
    {
        if (_dbContext == null)
        {
            return null;
        }

        try
        {
            var row = _dbContext.SubtitleLanguageCaches.Find(key.Path);
            return row != null
                && row.MtimeTicks == key.LastWriteUtcTicks
                && row.Length == key.Length
                ? row
                : null;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Reading the persisted sniff cache failed for {File}", key.Path);
            return null;
        }
    }

    /// <summary>
    /// Stages the upsert of one sniff result; the write itself happens in
    /// <see cref="FlushPersistedSniffsAsync"/> so a directory tree costs one
    /// SaveChanges instead of one per file. Find() re-attaches the tracked
    /// row the read-through already loaded, so an edited file updates its
    /// row in place instead of colliding on the path primary key.
    /// </summary>
    private void QueuePersistedSniff(SniffCacheKey key, string? language)
    {
        if (_dbContext == null)
        {
            return;
        }

        try
        {
            var row = _dbContext.SubtitleLanguageCaches.Find(key.Path);
            if (row == null)
            {
                _dbContext.SubtitleLanguageCaches.Add(new SubtitleLanguageCache
                {
                    Path = key.Path,
                    MtimeTicks = key.LastWriteUtcTicks,
                    Length = key.Length,
                    Language = language,
                    UpdatedAt = DateTimeOffset.UtcNow
                });
            }
            else
            {
                row.MtimeTicks = key.LastWriteUtcTicks;
                row.Length = key.Length;
                row.Language = language;
                row.UpdatedAt = DateTimeOffset.UtcNow;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Staging the sniff cache row failed for {File}", key.Path);
        }
    }

    /// <summary>
    /// Saves the staged sniff rows. Saved (or failed) rows are detached so a
    /// full-library scan does not grow the change tracker monotonically, and
    /// a failure cannot be retried unexpectedly by the next SaveChanges on
    /// the shared scoped context — the rows are simply re-sniffed next scan.
    /// </summary>
    private async Task FlushPersistedSniffsAsync()
    {
        if (_dbContext == null)
        {
            return;
        }

        var pending = _dbContext.ChangeTracker
            .Entries<SubtitleLanguageCache>()
            .Where(entry => entry.State is EntityState.Added or EntityState.Modified)
            .ToList();
        if (pending.Count == 0)
        {
            return;
        }

        try
        {
            await _dbContext.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex,
                "Persisting {Count} sniff cache rows failed — they will be re-sniffed on the next scan",
                pending.Count);
        }
        finally
        {
            foreach (var entry in pending)
            {
                entry.State = EntityState.Detached;
            }
        }
    }

    /// <summary>
    /// Keeps only the text column of ASS/SSA Dialogue lines (after the ninth
    /// comma); metadata and style sections drop out entirely.
    /// </summary>
    public static string StripAssStyleLayers(string content)
    {
        var builder = new StringBuilder();
        foreach (var rawLine in content.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r').Trim();
            if (line.Length == 0
                || line.StartsWith('[')
                || line.StartsWith("Format:", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("Style:", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("Comment:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (line.StartsWith("Dialogue:", StringComparison.OrdinalIgnoreCase))
            {
                // Text is field 10; the text column may itself contain commas.
                var rest = line["Dialogue:".Length..].TrimStart();
                var commas = 0;
                var start = 0;
                for (var i = 0; i < rest.Length; i++)
                {
                    if (rest[i] != ',')
                    {
                        continue;
                    }

                    commas++;
                    if (commas == 9)
                    {
                        start = i + 1;
                        break;
                    }
                }

                builder.AppendLine(rest[start..]);
            }
        }

        return builder.ToString();
    }

    private static void CacheSniffResult(SniffCacheKey key, string? language)
    {
        SniffCache[key] = (Interlocked.Increment(ref _sniffTick), language);
        if (SniffCache.Count <= MaxSniffCacheEntries)
        {
            return;
        }

        // Cap exceeded: evict the oldest half. Coarse, but this only guards a
        // long-running process against unbounded growth — good enough.
        foreach (var victim in SniffCache.OrderBy(kv => kv.Value.Tick)
                     .Take(MaxSniffCacheEntries / 2)
                     .Select(kv => kv.Key)
                     .ToList())
        {
            SniffCache.TryRemove(victim, out _);
        }
    }

    /// <summary>Reads the first 64 KB of a subtitle file — plenty of text for language detection.</summary>
    private static byte[] ReadSubtitleHead(string path)
    {
        const int headBytes = 64 * 1024;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var length = (int)Math.Min(stream.Length, headBytes);
        var buffer = new byte[length];
        var read = 0;
        while (read < length)
        {
            var chunk = stream.Read(buffer, read, length - read);
            if (chunk <= 0)
            {
                break;
            }

            read += chunk;
        }

        return read == length ? buffer : buffer[..read];
    }

    /// <inheritdoc />
    public async Task<List<SubtitleItem>> ReadSubtitles(string filePath)
    {
        var extension = Path.GetExtension(filePath).ToLower();
        ISubtitleParser parser = extension switch
        {
            ".srt" => new SrtParser(),
            ".ssa" or ".ass" => new SsaParser(),
            ".vtt" => new VttParser(),
            _ => throw new NotSupportedException($"Subtitle format {extension} is not supported")
        };

        await using var fileStream = File.OpenRead(filePath);
        return parser.ParseStream(fileStream, Encoding.UTF8);
    }

    /// <inheritdoc />
    public async Task WriteSubtitles(string filePath, List<SubtitleItem> subtitles, bool stripSubtitleFormatting, bool bilingual = false)
    {
        var extension = Path.GetExtension(filePath).ToLower();
        ISubtitleWriter writer = extension switch
        {
            ".srt" => new SrtWriter(),
            ".ssa" or ".ass" => new SsaWriter(),
            ".vtt" => new VttWriter(),
            _ => throw new NotSupportedException($"Subtitle format {extension} is not supported")
        };

        await using var fileStream = File.Create(filePath);
        await writer.WriteStreamAsync(fileStream, subtitles, stripSubtitleFormatting, bilingual);
    }

    /// <inheritdoc />
    public async Task<string> CreateFilePath(string originalPath, string targetLanguage, string subtitleTag)
    {
        var extension = Path.GetExtension(originalPath);
        var fileNameWithoutExtension = Path.GetFileNameWithoutExtension(originalPath);
        var parts = fileNameWithoutExtension.Split('.');
        var reversedParts = parts.Reverse().ToList();

        // Extract caption if present
        string? caption = null;
        int captionIndex = reversedParts.FindIndex(p => SupportedCaptions.Contains(p.ToLower()));
        if (captionIndex != -1)
        {
            caption = reversedParts[captionIndex].ToLowerInvariant();
            reversedParts.RemoveAt(captionIndex);
        }

        // Extract language (single- or dual-language tag: en, chs, scjp, 简日…)
        int languageIndex = reversedParts.FindIndex(part => SubtitleLanguageParser.TryParse(part, out _));
        if (languageIndex != -1)
        {
            reversedParts.RemoveAt(languageIndex);
        }

        // Resolve target language code
        string? targetLanguageCode = null;
        if (!string.IsNullOrEmpty(targetLanguage)
            && !TryGetLanguageByPart(targetLanguage, out targetLanguageCode))
        {
            targetLanguageCode = targetLanguage;
        }

        // Reconstruct the cleaned base name (caption/language segments stripped)
        var baseName = string.Join(".", reversedParts.AsEnumerable().Reverse());

        // Expand the naming template: {原名} = cleaned base name, {语言} = target
        // language tag. A missing/blank setting falls back to the historical
        // "{base}.{language}" — no migration needed.
        var template = await _settingService.GetSetting(SettingKeys.Translation.SubtitleNamingTemplate);
        if (string.IsNullOrWhiteSpace(template))
        {
            template = SettingKeys.Translation.DefaultSubtitleNamingTemplate;
        }

        var fileName = template
            .Replace("{原名}", baseName)
            .Replace("{语言}", targetLanguageCode?.ToLowerInvariant() ?? string.Empty);

        // An empty {语言} (e.g. remove_language_tag) leaves stray dots behind.
        while (fileName.Contains(".."))
        {
            fileName = fileName.Replace("..", ".");
        }
        fileName = fileName.Trim('.');

        var newParts = new List<string> { fileName };

        // Add caption if present
        if (!string.IsNullOrEmpty(caption))
        {
            newParts.Add(caption);
        }

        // Add tag if provided
        if (!string.IsNullOrEmpty(subtitleTag))
        {
            newParts.Add(subtitleTag.ToLowerInvariant());
        }

        // Build new file name and path (extension still follows the output format)
        var newFileName = string.Join(".", newParts) + extension;
        var directory = Path.GetDirectoryName(originalPath) ?? string.Empty;
        return Path.Combine(directory, newFileName);
    }

    /// <inheritdoc />
    public List<SubtitleItem> FixOverlappingSubtitles(List<SubtitleItem> subtitles)
    {
        const int gap = 20;
        const int minimumDuration = 500;
        var fixCount = 0;

        for (var index = 0; index < subtitles.Count - 1; index++)
        {
            var current = subtitles[index];
            var next = subtitles[index + 1];

            if (current.EndTime + gap <= next.StartTime)
            {
                continue;
            }

            var trimmedEndTime = next.StartTime - gap;
            if (trimmedEndTime - current.StartTime < minimumDuration)
            {
                // Intentionally stacked cues, such as dual-speaker dialogue sharing a
                // timestamp, would become unreadable when trimmed. Keep their timing.
                continue;
            }

            current.EndTime = trimmedEndTime;
            fixCount++;
            _logger.LogDebug("Trimmed subtitle #{Position} to remove overlap with #{NextPosition}",
                current.Position, next.Position);
        }

        _logger.LogInformation("Fixed {FixCount} overlapping subtitles by trimming end times", fixCount);
        return subtitles;
    }

    public bool ValidateSubtitle(string filePath, SubtitleValidationOptions options)
    {
        if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
        {
            _logger.LogWarning("Cannot validate non-existent subtitle file: {FilePath}", filePath);
            return false;
        }

        try
        {
            using var stream = File.OpenRead(filePath);
            return ValidateSubtitleStream(stream, Encoding.UTF8, options);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error validating subtitle file: {FilePath}", filePath);
            return false;
        }
    }

    private bool ValidateSubtitleStream(
        Stream subtitleStream,
        Encoding encoding,
        SubtitleValidationOptions options)
    {
        try
        {
            if (!subtitleStream.CanSeek)
            {
                throw new ArgumentException("Stream must be seekable to validate file size.");
            }

            // Validate file size
            if (subtitleStream.Length > options.MaxFileSizeBytes)
            {
                _logger.LogWarning("Subtitle file exceeds maximum size of {MaxSize} bytes", options.MaxFileSizeBytes);
                return false;
            }

            // Reset stream position to the beginning before parsing
            subtitleStream.Seek(0, SeekOrigin.Begin);

            var parser = new SrtParser();
            var subtitles = parser.ParseStream(subtitleStream, encoding);

            if (subtitles.Count < 2)
            {
                _logger.LogWarning("Subtitle file contains less than 2 valid subtitles");
                return false;
            }

            var expectedPosition = 1;
            SubtitleItem previousItem = null;

            foreach (var item in subtitles)
            {
                // Choose the appropriate text content based on stripSubtitleFormatting
                List<string> contentLines = options.StripSubtitleFormatting ? item.PlaintextLines : item.Lines;

                if (contentLines.Count == 0 || contentLines.All(string.IsNullOrWhiteSpace))
                {
                    _logger.LogWarning("Subtitle at position {Position} has no content", item.Position);
                    return false;
                }

                // Get the combined text for length checks
                string combinedText = string.Join(" ", contentLines);

                // Check that subtitle has at least the minimum length
                string trimmedText = combinedText.Trim();
                if (trimmedText.Length < options.MinSubtitleLength)
                {
                    _logger.LogWarning(
                        "Subtitle at position {Position} is too short. Length: {Length}, Minimum: {MinLength}",
                        item.Position, trimmedText.Length, options.MinSubtitleLength);
                    return false;
                }

                // Check sequence number/position
                if (item.Position != expectedPosition)
                {
                    _logger.LogWarning("Subtitle position mismatch. Expected: {Expected}, Found: {Found}",
                        expectedPosition, item.Position);
                    return false;
                }

                expectedPosition++;

                // Validate timing
                if (item.StartTime >= item.EndTime)
                {
                    _logger.LogWarning(
                        "Subtitle at position {Position} has invalid timing. StartTime: {StartTime}, EndTime: {EndTime}",
                        item.Position, item.StartTime, item.EndTime);
                    return false;
                }

                // Validate text length
                if (combinedText.Length > options.MaxSubtitleLength)
                {
                    _logger.LogWarning(
                        "Subtitle at position {Position} exceeds maximum length. Length: {Length}, Maximum: {MaxLength}",
                        item.Position, combinedText.Length, options.MaxSubtitleLength);
                    return false;
                }

                // Check for realistic durations
                var durationMs = item.EndTime - item.StartTime;
                if (durationMs < options.MinDurationMs || durationMs > options.MaxDurationSecs * 1000)
                {
                    _logger.LogWarning(
                        "Subtitle at position {Position} has unrealistic duration: {Duration}ms. Valid range: {MinDuration}ms to {MaxDuration}ms",
                        item.Position, durationMs, options.MinDurationMs, options.MaxDurationSecs * 1000);
                    return false;
                }

                // Check for overlapping with previous subtitle
                if (previousItem != null && item.StartTime < previousItem.EndTime)
                {
                    _logger.LogWarning(
                        "Subtitle at position {Position} overlaps with previous subtitle. Current start: {CurrentStart}, Previous end: {PreviousEnd}",
                        item.Position, item.StartTime, previousItem.EndTime);
                    return false;
                }

                // Check for control characters
                if (contentLines.Any(line => line.Any(c => char.IsControl(c) && c != '\n' && c != '\r')))
                {
                    _logger.LogWarning("Subtitle at position {Position} contains invalid control characters",
                        item.Position);
                    return false;
                }

                previousItem = item;
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Subtitle validation failed");
            return false;
        }
    }
    
    /// <summary>
    /// Adds an introductory subtitle at the beginning that identifies the translation service used.
    /// The intro duration is automatically adjusted to avoid overlapping with existing subtitles.
    /// </summary>
    /// <param name="serviceType">The translation service type (e.g., "openai", "google").</param>
    /// <param name="translatedSubtitles">The subtitle list to prepend the intro to.</param>
    /// <param name="translationService">The service instance used to extract model name if available.</param>
    public void AddTranslatorInfo(string serviceType, List<SubtitleItem> translatedSubtitles,
        ITranslationService translationService)
    {
        // Check if the service has a ModelName property
        var serviceName = char.ToUpper(serviceType[0]) + serviceType[1..];

        var modelField = translationService.GetType().GetField("_model",
            BindingFlags.NonPublic | BindingFlags.Instance);

        if (modelField != null)
        {
            var modelName = modelField.GetValue(translationService)?.ToString();
            if (!string.IsNullOrEmpty(modelName))
            {
                serviceName += " - " + modelName;
            }
        }

        var introText = $"# Translated with Lingarr using {serviceName} translator #";
        var introDuration = 5.0; // Default duration in seconds

        // Check if there are existing subtitles and if the first one starts before our intro ends
        if (translatedSubtitles.Count > 0)
        {
            var firstSubtitle = translatedSubtitles[0];
            var firstSubtitleStartTimeSeconds = firstSubtitle.StartTime / 1000.0;

            // If the first subtitle starts before our intro would end, adjust the intro duration
            if (firstSubtitleStartTimeSeconds < introDuration)
            {
                // Leave a small gap (e.g., 0.5 seconds) between intro and first subtitle
                introDuration = Math.Max(0.5, firstSubtitleStartTimeSeconds - 0.5);
                _logger.LogInformation(
                    "Adjusted intro duration to {introDuration} seconds to avoid overlap with first subtitle at {firstStart} seconds",
                    introDuration, firstSubtitleStartTimeSeconds);
            }
        }

        var introSubtitle = new SubtitleItem
        {
            StartTime = 0,
            EndTime = (int)(introDuration * 1000),
            Lines = [introText],
            PlaintextLines = [introText],
            TranslatedLines = [introText],
            // SsaWriter reads items[0].SsaFormat for the header — carry it over.
            SsaFormat = translatedSubtitles.FirstOrDefault()?.SsaFormat
        };

        translatedSubtitles.Insert(0, introSubtitle);
    }

    /// <inheritdoc />
    public async Task<List<Subtitles>> GetSubtitles(string path, string fileName)
    {
        var allSubtitles = await GetAllSubtitles(path);
        return allSubtitles
            .Where(s => s.FileName.StartsWith(fileName + ".") || s.FileName == fileName)
            .ToList();
    }

    /// <inheritdoc />
    public SelectedSourceSubtitle? SelectSourceSubtitle(
        List<Subtitles> matchingSubtitles,
        HashSet<string> sourceCodes,
        string ignoreCaptions)
    {
        // A dual-language file (scjp/简日…) is a valid source for every
        // language it carries, so matching runs over the full language set.
        var availableLanguages = matchingSubtitles
            .SelectMany(s => s.Languages.Count > 0 ? s.Languages : [s.Language])
            .Select(lang => lang.ToLowerInvariant())
            .ToHashSet();

        var sourceLanguage = sourceCodes.FirstOrDefault(codes => availableLanguages.Contains(codes));
        if (sourceLanguage == null)
        {
            return null;
        }

        bool CarriesSource(Subtitles s) => (s.Languages.Count > 0 ? s.Languages : [s.Language])
            .Any(lang => string.Equals(lang, sourceLanguage, StringComparison.OrdinalIgnoreCase));

        var sourceSubtitle = ignoreCaptions == "true"
            ? matchingSubtitles.FirstOrDefault(s => CarriesSource(s) && string.IsNullOrEmpty(s.Caption))
                ?? matchingSubtitles.FirstOrDefault(CarriesSource)
            : matchingSubtitles.FirstOrDefault(CarriesSource);

        if (sourceSubtitle == null)
        {
            return null;
        }

        return new SelectedSourceSubtitle
        {
            Subtitle = sourceSubtitle,
            SourceLanguage = sourceLanguage,
            AvailableLanguages = availableLanguages
        };
    }

    /// <summary>
    /// Tries to match the specified <paramref name="part"/> against any known culture/language
    /// and outputs the two-letter ISO language code if found.
    /// </summary>
    /// <param name="part">
    /// The string segment from the file name that may represent a culture or language code.
    /// </param>
    /// <param name="languageCode">
    /// When this method returns, contains the two-letter ISO language code corresponding to the 
    /// matched culture; otherwise, null if no match is found.
    /// </param>
    private bool TryGetLanguageByPart(string part, out string? languageCode)
    {
        languageCode = null;
        if (!_languageCodeService.Validate(part))
        {
            return false;
        }
        
        try
        {
            languageCode = LanguageCodeService.GetNormalizedCode(part);
            return true;
        }
        catch (ArgumentException)
        {
            // Should not happen as we validated
            return false;
        }
    }
}
