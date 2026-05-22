using CareerPanda.Framework.Configuration;

namespace CareerPanda.Framework.Timezone;

public static class TimezoneManager
{
    public static DateTime ToLocal(DateTime utc, TimeZoneConfig config)
    {
        try
        {
            var tz = TimeZoneInfo.FindSystemTimeZoneById(config.Timezone);
            return TimeZoneInfo.ConvertTimeFromUtc(utc, tz);
        }
        catch
        {
            return utc;
        }
    }

    public static string FormatDate(DateTime utc, TimeZoneConfig config) =>
        ToLocal(utc, config).ToString(config.DateFormat);

    public static string FormatDateTime(DateTime utc, TimeZoneConfig config) =>
        $"{FormatDate(utc, config)} {ToLocal(utc, config).ToString(config.TimeFormat)}";
}
