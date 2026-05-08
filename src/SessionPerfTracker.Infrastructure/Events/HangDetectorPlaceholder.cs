using SessionPerfTracker.Domain.Abstractions;
using SessionPerfTracker.Domain.Models;

namespace SessionPerfTracker.Infrastructure.Events;

public sealed class HangDetectorPlaceholder : IHangDetector
{
    public string Id => "hang-detector-placeholder";
    public IReadOnlySet<EventKind> HandledKinds { get; } = new HashSet<EventKind> { EventKind.HangSuspected };
    public bool RequiresWindowHandle => true;
    public string Reliability => "external-provider";

    public Task<IReadOnlyList<PerformanceEvent>> DetectAsync(
        EventDetectionInput input,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<PerformanceEvent>>([]);
}
