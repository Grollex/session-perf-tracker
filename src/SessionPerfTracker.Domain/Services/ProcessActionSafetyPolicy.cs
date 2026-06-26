namespace SessionPerfTracker.Domain.Services;

public sealed record ProcessActionSafetyAssessment(bool IsAllowed, string Reason)
{
    public static ProcessActionSafetyAssessment Allow() => new(true, "Allowed by process action safety policy.");

    public static ProcessActionSafetyAssessment Block(string reason) => new(false, reason);
}

public sealed class ProcessActionSafetyPolicy
{
    private static readonly HashSet<string> CriticalProcessNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "csrss",
        "csrss.exe",
        "idle",
        "lsass",
        "lsass.exe",
        "memory compression",
        "registry",
        "services",
        "services.exe",
        "smss",
        "smss.exe",
        "system",
        "system.exe",
        "wininit",
        "wininit.exe",
        "winlogon",
        "winlogon.exe"
    };

    private readonly string _windowsDirectory;
    private readonly string _applicationPath;
    private readonly int _currentProcessId;

    public ProcessActionSafetyPolicy(string? windowsDirectory, string? applicationPath, int currentProcessId)
    {
        _windowsDirectory = NormalizePath(windowsDirectory);
        _applicationPath = NormalizePath(applicationPath);
        _currentProcessId = currentProcessId;
    }

    public static ProcessActionSafetyPolicy Default { get; } = new(
        Environment.GetFolderPath(Environment.SpecialFolder.Windows),
        Environment.ProcessPath,
        Environment.ProcessId);

    public ProcessActionSafetyAssessment Assess(int? processId, string? executableName, string? fullPath)
    {
        if (processId == _currentProcessId)
        {
            return ProcessActionSafetyAssessment.Block("Session Perf Tracker cannot terminate or ban itself.");
        }

        var normalizedName = NormalizeExecutableName(executableName);
        if (!string.IsNullOrWhiteSpace(normalizedName) && CriticalProcessNames.Contains(normalizedName))
        {
            return ProcessActionSafetyAssessment.Block($"Windows critical process '{normalizedName}' is protected.");
        }

        var normalizedPath = NormalizePath(fullPath);
        if (!string.IsNullOrWhiteSpace(normalizedPath))
        {
            if (!string.IsNullOrWhiteSpace(_applicationPath)
                && string.Equals(normalizedPath, _applicationPath, StringComparison.OrdinalIgnoreCase))
            {
                return ProcessActionSafetyAssessment.Block("Session Perf Tracker cannot terminate or ban its own executable.");
            }

            if (!string.IsNullOrWhiteSpace(_windowsDirectory)
                && IsPathInsideDirectory(normalizedPath, _windowsDirectory))
            {
                return ProcessActionSafetyAssessment.Block("Executables inside the Windows directory are protected.");
            }
        }

        return ProcessActionSafetyAssessment.Allow();
    }

    private static bool IsPathInsideDirectory(string path, string directory)
    {
        if (string.Equals(path, directory, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var prefix = directory.EndsWith(Path.DirectorySeparatorChar)
            ? directory
            : directory + Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeExecutableName(string? executableName)
    {
        if (string.IsNullOrWhiteSpace(executableName))
        {
            return string.Empty;
        }

        var name = Path.GetFileName(executableName.Trim());
        return string.IsNullOrWhiteSpace(name)
            ? executableName.Trim()
            : name;
    }

    private static string NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        try
        {
            return Path.GetFullPath(path.Trim())
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch
        {
            return path.Trim()
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
    }
}
