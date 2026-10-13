using Microsoft.Extensions.Logging;
using Xunit;
using Lingarr.Server.Modules.Infrastructure;

namespace Lingarr.Server.Tests.Providers;

public class LogEntryTests
{
    [Theory]
    [InlineData("Lingarr.Server.Services.TranslationService", "TranslationService")]
    [InlineData("TranslationService", "TranslationService")]
    [InlineData(null, "")]
    public void FormattedSource_KeepsTheLastSegmentOfTheCategory(string? category, string expected)
    {
        var entry = new LogEntry { LogLevel = LogLevel.Information, Category = category };

        Assert.Equal(expected, entry.FormattedSource);
    }
}
