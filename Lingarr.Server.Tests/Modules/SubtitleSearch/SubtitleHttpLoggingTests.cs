using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Lingarr.Server.Modules.SubtitleSearch.Abstractions;
using Lingarr.Server.Modules.SubtitleSearch.Providers;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Lingarr.Server.Tests.Modules.SubtitleSearch;

/// <summary>
/// L4a acceptance of the shared HTTP gate's logging — the "assrt 493
/// download throttle shipped unlogged" closure: every verdict the gate maps
/// must now be visible, with the provider name in the message. Warning for
/// the quota/rate-limit verdict family (429/406 with zero remaining quota,
/// the bare rate-limit statuses 429/406/493/509), timeout and network
/// failures carrying the full exception; Debug for other non-success
/// statuses and for throttle waits of a second or more; the success path
/// stays log-free (aggregation is L4b's business). The request identifier
/// must be scheme://host/path only — the query (which carries the sources'
/// keys and tokens) must never reach the log.
/// </summary>
[Collection("SubtitleHttpThrottle")]
public class SubtitleHttpLoggingTests : IDisposable
{
    // A source key no real provider uses: it has no throttle-table entry
    // (requests leave immediately in the status-code cases) and the log
    // assertions can pick "our" entries out of the capture logger even if
    // a foreign test of another collection ever ran through the gate.
    private const string ProviderKey = "loggate";

    private readonly CapturingLogger _logger = new();

    public SubtitleHttpLoggingTests()
    {
        SubtitleHttp.ClearRequestThrottling();
        SubtitleHttp.Initialize(_logger);
    }

    public void Dispose()
    {
        // The gate logger is process-wide static state — hand it back so
        // later classes of this serialized collection run unlogged (their
        // pre-L4a expectations) and the interval table is pristine again.
        SubtitleHttp.Initialize(null);
        SubtitleHttp.ClearRequestThrottling();
    }

    private IReadOnlyList<CapturedEntry> Entries =>
        _logger.Entries.Where(entry => entry.Message.Contains(ProviderKey)).ToList();

    private IReadOnlyList<CapturedEntry> Warnings =>
        Entries.Where(entry => entry.Level == LogLevel.Warning).ToList();

    private IReadOnlyList<CapturedEntry> Debugs =>
        Entries.Where(entry => entry.Level == LogLevel.Debug).ToList();

    [Fact]
    public async Task QuotaExhausted429_LogsSingleDailyLimitWarningWithoutQuery()
    {
        var handler = new StubHttpMessageHandler().Enqueue(_ =>
            new HttpResponseMessage(HttpStatusCode.TooManyRequests)
            {
                Headers = { { "X-RateLimit-Remaining-Requests", "0" } }
            });

        var failure = await SendAndCaptureFailureAsync(handler);

        Assert.Equal(SubtitleProviderFailureReason.DailyLimit, failure.Reason);
        var warning = Assert.Single(Warnings);
        Assert.Contains("429", warning.Message);
        Assert.Contains("DailyLimit", warning.Message);
        Assert.Null(warning.Exception);
        // Request identifier: host+path only — the token in the query must
        // not leak into the log entry.
        Assert.Contains("http://subtitle.test/search", warning.Message);
        Assert.DoesNotContain("secret", warning.Message);
        Assert.DoesNotContain("token", warning.Message);
        Assert.Empty(Debugs);
    }

    [Fact]
    public async Task QuotaExhausted406_LogsSingleDailyLimitWarning()
    {
        var handler = new StubHttpMessageHandler().Enqueue(_ =>
            new HttpResponseMessage(HttpStatusCode.NotAcceptable)
            {
                Headers =
                {
                    { "remaining", "0" },
                    { "reset_time_utc", "2026-10-21T00:00:00Z" }
                }
            });

        var failure = await SendAndCaptureFailureAsync(handler);

        Assert.Equal(SubtitleProviderFailureReason.DailyLimit, failure.Reason);
        Assert.Equal(
            new DateTimeOffset(2026, 10, 21, 0, 0, 0, TimeSpan.Zero),
            failure.QuotaResetUtc);
        var warning = Assert.Single(Warnings);
        Assert.Contains("406", warning.Message);
        Assert.Contains("DailyLimit", warning.Message);
        Assert.Empty(Debugs);
    }

    [Fact]
    public async Task AssrtThrottleStatus493_LogsSingleRateLimitWarning()
    {
        // assrt's non-standard download-throttle status. It maps onto the
        // RateLimited verdict with a one-minute cooldown — NOT the daily
        // limit — so the warning states the cooldown verdict it actually is.
        var handler = new StubHttpMessageHandler().EnqueueStatus((HttpStatusCode)493);

        var failure = await SendAndCaptureFailureAsync(handler);

        Assert.Equal(SubtitleProviderFailureReason.RateLimited, failure.Reason);
        Assert.Equal(TimeSpan.FromMinutes(1), failure.Cooldown);
        var warning = Assert.Single(Warnings);
        Assert.Contains("493", warning.Message);
        Assert.Contains("cooldown", warning.Message);
        Assert.Empty(Debugs);
    }

