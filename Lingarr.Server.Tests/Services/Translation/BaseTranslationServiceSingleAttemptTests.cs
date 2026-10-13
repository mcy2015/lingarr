using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Lingarr.Contracts.Exceptions;
using Lingarr.Contracts.Models;
using Lingarr.Contracts.Translation;
using Lingarr.Server.Interfaces.Services;
using Lingarr.Server.Services;
using Lingarr.Server.Services.Translation.Base;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Lingarr.Server.Tests.Services.Translation;

/// <summary>
/// A minimal real <see cref="BaseTranslationService"/> whose provider call
/// always answers 429. Exposes how many times the operation ran so tests
/// can pin the retry-vs-single-attempt behaviour of the shared pipeline.
/// </summary>
public sealed class RateLimitedTranslationService : BaseTranslationService
{
    public int OperationCalls { get; private set; }

    public RateLimitedTranslationService(ISettingService? settings = null)
        : base(settings ?? new Mock<ISettingService>().Object,
            NullLogger<RateLimitedTranslationService>.Instance,
            new LanguageCodeService())
    {
        // Keep the backoff ladder fast when it is exercised in tests.
        _retryDelay = TimeSpan.FromMilliseconds(1);
        _retryDelayMultiplier = 2;
    }

    public override string? ModelName => null;

    public override Task<string> TranslateAsync(
        string text,
        string sourceLanguage,
        string targetLanguage,
        List<string>? contextLinesBefore,
        List<string>? contextLinesAfter,
        CancellationToken cancellationToken) =>
        ExecuteWithRetryAsync<string>(
            "RateLimited",
            "Failed to translate using RateLimited",
            _ =>
            {
                OperationCalls++;
                throw new HttpRequestException(
                    "rate limited", null, HttpStatusCode.TooManyRequests);
            },
            cancellationToken,
            text);

    public override Task<List<SourceLanguage>> GetLanguages() =>
        Task.FromResult(new List<SourceLanguage>());

    /// <summary>
    /// Stands in for a provider that can serve the pair, so probes reach
    /// the real TranslateAsync retry path instead of the unsupported-pair
    /// early return.
    /// </summary>
    public override Task<LanguagePair?> GetLanguagePair(
        string requestedSource,
        string requestedTarget,
        CancellationToken cancellationToken) =>
        Task.FromResult<LanguagePair?>(
            new LanguagePair { Source = requestedSource, Target = requestedTarget, Tier = MatchTier.Exact });

    public override Task<ModelsResponse> GetModels() =>
        Task.FromResult(new ModelsResponse());
}

/// <summary>
/// Single-attempt mode of the shared retry pipeline (probe path, P2-4):
/// a connection test must get its verdict in one provider round trip
/// instead of walking the exponential backoff ladder, while the health
/// tracker still sees the one attempt's outcome.
/// </summary>
public class BaseTranslationServiceSingleAttemptTests
{
    private Task<TranslationException> TranslateOnce(RateLimitedTranslationService service) =>
        Assert.ThrowsAsync<TranslationException>(() =>
            service.TranslateAsync("Hello", "en", "zh", null, null, CancellationToken.None));

    [Fact]
    public async Task DefaultMode_RetriesRateLimitedAttemptsUntilExhausted()
    {
        var service = new RateLimitedTranslationService();

        var exception = await TranslateOnce(service);

        Assert.Equal(5, service.OperationCalls);
        Assert.Contains("TooManyRequests", exception.Message);
    }

    [Fact]
    public async Task SingleAttempt_CallsOperationOnceAndThrowsWrapped429()
    {
        var service = new RateLimitedTranslationService();
        service.UseSingleAttempt();

        var exception = await TranslateOnce(service);

        Assert.Equal(1, service.OperationCalls);
        // The wrapped HttpRequestException must survive as the inner
        // exception so the probe can extract the 429 status code.
        var httpException = Assert.IsType<HttpRequestException>(exception.InnerException);
        Assert.Equal(HttpStatusCode.TooManyRequests, httpException.StatusCode);
    }

    [Fact]
    public async Task SingleAttempt_StillFeedsTheHealthWindow()
    {
        var service = new RateLimitedTranslationService();
        var healthMock = new Mock<ITranslationHealthService>();
        service.SetHealthTracking(healthMock.Object, "ratelimited");
        service.UseSingleAttempt();

        await TranslateOnce(service);

        healthMock.Verify(health => health.Record(
            "ratelimited", false, It.IsAny<TimeSpan>(), "TooManyRequests"), Times.Once);
    }
}
