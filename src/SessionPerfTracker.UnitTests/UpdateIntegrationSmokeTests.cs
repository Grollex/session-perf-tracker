using System.Text.Json;
using SessionPerfTracker.Domain.Models;
using SessionPerfTracker.Domain.Services;
using SessionPerfTracker.Infrastructure.Updates;

namespace SessionPerfTracker.UnitTests;

public sealed class UpdateIntegrationSmokeTests
{
    [Fact]
    public async Task Local_manifest_with_relative_package_checks_and_downloads_with_matching_hash()
    {
        var directory = CreateTemporaryDirectory();
        var installerPath = Path.Combine(directory, "SessionPerfTracker-99.0.0-win-x64-setup.exe");
        await File.WriteAllTextAsync(installerPath, "fake installer payload");
        var sha256 = await HttpUpdateServiceTests.ComputeSha256Async(installerPath);
        var manifestPath = await WriteManifestAsync(directory, new
        {
            version = "99.0.0",
            installerUrl = Path.GetFileName(installerPath),
            releaseNotes = "local smoke",
            sha256
        });
        var updateDirectory = Path.Combine(directory, "updates");
        var service = new HttpUpdateService();

        var result = await service.CheckAsync(manifestPath);

        Assert.True(result.IsUpdateAvailable);
        Assert.NotNull(result.Manifest);
        Assert.Equal(installerPath, result.Manifest!.InstallerUrl);
        Assert.Equal(sha256, result.Manifest.Sha256);

        var downloadedPath = await service.DownloadInstallerAsync(result.Manifest, updateDirectory);

        Assert.True(File.Exists(downloadedPath));
        Assert.Equal(sha256, await HttpUpdateServiceTests.ComputeSha256Async(downloadedPath));
    }

    [Fact]
    public async Task Local_manifest_package_download_rejects_hash_mismatch()
    {
        var directory = CreateTemporaryDirectory();
        var installerPath = Path.Combine(directory, "SessionPerfTracker-99.0.0-win-x64-setup.exe");
        await File.WriteAllTextAsync(installerPath, "fake installer payload");
        var manifestPath = await WriteManifestAsync(directory, new
        {
            version = "99.0.0",
            installerUrl = Path.GetFileName(installerPath),
            sha256 = new string('0', 64)
        });
        var service = new HttpUpdateService();
        var result = await service.CheckAsync(manifestPath);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.DownloadInstallerAsync(result.Manifest!, Path.Combine(directory, "updates")));

        Assert.Contains("SHA256", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Startup_update_policy_skips_configured_version()
    {
        var settings = new AppUpdateSettings
        {
            AutomaticallyCheckForUpdates = true,
            AutomaticallyInstallUpdatesOnStartup = true,
            ManifestUrl = "C:\\updates\\version.json",
            SkippedVersion = "v99.0.0"
        };
        var result = CreateUpdateResult("99.0.0");

        var decision = AppUpdatePolicy.DecideStartupUpdate(settings, result);

        Assert.Equal(StartupUpdateDecisionKind.Skipped, decision.Kind);
        Assert.Equal("99.0.0", decision.LatestVersion);
    }

    [Fact]
    public void Startup_update_policy_installs_non_skipped_available_version()
    {
        var settings = new AppUpdateSettings
        {
            AutomaticallyCheckForUpdates = true,
            AutomaticallyInstallUpdatesOnStartup = true,
            ManifestUrl = "C:\\updates\\version.json",
            SkippedVersion = "98.0.0"
        };
        var result = CreateUpdateResult("99.0.0");

        var decision = AppUpdatePolicy.DecideStartupUpdate(settings, result);

        Assert.Equal(StartupUpdateDecisionKind.Install, decision.Kind);
        Assert.Equal("99.0.0", decision.LatestVersion);
    }

    [Fact]
    public void Startup_update_policy_check_only_when_startup_install_is_disabled()
    {
        var settings = new AppUpdateSettings
        {
            AutomaticallyCheckForUpdates = true,
            AutomaticallyInstallUpdatesOnStartup = false,
            ManifestUrl = "C:\\updates\\version.json"
        };

        var decision = AppUpdatePolicy.DecideStartupUpdate(settings, CreateUpdateResult("99.0.0"));

        Assert.Equal(StartupUpdateDecisionKind.CheckOnly, decision.Kind);
    }

    private static UpdateCheckResult CreateUpdateResult(string version) => new()
    {
        CurrentVersion = "1.5.3",
        LatestVersion = version,
        IsUpdateAvailable = true,
        Manifest = new UpdateManifest
        {
            Version = version,
            InstallerUrl = "C:\\updates\\SessionPerfTracker-99.0.0-win-x64-setup.exe",
            Sha256 = new string('a', 64)
        },
        Status = "Update available."
    };

    private static string CreateTemporaryDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "spt-update-integration-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static async Task<string> WriteManifestAsync(string directory, object manifest)
    {
        var path = Path.Combine(directory, "version.json");
        var json = JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(path, json);
        return path;
    }
}
