using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using SessionPerfTracker.Domain.Abstractions;
using SessionPerfTracker.Domain.Models;

namespace SessionPerfTracker.Infrastructure.Targeting;

public sealed class ProcessTargetResolver : IProcessTargetResolver
{
    private const int ProcessQueryLimitedInformation = 0x1000;

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
            var path = process.MainModule?.FileName;
            if (!string.IsNullOrWhiteSpace(path))
            {
                return path;
            }
        }
        catch (InvalidOperationException)
        {
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        return TryQueryFullProcessImageName(process.Id);
    }

    private static string? TryQueryFullProcessImageName(int processId)
    {
        try
        {
            var handle = OpenProcess(ProcessQueryLimitedInformation, false, processId);
            if (handle == IntPtr.Zero)
            {
                return null;
            }

            try
            {
                var builder = new StringBuilder(1024);
                var size = builder.Capacity;
                if (!QueryFullProcessImageName(handle, 0, builder, ref size))
                {
                    return null;
                }

                var path = builder.ToString();
                return string.IsNullOrWhiteSpace(path) ? null : path;
            }
            finally
            {
                CloseHandle(handle);
            }
        }
        catch
        {
            return null;
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(int desiredAccess, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageName(
        IntPtr processHandle,
        int flags,
        StringBuilder exeName,
        ref int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
