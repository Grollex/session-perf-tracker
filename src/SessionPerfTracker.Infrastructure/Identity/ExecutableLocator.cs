using Microsoft.Win32;
using SessionPerfTracker.Domain.Abstractions;

namespace SessionPerfTracker.Infrastructure.Identity;

/// <summary>
/// Bounded search for an executable that disappeared from its saved path. It looks where programs
/// usually move to (sibling version folders, App Paths/Uninstall registry entries, standard install
/// roots) instead of crawling whole drives, and every search is capped in directories visited.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class ExecutableLocator : IExecutableLocator
{
    private const int MaxDirectoriesPerSearch = 6000;
    private static readonly EnumerationOptions DirectoryOptions = new()
    {
        IgnoreInaccessible = true,
        RecurseSubdirectories = false,
        AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.Offline
    };

    public Task<IReadOnlyList<string>> FindCandidatePathsAsync(
        string missingPath,
        string exeName,
        string? productName,
        int maxResults,
        CancellationToken cancellationToken = default) =>
        Task.Run(() => FindCandidatePaths(missingPath, exeName, productName, maxResults, cancellationToken), cancellationToken);

    internal IReadOnlyList<string> FindCandidatePaths(
        string missingPath,
        string exeName,
        string? productName,
        int maxResults,
        CancellationToken cancellationToken)
    {
        var fileName = NormalizeFileName(exeName, missingPath);
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return [];
        }

        var results = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var budget = new SearchBudget(MaxDirectoriesPerSearch);
        var windowsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

        void AddIfMatch(string? candidate)
        {
            if (string.IsNullOrWhiteSpace(candidate) || results.Count >= maxResults)
            {
                return;
            }

            try
            {
                var full = Path.GetFullPath(candidate.Trim().Trim('"'));
                if (!string.Equals(Path.GetFileName(full), fileName, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(full, missingPath, StringComparison.OrdinalIgnoreCase)
                    || full.StartsWith(windowsDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                    || !File.Exists(full)
                    || !seen.Add(full))
                {
                    return;
                }

                results.Add(full);
            }
            catch
            {
            }
        }

        // 1. Same tree: updates usually land in a sibling folder (app-1.2.3 -> app-1.2.4).
        var directory = SafeDirectoryName(missingPath);
        var depthByLevel = new[] { 2, 3, 2 };
        for (var level = 0; level < depthByLevel.Length && directory is not null; level++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Directory.Exists(directory) && !IsDriveRoot(directory))
            {
                SearchTree(directory, fileName, depthByLevel[level], budget, AddIfMatch, cancellationToken);
                if (results.Count > 0)
                {
                    break;
                }
            }

            directory = SafeDirectoryName(directory);
        }

        // 2. Registry hints from installers.
        foreach (var hint in ReadRegistryHints(fileName, productName))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(hint))
            {
                AddIfMatch(hint);
            }
            else if (Directory.Exists(hint))
            {
                SearchTree(hint, fileName, 2, budget, AddIfMatch, cancellationToken);
            }
        }

        // 3. Standard install roots, shallow.
        if (results.Count == 0)
        {
            foreach (var root in GetStandardRoots())
            {
                cancellationToken.ThrowIfCancellationRequested();
                SearchTree(root, fileName, 3, budget, AddIfMatch, cancellationToken);
                if (results.Count >= maxResults || budget.Exhausted)
                {
                    break;
                }
            }
        }

        return results;
    }

    private static void SearchTree(
        string root,
        string fileName,
        int maxDepth,
        SearchBudget budget,
        Action<string?> onCandidate,
        CancellationToken cancellationToken)
    {
        var stack = new Stack<(string Path, int Depth)>();
        stack.Push((root, 0));
        while (stack.Count > 0 && !budget.Exhausted)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (path, depth) = stack.Pop();
            if (!budget.TryVisit(path))
            {
                continue;
            }

            try
            {
                var candidate = Path.Combine(path, fileName);
                if (File.Exists(candidate))
                {
                    onCandidate(candidate);
                }

                if (depth >= maxDepth)
                {
                    continue;
                }

                foreach (var child in Directory.EnumerateDirectories(path, "*", DirectoryOptions))
                {
                    stack.Push((child, depth + 1));
                }
            }
            catch
            {
            }
        }
    }

    private static IEnumerable<string> ReadRegistryHints(string fileName, string? productName)
    {
        var hints = new List<string>();
        foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            try
            {
                using var appPath = hive.OpenSubKey($@"Software\Microsoft\Windows\CurrentVersion\App Paths\{fileName}");
                if (appPath?.GetValue(null) is string registered)
                {
                    hints.Add(Environment.ExpandEnvironmentVariables(registered.Trim('"')));
                }
            }
            catch
            {
            }

            foreach (var uninstallRoot in new[]
                     {
                         @"Software\Microsoft\Windows\CurrentVersion\Uninstall",
                         @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"
                     })
            {
                try
                {
                    using var uninstall = hive.OpenSubKey(uninstallRoot);
                    if (uninstall is null)
                    {
                        continue;
                    }

                    foreach (var name in uninstall.GetSubKeyNames())
                    {
                        using var entry = uninstall.OpenSubKey(name);
                        if (entry is null)
                        {
                            continue;
                        }

                        var displayIcon = CleanRegistryPath(entry.GetValue("DisplayIcon") as string);
                        var installLocation = CleanRegistryPath(entry.GetValue("InstallLocation") as string);
                        var displayName = entry.GetValue("DisplayName") as string;
                        var iconMatches = displayIcon is not null
                            && string.Equals(Path.GetFileName(displayIcon), fileName, StringComparison.OrdinalIgnoreCase);
                        var productMatches = !string.IsNullOrWhiteSpace(productName)
                            && !string.IsNullOrWhiteSpace(displayName)
                            && displayName.Contains(productName, StringComparison.OrdinalIgnoreCase);

                        if (iconMatches)
                        {
                            hints.Add(displayIcon!);
                        }

                        if ((iconMatches || productMatches) && installLocation is not null)
                        {
                            hints.Add(installLocation);
                        }
                    }
                }
                catch
                {
                }
            }
        }

        return hints.Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static string? CleanRegistryPath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var text = Environment.ExpandEnvironmentVariables(value.Trim());
        if (text.StartsWith('"'))
        {
            var closing = text.IndexOf('"', 1);
            text = closing > 0 ? text[1..closing] : text.Trim('"');
        }
        else
        {
            var comma = text.LastIndexOf(',');
            if (comma > 2)
            {
                text = text[..comma];
            }
        }

        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }

    private static IEnumerable<string> GetStandardRoots()
    {
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs"),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)
        };

        return roots
            .Where(root => !string.IsNullOrWhiteSpace(root) && Directory.Exists(root))
            .Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static string NormalizeFileName(string exeName, string missingPath)
    {
        var name = Path.GetFileName(string.IsNullOrWhiteSpace(missingPath) ? exeName : missingPath);
        if (string.IsNullOrWhiteSpace(name))
        {
            name = Path.GetFileName(exeName);
        }

        return string.IsNullOrWhiteSpace(name) || name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? name ?? string.Empty
            : $"{name}.exe";
    }

    private static string? SafeDirectoryName(string? path)
    {
        try
        {
            return string.IsNullOrWhiteSpace(path) ? null : Path.GetDirectoryName(path);
        }
        catch
        {
            return null;
        }
    }

    private static bool IsDriveRoot(string directory) =>
        string.Equals(Path.GetPathRoot(directory)?.TrimEnd('\\'), directory.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    private sealed class SearchBudget(int maxDirectories)
    {
        private readonly HashSet<string> _visited = new(StringComparer.OrdinalIgnoreCase);

        public bool Exhausted => _visited.Count >= maxDirectories;

        public bool TryVisit(string path) => !Exhausted && _visited.Add(path);
    }
}
