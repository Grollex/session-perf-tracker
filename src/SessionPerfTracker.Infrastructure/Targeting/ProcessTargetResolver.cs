using System.Diagnostics;
using SessionPerfTracker.Domain.Abstractions;
using SessionPerfTracker.Domain.Models;

namespace SessionPerfTracker.Infrastructure.Targeting;

public sealed class ProcessTargetResolver : IProcessTargetResolver
{
    public Task<IReadOnlyList<TargetDescriptor>> ListRunningTargetsAsync(CancellationToken cancellationToken = default)
    {
        var targets = Process.GetProcesses()
            .Select(TryCreateTarget)
            .Where(target => target is not null)
            .Select(target => target!)
            .OrderByDescending(target => !string.IsNullOrWhiteSpace(target.WindowTitle))
            .ThenBy(target => target.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return Task.FromResult<IReadOnlyList<TargetDescriptor>>(targets);
    }

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
        using var process = Process.GetProcessById(processId);
        var processName = SafeGetProcessName(process);
        var executablePath = SafeGetExecutablePath(process);

        return Task.FromResult(new TargetDescriptor
        {
            Id = $"process_{processId}",
            Kind = TargetSelectionKind.Process,
            LifecycleMode = TargetLifecycleMode.AttachToRunning,
            DisplayName = $"{processName} ({processId})",
            ExecutablePath = executablePath,
            ProcessId = processId,
            IncludeChildProcesses = includeChildProcesses,
            ScopeMode = includeChildProcesses ? ProcessScopeMode.IncludeChildProcesses : ProcessScopeMode.RootOnly
        });
    }

    private static TargetDescriptor? TryCreateTarget(Process process)
    {
        using (process)
        {
            try
            {
                if (process.HasExited)
                {
                    return null;
                }

                _ = process.TotalProcessorTime;
                _ = process.WorkingSet64;
                var name = SafeGetProcessName(process);
                var windowTitle = string.IsNullOrWhiteSpace(process.MainWindowTitle) ? null : process.MainWindowTitle;
                var displayName = windowTitle is null ? $"{name} ({process.Id})" : $"{name} ({process.Id}) - {windowTitle}";
                return new TargetDescriptor
                {
                    Id = $"process_{process.Id}",
                    Kind = TargetSelectionKind.Process,
                    LifecycleMode = TargetLifecycleMode.AttachToRunning,
                    DisplayName = displayName,
                    ProcessId = process.Id,
                    WindowTitle = windowTitle,
                    IncludeChildProcesses = false,
                    ScopeMode = ProcessScopeMode.RootOnly
                };
            }
            catch (InvalidOperationException)
            {
                return null;
            }
            catch (System.ComponentModel.Win32Exception)
            {
                return null;
            }
            catch (UnauthorizedAccessException)
            {
                return null;
            }
        }
    }

    private static string SafeGetProcessName(Process process)
    {
        try
        {
            return string.IsNullOrWhiteSpace(process.ProcessName) ? $"Process {process.Id}" : process.ProcessName;
        }
        catch (InvalidOperationException)
        {
            return $"Process {process.Id}";
        }
    }

    private static string? SafeGetExecutablePath(Process process)
    {
        try
        {
            return process.MainModule?.FileName;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }
}
