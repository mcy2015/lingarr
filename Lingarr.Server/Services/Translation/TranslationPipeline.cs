using Lingarr.Contracts.Exceptions;
using Lingarr.Contracts.Translation;
using Lingarr.Core.Configuration;
using Lingarr.Server.Interfaces.Services;
using Lingarr.Server.Interfaces.Services.Translation;
using Lingarr.Server.Models.FileSystem;
using Lingarr.Server.Services;
using Microsoft.Extensions.Logging;

namespace Lingarr.Server.Services.Translation;

/// <summary>
/// Single implementation of the translation dispatch core shared by the
/// file-based job and the content endpoint. Replaces two divergent copies of
/// "load settings → resolve service chain → batch or individual dispatch".
/// </summary>
public class TranslationPipeline : ITranslationPipeline
{
    private readonly ISettingService _settings;
    private readonly ITranslationServiceFactory _translationServiceFactory;
    private readonly IProgressService _progressService;
    private readonly IAsyncTranslationRegistry _asyncTranslationRegistry;
    private readonly ITranslationRequestEventService _eventService;
    private readonly ILogger<TranslationPipeline> _logger;

    public TranslationPipeline(
        ISettingService settings,
        ITranslationServiceFactory translationServiceFactory,
        IProgressService progressService,
        IAsyncTranslationRegistry asyncTranslationRegistry,
        ITranslationRequestEventService eventService,
        ILogger<TranslationPipeline> logger)
    {
        _settings = settings;
        _translationServiceFactory = translationServiceFactory;
        _progressService = progressService;
        _asyncTranslationRegistry = asyncTranslationRegistry;
        _eventService = eventService;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<TranslationDispatch> PrepareAsync()
    {
        var settings = await _settings.GetSettings([
            SettingKeys.Translation.ServiceType,
            SettingKeys.Translation.UseBatchTranslation,
            SettingKeys.Translation.MaxBatchSize,
            SettingKeys.Translation.StripSubtitleFormatting,
            SettingKeys.Translation.PreserveLineBreaks,
            SettingKeys.Translation.AiContextBefore,
            SettingKeys.Translation.AiContextAfter,
            SettingKeys.Translation.AiContextUseTranslated
        ]);

        var serviceNames = TranslationServices.Parse(settings[SettingKeys.Translation.ServiceType]);
        var services = _translationServiceFactory.CreateTranslationServices(serviceNames);
        if (services.Count == 0)
        {
            throw new TranslationException(
                $"No usable translation services configured: [{string.Join(", ", serviceNames)}]");
        }

        return new TranslationDispatch(
            services,
            serviceNames[0],
            settings[SettingKeys.Translation.StripSubtitleFormatting] == "true",
            settings[SettingKeys.Translation.PreserveLineBreaks] == "true")
        {
            // dispatch-plan extras carried alongside the record (not part of equality)
            UseBatch = settings[SettingKeys.Translation.UseBatchTranslation] == "true",
            MaxBatchSize = int.TryParse(settings[SettingKeys.Translation.MaxBatchSize], out var batchSize)
                ? batchSize
                : 10000,
            ContextBefore = int.TryParse(settings[SettingKeys.Translation.AiContextBefore], out var before)
                ? before
                : 0,
            ContextAfter = int.TryParse(settings[SettingKeys.Translation.AiContextAfter], out var after)
                ? after
                : 0,
            UseTranslatedContext = settings[SettingKeys.Translation.AiContextUseTranslated] == "true"
        };
    }

    /// <inheritdoc />
    public async Task<TranslationRunResult> RunAsync(
        TranslationRun run,
        TranslationDispatch? dispatch = null,
        CancellationToken cancellationToken = default)
    {
        dispatch ??= await PrepareAsync();

        var useBatch = dispatch.UseBatch
                       && (!run.BatchRequiresMultipleLines || run.Subtitles.Count > 1)
                       && dispatch.Services.Any(entry => entry.Service is IBatchTranslationService);

        if (useBatch)
        {
            _logger.LogInformation(
                "Using batch translation (max batch size: {MaxBatchSize}) for {LineCount} lines of request {RequestId}",
                dispatch.MaxBatchSize, run.Subtitles.Count, run.Request.Id);

            var translator = new SubtitleTranslationService(
                dispatch.Services, _logger, _progressService, pauseGate: _asyncTranslationRegistry,
                eventService: _eventService);
            var translated = await translator.TranslateSubtitlesBatch(
                run.Subtitles,
                run.Request,
                dispatch.StripSubtitleFormatting,
                dispatch.PreserveLineBreaks,
                dispatch.MaxBatchSize,
                cancellationToken);

            return new TranslationRunResult(
                translated, dispatch.ServiceType, dispatch.Services[0].Service,
                UsedBatchTranslation: true, dispatch.StripSubtitleFormatting);
        }

        if (dispatch.ContextBefore > 0 || dispatch.ContextAfter > 0)
        {
            _logger.LogInformation(
                "Using individual translation with context (before: {ContextBefore}, after: {ContextAfter}, translated context: {UseTranslatedContext}) for request {RequestId}",
                dispatch.ContextBefore, dispatch.ContextAfter, dispatch.UseTranslatedContext, run.Request.Id);
        }

        // Content lines carry no timing information, so nothing can be stacked: the
        // caller opts out of the merge that collapses same-timed .ass layers.
        var individualTranslator = new SubtitleTranslationService(
            dispatch.Services, _logger, _progressService, dispatch.UseTranslatedContext, run.MergeStackedLines,
            eventService: _eventService);
        var translatedItems = await individualTranslator.TranslateSubtitles(
            run.Subtitles,
            run.Request,
            dispatch.StripSubtitleFormatting,
            dispatch.PreserveLineBreaks,
            dispatch.ContextBefore,
            dispatch.ContextAfter,
            cancellationToken);

        // TranslateSubtitles returns partial results when cancellation lands between
        // two lines. Surface it so callers mark the request Cancelled and no partial
        // result is treated as success (previously only the content path did this).
        cancellationToken.ThrowIfCancellationRequested();

        return new TranslationRunResult(
            translatedItems, dispatch.ServiceType, dispatch.Services[0].Service,
            UsedBatchTranslation: false, dispatch.StripSubtitleFormatting);
    }
}
