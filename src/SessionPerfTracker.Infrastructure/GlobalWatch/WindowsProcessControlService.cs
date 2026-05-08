using System.Diagnostics;
using SessionPerfTracker.Domain.Abstractions;

namespace SessionPerfTracker.Infrastructure.GlobalWatch;

public sealed class WindowsProcessControlService : IProcessControlService
{
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

    private static Task<ProcessControlResult> KillCoreAsync(
        IReadOnlyList<int> processIds,
        bool entireTree,
        string reason,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var currentProcessId = Environment.ProcessId;
        var messages = new List<string>();
        var terminated = 0;

        foreach (var processId in processIds.Distinct())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (processId <= 0)
            {
                continue;
            }

            if (processId == currentProcessId)
            {
                messages.Add($"Skipped self PID {processId}.");
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
}
