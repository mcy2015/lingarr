using Lingarr.Contracts.Exceptions;
using Lingarr.Contracts.Models;
using Lingarr.Contracts.Translation;
using Lingarr.Core.Configuration;
using Lingarr.Server.Interfaces.Services;
using System.Net;

namespace Lingarr.Server.Services.Translation.Base;

public abstract class BaseTranslationService : ITranslationService
{
    protected readonly ISettingService _settings;
    protected readonly ILogger _logger;
    protected readonly LanguageCodeService _languageCodeService;

    // shared resilience knobs (see ApplyResilienceSettings / ExecuteWithRetryAsync)
    protected int _maxRetries = 5;
    protected TimeSpan _retryDelay = TimeSpan.FromSeconds(1);
    protected int _retryDelayMultiplier = 2;

    protected BaseTranslationService(
        ISettingService settings,
        ILogger logger,
        LanguageCodeService languageCodeService)
    {
        _settings = settings;
        _logger = logger;
        _languageCodeService = languageCodeService;
    }

    /// <inheritdoc />
    public abstract string? ModelName { get; }

    /// <inheritdoc />
    public abstract Task<string> TranslateAsync(
        string text,
        string sourceLanguage,
        string targetLanguage,
        List<string>? contextLinesBefore,
        List<string>? contextLinesAfter,
        CancellationToken cancellationToken);

    /// <inheritdoc />
    public abstract Task<List<SourceLanguage>> GetLanguages();

    /// <inheritdoc />
    public abstract Task<ModelsResponse> GetModels();

    /// <inheritdoc />
    public virtual async Task<LanguagePair?> GetLanguagePair(
        string requestedSource,
        string requestedTarget,
        CancellationToken cancellationToken)
    {
        List<SourceLanguage> languages;
        try
        {
            languages = await GetLanguages();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogDebug(exception, "Could not retrieve supported languages, treating as unsupported pair.");
            return null;
        }

        LanguagePair? bestLanguagePair = null;
        foreach (var language in languages)
        {
            var sourceMatch = _languageCodeService.GetBestMatch(requestedSource, [language.Code]);
            if (sourceMatch is null)
            {
                continue;
            }

            var targetMatch = _languageCodeService.GetBestMatch(requestedTarget, language.Targets);
            if (targetMatch is null)
            {
                continue;
            }

            var tier = sourceMatch.Tier;
            if (targetMatch.Tier > tier)
            {
                tier = targetMatch.Tier;
            }

            if (bestLanguagePair != null && tier >= bestLanguagePair.Tier)
            {
                continue;
            }

            bestLanguagePair = new LanguagePair
            {
                Source = sourceMatch.Code, 
                Target = targetMatch.Code, 
                Tier = tier
            };
            if (tier == MatchTier.Exact)
            {
                break;
            }
        }

        return bestLanguagePair;
    }

    /// <summary>
    /// Parses the shared retry/timeout settings dictionary into the resilience fields.
    /// Providers call this from their InitializeAsync instead of re-implementing
    /// the parsing (previously copy-pasted ten times, with drift: DeepSeek had no retry at all).
    /// </summary>
    protected void ApplyResilienceSettings(Dictionary<string, string> settings)
    {
        _maxRetries = int.TryParse(settings.GetValueOrDefault(SettingKeys.Translation.MaxRetries), out var maxRetries)
            ? maxRetries
            : 5;
        _retryDelay = int.TryParse(settings.GetValueOrDefault(SettingKeys.Translation.RetryDelay), out var delaySeconds)
            ? TimeSpan.FromSeconds(delaySeconds)
            : TimeSpan.FromSeconds(1);
        _retryDelayMultiplier = int.TryParse(
            settings.GetValueOrDefault(SettingKeys.Translation.RetryDelayMultiplier), out var multiplier)
            ? multiplier
            : 2;
    }

    /// <summary>
    /// Executes a provider operation with the shared retry policy: exponential
    /// backoff on 429/503, cancellation passthrough, any other failure wrapped
    /// in a <see cref="TranslationException"/>.
    /// </summary>
    /// <param name="serviceName">Display name used in log messages (e.g. "OpenAI").</param>
    /// <param name="failureMessage">Message for the wrapped exception on non-retryable failures.</param>
    /// <param name="operation">The provider call to execute, receives the linked cancellation token.</param>
    /// <param name="cancellationToken">Caller cancellation token.</param>
    /// <param name="logText">Optional text snippet for retry-exhausted logging.</param>
    protected async Task<T> ExecuteWithRetryAsync<T>(
        string serviceName,
        string failureMessage,
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken,
        string? logText = null)
    {
        using var retryCts = new CancellationTokenSource();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, retryCts.Token);

        var delay = _retryDelay;
        for (var attempt = 1; attempt <= _maxRetries; attempt++)
        {
            try
            {
                return await operation(linked.Token);
            }
            catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable)
            {
                if (attempt == _maxRetries)
                {
                    if (logText is null)
                    {
                        _logger.LogError(ex, "Max retries exhausted ({StatusCode}) for batch translation", ex.StatusCode);
                    }
                    else
                    {
                        _logger.LogError(ex, "Max retries exhausted ({StatusCode}) for text: {Text}", ex.StatusCode, logText);
                    }

                    throw new TranslationException($"Retry limit reached after {ex.StatusCode}.", ex);
                }

                await Task.Delay(delay, linked.Token).ConfigureAwait(false);
                delay = TimeSpan.FromTicks(delay.Ticks * _retryDelayMultiplier);

                _logger.LogWarning(
                    "{ServiceName} received {StatusCode}. Retrying in {Delay}... (Attempt {Attempt}/{MaxRetries})",
                    serviceName, ex.StatusCode, delay, attempt, _maxRetries);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error occurred during {ServiceName} operation", serviceName);
                throw new TranslationException(failureMessage, ex);
            }
        }

        throw new TranslationException($"{failureMessage}: maximum retry attempts reached.");
    }
}