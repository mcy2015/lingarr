using System;
using System.Net;
using System.Net.Http;
using Lingarr.Server.Modules.Infrastructure;
using Xunit;

namespace Lingarr.Server.Tests.Infrastructure;

/// <summary>
/// With the Serilog file sink, logs became persistent: anything
/// credential-bearing that used to age out of container stdout now lives on
/// disk. These tests pin the redaction helpers applied at log boundaries.
/// </summary>
public class LogSanitizerTests
{
    // ── Mask ────────────────────────────────────────────────────────────

    [Fact]
    public void Mask_ReplacesApikeyValueWithStars()
    {
        var masked = LogSanitizer.Mask(
            "GET http://radarr.local/api/v3/movie?apikey=abc123DEF refused");

        Assert.Contains("apikey=***", masked);
        Assert.DoesNotContain("abc123DEF", masked);
    }

    [Fact]
    public void Mask_CoversSnakeCaseApiKey_AndStopsAtNextParameter()
    {
        var masked = LogSanitizer.Mask("libre failed: api_key=t0ps3cret&source=en");

        Assert.Contains("api_key=***&source=en", masked);
        Assert.DoesNotContain("t0ps3cret", masked);
    }

    [Fact]
    public void Mask_IsCaseInsensitiveOnTheParameterName()
    {
        var masked = LogSanitizer.Mask("url ApiKey=SUPERsecret gone");

        Assert.Contains("ApiKey=***", masked);
        Assert.DoesNotContain("SUPERsecret", masked);
    }

    [Fact]
    public void Mask_LeavesCleanConnectionFailureTextUntouched()
    {
        const string clean = "No such host is known. (radarr.local:7878)";

        Assert.Equal(clean, LogSanitizer.Mask(clean));
    }

    [Fact]
    public void Mask_NullAndEmptyYieldEmpty()
    {
        Assert.Equal(string.Empty, LogSanitizer.Mask(null));
        Assert.Equal(string.Empty, LogSanitizer.Mask(string.Empty));
    }

    [Fact]
    public void ContainsSensitiveData_DetectsAndRejects()
    {
        Assert.True(LogSanitizer.ContainsSensitiveData("x?apikey=1"));
        Assert.False(LogSanitizer.ContainsSensitiveData("radarr.local:7878"));
        Assert.False(LogSanitizer.ContainsSensitiveData(null));
    }

    // ── Truncate ────────────────────────────────────────────────────────

    [Fact]
    public void Truncate_ShortTextPassesThroughWithFullLength()
    {
        var (text, originalLength) = LogSanitizer.Truncate("short body", 200);

        Assert.Equal("short body", text);
        Assert.Equal(10, originalLength);
    }

    [Fact]
    public void Truncate_LongTextIsCutToLimit_AndReportsOriginalLength()
    {
        var body = new string('x', 845);

        var (text, originalLength) = LogSanitizer.Truncate(body, 200);

        Assert.Equal(new string('x', 200), text);
        Assert.Equal(200, text.Length);
        Assert.Equal(845, originalLength);
    }

    [Fact]
    public void Truncate_ExactlyAtLimitIsNotCut()
    {
        var body = new string('y', 200);

        var (text, originalLength) = LogSanitizer.Truncate(body, 200);

        Assert.Equal(200, text.Length);
        Assert.Equal(200, originalLength);
    }

    [Fact]
    public void Truncate_NullAndEmptyYieldEmptyWithZeroLength()
    {
        var (nullText, nullLength) = LogSanitizer.Truncate(null, 200);
        var (emptyText, emptyLength) = LogSanitizer.Truncate(string.Empty, 200);

        Assert.Equal(string.Empty, nullText);
        Assert.Equal(0, nullLength);
        Assert.Equal(string.Empty, emptyText);
        Assert.Equal(0, emptyLength);
    }

    // ── MaskHttpRequestException ────────────────────────────────────────

    [Fact]
    public void MaskHttpRequestException_CleanChain_ReturnsNull()
    {
        var clean = new HttpRequestException(
            "No such host is known. (radarr.local:7878)",
            new Exception("Connection refused"));

        Assert.Null(LogSanitizer.MaskHttpRequestException(clean));
    }

    [Fact]
    public void MaskHttpRequestException_SensitiveMessage_ReturnsMaskedCloneWithTypeAndStatusCode()
    {
        var dirty = new HttpRequestException(
            "GET http://radarr.local/api/v3/movie?apikey=s3cr3t failed",
            new Exception("connect failed, target apikey=inn3r"),
            HttpStatusCode.BadGateway);

        var masked = LogSanitizer.MaskHttpRequestException(dirty);

        Assert.NotNull(masked);
        var clone = Assert.IsType<HttpRequestException>(masked);
        Assert.Equal(HttpStatusCode.BadGateway, clone.StatusCode);
        // neither the outer nor the inner apikey may survive a ToString render
        Assert.DoesNotContain("s3cr3t", clone.ToString());
        Assert.DoesNotContain("inn3r", clone.ToString());
        Assert.Contains("apikey=***", clone.ToString());
    }
}
