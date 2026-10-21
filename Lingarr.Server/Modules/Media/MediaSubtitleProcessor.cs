using System.Security.Cryptography;
using Lingarr.Contracts.Interfaces;
using Lingarr.Contracts.Models;
using Lingarr.Core.Configuration;
using Lingarr.Core.Data;
using Lingarr.Core.Enum;
using Lingarr.Core.Interfaces;
using Lingarr.Server.Models;
using Lingarr.Server.Models.FileSystem;
using Microsoft.EntityFrameworkCore;
using Lingarr.Server.Modules.Settings;
using Lingarr.Server.Modules.Subtitle;
using Lingarr.Server.Modules.Translation;

namespace Lingarr.Server.Modules.Media;

public class MediaSubtitleProcessor : IMediaSubtitleProcessor
{
    private readonly ITranslationRequestService _translationRequestService;
    private readonly ILogger<IMediaSubtitleProcessor> _logger;
    private readonly ISubtitleService _subtitleService;
    private readonly ISettingService _settingService;
    private readonly LingarrDbContext _dbContext;
    private readonly IEmbeddedTrackService _embeddedTrackService;
    private string _hash = string.Empty;
    private IMedia _media = null!;
    private MediaType _mediaType;

    public MediaSubtitleProcessor(
        ITranslationRequestService translationRequestService,
        ILogger<IMediaSubtitleProcessor> logger,
        ISettingService settingService,
        ISubtitleService subtitleService,
        LingarrDbContext dbContext,
        IEmbeddedTrackService embeddedTrackService)
    {
        _translationRequestService = translationRequestService;
        _settingService = settingService;
        _subtitleService = subtitleService;
        _dbContext = dbContext;
        _logger = logger;
        _embeddedTrackService = embeddedTrackService;
    }

    /// <inheritdoc />
    public async Task<bool> ProcessMedia(
        IMedia media,
        MediaType mediaType)
    {
        if (media.Path == null || media.FileName == null)
        {
            _logger.LogWarning(
                "Skipping media processing: Path or FileName is null for {MediaType} with ID {MediaId}",
                mediaType, media.Id);
            return false;
        }

        var subtitles = await _subtitleService.GetSubtitles(media.Path, media.FileName);

        // F2b opt-in: text-based embedded tracks join the candidate pool as
        // lightweight entries whose (deterministic) materialization path only
        // gets a real file when the track is actually picked as the source.
        var embeddedStreams = await MergeEmbeddedTracks(subtitles, media, mediaType);
        if (embeddedStreams != null)
        {
            subtitles = embeddedStreams.Value.Candidates;
        }

        if (!subtitles.Any())
        {
            return false;
        }

        var sourceLanguages = await GetLanguagesSetting<SourceLanguage>(SettingKeys.Translation.SourceLanguages);
        var targetLanguages = await GetLanguagesSetting<TargetLanguage>(SettingKeys.Translation.TargetLanguages);
        var ignoreCaptions = await _settingService.GetSetting(SettingKeys.Translation.IgnoreCaptions) ?? "false";

        _media = media;
        _mediaType = mediaType;
        _hash = CreateHash(subtitles, sourceLanguages, targetLanguages, ignoreCaptions);
        if (!string.IsNullOrEmpty(media.MediaHash) && media.MediaHash == _hash)
        {
            return false;
        }

        _logger.LogInformation("Initiating subtitle processing.");
        return await ProcessSubtitles(
            subtitles,
            sourceLanguages,
            targetLanguages,
            ignoreCaptions,
            embeddedStreams?.StreamIndexes);
    }

