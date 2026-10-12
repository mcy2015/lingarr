using Lingarr.Core.Configuration;
using Lingarr.Core.Data;
using Lingarr.Core.Entities;
using Lingarr.Core.Enum;
using Lingarr.Server.Interfaces.Services;
using Lingarr.Contracts.Models.Batch;
using Lingarr.Server.Models;
using Lingarr.Server.Models.Api;
using Lingarr.Server.Models.FileSystem;
using Hangfire;
using Microsoft.EntityFrameworkCore;

namespace Lingarr.Server.Services;

public class StatisticsService : IStatisticsService
{
    private readonly LingarrDbContext _dbContext;
    private readonly ISettingService _settingService;
    private readonly ITranslationHealthService _translationHealthService;
    private readonly ICostService _costService;

    private static DateOnly Today =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, TimeZoneConfiguration.Current).DateTime);

    public StatisticsService(
        LingarrDbContext dbContext,
        ISettingService settingService,
        ITranslationHealthService translationHealthService,
        ICostService costService)
    {
        _dbContext = dbContext;
        _settingService = settingService;
        _translationHealthService = translationHealthService;
        _costService = costService;
    }

    public async Task<Statistics> GetStatistics()
    {
        return await GetOrCreateStatistics();
    }

    public async Task<IEnumerable<DailyStatistics>> GetDailyStatistics(int days = 30)
    {
        var startDate = Today.AddDays(-days + 1); // +1 to include today
        var stats = await _dbContext.DailyStatistics
            .Where(d => d.Date >= startDate)
            .OrderBy(d => d.Date)
            .ToListAsync();

        return stats;
    }

    /// <summary>
    /// Overview pipeline payload (A2). Media/subtitle figures come from the
    /// scheduled statistics scan (StatisticsJob) — counting subtitle files
    /// requires the filesystem walk that job already owns. Queue depth is live
    /// from Hangfire; provider status from the health tracker.
    /// </summary>
    public async Task<PipelineStatistics> GetPipelineStatistics()
    {
        var stats = await GetOrCreateStatistics();
        var mediaCount = stats.TotalEpisodes + stats.TotalMovies;

        var targetLanguages = await _settingService.GetSettingAsJson<TargetLanguage>(
            SettingKeys.Translation.TargetLanguages);
        var byLanguage = stats.SubtitlesByLanguage;
        var missingSubtitles = targetLanguages
            .Sum(language => Math.Max(0, mediaCount - byLanguage.GetValueOrDefault(language.Code)));

        var monthStartLocal = Today.AddDays(1 - Today.Day).ToDateTime(TimeOnly.MinValue);
        var monthStart = new DateTimeOffset(
            monthStartLocal,
            TimeZoneConfiguration.Current.GetUtcOffset(monthStartLocal));
        var cutoff30Days = DateTimeOffset.UtcNow.AddDays(-30);

        var completed30 = await _dbContext.TranslationRequests
            .CountAsync(r => r.CreatedAt >= cutoff30Days && r.Status == TranslationStatus.Completed);
        var failed30 = await _dbContext.TranslationRequests
            .CountAsync(r => r.CreatedAt >= cutoff30Days && r.Status == TranslationStatus.Failed);

        return new PipelineStatistics
        {
            MediaCount = mediaCount,
            MissingSubtitles = missingSubtitles,
            QueueDepth = GetHangfireQueueDepth(),
            ServicesOnline = await _translationHealthService.GetOnlineServiceCountAsync(),
            WrittenThisMonth = await _dbContext.TranslationRequests
                .CountAsync(r => r.CompletedAt >= monthStart && r.TranslatedSubtitle != null),
            SuccessRate30Days = completed30 + failed30 > 0
                ? Math.Round(completed30 / (double)(completed30 + failed30) * 100, 1)
                : null,
            MonthlyCost = await _costService.GetCurrentMonthTotalCostAsync()
        };
    }

    /// <summary>Sum of enqueued jobs over every Hangfire queue; 0 when storage is unavailable.</summary>
    private static long GetHangfireQueueDepth()
    {
        try
        {
            var monitoring = JobStorage.Current?.GetMonitoringApi();
            return monitoring?.Queues().Sum(queue => queue.Length) ?? 0;
        }
        catch
        {
            return 0;
        }
    }

    private async Task<Statistics> GetOrCreateStatistics()
    {
        var stats = await _dbContext.Statistics.SingleOrDefaultAsync();
        if (stats == null)
        {
            stats = new Statistics();
            _dbContext.Statistics.Add(stats);
            await _dbContext.SaveChangesAsync();
        }

        return stats;
    }

    private async Task<DailyStatistics> GetOrCreateDailyStatistics(DateOnly today)
    {
        var dailyStats = await _dbContext.DailyStatistics
            .Where(d => d.Date >= today)
            .FirstOrDefaultAsync();

        if (dailyStats == null)
        {
            dailyStats = new DailyStatistics { Date = today };
            _dbContext.DailyStatistics.Add(dailyStats);
        }

        return dailyStats;
    }

    public async Task<int> UpdateTranslationStatisticsFromSubtitles(
        TranslationRequest request,
        string serviceType,
        string? modelName,
        List<SubtitleItem> translatedSubtitles)
    {
        var lineCount = translatedSubtitles.Sum(s => s.Lines.Count);
        var charCount = translatedSubtitles.Sum(s => s.Lines.Sum(l => l.Length));

        return await UpdateTranslationStatisticsInternal(
            request, serviceType, modelName, lineCount, charCount);
    }

    public async Task<int> UpdateTranslationStatisticsFromLines(
        TranslationRequest request,
        string serviceType,
        string? modelName,
        BatchSubtitleLine[] translatedLines)
    {
        var lineCount = translatedLines.Length;
        var charCount = translatedLines.Sum(s => s.Line.Length);

        return await UpdateTranslationStatisticsInternal(
            request, serviceType, modelName, lineCount, charCount);
    }

    private async Task<int> UpdateTranslationStatisticsInternal(
        TranslationRequest request,
        string serviceType,
        string? modelName,
        int totalLines,
        int totalCharacters)
    {
        var stats = await GetOrCreateStatistics();
        var today = Today;

        // Per-request character count feeds the cost rollups (A5); FindAsync
        // reuses the tracked instance when the caller shares this scope.
        var trackedRequest = await _dbContext.TranslationRequests.FindAsync(request.Id);
        if (trackedRequest != null)
        {
            trackedRequest.CharactersTranslated = totalCharacters;
        }

        // Update total counts
        stats.TotalLinesTranslated += totalLines;
        stats.TotalCharactersTranslated += totalCharacters;
        stats.TotalFilesTranslated++;

        // Update media type statistics
        var mediaType = request.MediaType.ToString();
        var mediaTypeStats = stats.TranslationsByMediaType;
        mediaTypeStats[mediaType] = mediaTypeStats.GetValueOrDefault(mediaType) + 1;
        stats.TranslationsByMediaType = mediaTypeStats;

        // Update service type statistics
        var serviceStats = stats.TranslationsByService;
        serviceStats[serviceType] = serviceStats.GetValueOrDefault(serviceType) + 1;
        stats.TranslationsByService = serviceStats;

        // Update language pair statistics 
        var languageStats = stats.SubtitlesByLanguage;
        var languagePair = $"{request.SourceLanguage}:{request.TargetLanguage}";
        languageStats[languagePair] = languageStats.GetValueOrDefault(languagePair) + 1;
        stats.SubtitlesByLanguage = languageStats;

        // Update model statistics (correlated with service)
        if (!string.IsNullOrEmpty(modelName))
        {
            var modelStats = stats.TranslationsByModel;
            var serviceModelKey = $"{serviceType}:{modelName}";
            modelStats[serviceModelKey] = modelStats.GetValueOrDefault(serviceModelKey) + 1;
            stats.TranslationsByModel = modelStats;
        }

        // Update daily statistics
        var dailyStats = await GetOrCreateDailyStatistics(today);
        dailyStats.TranslationCount++;

        return await _dbContext.SaveChangesAsync();
    }

    public async Task ResetStatistics()
    {
        // Reset the main statistics entity
        var stats = await GetOrCreateStatistics();
        stats.TotalLinesTranslated = 0;
        stats.TotalCharactersTranslated = 0;
        stats.TotalFilesTranslated = 0;
        stats.TotalMovies = 0;
        stats.TotalEpisodes = 0;
        stats.TotalSubtitles = 0;
        stats.TranslationsByMediaType = new Dictionary<string, int>();
        stats.TranslationsByService = new Dictionary<string, int>();
        stats.SubtitlesByLanguage = new Dictionary<string, int>();
        stats.TranslationsByModel = new Dictionary<string, int>();

        // Delete all daily statistics
        var dailyStats = await _dbContext.DailyStatistics.ToListAsync();
        _dbContext.DailyStatistics.RemoveRange(dailyStats);

        await _dbContext.SaveChangesAsync();
    }
}