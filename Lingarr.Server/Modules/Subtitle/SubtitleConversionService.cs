using System.Text.RegularExpressions;
using Lingarr.Core.Configuration;
using Lingarr.Core.Data;
using Lingarr.Core.Entities;
using Lingarr.Core.Enum;
using Lingarr.Server.Models.FileSystem;
using Lingarr.Server.Models.TranslationRequests;
using Microsoft.EntityFrameworkCore;
using Lingarr.Server.Modules.Settings;

namespace Lingarr.Server.Modules.Subtitle;

/// <inheritdoc />
public partial class SubtitleConversionService : ISubtitleConversionService
{
    private readonly LingarrDbContext _dbContext;
    private readonly ISubtitleService _subtitleService;
    private readonly ISettingService _settingService;
    private readonly ILogger<SubtitleConversionService> _logger;

    public SubtitleConversionService(
        LingarrDbContext dbContext,
        ISubtitleService subtitleService,
        ISettingService settingService,
        ILogger<SubtitleConversionService> logger)
    {
        _dbContext = dbContext;
        _subtitleService = subtitleService;
        _settingService = settingService;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<SubtitleConversionResult> ConvertAsync(int translationRequestId, bool bilingual)
    {
        var result = new SubtitleConversionResult { Id = translationRequestId, IsBilingual = bilingual };
        var request = await _dbContext.TranslationRequests.FindAsync(translationRequestId);
        if (request == null)
        {
            result.Message = "Translation request not found";
            return result;
        }

        result.Title = request.Title;
        result.IsBilingual = request.IsBilingual;
        if (request.Status != TranslationStatus.Completed)
        {
            result.Message = "Only completed translations can be converted";
            return result;
        }

        if (string.IsNullOrEmpty(request.TranslatedSubtitle) || !File.Exists(request.TranslatedSubtitle))
        {
            result.Message = "Translated subtitle file is missing";
            return result;
        }

        if (request.IsBilingual == bilingual)
        {
            result.Success = true;
            result.Skipped = true;
            result.Message = bilingual ? "Already bilingual" : "Already monolingual";
            return result;
        }

        var rows = await _dbContext.TranslationRequestLines
            .Where(l => l.TranslationRequestId == translationRequestId)
            .OrderBy(l => l.Position)
            .ToListAsync();
        if (rows.Count == 0)
        {
            result.Message = "No persisted lines; this request cannot be converted";
            return result;
        }

        List<SubtitleItem> outputCues;
        try
        {
            outputCues = await _subtitleService.ReadSubtitles(request.TranslatedSubtitle);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Failed to parse output subtitle for request {Id}: {Message}",
                translationRequestId, ex.Message);
            result.Message = "Translated subtitle file could not be parsed";
            return result;
        }

        // The output may contain one extra cue at the start (translator info intro).
        var offset = outputCues.Count - rows.Count;
        if (offset < 0 || offset > 1)
        {
            result.Message = $"Output structure mismatch (cues: {outputCues.Count}, lines: {rows.Count})";
            return result;
        }

        var items = new List<SubtitleItem>();
        if (offset == 1)
        {
            // Preserve the intro cue verbatim; no original line is appended for it.
            var intro = outputCues[0];
            items.Add(new SubtitleItem
            {
                Position = intro.Position,
                StartTime = intro.StartTime,
                EndTime = intro.EndTime,
                Lines = [],
                PlaintextLines = [],
                TranslatedLines = intro.Lines,
                SsaDialogue = intro.SsaDialogue,
                SsaFormat = intro.SsaFormat
            });
        }

        for (var i = 0; i < rows.Count; i++)
        {
            var cue = outputCues[i + offset];
            var row = rows[i];

            var translatedLines = cue.Lines
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .ToList();
            if (request.IsBilingual && translatedLines.Count > 0
                && Normalize(translatedLines[^1]) == Normalize(row.Source))
            {
                // Drop the appended original line from the existing bilingual output.
                translatedLines.RemoveAt(translatedLines.Count - 1);
            }

            if (translatedLines.Count == 0)
            {
                translatedLines = [row.Target];
            }

            items.Add(new SubtitleItem
            {
                Position = row.Position,
                StartTime = cue.StartTime,
                EndTime = cue.EndTime,
                Lines = [row.Source],
                PlaintextLines = [row.Source],
                TranslatedLines = translatedLines,
                SsaDialogue = cue.SsaDialogue,
                SsaFormat = cue.SsaFormat
            });
        }

        var stripSubtitleFormatting =
            await _settingService.GetSetting(SettingKeys.Translation.StripSubtitleFormatting) == "true";

        await _subtitleService.WriteSubtitles(
            request.TranslatedSubtitle, items, stripSubtitleFormatting, bilingual);

        request.IsBilingual = bilingual;
        await _dbContext.SaveChangesAsync();

        result.Success = true;
        result.IsBilingual = bilingual;
        result.Message = bilingual ? "Converted to bilingual" : "Converted to monolingual";
        _logger.LogInformation(
            "Subtitle for request {Id} converted to {Form}: |Green|{filePath}|/Green|",
            translationRequestId, bilingual ? "bilingual" : "monolingual", request.TranslatedSubtitle);
        return result;
    }

    /// <inheritdoc />
    public async Task<List<SubtitleConversionResult>> ConvertAllAsync(bool bilingual)
    {
        var requests = await _dbContext.TranslationRequests
            .Where(r => r.Status == TranslationStatus.Completed && r.TranslatedSubtitle != null)
            .OrderBy(r => r.Id)
            .ToListAsync();

        var results = new List<SubtitleConversionResult>();
        foreach (var request in requests)
        {
            try
            {
                results.Add(await ConvertAsync(request.Id, bilingual));
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Batch conversion failed for request {Id}: {Message}",
                    request.Id, ex.Message);
                results.Add(new SubtitleConversionResult
                {
                    Id = request.Id,
                    Title = request.Title,
                    Success = false,
                    IsBilingual = request.IsBilingual,
                    Message = ex.Message
                });
            }
        }

        return results;
    }

    /// <summary>
    /// Normalizes text for comparison: strips ASS override tags, collapses
    /// whitespace and lowercases so a persisted source line can be matched
    /// against the original line appended in a bilingual output file.
    /// </summary>
    private static string Normalize(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var stripped = OverrideTagRegex().Replace(text, string.Empty);
        return WhitespaceRegex().Replace(stripped, " ").Trim().ToLowerInvariant();
    }

    [GeneratedRegex(@"\{[^}]*\}")]
    private static partial Regex OverrideTagRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}