    /// <summary>
    /// Merged candidate pool when <c>consume_embedded_tracks</c> is on: the
    /// external files plus one synthetic entry per text-based embedded track.
    /// The synthetic entry carries the track's future translation-source path
    /// (<see cref="EmbeddedTrackService.PrepareTranslationSource"/> writes it
    /// there), so the media hash stays stable once materialized. Extraction
    /// itself is deferred until an embedded entry is actually selected.
    /// </summary>
    private async Task<(List<Subtitles> Candidates, Dictionary<string, int> StreamIndexes)?> MergeEmbeddedTracks(
        List<Subtitles> externalSubtitles,
        IMedia media,
        MediaType mediaType)
    {
        if (mediaType is not (MediaType.Movie or MediaType.Episode))
        {
            return null;
        }

        var consume = await _settingService.GetSetting(SettingKeys.Translation.ConsumeEmbeddedTracks);
        if (consume != "true")
        {
            return null;
        }

        var tracks = await _dbContext.MediaSubtitleTracks
            .Where(track => track.MediaType == mediaType
                            && track.MediaId == media.Id
                            && track.StreamIndex >= 0
                            && track.IsTextBased
                            && track.Language != null)
            .OrderBy(track => track.StreamIndex)
            .ToListAsync();
        if (tracks.Count == 0)
        {
            return null;
        }

        var candidates = new List<Subtitles>(externalSubtitles);
        var externalPaths = externalSubtitles
            .Select(s => s.Path)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var streamIndexes = new Dictionary<string, int>();
        foreach (var track in tracks)
        {
            var path = Path.Combine(media.Path!, EmbeddedTrackService.TranslationSourceFileName(media.FileName, track.Language!));
            // An external file already sitting at the materialization path IS
            // that source — never shadow it with an extraction.
            if (externalPaths.Contains(path))
            {
                continue;
            }

            candidates.Add(new Subtitles
            {
                Path = path,
                FileName = Path.GetFileNameWithoutExtension(path),
                Language = track.Language!,
                Languages = [track.Language!],
                Caption = string.Empty,
                Format = ".srt"
            });
            streamIndexes[path] = track.StreamIndex;
        }

        _logger.LogDebug(
            "Merged {Count} embedded text track(s) of {MediaType} {MediaId} into the source candidate pool",
            tracks.Count, mediaType, media.Id);
        return (candidates, streamIndexes);
    }

    /// <summary>
    /// Processes subtitle files for translation based on configured languages.
    /// </summary>
    /// <param name="subtitles">List of subtitle files to process.</param>
    /// <param name="sourceLanguages">The source languages.</param>
    /// <param name="targetLanguages">The target languages.</param>
    /// <param name="ignoreCaptions">The ignore captions setting.</param>
    /// <param name="embeddedStreamIndexes">
    /// Translation-source path → stream index of the synthetic embedded
    /// entries inside <paramref name="subtitles"/> (F2b); null when embedded
    /// tracks were not merged.
    /// </param>
    /// <returns>True if new translation requests were created, false otherwise.</returns>
    private async Task<bool> ProcessSubtitles(
        List<Subtitles> subtitles,
        HashSet<string> sourceLanguages,
        HashSet<string> targetLanguages,
        string ignoreCaptions,
        Dictionary<string, int>? embeddedStreamIndexes = null)
    {
        if (sourceLanguages.Count == 0 || targetLanguages.Count == 0)
        {
            _logger.LogWarning(
                "Source or target languages are empty. Source languages: {SourceCount}, Target languages: {TargetCount}",
                sourceLanguages.Count, targetLanguages.Count);
            await UpdateHash();
            return false;
        }

        var selected = _subtitleService.SelectSourceSubtitle(subtitles, sourceLanguages, ignoreCaptions);
        if (selected == null || !targetLanguages.Any())
        {
            _logger.LogWarning(
                "No valid source language or target languages found for media {FileName}. " +
                "Existing languages: {ExistingLanguages}, " +
                "Source languages: {SourceLanguages}, " +
                "Target languages: {TargetLanguages}",
                string.Join(", ", _media?.FileName),
                string.Join(", ", subtitles.Select(s => s.Language.ToLowerInvariant())),
                string.Join(", ", sourceLanguages),
                string.Join(", ", targetLanguages));

            await UpdateHash();
            return false;
        }

        var languagesToTranslate = targetLanguages.Except(selected.AvailableLanguages).ToList();
        if (ignoreCaptions == "true")
        {
            var targetLanguagesWithCaptions = subtitles
                .Where(s => targetLanguages.Contains(s.Language) && !string.IsNullOrEmpty(s.Caption))
                .Select(s => s.Language)
                .Distinct()
                .ToList();

            if (targetLanguagesWithCaptions.Any())
            {
                _logger.LogInformation(
                    "Translation skipped because captions exist for target languages: {CaptionLanguages} and ignoreCaptions is disabled",
                    string.Join(", ", targetLanguagesWithCaptions));
                await UpdateHash();
                return false;
            }
        }
        
        var activeStatuses = new[]
        {
            TranslationStatus.Pending,
            TranslationStatus.InProgress,
            TranslationStatus.Completed
        };
        var existingTranslationRequests = await _dbContext.TranslationRequests
            .Where(translationRequest => translationRequest.MediaId == _media.Id
                                         && translationRequest.MediaType == _mediaType
                                         && activeStatuses.Contains(translationRequest.Status))
            .Select(translationRequest => translationRequest.TargetLanguage)
            .Distinct()
            .ToListAsync();

        languagesToTranslate = languagesToTranslate.Except(existingTranslationRequests).ToList();
        if (!languagesToTranslate.Any())
        {
            await UpdateHash();
            return false;
        }

        foreach (var targetLanguage in languagesToTranslate)
        {
            var subtitlePath = selected.Subtitle.Path;
            var subtitleFormat = selected.Subtitle.Format;

            // The selected source is a synthetic embedded entry: run ffmpeg
            // now and swap in the materialized translation source. A failed
            // extraction skips this media (the run continues), matching how a
            // missing file is treated.
            if (embeddedStreamIndexes != null
                && embeddedStreamIndexes.TryGetValue(selected.Subtitle.Path, out var streamIndex))
            {
                try
                {
                    subtitlePath = await _embeddedTrackService.PrepareTranslationSource(
                        _mediaType, _media.Id, streamIndex);
                    subtitleFormat = ".srt";
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "Embedded track extraction failed for {MediaType} {MediaId} stream {StreamIndex} — skipping this media",
                        _mediaType, _media.Id, streamIndex);
                    await UpdateHash();
                    return false;
                }
            }

            await _translationRequestService.CreateRequest(new TranslateAbleSubtitle
            {
                MediaId = _media.Id,
                MediaType = _mediaType,
                SubtitlePath = subtitlePath,
                TargetLanguage = targetLanguage,
                SourceLanguage = selected.SourceLanguage,
                SubtitleFormat = subtitleFormat
            });
            _logger.LogInformation(
                "Initiating translation from {sourceLanguage} to {targetLanguage} for {subtitleFile}",
                selected.SourceLanguage,
                targetLanguage,
                subtitlePath);
        }

