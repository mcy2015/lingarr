using Lingarr.Core.Data;
using Lingarr.Core.Entities;
using Lingarr.Core.Enum;
using Lingarr.Core.Configuration;
using Lingarr.Contracts.Translation;
using Lingarr.Server.Modules.Translation;
using Lingarr.Server.Models;
using Microsoft.EntityFrameworkCore;
using Lingarr.Server.Modules.Settings;
using Lingarr.Server.Modules.Subtitle;

namespace Lingarr.Server.Modules.Translation;

/// <summary>
/// Proofread operations on translation requests: capability reporting, single-line
/// suggestions, full-request proofread passes and applying suggestions.
/// Owns <see cref="ResolveProofreadService"/> and therefore also the proofread capability
/// report (<c>GetProofreadStatus</c>), which is derived from the same resolution and is
/// not a database query.
/// </summary>
public class ProofreadRequestService : IProofreadRequestService
{
    private const string ManualService = "manual";

    private readonly LingarrDbContext _dbContext;
    private readonly IJobQueue _jobQueue;
    private readonly ITranslationRequestQueueService _queueService;
    private readonly ITranslationRequestQueryService _queryService;
    private readonly ITranslationRequestEventService _eventService;
    private readonly IProgressService _progressService;
    private readonly ISettingService _settingService;
    private readonly ISubtitleService _subtitleService;
    private readonly ITranslationServiceFactory _translationServiceFactory;
    private readonly ILogger<ProofreadRequestService> _logger;

    public ProofreadRequestService(
        LingarrDbContext dbContext,
        IJobQueue jobQueue,
        ITranslationRequestQueueService queueService,
        ITranslationRequestQueryService queryService,
        ITranslationRequestEventService eventService,
        IProgressService progressService,
        ISettingService settingService,
        ISubtitleService subtitleService,
        ITranslationServiceFactory translationServiceFactory,
        ILogger<ProofreadRequestService> logger)
    {
        _dbContext = dbContext;
        _jobQueue = jobQueue;
        _queueService = queueService;
        _queryService = queryService;
        _eventService = eventService;
        _progressService = progressService;
        _settingService = settingService;
        _subtitleService = subtitleService;
        _translationServiceFactory = translationServiceFactory;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<ProofreadStatusResponse> GetProofreadStatus()
    {
        var entry = await ResolveProofreadService();
        return new ProofreadStatusResponse
        {
            Supported = entry != null,
            Service = entry?.Name
        };
    }

    /// <inheritdoc />
    public async Task<string?> ProofreadLine(
        ProofreadLineRequest proofreadLineRequest,
        CancellationToken cancellationToken)
    {
        var entry = await ResolveProofreadService();
        if (entry == null)
        {
            return null;
        }

        return await entry.Value.Service.ProofreadAsync(
            proofreadLineRequest.SourceLine,
            proofreadLineRequest.TranslatedLine,
            proofreadLineRequest.SourceLanguage,
            proofreadLineRequest.TargetLanguage,
            cancellationToken);
    }

    /// <inheritdoc />
    public async Task<string?> ProofreadTranslationRequest(TranslationRequest proofreadRequest)
    {
        var translationRequest = await _dbContext.TranslationRequests.FirstOrDefaultAsync(
            candidate => candidate.Id == proofreadRequest.Id);
        if (translationRequest == null)
        {
            return null;
        }

        if (translationRequest.Status != TranslationStatus.Completed)
        {
            _logger.LogInformation(
                "Proofread skipped for request {Id}: status {Status} is not Completed.",
                translationRequest.Id, translationRequest.Status);
            return null;
        }

        if (string.IsNullOrEmpty(translationRequest.TranslatedSubtitle))
        {
            _logger.LogInformation(
                "Proofread skipped for request {Id}: no translated subtitle is recorded.",
                translationRequest.Id);
            return null;
        }

        translationRequest.Status = TranslationStatus.Pending;
        translationRequest.ErrorMessage = null;
        translationRequest.StackTrace = null;
        translationRequest.JobType = TranslationJobType.Proofread;
        await _dbContext.SaveChangesAsync();

        var jobId = _jobQueue.EnqueueProofreadJob(translationRequest);
        await _queueService.UpdateTranslationRequest(translationRequest, TranslationStatus.Pending, jobId);
        await _queryService.UpdateActiveCount();

        return $"Translation request with id {proofreadRequest.Id} is being proofread, new job id {jobId}";
    }

    /// <inheritdoc />
    public async Task<string?> ApplyProofreadLine(ProofreadLineApplyRequest applyRequest)
    {
        var translationRequest = await _dbContext.TranslationRequests.FirstOrDefaultAsync(
            candidate => candidate.Id == applyRequest.Id);
        if (translationRequest == null || string.IsNullOrEmpty(translationRequest.TranslatedSubtitle))
        {
            return null;
        }

        var stripSubtitleFormatting =
            await _settingService.GetSetting(SettingKeys.Translation.StripSubtitleFormatting) == "true";

        var translatedSubtitles = await _subtitleService.ReadSubtitles(translationRequest.TranslatedSubtitle);
        var target = translatedSubtitles.FirstOrDefault(subtitle => subtitle.Position == applyRequest.Position);
        if (target == null)
        {
            return null;
        }

        var sourceSubtitles = await _subtitleService.ReadSubtitles(translationRequest.SubtitleToTranslate);
        var sourceSubtitle = sourceSubtitles.FirstOrDefault(subtitle => subtitle.Position == applyRequest.Position);
        var sourceText = sourceSubtitle == null
            ? string.Empty
            : string.Join(" ", stripSubtitleFormatting ? sourceSubtitle.PlaintextLines : sourceSubtitle.Lines);

        foreach (var subtitle in translatedSubtitles)
        {
            subtitle.TranslatedLines = subtitle.Lines;
        }
        target.TranslatedLines = [applyRequest.Target];

        var service = applyRequest.Origin == ProofreadLineOrigin.Manual
            ? ManualService
            : (await ResolveProofreadService())?.Name;

        await _progressService.EmitProofreadLine(
            translationRequest,
            applyRequest.Position,
            sourceText,
            applyRequest.Target,
            service);

        await _subtitleService.WriteSubtitles(
            translationRequest.TranslatedSubtitle,
            translatedSubtitles,
            stripSubtitleFormatting,
            translationRequest.IsBilingual);

        return $"Line {applyRequest.Position} of translation request with id {applyRequest.Id} has been updated";
    }

    private async Task<(string Name, IProofreadService Service)?> ResolveProofreadService()
    {
        var serviceNames = TranslationServices.Parse(
            await _settingService.GetSetting(SettingKeys.Translation.ServiceType),
            _logger);
        var services = _translationServiceFactory.CreateTranslationServices(serviceNames);

        foreach (var entry in services)
        {
            if (entry.Service is IProofreadService proofreadService)
            {
                return (entry.Name, proofreadService);
            }
        }

        return null;
    }
}
