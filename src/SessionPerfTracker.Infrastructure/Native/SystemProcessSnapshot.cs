using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SessionPerfTracker.Infrastructure.Native;

/// <summary>
/// Raw per-process counters taken from one NtQuerySystemInformation call.
/// No process handles are opened, so protected and elevated processes are included too.
/// </summary>
internal readonly record struct ProcessRawInfo(
    int ProcessId,
    int ParentProcessId,
    string Name,
    long CreateTimeTicks,
    long CpuTime100Ns,
    ulong PrivateWorkingSetBytes,
    ulong WorkingSetBytes,
    ulong PrivateBytes,
    ulong ReadTransferBytes,
    ulong WriteTransferBytes,
    int ThreadCount,
    int HandleCount,
    int SessionId)
{
    public DateTimeOffset? StartedAtUtc => CreateTimeTicks <= 0
        ? null
        : DateTimeOffset.FromFileTime(CreateTimeTicks).ToUniversalTime();

    public double PrivateWorkingSetMb => PrivateWorkingSetBytes / 1024d / 1024d;
}

internal sealed class SystemProcessSnapshot
{
    private Dictionary<int, List<int>>? _childrenByParent;

    public SystemProcessSnapshot(
        DateTimeOffset capturedAt,
        long timestamp,
        IReadOnlyDictionary<int, ProcessRawInfo> processes)
    {
        CapturedAt = capturedAt;
        Timestamp = timestamp;
        Processes = processes;
    }

    public DateTimeOffset CapturedAt { get; }

    /// <summary>Stopwatch timestamp used for precise rate calculations.</summary>
    public long Timestamp { get; }

    public IReadOnlyDictionary<int, ProcessRawInfo> Processes { get; }

    public bool TryGet(int processId, out ProcessRawInfo info) => Processes.TryGetValue(processId, out info);

    public IReadOnlyDictionary<int, List<int>> ChildrenByParent
    {
        get
        {
            if (_childrenByParent is not null)
            {
                return _childrenByParent;
            }

            var map = new Dictionary<int, List<int>>();
            foreach (var process in Processes.Values)
            {
                if (process.ProcessId == 0 || process.ParentProcessId == process.ProcessId)
                {
                    continue;
                }

                if (!map.TryGetValue(process.ParentProcessId, out var children))
                {
                    children = [];
                    map[process.ParentProcessId] = children;
                }

                children.Add(process.ProcessId);
            }

            _childrenByParent = map;
            return map;
        }
    }

    public static double ElapsedSeconds(SystemProcessSnapshot previous, SystemProcessSnapshot current) =>
        Math.Max(0.001, (current.Timestamp - previous.Timestamp) / (double)Stopwatch.Frequency);

    /// <summary>CPU usage normalized to all logical processors (0-100), like Task Manager.</summary>
    public static double CpuPercent(long previousCpu100Ns, long currentCpu100Ns, double elapsedSeconds)
    {
        if (elapsedSeconds <= 0 || currentCpu100Ns < previousCpu100Ns)
        {
            return 0;
        }

        var cpuSeconds = (currentCpu100Ns - previousCpu100Ns) / 10_000_000d;
        return Math.Clamp(cpuSeconds / elapsedSeconds / Environment.ProcessorCount * 100, 0, 100);
    }

    public static double MegabytesPerSecond(ulong previousBytes, ulong currentBytes, double elapsedSeconds)
    {
        if (elapsedSeconds <= 0 || currentBytes < previousBytes)
        {
            return 0;
        }

        return (currentBytes - previousBytes) / 1024d / 1024d / elapsedSeconds;
    }
}

internal sealed class SystemProcessSnapshotReader : IDisposable
{
    private const int SystemProcessInformationClass = 5;
    private const uint StatusInfoLengthMismatch = 0xC0000004;
    private const int EntryNextOffset = 0x00;
    private const int EntryThreadCount = 0x04;
    private const int EntryPrivateWorkingSet = 0x08;
    private const int EntryCreateTime = 0x20;
    private const int EntryUserTime = 0x28;
    private const int EntryKernelTime = 0x30;
    private const int EntryImageNameLength = 0x38;
    private const int EntryImageNameBuffer = 0x40;
    private const int EntryProcessId = 0x50;
    private const int EntryParentProcessId = 0x58;
    private const int EntryHandleCount = 0x60;
    private const int EntrySessionId = 0x64;
    private const int EntryWorkingSet = 0x90;
    private const int EntryPagefileUsage = 0xB8;
    private const int EntryReadTransfer = 0xE8;
    private const int EntryWriteTransfer = 0xF0;

