using Lingarr.Server.Modules.Infrastructure;
using Xunit;

namespace Lingarr.Server.Tests.Services;

/// <summary>
/// Server-side equivalent of the frontend cron-validator gate: only
/// 5-field Hangfire-compatible expressions pass; everything else —
/// 6-field seconds syntax, Quartz L/#/W tokens, out-of-range values —
/// is rejected before any registration happens.
/// </summary>
public class CronExpressionValidatorTests
{
    [Theory]
    [InlineData("0 4 * * *")]
    [InlineData("*/30 * * * *")]
    [InlineData("0 */2 * * *")]
    [InlineData("15 3 1,15 * *")]
    [InlineData("0 0 * * 0")]
    [InlineData("0 0 * * 7")]
    [InlineData("0 4 * * 1-5")]
    [InlineData("30 8 1 jan *")]
    [InlineData("0 9 * * mon-fri")]
    [InlineData("0 0 1-7 * SUN")]
    [InlineData("5-10/2 * * * *")]
    [InlineData("@daily")]
    [InlineData("@hourly")]
    [InlineData("@weekly")]
    [InlineData("  0 4 * * *  ")]
    public void IsValid_AcceptsFiveFieldExpressions(string expression)
    {
        Assert.True(CronExpressionValidator.IsValid(expression));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("* * * *")]              // four fields
    [InlineData("0 4 * * * *")]          // six fields (seconds syntax)
    [InlineData("61 * * * *")]           // minute out of range
    [InlineData("* 24 * * *")]           // hour out of range
    [InlineData("* * 0 * *")]            // day of month out of range
    [InlineData("* * 32 * *")]           // day of month out of range
    [InlineData("* * * 13 *")]           // month out of range
    [InlineData("* * * * 8")]            // day of week out of range
    [InlineData("a b c d e")]            // non-numeric
    [InlineData("*/0 * * * *")]          // zero step
    [InlineData("5-1 * * * *")]          // inverted range
    [InlineData("0 4 * * mon-x")]        // unknown name
    [InlineData("0 4 L * *")]            // Quartz L token
    [InlineData("0 4 * * 1#2")]          // Quartz # token
    [InlineData("0 4 ? * *")]            // ? is not Hangfire syntax
    [InlineData("0 4 * * *,")]           // trailing comma
    [InlineData("0 4 * * /5")]           // step without range body
    public void IsValid_RejectsMalformedExpressions(string? expression)
    {
        Assert.False(CronExpressionValidator.IsValid(expression));
    }
}
