using System.Diagnostics;
using System.IO;

namespace SessionPerfTracker.App.Services;

public sealed class SelfUninstallService
{
    private const string PurgeUserDataArgument = "/PURGEUSERDATA=1";

    public FileInfo? FindUninstaller()
    {
        var directories = new[]
            {
                AppContext.BaseDirectory,
                Path.GetDirectoryName(Environment.ProcessPath)
            }
            .Where(directory => !string.IsNullOrWhiteSpace(directory))
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (var directory in directories)
        {
            if (!Directory.Exists(directory))
            {
                continue;
            }

            var uninstaller = Directory
                .EnumerateFiles(directory, "unins*.exe", SearchOption.TopDirectoryOnly)
                .Order(StringComparer.OrdinalIgnoreCase)
                .Select(path => new FileInfo(path))
                .FirstOrDefault(file => file.Exists);

            if (uninstaller is not null)
            {
                return uninstaller;
            }
        }

        return null;
    }

    public void LaunchUninstaller(FileInfo uninstaller)
    {
        if (!uninstaller.Exists)
        {
            throw new FileNotFoundException("The application uninstaller was not found.", uninstaller.FullName);
        }

        var started = Process.Start(new ProcessStartInfo
        {
            FileName = uninstaller.FullName,
            Arguments = PurgeUserDataArgument,
            WorkingDirectory = uninstaller.DirectoryName ?? AppContext.BaseDirectory,
            UseShellExecute = true,
            Verb = "runas"
        });

        if (started is null)
        {
            throw new InvalidOperationException("Windows did not start the application uninstaller.");
        }
    }
}
