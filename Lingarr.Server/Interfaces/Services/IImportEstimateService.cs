using Lingarr.Server.Models;
using Lingarr.Server.Models.Api;

namespace Lingarr.Server.Interfaces.Services;

public interface IImportEstimateService
{
    /// <summary>
    /// Prices the characters of the files queued for translation at the
    /// default provider's rate — the same rate/pricing convention as the
    /// library estimate (ICostService). Unpriced providers yield a null
    /// cost ("unknown"), never $0.
    /// </summary>
    Task<ImportEstimateResult> Estimate(List<ImportEstimateFile> files, int translateCount, string? targetLanguage);
}
