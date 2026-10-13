namespace Lingarr.Server.Modules.Infrastructure;

/// <summary>
/// Equivalent of the frontend cron-validator for the server: validates the
/// 5-field cron syntax Hangfire accepts (minute hour day-of-month month
/// day-of-week) — literal values, ranges, steps and comma lists, plus the
/// 3-letter day/month names and the @hourly/@daily/@weekly/@monthly macros.
/// Hangfire-specific extensions (L, #, W, ?) are not supported and rejected.
/// </summary>
public static class CronExpressionValidator
{
    private static readonly string[] MonthNames =
        ["jan", "feb", "mar", "apr", "may", "jun", "jul", "aug", "sep", "oct", "nov", "dec"];

    private static readonly string[] DayNames = ["sun", "mon", "tue", "wed", "thu", "fri", "sat"];

    private static readonly (int Min, int Max)[] FieldRanges =
    [
        (0, 59),  // minute
        (0, 23),  // hour
        (1, 31),  // day of month
        (1, 12),  // month
        (0, 7)    // day of week (0 and 7 are both Sunday)
    ];

    private static readonly Dictionary<string, string> Macros = new(StringComparer.OrdinalIgnoreCase)
    {
        ["@hourly"] = "0 * * * *",
        ["@daily"] = "0 0 * * *",
        ["@midnight"] = "0 0 * * *",
        ["@weekly"] = "0 0 * * 0",
        ["@monthly"] = "0 0 1 * *",
        ["@yearly"] = "0 0 1 1 *",
        ["@annually"] = "0 0 1 1 *"
    };

    /// <summary>True when the expression is a valid 5-field cron for Hangfire.</summary>
    public static bool IsValid(string? expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
        {
            return false;
        }

        var normalized = expression.Trim().ToLowerInvariant();
        if (Macros.TryGetValue(normalized, out var expanded))
        {
            normalized = expanded;
        }

        var fields = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length != 5)
        {
            return false;
        }

        for (var i = 0; i < fields.Length; i++)
        {
            if (!IsValidField(fields[i], i))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsValidField(string field, int index)
    {
        var (min, max) = FieldRanges[index];
        var names = index == 3 ? MonthNames : index == 4 ? DayNames : null;

        foreach (var part in field.Split(','))
        {
            if (part.Length == 0)
            {
                return false;
            }

            var body = part;
            var step = 1;
            var slash = part.IndexOf('/');
            if (slash >= 0)
            {
                body = part[..slash];
                var stepText = part[(slash + 1)..];
                if (stepText.Length == 0 || !int.TryParse(stepText, out step) || step <= 0)
                {
                    return false;
                }
            }

            int from;
            int to;
            if (body == "*")
            {
                from = min;
                to = max;
            }
            else
            {
                var dash = body.IndexOf('-');
                if (dash >= 0)
                {
                    var fromText = body[..dash];
                    var toText = body[(dash + 1)..];
                    if (!TryParseValue(fromText, names, out from) || !TryParseValue(toText, names, out to))
                    {
                        return false;
                    }
                }
                else if (!TryParseValue(body, names, out from))
                {
                    return false;
                }
                else
                {
                    to = from;
                }
            }

            if (from < min || to > max || from > to)
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryParseValue(string text, string[]? names, out int value)
    {
        if (int.TryParse(text, out value))
        {
            return true;
        }

        if (names != null)
        {
            var index = Array.IndexOf(names, text);
            if (index >= 0)
            {
                value = index + (names == MonthNames ? 1 : 0);
                return true;
            }
        }

        return false;
    }
}