    private readonly object _sync = new();
    private IntPtr _buffer;
    private int _bufferSize = 512 * 1024;
    private bool _disposed;

    public static SystemProcessSnapshotReader Shared { get; } = new();

    public static bool IsSupported => Environment.Is64BitProcess && OperatingSystem.IsWindows();

    public SystemProcessSnapshot Capture()
    {
        if (!IsSupported)
        {
            throw new PlatformNotSupportedException("System process snapshots require 64-bit Windows.");
        }

        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var processes = new Dictionary<int, ProcessRawInfo>(512);
            QueryIntoBuffer();
            var timestamp = Stopwatch.GetTimestamp();
            var capturedAt = DateTimeOffset.UtcNow;
            var offset = 0L;

            while (true)
            {
                var entry = _buffer + (nint)offset;
                var processId = (int)Marshal.ReadIntPtr(entry, EntryProcessId);
                var nameLengthBytes = (ushort)Marshal.ReadInt16(entry, EntryImageNameLength);
                var namePointer = Marshal.ReadIntPtr(entry, EntryImageNameBuffer);
                var name = processId switch
                {
                    0 => "Idle",
                    _ when namePointer == IntPtr.Zero || nameLengthBytes == 0 => processId == 4 ? "System" : $"Process {processId}",
                    _ => Marshal.PtrToStringUni(namePointer, nameLengthBytes / 2)
                };

                processes[processId] = new ProcessRawInfo(
                    processId,
                    (int)Marshal.ReadIntPtr(entry, EntryParentProcessId),
                    name,
                    Marshal.ReadInt64(entry, EntryCreateTime),
                    Marshal.ReadInt64(entry, EntryUserTime) + Marshal.ReadInt64(entry, EntryKernelTime),
                    (ulong)Math.Max(0, Marshal.ReadInt64(entry, EntryPrivateWorkingSet)),
                    (ulong)Marshal.ReadIntPtr(entry, EntryWorkingSet),
                    (ulong)Marshal.ReadIntPtr(entry, EntryPagefileUsage),
                    (ulong)Marshal.ReadInt64(entry, EntryReadTransfer),
                    (ulong)Marshal.ReadInt64(entry, EntryWriteTransfer),
                    Marshal.ReadInt32(entry, EntryThreadCount),
                    Marshal.ReadInt32(entry, EntryHandleCount),
                    Marshal.ReadInt32(entry, EntrySessionId));

                var next = (uint)Marshal.ReadInt32(entry, EntryNextOffset);
                if (next == 0)
                {
                    break;
                }

                offset += next;
                if (offset >= _bufferSize)
                {
                    break;
                }
            }

            return new SystemProcessSnapshot(capturedAt, timestamp, processes);
        }
    }

    private void QueryIntoBuffer()
    {
        for (var attempt = 0; attempt < 8; attempt++)
        {
            if (_buffer == IntPtr.Zero)
            {
                _buffer = Marshal.AllocHGlobal(_bufferSize);
            }

            var status = NativeMethods.NtQuerySystemInformation(
                SystemProcessInformationClass,
                _buffer,
                _bufferSize,
                out var returnLength);
            if (status == 0)
            {
                return;
            }

            if (status != StatusInfoLengthMismatch)
            {
                throw new InvalidOperationException($"NtQuerySystemInformation failed with status 0x{status:X8}.");
            }

            Marshal.FreeHGlobal(_buffer);
            _buffer = IntPtr.Zero;
            _bufferSize = Math.Max(_bufferSize * 2, returnLength + 64 * 1024);
        }

        throw new InvalidOperationException("NtQuerySystemInformation buffer kept growing; process list is unstable.");
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (_buffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(_buffer);
                _buffer = IntPtr.Zero;
            }
        }
    }
}