    [Fact]
    public async Task ServerError500_LogsDebugOnly()
    {
        var handler = new StubHttpMessageHandler().EnqueueStatus(HttpStatusCode.InternalServerError);

        var failure = await SendAndCaptureFailureAsync(handler);

        Assert.Equal(SubtitleProviderFailureReason.ServerError, failure.Reason);
        var debug = Assert.Single(Debugs);
        Assert.Contains("500", debug.Message);
        Assert.Contains("ServerError", debug.Message);
        Assert.Empty(Warnings);
    }

    [Fact]
    public async Task Timeout_LogsWarningWithFullException()
    {
        var handler = new StubHttpMessageHandler().EnqueueHang();

        var failure = await SendAndCaptureFailureAsync(handler, timeout: TimeSpan.FromMilliseconds(100));

        Assert.Equal(SubtitleProviderFailureReason.Timeout, failure.Reason);
        var warning = Assert.Single(Warnings);
        Assert.Contains("timed out", warning.Message);
        Assert.NotNull(warning.Exception);
        Assert.IsAssignableFrom<OperationCanceledException>(warning.Exception);
        Assert.Empty(Debugs);
    }

    [Fact]
    public async Task NetworkError_LogsWarningWithFullException()
    {
        using var client = new HttpClient(new ThrowingHandler())
        {
            BaseAddress = new Uri("http://subtitle.test/")
        };
        // Production providers build absolute request URIs (baseUrl + path);
        // a relative URI would leave RequestMessage.RequestUri host-less and
        // the logged request identifier would degrade to a bare path.
        using var request = new HttpRequestMessage(
            HttpMethod.Get, "http://subtitle.test/search?token=secret");

        var failure = await Assert.ThrowsAsync<SubtitleProviderException>(
            () => SubtitleHttp.SendCheckedAsync(client, request, ProviderKey, CancellationToken.None));

        Assert.Equal(SubtitleProviderFailureReason.NetworkError, failure.Reason);
        var warning = Assert.Single(Warnings);
        Assert.Contains("NetworkError", warning.Message);
        Assert.NotNull(warning.Exception);
        Assert.IsType<HttpRequestException>(warning.Exception);
        Assert.Contains("http://subtitle.test/search", warning.Message);
        Assert.DoesNotContain("secret", warning.Message);
        Assert.Empty(Debugs);
    }

    [Fact]
    public async Task Success_LogsNothing()
    {
        var handler = new StubHttpMessageHandler().EnqueueJson("{\"ok\":true}");

        using var client = handler.ToClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/search");
        using var response = await SubtitleHttp.SendCheckedAsync(
            client, request, ProviderKey, CancellationToken.None);

        Assert.True(response.IsSuccessStatusCode);
        // Nothing at all — the capture logger saw no entry of any level,
        // for this provider or any other (success aggregation is L4b's).
        Assert.Empty(_logger.Entries);
    }

    [Fact]
    public async Task ThrottleWait_OfASecondOrMore_LogsSingleDebug()
    {
        SubtitleHttp.SetMinimumRequestInterval(ProviderKey, TimeSpan.FromMilliseconds(1100));

        await SubtitleHttp.ThrottleAsync(ProviderKey, CancellationToken.None);
        await SubtitleHttp.ThrottleAsync(ProviderKey, CancellationToken.None); // waits ~1.1s

        var debug = Assert.Single(Debugs);
        Assert.Contains("waited", debug.Message);
        Assert.Contains("ms", debug.Message);
        Assert.Empty(Warnings);
    }

    [Fact]
    public async Task ThrottleWait_BelowASecond_LogsNothing()
    {
        SubtitleHttp.SetMinimumRequestInterval(ProviderKey, TimeSpan.FromMilliseconds(300));

        await SubtitleHttp.ThrottleAsync(ProviderKey, CancellationToken.None);
        await SubtitleHttp.ThrottleAsync(ProviderKey, CancellationToken.None); // waits ~0.3s

        Assert.Empty(Debugs);
        Assert.Empty(Warnings);
    }

    /// <summary>
    /// Sends one GET (query carrying a fake token, so the redaction
    /// assertions have something to catch) through the gate and returns the
    /// <see cref="SubtitleProviderException"/> the verdict mapping threw.
    /// </summary>
    private static async Task<SubtitleProviderException> SendAndCaptureFailureAsync(
        StubHttpMessageHandler handler,
        TimeSpan? timeout = null)
    {
        using var client = handler.ToClient(timeout);
        // Absolute URI like the production providers use, so the logged
        // request identifier carries scheme+host+path.
        using var request = new HttpRequestMessage(
            HttpMethod.Get, "http://subtitle.test/search?token=secret");
        return await Assert.ThrowsAsync<SubtitleProviderException>(
            () => SubtitleHttp.SendCheckedAsync(client, request, ProviderKey, CancellationToken.None));
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new HttpRequestException("connection refused");
    }

    private sealed record CapturedEntry(LogLevel Level, string Message, Exception? Exception);

    /// <summary>
    /// Captures every gate log entry — the same direct-ILogger pattern as
    /// LibreServiceLogRedactionTests' capturing provider, minus the factory
    /// (the gate takes the logger itself through
    /// <see cref="SubtitleHttp.Initialize"/>).
    /// </summary>
    private sealed class CapturingLogger : ILogger
    {
        private readonly List<CapturedEntry> _entries = [];

        internal IReadOnlyList<CapturedEntry> Entries => _entries;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => _entries.Add(new CapturedEntry(logLevel, formatter(state, exception), exception));
    }
}
