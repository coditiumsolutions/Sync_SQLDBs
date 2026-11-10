namespace DatabaseSync.Core.Diff;

/// <summary>
/// Normalises SQL datetime values for conflict comparison.
/// datetimeoffset is converted to UTC. datetime, datetime2, and smalldatetime are compared
/// as the clock value that was stored (offset zero), so both servers must store those columns the same way.
/// </summary>
public static class TimestampValue
{
    public static DateTimeOffset? Read(object? value)
    {
        switch (value)
        {
            case null:
            case DBNull:
                return null;
            case DateTimeOffset dto:
                return dto.ToUniversalTime();
            case DateTime dt when dt.Kind == DateTimeKind.Utc:
                return new DateTimeOffset(dt);
            case DateTime dt:
                var clock = DateTime.SpecifyKind(dt, DateTimeKind.Unspecified);
                return new DateTimeOffset(clock, TimeSpan.Zero);
            default:
                return null;
        }
    }
}
