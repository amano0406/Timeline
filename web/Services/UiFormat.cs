using System.Globalization;

namespace Timeline.Web.Services;

public static class UiFormat
{
    public static string Bytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var value = (double)Math.Max(0, bytes);
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return $"{value:0.#} {units[unit]}";
    }

    public static string Duration(double seconds)
    {
        if (seconds <= 0)
        {
            return "-";
        }

        var time = TimeSpan.FromSeconds(seconds);
        if (time.TotalHours >= 1)
        {
            return $"{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}";
        }

        return $"{time.Minutes:00}:{time.Seconds:00}";
    }

    public static string ShortDate(string value)
    {
        return ShortDate(value, "");
    }

    public static string ShortDate(string value, string timeZoneId)
    {
        if (DateTimeOffset.TryParse(value, CultureInfo.CurrentCulture, DateTimeStyles.AssumeLocal, out var parsed))
        {
            return ConvertToTimeZone(parsed, timeZoneId).ToString("yyyy-MM-dd HH:mm");
        }

        return string.IsNullOrWhiteSpace(value) ? "-" : value;
    }

    private static DateTimeOffset ConvertToTimeZone(DateTimeOffset value, string timeZoneId)
    {
        if (string.IsNullOrWhiteSpace(timeZoneId))
        {
            return value;
        }

        try
        {
            var zone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
            return TimeZoneInfo.ConvertTime(value, zone);
        }
        catch (TimeZoneNotFoundException)
        {
            return value;
        }
        catch (InvalidTimeZoneException)
        {
            return value;
        }
    }
}
