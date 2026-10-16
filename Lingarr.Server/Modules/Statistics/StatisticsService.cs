using Lingarr.Core.Configuration;
using Lingarr.Core.Data;
using Lingarr.Core.Entities;
using Lingarr.Core.Enum;
using Lingarr.Contracts.Models.Batch;
using Lingarr.Server.Models;
using Lingarr.Server.Models.Api;
using Lingarr.Server.Models.FileSystem;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Lingarr.Server.Modules.Settings;
using Lingarr.Server.Modules.Translation;

namespace Lingarr.Server.Modules.Statistics;

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

    public async Task<Lingarr.Core.Entities.Statistics> GetStatistics()
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
    /// Daily write-back and cost series for the overview trend card (P1-1).
    /// Aggregated on the fly from completed requests — smaller footprint than
    /// extending <see cref="DailyStatistics"/> with a cost column that would
    /// need back-filling. Days are local (TimeZoneConfiguration) and
    /// zero-filled. Each day is one SQL GROUP BY over its completed_at range
    /// (indexed, M0006) so neither request rows nor their subtitle payloads
    /// enter memory (scale audit P1-10); day boundaries come from the
    /// configured time zone exactly like CostService's month window.
    /// </summary>
    public async Task<DailyTrendStatistics> GetDailyTrendStatistics(int days = 14)
    {
        days = Math.Clamp(days, 1, 90);
        var today = Today;
        var startDate = today.AddDays(-days + 1);
        var timeZone = TimeZoneConfiguration.Current;

        var rates = await _costService.GetRates();
        var points = new Dictionary<DateOnly, DailyTrendPoint>(days);
        var charactersByDay = new Dictionary<DateOnly, Dictionary<string, long>>(days);
        for (var offset = 0; offset < days; offset++)
        {
            var date = startDate.AddDays(offset);
            points[date] = new DailyTrendPoint { Date = date };
            charactersByDay[date] = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        }

        // Any provider with usage but no rate prices the whole window as unknown,
        // mirroring CostSummary's "unknown, never $0" rule.
        var unpricedUsage = false;
        for (var offset = 0; offset < days; offset++)
        {
            var date = startDate.AddDays(offset);
            var dayStartLocal = date.ToDateTime(TimeOnly.MinValue);
            var dayStart = new DateTimeOffset(dayStartLocal, timeZone.GetUtcOffset(dayStartLocal));
            var dayEndLocal = date.AddDays(1).ToDateTime(TimeOnly.MinValue);
            var dayEnd = new DateTimeOffset(dayEndLocal, timeZone.GetUtcOffset(dayEndLocal));

            var usage = await _dbContext.TranslationRequests
                .AsNoTracking()
                .Where(r => r.CompletedAt >= dayStart && r.CompletedAt < dayEnd)
                .GroupBy(r => r.ServiceType)
                .Select(g => new
                {
                    Provider = g.Key,
                    Written = g.Count(x => x.TranslatedSubtitle != null),
                    CharacterRows = g.Count(x => x.CharactersTranslated != null),
                    Characters = g.Sum(x => x.CharactersTranslated ?? 0)
                })
                .ToListAsync();
            if (usage.Count == 0)
            {
                continue;
            }

            var point = points[date];
            var dayCharacters = charactersByDay[date];
            foreach (var entry in usage)
            {
                point.WrittenCount += entry.Written;
                if (entry.Provider is not null && entry.CharacterRows > 0)
                {
                    dayCharacters[entry.Provider] =
                        dayCharacters.GetValueOrDefault(entry.Provider) + entry.Characters;
                    if (!rates.ContainsKey(entry.Provider))
                    {
                        unpricedUsage = true;
                    }
                }
            }
        }

        foreach (var (date, point) in points)
        {
            point.Cost = unpricedUsage
                ? null
                : charactersByDay[date].Sum(entry =>
                    rates.TryGetValue(entry.Key, out var rate)
                        ? CostService.Price(entry.Value, rate)
                        : 0);
        }

        return new DailyTrendStatistics
        {
            Days = [.. points.Values.OrderBy(point => point.Date)],
            TotalWritten = points.Values.Sum(point => point.WrittenCount),
            TotalCost = unpricedUsage ? null : points.Values.Sum(point => point.Cost ?? 0),
            RatesConfigured = !unpricedUsage
        };
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

        // Exempt media (no_subtitle_needed = 1: audio already covers a
        // target language, or a manual mark) are not "missing" anything —
        // subtract them from the per-language gap so the KPI matches what
        // the library actually owes. The flags are scan-maintained just like
        // the totals above, so the two counts stay consistent.
        var exemptEpisodes = await _dbContext.Episodes
            .CountAsync(e => e.NoSubtitleNeeded);
        var exemptMovies = await _dbContext.Movies
            .CountAsync(m => m.NoSubtitleNeeded);
        var owedMediaCount = Math.Max(0, mediaCount - exemptEpisodes - exemptMovies);

        var missingSubtitles = targetLanguages
            .Sum(language => Math.Max(0, owedMediaCount - byLanguage.GetValueOrDefault(language.Code)));

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
            MonthlyCost = await _costService.GetCurrentMonthTotalCostAsync(),
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

    private async Task<Lingarr.Core.Entities.Statistics> GetOrCreateStatistics()
    {
        var stats = await _dbContext.Statistics.SingleOrDefaultAsync();
        if (stats == null)
        {
            stats = new Lingarr.Core.Entities.Statistics();
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