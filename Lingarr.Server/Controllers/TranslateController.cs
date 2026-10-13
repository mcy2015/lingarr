using System.Diagnostics;
using System.Net;
using Lingarr.Contracts.Exceptions;
using Lingarr.Contracts.Models;
using Lingarr.Contracts.Translation;
using Lingarr.Core.Configuration;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Lingarr.Server.Models;
using Lingarr.Contracts.Models.Batch;
using Lingarr.Server.Models.FileSystem;
using Lingarr.Server.Interfaces.Services;
using Lingarr.Server.Interfaces.Services.Translation;
using Lingarr.Server.Models.Api;
using Lingarr.Server.Services;
using Lingarr.Server.Services.Translation;
using Lingarr.Server.Services.Translation.Base;

namespace Lingarr.Server.Controllers;

[ApiController]
[Authorize("LingarrAccess")]
[Route("api/[controller]")]
public class TranslateController : ControllerBase
{
    private readonly ITranslationServiceFactory _translationServiceFactory;
    private readonly ITranslationRequestService _translationRequestService;
    private readonly ISettingService _settings;
    private readonly LanguageCodeService _languageCodeService;
    private readonly ITranslationHealthService _translationHealthService;
    private readonly IGlossaryService _glossaryService;
    private readonly ICostService _costService;
    private readonly ILogger<TranslateController> _logger;

    public TranslateController(
        ITranslationServiceFactory translationServiceFactory,
        ITranslationRequestService translationRequestService,
        ISettingService settings,
        LanguageCodeService languageCodeService,
        ITranslationHealthService translationHealthService,
        IGlossaryService glossaryService,
        ICostService costService,
        ILogger<TranslateController> logger)
    {
        _translationServiceFactory = translationServiceFactory;
        _translationRequestService = translationRequestService;
        _settings = settings;
        _languageCodeService = languageCodeService;
        _translationHealthService = translationHealthService;
        _glossaryService = glossaryService;
        _costService = costService;
        _logger = logger;
    }

    /// <summary>
    /// Health row per registered provider (gap-audit A1): configuration status
    /// plus the rolling latency/success window recorded by the shared retry
    /// loop. Cost stays null until usage/price tracking lands (A5).
    /// </summary>
    [HttpGet("health")]
    public async Task<ActionResult<List<Models.Api.TranslationServiceHealth>>> GetServiceHealth()
    {
        return Ok(await _translationHealthService.GetServiceHealthAsync());
    }

    /// <summary>
    /// Probe constants for the active connection test (PM analysis P0):
    /// a minimal request on the configured default language pair that
    /// every provider can legally serve. The en→zh pair is only the
    /// last-resort fallback for an unconfigured instance.
    /// </summary>
    private const string ProbeText = "Hello";
    private const string FallbackSourceLanguage = "en";
    private const string FallbackTargetLanguage = "zh";

