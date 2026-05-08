using SessionPerfTracker.Domain.Abstractions;
using SessionPerfTracker.Domain.Models;

namespace SessionPerfTracker.Infrastructure.Collectors;

public sealed class UnavailableMetricCollector : IMetricCollector
{
    public string Id => "unavailable-metric-collector";
    public string Label => "Unavailable collector placeholder";

    public Task<MetricCapabilities> GetCapabilitiesAsync(TargetDescriptor target, CancellationToken cancellationToken = default) =>
        Task.FromResult(new MetricCapabilities());

    public Task<MetricSample> CollectAsync(
        TargetDescriptor target,
        string sessionId,
        long elapsedMs,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new MetricSample
        {
            Id = $"unavailable_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}",
            SessionId = sessionId,
            Timestamp = DateTimeOffset.UtcNow,
            ElapsedMs = elapsedMs,
            RootProcessId = target.ProcessId,
            ProcessCount = target.IncludeChildProcesses ? 1 : 0
        });
    }
}
