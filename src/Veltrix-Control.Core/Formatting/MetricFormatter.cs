using System.Globalization;

namespace VeltrixControl.Core.Formatting;

public static class MetricFormatter
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB", "PB"];

    public static string Bytes(long bytes)
    {
        var value = Math.Max(0, (double)bytes);
        var unit = 0;
        while (value >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        var format = value >= 100 || unit == 0 ? "0" : "0.#";
        return value.ToString(format, CultureInfo.CurrentCulture) + " " + Units[unit];
    }

    public static string Memory(long usedBytes, long totalBytes) =>
        totalBytes <= 0 ? "Unavailable" : $"{Bytes(usedBytes)} / {Bytes(totalBytes)}";

    public static string FreeMemory(long usedBytes, long totalBytes) =>
        totalBytes <= 0 ? "Free memory unavailable" : $"{Bytes(Math.Max(0, totalBytes - usedBytes))} free";

    public static string MemoryDetail(long usedBytes, long totalBytes)
    {
        if (totalBytes <= 0) return "Memory telemetry unavailable";
        return $"{Bytes(Math.Max(0, totalBytes - usedBytes))} free · {Percent(usedBytes, totalBytes):0.#}% used";
    }

    public static double Percent(long used, long total) =>
        total <= 0 ? 0 : Math.Clamp(used * 100d / total, 0, 100);

    public static string Uptime(long seconds)
    {
        var duration = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return duration.TotalDays >= 1
            ? $"{(int)duration.TotalDays}d {duration.Hours}h"
            : $"{duration.Hours}h {duration.Minutes}m";
    }
}
