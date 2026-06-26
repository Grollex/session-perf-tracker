using System.Diagnostics;
using SessionPerfTracker.Domain.Abstractions;
using SessionPerfTracker.Domain.Services;

namespace SessionPerfTracker.Infrastructure.GlobalWatch;

public sealed class WindowsProcessControlService : IProcessControlService
{
    private readonly ProcessActionSafetyPolicy _safetyPolicy;

    public WindowsProcessControlService(ProcessActionSafetyPolicy? safetyPolicy = null)
    {
        _safetyPolicy = safetyPolicy ?? ProcessActionSafetyPolicy.Default;
    }

    public Task<ProcessControlResult> KillProcessAsync(
        int processId,
        string reason,
        CancellationToken cancellationToken = default) =>
        KillCoreAsync([processId], entireTree: false, reason, cancellationToken);

    public Task<ProcessControlResult> KillProcessTreeAsync(
        int processId,
        string reason,
        CancellationToken cancellationToken = default) =>
        KillCoreAsync([processId], entireTree: true, reason, cancellationToken);

    public Task<ProcessControlResult> KillProcessesAsync(
        IReadOnlyList<int> processIds,
        string reason,
        CancellationToken cancellationToken = default) =>
        KillCoreAsync(processIds, entireTree: false, reason, cancellationToken);

    private Task<ProcessControlResult> KillCoreAsync(
        IReadOnlyList<int> processIds,
        bool entireTree,
        string reason,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var messages = new List<string>();
        var terminated = 0;

        foreach (var processId in processIds.Distinct())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (processId <= 0)
            {
                continue;
            }

            try
            {
                using var process = Process.GetProcessById(processId);
                if (process.HasExited)
                {
                    messages.Add($"PID {processId} already exited.");
                    continue;
                }

                var name = SafeGetProcessName(process);
                var fullPath = SafeGetProcessPath(process);
                var safety = _safetyPolicy.Assess(processId, name, fullPath);
                if (!safety.IsAllowed)
                {
                    messages.Add($"Skipped {name} ({processId}): {safety.Reason}");
                    continue;
                }

                process.Kill(entireProcessTree: entireTree);
                terminated++;
                messages.Add(entireTree
                    ? $"Hard-killed tree for {name} ({processId}) because {reason}."
                    : $"Hard-killed {name} ({processId}) because {reason}.");
            }
            catch (ArgumentException)
            {
                messages.Add($"PID {processId} not found.");
            }
            catch (InvalidOperationException error)
            {
                messages.Add($"PID {processId} could not be killed: {error.Message}");
            }
            catch (System.ComponentModel.Win32Exception error)
            {
                messages.Add($"PID {processId} denied by Windows: {error.Message}");
            }
            catch (UnauthorizedAccessException error)
            {
                messages.Add($"PID {processId} denied by Windows: {error.Message}");
            }
        }

        return Task.FromResult(new ProcessControlResult(
            processIds.Distinct().Count(),
            terminated,
            messages));
    }

    private static string SafeGetProcessName(Process process)
    {
        try
        {
            return string.IsNullOrWhiteSpace(process.ProcessName)
                ? "process"
                : process.ProcessName;
        }
        catch
        {
            return "process";
        }
    }

    private static string? SafeGetProcessPath(Process process)
    {
        try
        {
            return process.MainModule?.FileName;
        }
        catch
        {
            return null;
        }
    }
}
