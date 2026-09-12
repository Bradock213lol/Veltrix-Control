using NexaGrid.Contracts;

namespace NexaGrid.Core.Health;

public static class HealthScorer
{
    public static int Calculate(TelemetrySnapshot? snapshot, bool online)
    {
        if (!online || snapshot is null)
        {
            return 0;
        }

        var score = 100;
        if (snapshot.CpuPercent > 90) score -= 25;
        else if (snapshot.CpuPercent > 75) score -= 10;

        var memoryPercent = snapshot.TotalMemoryBytes == 0
            ? 100
            : snapshot.UsedMemoryBytes * 100d / snapshot.TotalMemoryBytes;
        if (memoryPercent > 95) score -= 35;
        else if (memoryPercent > 85) score -= 15;

        if (snapshot.Disks.Any(d => d.TotalBytes > 0 && d.AvailableBytes * 100d / d.TotalBytes < 10)) score -= 25;
        return Math.Max(score, 0);
    }
}
