using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using SessionPerfTracker.Domain.Abstractions;
using SessionPerfTracker.Infrastructure.Collectors;
using SessionPerfTracker.Infrastructure.Targeting;

namespace SessionPerfTracker.Infrastructure.GlobalWatch;

public sealed class LightweightGlobalProcessScanner : IGlobalProcessScanner
{
    private static readonly TimeSpan RecentProcessWindow = TimeSpan.FromSeconds(60);
    private static readonly Dictionary<string, string> SignerStatusByPath = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object SignerCacheSync = new();
    private readonly object _sync = new();
    private Dictionary<int, ProcessProbe> _previousByProcessId = [];
    private DateTimeOffset? _previousCapturedAt;

    public Task<GlobalProcessScan> ScanAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var current = CaptureProbe(cancellationToken);
        Dictionary<int, ProcessProbe> previousByProcessId;
        DateTimeOffset? previousCapturedAt;

        lock (_sync)
        {
            previousByProcessId = new Dictionary<int, ProcessProbe>(_previousByProcessId);
            previousCapturedAt = _previousCapturedAt;
            _previousByProcessId = current.ToDictionary(process => process.ProcessId);
            _previousCapturedAt = current.Count == 0 ? DateTimeOffset.UtcNow : current.Max(process => process.CapturedAt);
        }

        var snapshots = current
            .Select(process => BuildSnapshot(process, previousByProcessId.GetValueOrDefault(process.ProcessId)))
            .OrderByDescending(process => process.CpuPercent ?? 0)
            .ThenByDescending(process => process.MemoryMb ?? 0)
            .ToArray();

