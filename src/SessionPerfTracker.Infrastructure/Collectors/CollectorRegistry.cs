using SessionPerfTracker.Domain.Abstractions;

namespace SessionPerfTracker.Infrastructure.Collectors;

public sealed class CollectorRegistry
{
    private readonly Dictionary<string, IMetricCollector> _collectors = new(StringComparer.OrdinalIgnoreCase);

    public void Register(IMetricCollector collector) => _collectors[collector.Id] = collector;

    public IReadOnlyList<IMetricCollector> List() => _collectors.Values.ToArray();
}
