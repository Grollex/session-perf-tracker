using SessionPerfTracker.Domain.Abstractions;
using SessionPerfTracker.Domain.Models;

namespace SessionPerfTracker.Infrastructure.Events;

public sealed class EventPipeline
{
    private readonly Dictionary<string, IEventDetector> _detectors = new(StringComparer.OrdinalIgnoreCase);

    public void Register(IEventDetector detector) => _detectors[detector.Id] = detector;

    public async Task<IReadOnlyList<PerformanceEvent>> DetectAsync(
        EventDetectionInput input,
        CancellationToken cancellationToken = default)
    {
        var batches = await Task.WhenAll(_detectors.Values.Select(detector => detector.DetectAsync(input, cancellationToken)));
        return batches.SelectMany(batch => batch).ToArray();
    }
}