        var capturedAt = current.Count == 0 ? DateTimeOffset.UtcNow : current.Max(process => process.CapturedAt);
        return Task.FromResult(new GlobalProcessScan(
            capturedAt,
            previousCapturedAt is null ? null : capturedAt - previousCapturedAt.Value,
            snapshots));
    }

    private static IReadOnlyList<ProcessProbe> CaptureProbe(CancellationToken cancellationToken)
    {
        var capturedAt = DateTimeOffset.UtcNow;
        var probes = new List<ProcessProbe>();
        var parentByProcess = WindowsProcessTreeSnapshot.GetParentProcessMap();

        foreach (var process in Process.GetProcesses())
        {
            cancellationToken.ThrowIfCancellationRequested();

            using (process)
            {
                try
                {
                    if (process.HasExited)
                    {
                        continue;
                    }

                    var parentProcessId = parentByProcess.TryGetValue(process.Id, out var parentId)
                        ? parentId
                        : (int?)null;
                    probes.Add(new ProcessProbe(
                        process.Id,
                        parentProcessId,
                        SafeGetProcessName(process),
                        SafeGetWindowTitle(process),
                        SafeGetMainModulePath(process),
                        SafeGetStartTime(process),
                        capturedAt,
                        SafeGetTotalProcessorTime(process),
                        SafeGetMemoryMb(process),
                        WindowsProcessIoCounters.TryGet(process)));
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
            }
        }

        var nameByProcessId = probes.ToDictionary(process => process.ProcessId, process => process.Name);
        var childrenByParentId = probes
            .Where(process => process.ParentProcessId is not null)
            .GroupBy(process => process.ParentProcessId!.Value)
            .ToDictionary(group => group.Key, group => group.Select(process => process.ProcessId).ToArray());

        return probes
            .Select(process => process with
            {
                ParentProcessName = process.ParentProcessId is int parentId && nameByProcessId.TryGetValue(parentId, out var parentName)
                    ? parentName
                    : null,
                DescendantProcessCount = CountDescendants(process.ProcessId, childrenByParentId)
            })
            .ToArray();
    }

    private static GlobalProcessSnapshot BuildSnapshot(ProcessProbe current, ProcessProbe? previous)
    {
        var wallMs = previous is null
            ? 0
            : Math.Max(1, (current.CapturedAt - previous.CapturedAt).TotalMilliseconds);
        double? cpuPercent = null;
        double? memoryDeltaMb = null;
        double? diskReadMbPerSec = null;
        double? diskWriteMbPerSec = null;

        if (previous is not null)
        {
            if (current.TotalProcessorTime is not null && previous.TotalProcessorTime is not null)
            {
                var cpuDeltaMs = Math.Max(0, (current.TotalProcessorTime.Value - previous.TotalProcessorTime.Value).TotalMilliseconds);
                cpuPercent = Math.Clamp(cpuDeltaMs / wallMs / Environment.ProcessorCount * 100, 0, 100);
            }

            if (current.MemoryMb is not null && previous.MemoryMb is not null)
            {
                memoryDeltaMb = current.MemoryMb.Value - previous.MemoryMb.Value;
            }

            if (current.IoCounters is not null && previous.IoCounters is not null)
            {
                var seconds = wallMs / 1000d;
                diskReadMbPerSec = BytesToMegabytesPerSecond(
                    current.IoCounters.ReadTransferCount,
                    previous.IoCounters.ReadTransferCount,
                    seconds);
                diskWriteMbPerSec = BytesToMegabytesPerSecond(
                    current.IoCounters.WriteTransferCount,
                    previous.IoCounters.WriteTransferCount,
                    seconds);
            }
        }

        return new GlobalProcessSnapshot
        {
            ProcessId = current.ProcessId,
            ParentProcessId = current.ParentProcessId,
            ParentProcessName = current.ParentProcessName,
            DescendantProcessCount = current.DescendantProcessCount,
            Name = current.Name,
            WindowTitle = current.WindowTitle,
            FullPath = current.FullPath,
            FileDescription = current.FileDescription,
            ProductName = current.ProductName,
            CompanyName = current.CompanyName,
            SignerStatus = current.SignerStatus,
            Version = current.Version,
            OriginalFileName = current.OriginalFileName,
            StartedAt = current.StartedAt,
            IsNewSincePreviousScan = previous is null,
            StartedRecently = current.StartedAt is not null
                && current.CapturedAt - current.StartedAt.Value.ToUniversalTime() <= RecentProcessWindow,
            CpuPercent = cpuPercent,
            MemoryMb = current.MemoryMb,
            MemoryDeltaMb = memoryDeltaMb,
            DiskReadMbPerSec = diskReadMbPerSec,
            DiskWriteMbPerSec = diskWriteMbPerSec
        };
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

    private static string? SafeGetWindowTitle(Process process)
    {
        try
        {
            return string.IsNullOrWhiteSpace(process.MainWindowTitle) ? null : process.MainWindowTitle;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static string? SafeGetMainModulePath(Process process)
    {
        try
        {
            var path = process.MainModule?.FileName;
            return string.IsNullOrWhiteSpace(path) ? null : path;
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

    private static FileIdentityMetadata GetFileIdentity(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return new FileIdentityMetadata(null, null, null, null, null, null);
        }

        try
        {
            var version = FileVersionInfo.GetVersionInfo(path);
            return new FileIdentityMetadata(
                NormalizeBlank(version.FileDescription),
                NormalizeBlank(version.ProductName),
                NormalizeBlank(version.CompanyName),
                GetSignerStatus(path),
                NormalizeBlank(version.FileVersion),
                NormalizeBlank(version.OriginalFilename));
        }
        catch
        {
            return new FileIdentityMetadata(null, null, null, GetSignerStatus(path), null, null);
        }
    }

    private static string GetSignerStatus(string path)
    {
        lock (SignerCacheSync)
        {
            if (SignerStatusByPath.TryGetValue(path, out var cached))
            {
                return cached;
            }
        }

        var status = "Unknown";
        try
        {
            using var certificate = X509CertificateLoader.LoadCertificateFromFile(path);
            status = certificate is null ? "Unsigned" : "Signed";
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            status = "Unsigned";
        }
        catch
        {
            status = "Unknown";
        }

        lock (SignerCacheSync)
        {
            SignerStatusByPath[path] = status;
        }

        return status;
    }

    private static string? NormalizeBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static DateTimeOffset? SafeGetStartTime(Process process)
    {
        try
        {
            return new DateTimeOffset(process.StartTime).ToUniversalTime();
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

    private static TimeSpan? SafeGetTotalProcessorTime(Process process)
    {
        try
        {
            return process.TotalProcessorTime;
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

    private static double? SafeGetMemoryMb(Process process)
    {
        try
        {
            return WindowsProcessMemoryCounters.GetRamForAggregation(process).Megabytes;
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

    private static double BytesToMegabytesPerSecond(ulong current, ulong previous, double seconds)
    {
        if (current < previous || seconds <= 0)
        {
            return 0;
        }

        return (current - previous) / 1024d / 1024d / seconds;
    }

    private static int CountDescendants(int processId, IReadOnlyDictionary<int, int[]> childrenByParentId)
    {
        if (!childrenByParentId.TryGetValue(processId, out var children) || children.Length == 0)
        {
            return 0;
        }

        var count = 0;
        var stack = new Stack<int>(children);
        while (stack.Count > 0)
        {
            var childId = stack.Pop();
            count++;
            if (!childrenByParentId.TryGetValue(childId, out var grandchildren))
            {
                continue;
            }

            foreach (var grandchildId in grandchildren)
            {
                stack.Push(grandchildId);
            }
        }

        return count;
    }

    private sealed record ProcessProbe(
        int ProcessId,
        int? ParentProcessId,
        string Name,
        string? WindowTitle,
        string? FullPath,
        DateTimeOffset? StartedAt,
        DateTimeOffset CapturedAt,
        TimeSpan? TotalProcessorTime,
        double? MemoryMb,
        IoCounterSnapshot? IoCounters)
    {
        private readonly FileIdentityMetadata _fileIdentity = GetFileIdentity(FullPath);

        public string? FileDescription => _fileIdentity.FileDescription;
        public string? ProductName => _fileIdentity.ProductName;
        public string? CompanyName => _fileIdentity.CompanyName;
        public string? SignerStatus => _fileIdentity.SignerStatus;
        public string? Version => _fileIdentity.Version;
        public string? OriginalFileName => _fileIdentity.OriginalFileName;
        public string? ParentProcessName { get; init; }
        public int DescendantProcessCount { get; init; }
    }

    private sealed record IoCounterSnapshot(ulong ReadTransferCount, ulong WriteTransferCount);
    private sealed record FileIdentityMetadata(
        string? FileDescription,
        string? ProductName,
        string? CompanyName,
        string? SignerStatus,
        string? Version,
        string? OriginalFileName);

    private static class WindowsProcessIoCounters
    {
        public static IoCounterSnapshot? TryGet(Process process)
        {
            try
            {
                return GetProcessIoCounters(process.Handle, out var counters)
                    ? new IoCounterSnapshot(counters.ReadTransferCount, counters.WriteTransferCount)
                    : null;
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

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetProcessIoCounters(IntPtr processHandle, out IoCounters ioCounters);

        [StructLayout(LayoutKind.Sequential)]
        private struct IoCounters
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }
    }
}
