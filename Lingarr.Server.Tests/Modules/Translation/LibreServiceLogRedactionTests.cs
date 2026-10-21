using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Lingarr.Contracts.Exceptions;
using Lingarr.Core.Configuration;
using Lingarr.Server.Modules.Settings;
using Lingarr.Server.Modules.Translation;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Lingarr.Server.Tests.Modules.Translation;

/// <summary>
/// LibreService used to persist whole subtitle lines and full error response
/// bodies into the log. With the Serilog file sink those entries outlive the
/// container, so the retry-exhausted message must not carry the source text
/// and error bodies are capped at 200 characters with an honest
/// logged/original count.
/// </summary>
public class LibreServiceLogRedactionTests
{
    private sealed record CapturedEntry(LogLevel Level, string Message, Exception? Exception);

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly List<CapturedEntry> _entries = [];

        public IReadOnlyList<CapturedEntry> Entries => _entries;

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(this);

        public void Dispose() { }

        private sealed class CapturingLogger(CapturingLoggerProvider owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
                => owner._entries.Add(new CapturedEntry(logLevel, formatter(state, exception), exception));
        }
    }

    private sealed class FixedStatusHandler(HttpStatusCode statusCode, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
    }

    private static LibreService CreateService(
        HttpMessageHandler handler, CapturingLoggerProvider logs, string maxRetries)
    {
        var settings = new Mock<ISettingService>();
        settings
            .Setup(s => s.GetSettings(It.IsAny<IEnumerable<string>>()))
            .ReturnsAsync(new Dictionary<string, string>
            {
                [SettingKeys.Translation.LibreTranslate.Url] = "http://libre.local",
                [SettingKeys.Translation.MaxRetries] = maxRetries,
                [SettingKeys.Translation.RetryDelay] = "0",
                [SettingKeys.Translation.RetryDelayMultiplier] = "2"
            });
        settings
            .Setup(s => s.GetEncryptedSetting(SettingKeys.Translation.LibreTranslate.ApiKey))
            .ReturnsAsync("libre-key");

        var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(logs));
        return new LibreService(
            new HttpClient(handler),
            settings.Object,
            loggerFactory.CreateLogger<LibreService>(),
            new LanguageCodeService());
    }

    [Fact]
    public async Task RetryExhausted_LogsWithoutTheSubtitleText()
    {
        const string subtitleLine = "a dialogue line that must not reach the log";
        var logs = new CapturingLoggerProvider();
        var service = CreateService(
            new FixedStatusHandler(HttpStatusCode.TooManyRequests, "{}"), logs, maxRetries: "0");

        await Assert.ThrowsAsync<TranslationException>(
            () => service.TranslateAsync(subtitleLine, "en", "nl", null, null, CancellationToken.None));

        var exhausted = Assert.Single(logs.Entries, e => e.Message.Contains("Max retries exhausted"));
        // {StatusCode} renders the enum name, not the number.
        Assert.Equal("Max retries exhausted (TooManyRequests)", exhausted.Message);
        // and no other entry leaked the line either
        Assert.DoesNotContain(logs.Entries, e => e.Message.Contains("dialogue"));
    }

    [Fact]
    public async Task ResponseError_LongBodyIsTruncatedTo200CharsWithOriginalLength()
    {
        var longBody = new string('x', 845);
        var logs = new CapturingLoggerProvider();
        var service = CreateService(
            new FixedStatusHandler(HttpStatusCode.InternalServerError, longBody), logs, maxRetries: "0");

        await Assert.ThrowsAsync<TranslationException>(
            () => service.TranslateAsync("some line", "en", "nl", null, null, CancellationToken.None));

        var contentEntry = Assert.Single(logs.Entries, e => e.Message.Contains("Response Content"));
        Assert.Contains("(200/845 chars)", contentEntry.Message);
        // the excerpt is capped, not the full body
        Assert.DoesNotContain(new string('x', 201), contentEntry.Message);
    }

    [Fact]
    public async Task ResponseError_ShortBodyIsLoggedInFullWithMatchingCounts()
    {
        var logs = new CapturingLoggerProvider();
        var service = CreateService(
            new FixedStatusHandler(HttpStatusCode.InternalServerError, "nope"), logs, maxRetries: "0");

        await Assert.ThrowsAsync<TranslationException>(
            () => service.TranslateAsync("some line", "en", "nl", null, null, CancellationToken.None));

        var contentEntry = Assert.Single(logs.Entries, e => e.Message.Contains("Response Content"));
        Assert.Contains("(4/4 chars)", contentEntry.Message);
        Assert.EndsWith(": nope", contentEntry.Message);
    }
}
