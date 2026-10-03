using SessionPerfTracker.Domain.Models;

namespace SessionPerfTracker.Domain.Metrics;

public sealed record MetricDefinition(
    MetricKey Key,
    string Label,
    string Unit,
    ComparisonDirection Direction,
    double? WarningThreshold = null,
    double? SpikeDelta = null);

public static class MetricCatalog
{
    public static IReadOnlyList<MetricDefinition> All { get; } =
    [
        new(MetricKey.CpuPercent, "CPU", "%", ComparisonDirection.LowerIsBetter, 80, 35),
        new(MetricKey.MemoryMb, "RAM", "MB", ComparisonDirection.LowerIsBetter, 4096, 512),
        new(MetricKey.GpuPercent, "GPU", "%", ComparisonDirection.LowerIsBetter, 85, 40),
        new(MetricKey.GpuMemoryMb, "GPU Memory", "MB", ComparisonDirection.LowerIsBetter),
        new(MetricKey.DiskReadMbPerSec, "Disk Read", "MB/s", ComparisonDirection.LowerIsBetter, 180, 80),
        new(MetricKey.DiskWriteMbPerSec, "Disk Write", "MB/s", ComparisonDirection.LowerIsBetter, 120, 60),
        new(MetricKey.TemperatureC, "GPU Temp", "°C", ComparisonDirection.LowerIsBetter, 87)
    ];

    public const double DefaultGpuThresholdPercent = 85;
    public const double GpuTemperatureWarningC = 87;

    public static MetricDefinition Get(MetricKey key) =>
        All.FirstOrDefault(metric => metric.Key == key)
        ?? throw new ArgumentOutOfRangeException(nameof(key), key, "Unknown metric key.");
}
