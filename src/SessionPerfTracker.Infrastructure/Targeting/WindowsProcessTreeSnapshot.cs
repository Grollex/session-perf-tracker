using System.Runtime.InteropServices;

namespace SessionPerfTracker.Infrastructure.Targeting;

internal static class WindowsProcessTreeSnapshot
{
    private const uint Th32csSnapprocess = 0x00000002;
    private static readonly IntPtr InvalidHandleValue = new(-1);

    public static IReadOnlyList<int> GetDescendantProcessIds(int rootProcessId)
    {
        var parentByProcess = GetParentProcessMap();
        var childrenByParent = parentByProcess
            .GroupBy(pair => pair.Value)
            .ToDictionary(group => group.Key, group => group.Select(pair => pair.Key).ToArray());

        var descendants = new List<int>();
        var visited = new HashSet<int> { rootProcessId };
        var queue = new Queue<int>();
        queue.Enqueue(rootProcessId);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (!childrenByParent.TryGetValue(current, out var children))
            {
                continue;
            }

            foreach (var child in children)
            {
                if (!visited.Add(child))
                {
                    continue;
                }

                descendants.Add(child);
                queue.Enqueue(child);
            }
        }

        return descendants;
    }

    public static IReadOnlyList<int> GetIncludedProcessIds(int rootProcessId, bool includeChildProcesses)
    {
        if (!includeChildProcesses)
        {
            return [rootProcessId];
        }

        return [rootProcessId, .. GetDescendantProcessIds(rootProcessId)];
    }

    public static int? GetParentProcessId(int processId)
    {
        var parentByProcess = GetParentProcessMap();
        return parentByProcess.TryGetValue(processId, out var parentProcessId) ? parentProcessId : null;
    }

    public static Dictionary<int, int> GetParentProcessMap()
    {
        var snapshot = CreateToolhelp32Snapshot(Th32csSnapprocess, 0);
        if (snapshot == InvalidHandleValue)
        {
            return [];
        }

        try
        {
            var entry = new ProcessEntry32
            {
                Size = (uint)Marshal.SizeOf<ProcessEntry32>()
            };
            var result = new Dictionary<int, int>();

            if (!Process32First(snapshot, ref entry))
            {
                return result;
            }

            do
            {
                result[(int)entry.ProcessId] = (int)entry.ParentProcessId;
            }
            while (Process32Next(snapshot, ref entry));

            return result;
        }
        finally
        {
            CloseHandle(snapshot);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool Process32First(IntPtr snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool Process32Next(IntPtr snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct ProcessEntry32
    {
        public uint Size;
        public uint Usage;
        public uint ProcessId;
        public IntPtr DefaultHeapId;
        public uint ModuleId;
        public uint Threads;
        public uint ParentProcessId;
        public int PriorityClassBase;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string ExeFile;
    }
}
