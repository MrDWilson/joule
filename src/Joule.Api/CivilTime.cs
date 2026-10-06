namespace Joule;

internal static class CivilTime
{
    // Resolve the first occurrence of a local clock time, advancing over a
    // skipped interval (including a whole skipped date) to its first valid minute.
    public static DateTimeOffset FirstValidInstant(DateTime local, TimeZoneInfo zone)
    {
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        while (true)
        {
            if (!zone.IsInvalidTime(local))
            {
                var instant = zone.IsAmbiguousTime(local)
                    ? new DateTimeOffset(local, zone.GetAmbiguousTimeOffsets(local).Max()).ToUniversalTime()
                    : new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, zone));
                // Some historical base-offset changes normalize nonexistent local
                // times instead of marking the whole gap invalid. Require a round trip.
                if (TimeZoneInfo.ConvertTime(instant, zone).DateTime == local) return instant;
            }
            local = local.AddMinutes(1);
        }
    }
}
