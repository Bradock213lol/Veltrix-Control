using VeltrixControl.Core.Formatting;

namespace VeltrixControl.UnitTests;

public sealed class MetricFormatterTests
{
    private const long KiB = 1024;
    private const long MiB = 1024 * KiB;
    private const long GiB = 1024 * MiB;

    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(512, "512 B")]
    [InlineData(KiB, "1 KB")]
    [InlineData(2 * MiB, "2 MB")]
    [InlineData(32 * GiB, "32 GB")]
    public void BytesUsesReadableBinaryUnits(long bytes, string expected)
    {
        Assert.Equal(expected, MetricFormatter.Bytes(bytes));
    }

    [Fact]
    public void MemoryShowsUsedAndTotalTogether()
    {
        var text = MetricFormatter.Memory(6 * GiB, 32 * GiB);
        Assert.Equal("6 GB / 32 GB", text);
    }

    [Fact]
    public void FreeMemoryShowsRemainingCapacity()
    {
        Assert.Equal("26 GB free", MetricFormatter.FreeMemory(6 * GiB, 32 * GiB));
        Assert.Contains("free", MetricFormatter.MemoryDetail(6 * GiB, 32 * GiB));
    }

    [Fact]
    public void MissingTelemetryIsExplicit()
    {
        Assert.Equal("Unavailable", MetricFormatter.Memory(1024, 0));
        Assert.Equal("Free memory unavailable", MetricFormatter.FreeMemory(1024, 0));
        Assert.Equal("Memory telemetry unavailable", MetricFormatter.MemoryDetail(0, 0));
    }

    [Theory]
    [InlineData(8, 32, 25)]
    [InlineData(16, 32, 50)]
    [InlineData(32, 32, 100)]
    [InlineData(0, 0, 0)]
    public void PercentIsClamped(long used, long total, double expected)
    {
        Assert.Equal(expected, MetricFormatter.Percent(used, total));
    }

    [Theory]
    [InlineData(3600, "1h 0m")]
    [InlineData(90000, "1d 1h")]
    public void UptimeReadsNaturally(long seconds, string expected)
    {
        Assert.Equal(expected, MetricFormatter.Uptime(seconds));
    }
}
