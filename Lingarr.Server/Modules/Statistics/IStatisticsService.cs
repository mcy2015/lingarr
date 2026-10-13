using Lingarr.Core.Entities;
using Lingarr.Contracts.Models.Batch;
using Lingarr.Server.Models.Api;
using Lingarr.Server.Models.FileSystem;

namespace Lingarr.Server.Modules.Statistics;

public interface IStatisticsService
{
    // NOTE: fully qualified because the Modules.Statistics namespace shadows
    // the Lingarr.Core.Entities.Statistics entity for simple-name lookup.
    Task<Lingarr.Core.Entities.Statistics> GetStatistics();
    Task<IEnumerable<DailyStatistics>> GetDailyStatistics(int days = 30);
    Task<PipelineStatistics> GetPipelineStatistics();
    Task<DailyTrendStatistics> GetDailyTrendStatistics(int days = 14);
    Task<int> UpdateTranslationStatisticsFromSubtitles(
        TranslationRequest request,
        string serviceType,
        string? modelName,
        List<SubtitleItem> translatedSubtitles);
    Task<int> UpdateTranslationStatisticsFromLines(
        TranslationRequest request,
        string serviceType,
        string? modelName,
        BatchSubtitleLine[] translatedLines);
    Task ResetStatistics();
}