        await UpdateHash();
        return true;
    }

    /// <summary>
    /// Creates a hash of the current subtitle file state.
    /// </summary>
    /// <param name="subtitles">List of subtitle file paths to include in the hash.</param>
    /// <param name="sourceLanguages">The source languages.</param>
    /// <param name="targetLanguages">The target languages.</param>
    /// <param name="ignoreCaptions">The ignore captions setting.</param>
    /// <returns>A Base64 encoded string representing the hash of the current subtitle state.</returns>
    private string CreateHash(
        List<Subtitles> subtitles,
        HashSet<string> sourceLanguages,
        HashSet<string> targetLanguages,
        string ignoreCaptions)
    {
        using var sha256 = SHA256.Create();
        var subtitlePaths = string.Join("|", subtitles.Select(subtitle => subtitle.Path)
            .ToList()
            .OrderBy(f => f));
        
        var sourceLangs = string.Join(",", sourceLanguages.OrderBy(l => l));
        var targetLangs = string.Join(",", targetLanguages.OrderBy(l => l));
        
        var hashInput = $"{subtitlePaths}|{sourceLangs}|{targetLangs}|{ignoreCaptions}";
        var hashBytes = sha256.ComputeHash(System.Text.Encoding.UTF8.GetBytes(hashInput));
        return Convert.ToBase64String(hashBytes);
    }

    /// <summary>
    /// Retrieves language settings from the application configuration.
    /// </summary>
    /// <typeparam name="T">The type of language setting to retrieve (Source or Target).</typeparam>
    /// <param name="settingName">The name of the setting to retrieve.</param>
    /// <returns>A HashSet of language codes from the configuration.</returns>
    private async Task<HashSet<string>> GetLanguagesSetting<T>(string settingName) where T : class, ILanguage
    {
        var languages = await _settingService.GetSettingAsJson<T>(settingName);
        return languages
            .Select(lang => lang.Code)
            .ToHashSet();
    }

    /// <summary>
    /// Updates the media hash in the database.
    /// </summary>
    /// <returns>A task representing the asynchronous operation.</returns>
    private async Task UpdateHash()
    {
        _media.MediaHash = _hash;
        _dbContext.Update(_media);
        await _dbContext.SaveChangesAsync();
    }
}