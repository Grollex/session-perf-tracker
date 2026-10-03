using SessionPerfTracker.Domain.Abstractions;
using SessionPerfTracker.Domain.Models;
using SessionPerfTracker.Infrastructure.Native;

namespace SessionPerfTracker.Infrastructure.Targeting;

public sealed class ProcessTargetResolver : IProcessTargetResolver
{
    private static readonly HashSet<string> HiddenSystemProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Idle",
        "System",
        "Registry",
        "Memory Compression",
        "Secure System",
        "vmmem"
    };

    /// <summary>One process snapshot plus one window pass, instead of per-process handle and window queries.</summary>
    public Task<IReadOnlyList<TargetDescriptor>> ListRunningTargetsAsync(CancellationToken cancellationToken = default) =>
        Task.Run<IReadOnlyList<TargetDescriptor>>(() =>
        {
            var snapshot = SystemProcessSnapshotReader.Shared.Capture();
            var windows = WindowSnapshot.Capture();
            return snapshot.Processes.Values
                .Where(process => process.ProcessId > 4 && !HiddenSystemProcesses.Contains(process.Name))
                .Select(process =>
                {
                    var name = StripExe(process.Name);
                    var windowTitle = windows.GetTitle(process.ProcessId);
                    return new TargetDescriptor
                    {
                        Id = $"process_{process.ProcessId}",
                        Kind = TargetSelectionKind.Process,
                        LifecycleMode = TargetLifecycleMode.AttachToRunning,
                        DisplayName = windowTitle is null
                            ? $"{name} ({process.ProcessId})"
                            : $"{name} ({process.ProcessId}) - {windowTitle}",
                        ProcessId = process.ProcessId,
                        WindowTitle = windowTitle,
                        IncludeChildProcesses = false,
                        ScopeMode = ProcessScopeMode.RootOnly
                    };
                })
                .OrderByDescending(target => !string.IsNullOrWhiteSpace(target.WindowTitle))
                .ThenBy(target => target.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }, cancellationToken);

    public Task<TargetDescriptor> ResolveExecutableAsync(
        string path,
        bool includeChildProcesses,
        CancellationToken cancellationToken = default)
    {
        var displayName = Path.GetFileName(path);
        return Task.FromResult(new TargetDescriptor
        {
            Id = $"exe_{Guid.NewGuid():N}",
            Kind = TargetSelectionKind.Executable,
            LifecycleMode = TargetLifecycleMode.LaunchAndTrack,
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? path : displayName,
            ExecutablePath = path,
            IncludeChildProcesses = includeChildProcesses,
            ScopeMode = includeChildProcesses ? ProcessScopeMode.IncludeChildProcesses : ProcessScopeMode.RootOnly
        });
    }

    public Task<TargetDescriptor> ResolveProcessAsync(
        int processId,
        bool includeChildProcesses,
        CancellationToken cancellationToken = default)
    {
        var snapshot = SystemProcessSnapshotReader.Shared.Capture();
        if (!snapshot.TryGet(processId, out var process))
        {
            throw new ArgumentException($"Process with an Id of {processId} is not running.", nameof(processId));
        }

        return Task.FromResult(new TargetDescriptor
        {
            Id = $"process_{processId}",
            Kind = TargetSelectionKind.Process,
            LifecycleMode = TargetLifecycleMode.AttachToRunning,
            DisplayName = $"{StripExe(process.Name)} ({processId})",
            ExecutablePath = ProcessImagePath.TryQuery(processId),
            ProcessId = processId,
            IncludeChildProcesses = includeChildProcesses,
            ScopeMode = includeChildProcesses ? ProcessScopeMode.IncludeChildProcesses : ProcessScopeMode.RootOnly
        });
    }

    private static string StripExe(string name) =>
        name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
}
