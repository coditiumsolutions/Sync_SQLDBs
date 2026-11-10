using System.Globalization;

namespace DatabaseSync.Core.Scheduling;

/// <summary>
/// Five-field cron: minute hour day-of-month month day-of-week.
/// Supports *, lists, ranges, and steps. Month and day names are accepted.
/// Day-of-week: 0 or 7 is Sunday. When both day-of-month and day-of-week are restricted,
/// a date matches if either field matches (standard cron OR semantics).
/// </summary>
public sealed class CronExpression
{
    private readonly bool[] _minutes;
    private readonly bool[] _hours;
    private readonly bool[] _daysOfMonth;
    private readonly bool[] _months;
    private readonly bool[] _daysOfWeek;
    private readonly bool _anyDayOfMonth;
    private readonly bool _anyDayOfWeek;

    private CronExpression(
        bool[] minutes,
        bool[] hours,
        bool[] daysOfMonth,
        bool[] months,
        bool[] daysOfWeek,
        bool anyDayOfMonth,
        bool anyDayOfWeek)
    {
        _minutes = minutes;
        _hours = hours;
        _daysOfMonth = daysOfMonth;
        _months = months;
        _daysOfWeek = daysOfWeek;
        _anyDayOfMonth = anyDayOfMonth;
        _anyDayOfWeek = anyDayOfWeek;
    }

    public static bool TryParse(string? expression, out CronExpression? cron, out string? error)
    {
        cron = null;
        error = null;
        if (string.IsNullOrWhiteSpace(expression))
        {
            error = "Cron expression is empty.";
            return false;
        }

        var fields = expression.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length != 5)
        {
            error = "Cron expression must have 5 fields: minute hour day-of-month month day-of-week.";
            return false;
        }

        try
        {
            var minutes = ParseField(fields[0], 0, 59, FieldKind.Number);
            var hours = ParseField(fields[1], 0, 23, FieldKind.Number);
            var days = ParseField(fields[2], 1, 31, FieldKind.Number);
            var months = ParseField(fields[3], 1, 12, FieldKind.Month);
            var dows = ParseField(fields[4], 0, 7, FieldKind.DayOfWeek);
            cron = new CronExpression(
                minutes,
                hours,
                days,
                months,
                dows,
                IsWildcard(fields[2]),
                IsWildcard(fields[4]));
            return true;
        }
        catch (FormatException ex)
        {
            error = ex.Message;
            return false;
        }
    }

    public bool IsMatch(DateTime local)
    {
        if (!_minutes[local.Minute] || !_hours[local.Hour] || !_months[local.Month])
        {
            return false;
        }

        var dom = _daysOfMonth[local.Day];
        var dow = _daysOfWeek[(int)local.DayOfWeek] || _daysOfWeek[7] && local.DayOfWeek == DayOfWeek.Sunday;
        if (_anyDayOfMonth && _anyDayOfWeek)
        {
            return true;
        }

        if (_anyDayOfMonth)
        {
            return dow;
        }

        if (_anyDayOfWeek)
        {
            return dom;
        }

        return dom || dow;
    }

    private static bool IsWildcard(string field) =>
        field.Trim() is "*" or "*/1";

    private static bool[] ParseField(string field, int min, int max, FieldKind kind)
    {
        var set = new bool[max + 1];
        foreach (var rawPart in field.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var part = rawPart;
            var step = 1;
            var slash = part.IndexOf('/');
            if (slash >= 0)
            {
                if (!int.TryParse(part[(slash + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out step) || step <= 0)
                {
                    throw new FormatException($"Invalid step in '{field}'.");
                }

                part = part[..slash];
            }

            int start;
            int end;
            if (part is "*" or "?")
            {
                start = min;
                end = max;
            }
            else if (part.Contains('-', StringComparison.Ordinal))
            {
                var bounds = part.Split('-', 2, StringSplitOptions.TrimEntries);
                start = ParseToken(bounds[0], min, max, kind);
                end = ParseToken(bounds[1], min, max, kind);
                // SUN is 0, but FRI-SUN means Friday through Sunday.
                if (kind == FieldKind.DayOfWeek && end == 0 && start > 0)
                {
                    end = 7;
                }
            }
            else
            {
                start = end = ParseToken(part, min, max, kind);
            }

            if (start < min || end > max || end < start)
            {
                throw new FormatException($"Range '{rawPart}' is outside {min}-{max}.");
            }

            for (var value = start; value <= end; value += step)
            {
                set[value] = true;
            }
        }

        if (!set.Any(flag => flag))
        {
            throw new FormatException($"Field '{field}' matched no values.");
        }

        return set;
    }

    private static int ParseToken(string token, int min, int max, FieldKind kind)
    {
        if (kind == FieldKind.Month && MonthNames.TryGetValue(token, out var month))
        {
            return month;
        }

        if (kind == FieldKind.DayOfWeek && DayNames.TryGetValue(token, out var day))
        {
            return day;
        }

        if (!int.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out var number)
            || number < min
            || number > max)
        {
            throw new FormatException($"Value '{token}' is outside {min}-{max}.");
        }

        return number;
    }

    private enum FieldKind
    {
        Number,
        Month,
        DayOfWeek
    }

    private static readonly Dictionary<string, int> MonthNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["JAN"] = 1, ["FEB"] = 2, ["MAR"] = 3, ["APR"] = 4, ["MAY"] = 5, ["JUN"] = 6,
        ["JUL"] = 7, ["AUG"] = 8, ["SEP"] = 9, ["OCT"] = 10, ["NOV"] = 11, ["DEC"] = 12
    };

    private static readonly Dictionary<string, int> DayNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["SUN"] = 0, ["MON"] = 1, ["TUE"] = 2, ["WED"] = 3, ["THU"] = 4, ["FRI"] = 5, ["SAT"] = 6,
        ["SUNDAY"] = 0, ["MONDAY"] = 1, ["TUESDAY"] = 2, ["WEDNESDAY"] = 3,
        ["THURSDAY"] = 4, ["FRIDAY"] = 5, ["SATURDAY"] = 6
    };
}
