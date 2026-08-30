using System.IO;

namespace SessionPerfTracker.App.Services;

public static class ExecutableIdentity
{
    public static string NormalizeExeNameForAssignment(string? exeName)
    {
        exeName = Path.GetFileName((exeName ?? string.Empty).Trim());
        if (string.IsNullOrWhiteSpace(exeName))
        {
            return string.Empty;
        }

        return exeName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? exeName.ToLowerInvariant()
            : $"{exeName}.exe".ToLowerInvariant();
    }

    public static bool IsUnavailablePath(string? path) =>
        string.IsNullOrWhiteSpace(path)
        || string.Equals(path, "Unavailable", StringComparison.OrdinalIgnoreCase)
        || string.Equals(path, "n/a", StringComparison.OrdinalIgnoreCase);
}
