using System.Text;

namespace SessionPerfTracker.Infrastructure.Native;

/// <summary>
/// One EnumWindows pass that maps process ids to their visible top-level windows.
/// Replaces per-process MainWindowTitle/Responding calls, which enumerate every window
/// for each process and can block on hung windows.
/// </summary>
internal sealed class WindowSnapshot
{
    private const uint GwOwner = 4;
    private readonly Dictionary<int, List<IntPtr>> _windowsByProcess;
    private readonly Dictionary<int, string> _titleByProcess;

    private WindowSnapshot(Dictionary<int, List<IntPtr>> windowsByProcess, Dictionary<int, string> titleByProcess)
    {
        _windowsByProcess = windowsByProcess;
        _titleByProcess = titleByProcess;
    }

    public static WindowSnapshot Empty { get; } = new([], []);

    public static WindowSnapshot Capture()
    {
        var windowsByProcess = new Dictionary<int, List<IntPtr>>();
        var titleByProcess = new Dictionary<int, string>();
        var builder = new StringBuilder(256);

        try
        {
            NativeMethods.EnumWindows((hwnd, _) =>
            {
                if (!NativeMethods.IsWindowVisible(hwnd) || NativeMethods.GetWindow(hwnd, GwOwner) != IntPtr.Zero)
                {
                    return true;
                }

                var length = NativeMethods.GetWindowTextLength(hwnd);
                if (length <= 0)
                {
                    return true;
                }

                NativeMethods.GetWindowThreadProcessId(hwnd, out var processId);
                if (processId <= 0)
                {
                    return true;
                }

                if (!windowsByProcess.TryGetValue(processId, out var windows))
                {
                    windows = [];
                    windowsByProcess[processId] = windows;
                }

                windows.Add(hwnd);
                if (!titleByProcess.ContainsKey(processId))
                {
                    builder.Clear();
                    builder.EnsureCapacity(Math.Min(length + 1, 512));
                    if (NativeMethods.GetWindowText(hwnd, builder, builder.Capacity) > 0)
                    {
                        titleByProcess[processId] = builder.ToString();
                    }
                }

                return true;
            }, IntPtr.Zero);
        }
        catch
        {
            return Empty;
        }

        return new WindowSnapshot(windowsByProcess, titleByProcess);
    }

    public string? GetTitle(int processId) =>
        _titleByProcess.TryGetValue(processId, out var title) ? title : null;

    public bool HasWindow(int processId) => _windowsByProcess.ContainsKey(processId);

    /// <summary>
    /// Non-blocking hang check (IsHungAppWindow). Returns null when the process has no visible window.
    /// </summary>
    public bool? IsResponding(int processId)
    {
        if (!_windowsByProcess.TryGetValue(processId, out var windows) || windows.Count == 0)
        {
            return null;
        }

        foreach (var hwnd in windows)
        {
            try
            {
                if (!NativeMethods.IsHungAppWindow(hwnd))
                {
                    return true;
                }
            }
            catch
            {
                return true;
            }
        }

        return false;
    }
}