    /// <summary>
    /// Actively tests one provider by sending a real minimal translation
    /// request and measuring the round trip (prototype screen-06
    /// 「测试连接」). Provider failures come back as ok=false with a
    /// message instead of a 500 so the card can render the inline result.
    /// </summary>
    /// <param name="serviceType">The provider to probe, e.g. "openai".</param>
    /// <param name="cancellationToken">Token to cancel the probe.</param>
    [HttpPost("test/{serviceType}")]
    public async Task<ActionResult<TranslationTestResult>> TestServiceConnection(
        string serviceType,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        ITranslationService service;
        try
        {
            service = _translationServiceFactory.CreateTranslationService(serviceType);
        }
        catch (ArgumentException ex)
        {
            return Ok(new TranslationTestResult
            {
                Ok = false,
                LatencyMs = 0,
                Message = ex.Message
            });
        }

        // A probe is a verdict, not a job: one provider attempt, no task-level
        // retry/backoff, so a rate-limited provider answers in one round trip
        // instead of hanging the button through the full ladder.
        if (service is BaseTranslationService builtInService)
        {
            builtInService.UseSingleAttempt();
        }

        var stopwatch = Stopwatch.StartNew();
        try
        {
            var (probeSource, probeTarget) = await GetConfiguredLanguagePair();
            var pair = await service.GetLanguagePair(probeSource, probeTarget, cancellationToken);
            if (pair is null)
            {
                return Ok(new TranslationTestResult
                {
                    Ok = false,
                    LatencyMs = stopwatch.Elapsed.TotalMilliseconds,
                    Message = $"Service does not support {probeSource} -> {probeTarget}."
                });
            }

            var translated = await service.TranslateAsync(
                ProbeText, pair.Source, pair.Target, null, null, cancellationToken);
            stopwatch.Stop();

            return Ok(new TranslationTestResult
            {
                Ok = true,
                Status = (int)HttpStatusCode.OK,
                LatencyMs = stopwatch.Elapsed.TotalMilliseconds,
                Message = $"\"{ProbeText}\" -> \"{translated}\""
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Ok(new TranslationTestResult
            {
                Ok = false,
                Status = ExtractHttpStatus(ex),
                LatencyMs = stopwatch.Elapsed.TotalMilliseconds,
                Message = DescribeFailure(ex)
            });
        }
    }

    /// <summary>
    /// Preview endpoint for the request-template screen (prototype
    /// screen-10 「试译预览」): translates a single line through the named
    /// service — which renders its saved request template exactly like a
    /// real job — and reports latency, per-call cost and glossary hits.
    /// Failures keep the same shape with ok=false.
    /// </summary>
    /// <param name="request">The service, line and optional language pair.</param>
    /// <param name="cancellationToken">Token to cancel the translation.</param>
    [HttpPost("preview")]
    public async Task<ActionResult<TranslationPreviewResponse>> PreviewTranslation(
        [FromBody] TranslationPreviewRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(request.Text))
        {
            return Ok(new TranslationPreviewResponse
            {
                Ok = true,
                Translated = request.Text,
                LatencyMs = 0
            });
        }

        ITranslationService service;
        try
        {
            service = _translationServiceFactory.CreateTranslationService(request.ServiceType);
        }
        catch (ArgumentException ex)
        {
            return Ok(PreviewFailure(ex.Message, 0));
        }

        var (defaultSource, defaultTarget) = await GetConfiguredLanguagePair();
        var sourceLanguage = request.SourceLanguage ?? defaultSource;
        var targetLanguage = request.TargetLanguage ?? defaultTarget;

        var stopwatch = Stopwatch.StartNew();
        try
        {
            var pair = await service.GetLanguagePair(sourceLanguage, targetLanguage, cancellationToken);
            if (pair is null)
            {
                return Ok(PreviewFailure(
                    $"Service does not support {sourceLanguage} -> {targetLanguage}.",
                    stopwatch.Elapsed.TotalMilliseconds));
            }

            var translated = await service.TranslateAsync(
                request.Text, pair.Source, pair.Target, null, null, cancellationToken);
            stopwatch.Stop();

            var glossaryHits = (await _glossaryService.FindHits(request.Text))
                .Select(hit => new TranslationPreviewGlossaryHit
                {
                    SourceTerm = hit.SourceTerm,
                    TargetTerm = hit.TargetTerm,
                    IsLocked = hit.IsLocked
                })
                .ToList();

            var rates = await _costService.GetRates();
            var cost = rates.TryGetValue(request.ServiceType, out var rate)
                ? CostService.Price(request.Text.Length, rate)
                : null;

            return Ok(new TranslationPreviewResponse
            {
                Ok = true,
                Translated = translated,
                LatencyMs = stopwatch.Elapsed.TotalMilliseconds,
                Cost = cost,
                GlossaryHits = glossaryHits
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Ok(PreviewFailure(
                DescribeFailure(ex),
                stopwatch.Elapsed.TotalMilliseconds));
        }
    }

    /// <summary>
    /// Picks the most actionable message from a provider failure: wrapped
    /// <see cref="TranslationException"/>s carry a concise summary, raw
    /// exceptions often hide the cause in the inner exception.
    /// </summary>
    private static string DescribeFailure(Exception ex) =>
        ex is TranslationException ? ex.Message : ex.InnerException?.Message ?? ex.Message;

    /// <summary>
    /// Extracts the provider HTTP status from a failure. Services that run
    /// through the shared retry pipeline wrap their HttpRequestExceptions
    /// inside a <see cref="TranslationException"/>, so the status code has
    /// to be looked up along the inner-exception chain — checking only the
    /// outermost exception loses 429/503 etc.
    /// </summary>
    private static int? ExtractHttpStatus(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is HttpRequestException { StatusCode: not null } httpException)
            {
                return (int)httpException.StatusCode!;
            }
        }

        return null;
    }

    /// <summary>
    /// Resolves the default language pair from the configured source/target
    /// language lists (first valid entry each); en→zh is only the fallback
    /// for an instance with no languages configured yet.
    /// </summary>
    private async Task<(string Source, string Target)> GetConfiguredLanguagePair()
    {
        var source = FirstValidCode(
            (await _settings.GetSettingAsJson<Language>(SettingKeys.Translation.SourceLanguages))
            ?.Select(language => language.code));
        var target = FirstValidCode(
            (await _settings.GetSettingAsJson<TargetLanguage>(SettingKeys.Translation.TargetLanguages))
            ?.Select(language => language.Code));

        return (
            source ?? FallbackSourceLanguage,
            target ?? FallbackTargetLanguage);
    }

    private string? FirstValidCode(IEnumerable<string>? codes) =>
        codes?.FirstOrDefault(code =>
            !string.IsNullOrWhiteSpace(code) && _languageCodeService.Validate(code));

    private static TranslationPreviewResponse PreviewFailure(string message, double latencyMs) => new()
    {
        Ok = false,
        LatencyMs = latencyMs,
        Message = message
    };

    /// <summary>
    /// Initiates a translation job for the provided subtitle data.
    /// </summary>
    /// <param name="translateAbleSubtitle">The subtitle data to be translated. 
    /// This includes the subtitle path, subtitle source language and subtitle target language.</param>
    /// <returns>Returns an HTTP 200 OK response if the job was successfully enqueued.</returns>
    [HttpPost("file")]
    public async Task<ActionResult<TranslationJobDto>> Translate([FromBody] TranslateAbleSubtitle translateAbleSubtitle)
    {
        var jobId = await _translationRequestService.CreateRequest(translateAbleSubtitle);
        return Ok(new TranslationJobDto
        {
            JobId = jobId,
        });
    }

    /// <summary>
    /// Initiates translation jobs for multiple media items.
    /// Handles subtitle discovery and source language resolution server-side.
    /// </summary>
    /// <param name="request">The bulk translate request containing media IDs, target language, and media type.</param>
    /// <returns>Returns a list of created translation request IDs.</returns>
    [HttpPost("bulk")]
    public async Task<ActionResult> BulkTranslate([FromBody] BulkTranslateRequest request)
    {
        await _translationRequestService.CreateBulkRequest(request);
        return Ok();
    }

    /// <summary>
    /// Translate a single subtitle line
    /// </summary>
    /// <param name="translateAbleSubtitleLine">The subtitle to be translated. 
    /// This includes the subtitle line, subtitle source language and subtitle target language.</param>
    /// <param name="cancellationToken">Token to cancel the translation operation</param>
    /// <returns>Returns translated string if the translation was successful.</returns>
    [HttpPost("line")]
    public async Task<string> TranslateLine(
        [FromBody] TranslateAbleSubtitleLine translateAbleSubtitleLine,
        CancellationToken cancellationToken)
    {
        var serviceType = TranslationServices.Parse(await _settings.GetSetting(SettingKeys.Translation.ServiceType))[0];
        var subtitleTranslator = new SubtitleTranslationService(
            _translationServiceFactory.CreateTranslationServices([serviceType]),
            _logger);

        if (translateAbleSubtitleLine.SubtitleLine == "")
        {
            return translateAbleSubtitleLine.SubtitleLine;
        }
        var result = await subtitleTranslator.TranslateSubtitleLine(translateAbleSubtitleLine, cancellationToken);
        return result.Translation;
    }

    /// <summary>
    /// Translates subtitle content, supporting both single line and batch translation.
    /// </summary>
    /// <param name="translateAbleSubtitleContent">The translation request containing one or more subtitle items</param>
    /// <param name="cancellationToken">Token to cancel the translation operation</param>
    /// <returns>Translated subtitle content</returns>
    [HttpPost("content")]
    public async Task<ActionResult<BatchSubtitleLine[]>> TranslateContent(
        [FromBody] TranslateAbleSubtitleContent translateAbleSubtitleContent,
        CancellationToken cancellationToken)
    {
        var results = await _translationRequestService.TranslateContentAsync(translateAbleSubtitleContent, cancellationToken);
        return Ok(results);
    }

    /// <summary>
    /// Reports whether proofreading is available and which configured service would perform it.
    /// </summary>
    /// <returns>The capability flag and the provider name, or null when no configured service qualifies.</returns>
    [HttpGet("proofread/status")]
    public async Task<ActionResult<ProofreadStatusResponse>> GetProofreadStatus()
    {
        return Ok(await _translationRequestService.GetProofreadStatus());
    }

    /// <summary>
    /// Proofreads a single translated line against its source line without persisting anything.
    /// </summary>
    /// <param name="proofreadLineRequest">The source line, its translation and the language pair</param>
    /// <param name="cancellationToken">Token to cancel the proofread operation</param>
    /// <returns>Returns the corrected translation if a configured service can proofread.</returns>
    [HttpPost("proofread")]
    public async Task<ActionResult<string>> ProofreadLine(
        [FromBody] ProofreadLineRequest proofreadLineRequest,
        CancellationToken cancellationToken)
    {
        var result = await _translationRequestService.ProofreadLine(proofreadLineRequest, cancellationToken);
        if (result == null)
        {
            return BadRequest("No configured translation service supports proofreading.");
        }

        return Ok(result);
    }

    /// <summary>
    /// Returns the canonical culture list used by the language picker.
    /// Per-service capability gating happens at translate time.
    /// </summary>
    /// <returns>A list of cultures with code and English name. Targets is always empty.</returns>
    [HttpGet("languages")]
    public ActionResult<IReadOnlyList<SourceLanguage>> GetLanguages()
    {
        var languageCodes = _languageCodeService.GetSupportedLanguages();
        return Ok(languageCodes);
    }

}